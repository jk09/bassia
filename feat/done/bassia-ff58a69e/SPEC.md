# Component tasks, affected components and the integration's verification gate

## Outcome

Each component can define tasks (`build`, `test`, `lint`, ...) in a `bassia.toml` at its root, with nx-style
dependencies (`depends_on = ["^build"]`: the same task in the components it references first). Bassia runs them over a
checkout of the selected components (`bassia task run`), inside an agentic run's folder, or as the integration's gate.
The gate runs the tasks `merge.verify` names on every component the integration changed and on every component that
depends on one of them, before anything is published. `bassia affected` tells which components a set of runs (or
components) affects.

## Context

Step 6 of the nx-style enforcement plan. Components had no build or test definitions. The integration merged and tagged
results without building or testing them, which matters more with an LLM resolver. Runs materialize a selection into a
folder with each reference nested as a junction (`AgentRuns.MaterializeAsync`). The integration merges each component
in its own checkout under `.workspace/<integration-id>/<component>` and publishes per component
(`IntegrationRunner.Session`). Caching of task results is left for later.

## Acceptance criteria

- [x] A component's `bassia.toml` holds `[tasks.<name>]` tables with `command` and optional `depends_on`. An entry
      `x` means this component's task `x` runs first; `^x` means task `x` of every component in its closure that defines
      it runs first. The file is read from the store at a commit, or from a folder. An invalid file, an unknown
      dependency or a cycle is an error that names the component and task.
- [x] `bassia task list [-name <c>]` lists each component's tasks at the tip of its default branch.
- [x] `bassia affected (-runs <ids>|all | -components <c,...>)` lists the changed components and every component that
      references one of them, transitively, in dependency order. It also gives the `-select` value that covers their
      closure.
- [x] `bassia task run -task <t,...> [-select <c[@ref],...>] [-keep]` materializes the selection's closure into a
      workspace folder, the same layout a run gets. It runs the tasks of the selected components in dependency order,
      with their dependencies, and stops at the first failure. It reports every task with its exit code, duration and
      log file, and removes the folder unless `-keep` (or a failure). Inside a run folder (`BASSIA_RUN_DIR`) and
      without `-select`, it runs there on the run's own checkout.
- [x] `merge.verify` (a list of task names; per component; default empty) gates the integration. After the merges and
      before publishing, the integration checks out the base tip of every affected component it did not change, nests
      the references, and runs those tasks on the affected components. If one fails, nothing is published: the
      integrated components are `failed` with the failing task named, the integration is `partial`, and the
      verification is recorded in `integration.toml` (`[[verification]]`: component, task, exit code, log). If all pass,
      it publishes as before. Without `merge.verify`, nothing changes.
- [x] A component without `bassia.toml` has no tasks. A task asked for but defined nowhere in the selection is an error.
- [x] Documented in `docs/reference.md` and the help.

## Approach

- `Core/Tasks`: `TaskFile` (parse, read from store or folder), `TaskPlan` (the ordered `(component, task)` list for
  targets over a registry), `TaskRunner` (runs a plan in a folder through an injectable shell runner, logs per task).
- `Core/Affected`: changed components plus transitive referrers, in dependency order.
- `Core/Workspaces`: materializing a closure (checkouts plus nested junctions), taken out of `AgentRuns` so that
  runs, `task run` and the integration share it.
- Integration: `IntegrationRunner` merges every component first, then verifies (when any affected component has
  `merge.verify`), then publishes. `IntegrationSettings.RunTask` is injectable for tests; `MergePolicy.Verify` is
  read from config.

## Decisions

- Tasks live with the component's code (`bassia.toml`, like nx's `project.json`), so a task definition is versioned
  with what it builds and an agent can change it in a run; the integration reads them from the checkout it verifies.
- The gate covers the affected set, not only the changed components: a change to `lib` must not break `app`, which
  references it.
- A failed gate publishes nothing of the integration. Publishing only the components that passed would advance a
  combination nobody verified.
- Task results are not cached yet. A cache keyed by the component's commit and its closure's commits is possible
  later.

## Progress

- [x] Tasks, plans, affected, workspaces with unit tests
- [x] `task list|run`, `affected` CLI
- [x] Integration gate
- [x] Docs and help

Validated: `dotnet build` clean; `dotnet test` 509 passed, 1 skipped (as before), including `Core/TaskTests`
(parsing, plans, affected, runner, the gate passing and failing on the memory storage) and `TaskCommandTests` (git and
real shell tasks: `task list`, `task run` passing and failing, `affected`, a failing `merge.verify` gate that
publishes nothing).

## Validation

- `dotnet build`, `dotnet test` (new `TaskTests` on the memory storage, CLI tests on git).
- By hand: `lib` and `app` with `bassia.toml` tasks; `bassia task run -task test -select app` runs `lib:build`,
  `app:build`, `app:test`; an integration with `merge.verify = test` whose run breaks `app`'s test publishes nothing.
