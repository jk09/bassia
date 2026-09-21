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

- [ ] Running `bassia` inside an initialized monorepo (a `.bassia` folder is found by walking up, per `Monorepo.FindRoot`) with no subcommand — or a dedicated subcommand, e.g. `bassia ui` — enters a full-screen interactive session; running it outside an initialized monorepo fails the same way the existing subcommands do (non-zero exit, error on stderr) instead of opening the UI.
- [ ] A component view lists every component registered in `components.toml` (name, source URL, outgoing reference edges) as a detailed list, and the user can switch to a directed-acyclic-graph rendering of the same data in the console.
- [ ] From the graph view, the user can export the currently displayed graph to at least one of SVG or Markdown/Mermaid, written to a file path the user provides or confirms; the export renders correctly in a Web browser (SVG) or a Markdown viewer (Mermaid).
- [ ] Selecting a component shows its `git` tree: local branches and tags, with annotated tags distinguishable from lightweight ones (e.g. `agent/run-*` result tags vs. baseline tags).
- [ ] From a component's tag view, the user can create a new annotated tag pointing at an existing branch, tag, or commit of that component, by supplying a tag name and message; the tag is created via `git tag -a` and appears in the view without restarting the tool.
- [ ] The user can start an agentic run by interactively picking one or more `<component>@<tag>` pairs, a prompt, and run parameters (model, context, effort), and the frontend invokes the equivalent of `bassia agent -select <picks> -run <composed command>`; the run's outcome (success, failure, or partial, per `ResultStatus`) and the result tags it produced are shown when it finishes.
- [ ] A run view lists every agentic run recorded in the `.agentic-runs` repo — running, completed, failed, partial, and abandoned — with its `-select` value, status, and per-component result commit/tag.
- [ ] From a component's view, the user can see the reverse mapping: every run that touched that component and the result tag(s) it produced there.
- [ ] Stop/suspend/resume/handoff actions on a run, and any "integrate/merge this run's results" action, are present as menu entries but return a clear "not implemented yet" outcome without altering any component or run state — consistent with Context's note that these are stubbed for now.
- [ ] A `git` command failure (e.g. network error on fetch, or `bassia agent` returning non-zero) surfaces as a visible in-app error message; the frontend does not crash or leave the terminal in a corrupted state (raw mode/alternate screen is always restored on exit, including on error or Ctrl-C).

## Approach

- **Data access**: the frontend reads through the same model the scriptable CLI already uses — `Monorepo.Load` for `components.toml`, `GitClient` for each component's branches/tags, and `RunMetadataStore`/`RunMetadata` for the `.agentic-runs` repo — rather than re-parsing TOML or shelling raw `git` itself. Two read paths don't exist yet and need adding as internal services (usable by the frontend and, later, by scriptable subcommands if wanted): enumerating every run id recorded in `.agentic-runs`, and listing a single component's branches/tags in a structured (non-JSON-line) form.
- **Actions**: every mutating action available in the UI (create an annotated tag, start a run) calls the same code path the equivalent CLI command uses (`git tag -a`, `AgentCommand`/`bassia agent -select ... -run ...`) instead of a parallel implementation, so the interactive tool can never leave the repo in a state the scriptable CLI wouldn't also produce.
- **Console UI**: a full-screen, redrawable console UI (menus, selectors, an ASCII DAG renderer) is needed; the specific toolkit/library is an implementation-time decision, not fixed by this record. The DAG's SVG/Mermaid exporter is a separate, reusable renderer from the console ASCII one, sharing only the graph data model.
- **Entry point**: one new top-level command (bare `bassia` or `bassia ui`) routes into the interactive loop; all existing subcommands keep working unchanged for scripting/CI use.
- **Boundaries**: this record covers observation (components, DAG, git trees, tags) and starting/monitoring agentic runs. Run lifecycle control beyond start (stop/suspend/resume/handoff) and result integration/merge are stubbed only — their real implementation is future work per Context.

## Decisions

- The interactive frontend is a mode of the existing `bassia` executable, not a separate binary or package, so there is one thing to install and one thing to keep in sync with the scriptable command set.
- Mutating UI actions (tag creation, run start) reuse the existing command implementations rather than duplicating git/agent logic, so the interactive and scriptable paths can't drift apart.
- Result integration (AI-assisted merge of an agentic run's outcome) and non-basic run lifecycle actions (stop/suspend/resume/handoff) are explicitly out of this record's acceptance criteria and ship only as stub menu entries; both are called out as future work in Context.
- The frontend introduces no new persistent state: everything it displays is derived from `components.toml`, each component's `git` refs, and the `.agentic-runs` repo, which already exist as of [bassia-8ab66e1d](../../done/bassia-8ab66e1d/SPEC.md) and [bassia-3a5b27a6](../../done/bassia-3a5b27a6/SPEC.md).

## Progress

- [ ] Component list + DAG console view, with SVG/Mermaid export.
- [ ] Component `git` tree view (branches, tags, annotated-tag distinction) and interactive annotated-tag creation.
- [ ] Agentic run start flow wired to the existing `bassia agent -select ... -run ...` path.
- [ ] Run list/detail view reading `.agentic-runs`, including the reverse component → runs lookup.
- [ ] Stub menu entries for run lifecycle control (stop/suspend/resume/handoff) and result integration.

## Validation

- Build a scratch monorepo with `scripts/New-TestMonorepo.ps1` (single component, baseline tag) and launch the frontend against it; confirm the component appears in both the list and DAG views, and that the DAG exports to a file openable in a browser or Markdown viewer.
- From the tag view, create a new annotated tag on the baseline component and confirm `git -C <component> tag -n` lists it as annotated.
- Start an agentic run from the frontend selecting that component/tag with a trivial prompt (mirroring `scripts/Invoke-AgentRun.ps1`); confirm the run appears in the run view with its final status and result tag, and that `git -C <component> log --oneline --decorate` shows the same result commit/tag.
- Confirm the reverse lookup on that component lists the run just started.
- Trigger the stop/suspend/resume/handoff and integration stubs and confirm they report "not implemented" without changing any component or run state (`git status` / `.agentic-runs` tag list unchanged).
- Run `bassia` outside any monorepo folder and confirm it fails with the same error/exit-code convention as other `bassia` commands instead of opening the UI; Ctrl-C during the interactive session leaves the terminal usable afterward.
- `dotnet test` continues to pass, covering any new internal services (run enumeration, structured branch/tag listing) added to support the frontend.