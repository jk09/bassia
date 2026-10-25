# Component templates, conformance rules and owner review

## Outcome

A new component can start from a template kept in the meta-repo (`bassia component create -template <t>`). The template
gives it starter files, tags, visibility, exports and owners, so it follows the monorepo's conventions from its
first commit. Conformance rules in `components.toml` say what components with given tags must have: files, tasks,
owners, exports. `bassia conformance check` reports where they fall short, and the dashboard shows each component's
boundary and conformance findings. Owners take part in the integration: with `merge.owner_review = manual`, a result
in a component with owners is left for a human, and the integration names the owners to ask.

## Context

Steps 7–8 of the nx-style enforcement plan. Builds on `bassia-b7f7b692` (tags, visibility, owners, boundary rules),
`bassia-50c0f9a8` (exports, code boundaries) and `bassia-ff58a69e` (tasks). Until now `component create` made an empty
component that followed no convention, nothing checked that components had what the monorepo expects of them, and
`owners` was only recorded. The meta-repo (`.bassia`) already holds `skills/`. Templates sit next to them, versioned
with the registry.

## Acceptance criteria

- [x] A template is a folder `.bassia/templates/<name>/` with a `template.toml` (optional `description`, `tags`,
      `visibility`, `exports`, `owners`, `references`) and the files to start with, in a `files/` subfolder. In file
      contents and paths, `{{name}}` becomes the new component's name.
- [x] `bassia template list` lists the templates with their description and defaults.
- [x] `bassia component create -name <c> -template <t>` creates the component with its empty first commit and a second
      commit "Apply template <t>" holding the template's files. It registers the template's tags, visibility, exports,
      owners and references, and switches given on the command line win. An unknown template, or a template that does
      not parse, fails before anything is created. Boundary rules apply as to any new component.
- [x] `[[conformance]]` tables in `components.toml` hold the rules: `components` (a tag pattern, as in boundary rules),
      and any of `files` (paths that must exist at the tip), `tasks` (tasks its `bassia.toml` must define),
      `owners = true`, `exports = true`, plus `description`. Invalid rules are rejected naming the rule.
- [x] `bassia conformance list | add | remove | check [-name <c>] [-strict]` manage and check the rules. `check` reads
      each matching component at the tip of its default branch and lists every unmet requirement; `-strict` fails on
      any.
- [x] The dashboard's components table shows each component's tags and its number of boundary and conformance
      findings. A component's page shows its tags, visibility, exports and owners, and lists its findings.
- [x] `merge.owner_review = off | manual` (per component; default `off`): with `manual`, a step that would change a
      component with owners is left for a human, with a note naming the owners. Each integrated component records its owners
      (`owners` in `integration.toml`), and `integration plan`/`show` report them.
- [x] Documented in `docs/reference.md` and the help.

## Approach

- `Core/Templates`: read `.bassia/templates`. Applying a template is done through the storage contract: check out the
  new component's first commit, write the files, commit and publish the default branch.
- `Core/Conformance`: the rules (`ConformanceRule`) are stored in the registry like boundary rules
  (`RegistryChange.AddConformance`/`RemoveConformance`, `[[conformance]]` blocks). The checker reads files and tasks
  through `IComponentStore`.
- Integration: `MergePolicy.OwnerReview` in the planner. `IntegrationStep.Owners` is recorded and shown.

## Decisions

- Templates live in the meta-repo, not in a component, so they are versioned with the conventions they encode and
  need no checkout to use.
- Conformance is reported, never enforced on change: what a component holds changes through runs, and the
  integration's gate (`merge.verify`) or CI (`conformance check -strict`) is where to hold a result back.
- The owners are recorded per integrated component rather than per step: they belong to the component, and every
  step of it shares them.
- Starter files are committed through a working copy on the component's default branch; the git store's checkout
  now uses `git checkout -B`, so a working copy can continue the branch the clone already has.
- Owner review is a merge policy like `merge.semantic = manual`, rather than an approval mechanism: Bassia has no
  identities, so the step is left for a human and the owners are named.

## Progress

- [x] Templates and `component create -template`
- [x] Conformance rules, storage and check
- [x] Owner review in the integration
- [x] Dashboard, docs and help

Validated: `dotnet build` clean; `dotnet test` 526 passed, 1 skipped (as before), including `Core/ConformanceTests`
(rules, checks, starter files, owner review on the memory storage), `TemplateAndConformanceCommandTests` (git:
templates, conformance add/check/remove, owner review in the plan) and the dashboard's findings test.

## Validation

- `dotnet build`, `dotnet test` (new `ConformanceTests`, template and owner-review tests on memory and git).
- By hand: a `lib` template with `README.md` and `bassia.toml`; `component create -name core -template lib` gives a
  two-commit component with the template's tags; a rule `components = "type:lib"`, `tasks = ["test"]` reports a lib
  without a test task.
