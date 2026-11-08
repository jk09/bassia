# Schema versioning and migrations for the PostgreSQL stores

## Outcome

Every PostgreSQL schema Bassia keeps (a server's, a local monorepo's, a clone's tree cache) has a recorded version per
component, and a new build brings it forward with ordered, transactional steps, so a changed table (a new column, type
or collation) reaches existing databases instead of being skipped by `CREATE TABLE IF NOT EXISTS`. Server nodes that
start together migrate once; old and new builds run side by side through a rolling deploy as long as the steps between
them only add; a build refuses a database it cannot use instead of failing on queries later.

## Context

- Before this change `PostgresState` and `PostgresTreeStore` ran `CREATE ... IF NOT EXISTS` on every start, and
  `LocalPostgres` ran `CREATE SCHEMA IF NOT EXISTS`. Fresh databases, new tables and concurrent starts worked; any
  change to an existing table was never applied. `bassia_meta` held only `indexed_seq`.
- The version core reaches storage only through `IObjectStore`, `IViewStore`, `IPathIndex`, `ILeaseStore` (and
  `ITreeStore`) and may not reference a database driver, so the runner sits in the stores layer (`Bassia/Stores`).
- Several server nodes share one database (Azure Database for PostgreSQL flexible server, PostgreSQL 13+; Terraform
  provisions the server, not the schema). Development and tests use a local PostgreSQL (`BASSIA_TEST_POSTGRES`), one
  schema per test.
- One schema holds both `state` and `trees` on a server and in a local monorepo (same connection string); a clone's
  schema holds only `trees`, as a cache of the server's (`CachingTreeStore` writes the server first).
- Design and the rebuildability analysis: [DESIGN.md](DESIGN.md).

## Acceptance criteria

- [x] A fresh schema is created at the latest version of each component and recorded in `bassia_schema` and
      `bassia_schema_history`; opening it again runs one query, takes no lock and changes nothing.
- [x] Tables made before versioning that match migration 1 are stamped as version 1 without changing their data (heads,
      `indexed_seq`, the fencing sequence's position); a half-created pre-versioning schema is completed.
- [x] Pre-versioning tables that differ from migration 1 (a column's type, collation, nullability, a missing or extra
      column) are refused with a message naming each difference, and left unchanged.
- [x] Later steps run in order, each once, each in one transaction with its version bump; a failing step is rolled
      back and leaves the version where it was.
- [x] Concurrent starters on one schema (8 runners; 6 server nodes constructing both stores) apply each step exactly
      once and all succeed.
- [x] A database newer than the build opens when its `compatible_from` is at most the build's latest version (expand),
      and is refused in both modes otherwise (contract), without changes.
- [x] `-migrate verify` (and `bassia-server migrate -check`) changes nothing and refuses an unversioned or outdated
      schema; `bassia-server migrate` migrates without a data folder.
- [x] A clone's tree cache that the build cannot use (drifted, or newer and incompatible) is dropped and created again;
      the server's and a local monorepo's trees are never dropped.
- [x] A starter that cannot get the migration lock within its timeout fails with a clear message and changes nothing.
- [x] The path index (`bassia_paths`, `indexed_seq`) is shown to rebuild identically from the operation log and trees.
- [x] The existing test suite passes unchanged.

## Approach

`SchemaMigrator` (stores layer, Npgsql) runs a `SchemaComponent` - a name and steps numbered 1, 2, 3... - on a data
source: fast path (one query) when current; otherwise a session advisory lock `(0x62617373, hashtext(current_schema()))`,
re-read, baseline or migrate, unlock. Each `SchemaMigration` may set `MinimumCode` (contract steps) and
`Transactional = false` (for `CREATE INDEX CONCURRENTLY`). Migration 1 of each store is today's DDL verbatim. Baseline
detection runs migration 1 in the session's temporary schema inside a rolled-back transaction and compares columns
(`pg_attribute`: type, collation, nullability), so the expected shape is never written twice. The stores take a
`SchemaMode` (`Migrate`, `Verify`); the clone's tree store is opened as a cache (`DropSql` set). `bassia-server` gets
`-migrate auto|verify` and a `migrate [-check]` command.

## Decisions

- **Startup migration and an explicit command, one runner** (options a + b). Startup migration is the default because
  every node already opened the schema on start and the lock makes it safe; `bassia-server migrate` plus
  `-migrate verify` is for deployments that want the schema change as a separate, observable release step.
  Rejected: an off-the-shelf tool (option c, DbUp/FluentMigrator) - see DESIGN.md. *Provisional: see open question 1.*
- **A table of its own, `bassia_schema`, not a row in `bassia_meta`.** `bassia_meta` belongs to the `state` component's
  own schema (it would have to exist before the migration that creates it), is absent from a clone's schema, and
  holds a single bigint per key - no room for `compatible_from`.
- **Per-component versions** (`state`, `trees`), because a clone's schema has only `trees` and the two stores open
  independently.
- **Forward-only.** No down steps: a bad step is fixed by a later one; rollback of a deploy relies on expand/contract.
- **`compatible_from` for mixed versions.** Expand steps leave it; contract steps raise it. A build refuses a database
  only when `compatible_from` exceeds its latest version.
- **Drop-and-rebuild only for the clone's tree cache.** Server and local-monorepo trees are primary data (trees are not
  in blob storage); the path index is rebuildable but is migrated in place (rebuild costs a replay of all history).
- **No real step 2 ships with this change.** The example steps (expand, contract, failing, slow) live in the tests' own
  `widgets` component; adding an unneeded change to the production schema just to have an example was rejected.

### Open questions (defaults applied; confirm or change)

1. Should `bassia-server` migrate on start by default (`-migrate auto`), or should production require
   `bassia-server migrate` and default to `verify`? Default applied: `auto`.
2. Lock wait timeout: 5 minutes by default (`SchemaMigrator.DefaultLockTimeout`), not yet a server switch. Enough?
3. Should a clone's cache that is rebuilt be reported to the user (a line on stderr), or stay silent? Default: silent.

## Progress

- [x] Runner, stores wired, server switch and command.
- [x] Tests: 15 in `SchemaMigrationTests`; full suite green on PostgreSQL 16.
- [ ] Open questions answered; record moved to `feat/done/`; pull request with auto-merge.
- [ ] CI run (postgres:16 service container). Not run on PostgreSQL 13-15.

## Validation

```sh
export BASSIA_TEST_POSTGRES="Host=localhost;Username=postgres;Password=postgres;Database=bassia_test"
dotnet test Bassia.Tests --filter FullyQualifiedName~SchemaMigrationTests
dotnet test Bassia.Tests
```
