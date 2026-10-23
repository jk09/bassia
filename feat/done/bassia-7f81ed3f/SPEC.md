# Guided setup from a bare `bassia`

## Outcome

A person typing a bare `bassia` at a terminal gets a short help, followed by a guided offer. Outside a monorepo,
Bassia asks whether to set up a new monorepo. If the answer is yes, it walks a newcomer, who knows nothing about
Bassia, through the setup in plain language: which repositories become components, which component needs which, and
which folder becomes the monorepo's root (the meta-repo). Then it runs the `bassia` commands that do it and shows each
one, so the newcomer also learns the command line. Inside an existing monorepo, Bassia prints where the user is and
offers the next steps that fit it. `bassia tutorial` starts the same guide on purpose. Agents, scripts and anyone
with a redirected stream see exactly the old output of a bare `bassia`.

## Context

- Today `bassia` with no arguments (and `bassia help`, `-h`, `--help`) prints the help overview as TOML (exit 0),
  via `ProgramCli.RunAsync` → `Help.Show([])`. Agents rely on this, and the CLI is TOML-first (`-human` renders
  results for people, see `bassia-7d73ffe0`).
- Setup today means `init` in an empty folder, `component add -url ... [-references ...]`, `component set
  -references` and optionally `component tag`. A component's references are its dependencies: they are nested at a
  subfolder in a run's checkout.
- `bassia prompt` (`PromptSession`) is the LLM layer. It reads the LLM settings (`llm.backend`, `llm.command`)
  through `LlmBackends`.

## Acceptance criteria

- [x] `bassia` with no arguments behaves exactly as before (the full TOML help on stdout, exit 0, nothing read
      from stdin) unless stdin, stdout and stderr are all terminals. `help`, `-h` and `--help` always print the full
      help.
- [x] `bassia` with no arguments at an interactive terminal prints a short help (usage, `bassia help`,
      `bassia tutorial`, `bassia prompt`, `-human`) on stdout. Then comes the guided part, on stderr.
- [x] Outside a monorepo, the guided part explains in plain sentences what Bassia is and asks
      `Set up a new Bassia monorepo now? [y/N]`. No, Enter or end of input ends with exit 0 and a hint to run
      `bassia tutorial` later.
- [x] Answering yes runs the setup. Each step explains its concept before asking, and nothing changes before the
      final confirmation:
  - [x] **Components:** repository URLs or local folders, one per line, empty line to finish. Each is checked with
        `git ls-remote` and named (inferred name offered as the default). An unreachable repository, an invalid or
        duplicate name is refused with the reason, and the user is asked again.
  - [x] **Dependencies** (with two or more components): for each component, which others it needs, by number or
        name, Enter for none. An unknown name, the component itself or a cycle is refused with the reason, and the
        user is asked again.
  - [x] **Root folder:** where the monorepo (its meta-repo `.bassia`) is created. The default is the current
        directory when it is empty, else a new subfolder. A non-empty folder or one inside another monorepo is
        refused with the reason, and the user is asked again.
  - [x] **Plan:** the exact `bassia` commands (`init`, then `component add` in dependency order with
        `-references`) are listed and confirmed with `[Y/n]`.
- [x] Each command is run as its own `bassia` process and shown as `> bassia ...` with its result message. If one
      fails, the setup stops: it says what failed, what was done and which commands remain.
- [x] Finally it prints the dependency tree and the next steps: `status`, a first `run start`, `web`, `prompt`,
      and `bassia` again for the options.
- [x] At any question, `?` prints a longer explanation of the step. `? <question>` asks the configured LLM
      (`llm.backend`/`llm.command`) to answer it in the context of the step. If no LLM is available, the reason
      is said and the built-in explanation is printed. End of input at any question stops the guide, and nothing
      is changed before the plan is confirmed.
- [x] Inside an existing monorepo, the guided part prints the root and the status summary with the dependency tree,
      and then a menu. The menu offers: add components, set a component's dependencies, show the status, start an
      agentic run (detached), ask in plain English (hands off to `bassia prompt`), open the web dashboard (hands
      off to `bassia web`), and quit. A monorepo with no components points to adding components first. Commands
      that change something are shown and confirmed before they run.
- [x] `bassia tutorial` runs the guide without the yes/no offer. It reads answers from stdin, so it can be scripted.
      It ends with a TOML result (`-human` renders it) on stdout listing the commands run. It is listed in
      `bassia help`, and `bassia prompt` never runs it.
- [x] Tests cover these cases: a non-interactive bare `bassia` is unchanged; the interactive short help and its
      decline; a full scripted setup with local repositories (two components, a dependency, a root folder) that
      produces the dependency in `components.toml`; refusals (unreachable repository, cycle, non-empty folder); `?`
      without an LLM; the welcome and menu inside a monorepo.
- [x] `README.md` and `docs/reference.md` describe the guided setup.

## Approach

- `Bassia/Tutorial/TutorialSession.cs` holds the dialogue. It takes a `TextReader`/`TextWriter` pair, an
  `ICommandExecutor` (reused from `Prompt/`) and an LLM factory, so tests drive it in-process with scripted input.
- `Bassia/CliCommands/TutorialCommand.cs` holds `bassia tutorial` and the interactive bare-`bassia` entry, which
  `ProgramCli.RunAsync` calls when there are no arguments and `ProgramCli.Interactive` holds.
- The guide changes things only through `bassia` commands (the executor), never by writing `components.toml`
  itself. Each command's output is captured (`SelfExecutor` without relaying), and its message is shown.
- A hand-off (`prompt`, `web`) ends the guide and runs that command line in the same process.

## Decisions

- "Root repo" means the monorepo's root folder, i.e. where its meta-repo `.bassia` is created, not a component.
  Asked last, after components and dependencies, as the request orders it, because nothing is created before the
  final confirmation.
- At a terminal, a bare `bassia` prints a short help instead of the full command table. The full table stays with
  `bassia help` and any non-interactive use.
- `bassia tutorial` is the explicit command. It works with `-C`, can be rerun, and is scriptable through stdin.
- The dialogue is scripted and works offline. The LLM is optional, behind `?` at any question.
- Interactive only when stdin, stdout and stderr are all terminals. A bare `bassia` stays a pure help call for agents
  and scripts, which meets the "keep existing functionality" requirement.
- Dependencies use the default nesting path (the component's name). Custom paths stay with
  `component add|set -references c:path`, which the `?` help mentions.

## Progress

- [x] Open questions resolved
- [x] Guide, welcome menu and `bassia tutorial` implemented
- [x] Tests and docs

## Validation

- `dotnet build` and `dotnet test` (new `TutorialTests`, plus the existing `ProgramCliTests`, `CliSurfaceTests`,
  `HumanOutputTests` and `PromptCommandTests`).
- Manual: in an empty folder, run `bassia tutorial` with scripted input for two local repositories. Run `bassia`
  with redirected stdin and compare it with the old help output.

Verified: the 9 `TutorialTests` and all of `ProgramCliTests`, `CliSurfaceTests`, `HumanOutputTests` and
`PromptCommandTests` pass. The full suite has 358 passed and 1 skipped; the 2 failures are the end-to-end tests that clone
`github.com/jk09/example`, which fail identically without this change where github.com is unreachable. A bare `bassia`
under a pseudo-terminal (`script`) printed the short help and the offer, and declined cleanly. With stdin from
`/dev/null` it printed the TOML help as before.
