# Load harness: measure the server under many agents and a large monorepo

Status: done. Part 1 of 3 of the first scalability step; it measures what `bassia-c7ba795f` (workspace registry)
and `bassia-ff9140e9` (mainline index) improve.

## Outcome

A developer runs one command and gets numbers for how `bassia-server` behaves at a chosen scale: a synthetic monorepo
of a given size, a given number of simulated agents that create workspaces, snapshot and land at once, and a given
length of history. The report gives throughput and latency per operation and what the server asked of PostgreSQL and
blob storage for it, and shows how each grows from one scale to the next - so a scalability change is ranked and
accepted by measurement instead of by reading the code.

## Context

- The repository has no benchmark or load test. `SyncEfficiencyTests` counts the requests of one client's checkout and
  snapshot; `ServerTests` and `AzuriteIntegrationTests` run swarms of 8 agents for correctness, not for cost.
- A code review of scalability (November 2026) found paths whose cost grows with the number of workspaces or with the
  length of history rather than with the work done: `Reconciler.AffectedAsync` and `Repository.ListWorkspacesAsync`
  after every landing, `Repository.MainAtSeqAsync` and `LogAsync`, `GarbageCollector.CollectAsync`, lease grants, and
  wide directories in `PostgresTreeStore.PutManyAsync`. None of it was measured.
- The deployment in `deploy/azure` runs 1-3 server replicas; the working target is one organization of ~1,000 users and
  ~500 concurrent agents.
- Tests need a local PostgreSQL (`BASSIA_TEST_POSTGRES`) and run without a network; the harness has the same needs
  and must not make `dotnet test` slower.
- `CountingObjectStore` (a decorator in `Bassia.Core/Storage`) already counts object-store calls; nothing counts
  PostgreSQL statements yet.

## Acceptance criteria

- [x] One command runs a scenario against a `bassia-server` it starts itself (local-disk blobs and the PostgreSQL in
      `BASSIA_TEST_POSTGRES`, a schema of its own, dropped afterwards) and, with `-server <url> -token <t>`, against a
      server that is already running.
- [x] The scenario is set by parameters with documented defaults: files and directory fan-out of the seeded monorepo,
      landings of history seeded before measuring, closed workspaces left behind by that history, concurrent agents,
      operations per agent, files changed per snapshot, and the share of agents that touch the same paths (so some
      landings need a replay).
- [x] A run is reproducible: the same parameters and seed produce the same tree, the same changes and the same order of
      overlapping changes.
- [x] Seeding a monorepo of 1,000,000 files and 10,000 landings finishes in minutes on a developer machine (it writes
      objects through the stores, not through working folders), and a seeded state can be kept and reused by a later run.
- [x] The report gives, per operation (workspace create, snapshot, sync, land by outcome, reconcile after a landing,
      conflict overview, workspace list, mainline at a past seq, mainline log page, lease acquire, garbage collection):
      count, throughput, and latency at p50, p95 and p99 - and per operation the number of PostgreSQL statements and of
      blob-store calls the server made for it.
- [x] A sweep runs a scenario at several values of one parameter (for example 100, 1,000 and 10,000 open plus closed
      workspaces) and reports each operation's growth between them, marking those that grow with the parameter although
      the work done did not.
- [x] The report is written as TOML (like `bassia` results) and as a Markdown summary; two reports can be compared,
      listing operations that got slower or cheaper by more than a threshold.
- [x] A small smoke scenario (seconds) runs in CI on every change and fails only when the harness itself breaks - not
      on timing; the full scenarios are not part of `dotnet test` or of the default CI run.
- [x] A baseline report of `main` as it is before `bassia-c7ba795f` and `bassia-ff9140e9` is recorded in this feature
      record, with the parameters used.
- [x] `docs/reference.md` documents the command, the parameters and how to read the report.

## Approach

A new project, `Bassia.LoadTest` (`bassia-loadtest`), a console program beside `Bassia.Tests`. It references `Bassia`
and `Bassia.Server` and drives the server the way clients do - `ServerConnection` and the remote stores over HTTP -
with agents as tasks in its own process, not as `bassia` processes or working folders: the subject is the server, and
the client's scan and checkout are already covered by `SyncEfficiencyTests`. Seeding writes trees, commits and
operations through the version core (straight to the stores of a hosted server, through the API for a remote one) and
lands them, so history and closed workspaces exist as they would after real use.

Cost per operation is counted inside the hosted server, per request, and returned to the client: `ServerOptions` has
two hooks that are not command-line switches - `DecorateBlobs` (the harness wraps the blob store and counts its calls)
and `RequestProbe` (a middleware around each request). The probe charges every Npgsql command (an `ActivityListener`
on Npgsql's activity source) and every blob call made in the request's execution context to that request and sets two
response headers; the client's HTTP handler adds them to the operation in flight. Work outside a request (the
reconcile queue) is charged to a background total. Against a remote server only latency and requests are reported.

Latency is measured at the client, from request to reply. The server's work after a landing is measured twice: the
scan for stale workspaces directly in the hosted server (time and counts), and the time from a snapshot to its
`reconciled` event on the watch stream.

## Decisions

- **In-process agents over HTTP, not `bassia` processes.** Hundreds of processes and working folders measure the
  machine, not the server.
- **Counts of statements and blob calls beside time.** Times on a developer machine vary; counts do not, and growth of
  a count with an unrelated parameter is the defect this harness exists to show.
- **Not part of `dotnet test`.** Only a smoke scenario runs in CI, and it never fails on timing.
- **Rounds, with landings on shared files in agent order.** Free-running agents make the order of overlapping landings
  a matter of timing. In a round all agents create and snapshot at once, then land: agents with paths of their own at
  once, agents meeting on a shared file one after another. The outcome (who lands, who replays) and the final tree are
  the same in every run; concurrency is kept where it does not decide content.
- **Reproducible means the same tree.** Commit and operation ids carry the time and differ between runs. The counts of
  the read operations repeat exactly; those of the agents' operations vary slightly, because a landing takes the fast
  path only when the background reconciliation got there first.
- **Seeding writes workspace records directly**, not through `WorkspaceService.CreateAsync`, whose name check lists
  every workspace - seeding 20,000 workspaces must not cost what the run is there to measure - and runs before the
  server starts, so the server's reconciliation does not follow every seeded landing.
- **Agents share one object cache; read operations use a cold client.** Agents on one machine share `.bassia/cache`;
  the read operations are measured as a job container that just started would pay for them, which also makes their
  counts repeatable.
- **`-data <folder>` keeps and reuses a state** (instead of a separate keep switch): the folder holds the blobs and
  the name of the schema. A run on a kept state continues on it; what the agents write includes the starting seq, so
  they make changes of their own.
- **A sweep marks growth only for parameters that size the state.** For load parameters (agents, rounds) more work is
  asked for, so growth says nothing.

Confirmed by the user (were open questions):

- **Full scenarios run on demand**, plus a manually triggered workflow that keeps the report as an artifact; no
  nightly run.
- **The default large scenario**: 1,000,000 files, 10,000 landings of history, 20,000 closed and 500 open workspaces,
  500 concurrent agents.
- **Local-disk blob store first.** Azurite is not a harness backend for now; the remote-server mode covers a deployed
  Azure server.

## Baseline

`main` before `bassia-c7ba795f` and `bassia-ff9140e9`, on a 4-core container with a local PostgreSQL 16 (fsync off) and
local-disk blobs; 10 November 2026. Times are this machine's; requests, statements and blob calls are not.

### The large state, few agents

`run -scenario large -agents 4 -rounds 1 -probes 1`: 1,000,000 files, 10,000 landings of history, 20,000 closed and
500 open workspaces. Seeding took 241 s (files 21 s, history 210 s, workspaces 9 s). With 500 agents the run does not
finish in reasonable time on this build: every agent's first `workspace create` lists every workspace (below), so the
full large scenario is left for after `bassia-c7ba795f`.

| Operation | Count | Per second | p50 ms | p95 ms | p99 ms | Requests | Statements | Blob calls | Errors |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| workspace create | 4 | 0.4 | 10432.27 | 10436.23 | 10436.23 | 37316 | 6 | 111929 | 0 |
| lease acquire | 4 | 59.8 | 59.83 | 63.76 | 63.76 | 1 | 7 | 0 | 0 |
| snapshot | 8 | 47.1 | 73.61 | 90.36 | 90.36 | 13 | 13 | 14.3 | 0 |
| reconciled after a snapshot | 8 | 65.1 | 35.3 | 62.22 | 62.22 | 0 | - | - | 0 |
| sync | 2 | 97.8 | 10.72 | 20.44 | 20.44 | 2 | 2 | 0 | 0 |
| land (landed) | 4 | 31.6 | 97.33 | 109.68 | 109.68 | 1 | 18.5 | 5.3 | 0 |
| lease release | 4 | 205.4 | 19.41 | 19.44 | 19.44 | 1 | 1 | 0 | 0 |
| reconcile scan after a landing | 1 | 2.7 | 371.84 | 371.84 | 371.84 | 0 | 1003 | 0 | 0 |
| reconcile catch-up at start | 1 | 0.2 | 6166.83 | 6166.83 | 6166.83 | 0 | 5510 | 42003 | 0 |
| conflict overview | 1 | 2.1 | 471.7 | 471.7 | 471.7 | 1 | 1003 | 0 | 0 |
| workspace list (open) | 1 | 0.1 | 8974.73 | 8974.73 | 8974.73 | 41009 | 1 | 123024 | 0 |
| workspace resolve by name | 1 | 0.1 | 9329.47 | 9329.47 | 9329.47 | 41010 | 2 | 123024 | 0 |
| mainline at seq 1 | 1 | 0.5 | 2115.19 | 2115.19 | 2115.19 | 10010 | 1 | 30027 | 0 |
| mainline log page (middle) | 1 | 1 | 1023.52 | 1023.52 | 1023.52 | 5058 | 1 | 15171 | 0 |
| mainline log page (head) | 1 | 59.5 | 16.79 | 16.79 | 16.79 | 51 | 1 | 150 | 0 |
| garbage collection (dry run) | 1 | 0 | 25116.7 | 25116.7 | 25116.7 | 0 | 504 | 103062 | 0 |

Background reconciliation while the agents ran: 1,032 statements per landing (two per open workspace).

What it shows:

- **`workspace create`, `workspace list` and `workspace resolve by name`** read the operation and view of every
  workspace ever kept: 37,000-41,000 requests and 9-10 s on a cold client, 3 blob calls per request.
- **The scan after every landing and the conflict overview** make 2 statements per open workspace (1,003 for 500), 0.4 s
  each; the server's decoded-object cache keeps blob reads at 0 while the workspaces fit in it (50,000 objects).
- **A node's start** reconciles for 6 s and reads 42,000 blobs.
- **`mainline at seq 1`** walks the log: 10,010 requests, 2.1 s; a log page in the middle 5,058 requests.
- **Garbage collection** (dry run) takes 25 s and 103,000 blob reads.
- `land`, `snapshot`, `sync` and leases do not depend on the size of the state: 18.5 statements per landing, 13 per snapshot.
- One `GET /objects/{id}` costs 3 blob calls (two stats and a read).

### Sweeps of the small scenario

Growth from the smallest to the largest value; everything not listed stayed at 1x in requests, statements and blob calls.

| Sweep | Operation | Requests | Statements | Blob calls | p50 |
| --- | --- | ---: | ---: | ---: | ---: |
| `-closed 100,1000,10000` | workspace create | 81x | 1x | 86x | 3.9x |
| | workspace list (open) | 72x (279 to 20,077) | 1x | 72x | 32x (155 ms to 4.9 s) |
| | workspace resolve by name | 72x | 1x | 72x | 32x |
| | reconcile catch-up at start | - | 1x | 70x (285 to 20,083) | 3.8x |
| `-history 100,1000,10000` | mainline at seq 1 | 82x | 1x | 83x | 34x |
| | mainline log page (middle) | 44x | 1x | 45x | 21x |
| `-open 20,100,500` | reconcile scan after a landing | - | 24x | - | 19x |
| | conflict overview | 1x | 24x | - | 19x |
| | reconcile catch-up at start | - | 24x | 5x | 11x |
| | workspace create, list, resolve | 3x | 1x | 3x | - |

The scan after a landing does **not** grow with closed workspaces on a warm server (41 statements at 100 and at 10,000
closed, 19 ms to 47 ms): it grows with open ones. The cost of closed workspaces falls on clients and on a starting node.

## Progress

- [x] `Bassia.LoadTest` with seeding and one scenario
- [x] Statement and blob-call counters attributed per operation
- [x] Sweeps, report comparison
- [x] CI smoke scenario, documentation
- [x] Baseline of `main` recorded here

## Validation

- `dotnet run --project Bassia.LoadTest -c Release -- smoke` (the CI scenario) passes with `BASSIA_TEST_POSTGRES` set,
  hosted and against a separately started `bassia-server` (`-server`).
- `dotnet run --project Bassia.LoadTest -c Release -- sweep -closed 100,1000,10000` marks workspace creation, listing,
  name resolution and the reconciliation at start as growing with the number of closed workspaces on today's `main`.
- Two runs with the same seed end on the same mainline tree and report the same requests, statements and blob calls
  for the read operations.
- `run -scenario large` with few agents seeds 1,000,000 files and 10,000 landings; a second run with the same `-data`
  folder does not seed.
- `compare` of two reports lists the operations that differ.
- `dotnet test` passes and takes no longer than before.
