# Integration test: a monorepo on Azurite, imported from GitHub, with parallel agentic edits

## Outcome

One CI-run integration test proves the whole Azure path in one scenario. It starts a `bassia-server` whose objects live
in Azure Blob Storage (Azurite), imports `jk09/example` from GitHub through a client, and opens several clients with
workspaces in them. Agentic runs then edit the monorepo **in parallel**, each in a workspace of its own and as its own
`bassia` process. Disjoint edits land, a conflicting edit is refused as `needs-replay` and lands after
`agent replay`, and mainline, a fresh workspace and the blob container all show the result.

## Context

`main-client-server` already has the Azure backend: `AzureBlobObjectStore` (`-storage azure -azure-connection`), with
PostgreSQL for the mutable state (`-state postgres`). `AzureBackendTests` covers the store contract, SAS URLs and one
two-client landing on Azurite + PostgreSQL. The `Test` CI job starts Azurite and PostgreSQL and sets
`BASSIA_TEST_AZURE_BLOB` and `BASSIA_TEST_POSTGRES`. No test imports a real git repository on the Azure backend, and
none runs agents concurrently against it. The CLI tests drive `ProgramCli` in-process, through the process-wide current
directory and console, so they cannot run agents concurrently. This test starts the built `Bassia` apphost as child
processes instead.

`jk09/example` (`README.md`, `.gitignore`, `LICENSE`) is private, so CI's `GITHUB_TOKEN` cannot clone it.

## Acceptance criteria

- [x] `AzuriteIntegrationTests` (`Category=Azurite`) starts `bassia-server` in the test process with
      `-storage azure` on Azurite, in a fresh container. It uses PostgreSQL state when `BASSIA_TEST_POSTGRES` is set,
      otherwise SQLite.
- [x] A client imports the repository at `BASSIA_TEST_GIT_URL` (default `https://github.com/jk09/example.git`) with
      `bassia import -git`. Mainline is then at seq 1, and the imported tree holds every file of the source's default
      branch, byte for byte, under `example/`.
- [x] Three clients (`bassia clone` with a token) each start agentic runs as separate `bassia agent start -land -run`
      processes, all at once. Every agent waits at a barrier until all have started, which proves that the runs
      overlap. Each run works in its own workspace.
- [x] Each run that adds its own file lands, with distinct seqs and no gaps, one per run. Of two runs appending to
      `example/README.md`, exactly one lands and the other reports `needs-replay`. `agent replay -land` then lands it
      with the same change id.
- [x] `mainline log` lists every landing with its run. A new workspace in another client holds every agent's file and
      both README edits. The Azurite container holds the objects (blobs under `objects/`), and the server's scrub
      finds none corrupt.
- [x] Without `BASSIA_TEST_AZURE_BLOB` the test checks nothing, like the other cloud tests, unless
      `BASSIA_TEST_REQUIRE_AZURITE` is set: then it fails, so CI cannot pass it vacuously.
- [ ] CI runs it in the `Test` job (which has Azurite), and it is part of the `local-cloud` script's `-Filter cloud`.
      With the repository secret `BASSIA_EXAMPLE_REPO_TOKEN`, CI imports `jk09/example` from GitHub. Without the
      secret, CI imports a local stand-in with the same files and logs a warning saying so.

## Approach

- A test class next to `AzureBackendTests`, reusing its `Backends` helpers. The server runs in-process
  (`ServerApp`) and the clients are child processes of the built apphost, so the runs really overlap.
- Agent stand-in: a deterministic shell command (no model): a barrier on a shared folder, then the edit. On Windows the
  command runs under PowerShell.
- The expected import comes from a `git clone` of the same URL, so the test holds for GitHub and for the stand-in.
- CI: a step before the tests either sets git's auth header for `github.com` from the secret, or creates the stand-in
  and sets `BASSIA_TEST_GIT_URL`.

## Decisions

- Builds on `main-client-server`, where the Azure storage layer exists; the user chose this base. No new storage
  layer is needed.
- Azurite is the blob store; PostgreSQL is used when available (CI) but not required, so the test also runs against
  Azurite alone.
- The private-repository problem is solved with an optional secret and a stand-in fallback. Rejected: making the test
  network-only, which would fail every CI run without the secret. Making `jk09/example` public would also remove the
  need for a secret; that is the owner's call.

## Progress

- [x] Scenario verified by hand against Azurite with the built binaries (8 parallel runs, one replay).
- [x] Test written and passing against Azurite locally, importing from GitHub.
- [x] CI wiring and docs: an `Azurite integration test` step in the `Test` job (the rest of the suite runs with
      `Category!=Azurite`), `scripts/Use-ExampleRepository.ps1` in `Test` and `local-cloud`, and the test added to
      `-Filter cloud`.
- [x] Verified locally on Linux: Azurite with SQLite state and with PostgreSQL state (3 passing runs in a row), the
      local stand-in, the `BASSIA_TEST_REQUIRE_AZURITE` guard, and the full suite (204 passed).
- [ ] Verified in CI, including the Windows `local-cloud` job, where the agent stand-in runs under PowerShell. That
      path has not run yet; the branch has not been pushed.

## Validation

```sh
# Azurite on 127.0.0.1:10000, e.g. pwsh scripts/bassia-local-cloud.ps1 up
export BASSIA_TEST_AZURE_BLOB='DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=...;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;'
dotnet test --filter Category=Azurite --logger "console;verbosity=normal"
```
