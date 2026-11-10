# Object catalog: answer "have" from PostgreSQL and collect garbage safely

Status: proposed. Second scalability step; depends on `bassia-ff9140e9` (mainline index) for the roots of mainline.

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

- [ ] `Have` for any number of ids costs PostgreSQL statements in proportion to batches (one per 5,000 ids), and no
      blob-store call, for objects stored through the server.
- [ ] An object reported present by `Have`, or put again, is not swept for a grace period counted from that moment: a
      test interleaves a collection between a client's `Have` and its head move, and the workspace reads back whole.
      The same holds for trees.
- [ ] Garbage collection takes its candidates from the catalog (no bucket listing), mainline's roots from the mainline
      index (no walk of the log), deletes in batches, and reports what it did as today; a dry run changes nothing.
- [ ] The live set of a collection is held in PostgreSQL or streamed, not as one in-memory set of every id.
- [ ] A catalog missing or behind (an existing deployment, a direct upload that never completed, a restore) is brought
      in step by `bassia-server gc -reconcile`, which lists the bucket once; until then `Have` falls back to the blob
      store for ids the catalog does not know, so nothing is reported missing that exists.
- [ ] Objects uploaded directly on signed URLs are in the catalog once the client's `Have` or put-through sees them.
- [ ] `scrub` checks catalog against bucket for the sample it reads.
- [ ] Schema migration adds only; older nodes keep working through a rolling deploy.
- [ ] Harness: `Have` blob calls per snapshot go to 0; garbage collection's blob reads and time on the large state are
      recorded before and after.

## Approach

A trait `IObjectCatalog` (known, record, touch, unreferenced older than, remove) with PostgreSQL (`bassia_objects`:
id, size, written, touched) and in-memory implementations, and a decorator `CatalogedObjectStore` over the blob store:
`PutAsync` writes the blob then the row; `HasAsync` answers from the catalog, touching what it reports, and asks the
blob store only for unknown ids (recording those it finds). Collection marks into a temporary table (`live`), sweeps
`catalog EXCEPT live WHERE touched < now - grace` in batches, deleting blobs then rows. `bassia_trees` gets `touched`
the same way.

## Decisions

Taken unattended, as the user asked (the recommended options):

- **Touch on `Have`, not leases on uploads.** A lease per snapshot would need a protocol change; a touch needs none
  and bounds the race by the grace period, which is already the contract for uploads in flight.
- **The catalog is derived**: the bucket is the truth, `-reconcile` rebuilds it.
- **Packing small objects into larger blobs is not part of this**: it changes the storage layout and the direct-URL
  protocol, and the catalog removes most of the per-object round trips that motivated it. To be decided on harness
  numbers afterwards.

## Progress

- [ ] Trait, memory and PostgreSQL catalog, decorator, contract tests
- [ ] Collection on the catalog and the mainline index; the race test
- [ ] `-reconcile`, scrub, docs; harness before and after

## Validation

`dotnet test`; `bassia-loadtest run -scenario large -agents 4` (garbage collection row) before and after.
