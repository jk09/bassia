# Mainline index: reach any seq of mainline in one lookup

Status: proposed. Part 3 of 3 of the first scalability step; measured with `bassia-1def6a48` (load harness), sibling
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

- [ ] `MainAtSeqAsync(seq)` reads a constant number of objects and makes a constant number of store calls for any seq
      and any length of history (one index lookup, then the operation, view and commit), with the result it gives today.
- [ ] A page of mainline's log starting at any seq costs the page: no walk from the head. `mainline log` and Studio's
      log page show what they show today.
- [ ] The history of a path in Studio makes a number of calls bounded by the versions shown, each constant - not by
      the distance of those versions from the head.
- [ ] A seq outside mainline (negative, or beyond the head) is refused with today's message.
- [ ] The index row of a seq is written in the same transaction as that seq's path changes and `indexed_seq`: after
      any crash, the mainline index and the path index cover exactly the same seqs.
- [ ] The index is derived: dropping it and starting a server rebuilds it from the operation log, identical to the one
      kept incrementally (shown by a test, beside the path index's rebuild test).
- [ ] An existing database gets the index by a schema migration and one backfill from the operation log on start
      (a single walk, resumable, done once when several nodes start together); until the backfill covers a seq, lookups
      of that seq fall back to the walk and stay correct. An older build keeps working through a rolling deploy.
- [ ] `EnsureIndexedAsync` finds where to resume from the index instead of walking back from the head when it is far
      behind; garbage collection takes mainline's operations, views and commits from the index instead of reading the
      chain one object at a time, and marks the same objects as today.
- [ ] A clone resolves a seq and a log page with one call each to the server; an older client that walks the log keeps
      working.
- [ ] The core's tests run on an in-memory implementation held to the same contract as the PostgreSQL one.
- [ ] With the harness (`bassia-1def6a48`): mainline at a past seq and a log page no longer grow with the length of
      history, and the report before and after is recorded here.
- [ ] The existing test suite passes unchanged.

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

Open questions (to resolve before implementation):

1. Changing `IPathIndex.ApplyAsync`'s signature touches every implementation and `RemotePathIndex`. Is extending the
   trait acceptable, or should the entry go through a separate optional interface the stores also implement?
   Proposed: extend the trait - there are three implementations, and an optional interface invites a store that
   indexes paths but not seqs.
2. Should the entry also keep the landing's first line of intent and its author, so a log page reads no objects at
   all? Proposed: no - a page of 50 reads 50 small cached objects in one batch; keep the table narrow.
3. Backfill on a long history holds no lock on landings, but takes time. Is a background backfill with fallback
   acceptable, or should `bassia-server migrate` finish it before nodes start? Proposed: background, and
   `bassia-server migrate` also runs it to completion for deployments that migrate as a release step.

## Progress

- [ ] Trait extension, in-memory implementation, contract tests
- [ ] PostgreSQL table, migration, write inside `ApplyAsync`, backfill and rebuild
- [ ] `Repository` (seq lookup, log, catch-up) and `GarbageCollector` on the index
- [ ] Server API and remote implementation
- [ ] Studio's log and path history on the index
- [ ] Harness report before and after, recorded here; documentation (`docs/reference.md`: schema, server API)

## Validation

- `dotnet test` (with `BASSIA_TEST_POSTGRES`): the store contract tests over memory and PostgreSQL; a test that counts
  object reads of `MainAtSeqAsync(1)` on histories of 10 and 1,000 landings and requires them equal; the rebuild test
  (drop, restart, compare); a backfill test on a database migrated from the previous version, with lookups during the
  backfill; several landing services landing at once leave no gap in the index; garbage collection marks the same set
  with and without the index.
- `dotnet run --project Bassia.LoadTest -- sweep -history 1000,10000,100000` before and after.
