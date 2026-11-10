# Mainline index: reach any seq of mainline in one lookup

Status: done. Part 3 of 3 of the first scalability step; measured with `bassia-1def6a48` (load harness), sibling
of `bassia-c7ba795f` (workspace registry).

## Outcome

Mainline at a past seq, a page of mainline's log and the history of a path cost the same on a monorepo with a million
landings as on a new one. Reading mainline as of seq N no longer walks the operation log back from the head, one object
read per landing.

## Context

- Mainline is a chain of operations: each landing's operation points at its parent, and through its view at the commit
  and tree. Only the head is addressable (`main` in `bassia_heads`). `Repository.MainAtSeqAsync(seq)` therefore reads
  `head.seq - seq` operations one after another, each a blob read on a server and an HTTP round trip on a clone, and
  `LogAsync` pages by walking from the head as well.
- Callers: `bassia mainline` commands with `-seq` and restore to a seq (`MainlineCommands`); Studio's tree browser at
  a seq, diffs between seqs, the log page (which re-walks from the head for every page) and path history, which calls
  `MainAtSeqAsync` once per version of the path (`MonorepoClient`). `Repository.EnsureIndexedAsync` walks back from the
  head to the indexed seq, and `GarbageCollector` walks the whole log to collect its roots.
- The path index (`bassia_paths`, `indexed_seq` in `bassia_meta`) is already filled in seq order from the operation
  log by `EnsureIndexedAsync`, in one transaction per seq under a row lock, by whichever node gets there first. It is
  derived and rebuildable; `IPathIndex.ApplyAsync(seq, changes)` is its only writer.
- Seq 0 is the empty root commit; the path index holds nothing for it.
- The version core may not reference a database driver; a clone reaches the server's path index through
  `RemotePathIndex` (`/paths/...`).

## Acceptance criteria

- [x] `MainAtSeqAsync(seq)` reads a constant number of objects and makes a constant number of store calls for any seq
      and any length of history (one index lookup, then the operation, view and commit), with the result it gives today.
- [x] A page of mainline's log starting at any seq costs the page: no walk from the head. `mainline log` and Studio's
      log page show what they show today.
- [x] The history of a path in Studio makes a number of calls bounded by the versions shown, each constant - not by
      the distance of those versions from the head.
- [x] A seq outside mainline (negative, or beyond the head) is refused with today's message.
- [x] The index row of a seq is written in the same transaction as that seq's path changes and `indexed_seq`: after
      any crash, the mainline index and the path index cover exactly the same seqs.
- [x] The index is derived: dropping it and starting a server rebuilds it from the operation log, identical to the one
      kept incrementally (shown by a test, beside the path index's rebuild test).
- [x] An existing database gets the index by a schema migration and one backfill from the operation log on start
      (a single walk, resumable, done once when several nodes start together); until the backfill covers a seq, lookups
      of that seq fall back to the walk and stay correct. An older build keeps working through a rolling deploy.
- [x] Garbage collection takes mainline's operations, views, commits, task specs and tree roots from the index instead
      of reading the chain one object at a time, and marks the same objects as today; with an index that does not hold
      every seq it walks the log as before.
- [x] A clone resolves a seq with one call to the server and then reads that landing's objects; a client of a server
      without the index, and an older client that walks the log, keep working.
- [x] The core's tests run on an in-memory implementation held to the same contract as the PostgreSQL one.
- [x] With the harness (`bassia-1def6a48`): mainline at a past seq and a log page no longer grow with the length of
      history, and the report before and after is recorded here.
- [x] The existing test suite passes unchanged.

## Approach

Extend the path index with the landing itself. `IPathIndex.ApplyAsync` takes, beside the seq and its changes, the
landing's entry - operation id, commit id, tree id, the workspace that landed, and time - and the trait gains
`GetEntryAsync(seq)` and `ListEntriesAsync(fromSeq, count)` (newest first). Both stores implement it: in memory a list;
on PostgreSQL a new table `bassia_mainline (seq bigint PRIMARY KEY, op, commit, tree, workspace, time)` added by a
migration of the `state` component and written inside `ApplyAsync`'s existing transaction. `EnsureIndexedAsync`
already has every value in hand when it applies a seq.

`Repository.MainAtSeqAsync` and `LogAsync(HeadName.Main, ...)` read the entry and then the objects it names; when the
entry is missing (backfill not there yet, or a store without the index) they walk as today. Seq 0 gets an entry too.

The server exposes `GET /api/v1/main/at?seq=` and `GET /api/v1/main/log?from=&count=`; `RemotePathIndex` calls them.

Backfill: a second watermark in `bassia_meta` (`mainline_indexed_from`) marks the lowest seq with an entry; a
background step at start walks the log once from that seq down to 0 in batches, under the migration's advisory lock.

## Decisions

- **Part of the path index, not a separate store.** Same writer, same order, same transaction, same rebuild; a
  separate trait could cover a different set of seqs than the path index and would need its own catch-up.
  Rejected: heads named `main/<seq>` - a million rows in `bassia_heads`, listed by every head listing, and they would
  be primary data.
- **Derived, with a fallback to the walk.** Correctness never depends on the index; only cost does.
- **Workspace id and time in the entry.** The log page and Studio's history show them; storing them avoids reading
  the operation for each row of a page.

- **The entry also names the view and the commit's task spec.** With them garbage collection marks everything a
  landing holds without reading its operation, view or commit: three blob reads per landing become none.
- **`EnsureIndexedAsync` is left as it is.** It walks back only from the head to the indexed seq - exactly the landings
  it has to apply, whose trees it needs anyway - so there is nothing for the index to save there.
- **No separate log-page call for clients.** Studio's log page and `mainline log` start with mainline at a seq and read
  the page's operations from there; with the lookup constant, the page costs the page.
- **`bassia-server migrate` does not backfill**: it runs without a data folder or blob store, and the backfill reads
  operations. The server backfills in the background at start (lookups fall back to the walk meanwhile); a local
  monorepo backfills when it is opened and finds landings the index lacks. This replaces the answer to open question 3.

Confirmed by the user (were open questions):

- **`IPathIndex` is extended** (`ApplyAsync` takes the landing's entry; `GetEntryAsync`, `ListEntriesAsync`), rather
  than adding an optional interface a store might leave out.
- **The row stays narrow**: no intent or author; a log page reads its operations and commits in one batch.
- **Backfill runs in the background** with the walk as fallback, and `bassia-server migrate` runs it to completion for
  deployments that migrate as a release step.

## Results

Load harness (`bassia-1def6a48`), counts per execution, the same machine as the baseline.

| On 10,000 landings of history (large scenario), cold client | Before | After |
| --- | ---: | ---: |
| mainline at seq 1: requests / blob calls / p50 | 10,010 / 30,027 / 2.1 s | 8 / 12 / 5 ms |
| mainline log page in the middle: requests / blob calls / p50 | 5,058 / 15,171 / 1.0 s | 57 / 110 / 16 ms |
| mainline log page at the head: requests | 51 | 51 |

**The full large scenario** - 1,000,000 files, 10,000 landings, 20,000 closed and 500 open workspaces, 500 agents, 2
rounds - which did not finish before the workspace registry, now runs to the end: 1,000 landings in 70 s (14.3 per
second), 234 refused and replayed, no errors; seeding 272 s. Under that load a landing waits for the gate (p50 5.3 s),
a snapshot takes 5.9 s and a lease 1.8 s at the median: the next things to look at (`bassia-6272ab3a` for the gate,
`bassia-1cdec5d0` for leases). After every landing the server's background reconciliation costs 18 statements
(1,032 before).

Garbage collection (dry run) on that state: 37 s and 127,000 blob reads. Mainline's operations, views and commits are
no longer among them (a test counts at least three reads per landing fewer); what remains is one read per file object
and the operations of 21,000 workspaces - the subject of `bassia-2e6c8766`.

## Progress

- [x] Trait extension, in-memory implementation, contract tests
- [x] PostgreSQL table, migration, write inside `ApplyAsync`, backfill and rebuild
- [x] `Repository` (seq lookup, log, catch-up) and `GarbageCollector` on the index
- [x] Server API and remote implementation
- [x] Studio's log and path history on the index
- [x] Harness report before and after, recorded here; documentation (`docs/reference.md`: schema, server API)

## Validation

- `dotnet test` (with `BASSIA_TEST_POSTGRES`): the store contract tests over memory and PostgreSQL; a test that counts
  object reads of `MainAtSeqAsync(1)` on histories of 10 and 1,000 landings and requires them equal; the rebuild test
  (drop, restart, compare); a backfill test on a database migrated from the previous version, with lookups during the
  backfill; several landing services landing at once leave no gap in the index; garbage collection marks the same set
  with and without the index.
- `dotnet run --project Bassia.LoadTest -- sweep -history 1000,10000,100000` before and after.
