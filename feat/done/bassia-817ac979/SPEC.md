# Bound the server's memory: batched replies by size, streamed uploads, an object cache that evicts

Status: done. Second scalability step.

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

- [x] `/objects/fetch` stops adding objects when the reply reaches a byte budget (default 32 MiB, `-fetch-budget`),
      always looks at the first group of ids, says how many ids it left unlooked at, and the client asks again for
      those instead of reading them one by one.
- [x] A request body over a limit (default 32 MiB, `-put-limit`) is refused with a 413 and a clear message before it
      is read; no client batch exceeds it (clients send 8 MiB batches).
- [x] The decoded-object cache is bounded by approximate bytes (default 256 MiB, `-object-cache`) and evicts least
      recently used entries; a hot object is not read twice because an unrelated burst filled the cache (a test).
- [x] One batched reply holds at most its budget plus one group of 64 small objects (a test on a server with a 1 MiB
      budget); see Decisions for why this replaced a measurement of the process's memory.
- [x] Protocol compatibility: a client that does not know the budget reads what a reply left out one by one and
      completes; the new client against a server that sends no count behaves as before.
- [x] Harness: requests, statements and blob calls of the medium scenario do not rise.

## Approach

Fetch in id order under a budget; reply frames as today. Client `ReadManyAsync` loops over what a reply left out and
falls back to single reads only for ids the server reports as large or missing. Kestrel's request-size limit per
endpoint for puts. `ObjectAccess` gets a size-aware LRU (approximate sizes from the encoded length) behind the same
methods.

## Decisions

Taken unattended, as the user asked (the recommended options):

- **The put limit defaults to 32 MiB, not the 64 first proposed**: Kestrel's own default was about 29 MiB, clients
  send 8 MiB batches, and a larger default would only raise what one request may hold.
- **The bound on a reply is tested, not the process's memory.** A managed-memory assertion in a test process that
  runs 380 other tests is noise; that one reply holds at most the budget plus one group is the property that bounds
  memory, and it is exact.
- **A refused body closes the connection.** The server answers before reading; a client that sent `Expect:
  100-continue` reads the 413 and its message, one that is already sending sees the connection drop.
- **The `/trees` calls keep their bounds by count** (5,000 trees; 20,000 for a subtree) for now: tree encodings are
  small, and wide directories are a separate question.

- **A byte budget, not a streamed reply**: the frame format and clients stay as they are.
- **Approximate sizes** (encoded length) are enough to bound the cache.

## Progress

- [x] Fetch budget and client loop
- [x] Put limits
- [x] LRU cache
- [x] Tests; harness: the medium scenario's requests, statements and blob calls are unchanged (100 landings at 17.8 per second, against 18.5 before; no operation differs by more than 15% except `sync`, which depends on timing)

## Validation

`dotnet test`; `bassia-loadtest run -scenario medium` before and after.
