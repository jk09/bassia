# bassia-server keeps its state in PostgreSQL only

## Outcome

`bassia-server` keeps heads, the path index and leases in PostgreSQL only: SQLite is removed as a server state
backend, and `-postgres <connection string>` is required to serve, collect garbage or scrub. Local monorepos
(`bassia init`) keep SQLite as their embedded, zero-install store. Tests that start a server run against PostgreSQL
and are reported as skipped without one.

## Context

The server's state had two backends: SQLite (`-state sqlite`, the default, `<data>/state.db`, one node) and
PostgreSQL (`-state postgres`, any number of nodes). Every real deployment (Terraform on Azure, the local cloud and
cloud-server skills) already uses PostgreSQL, and SQLite cannot serve more than one node. Keeping both doubles the
server's state surface for no deployment that needs it. SQLite stays where it fits: a local monorepo, where the CLI
itself is the only writer and nothing may need installing.

## Acceptance criteria

- [x] `bassia-server` (serve, `gc`, `scrub`) uses `PostgresState`; without `-postgres` (or `BASSIA_SERVER_POSTGRES`)
      it exits with code 2 and a message naming the switch.
- [x] `-state postgres` (`BASSIA_SERVER_STATE=postgres`) is still accepted, so existing deployments and scripts keep
      working; `-state sqlite` or any other value fails with a message that SQLite was removed from the server.
- [x] A data folder holding a `state.db` from an earlier server refuses to start, with a message saying the SQLite
      state is not migrated and the file must be moved away; no data is silently ignored.
- [x] `bassia init` local monorepos still use SQLite (`.bassia/state.db`); their tests run without a database.
- [x] Tests that start a `bassia-server` (`ServerTests`, `StudioTests`, `StudioAuthTests`, `StudioRunTests`, and the
      server cases of `AzureBackendTests` and the Azurite integration tests) run on a fresh PostgreSQL schema from
      `BASSIA_TEST_POSTGRES` and are reported as skipped when it is not set.
- [x] The Docker smoke test runs the server image against a PostgreSQL container.
- [x] Docs (`docs/reference.md`, `README.md`, `CLAUDE.md`, usage text) describe PostgreSQL as the server's only state
      backend and SQLite as the local monorepo's.
- [x] The whole suite passes with `BASSIA_TEST_POSTGRES` set.

## Approach

- `ServerOptions`: drop the `State` default of `sqlite`; validate `-state` at parse time (only `postgres`) and require
  `-postgres`. `ServerApp` always builds `PostgresState`, after checking the data folder for a leftover `state.db`.
  The data folder keeps the signing key and the local-disk blob store.
- `SqliteState` stays in `Bassia/Stores` for the local monorepo; its comment no longer mentions a single-node server.
- Tests: `PostgresFact` / `PostgresTheory` attributes set `Skip` when `BASSIA_TEST_POSTGRES` is missing (xUnit 2 has
  no runtime skip); server fixtures pass `Backends.FreshPostgres()`, a new schema per test.
- `deploy/docker/smoke.sh` starts `postgres:16` and passes its connection string to the server container.

## Decisions

- **Scope: server only** (user). Local monorepos keep SQLite: requiring a database server for `bassia init` would
  remove the zero-install, offline mode.
- **Remove now, no migration** (user). Existing `state.db` files are not converted. Starting on a data folder that
  still holds one is refused rather than silently starting from an empty mainline.
- **Server tests require PostgreSQL and are skipped without it** (user). CI's Test job already provides it.
- **Keep accepting `-state postgres`**: Terraform sets `BASSIA_SERVER_STATE=postgres`, and the scripts pass
  `-state postgres`; rejecting it would break them for no gain.

## Progress

- [x] Server: PostgreSQL only, `-state` validation, leftover `state.db` refusal, usage text.
- [x] Tests on PostgreSQL with skip attributes; new tests for the option errors.
- [x] Docker smoke test (no Docker daemon in the authoring session: verified by CI's image smoke test), docs.
- [x] Full suite with PostgreSQL.

## Validation

- `BASSIA_TEST_POSTGRES=... dotnet test Bassia.Tests` - the whole suite, server tests on PostgreSQL.
- `dotnet test Bassia.Tests` without it - server tests reported as skipped, the rest pass.
- CI: Test, Local cloud and the image smoke test (`deploy/docker/smoke.sh`).
