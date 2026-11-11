# Object catalog: answer "have" from PostgreSQL and collect garbage safely

Status: done. Second scalability step; depends on `bassia-ff9140e9` (mainline index) for the roots of mainline.

## Outcome

The server knows which objects it holds without asking blob storage one object at a time, garbage collection no longer
lists the whole bucket or walks mainline's log blob by blob, and an object a client was just told the server has can
not be swept before the client's snapshot refers to it.

## Context

- Every object is a blob. `HasAsync` is one `HEAD` per id (16 at a time on Azure, all at once on S3); a snapshot asks
  about every new object before it uploads.
- `GarbageCollector.CollectAsync` marks from every head, reading each operation, view and commit of mainline's whole
  log one blob at a time, keeps the live set in memory, lists every blob in the bucket and deletes one at a time. The
  baseline (`bassia-1def6a48`): 25 s and 103,000 blob reads for a dry run on 10,000 landings.
- **A race.** Blobs are swept when unreachable and older than the grace period (24 h), by the time they were written.
  A put of an object that exists changes nothing (`If-None-Match`), so its age stays. A client whose `Have` says an old,
  unreferenced object is present skips the upload; a collection that marked before the client's head moved then
  deletes it, and the workspace points at a missing object.
- Trees already live in PostgreSQL (`bassia_trees`, with `written`); the same race applies to them.
- The core may not reference a database driver; stores are decorated (`CachingObjectStore`, `CountingObjectStore`).

## Acceptance criteria

- [x] `Have` for any number of ids costs one PostgreSQL statement per 5,000 ids and no blob-store call, for objects
      stored through the server.
- [x] An object reported present by `Have`, or put again, is not swept by a collection whose cutoff precedes that
      moment: a test interleaves a client's snapshot between a collection's mark and its sweep, and the workspace
      reads back whole (memory and PostgreSQL). The same holds for trees.
- [x] An upload of an object a collection is deleting waits for the deletes and stores the object after them.
- [x] Garbage collection takes its candidates from the catalog (no bucket listing), mainline's roots from the mainline
      index, deletes in batches of 1,000 with 16 deletes at a time, and reports what it did as today; a dry run
      changes nothing.
- [x] A catalog that does not cover the store (an existing deployment, a restore) is brought in step by
      `bassia-server gc`, which then lists the bucket once (`-reconcile` forces it); until then `Have` asks the blob
      store about ids the catalog does not know, so nothing is reported missing that exists.
- [x] Objects uploaded directly on signed URLs are announced as pending and recorded when the next `Have` (or the
      server's own blob endpoint) sees them.
- [x] `scrub` reads the blobs the catalog lists and reports those it cannot read.
- [x] The schema migration adds only; older nodes keep working through a rolling deploy.
- [x] Harness: blob calls per snapshot fall (recorded below).

Not done here, by decision (below): keeping the live set of a collection out of memory.

## Approach

A trait `IObjectCatalog` (touch, begin, confirm, record, doom, remove, the sweep lock, list, complete) with PostgreSQL
(`bassia_objects`: id, size, state, touched; migration 4 of `state`) and in-memory implementations, and a decorator
`CatalogedObjectStore` over the blob store: `HasAsync` answers from the catalog, touching what it reports, and asks the
blob store only about pending rows and, while the catalog is not complete, unknown ids; `PutManyAsync` writes the
blobs and records them in one statement; maintenance lists the catalog, and `SweepAsync` dooms a batch not used since
the cutoff, deletes the blobs and removes the rows under an advisory lock. Trees: `HasAsync` and a repeated put
refresh `written`, and `DeleteAsync` takes the cutoff.

## Decisions

Taken unattended, as the user asked (the recommended options):

- **Three states and a lock, not a transaction around blob deletes.** A blob store cannot join a database
  transaction. A collection marks rows doomed, deletes the blobs, removes the rows; a crash in between leaves doomed
  rows, which are not vouched for, so clients upload again and the next collection finishes the job.
- **A put is write-then-record.** One blob write and one statement per batch. The catalog refuses to record a doomed
  object; only those take the slower path that waits for the collection.
- **The server only.** A local monorepo's blob store is a folder (`File.Exists`), and one user; it keeps no catalog.
- **The live set stays in memory for now.** Marking still builds one set of ids in the collecting process (about 100
  bytes an object). Moving it into PostgreSQL changes the marking walk itself and belongs with reading file objects
  in batches - both in a follow-up, since neither affects correctness.
- **PostgreSQL statements in place of blob calls.** Writing an object through the server now costs a statement, and
  `Have` one: a snapshot went from 13.5 statements to 15.5 while its blob calls fell from 13.9 to 7.9. A statement on
  the server's own database is cheaper than a request to blob storage.

- **Touch on `Have`, not leases on uploads.** A lease per snapshot would need a protocol change; a touch needs none
  and bounds the race by the grace period, which is already the contract for uploads in flight.
- **The catalog is derived**: the bucket is the truth, `-reconcile` rebuilds it.
- **Packing small objects into larger blobs is not part of this**: it changes the storage layout and the direct-URL
  protocol, and the catalog removes most of the per-object round trips that motivated it. To be decided on harness
  numbers afterwards.

## Results

Load harness, medium scenario (100,000 files, 2,000 landings, 50 agents), before and after:

| | Before | After |
| --- | ---: | ---: |
| snapshot: blob calls / statements | 13.9 / 13.5 | 7.9 / 15.5 |
| workspace create: statements | 7 | 9 |
| landings per second | 17.8 | 16.4 |

Garbage collection (dry run) on that state takes 4.7 s with 14,170 blob reads and 109 statements; it no longer
lists the bucket. Its blob reads are now the file objects it opens to find their chunks and the operations of
workspaces - what the follow-up above is for.

### Follow-up: file objects read ahead

The marking walk opened every file object one after another to find its chunks. It now reads them 16 at a time
first. Behind a blob store whose reads each take 20 ms, a collection over 160 files took 3.6 s before and under 1.6 s
after (`GarbageCollectionLatencyTests`). On the load harness's local-disk store it makes no measurable difference -
0.42 ms per blob read without it, 0.45 ms with it, on states of 93,000 and 107,000 reads - since a local read does not
wait on a network. The full large scenario (1,000,000 files, 10,000 landings, 21,000 closed workspaces) collects in
29 s with 80,000 blob reads, against 37 s and 127,000 before the catalog.



- [x] Trait, memory and PostgreSQL catalog, decorator, contract tests
- [x] Collection through the catalog; the race tests (objects and trees); an upload meeting a sweep
- [x] `gc -reconcile`, docs; harness before and after

## Validation

`dotnet test`; `bassia-loadtest run -scenario large -agents 4` (garbage collection row) before and after.
