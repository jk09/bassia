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

- [x] A tree store trait (`ITreeStore`, `ITreeStoreMaintenance`) exists in `Bassia.Core.Storage` with a PostgreSQL
      implementation (`PostgresTreeStore`, used by `bassia-server` and by local monorepos and clones), an in-memory one and
      a blob-backed one for core tests and the old path; `Bassia.Core` still has no driver reference.
- [x] Object ids are unchanged: tree ids stay BLAKE3 of the canonical CBOR encoding, recomputed from the rows (scrub
      re-encodes and re-hashes), so git import/export and clones stay compatible.
- [x] Tree objects are no longer written to blob storage (a test asserts no stored blob is a tree); chunks, file objects,
      conflicts, commits, operations and views stay in blobs.
- [x] An explicit `bassia-server rebuild-trees -tree-store blob [-delete-blobs]` moves an existing server's trees into
      PostgreSQL; until it ran, the server refuses to start on the relational store (`-tree-store blob` keeps the old path).
- [x] A path lookup at depth *d* is one query (recursive CTE), a subtree walk is one query, and diff costs one batched
      read per differing level; a test shows zero blob reads for lookup, walk and diff on a relational store.
- [x] Garbage collection marks trees with one recursive query over all roots and sweeps unreachable tree rows.
- [x] Tree stores run the same contract tests (memory, blobs, PostgreSQL); the checkout test asserts a client uses at most
      3 `/trees/` requests for a 22-tree checkout. The tree algebra property tests (diff/apply, merge identities, pruning,
      edit clashes) run over both the in-memory and the PostgreSQL tree store with identical assertions.
- [x] `docs/reference.md` describes the tree store ("Trees").
- [x] Local monorepos and clones use a local PostgreSQL (schema per folder); SQLite and `SqliteState` are removed.

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
- Local monorepos: PostgreSQL replaces SQLite for all local state (heads, path index, leases, trees); PostgreSQL is
  assumed to be installed locally (`-postgres` / `BASSIA_POSTGRES` / `postgres.connection`). Earlier SQLite monorepos are
  refused, not migrated (as for the server).
- Clones keep the subtrees their work touched in a schema of the local PostgreSQL, filled by the server's `/trees/*`
  calls; they never query the server's database.
- Scrub re-encodes rows and re-hashes; GC deletes tree rows by reachability.
- No child-id index on `bassia_tree_entries` (reachability only walks forward): saves space on tens of millions of rows.

### Resolved questions

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
- [x] Open questions answered.
- [x] Implementation: tree store, PostgreSQL store, server `/trees/*`, GC/scrub/migration, local monorepos and clones on PostgreSQL.
- [x] Tests: full suite against a local PostgreSQL 16.
- [ ] CI: `scripts/Start-CiPostgres.ps1` starts a PostgreSQL in the Dev Drive and package jobs (and `bassia-local-cloud.ps1` exports `BASSIA_POSTGRES` for clones); written but not yet run on the hosted runners - confirm on the first CI run, then move this record to `feat/done/`.

## Validation

- `dotnet test` (unit, contract and property tests); with `BASSIA_TEST_POSTGRES` set for the PostgreSQL tests.
- Counting-store test asserting zero tree blob GETs for lookup / walk / diff / GC on the server stores.
- Manual: import a large git repository through `bassia-server` on Azurite + PostgreSQL (skill `bassia-cloud-server`)
  and compare landing and checkout timings before and after.
