# Scenario tests: Bassia exercised through its command line, by user scenario

## Outcome

A test project, `Bassia.Scenarios`, simulates people and agents using Bassia: every test drives the `bassia` command
line (arguments in, TOML results out) against a `bassia-server` whose blob storage is an in-memory mock and whose state
and trees live in a real PostgreSQL. Tests are grouped by user scenario (import a repository, run an agent, land, merge,
...), so a scenario's tests can be run, read and debugged on their own on a developer machine, and their results say
what works. They rely only on the documented command-line contract, so they keep running unchanged as Bassia evolves.

## Context

- `Bassia.Tests` (about 9,500 lines) tests the core and the CLI, mostly through internal APIs, alongside the code; it is
  a developer's test suite, not an evaluation of the product's behavior from the outside.
- The command line is the primary interface (`docs/reference.md`, "Command line"): every command answers a TOML result
  that opens with `# bassia result`, with `ok`, `command`, `message`/`error`, exit code 0/1/2. That is the stable
  contract scenario tests may rely on.
- `bassia-server` keeps heads, path index, leases, the workspace registry and trees in PostgreSQL; its blobs go to
  `-storage disk|s3|azure`. A client is `bassia clone -from <url>`. Detached agent runs and lazy folders start further
  `bassia` processes, so a mock blob store must be reachable from other processes: a blob store inside the test
  process's server, reached over HTTP, is.
- One capability had no command: the live conflict overview (`GET /api/v1/conflicts`), shown only by Studio.
- Agentic runs start `agent.command` (by default `claude -p --permission-mode acceptEdits`) in the workspace folder.

## Acceptance criteria

- [x] `bassia-server -storage memory` keeps blobs in memory (nothing is written to the data folder's `objects/`):
      signed blob URLs, batched fetch/put and direct reads work as with the disk store. Documented.
- [x] `bassia workspace conflicts` prints the conflict overview (each open workspace's would-be outcome, overlaps
      between open workspaces, hot paths) on a clone and on a local monorepo. Documented, listed by `bassia help`.
- [x] `Bassia.Scenarios` builds in `Bassia.slnx` and runs with `dotnet test Bassia.Scenarios`; each test carries a
      `Scenario` trait, so `dotnet test --filter Scenario=<name>` runs one scenario.
- [x] Scenario tests use only the command line (`bassia ...` arguments, the TOML result, exit code, files in workspace
      folders) and git; none references a Bassia type other than the harness that hosts the CLI and the server.
- [x] Each test runs against a fresh server (in-memory blob storage, a fresh PostgreSQL schema) and fresh clients.
- [x] PostgreSQL comes from `BASSIA_TEST_POSTGRES`, or else a throw-away container the harness starts with Docker; with
      neither, scenarios are skipped with the reason.
- [x] The CLI runs in-process by default (breakpoints in Bassia's code work from the test), or as an installed `bassia`
      named by `BASSIA_SCENARIO_CLI`, so a released build can be evaluated with the same tests.
- [x] Scenarios covered: monorepo setup (clone, status, config, help, results contract); git import (baseline, history,
      prefixes, several repositories, real GitHub repositories); workspaces (create, edit, status, diff, describe,
      undo/restore, log, close, abandon, stacking, cloning, checkout); landing (check, land, keep, nothing, mainline
      log/show/ls/cat/changed/restore); merge and conflicts (replay, textmerge, conflict markers and resolution, sync,
      strict read sets, overview); leases; context-specs; agentic runs (scripted command, agent command with a prompt,
      detach/list/show/logs/wait/stop, land, replay, failure); multiple clients sharing one server; git export
      (round trip with git, incremental); maintenance (gc, scrub).
- [x] Real GitHub repositories (small public ones by default, `BASSIA_SCENARIO_REPOS` to choose others) are imported
      when the network allows (opt-out with `BASSIA_SCENARIO_OFFLINE=1`); the assertions compare Bassia with git itself
      (file list and contents, commit count), never fixed numbers.
- [ ] Agent scenarios run with a deterministic stand-in agent (a script in place of `claude`, given the prompt the
      same way) by default; with `BASSIA_SCENARIO_CLAUDE=1` they also run the real local Claude Code, asserting only
      outcomes an agent cannot get wrong by phrasing (the file the prompt asks for exists, the run succeeded and landed).
      *(Stand-in runs verified; the real Claude Code run is still to be verified on a signed-in machine: the record
      stays active until then.)*
- [ ] Every test is part of GitHub CI: the Linux Test job runs all of `Bassia.Tests` and `Bassia.Scenarios`, the
      real Claude Code scenarios included whenever the `ANTHROPIC_API_KEY` secret is set (with a notice when it is
      not); a Windows job runs `Bassia.Scenarios` (cmd.exe agents, ProjFS lazy folders). Prerequisites a job provides
      are required (`BASSIA_SCENARIO_REQUIRE`): their tests fail instead of being skipped.
- [x] `Bassia.Scenarios/README.md` explains how to run and debug the scenarios locally (PostgreSQL in Docker, filters,
      the environment variables) and lists the scenarios.

## Approach

- Server option `-storage memory` (`MemoryObjectStore` behind the object catalog; `/blobs/{id}` serves and accepts it
  like the disk store).
- CLI `workspace conflicts` over `Monorepo.Overview` (`IConflictOverviewSource`): remote on a clone, reconciled on read
  in a local monorepo.
- `Bassia.Scenarios` (xUnit): a harness (`ScenarioHost`: PostgreSQL schema, in-process `bassia-server` with memory
  storage, clients made with `bassia clone`; `Cli`: runs `bassia` in-process or as a process, parses TOML; small git
  helpers), and one folder/class per scenario with `[Trait("Scenario", ...)]`.
- Tests assert behavior named in `docs/reference.md` (outcomes, keys, files), not message wording.

## Decisions

- Mock blob storage at the server (`-storage memory`), not in the client: clients, detached runs and lazy-folder
  servers in other processes all reach it over HTTP, and the trees stay in the server's real PostgreSQL.
- Scenarios run against a server and clones, the main topology; a local monorepo (`bassia init`) is covered by its own
  small scenario, since its blobs are on local disk by design.
- (User) Real Claude Code runs are opt-in (`BASSIA_SCENARIO_CLAUDE=1`): they cost tokens and are not deterministic.
- (User, follow-up) Make all tests part of GitHub CI: the Claude Code scenarios run on every push and PR where the
  `ANTHROPIC_API_KEY` secret exists; the scenario suite also runs on Windows (not macOS, not in installed-binary mode).
- (User) GitHub imports use small public repositories: `octocat/Hello-World`, `octocat/Spoon-Knife` and
  `sindresorhus/slugify` (real history); overridable with `BASSIA_SCENARIO_REPOS`.

## Findings

What the scenarios turned up in Bassia, and what was done about it:

- `textmerge` that cannot merge cleanly refused the landing as `conflict` (store and resolve by hand), while the
  reference says "otherwise as `replay`" - text is never merged behind an agent's back. Fixed: it is `needs-replay`.
- The context boundary error says "Widen the context ('bassia workspace context -add <rule>')", but `-add` snapshots
  first under the old spec and failed with the same error. Fixed: a change outside the current spec that the new spec
  takes in is snapshotted within the new spec.
- The conflict overview had no command (Studio only). Added `bassia workspace conflicts`.
- Observed once, not reproduced since: in the very first full run, `CollaborationScenarios.ConcurrentLandings...`
  landed four disjoint workspaces as seqs 1-4 (all `landed`, pre-reconciled) and then `mainline cat area1/change.txt`
  at seq 4 failed. More than 60 further runs of that scenario (in the full suite, in stress loops, in-process and
  out-of-process) passed. The scenario now records `mainline show` and `mainline ls` per seq, so a recurrence shows
  whether the tree lost the path or the read failed.

## Progress

- [x] Server `-storage memory`
- [x] `workspace conflicts`
- [x] Harness and README
- [x] Scenarios (84 tests in 12 scenarios)
- [x] Full run green locally: 82 passed, the 2 real-Claude tests skipped (opt-in); `Bassia.Tests` 403 passed
- [x] Out-of-process mode (`BASSIA_SCENARIO_CLI`, `BASSIA_SCENARIO_SERVER`) green on MonorepoSetup, Landing, Merge
- [x] Opt-in Claude wiring checked with a fake `claude` on PATH (this container's Claude Code has no usage credits);
      a run against the real Claude Code is left to a machine where it is signed in

## Validation

- `BASSIA_TEST_POSTGRES=... dotnet test Bassia.Scenarios` - all scenarios pass (GitHub and Claude ones where available).
- `dotnet test Bassia.Tests` - no regression.
- `dotnet test Bassia.Scenarios --filter Scenario=ImportRepo` runs only that scenario.
