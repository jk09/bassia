# Several server nodes: one watch stream, one reconcile queue, lease waits without polling

Status: done. Second scalability step.

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

- [x] An event published on one node (landed, workspace, reconciled) reaches watchers of every node, once per watcher
      (a test with two servers on one database and blob folder).
- [x] A workspace is reconciled by one node at a time (a claim among the nodes); a node asked to reconcile a workspace
      another has claimed looks at it again shortly. Several workers per node reconcile in parallel
      (`-reconcile-workers`, default 4).
- [x] A workspace made stale whose event reached no live queue is found within 30 seconds by any node's periodic look
      (a test asks for the look instead of waiting).
- [x] A lease waiter is granted within 400 ms of the release that frees it, on another node, and while it waits it
      looks twice a second, not ten times (a test counts statements).
- [x] A grant reads only the leases and waiters whose prefix overlaps the requested one.
- [x] A node that loses its listening connection reconnects, tells its watchers to `resync` and looks for stale
      workspaces again, rather than miss events silently. An event too large for a notification travels as `resync`.
- [x] One node behaves as before; the in-memory stores keep working without any of this.
- [x] Harness: recorded below.

## Approach

A small trait in the server's stores layer, `IServerEvents` (publish, subscribe), implemented on PostgreSQL with
`NOTIFY bassia_events, <json>` and one listening connection per node, and in memory for tests. `ServerApp.Publish`
publishes through it and delivers to local watchers only what arrives from it. The reconcile queue enqueues on events
from any node and takes a workspace with `pg_try_advisory_lock` on its id for the length of the reconciliation; workers
are tasks reading one channel. Lease waits listen for `released` events (and a 1 s fallback poll); grants read
overlapping rows by prefix range.

## Decisions

Taken unattended, as the user asked (the recommended options):

- **A node delivers its own events at once and announces them to the others**, which ignore their own on the way
  back: one node needs no round trip through PostgreSQL to serve its watchers, and a failed announcement costs only
  the other nodes' watchers one event.
- **Only the node whose event it was queues the reconciliation; the others rely on the claim and the periodic look.**
  Queuing on every node would multiply the work by the number of nodes for the claim to throw away.
- **Channels and claims are named per database schema**: PostgreSQL's are per database, and several monorepos (and
  every test) share one.
- **A waiter's fallback look is every 500 ms** (it was 100 ms): what a release does not announce - an expiry, a waiter
  ahead giving up - is noticed that late at worst.
- **The lease lock stays one per database.** Grants still serialize on one advisory lock; they are now short, since
  each reads only overlapping rows. Splitting the lock by prefix is left until a measurement asks for it.
- **The reconnect path is not covered by a test**: it needs the listening connection killed under a running node.

- **`LISTEN`/`NOTIFY`, not a queue table or a broker.** Events are hints: every consumer can recompute from the
  database (catch-up scan, lease list), so at-least-once with resync on reconnect is enough.
- **Advisory try-locks, not a persisted reconcile queue**: the registry's stale query is the durable queue.
- **Payloads over 8 kB are published as "resync"**, PostgreSQL's limit.

## Results

Load harness, medium scenario (50 agents), one hosted node, before and after:

| | Before | After |
| --- | ---: | ---: |
| reconciled after a snapshot: p50 / p95 | 465 ms / 1,237 ms | 163 ms / 385 ms |
| background reconciliation: statements per landing | 25 | 62 |
| landings per second | 16.4 | 16.8 |
| lease acquire: statements | 7 | 7 |

Four workers reconcile three times sooner. The background statements per landing rise: each reconciliation now
takes and releases a claim, and with several workers fewer snapshots of one workspace are folded into one
reconciliation. The harness hosts one node; two nodes are covered by tests, not measured.

## Progress

- [x] Events, PostgreSQL; watch through them
- [x] Reconcile workers with claims and the periodic look
- [x] Lease wake-ups and overlapping reads
- [x] Two-node tests; harness numbers

## Validation

`dotnet test` (two servers on one schema); harness lease and reconcile rows before and after.
