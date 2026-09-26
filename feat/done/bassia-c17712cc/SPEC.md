# Complete, consistent command line for agents

## Outcome

`bassia` exposes everything the monorepo can do - creation, configuration, components and their dependencies,
starting agentic runs in the foreground or detached, monitoring, stopping, retrying and abandoning them, reviewing
their results, integrating (merging) them and advancing base branches - through one consistent command line:
`bassia <command> [<subcommand>] -switch value ...`. Every command, help included, prints TOML; history and the
component dependencies come with ASCII graphics inside that TOML. An agent can drive Bassia from a shell at least
as completely as a person can from `bassia ui` or `bassia web`.

## Context

The CLI grew one command at a time: `agent -select ... -run ...`, `add-component <url> [name]`, `integrate
advance <id>`, `web --port`, and `status`/`log`/`branch`/`commit`, which pass raw git output through. Its help is
one screen without examples. Some of what the frontends can do has no CLI path at all:

- listing and inspecting components, their tags, branches and dependency graph (`ui` boards, `web` pages)
- tagging a component (`ui` key `t`)
- starting a run in the background, watching it, reading its output, and stopping it (`ui` `n`/`x`, `web` New run
  / Stop), and composing the run command from a prompt, model, effort and context
- listing and inspecting recorded runs and integrations
- a cross-component timeline (`web` Timeline)
- exporting the dependency graph as Mermaid or SVG (`ui` `g`)
- editing configuration and component references (only by hand-editing TOML today)

Bassia is operated and monitored mainly by AI agents, so the command line is the primary interface. Related records:
`bassia-a7b47671` (interactive frontend), `bassia-f552a672`/`bassia-4f1c8e73` (runs, cancellation), the TOML results
change (`2e313f7`) and the web dashboard.

## Acceptance criteria

Grammar, output and help

- [x] Commands follow `bassia [-C <path>] <command> [<subcommand>] [-switch [value]]...`. Switches are
      case-insensitive and accept `-name` and `--name`; a subcommand's main argument may also be given
      positionally (`bassia run show 3f2a91c4`). Unknown commands, subcommands and switches fail with a TOML error
      naming the problem and the help command to run, exit code 2.
- [x] Every command prints a TOML result (`# bassia result`, `ok`, `command`, `message`|`error`, then data): on
      stdout with exit code 0 on success, on stderr with a non-zero exit code on failure. ASCII graphics are
      multi-line literal strings inside the result, so the output stays one valid TOML document.
- [x] `bassia help`, `bassia help <command> [<subcommand>]` and `-help` on any command print TOML help: summary,
      usage, every switch with its description, and at least one example per (sub)command. `bassia help` lists
      every command.

Monorepo and configuration

- [x] `init [-path <dir>]` (positional kept) creates a monorepo, as today.
- [x] `status` reports the monorepo: root, workspace, component count, runs by status, live runs, integrations,
      and the meta-repo's working-tree changes.
- [x] `config list`, `config get -key <k>`, `config set -key <k> -value <v>` read and write the known keys
      (`workspace.path`, `agent.command`, `agent.commit.subject`, `integration.resolver`) in `config.toml`, keep its
      comments, and commit the change to the meta-repo. Unknown keys fail.

Components

- [x] `component list` lists every component with its URL, references, referrers, branch/tag counts and runs.
- [x] `component add -url <url> [-name <n>] [-references <a,b>]` registers a component (as `add-component`).
- [x] `component show -name <n>` shows its branches, tags (annotated marked), references, referrers and the runs
      that touched it with their result tags.
- [x] `component set -name <n> -references <a,b:path,...>` replaces a component's references; a cycle or an
      unregistered name is rejected and nothing is written; the change is committed to the meta-repo.
- [x] `component remove -name <n> [-purge]` unregisters a component nobody references; `-purge` also deletes its
      source-of-truth repo.
- [x] `component tag -name <n> -tag <t> [-ref <commit-ish>] [-message <m>]` creates an annotated tag.
- [x] `graph [-name <n>] [-format ascii|mermaid|svg] [-out <file>]` shows the dependency graph as an ASCII tree
      (default), Mermaid or SVG, optionally written to a file.

History

- [x] `log [-component <a,b>] [-limit <n>] [-page <n>]` shows the meta-repo history by default, or the combined
      timeline of the chosen components and their dependencies, with an ASCII graph: git's commit graph for one
      component, one lane per component for several.

Agentic runs

- [x] `run start -select <c@tag,...> (-run <command> | -prompt <text> [-agent <cmd>] [-model <m>] [-effort <e>]
      [-context <text>]) [-detach]` starts a run through the same path as `bassia agent`. Without `-detach` it
      behaves like `bassia agent` (agent output streamed, result at the end). With `-detach` the selection is
      validated first, then the run continues in a background bassia process, and the result (with `run_id`,
      `pid`, `log`) returns immediately.
- [x] `run list [-status <s>] [-component <c>] [-limit <n>]` lists recorded runs newest first, marking live ones.
- [x] `run show -id <run>` shows a run's record, per-component results, whether its process is alive, and the
      last line of its output (detached runs).
- [x] `run logs -id <run> [-tail <n>]` prints a detached run's captured output.
- [x] `run wait -id <run> [-timeout <seconds>]` blocks until the run reaches a terminal status; on timeout it fails
      with `timed_out = true`.
- [x] `run stop -id <run>` stops a live run started from the CLI (foreground in another shell, or detached): the
      agent's process tree is killed, the run is recorded `cancelled` and its folder kept; stopping a run that is
      not live fails.
- [x] `run retry -id <run>` and `run abandon -id <run>` behave as `agent retry`/`agent abandon`.
- [x] `run diff -id <run> [-component <c>] [-patch]` shows what the run changed per component (diffstat, and the
      patch with `-patch`).

Integration (merging)

- [x] `integration plan -runs <ids|all> [-onto ...] [-semantic ...] [-skip ...]` prints the triage (as
      `integrate -plan`), with an ASCII picture of the steps per component.
- [x] `integration start -runs ... [-onto ...] [-semantic ...] [-skip ...] [-resolve <cmd>] [-detach]`
      integrates (as `integrate`); `-detach` runs it in the background like `run start -detach`.
- [x] `integration list`, `integration show -id <id>`, `integration wait -id <id>`, `integration stop -id <id>`,
      `integration logs -id <id>` and `integration advance -id <id>` cover recorded and live integrations.

Frontends and compatibility

- [x] `web [-port <n>] [-no-open]` and `ui` are unchanged apart from single-dash switches.
- [x] The replaced forms (`add-component`, `agent`, `integrate`, `commit`, `branch`) are no longer accepted: each
      fails with exit code 2 and a TOML error naming the command that replaces it.
- [x] README documents the command line; the tests cover every new command.

## Approach

- A small declarative command table (name, subcommand, summary, switches, positional, examples, handler) replaces
  the ad-hoc PowerArgs routing. One parser produces a switch map from it; help, unknown-switch errors and usage
  are generated from the same table, so help can never drift from what is accepted.
- New handlers reuse the existing model: `Monorepo`, `RunMetadataStore`, `IntegrationStore`, `IntegrationPlanner`,
  `AgentCommand.StartRunAsync`, `IntegrationRunner`, `ComponentGraph`, `Timeline`, `InteractiveSession.ComposeCommand`.
- **Jobs**: a CLI run or integration registers a job file in `<workspace>/.jobs/<id>.toml` (pid, kind, started,
  log). The process watches for `<id>.stop`; `run stop` creates it, which cancels the run through the existing
  `AgentRunContext.Cancellation` (records `cancelled`), and kills the process tree itself if it does not exit in
  time. `-detach` spawns bassia again with a pre-generated id and a hidden `-log` switch, with no inherited
  standard handles, so a caller that captures stdout is never held open by the background run.
- `TomlResult` learns a multi-line text value, emitted as a TOML literal string, for ASCII graphics.
- The replaced commands are removed; a table of hints maps each old name to its successor.

## Decisions

- **Replace, don't alias** (user): the old forms (`agent -select ...`, `add-component <url>`, `integrate ...`,
  `commit`, `branch`, raw-git `status`/`log`) are removed rather than kept as aliases; they fail with a hint to the
  new command. `init <dir>` keeps its positional argument because the new grammar allows one per command.
- **Foreground by default** (user): `run start` streams the agent like `bassia agent` did; `-detach` is opt-in.
- **TOML help** (user): help is a TOML document (summary, usage, switches, examples), one parsing path for agents.

- ASCII art is embedded in results as multi-line literal strings, so output stays one TOML document.
- Noun-verb command groups (`component`, `run`, `integration`, `config`) instead of more top-level verbs.
- Job state lives in the workspace (local, disposable), not in `.agentic-runs` (durable, shared record).

## Progress

- [x] Command table, parser, TOML help (with multi-line ASCII text in results)
- [x] Monorepo, config, component, graph, log commands
- [x] Run commands with jobs, detach and stop (foreground, detached, and runs hosted by `ui`/`web`)
- [x] Integration commands
- [x] Replaced commands removed with hints; UI/web texts, scripts and README updated; tests

## Validation

- `dotnet build`: no warnings.
- `dotnet test` (with `BASSIA_E2E_COMPONENT_URL` pointing at a local mirror, as GitHub was not reachable anonymously):
  225 of 226 pass. New: `CliSurfaceTests` (grammar, help of every command, status, config, components, graph, log),
  `RunCommandTests` (prompt composition, list/show/diff/logs, stale and hosted stop, detached run with wait/logs,
  detached stop, detached integration with advance) and `TomlResultTests` for multi-line text. The one failure,
  `Agent_ComponentChain_IsCheckedOutSideBySideAndJunctionedIntoItsReferrers`, fails identically on `main` on Linux
  (the nested component link is committed by the referrer there); it is unrelated to this change.
- Manual, on a throwaway monorepo driven only through `bassia`: init, component add (with references) / tag, graph,
  status, config list/set, run start (foreground error, `-detach`), run list/show/wait/logs/diff/stop (the agent's
  `sleep` was gone afterwards), integration plan / start `-detach` / list / stop (a real `claude` resolver was killed
  and the integration recorded `cancelled`) / logs, log for the meta-repo, one component and several.
