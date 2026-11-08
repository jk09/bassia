# Design: schema versioning and migrations for Bassia's PostgreSQL state

Spec: [SPEC.md](SPEC.md). Code: `Bassia/Stores/SchemaMigrator.cs`; tests: `Bassia.Tests/SchemaMigrationTests.cs`.

## 1. Options

| | (a) Version row + steps at startup | (b) Explicit `bassia-server migrate` | (c) DbUp / FluentMigrator |
| --- | --- | --- | --- |
| Works for local monorepos and clones (no deploy step) | yes | no: every CLI user would have to run it | yes, but a new dependency in the CLI |
| Concurrency of N starters | needs a lock (every node migrates) | one runner, but nodes must still check | not verified here whether either ships a cross-node lock for PostgreSQL; Bassia would wrap its own advisory lock around it anyway, to share the fast path and the cache rule |
| Rolling deploys | needs a compatibility rule | same | neither tool has one: both record "applied scripts", not "which builds can read this" |
| Baselining existing `IF NOT EXISTS` tables | custom | custom | custom (both need a hand-made journal row) |
| Per-schema versioning (one schema per local monorepo, per test) | natural (`search_path`) | natural | DbUp: journal table per schema, workable; FluentMigrator: VersionInfo per schema, workable |
| Core free of database drivers | yes (stores layer) | yes | yes as long as it sits in the stores layer: the rule is not the obstacle |
| Size | ~400 lines, no dependency | the same runner | DbUp is small; FluentMigrator brings a runner and a fluent DSL Bassia does not need (it writes SQL directly) |

**Recommendation: one runner in the stores layer, used both ways** - (a) by default, (b) as an option.

- Local monorepos and clones have no deploy step: only (a) reaches them, so the runner must exist for (a) anyway.
- A server node already opened the schema on start; with an advisory lock and a fast path that costs one query,
  migrating there is safe and needs no extra deployment machinery.
- (b) is the same runner behind `bassia-server migrate` for operators who want the change as its own release step
  (a pipeline job before the new revision), with nodes started as `-migrate verify` so no node ever changes the schema.
- (c) is rejected not because of the driver rule (the runner is in the stores layer either way) but because the hard
  parts - the lock, baselining, the compatibility floor, the cache rebuild - are Bassia-specific and would still be
  written by hand around the tool. What remains (run SQL strings in order in a transaction and record them) is ~50
  lines, not worth a dependency.

## 2. Concurrency and mixed versions

- **Fast path.** `SELECT ... FROM bassia_schema WHERE component = ...`: current (or newer and compatible) returns at once,
  no lock. This is what every server start and every CLI command pays.
- **Lock.** Otherwise `pg_try_advisory_lock(0x62617373, hashtext(current_schema()))`, polled every 100 ms up to a timeout
  (default 5 min), session-level so it spans several step transactions and `CREATE INDEX CONCURRENTLY`. The key includes
  the schema, so local monorepos and test schemas sharing one database do not wait on each other; the two-int key
  space cannot collide with the leases' single-bigint `pg_advisory_xact_lock`. Released explicitly; a dropped connection
  or Npgsql's pool reset (`DISCARD ALL`) releases it too.
- **Re-read under the lock**: a starter that waited finds the work done and returns `Current`.
- **Steps**: each in one transaction with the version bump (PostgreSQL DDL is transactional), so a crash or failure
  leaves the schema at the previous version. Non-transactional steps must be idempotent.
- **Newer than the code.** Each component stores `version` and `compatible_from`. A build whose latest version is L opens
  a schema at version V when `V == L`, or `V > L` and `compatible_from <= L`; otherwise it refuses (`SchemaException`,
  "Upgrade Bassia"), in both modes, changing nothing.
- **Expand/contract.** A change that would break the previous build is split across releases:
  1. *expand* (release N+1): add the new table/column (nullable or with a default), write both shapes; `compatible_from`
     unchanged, so build N keeps running during the roll;
  2. backfill (a later step or a background job);
  3. *contract* (release N+2, once no node runs N): remove the old shape, with `MinimumCode = N+1`, so a stray build N
     refuses to start instead of failing on queries.
  Rules for an expand step: no `DROP`, no `RENAME`, no type or collation change of a column in use, no `NOT NULL`
  without a default, no table rewrite of a large table inside the start path (use `Transactional = false` +
  `CREATE INDEX CONCURRENTLY IF NOT EXISTS` for indexes on `bassia_paths` and `bassia_tree_entries`). A collation or
  type change is always expand (new column) + backfill + contract.

## 3. The three stores

| Store | Schema | Component | Policy |
| --- | --- | --- | --- |
| `PostgresState` (server, local monorepo) | server DB / monorepo schema | `state` | migrate in place; never drop |
| `PostgresTreeStore` (server, local monorepo) | same schema as `state` | `trees` | migrate in place; never drop |
| `PostgresTreeStore` in a clone | clone's own schema | `trees` (`CacheSchema`) | migrate; **drop and rebuild** when drifted or newer-incompatible |
| `LocalPostgres` | `CREATE SCHEMA IF NOT EXISTS` | - | unchanged: a schema has no shape to migrate |

Correction to the premise: the `LocalPostgres` schema is a cache only for a **clone**. A **local monorepo** (`bassia
init`) keeps its heads, path index, leases and trees in that schema as primary data (`Monorepo.cs`, local branch), so
it is migrated exactly like a server's and never dropped. Only `Monorepo.cs`'s clone branch opens the store with `cache: true`.

## 4. Rebuildability (checked in the code)

- **Trees: not rebuildable.** `ObjectAccess` splits fresh objects into blobs and trees and sends trees only to the tree
  store (`freshTrees` -> `WriteTreesAsync` -> `ITreeStore.PutManyAsync`; `freshBlobs` -> the object store); `ServerApp`
  documents "trees live in PostgreSQL, not in blob storage"; `docs/reference.md` says the same. Commits in blob storage
  name tree ids whose contents exist nowhere else. So `bassia_trees`/`bassia_tree_entries` are primary data on a server
  and in a local monorepo: every migration must preserve them. (`written` also drives the GC grace period.)
- **Clone tree cache: rebuildable.** `CachingTreeStore.PutManyAsync` writes the server first, then the local store; reads
  fill the local store from the server. Losing it costs refetches only.
- **Path index: rebuildable, from the operation log (blobs) and the trees.** `IPathIndex` documents it; the code agrees:
  `Repository.EnsureIndexedAsync` replays from `indexed_seq` by walking operations and diffing consecutive mainline
  trees, and `ApplyAsync` is idempotent per seq. Verified by `PathIndex_IsRebuiltFromTheOperationLogAndTrees`
  (truncate, reset `indexed_seq`, re-index, identical rows). Caveats: it depends on the trees; it replays all history
  (cost grows with mainline length); and callers not yet audited might read `bassia_paths` between truncate and catch-up
  (landing and the reconciler call `EnsureIndexedAsync` first; the RPC read paths in `ServerApp` were not audited).
  So rebuild is a valid strategy for a path-index change, but run as a maintenance step, not inside the start lock.
- **Heads**: primary. **Fencing sequence**: must never move backward - no step may recreate it. **Leases and lease
  queue**: short-lived, but live leases guard running work; drop only with every node stopped.

## 5. Baselining

When `bassia_schema` has no row for a component:

1. Find which of migration 1's tables exist in the current schema. None: create from step 1 (`Created`).
2. Otherwise compute migration 1's shape by running it in `pg_temp` inside a transaction that is rolled back, and
   compare columns (type via `format_type`, non-default collation, nullability, missing/extra columns).
3. Match: run migration 1 (idempotent `IF NOT EXISTS`, so it changes no existing object and only adds a missing index
   or sequence) and stamp version 1 with history description `baseline (...)` (`Baselined`).
4. Mismatch: refuse, listing every difference, change nothing - or, for the clone's cache, drop and rebuild.

The shapes have not changed since the tables were introduced (git history of `PostgresState.cs`/`PostgresTreeStore.cs`),
so existing databases are expected to baseline cleanly. Not compared: primary keys, indexes, defaults.

## 6. Tests (`SchemaMigrationTests`, each on a fresh schema of `BASSIA_TEST_POSTGRES`)

Fresh schema; baseline of the exact pre-versioning DDL with data kept and the sequence's position kept; a half-created
pre-versioning schema; drift refused; expand then contract with `compatible_from`; failing step rolled back; step
numbering; 8 concurrent runners over a slow non-idempotent step; 6 concurrent server nodes; lock timeout; newer database
(compatible and not, both modes); verify mode; `bassia-server migrate [-check]`; the clone cache rebuilt; path index
rebuilt.

## 7. Portability (PostgreSQL 13-16)

Used: `to_regclass`, `pg_try_advisory_lock(int, int)`, `hashtext` (undocumented but present and stable in every
version; all nodes hit the same server, so its value only has to agree within one server), `pg_my_temp_schema`,
`pg_attribute`/`pg_class`/`pg_collation`, `format_type`, transactional DDL. Temporary tables must be allowed (Azure
flexible server allows them by default). Tested on PostgreSQL 16 locally and in CI; not run on 13.

## Adding a step

```csharp
public static readonly SchemaComponent Schema = new("state",
[
	new SchemaMigration(1, "...", """ ...the original DDL, never edited... """),
	// Expand: older builds ignore the column; no MinimumCode.
	new SchemaMigration(2, "lease note", "ALTER TABLE bassia_leases ADD COLUMN note text"),
	// Index on a big table, outside a transaction, idempotent.
	new SchemaMigration(3, "paths by kind", "CREATE INDEX CONCURRENTLY IF NOT EXISTS bassia_paths_kind ON bassia_paths (kind)") { Transactional = false },
]);
```
