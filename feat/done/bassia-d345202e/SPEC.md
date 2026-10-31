# Local cloud emulation: Azurite and PostgreSQL for testing Bassia's Azure backend

## Outcome

A `/bassia-local-cloud` skill (and the script behind it) sets up the cloud infrastructure of the Azure backend on the
developer's machine - Azurite standing in for Azure Blob Storage and a local PostgreSQL standing in for Azure Database
for PostgreSQL - runs the test suite against them, and runs an end-to-end smoke test: a `bassia-server` on Azurite and
PostgreSQL, clones that import git repositories, land a change, see each other's work, and export mainline back to git.

## Context

- The Azure tests (`AzureBackendTests.cs`) only run when `BASSIA_TEST_POSTGRES` and `BASSIA_TEST_AZURE_BLOB` are set;
  without them they pass without checking anything. CI starts PostgreSQL 16 and Azurite 3.37 itself
  (bassia-323f4b4f); locally there is no command for it.
- The existing skills (`/bassia-install`, `/bassia-use`, `/bassia-uninstall`) drive one PowerShell 7 script under
  `scripts/`. The maintainer works on Windows; CI and agents run on Linux.
- Docker is the common way to run both services, but is not always available (a cloud sandbox has no daemon; some
  Windows machines have no Docker Desktop). Azurite also runs from npm, PostgreSQL from its own binaries.

## Acceptance criteria

- [x] `scripts/bassia-local-cloud.ps1 check` reports what is available - Docker (with a running daemon), Node/npx,
      PostgreSQL server binaries, git, the .NET SDK - and which mode `up` would use; exits 1 when neither mode is
      possible, naming what to install.
- [x] `up` starts Azurite (blob service) and PostgreSQL 16, in Docker containers (`-Mode docker`) or as local
      processes (`-Mode native`: Azurite via `npx azurite@3.37.0`, PostgreSQL via `initdb`/`pg_ctl` in a per-user data
      folder), `auto` preferring Docker; waits until both accept connections; is idempotent (a second `up` reuses what
      runs); ports are configurable (default 10000 and 55432, so a PostgreSQL on 5432 is left alone).
- [x] `status` tells whether each service is up; `env` prints the connection strings as `BASSIA_TEST_*` and
      `BASSIA_SERVER_*` variables for pwsh, sh or cmd.
- [x] `test` runs `dotnet test` with the emulators' connection strings, and fails if the Azure and PostgreSQL tests
      did not actually run (e.g. the services are down).
- [x] `smoke` builds `bassia` and `bassia-server` (or uses `-Bassia`/`-Server` executables), starts the server on
      Azurite + PostgreSQL in a fresh container and database, imports two git repositories with history through a
      clone, lands a workspace change, checks a second clone sees it, exports mainline to git (and an incremental
      second export), checks the git history and file contents, and checks that the objects are in Azurite and the
      heads in PostgreSQL; it stops the server and cleans its temporary folder, and exits 1 on any failed check.
- [x] `down` stops both services; `down -Purge` also deletes their data (containers, volumes, data folders).
- [x] Running as root on Linux (agents' sandboxes) works for native PostgreSQL (it runs as the `postgres` user).
- [x] `.claude/skills/bassia-local-cloud/SKILL.md` maps the skill's arguments onto the script and tells the agent
      how to report results; `docs/reference.md` documents the script.

## Approach

One PowerShell 7 script, `scripts/bassia-local-cloud.ps1 <check|up|status|env|test|smoke|down>`, in the style of
`bassia-versions.ps1`. State (mode, ports, pids, data folders) is kept in `<root>/local-cloud/state.json`, where
`<root>` is the install root the version manager uses (`$BASSIA_HOME`, `%LOCALAPPDATA%\Bassia`,
`~/.local/share/bassia`). The smoke test runs the CLI through its TOML results (`Bassia.common.ps1`'s parser) and
reads PostgreSQL through `psql` (in the container, or next to the native binaries) and Azurite through the server's
`/api/v1/info` and its blob listing.

## Decisions

- Docker preferred, native fallback: no single dependency is required, and both are what developers already have.
- Default PostgreSQL port 55432, not 5432: a developer's own PostgreSQL keeps its port.
- The emulators' data lives in the per-user Bassia folder, not in the repository, so it survives clones and never shows
  in `git status`.
- Azurite 3.37.0 and PostgreSQL 16, the versions CI uses (native mode takes the installed PostgreSQL, 13 or later).
- Native Azurite is installed once into the state folder with npm (not `npx` on every start), so its pid is node's own
  and `down` can stop it; on Unix it is started through `sh` with `setsid`, inheriting no descriptor of the caller -
  started directly by PowerShell it was killed along with the caller's tool session.
- `Bassia.common.ps1` finds the Debug build on every platform and accepts a preset executable, so the smoke test parses
  the CLI's TOML results with the same code as the hand-check scripts.
- A `Local cloud` CI job runs the script itself: Docker mode on ubuntu-latest, native mode on windows-2025 (its runner
  ships PostgreSQL and Node.js), so both modes are verified where this sandbox cannot run Docker or Windows.

## Progress

- [x] Script and skill.
- [x] Validated natively on Linux (no Docker daemon here): check, up (fresh and again), status, env (sh, pwsh, cmd),
      smoke (10 checks; also with `-NoBuild -Bassia -Server`), test (157 tests; `-Filter cloud`: 8), down, down -Purge,
      and smoke/test refusing while the services are down.
- [x] CI `Local cloud` jobs pass: Docker mode on Linux, native mode on Windows (after fixes: pg_ctl output via files,
      Azurite created through WMI outside the caller's job object, Docker PostgreSQL readiness over TCP).

## Validation

- `pwsh scripts/bassia-local-cloud.ps1 check`, `up -Mode native`, `status`, `env`, `test`, `smoke`, `down -Purge`.
- `up` twice in a row; `smoke` with the services down fails with a clear message.
