# Remove the terminal UI

## Outcome

`bassia` no longer has an interactive terminal frontend. The command line (`bassia <command>`, TOML results) and the web dashboard (`bassia web`) are the two ways to use a monorepo; `bassia ui`, its wallboards, wizard and integration control panel are gone, together with the Spectre.Console dependency they were built on.

## Context

The terminal frontend was added by [bassia-a7b47671](../../done/bassia-a7b47671/SPEC.md) as `bassia ui` (`CliCommands/UiCommand.cs` and the `Bassia/Ui` folder). It is not needed for now: the CLI serves agents and scripts, and the web dashboard serves people. Keeping a third frontend in sync with every new command costs more than it returns.

Part of `Bassia/Ui` is not terminal-specific and has other users that stay:

- `ComponentGraph` (tree text, Mermaid, SVG, levels, referrers) backs `graph`, `component show/set`, `status`, the splitter and the web dashboard.
- `ComponentBoard` and `CharCanvas` draw `bassia graph -format board` (plain ASCII).
- `RunSupervisor`/`RunCard` host the runs the web dashboard starts; the dashboard also uses `RunBoard`'s phase and elapsed-time text, `InteractiveSession.ComposeCommand` (shared with `run start -prompt`) and `IntegrationPanel.Summary` (via `Spectre.Console.Markup.Remove`).

## Acceptance criteria

- [x] `bassia ui` is no longer a command: `bassia help` does not list it, and `bassia ui` fails like the other removed commands (`agent`, `commit`, ...): exit code 2 and a TOML error naming what to use instead (`bassia web`, or the command line).
- [x] No source file, project file or test references Spectre.Console; `Spectre.Console` and `Spectre.Console.Testing` are removed from the projects.
- [x] `bassia graph -format board` prints the same ASCII board as before; `graph` in the other formats, `component show/set`, `status`, `component split` and the web dashboard (component graph, runs started from a prompt, run pages, triage preview) behave as before.
- [x] `bassia run start -prompt ...` and the dashboard's New run compose the same `-run` command as before.
- [x] Integrations are still performed with `bassia integration start` and previewed in the web dashboard; no code path remains that exists only for the terminal frontend (e.g. the hosted integration start).
- [x] README, command help, the generated `config.toml` comment and code comments no longer mention `bassia ui`.
- [x] `dotnet build` and `dotnet test` pass.

## Approach

- Delete the terminal-only code: `UiCommand`, `InteractiveSession`, `IntegrationPanel`, `IntegrationSupervisor`, `RunBoard`, `IntegrationCommands.StartHostedAsync`, the `ui` entry of the command table, and their tests. `ui` joins `CommandTable.Replaced`, so it fails naming its successor.
- Keep the shared code, moved out of the `Bassia.Ui` namespace so the name no longer suggests a frontend:
  - `ComponentGraph`, `ComponentBoard` (with `ComponentStatus`) and `CharCanvas` to `Bassia/Graph` (`Bassia.Graph`). The board and canvas lose their Spectre markup rendering, styles and selection, which only the terminal used; the canvas keeps a plain Unicode rendering (for tests) and the ASCII rendering.
  - `RunSupervisor`/`RunCard` to `Bassia/Web` (`Bassia.Web`), their only production user. The phase and elapsed-time text move onto `RunCard`.
  - `ComposeCommand` to `RunCommands`, next to `run start -prompt` which uses it.
  - The triage summary line becomes plain text in the dashboard, replacing the Spectre markup it stripped.
- Drop the Spectre.Console packages.
- Update README, help text and comments.

## Decisions

- The shared pieces are kept and moved, not rewritten: their behavior (and the `graph -format board` output) must not change.
- `RunSupervisor` keeps its full API (`ConsumeSettled`, `WhenAllSettledAsync`, etc.) even where only its tests call a member today; trimming it is not part of removing a frontend.
- [bassia-a7b47671](../../done/bassia-a7b47671/SPEC.md) (the terminal frontend, left in `active/` awaiting a manual hand-check) is moved to `done/` with a note that this record removed the frontend; the hand-check no longer applies.
- No replacement for the terminal's integration control panel is added to the web dashboard; performing an integration stays with `bassia integration start`.

## Progress

- [x] Terminal-only code, `ui` command and their tests removed.
- [x] Shared code moved to `Bassia.Graph` / `Bassia.Web`, Spectre.Console removed.
- [x] README, help, config comment and code comments updated.
- [x] Build and tests pass.

## Validation

- `dotnet build Bassia.slnx` — no warnings introduced, no errors.
- `dotnet test Bassia.slnx` — 201 of 203 pass. The two failures (`AgentCommandTests.Agent_ComponentChain_IsCheckedOutSideBySideAndJunctionedIntoItsReferrers`, `ParallelAgentRunEndToEndTests.TwoComponentsClonedFromRemote_...`) fail the same way on the unchanged base when run on Linux, and neither touches the removed code; the 33 tests fewer are the deleted terminal-UI tests.
- `grep -rn "Spectre\|Bassia\.Ui\|bassia ui" Bassia Bassia.Tests README.md` — only the `CommandTable.Replaced` entry for `ui`.
- `bassia help` does not list `ui`; `bassia ui` exits with code 2, naming `bassia web` (`ProgramCliTests.RunAsync_ReplacedCommand_FailsNamingItsSuccessor`).
- `bassia graph -format board` on the same monorepo prints byte-identical output before and after the change.
