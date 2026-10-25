# Agents drive Bassia from the outside, through its CLI

## Outcome

Bassia contains no LLM integration of its own. `bassia prompt` and the LLM backend behind it are gone; instead, any
agent (Claude Code, Codex, a script around any model) learns Bassia by running `bassia`, `bassia help ...` and other
commands and parsing their TOML, and then drives it with ordinary `bassia ...` command lines. The help output is
complete enough that an agent with no other source can infer what Bassia is, how its parts fit together, which
commands only read and which change something, and every command's switches.

## Context

`bassia prompt <ask>` (`Bassia/Prompt`) sent a catalog generated from `CommandTable`, the monorepo's context and the
skills to an LLM it started itself (`llm.backend`, `llm.command`, default `claude -p`), parsed its TOML replies into
command lines and ran them. The tutorial's `? <question>` used the same backend. That couples Bassia to a model and
duplicates what any agent harness does better. The user wants the reverse direction: the agent stays outside and
queries Bassia's CLI.

Running the CLI as an outside agent (bare `bassia` with redirected stdio, `bassia help`, `bassia help <group>`,
`bassia help <command>`, then `init`, `component create`, `agent start -run`, `integration plan|start`) showed what an
agent cannot infer from the output alone:

- the overview's command table truncates summaries at 160 characters, and there is no structured command list;
- nothing explains the concepts (component, reference, meta-repo, workspace, run, integration, lock, skill) or the
  usual sequence of commands;
- nothing says which commands only read (safe to explore with) and which change something, need a person at a
  terminal (`tutorial`, `feedback send`), or never return (`web`);
- learning every command takes one `help` call per command;
- the overview's prose quoted the result marker (`# bassia result`), so a parser that follows its own advice and
  reads from the last marker line starts inside that prose;
- nothing said how the words after `-run` / `-resolve` become a command: one argument is used verbatim, several are
  re-joined (a word with whitespace wrapped in double quotes) and run by the platform shell, so a command with its
  own quotes breaks unless given as one argument. Found when this run's own `-run sh -c '...'` failed.

## Acceptance criteria

- [x] `bassia prompt` is no longer a command: it fails with exit code 2 and points to `bassia help`, like other
      replaced commands. No `ILlmBackend`, LLM process or LLM-specific prompt text remains in Bassia.
- [x] `llm.backend` and `llm.command` are no longer configuration keys and `init` no longer writes an `[llm]` table;
      an existing `config.toml` with an `[llm]` table still loads, and `config set llm.command` fails as an unknown key.
- [x] The tutorial no longer calls an LLM: `?` explains the step from built-in help, `? <question>` shows the same help
      and points to `bassia help`; the menu no longer offers "ask in plain English".
- [x] `bassia help` (and bare `bassia` without a terminal) gives, besides usage, grammar and the result format:
      `concepts` (what Bassia's parts are), `workflow` (the usual sequence of commands), `discover` (how to learn more),
      and one `[[commands]]` entry per command with its full summary, usage, `read_only` and `interactive`.
- [x] `bassia help -all` returns every command's full help (summary, usage, details, positional, examples, switches,
      `read_only`, `interactive`) in one result, so an agent can learn the whole CLI in one call.
- [x] `bassia help <command>` and `bassia help <group>` report `read_only` and `interactive` for each command.
- [x] The marker line appears in help output only as the result's own first line, and the grammar and the `-run` /
      `-resolve` switches explain how a rest-of-line command reaches the shell.
- [x] `skill list|show` remain as a way for outside agents to read the monorepo's reusable instructions; their help
      no longer mentions `bassia prompt`.
- [x] README, `docs/reference.md`, the dashboard and help texts no longer mention `bassia prompt` or `llm.*`, and
      document how agents learn and drive Bassia through the CLI.
- [x] An agent (this run's) learns Bassia only from `bassia` and `bassia help ...` output and then completes a full
      cycle with it: init, create components with a reference, run an agent, inspect the run, plan and start an
      integration, advance it.

## Approach

Delete `Bassia/Prompt/{LlmBackend,PromptSession,PromptReply}.cs`, the `prompt` command and its tests; register
`prompt` in `CommandTable.Replaced`. What still serves other code moves out of the `Prompt` namespace:
`CommandCatalog` (only `Display` and the read-only set remain) into `Bassia.Cli`, `SelfExecutor`/`ICommandExecutor`
next to the tutorial, `SkillStore` into `Bassia.Skills` with `skill list|show` in `SkillCommands`.
`PromptException` goes; the few failures that used it become `MonorepoException` or `IOException`.

`CommandSpec` gets `ReadOnly` and `Interactive` derived from one table in `CommandTable`, and `Help` renders them;
the overview adds the concepts, workflow and discovery text and a structured `[[commands]]` list (`command` is a key
every result reserves); `help -all` renders each command like `help <command>` as `[[commands]]` entries. A person
(`-human`) still gets the overview's command table instead of the entries.

## Decisions

- `agent start -prompt` / `agent.command` and `integration.resolver` stay. They start the agent a run or an
  integration exists for, chosen by the user; Bassia does not consult that agent about itself or hand it internals
  beyond the run's prompt and the merge brief.
- `skill list|show` stay: skills are repository content an outside agent reads through the CLI.
- Old `[llm]` tables are ignored rather than rejected, so existing monorepos keep working without an edit.
- The read-only set is a property of the command table (not of a prompt layer), so help and any future consumer
  agree on it. `graph -out` writes only the file it is given, so `graph` counts as read-only. `web` changes nothing
  and is read-only, but interactive (it serves until Ctrl-C), so agents should not run it to explore.
- The tutorial's "ask in plain English" menu item is dropped; the dashboard becomes item 5.

## Progress

- [x] Explore the CLI as an outside agent and list what it cannot infer.
- [x] Remove `bassia prompt`, the LLM backend and the `llm.*` keys; tutorial without LLM.
- [x] Agent-oriented help: concepts, workflow, `[[commands]]`, `read_only`, `interactive`, `help -all`.
- [x] Docs, dashboard and tests updated; full test suite green.
- [x] Re-learn Bassia from the new help output alone and run a full cycle.

## Validation

- `dotnet test` (whole suite).
- `bassia </dev/null`, `bassia help`, `bassia help -all`, `bassia help agent start`, `bassia prompt x` (exit 2),
  `bassia config set -key llm.command -value x` (unknown key) on the Debug build.
- In a scratch folder: `init`, `component create lib`, `component create app -references lib`,
  `agent start -select app,lib -run ...`, `agent show`, `agent diff`, `integration plan -runs all`,
  `integration start -runs all`, `integration advance`, `status`, `graph`, `log`.

`ThreeComponentEndToEndTests` and `ParallelAgentRunEndToEndTests` clone `https://github.com/jk09/example.git` and fail
where GitHub is not reachable without credentials (as in this run's sandbox); they do not touch the changed code.
