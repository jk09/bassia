# Several server nodes: one watch stream, one reconcile queue, lease waits without polling

Status: proposed. Second scalability step.

## Outcome

With several `bassia-server` nodes behind a load balancer, a client watching any node sees every landing and workspace
change, each stale workspace is reconciled once by whichever node is free, and a client waiting for a lease is woken
when it is released instead of asking ten times a second.

## Context

- `ServerApp.Publish` writes watch events to channels in the node's own memory: a client connected to node B never
  sees a landing node A performed. Studio's live updates and `mainline watch` depend on it. `deploy/azure` runs 1-3
  replicas.
- `ReconcileQueue` is an in-memory, de-duplicated queue with one worker, fed by the events of its own node. Two nodes
  can reconcile the same workspace; a node that dies loses its queue until another start's catch-up.
- `PostgresState.AcquireAsync` serializes every grant on one advisory lock, loads every live lease and the whole queue
  per attempt, and a waiter repeats that every 100 ms in a new transaction.
- All nodes share one PostgreSQL; `LISTEN`/`NOTIFY` is available on every supported version and on Azure.

## Acceptance criteria

- [ ] An event published on one node (landed, workspace, reconciled) reaches watchers of every node, in the order the
      database delivered it, at most once per watcher (a test with two servers on one database).
- [ ] A workspace made stale is reconciled by one node at a time; when the node that took it dies, another takes it
      within a bounded time. Several workers per node reconcile in parallel (`-reconcile-workers`, default 4).
- [ ] A lease waiter is granted within a few milliseconds of the release that frees it, and an idle waiter issues no
      statement more often than its keep-alive (a test counts statements while it waits).
- [ ] A grant loads only the leases and waiters that overlap the requested prefix.
- [ ] A node that loses its listening connection reconnects and catches up (a full reconcile scan; watchers get a
      `resync` event) rather than miss events silently.
- [ ] One node behaves as today; the in-memory stores keep working without any of this.
- [ ] Harness: lease acquire statements with 500 live leases; `reconciled after a snapshot` with two hosted nodes.

## Approach

A small trait in the server's stores layer, `IServerEvents` (publish, subscribe), implemented on PostgreSQL with
`NOTIFY bassia_events, <json>` and one listening connection per node, and in memory for tests. `ServerApp.Publish`
publishes through it and delivers to local watchers only what arrives from it. The reconcile queue enqueues on events
from any node and takes a workspace with `pg_try_advisory_lock` on its id for the length of the reconciliation; workers
are tasks reading one channel. Lease waits listen for `released` events (and a 1 s fallback poll); grants read
overlapping rows by prefix range.

## Decisions

Taken unattended, as the user asked (the recommended options):

- **`LISTEN`/`NOTIFY`, not a queue table or a broker.** Events are hints: every consumer can recompute from the
  database (catch-up scan, lease list), so at-least-once with resync on reconnect is enough.
- **Advisory try-locks, not a persisted reconcile queue**: the registry's stale query is the durable queue.
- **Payloads over 8 kB are published as "resync"**, PostgreSQL's limit.

## Progress

- [ ] Events trait, PostgreSQL and memory; watch through it
- [ ] Reconcile workers with try-locks
- [ ] Lease wake-ups and overlapping reads
- [ ] Two-node tests; harness numbers

## Validation

`dotnet test` (two servers on one schema); harness lease and reconcile rows before and after.
