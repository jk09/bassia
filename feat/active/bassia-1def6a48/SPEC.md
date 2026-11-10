# Load harness: measure the server under many agents and a large monorepo

Status: active. Part 1 of 3 of the first scalability step; it measures what `bassia-c7ba795f` (workspace registry)
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

- [ ] One command runs a scenario against a `bassia-server` it starts itself (local-disk blobs and the PostgreSQL in
      `BASSIA_TEST_POSTGRES`, a schema of its own, dropped afterwards) and, with `-server <url> -token <t>`, against a
      server that is already running.
- [ ] The scenario is set by parameters with documented defaults: files and directory fan-out of the seeded monorepo,
      landings of history seeded before measuring, closed workspaces left behind by that history, concurrent agents,
      operations per agent, files changed per snapshot, and the share of agents that touch the same paths (so some
      landings need a replay).
- [ ] A run is reproducible: the same parameters and seed produce the same tree, the same changes and the same order of
      overlapping changes.
- [ ] Seeding a monorepo of 1,000,000 files and 10,000 landings finishes in minutes on a developer machine (it writes
      objects through the stores, not through working folders), and a seeded state can be kept and reused by a later run.
- [ ] The report gives, per operation (workspace create, snapshot, sync, land by outcome, reconcile after a landing,
      conflict overview, workspace list, mainline at a past seq, mainline log page, lease acquire, garbage collection):
      count, throughput, and latency at p50, p95 and p99 - and per operation the number of PostgreSQL statements and of
      blob-store calls the server made for it.
- [ ] A sweep runs a scenario at several values of one parameter (for example 100, 1,000 and 10,000 open plus closed
      workspaces) and reports each operation's growth between them, marking those that grow with the parameter although
      the work done did not.
- [ ] The report is written as TOML (like `bassia` results) and as a Markdown summary; two reports can be compared,
      listing operations that got slower or cheaper by more than a threshold.
- [ ] A small smoke scenario (seconds) runs in CI on every change and fails only when the harness itself breaks - not
      on timing; the full scenarios are not part of `dotnet test` or of the default CI run.
- [ ] A baseline report of `main` as it is before `bassia-c7ba795f` and `bassia-ff9140e9` is recorded in this feature
      record, with the parameters used.
- [ ] `docs/reference.md` documents the command, the parameters and how to read the report.

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

## Progress

- [ ] `Bassia.LoadTest` with seeding and one scenario
- [ ] Statement and blob-call counters attributed per operation
- [ ] Sweeps, report comparison
- [ ] CI smoke scenario, documentation
- [ ] Baseline of `main` recorded here

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
