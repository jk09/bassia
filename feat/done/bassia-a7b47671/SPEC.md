# Command line frontend for monorepo management

## Outcome

The user will have access to an interactive command line frontend which will enable them, in a user-friendly way, to manage frequent activities such as component dependency observation, `git` tree/tag inspection, starting agentic runs, and monitoring their outcomes. Result *integration* (AI-assisted merge of an agentic run's outcome) is out of scope for this record's acceptance criteria — Context explains why — but the frontend exposes a stub entry point for it.

## Context

<!-- Explain the problem, relevant constraints, and links to dependent feature records or external issues. -->

The monorepo consists of potentially many components with complex dependencies expressed by a directed acyclic graph. Each component contains many tags representing baseline state as well as the outcomes of agentic runs. We need a user-friendly frontend (user being a human here) to monitor the components and agentic runs.

Today `bassia` is a non-interactive, scriptable CLI (`status`, `log`, `branch`, `commit`, `init`, `add-component`, `agent [retry|abandon]`; see [bassia-d3226a2c](../../done/bassia-d3226a2c/SPEC.md) and [bassia-8ab66e1d](../../done/bassia-8ab66e1d/SPEC.md)) that prints JSON results and has no notion of "list every component" or "list every agentic run" — a caller who wants that today has to read `components.toml` and walk the `.agentic-runs` bare repo's tags directly, the way `RunMetadataStore` does internally. This record adds an interactive layer on top of (not instead of) that scriptable core.

We need an interactive command line frontend tool which will be able to display and interact with the list of components, agentic runs, and  associated actions in the console, using ASCII art graphics, selectors etc., in the spirit of Claude or Copilot command line tools.
The tool should be started and provide intuitive choice selectors for different actions, such as:

### Component display

The frontend displays the components as a detailed list, or as a directed acyclic graph. An export to at least one of SVG or Markdown Mermaid should be possible in order to display the graph in a Web browser or a Markdown viewer.

The `git` tree of each component should be displayed, along with references such as branches and tags.
Especially the list of annotated tags for each component should be accessible, to be used in the agentic runs. The user should be able to create a new annotated tag using existing references.

### Agentic run startup

The user should be able to interactively select one or more components and their tags, and provide an agentic prompt (along with parameters such as the AI model, context, effort etc.), whereupon the agentic run commences using `Bassia` infrastructure (the command `bassia agent -select ... -run ...`). The information about the agentic run will be accessible in the frontend, in a manner similar to command line agentic tools such as Claude. The user will be able to stop, suspend, resume, or handoff the agentic run (non-basic functionality can be stubbed for now). The list of all running and completed agentic runs will be available, along with generated tags in respective components. Prospectively, we want to be able to interpret the monorepo in terms of agentic runs replacing the role of `git` branches in the sense of each agentic run being a flow branching off a certain component tag, proceeding for a few steps (due to user interactive input to the agent), and possibly being integrated with outcomes of other agentic runs. The integration is an analogue of `git` merge and will be the subject of future work, using AI-assisted merging taking into consideration not only `git` diffs, but also the semantics of the agentic run outcomes; this frontend only stubs an entry point for it. Each agentic run can be represented as a flow by virtue of the annotated, lineage-counted tags already produced by `bassia agent` (`agent/run-<id>/<lineage>` in the run-metadata repo, and the same run's result tags pushed to the top-level component repos — see [bassia-8ab66e1d](../../done/bassia-8ab66e1d/SPEC.md) and [bassia-3a5b27a6](../../done/bassia-3a5b27a6/SPEC.md)). The frontend tool will provide not only the list of agentic runs and their relation to components, but also the reverse information: how any given component has been affected by any agentic run.

## Acceptance criteria

- [x] Running `bassia ui` inside an initialized monorepo (a `.bassia` folder is found by walking up, per `Monorepo.FindRoot`) enters a full-screen interactive session; running it outside an initialized monorepo fails the same way the existing subcommands do (non-zero exit, JSON error on stderr) instead of opening the UI, and so does running it without an interactive terminal.
- [x] A component view lists every component registered in `components.toml` (name, source URL, outgoing reference edges) as a detailed list, and the user can switch to a directed-acyclic-graph rendering of the same data in the console.
- [x] From the graph view, the user can export the currently displayed graph to Markdown/Mermaid and to SVG, written to a file path the user provides or confirms (default `<root>/components.md` / `components.svg`); the export renders correctly in a Web browser (SVG) or a Markdown viewer (Mermaid).
- [x] Selecting a component shows its `git` tree: local branches and tags, with annotated tags distinguishable from lightweight ones (e.g. `agent/run-*` result tags vs. baseline tags).
- [x] From a component's tag view, the user can create a new annotated tag pointing at an existing branch, tag, or commit of that component, by supplying a tag name and message; the tag is created via `git tag -a` and appears in the view without restarting the tool.
- [x] The user can start an agentic run by interactively picking one or more components (the reference closure is completed automatically) and an annotated tag for each, a prompt, and run parameters (model, effort, context), and the frontend invokes the equivalent of `bassia agent -select <picks> -run <composed command>`; the run's outcome (success, failure, or partial, per `ResultStatus`) and the result tags it produced are shown when it finishes.
- [x] A run view lists every agentic run recorded in the `.agentic-runs` repo — started, completed, failed, partial, and abandoned — with its `-select` value, status, and per-component result commit/tag.
- [x] From a component's view, the user can see the reverse mapping: every run that touched that component and the result tag(s) it produced there.
- [x] Stop/suspend/resume/handoff actions on a run, and an "integrate results" action, are present as menu entries but return a clear "not implemented yet" outcome without altering any component or run state — consistent with Context's note that these are stubbed for now.
- [x] A `git` command failure (e.g. a bad tag target, or `bassia agent` failing) surfaces as a visible in-app error message and the session continues; the frontend does not enter raw mode or an alternate screen, and the cursor is restored on exit, including on Ctrl-C.

## Approach

- **Data access**: the frontend reads through the same model the scriptable CLI already uses — `Monorepo.Load` for `components.toml`, `GitClient` for each component's branches/tags, and `RunMetadataStore`/`RunMetadata` for the `.agentic-runs` repo — rather than re-parsing TOML or shelling raw `git` itself. Two read paths don't exist yet and need adding as internal services (usable by the frontend and, later, by scriptable subcommands if wanted): enumerating every run id recorded in `.agentic-runs`, and listing a single component's branches/tags in a structured (non-JSON-line) form.
- **Actions**: every mutating action available in the UI (create an annotated tag, start a run) calls the same code path the equivalent CLI command uses (`git tag -a`, `AgentCommand`/`bassia agent -select ... -run ...`) instead of a parallel implementation, so the interactive tool can never leave the repo in a state the scriptable CLI wouldn't also produce.
- **Console UI**: a full-screen, redrawable console UI (menus, selectors, an ASCII DAG renderer) built on Spectre.Console (`SelectionPrompt`/`MultiSelectionPrompt`/`TextPrompt`, tables, panels), driven through `IAnsiConsole` so tests script it with `Spectre.Console.Testing`. The DAG's SVG/Mermaid exporters are separate, reusable renderers from the console text one, sharing only the graph data model (`ComponentGraph`).
- **Entry point**: one new top-level command, `bassia ui`, routes into the interactive loop (`UiCommand` → `InteractiveSession`); all existing subcommands keep working unchanged for scripting/CI use.
- **Boundaries**: this record covers observation (components, DAG, git trees, tags) and starting/monitoring agentic runs. Run lifecycle control beyond start (stop/suspend/resume/handoff) and result integration/merge are stubbed only — their real implementation is future work per Context.

## Decisions

- The interactive frontend is a mode of the existing `bassia` executable, not a separate binary or package, so there is one thing to install and one thing to keep in sync with the scriptable command set.
- Mutating UI actions (tag creation, run start) reuse the existing command implementations rather than duplicating git/agent logic, so the interactive and scriptable paths can't drift apart.
- Result integration (AI-assisted merge of an agentic run's outcome) and non-basic run lifecycle actions (stop/suspend/resume/handoff) are explicitly out of this record's acceptance criteria and ship only as stub menu entries; both are called out as future work in Context.
- The frontend introduces no new persistent state: everything it displays is derived from `components.toml`, each component's `git` refs, and the `.agentic-runs` repo, which already exist as of [bassia-8ab66e1d](../../done/bassia-8ab66e1d/SPEC.md) and [bassia-3a5b27a6](../../done/bassia-3a5b27a6/SPEC.md).
- The entry point is `bassia ui`, not bare `bassia`: a bare invocation keeps printing the help, so scripts and CI that probe the CLI never get a prompt waiting for input. `ui` refuses to start when stdin/stdout are redirected, with the usual JSON error, for the same reason.
- Spectre.Console is the console toolkit. It gives the selectors, tables and prompts the record asks for without a windowing model of its own, keeps the terminal in cooked mode (no raw mode or alternate screen to restore on a crash), and its `IAnsiConsole` abstraction plus `Spectre.Console.Testing` let xUnit drive the whole session with scripted input. Menus have typed search enabled, so a choice is reachable by name rather than by arrow-key position, which also keeps the tests independent of menu order.
- `AgentCommand.StartAsync` was split into a shared `StartRunAsync` that returns an `AgentRunOutcome` and a CLI wrapper that serializes it, so the frontend runs the exact same sequence and renders the outcome instead of parsing the CLI's JSON. The agent's own output and `bassia:` progress lines stream to the terminal as they do in the CLI; the outcome is printed below them, not on a cleared screen.
- The run's `-run` value is composed as `<agent command> [--model X] [--effort Y] "<prompt> Context: <context>"` and shown in an editable prompt before the run starts, so a flag the chosen agent CLI does not support can be removed there. The context is joined with a space, not a newline, because the value is one shell command line.
- `ListLatestAsync` enumerates runs from the `agent/run-*/*` tags of the metadata repo and loads each run's latest lineage; there is no index to keep in sync, matching the plumbing-only store.
- `retry` and `abandon` are not surfaced in the UI; a `partial` run shows the CLI commands to use. Wiring them is straightforward but outside this record's criteria.
- Superseded: the terminal frontend was removed by [bassia-4df7706a](../bassia-4df7706a/SPEC.md) in favour of the command line and the web dashboard. The manual hand-check under **Validation** no longer applies; the parts other commands use (`ComponentGraph`, the component board, `RunSupervisor`) were kept and moved.

## Progress

- [x] Component list + DAG console view, with Markdown/Mermaid and SVG export (`Ui/ComponentGraph.cs`, `Ui/InteractiveSession.cs`).
- [x] Component `git` tree view (branches, tags, annotated-tag distinction via `Git/GitRef.cs`) and interactive annotated-tag creation.
- [x] Agentic run start flow wired to the existing `bassia agent -select ... -run ...` path (`AgentCommand.StartRunAsync`, `AgentRunOutcome`).
- [x] Run list/detail view reading `.agentic-runs` (`RunMetadataStore.ListLatestAsync`), including the reverse component → runs lookup.
- [x] Stub menu entries for run lifecycle control (stop/suspend/resume/hand off) and result integration.
- [x] `bassia ui` entry point with the outside-monorepo and non-interactive guards; help and README updated.
- [x] xUnit coverage: graph renderers, ref listing, run enumeration, `ui` guards, and scripted sessions for every flow above (components, graph + both exports, tag creation, run list + stubs, reverse lookup, run start against a real `bassia agent` run, missing-tag refusal, in-app git error).

## Validation

- `dotnet test` — 64 tests pass, including the 25 added by this record (`Bassia.Tests/Ui/*`, `Git/GitRefTests.cs`, `RunMetadataStoreTests.cs`) which script the session through `Spectre.Console.Testing.TestConsole` and check the resulting repo state with `git`.
- `Bassia.exe -C C:\Windows\Temp ui` — JSON error `is not inside a Bassia monorepo`, exit code 1. `Bassia.exe -C <fresh monorepo> ui` with redirected output — JSON error `needs an interactive terminal`, exit code 1. `Bassia.exe help` lists `ui`.
- Hand-check in a real terminal (the automated checks cover the same flows through a scripted console; do this once to see the rendering): `$r = ./scripts/New-TestMonorepo.ps1`, then `.\Bassia\bin\Debug\net10.0\Bassia.exe -C $r ui`:
  - *Components* → *Show dependency graph* → export both formats; open `components.svg` in a browser and `components.md` in a Mermaid-capable Markdown viewer.
  - *Open example* → *Create annotated tag* on `main`; `git -C $r\example cat-file -t <tag>` prints `tag` (annotated) and the view already shows it.
  - *Start an agentic run* → `example@<tag>` with a trivial prompt (the default agent command mirrors `scripts/Invoke-AgentRun.ps1`); the outcome lists the result tag, and `git -C $r\example log --oneline --decorate` shows the same commit/tag. *Open example* then lists the run under *Agentic runs touching this component*.
  - *Agentic runs* → open the run → each of *Stop*, *Suspend*, *Resume*, *Hand off*, *Integrate results* reports "Not implemented yet"; `git -C $r\.agentic-runs\.git tag --list` is unchanged.
  - Ctrl-C at any prompt exits with the cursor visible and the terminal usable.