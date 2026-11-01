---
name: bassia-local-cloud
description: Set up a local emulation of Bassia's Azure cloud backend - Azurite for Azure Blob Storage and PostgreSQL for Azure Database for PostgreSQL, in Docker or as local processes - and test Bassia against it - the cloud tests, and an end-to-end smoke test of bassia-server, clones, git import, landing and git export. Use when asked to set up, start, stop or reset the local cloud, Azurite or PostgreSQL for Bassia, to test or verify Bassia against Azure or PostgreSQL locally, to get the emulators' connection strings, or to smoke-test git export on the cloud backend. Accepts optional arguments - a step (`check`, `up`, `status`, `env`, `test`, `smoke`, `down`; default: up, then smoke and test) and the words `docker`, `native`, `purge`, `keep`, `installed`, `all-tests`.
---

# bassia-local-cloud

Drives `scripts/bassia-local-cloud.ps1`, which emulates the cloud infrastructure of Bassia's Azure backend on this
machine:

- **Azurite** (3.37.0) stands in for Azure Blob Storage, where objects live.
- **PostgreSQL** (16 in Docker, otherwise whichever version is installed) stands in for Azure Database for PostgreSQL,
  where heads, the path index and leases live.

Both listen on 127.0.0.1 only: Azurite's blob service on port 10000, PostgreSQL on 55432 (so a PostgreSQL already on
5432 is left alone). Their data persists between runs in `<root>/local-cloud`, where `<root>` is the install root that
`/bassia-install` uses.

Run the script with PowerShell 7 from the root of this checkout: `pwsh -NoProfile -File scripts/bassia-local-cloud.ps1 <step> ...`.
On Windows without `pwsh`, use `powershell -NoProfile -ExecutionPolicy Bypass -File ...`. That works only if Windows
PowerShell is 7+, so prefer installing PowerShell 7 (`winget install Microsoft.PowerShell`).

## Arguments

| Skill argument | Script |
| --- | --- |
| a step: `check`, `up`, `status`, `env`, `test`, `smoke`, `down` | that command; with no step, run `up`, then `smoke`, then `test -Filter cloud` |
| `docker` / `native` | `up -Mode docker` / `up -Mode native` (the default `auto` prefers Docker when its daemon answers) |
| `purge` | `down -Purge`: delete the containers and volumes, or the data folders |
| `keep` | `smoke -Keep`: keep the smoke test's folder and database for inspection |
| `installed` | `smoke -Bassia <root>/current/bassia -Server <bassia-server>`: test an installed build instead of building this checkout |
| `all-tests` | `test` with no filter: the whole suite with the cloud tests enabled (instead of `-Filter cloud`) |

Other switches: `-BlobPort`, `-PostgresPort` (on `up`), `-Shell pwsh|sh|cmd` (on `env`), `-Filter <expr>` (on `test`), `-NoBuild` (on `smoke`).

## Steps

1. **Check.** Run `check`. It lists Docker (and whether its daemon answers), Node.js + npm, the PostgreSQL server
   binaries, git and the .NET SDK, and the mode `up` would pick. If it exits 1, neither mode is possible. Tell the user
   what to install, using the commands the script prints: start Docker Desktop, or install Node.js and PostgreSQL. Do
   not install anything without the user's agreement.
2. **Up.** Run `up` (with `-Mode` if the user asked). It works in either mode:
   - **Docker:** creates or starts the containers `bassia-azurite` and `bassia-postgres`, with named volumes.
   - **Native:** installs Azurite once into the state folder with npm and starts it detached. It runs `initdb` once and
     starts PostgreSQL with `pg_ctl`; as root on Linux it runs PostgreSQL as the `postgres` user.

   It waits until both services answer, then prints their connection strings. It is idempotent. If a port is taken,
   or the emulators were set up in the other mode, it says so; pass the hint on to the user.
3. **Smoke.** Run `smoke`. It builds `bassia` and `bassia-server` (Debug), then starts the server on Azurite +
   PostgreSQL in a fresh container and database. Two clones then import two small git repositories with history, land
   a change, see each other's landings and export mainline to git twice (the second time incrementally). Finally it
   checks what PostgreSQL holds, and re-hashes every object in Azurite with `bassia-server scrub`. Each check prints
   `ok` or `FAIL`. On a failure the folder and database are kept: report the failing check and the last lines of the
   server log the script names.
4. **Test.** Run `test -Filter cloud` (or `test` for `all-tests`). It runs `dotnet test` with `BASSIA_TEST_POSTGRES`
   and `BASSIA_TEST_AZURE_BLOB` set, and refuses to run while a service is down. Without those variables, the cloud
   tests pass without checking anything. Report the pass/fail counts and any failing test with its message.
5. **Report** the mode, the ports, each smoke check and the test counts. Show `env -Shell <the user's shell>` if the
   user wants to run the tests or a server against the emulators themselves (`BASSIA_SERVER_*` configures
   `bassia-server -storage azure -state postgres`).

Leave the emulators running unless the user asked to stop them: `down` stops them and keeps their data; `down -Purge`
also deletes it. The npm package cache in the state folder is kept either way.
