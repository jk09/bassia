# Code-level boundaries: exports, detected dependencies, enforcement in runs and integration

## Outcome

Boundaries reach into the code, not only the component graph. A component can declare which of its paths are its
public API (`exports`). Bassia reads the components' code for path-based dependencies: relative imports and includes,
project references, `file:` packages and CMake subdirectories. It reports three things:

- uses of a referenced component's non-exported paths
- uses of a component reached only through another component's nested reference (indirect use)
- paths that leave the component's root

It also reports declared references the code never uses. The checks run on demand (`boundary check -code`), after
every agentic run (new violations are recorded with the run), and in the integration (new violations are recorded with
the step; `merge.boundaries = manual` leaves them for a human). An agent learns the boundaries of its run from the run
folder's `AGENTS.md`/`CLAUDE.md`.

## Context

Steps 3–5 of the nx-style enforcement plan; builds on `feat/done/bassia-b7f7b692` (tags, visibility, boundary rules).
A run nests each referenced component as a junction at its reference path, so a referrer sees *all* of a referenced
component, including the components *that* one nests (`app/lib/vendor/core`). Nothing stopped `app` from using
`lib`'s internals or reaching `core` through `lib` without declaring it. Integration policies live in
`Integration/MergePolicy` (`merge.*`, per component); run records in `RunMetadata`; the lock guide is the run
folder's `AGENTS.md`/`CLAUDE.md` (`AgentCommand.WriteLockGuide`).

## Acceptance criteria

- [x] `[[component]]` accepts `exports = [patterns]` (the split plans' path patterns: `**`, `*`, `?`, folders match
      what is below them, `!` excludes); `component add|create|fork|set` take `-exports`, `set` also `-clear-exports`;
      `list`/`show` report them. Without `exports`, everything is exported.
- [x] The storage contract can read a file at a commit (`IComponentStore.ReadFileAsync`), on git and in memory.
- [x] A dependency scanner finds path-like dependencies in a component's files at a commit:
      JS/TS (`import ... from`, `import(...)`, `require(...)`, `export ... from`; relative specifiers only), C/C++ quoted
      `#include`, MSBuild `Include`/`Project` attributes in `*.*proj`, `*.props`, `*.targets`, `package.json`
      `file:` dependencies, and CMake `add_subdirectory`/`include`. Each is resolved against the file's folder in the
      component's namespace, where each reference is nested at its path.
- [x] Code violations, each naming the component, file, line, specifier and target:
      `private` (a path of a direct reference that its `exports` do not cover), `indirect` (a component reached through
      another's nested reference, e.g. `lib/vendor/core/...`), `outside` (a path above the component's root). Declared
      references no scanned dependency uses are listed as `unused_references`, as information only.
- [x] `bassia boundary check -code [-name <c>] [-strict]` also scans every component at the tip of its default branch.
      It reports code violations and unused references next to the graph violations; `-strict` fails on any violation.
- [x] After an agentic run publishes a component's result, the result is scanned. Code violations not present at the
      run's base commit are recorded in `run.toml` (`[[component.boundary_violation]]`) and named in the run's
      completion message. `agent show` reports them. The run still completes.
- [x] When the selection has rules, tags, visibility or exports, the run folder's `AGENTS.md`/`CLAUDE.md` explains
      the boundaries of the selected components (what each may reference, what each exports), alongside the lock guide
      when there is one.
- [x] The integration records, per step, the code violations its result adds over the base (`boundary_violations`
      in `integration.toml`, `integration plan`/`show`). `merge.boundaries = warn` (default) only records them;
      `manual` leaves the step for a human; both can be set per component. The semantic brief lists them.
- [x] A monorepo without exports or rules behaves as before, apart from the extra (empty) checks.
- [x] Documented in `docs/reference.md` and the help.

## Approach

- `Core/CodeBoundaries`: the scanner (detectors per file kind, resolution in the component namespace) and the checker
  over a registry, producing `CodeViolation`s and used references. It reads files only through `IComponentStore`.
- `ComponentDefinition.Exports` with a `RegistryChange.SetExports`, stored like the other component lists.
- Runs: `AgentRuns` scans after `FinalizeComponentAsync` publishes. It compares with the base commit and stores the new
  violations on `ComponentRun`. The run guide writer composes the lock and boundary sections.
- Integration: `IntegrationPlanner` scans each candidate step's source commit against the base. The policy comes from
  `MergePolicy.Boundaries`.

## Decisions

- Exports are enforced by analysis, not by junctioning only the exported paths. A nested component must stay complete
  so that it builds and so that the run can commit it as a whole; analysis gives the same signal without breaking that.
- Only path-based dependencies are detected. Namespace or module imports (C# `using`, Python, Go, Java) do not name a
  folder, so they cannot be attributed to a component without a language toolchain. Detectors are per file kind and
  can be added later.
- An indirect use is a violation even when the referrer also references the target directly. It should use its own
  nested copy, so that each reference stays at the version the referrer selects.
- Unused references are information, not violations: detection is incomplete (namespace imports), so a reference with
  no detected use may still be needed.
- Runs only record violations, so an agent's work is never lost. The integration is where policy can hold a result
  back, like `lock.conflict`.

## Progress

- [x] Exports and file reading in the storage contract
- [x] Scanner and checker with unit tests
- [x] `boundary check -code`, run recording and guide, integration policy
- [x] Docs and help

Validated: `dotnet build` clean; `dotnet test` 496 passed, 1 skipped (as before), including `Core/CodeBoundaryTests`
(scanner, resolution, checker, runs and integration on the memory storage) and the code-boundary cases of
`BoundaryCommandTests` (git: `boundary check -code -strict`, run record, run guide, `merge.boundaries = manual`).

## Validation

- `dotnet build`, `dotnet test` (new `CodeBoundaryTests` over the memory storage, CLI tests over git).
- By hand: `app -> lib -> core` with `lib` exporting `src/api`; a file in `app` importing `./lib/src/internal/x` and
  `./lib/vendor/core/y` gives a `private` and an `indirect` violation in `boundary check -code`, in a run's record
  and in the integration plan.
