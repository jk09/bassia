# Workspace registry: find workspaces by query, not by reading every head

Status: done. Part 2 of 3 of the first scalability step; measured with `bassia-1def6a48` (load harness), sibling
of `bassia-ff9140e9` (mainline index).

## Outcome

Listing workspaces, resolving one by name or prefix, checking that a name is free, the conflict overview and - above
all - finding the workspaces a landing made stale cost what they return, not the number of workspaces that ever
existed. A server with tens of thousands of closed workspaces and hundreds of open ones lands, reconciles and serves
Studio as fast as an empty one.

## Context

- A workspace is a head `ws/<id>` pointing at an operation whose view holds the `WorkspaceRecord` (name, status,
  owner, base seq, created, closed, run, last landing). Nothing else records it, so every question about workspaces
  lists all `ws/` heads and reads an operation and a view per head (`Repository.ListWorkspacesAsync`) - closed
  workspaces included, which stay for `gc.retention_days` (14) and are only filtered out after being read.
- Callers of that scan: `Reconciler.AffectedAsync` after **every landing** and at node start (then one candidate read
  and one path query per open workspace); `Reconciler.OverviewAsync` (`GET /conflicts`); `Repository.ResolveWorkspaceAsync`
  when the reference is not an exact id (so `land <name>`); `OpenWorkspaceNamedAsync` when a name is free (so every
  workspace creation); `workspace list`, `status`, `mainline log`, `AgentRunner`; most Studio pages (Home, Workspaces,
  Conflicts, Leases, New workspace, New run, `RunService`).
- On a clone each of these is `GET /heads?prefix=ws/` (unpaged) and then object fetches for every head.
- The version core reaches storage only through its traits and may not reference a database driver. The path index is
  the precedent for derived state: rebuildable from primary data, kept in PostgreSQL, behind a trait, with an
  in-memory implementation for the core's tests.
- Heads move by compare-and-swap, from any server node and, for workspace heads, on behalf of clients
  (`POST /heads/advance`, which already reads the operation and its view before moving the head).
- A workspace's touched paths are known once it is reconciled: its `Candidate` (behind `rc/<id>`) lists them.

## Acceptance criteria

- [x] Open workspaces are listed in one query that returns only open workspaces, without reading objects of closed
      ones; with 20,000 closed and 500 open workspaces the server makes the same number of PostgreSQL statements and
      blob reads as with 0 closed.
- [x] A workspace is resolved by id, by name, and by a unique prefix (4+ characters) of either, with the results and
      error messages `ResolveWorkspaceAsync` gives today, in a bounded number of statements whatever the number of
      workspaces.
- [x] Creating a workspace whose name is free does not list workspaces.
- [x] After a landing, the workspaces it made stale are found in one statement, whatever the number of workspaces, from
      the registry's touched paths and the path index (see Decisions for what the database does inside it). The result
      is the same set `AffectedAsync` returns today (workspaces with no candidate or one for other commits; workspaces touching a
      changed path; every reconciled workspace when `.bassia.toml` changed).
- [x] The conflict overview (workspaces, overlaps, hot paths) is unchanged in content and no longer reads one
      candidate and runs one path query per workspace.
- [x] `workspace list` (open, and `-all`), `status`, `mainline log` and the Studio pages show what they show today;
      lists of closed workspaces are paged rather than read whole.
- [x] The registry never disagrees with the heads for longer than a head move takes: a crash between moving a head
      and recording it is repaired without an operator, and a reader never acts on a row that names another head than
      the one stored.
- [x] The registry is derived: dropping its tables and starting a server rebuilds it from the heads, identical to the
      one kept incrementally (shown by a test, as for the path index).
- [x] An existing database gets the registry by a schema migration and one backfill on start; several nodes starting
      together backfill once. An older build keeps working through a rolling deploy (the step only adds).
- [x] Clients ask the server instead of listing heads: a clone's workspace list and name resolution are one call each.
      An older client that still lists heads keeps working.
- [x] Garbage collection prunes a closed workspace's row with its head.
- [x] The core's tests run on an in-memory registry held to the same contract as the PostgreSQL one.
- [x] With the harness (`bassia-1def6a48`): reconciliation after a landing, workspace list, name resolution and the
      conflict overview no longer grow with the number of closed workspaces, and the report before and after is
      recorded here.
- [x] The existing test suite passes; only assertions that named the `state` schema's version as 1 follow the new step.

## Approach

A new trait in `Bassia.Core/Storage`, `IWorkspaceIndex`, beside `IPathIndex`: record a workspace as of a head (its
summary: id, name, status, owner, parent, base seq, created, closed, head), record the touched paths of its candidate,
list by status with paging, find by name and by prefix, find the workspaces touching any of a set of paths, and
remove. `Repository` uses it for every workspace question and no longer reads heads to answer them; the in-memory
implementation serves the core's tests and jobs that run without PostgreSQL.

On PostgreSQL (`PostgresState`, schema component `state`, a new migration): `bassia_workspaces` keyed by id with
indexes on `(status, created)` and on name (`COLLATE "C"`, so prefix ranges are exact), and
`bassia_workspace_paths (path, workspace)` filled from each stored candidate. The row is written in the same
transaction as the compare-and-swap of the `ws/` head it describes, so the two cannot diverge; each row carries the
head it was derived from. The touched paths are replaced when a candidate is stored (`rc/` head moved), again in that
transaction.

`AffectedAsync(seq)` becomes: the paths changed at that seq (already in `bassia_paths`) joined with
`bassia_workspace_paths`, plus open workspaces without a current candidate - one statement.

The server exposes the registry under `/api/v1/workspaces` (list with status and paging, find, get, stale);
`RemoteWorkspaceIndex` calls it. `GET /heads` stays for older clients and for garbage collection.

Backfill, and repair after a crash, is the catch-up the path index already has: rows are rebuilt from the `ws/` and
`rc/` heads under the migration's advisory lock.

## Decisions

- **A derived index behind a trait, not a new source of truth.** Heads and the objects behind them stay primary; the
  registry can be dropped and rebuilt, like the path index. Rejected: moving the workspace record out of the view
  object into a table - it would change object formats, the operation log of a workspace and clones.
- **Touched paths come from the candidate.** The server already computes them when it reconciles; a workspace that is
  not reconciled yet is found by its missing candidate instead.

- **A repository without a registry scans** (`ScanningWorkspaceIndex`): the old behavior behind the same trait, for an
  older client's remote stores and for plain in-memory stores. `Repository` has one code path.
- **The registry moves the candidate heads too** (`AdvanceCandidateAsync`), so the touched paths change in the
  transaction that moves `rc/<id>`, as the workspace row does with `ws/<id>`.
- **Stale workspaces in one statement, evaluated per open workspace.** The query checks, for each open workspace, its
  candidate's commits and whether one of its touched paths (or `.bassia.toml`) has a path-index row after the
  candidate's seq. Driving it from the landing's changed paths instead was rejected: a candidate that nothing
  overtakes keeps an old seq, so the range of history to join against is not bounded by the last landing. The work
  inside the statement follows open workspaces times their touched paths (index probes), never closed ones.
- **Summaries keep millisecond times**, the precision a stored record has, so a row written with the head equals one
  rebuilt from it.
- **A listing with closed workspaces returns all open ones and a page of closed ones** (`Repository.ClosedPage` = 100,
  newest first); run lists and name lookups that used to read every workspace see that page.
- **`GET /heads` is not paged** in this change: with the registry no client lists workspace heads any more; garbage
  collection still reads all heads in one reply. Left for the garbage-collection work.
- **The load harness analyzes its database after seeding**: without statistics PostgreSQL scanned the whole workspace
  table for the open ones (18 ms at 10,000 closed, 2 ms with statistics).

Confirmed by the user (were open questions):

- **`IWorkspaceIndex` is a trait of its own**, beside `IPathIndex`: derived data, with memory, PostgreSQL and remote
  implementations. `CLAUDE.md` and the reference's count of the core's traits are updated with it.
- **The row is written in the head's transaction**, through one combined store call that
  `Repository.CommitWorkspaceAsync` and `/heads/advance` use; stores without it (memory) do the two steps under a lock.
  Rejected: writing the row afterwards and letting every reader check its head.
- **Touched paths are dropped when a workspace closes**; the path index answers "who changed this path" for landed
  changes.
- **Lists of closed workspaces are paged**: 100 per page, newest first, in `workspace list -all` and Studio.

## Results

Load harness (`bassia-1def6a48`), small scenario, the same machine as the baseline; counts are per execution.

| At 10,000 closed workspaces | Before | After |
| --- | ---: | ---: |
| workspace list (open), cold client: requests / blob calls | 20,077 / 60,228 | 3 / 80 |
| workspace resolve by name: requests | 20,078 | 5 |
| workspace create: requests / blob calls | 7,851 / 23,534 | 10 / 9 |
| reconcile catch-up at start: blob calls | 20,083 | 83 |
| scan after a landing: statements | 41 | 3 |
| conflict overview: statements | 41 | 4 |

The sweep over 100, 1,000 and 10,000 closed workspaces marks nothing as growing any more. Over 20, 100 and 500 open
workspaces the scan after a landing stays at 3 statements (979 before at 500 open; 11 ms) and the conflict overview at
4; what still grows with open workspaces is the reconciliation of each at a node's start, which is work per workspace.
`workspace close` went from 2 statements to 4 and resolve by name from 2 to 3 (the row is written in a transaction).

The full large scenario (500 agents on 20,000 closed workspaces), which did not finish before, is recorded with
`bassia-ff9140e9`: its first attempt here found a defect in the harness itself (an agent that owns few files could
pick the same one twice in a snapshot), fixed in this change.

## Progress

- [x] `IWorkspaceIndex`, in-memory implementation, contract tests
- [x] PostgreSQL tables, migration, transactional write with the head, backfill and rebuild
- [x] `Repository` and `Reconciler` on the registry
- [x] Server API and remote implementation; paged `/heads`
- [x] CLI and Studio on paged lists
- [x] Harness report before and after, recorded here; documentation (`docs/reference.md`: Architecture, server API)

## Validation

- `dotnet test` (with `BASSIA_TEST_POSTGRES`): the store contract tests over memory and PostgreSQL, a rebuild test
  (drop the tables, restart, compare), a crash test (head moved, row not written), concurrent head moves from several
  landing services, and `ReconcilerTests` and `ServerTests` unchanged.
- A test counts statements and object reads of a landing's reconciliation with 0 and with 2,000 closed workspaces and
  requires them equal.
- `SchemaMigrationTests`-style test: a database at the previous version is migrated and backfilled by several nodes
  starting at once.
- `dotnet run --project Bassia.LoadTest -- sweep -workspaces 100,1000,10000` before and after.
