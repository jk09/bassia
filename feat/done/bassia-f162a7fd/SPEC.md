# Pre-reconciled landings and a live conflict overview

## Outcome

The server keeps every open workspace reconciled with mainline as edits arrive, so it always knows, in real time,
which workspaces can land, which conflict with mainline and which overlap each other. Landing a workspace whose
reconciliation is current costs only a check, a tree edit over its touched paths and the compare-and-swap of `main`:
conflict policies, semantic merges and validation run beforehand, outside the landing's critical section.

## Context

- A landing (`LandingService.LandOnceAsync`) does everything inside the per-node gate: path-index lookups, conflict
  policies, the semantic merge driver, the validation hook, the tree edit, and the CAS. A slow validation hook caps a
  node at about one landing per validation.
- Conflict overviews (Studio's Conflicts page, the dashboard) run one dry-run landing per open workspace on request,
  through the same gate.
- The server already learns of every workspace snapshot: the client advances the workspace head, and the server
  publishes a `workspace` event on its watch stream.
- Limit: mainline's root tree changes at every landing, so a merged tree computed against seq N is stale at N+1. What
  can move out of the landing is the decision (conflicts, merges, validation); a final edit of the touched paths onto
  the current mainline tree remains, and costs only those paths.

## Acceptance criteria

- [x] A reconciler keeps, for every open workspace, a **candidate**: the base and working commits it is for, the
      mainline seq it was reconciled against, the touched paths, the resolved changes, conflicts, merged paths and the
      outcome a landing would have. It is stored behind `rc/<workspace>` heads (PostgreSQL on a server) and dropped
      when the workspace closes.
- [x] On a server, a workspace snapshot queues its reconciliation, and a landing queues the reconciliation of the
      open workspaces whose touched paths (or `.bassia.toml`) it moved; a node catches up at start. Clients cannot
      move or delete candidate heads.
- [x] While an agent runs, its workspace is snapshotted once its files settle (`agent.snapshot_quiet`, default 2 s,
      0 = off), so the server sees the edits before the run ends.
- [x] A landing whose candidate is for the workspace's current commits, would land, and none of whose paths (nor
      `.bassia.toml`) moved since, applies the candidate's changes and advances `main` by CAS without diffing,
      conflict policies, merge drivers or validation; leases are still checked. `reconciled = true` says so.
- [x] Otherwise the landing plans as before, with the same planner the reconciler uses; strict landings never take
      the fast path; outcomes are unchanged (the existing suite passes).
- [x] Validation of machine-merged results runs in the reconciler, outside the landing's critical section: a landing
      on the seq it was validated on does not validate again, and another workspace's validation does not hold up
      unrelated landings. A machine-merged candidate is validated again when mainline moved (by design).
- [x] A **conflict overview** (`GET /api/v1/conflicts`, `Monorepo.Overview`) reports every open workspace's state
      against mainline, whether its candidate is current, overlaps between open workspaces and hot paths. Studio's and
      the dashboard's conflict views read it (one call instead of a dry-run landing per workspace) and refresh on the
      `reconciled` watch event. A local monorepo reconciles stale candidates when the overview is read.

## Approach

1. **Edit telemetry: snapshots, taken automatically.** While an agent runs, `bassia agent start` watches the workspace
   folder and snapshots it once files have been quiet for `agent.snapshot_quiet` seconds (default 2; 0 turns it off).
   A snapshot advances the workspace head on the server, which is the signal to reconcile.
2. **Candidates are objects.** A `Candidate` (canonical CBOR, like every structural object) records the workspace's
   base and working commits, the mainline seq and commit it was reconciled against, the touched paths, the resolved
   path changes, conflicts, merged paths, whether it was machine-merged, and the outcome a landing would have. The
   head `rc/<workspace>` points at the workspace's latest candidate. Heads are already stored in PostgreSQL on a
   server (SQLite in a local monorepo), so candidates persist, survive restarts and are shared by every node, and
   `Bassia.Core` keeps its four storage traits. Garbage collection keeps the candidates of open workspaces and drops
   the rest.
3. **One planner.** The per-path classification of a landing (path index since the base, conflict policies, merge
   drivers, validation of machine-merged trees) moves out of `LandingService` into a planner used by both the
   reconciler and the landing, so a candidate's outcome is by construction what a landing would decide.
4. **Reconciler** (`Bassia.Core`). Reconciles one workspace into a candidate and stores it by compare-and-swap of its
   head. After a landing it reconciles only the workspaces with a touched path that changed (or all of them when
   `.bassia.toml` changed); the others keep their candidate, whose validity a landing checks against the path index.
5. **Landing fast path.** A landing whose candidate matches the workspace's base and working commits, would land, and
   none of whose touched paths (nor `.bassia.toml`) changed on mainline after the candidate's seq, applies the
   candidate's changes to the current mainline tree, checks leases and advances `main` by CAS: no diff of the
   workspace, no conflict policies, merges or validation. A machine-merged candidate is used only on the exact seq it
   was validated on. Otherwise the landing takes the existing path.
6. **Server.** The node that receives a workspace snapshot, or performs a landing, queues the reconciliation of the
   workspaces affected (a background queue, de-duplicated); at start a node catches up on open workspaces whose
   candidate is missing or stale. A `reconciled` watch event follows every stored candidate. A local monorepo has no
   long-running process: it reconciles stale candidates when the overview is read.
7. **Overview.** `GET /conflicts` (and the local equivalent) returns every open workspace's candidate state against
   mainline, the overlaps between open workspaces (paths two or more of them touch) and the hot paths. Studio's and
   the dashboard's conflict views read it instead of running one dry-run landing per workspace.

## Decisions

- **Telemetry: automatic snapshots** (user), debounced while an agent runs; full content, so the server can pre-merge.
- **Overview: mainline state plus overlaps between open workspaces and hot paths, reporting only** (user): no
  automatic notification or action.
- **No group commit** (user): one landing per CAS; batching can follow as its own feature.
- **Candidates persisted in PostgreSQL** (user). Stored as objects behind `rc/` heads, so they ride on the existing
  heads table instead of a new one. Rather than electing one node to compute them, the node that sees an event
  (snapshot or landing) computes the candidates it affects: every event reaches exactly one node, so the work is not
  duplicated, and there is no election to fail over. Concurrent writers are resolved by the head's CAS.
- **Validation of machine-merged results is not reused across seqs**: other landings may break a build without
  touching the merged paths, so such a candidate takes the fast path only when mainline has not moved since.

## Progress

- [x] Open questions answered.
- [x] Candidate object and rc/ heads; GC.
- [x] Shared planner and reconciler.
- [x] Landing fast path.
- [x] Server queue, overview API and watch events; local lazy reconciliation.
- [x] Auto-snapshots during agent runs.
- [x] Studio and dashboard on the overview; docs.
- [x] Full suite with PostgreSQL (260 passed) and without (217 passed, 42 skipped); timing-sensitive tests repeated 5x.

## Validation

- `ReconcilerTests` (in-memory stores): candidate encoding; the fast path after unrelated landings; no fast path when
  a touched path moved, for older commits, after a policy change, for a strict landing, or for a machine-merged
  candidate after mainline moved; a machine-merged candidate validated once and landing without waiting for a 400 ms
  validation; a 2 s validation in the reconciler not holding up a disjoint landing; the overview's states, overlaps and
  hot paths; garbage collection keeping open workspaces' candidates (with merge results) and dropping closed ones.
- `ServerTests.TheServer_ReconcilesSnapshotsAheadOfLanding_AndShowsTheOverviewLive` (PostgreSQL): background
  reconciliation after snapshots and after a landing, the remote overview, a fast-path landing over HTTP, and the
  refused candidate head writes.
- `AutoSnapshotTests` and `CliTests.AgentStart_SnapshotsTheWorkspaceWhileTheAgentWorks`: snapshots while the agent
  works, not while nothing changes, not for `.bassia` files, retried after a failure.
- `BASSIA_TEST_POSTGRES=... dotnet test Bassia.Tests`: the whole suite.
