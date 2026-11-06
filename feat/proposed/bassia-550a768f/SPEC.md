# Relational tree store

## Outcome

Walking a tree on the server (lookup, diff, merge, landing, garbage collection, checkout) no longer costs one blob
`GET` per tree level or per tree. The Merkle tree structure (tree objects and their entries) is also held in
PostgreSQL, so a walk is one or a few queries; blob storage keeps chunks, file objects and the canonical bytes of every
object.

## Context

- Trees are immutable, content-addressed objects in `IObjectStore` (`ObjectAccess.ReadTreeAsync` ->
  `store.ReadAsync(id)` -> `ObjectCodec.Decode`). Every walk (`Trees.GetAsync`, `WalkAsync`, `DiffAsync`,
  `GarbageCollector` mark phase, three-way merge) reads one tree object at a time. On S3/Azure each read is a
  network `GET` (tens of ms); a path lookup at depth *d* costs *d* serial GETs, a full walk costs one per directory.
- Mitigations exist but only soften it: the decoded-object cache in `ObjectAccess` (50k entries, per process),
  `CachingObjectStore` on clients, `IObjectBatchReader`/`PrefetchAsync` (breadth-first levels in `Trees.DiffAsync`).
  Server-side code (landing, reconciler, GC) has no such cache across requests and walks serially.
- `IPathIndex` already keeps mainline's *flattened* path versions in PostgreSQL (`bassia_paths`), which answers
  "path at seq" without touching trees. It does not serve arbitrary trees (workspace snapshots, commits, merge bases),
  nor diff/GC.
- Constraint: `Bassia.Core` may reach storage only through traits and must not reference a database driver. The
  four traits are fixed by the architecture; a tree store must be a trait (or an optional capability of
  `IObjectStore`) implemented in `Bassia` (PostgreSQL) with in-memory/SQLite/disk fallbacks.

## Acceptance criteria

- [ ] A tree store trait exists in `Bassia.Core.Storage` with a PostgreSQL implementation (used by `bassia-server` and
      by local monorepos) and an in-memory one for core tests; `Bassia.Core` still has no driver reference.
- [ ] Object ids are unchanged: tree ids stay BLAKE3 of the canonical CBOR encoding, recomputable from the rows
      (entries re-encoded canonically), so verification, git import/export and clones stay compatible.
- [ ] Tree objects are no longer written to blob storage; chunks, file objects, conflicts, commits, operations and
      views stay in blobs.
- [ ] An explicit rebuild command moves an existing store's tree blobs into the relational store and (optionally)
      deletes them; until it ran, the feature is off (flag) and the old blob path is used.
- [ ] Looking up a path at depth *d* in a stored tree costs O(1) queries (recursive CTE or per-level batch), not *d* blob
      GETs; a recursive walk of a subtree is one streamed query; a diff of two trees prunes equal subtrees in SQL or in
      per-level batches.
- [ ] Garbage collection's mark phase uses the relational structure for trees (reachability without reading every
      tree blob) and still deletes blobs correctly.
- [ ] Benchmark/test shows the number of blob GETs for lookup, walk, diff and GC drops to ~0 for trees on the server
      (counting store decorator), with identical results to the blob path (property test over the tree algebra).
- [ ] Store contract tests run over every implementation; PostgreSQL ones are skipped without `BASSIA_TEST_POSTGRES`.
- [ ] `docs/reference.md` Architecture section describes the tree store.

## Approach

Open design (to be settled via the questions below). Candidate schema:

```
bassia_trees   (id bytea PRIMARY KEY, total_size bigint, entry_count int)
bassia_entries (tree_id bytea, name text COLLATE "C", kind smallint, child_id bytea, size bigint, mode smallint,
                PRIMARY KEY (tree_id, name))
CREATE INDEX bassia_entries_child ON bassia_entries (child_id);  -- for reachability / reverse lookups
```

Reads: `ReadTreeAsync` asks the tree store first (one query returns the entries), falls back to the blob. Subtree
walks: `WITH RECURSIVE` over `bassia_entries` joined on `child_id`. Diff: recursive CTE on a pair of roots where
`old.child_id IS DISTINCT FROM new.child_id`. Writes: tree objects are written to blob storage as today and their rows
inserted in the same server request (`INSERT ... ON CONFLICT DO NOTHING`), since trees are immutable and idempotent.
Clients keep reading through the server API, adding a batched "read subtree to depth *n*" RPC so a client prefetches
whole levels in one round trip instead of the current breadth-first batches of ids.

Alternatives considered:

1. *Cache trees on the server only* (an in-memory/disk LRU of decoded trees, or a persistent local cache): no schema,
   cheap, but cold-start and multi-instance servers still pay N GETs; does not give SQL walks or GC reachability.
2. *Pack trees into one blob per commit* (a "tree pack" prefetched with a single GET): fewer GETs but breaks sharing of
   unchanged subtrees across commits (ingestion and storage cost grow with tree size).
3. *Rely on `IPathIndex`*: already exists for mainline at a seq; extend rather than duplicate for mainline-only reads,
   but it cannot serve non-mainline trees.

Risks: PostgreSQL growth (a monorepo with millions of files = tens of millions of entry rows; entries of *every* tree
version are retained, not only the current one); write amplification on landing (a change at depth *d* writes *d*
trees, each with all siblings re-listed); two sources of truth to keep consistent; GC must delete rows with blobs;
`bytea` ids vs the current hex `text` ids.

## Decisions

- Design: trees live **only** in PostgreSQL (no tree blobs). Rejected: relational mirror next to blobs, and a
  server-side cache only (can be reconsidered if the storage/consistency cost below proves too high).
- Scope: `bassia-server` **and local monorepos** use PostgreSQL for trees; no SQLite tree store.
- Migration: explicit rebuild only (no lazy backfill); a flag keeps the blob path until the rebuild ran.

### Open questions (to resolve before implementation)

1. Local monorepos: does "based on local PostgreSQL" mean PostgreSQL replaces SQLite for *all* local state (heads,
   path index, leases) and a local monorepo then needs a PostgreSQL instance (contradicting "tests run without a
   network"), or only trees move to PostgreSQL while SQLite stays for the rest?
2. Clients/clones: a clone must still get trees. Confirm that clones keep a *local* tree store (their own local
   PostgreSQL, or a small embedded cache) filled through a server RPC that returns whole subtrees, rather than
   querying the server's database directly.
3. GC scrub and `bassia export`/`import` currently treat every object as a blob (list, re-hash, delete). Confirm that
   trees are verified by re-encoding rows and re-hashing, and that GC deletes tree rows by reachability.

## Progress

- [x] Specification drafted from the request and the codebase.
- [ ] Open questions answered.
- [ ] Implementation.

## Validation

- `dotnet test` (unit, contract and property tests); with `BASSIA_TEST_POSTGRES` set for the PostgreSQL tests.
- Counting-store test asserting zero tree blob GETs for lookup / walk / diff / GC on the server stores.
- Manual: import a large git repository through `bassia-server` on Azurite + PostgreSQL (skill `bassia-cloud-server`)
  and compare landing and checkout timings before and after.
