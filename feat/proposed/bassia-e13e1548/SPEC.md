# Shared-state contracts: every piece of monorepo state behind a backend

Phase 1, part 1 of 4 of hosting Bassia as a scalable service: this record, then `bassia-ce722c6c` (native S3 and
DynamoDB storage for histories) and `bassia-f245cd5f` (Bassia server and remote backend), deployed by `bassia-b9581afe`
(AWS). Target of the first deployment: one organization, ~1,000 users, ~500 concurrent agents.

## Outcome

Everything a `bassia` process reads or writes about the monorepo goes through a contract with an interchangeable
backend: component histories (already), the registry and configuration, run and integration records, the lock table,
live jobs, the task cache, and the history views of `log`, `agent diff`, `component survey` and the dashboard. The
local backend keeps today's on-disk layout, so nothing changes for users; a remote backend (`bassia-f245cd5f`) becomes a
matter of implementing the contracts. Identity becomes a contract too, so a server can vouch for who acts.

## Context

`bassia-0cd6f969` put component histories, the registry (`IMetaStore`) and records (`IRecordStore`,
`IRunRecordStore`) behind `Bassia/Storage`, and `bassia-f66364b5` made a clone a cache of a depot. What is still local
to one machine, and so cannot be shared by many users and agents:

- **Locks**: `LockTable` keeps one TOML file per run in `<root>/.locks`, changed under a file mutex by reading every entry,
  deciding (`LockSession`: holds, waits, deadlock cycles, conflicts, catch-ups) and writing what changed. Liveness is a
  heartbeat within `lock.ttl` plus, on the same machine, the run's process id and start time.
- **Jobs**: `JobRegistry` keeps `<workspace>/.jobs/<id>.toml|.log|.result.toml`; liveness and stopping are by process
  id (`IsProcessAlive`, `Kill`, `RequestStop` through a stop file); detached runs are `DetachedProcess`es.
- **Records are wired by hand**: commands construct `RunMetadataStore(new GitClient(root), RunsRepoDir)` and
  `IntegrationStore` themselves (RunCommands, LockCommands, LogCommand, SplitCommands, TaskCommands,
  MonorepoCommands), rather than asking the monorepo for its record stores.
- **Registry and configuration** are read from files: `Monorepo.Load` calls `GitMetaStore.Read*` statics and
  `ConfigSnapshot.Load(root)`; `ConfigFile.Set` edits files; `InitCommand`, `DepotCommands` and `TagCommands` use git
  directly.
- **History bypasses the contract**: `LogCommand`, `agent diff`, `component survey`, `Web/*` (Dashboard, Timeline,
  WebData) read the component repositories with `GitClient`.
- **Task cache**: `TaskCache` is a folder under the workspace.
- **Identity**: `UserCommands.ResolveAsync` trusts `BASSIA_USER`, `user.id` or the git email; `Actor` carries it.

## Acceptance criteria

- [ ] `ILockStore` replaces direct use of the `.locks` folder: it runs one lock decision atomically over the entries it
      reads (the `LockSession` semantics: holds, waits, cycles, conflicts, catch-ups, release, forget) and judges
      liveness by lease (heartbeat + TTL) alone; process-id checks become an optimization of the local backend only.
      `FileLockStore` is today's behavior; `MemoryLockStore` serves the unit tests.
- [ ] `IJobScheduler` replaces `JobRegistry` for callers: register, find, list, read the log (from an offset, for
      following), request a stop, record the result, and start a detached job. Stopping goes through the scheduler,
      never through a process id held by the caller. `LocalJobScheduler` is today's behavior (background process, stop
      file, log file).
- [ ] `ITaskCache` replaces direct use of the cache folder (look up, restore, store by key); the local backend is today's.
- [ ] `IIdentity` answers who the current user is and how that is known; the local backend is today's resolution
      (`BASSIA_USER`, `user.id`, git email). Attribution (`Actor`) takes the user from it.
- [ ] A `MonorepoBackend` (or equivalent) is the one place a command gets its storage, meta store, record stores, lock
      store, scheduler, task cache, identity and configuration from; `Monorepo.Load` builds the local one. No command or
      dashboard page constructs `RunMetadataStore`, `IntegrationStore`, `LockTable`, `JobRegistry` or `TaskCache`, or
      reads `components.toml`/`config*.toml`, itself.
- [ ] `log`, `agent diff`, `component survey` and the dashboard's history views read through `IComponentStore` (adding
      the queries they need to the contract, e.g. history between two commits with changed paths), so they also work
      on a clone and on a remote backend. Commands that only make sense on git (`init`'s meta-repo, the meta-repo clone
      of `bassia clone`) are confined to the local backend.
- [ ] Configuration is read and written through the backend (`IConfigStore`): the monorepo layer with the meta store,
      the user layer stays a local file.
- [ ] No behavior change: the whole existing suite passes unchanged, apart from tests that constructed the moved
      types; the unit tests of locks, jobs and runs also run against the memory backends.
- [ ] `docs/reference.md` ("Architecture") describes the contracts and which state each holds.

## Approach

Introduce the contracts in `Bassia/Storage` next to the existing ones, with local implementations that wrap today's
code (`LockTable` becomes `FileLockStore`, `JobRegistry` becomes `LocalJobScheduler`, ...), then move callers over
command by command, keeping each step green. The lock decision stays in Core (`LockSession` logic) and the store only
provides "read these entries, apply this decision atomically": a file mutex locally, a transaction or conditional writes
remotely. Add the history queries the commands need to `IComponentStore` and implement them for git and memory before
switching `log`, `agent diff`, `survey` and the dashboard.

## Decisions

- One record per deliverable: the contracts ship on their own (no behavior change) and unblock the cloud storage
  (`bassia-ce722c6c`) and the server (`bassia-f245cd5f`).
- The contract test suites written here (one set of tests per contract, run against every backend) are what the cloud
  and remote backends are checked with.
- Lock liveness is a lease. Process ids cannot be checked across machines, so the contract never depends on them.
- The user configuration layer stays local to each client; only the monorepo layer is shared.

## Progress

- [ ] Contracts and local/memory implementations
- [ ] Callers moved (commands, dashboard, integration, locks)
- [ ] History queries in the storage contract; `log`, `agent diff`, `survey`, dashboard moved
- [ ] Docs; full suite green

## Validation

```sh
dotnet build
dotnet test --filter "Category!=EndToEnd"
grep -rn "new RunMetadataStore\|new LockTable\|new JobRegistry\|new TaskCache" Bassia/CliCommands Bassia/Web   # none
```
