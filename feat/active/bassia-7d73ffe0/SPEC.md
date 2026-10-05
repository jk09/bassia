# Human-readable output with `-human`

## Outcome

Every `bassia` command can print its result for a person instead of as TOML: `bassia status -human`,
`bassia -help -human`, `bassia run show <id> -human`. TOML stays the default, so agents and scripts are unaffected.
The help contract is unchanged: `bassia -help` lists all commands, and `bassia <command> -help` (or
`bassia <command> <subcommand> -help`) explains one; a command that needs arguments reports the missing ones, and a
command that needs none just runs.

## Context

Results are TOML on stdout (exit 0) or stderr (exit 1, or 2 for a command line that does not parse), opened by the
`# bassia result` marker (`TomlResult`, `ProgramCli.WriteResult`). Help is generated from `CommandTable`, and `-help`,
`--help`, `-h` and `help` are already accepted anywhere. What is missing is a readable rendering for people at a
terminal. Every result passes through `ProgramCli.WriteResult`, so one renderer covers all commands.

## Acceptance criteria

- [ ] `-human` (or `--human`, case-insensitive) is accepted by every command, with or without `-help`, wherever a
      switch may appear, and is not listed as a switch of any command. Without it, output is byte-for-byte what it
      was (TOML).
- [ ] With `-human`, a result prints no `# bassia result` marker and no TOML syntax: the message (or `error: <message>`
      for a failure), then the data as `key: value` lines, string lists as bullets, nested tables as indented
      sections, and lists of entries as an ASCII table of their scalar columns.
- [ ] Multi-line text results (graphs, trees, tables) are printed as drawn, with no quoting. When a result has its own
      `table` text, that table stands for the entry lists, which are not repeated.
- [ ] `-human` changes only the format: exit codes and the stdout/stderr split are the same, and usage errors
      (`bassia nosuch -human`) are rendered the same way.
- [ ] `bassia -help -human`, `bassia <command> -help -human` and `bassia <group> -help -human` print readable help:
      usage, summary, switches with their descriptions and examples.
- [ ] `bassia <command>` with a missing required switch reports which one (and the usage) in both formats.
- [ ] `-human` after a rest-of-line switch (`-run`, `-resolve`, the ask of `prompt`) belongs to that command line and
      is not consumed.
- [ ] Unit tests cover the renderer and the CLI surface, and `docs/reference.md` documents `-human`.

## Approach

A `HumanOutput` renderer turns the result's payload dictionary into text. `ProgramCli.RunAsync` extracts `-human` up
front (before the rest-of-line switches, which stay verbatim), records the mode for the run, and `WriteResult` picks
the TOML or human rendering. Since help, errors and command results all go through `WriteResult`, nothing else changes.

## Decisions

- **`-human` applies to every command**, not only help: the request pairs it with `-help` but defines it as the
  alternative to the default TOML output.
- **Plain text, not markdown or colour**: it must read the same in a terminal, a log and a pipe.
- **Entry lists render as ASCII tables of their scalar columns**; nested values inside an entry are summarized, and
  the TOML output remains the complete form.
- **A result's own `table` replaces its entry lists** in human mode, because the TOML result already carries both
  forms of the same data (e.g. `component list`).
- **Already implemented, so unchanged**: `-help` on every command, `bassia -help`, the missing-argument errors.

## Progress

- [x] Specification.
- [x] `HumanOutput` renderer and `-human` plumbing.
- [x] Tests and documentation.
- [ ] Build and test run (no .NET SDK was available when this was written; see Validation).

## Validation

`dotnet test`; by hand `bassia -help -human`, `bassia component -help -human`, `bassia status -human`,
`bassia component add -human` (missing `-url`).
