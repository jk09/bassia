# Command-line parsing with System.CommandLine

## Outcome

The `bassia` command line is parsed by an existing library, `System.CommandLine`, instead of a hand-written parser. The
structure of the command line - which commands and subcommands exist, which options, arguments and global options each
takes, which are required - is declared as a tree of `Command`, `Option` and `Argument` objects in one place, so a
developer can read that code and see exactly what `bassia` accepts.

## Context

Until now `Cli/Invocation.cs` parsed arguments by hand against a flat `CommandTable` of `CommandSpec`/`SwitchSpec`
records, and `ProgramCli` pre-scanned the line for `-C`, `-human`, rest-of-line switches and subcommands. The table
already held the metadata, but the grammar lived in custom code that every contributor had to learn.

Constraints: agents already drive `bassia` with the existing grammar (`-name value`, `--name`, case-insensitive, a bare
main argument, comma-separated lists, `-run`/`feedback send` taking the rest of the line), results stay TOML and exit
code 2 still means a command line that does not parse. Handlers keep their `Invocation` API.

## Acceptance criteria

- [x] `System.CommandLine` is referenced by `Bassia`; commands are declared as a nested `Command` tree (groups such as `workspace`, `mainline`, `agent` hold their subcommands) with typed `Option`/`Argument` declarations.
- [x] The hand-written switch parser is gone; `Invocation` is a thin adapter over `ParseResult`.
- [x] The existing grammar is unchanged: `-name`/`--name`, case-insensitive switches and commands, a bare main argument for a command's positional switch, repeated/comma lists, `-C` (repeatable), `-human` anywhere, `-h`/`-help`/`--help`.
- [x] Rest-of-line switches (`agent start -run`, `feedback send <message...>`) still take everything after them verbatim.
- [x] Unknown command/subcommand/switch, a missing value and a missing required switch still exit 2 with a TOML error carrying the usage line; replaced commands still print their replacement hint.
- [x] `bassia help` output (TOML and `-human`) is generated from the same tree and lists the same commands and usage lines as before.
- [x] The existing CLI, human-output, feedback and new-monorepo tests pass; new tests pin the declared tree and the grammar.
- [x] `docs/reference.md` says where the command line is declared.

## Approach

Declare commands with small factory helpers over `System.CommandLine` types (`BassiaCommand` adds the metadata help and
errors need: examples, details, handler, positional). A normalisation pass maps `--x`/`-X` to each option's canonical
`-x` and cuts rest-of-line text off before the library parses the rest. The dispatcher inspects the `ParseResult`:
help, group without a subcommand, replaced commands, parse errors, then invokes the command's action.

## Decisions

- Library: `System.CommandLine` 2.0 (Microsoft, stable, the standard for .NET CLIs, no extra dependencies). Spectre.Console.Cli, CommandLineParser and Cocona were not chosen: none models single-dash long options or a rest-of-line switch better.
- Compatibility over library defaults: the library is case-sensitive and ships its own `--help`/`--version`; these are replaced (canonicalisation, a `-help` option, no built-in version option) so existing scripts and agent prompts keep working.
- Handlers keep reading values through `Invocation.Get/Has/Require/List/Int`; typed option values are a separate, larger change.

## Progress

- [x] Declarations ported to a `Command` tree
- [x] Dispatcher and `Invocation` over `ParseResult`
- [x] Help generated from the tree
- [x] Tests and docs

## Validation

`dotnet test Bassia.slnx` with `BASSIA_TEST_POSTGRES` set (316 tests pass, none skipped); `bassia help` and `bassia help -all` are byte-identical to the output of the hand-written parser; `Bassia.Tests/CommandLineTests.cs` pins the declared tree and the grammar.
