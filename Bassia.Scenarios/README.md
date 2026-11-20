# Bassia scenario tests

`Bassia.Scenarios` exercises Bassia the way people and agents use it: every test drives the `bassia` command line
(arguments in, the TOML result and exit code out, files edited in workspace folders) against a `bassia-server` whose
**blob storage is an in-memory mock** (`-storage memory`) and whose **state and trees live in a real PostgreSQL**.
Tests are grouped by user scenario, and each one writes a transcript of every command and its result, so a run reads as
a record of what Bassia did.

The scenarios rely only on the documented command-line contract (`docs/reference.md`: commands, switches, result keys
and outcomes), never on Bassia's internal types, so they keep running unchanged as Bassia evolves. Only the harness
(`Harness/`) touches Bassia's code, to host the CLI and the server in the test process.

## Running

Requirements: the .NET 10 SDK, git, and PostgreSQL - either Docker (the harness then starts a throw-away
`postgres:16` container by itself) or a connection string in `BASSIA_TEST_POSTGRES`.

```sh
dotnet test Bassia.Scenarios                                   # every scenario
dotnet test Bassia.Scenarios --filter Scenario=Merge           # one scenario
dotnet test Bassia.Scenarios --filter "Scenario=ImportRepo|Scenario=GitExport"
dotnet test Bassia.Scenarios --filter "FullyQualifiedName~ReplayedOnTheNewMainline"   # one test
dotnet test Bassia.Scenarios --logger "console;verbosity=detailed"                    # show the transcripts
```

A persistent PostgreSQL is handy while debugging (inspect the schemas a test left with `BASSIA_SCENARIO_KEEP=1`):

```sh
docker run -d --name bassia-postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:16
export BASSIA_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres;Database=postgres"
# PowerShell: $env:BASSIA_TEST_POSTGRES = "Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres;Database=postgres"
```

## Debugging

By default the CLI and the server run **in the test process**: `bassia ...` calls the same entry point as the
executable (`ProgramCli.RunAsync`) and the server is the same `ServerApp` that `bassia-server` starts, from its command
line. Debug a test from Visual Studio, Rider or VS Code's test explorer and breakpoints anywhere in `Bassia`,
`Bassia.Core` or `Bassia.Server` are hit. Detached agent runs and lazy-folder servers are separate `bassia` processes
(attach to them by process name), and they reach the in-memory blob store over HTTP like any client.

Each test's transcript is in its output (the test explorer's output pane, or `--logger "console;verbosity=detailed"`):

```
[alice] ./alice$ bassia workspace check second   -> exit 0
# bassia result
ok = true
command = "workspace check"
outcome = "needs-replay"
...
```

## Environment variables

| Variable | Effect |
| --- | --- |
| `BASSIA_TEST_POSTGRES` | PostgreSQL connection string to use instead of a container. Each test gets fresh schemas, dropped afterwards. |
| `BASSIA_SCENARIO_DOCKER=0` | Never start a container: without `BASSIA_TEST_POSTGRES` the scenarios are skipped. |
| `BASSIA_SCENARIO_KEEP=1` | Keep each test's folders (under `<temp>/bassia-scenarios/`) and PostgreSQL schemas for a look afterwards. |
| `BASSIA_SCENARIO_TRANSCRIPTS=<dir>` | Also write every test's transcript there, as `<Class>.<Test>.md`. |
| `BASSIA_SCENARIO_CLI=<path>` | Run an installed `bassia` executable as a process instead of the in-process CLI: evaluate a release with the same tests. |
| `BASSIA_SCENARIO_SERVER=<path>` | Run an installed `bassia-server` as a process (still `-storage memory`) instead of the in-process server. |
| `BASSIA_SCENARIO_REPOS=<url>,...` | The git repositories the GitHub import scenarios use (default: `octocat/Hello-World`, `octocat/Spoon-Knife`, `sindresorhus/slugify`; the last one is imported with history). |
| `BASSIA_SCENARIO_OFFLINE=1` | Skip the scenarios that need GitHub. |
| `BASSIA_SCENARIO_CLAUDE=1` | Also run the agent scenarios with the real Claude Code (`claude` on PATH, signed in). Costs tokens and is not deterministic. |

Scenarios that need something else say so when skipped: git, the network, FUSE 3 (`fusermount3`, Linux) or ProjFS
(Windows) for lazy folders, weave's `weave-driver` on PATH for the semantic merge (`pwsh packaging/Get-Weave.ps1 -Rid
<rid> -Destination <dir>`), a Unix file system for executable bits and case-sensitive names.

## Scenarios

| Scenario (trait) | What a user does | File |
| --- | --- | --- |
| `MonorepoSetup` | Clone a server, `status`, `version`, `help`, configuration, the result contract (TOML, exit codes 0/1/2, `-human`), a local monorepo (`init`) | `MonorepoSetupScenarios.cs` |
| `ImportRepo` | Import git repositories: baseline, first-parent history, `-limit`, `-prefix`, several at once, submodules skipped; real GitHub repositories compared file for file with git, and imported with history then exported back | `GitImportScenarios.cs` |
| `Workspaces` | Create, edit with any tool, implicit snapshots, status/diff (added, modified, deleted, renamed, mode), ignore rules, describe, undo/redo, restore, names, close/abandon, stacking (`-on`), cloning, checkout, case clashes | `WorkspaceScenarios.cs` |
| `Landing` | Check and land, linear seqs, nothing to land, `-keep`, disjoint changes, reading mainline (`log`, `show`, `ls`, `cat`, `changed`) at any seq, `mainline restore`, strict read sets, large and binary files | `LandingScenarios.cs` |
| `Merge` | Two changes to one file: `replay` (needs-replay), `textmerge` (clean merge, or replay), per-path rules, `conflict` (markers, resolve by editing), `sync`, the conflict overview (`workspace conflicts`), semantic merge with weave and a validation hook | `MergeScenarios.cs` |
| `Leases` | Exclusive and intent leases, renew, release, required leases guarding a landing, stale fencing tokens refused, leases across clients | `LeaseScenarios.cs` |
| `ContextSpecs` | Workspaces over part of the monorepo: rules and profiles, the boundary, widening it to accept a change, landing narrow workspaces, context changes as undoable operations | `ContextSpecScenarios.cs` |
| `LazyFolders` | A lazy folder: every file listed, fetched when read, edits snapshotted and landed | `LazyWorkspaceScenarios.cs` |
| `AgentRuns` | `agent start` with a prompt (deterministic stand-in for Claude Code) or a shell command, `-land`, failures, the run's environment, detached runs (`list`, `show`, `logs`, `wait`, `stop`), replay after losing a race (same change id), existing and stacked workspaces, `-model`, parallel agents, leases; real Claude Code on opt-in | `AgentRunScenarios.cs` |
| `Collaboration` | Several clients on one server: landings and workspaces seen everywhere, unique names, a workspace handed over between machines, concurrent landings, who acted | `CollaborationScenarios.cs` |
| `GitExport` | Export mainline to git (one commit per landing, trailers), incremental exports, import-then-export round trip | `GitExportScenarios.cs` |
| `Maintenance` | Garbage collection keeps what is reachable, scrub finds a corrupted blob (local monorepo), a client's own `gc` and `scrub` | `MaintenanceScenarios.cs` |

### The stand-in agent

`Bassia.Scenarios.Agent` (`bassia-standin-agent`) takes Claude Code's place as `agent.command`: Bassia runs it in the
workspace folder with the prompt as its last argument, exactly as it runs `claude -p`. It carries out plain-English
instructions that a real agent understands the same way, so prompts stay meaningful for `BASSIA_SCENARIO_CLAUDE=1`:

```
Create a file named <path> containing exactly the text: <text>
Append the line <text> to the file <path>
Replace <old> with <new> in the file <path>
Delete the file <path>
Fail with exit code <n>
Sleep for <n> seconds
```

## Writing a scenario

Derive from `ScenarioTest`, put `[Trait("Scenario", "<Name>")]` on the class and `[ScenarioFact]` (with `Needs.*`
for anything beyond PostgreSQL) on each test. Make clients with `CloneAsync("alice")` (or `InitLocalAsync()`), run
commands with `OkAsync` / `FailAsync` / `RunAsync`, edit folders with `Write` / `Read` / `Delete`, and read results by
the keys the reference documents (`Str`, `Long`, `Tables`, `Strings`). Assert outcomes and keys, not message wording,
and compare with git itself rather than fixed numbers where outside data is involved.
