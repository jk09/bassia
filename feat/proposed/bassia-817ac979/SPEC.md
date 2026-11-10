# Bound the server's memory: batched replies by size, streamed uploads, an object cache that evicts

Status: proposed. Second scalability step.

## Outcome

No single request can make `bassia-server` hold more than a configured amount of object data in memory, and its cache
of decoded objects stays within a size limit by evicting what was used least recently instead of dropping everything.

## Context

- `POST /objects/fetch` reads up to 5,000 objects of under 256 KiB each into memory and builds one reply: about
  1.3 GB at worst, per request. Each object costs a stat and a read.
- `POST /objects/put`, `POST /trees/put` and `PUT /objects/{id}` read the whole body into a `MemoryStream`.
- `ObjectAccess` keeps decoded objects in a dictionary and clears all of it when it passes 50,000 entries, whatever
  their size: a tree with 10,000 entries counts as one, and after a clear every request reads again.
- Clients already handle a batched reply that leaves ids out (`ReadManyAsync`: "ids it leaves out are read one by one").

## Acceptance criteria

- [ ] `/objects/fetch` stops adding objects when the reply reaches a byte budget (default 32 MiB, `-fetch-budget`),
      always returns at least one requested object that exists and fits, and the client asks again for small objects
      left out instead of reading them one by one.
- [ ] `/objects/put` and `/trees/put` refuse bodies over a limit (default 64 MiB, a 413 with a clear message) and
      clients split batches below it; no client batch exceeds it today (a test).
- [ ] The decoded-object cache is bounded by approximate bytes (default 256 MiB, `-object-cache`) and evicts least
      recently used entries; a hot object is never read twice because an unrelated burst filled the cache (a test).
- [ ] A test drives 16 concurrent maximal fetches and asserts the process's managed memory stays under a bound.
- [ ] Protocol compatibility: an older client against the new server and the new client against an older server both
      complete a checkout and a snapshot.
- [ ] Harness: requests and blob calls of snapshot and workspace list do not rise.

## Approach

Fetch in id order under a budget; reply frames as today. Client `ReadManyAsync` loops over what a reply left out and
falls back to single reads only for ids the server reports as large or missing. Kestrel's request-size limit per
endpoint for puts. `ObjectAccess` gets a size-aware LRU (approximate sizes from the encoded length) behind the same
methods.

## Decisions

Taken unattended, as the user asked (the recommended options):

- **A byte budget, not a streamed reply**: the frame format and clients stay as they are.
- **Approximate sizes** (encoded length) are enough to bound the cache.

## Progress

- [ ] Fetch budget and client loop
- [ ] Put limits
- [ ] LRU cache
- [ ] Memory test; harness numbers

## Validation

`dotnet test`; `bassia-loadtest run -scenario medium` before and after.
