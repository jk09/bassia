# Natural-language prompt over the command line

## Outcome

`bassia prompt <ask in natural language>` turns an ask such as `initialize the monorepo at folder C:\mono` into
ordinary `bassia` commands, runs them, and answers with a TOML result like every other command. An LLM plans the
commands; Bassia validates and executes them itself, so the LLM never gets a shell. The LLM backend is Claude Code
(`claude -p`) by default, and any other model can be plugged in as a command that reads a prompt on stdin, or as a new
backend in code. Skills - reusable instructions for recurring asks - live in the meta-repo under
`.bassia/skills/<name>/SKILL.md` and are offered to the LLM.

## Context

The command line is regular: every command is `bassia [-C <path>] <command> [<subcommand>] [-switch [value]]...`,
described by one record in `CommandTable` (summary, switches, examples, details), and every command prints a TOML
result opening with `# bassia result`. That makes a thin LLM layer possible without per-command glue: the command
catalog given to the LLM is generated from the table, a proposed command is checked with the same parser the command
line uses, and results are read back from the TOML. Claude Code is installed on the user's machine; other backends
(local models, other vendors' CLIs, HTTP APIs) must remain possible without changing the prompt layer. `bassia init`
has to work through the prompt while no monorepo exists yet.

## Acceptance criteria

- [x] `bassia prompt <words...>` (or `-ask <text>`) sends the ask, the command catalog generated from `CommandTable`,
      the context (working directory, platform, monorepo root and components when inside one) and the available
      skills to the LLM, and executes the commands it answers with, in order, as `bassia` child processes.
- [x] The LLM answers in TOML (`[[command]] args = [...]`, `done`, `answer`, `load_skills`); a fenced or prefixed
      answer is still read. An unreadable answer fails the command with the raw reply in the result.
- [x] Every proposed command is validated against the command table before it runs (unknown command, subcommand or
      switch, missing value). `prompt` and `web` are never run. An invalid command is not run; the error goes back
      to the LLM.
- [x] Multi-step asks work: when the LLM is not done, the executed commands' TOML results are sent back and it plans
      the next step, up to `-max-rounds` rounds (default 8). The final `answer` is the result's message; the result
      is not ok when the last command run failed or the rounds ran out.
- [x] Read-only commands run without asking. A command that changes something is confirmed interactively
      (`y`/`n`/`a` for all); with stdin redirected it is refused unless `-yes` is given. `-dry-run` changes nothing:
      it runs read-only commands only and stops with the plan at the first command that would change something.
- [x] The result is TOML: `ok`, `command = "prompt"`, `message` (the answer or a summary), `backend`, `steps`, and one
      `[[step]]` per proposed command with `command_line`, `status` (ok, failed, rejected, declined, planned, skipped) and the
      child's `message`/`error`. Each child's own output is shown before the final result, which is parsed from the
      last `# bassia result` marker as with `run start`.
- [x] The backend is chosen by `-backend` or `llm.backend` (`claude` default, `command`), its command by `-llm` or
      `llm.command`, the model by `-model`. Backends implement one interface and are registered by name; an unknown
      backend name fails with the known ones listed. A backend command that fails or prints nothing fails the
      command with its exit code and output.
- [x] Skills are `.bassia/skills/<name>/SKILL.md` files with optional front matter (`name`, `description`). Their
      names and descriptions are always offered; the LLM loads a skill's body by name (`load_skills`), `-skill
      <name>,...` or a `/<name>` word in the ask preloads it. An unknown `-skill` fails with the known skills listed.
- [x] `bassia skill list` and `bassia skill show -name <skill>` list and print the meta-repo's skills.
- [x] `bassia config` knows `llm.backend` and `llm.command`. Help, README and tests cover the new commands.

## Approach

A `Bassia.Prompt` area holds the layer: the catalog and system prompt built from `CommandTable`, the TOML reply
parser, the validator (lookup and `Invocation.Parse` without running the handler), the read-only policy, the skill
store, the backend interface with its registry, and the step loop. Commands are executed by re-launching the running
`bassia` (its own executable, or `dotnet <dll>`) with an argument list, never through a shell, so paths with spaces
or backslashes need no quoting and a command cannot escape the table. The loop, the executor and the confirmation are
injectable so tests drive it with a scripted backend and no LLM.

## Decisions

- **Bassia drives, the LLM plans.** The LLM only returns command argument lists; Bassia validates and runs them.
  Rejected: giving Claude Code a Bash tool to call `bassia` itself - it would only work with agentic backends, bypass
  validation and confirmation, and tie the layer to one vendor.
- **Backends are text in, text out.** `ILlmBackend.CompleteAsync(system, prompt)` is the whole contract, so a plain
  completion API suffices. The `claude` backend runs `llm.command` (default `claude -p`) with `--tools ""` (planning
  needs no tools), `--output-format text` and `--model`; the prompt goes on stdin (command-line length limits on
  Windows). The `command` backend runs `llm.command` verbatim with the system prompt and the prompt on stdin, e.g.
  `ollama run llama3` or `llm -m gpt-4o`.
- **TOML replies with argument arrays**, not shell lines: the same notation as every result, and no quoting rules to
  get wrong for Windows paths (the LLM is told to use literal strings).
- **Confirm what changes something.** A fixed set of read-only commands (`status`, `config list|get`, `version`,
  `component list|show|survey`, `tag list|show`, `graph` without `-out`, `log`, `run list|show|logs|wait|diff`,
  `integration plan|list|show|logs|wait`, `skill list|show`, `help`) runs freely; everything else - including any
  command added later - needs confirmation or `-yes`.
- **Skills follow the Claude Code skill shape** (`<name>/SKILL.md` with `name`/`description` front matter) and
  progressive disclosure: descriptions up front, bodies on request, so many skills cost little prompt. They are
  versioned in the meta-repo like `config.toml`; there is no `skill add` command - they are written and committed
  like any other meta-repo file.
- **Dry run executes reads.** A plan for "remove the component that nothing uses" needs the component list first, so
  `-dry-run` runs read-only commands and stops at the first change, rather than showing only a first round of lookups.
- **`init` writes an `[llm]` section** with both keys and their explanation, like the other settings.
- **Assumed, not asked** (the request left them open; revisit if wrong): confirmation semantics above, the round limit
  of 8, child output echoed to the terminal, skills only from the meta-repo (no per-user skill folder yet).

## Progress

- [x] Specification.
- [x] Prompt layer: catalog, reply parser, validator, policy, skills, backends, step loop, `prompt` and `skill`
      commands, config keys.
- [x] Tests with a scripted backend and executor; README and help.

## Validation

- `dotnet build` and `dotnet test` (new `PromptCommandTests`; the `CliSurfaceTests` help theory covers `prompt` and
  `skill`). Verified: all pass except the two end-to-end tests that clone from github.com, which the sandbox cannot
  reach (unrelated to this change).
- Manual, with Claude Code 2.1 on Linux: `bassia prompt -dry-run initialize thee monorepo at folder <dir>` planned
  `init -path <dir>`; `-yes` ran it; with an `onboard` skill in `.bassia/skills`, "onboard the library at ../lib"
  loaded the skill in round 1 and ran `component add` and `component tag -tag v1 -message baseline` in round 2; "which
  tags does lib have" ran `tag list -component lib` and answered from its result; "remove the lib component" with
  stdin redirected and no `-yes` was declined without running anything.
