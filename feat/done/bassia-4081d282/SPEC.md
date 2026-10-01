# Select a component's main tip with a bare name

## Outcome

`bassia run start -select app,lib ...` - a component named without an `@<tag|hash>` selector - starts the run from
the tip of that component's default branch (usually `main`). The tip is resolved to its commit when the run starts and recorded as the
run's provenance, so the run record stays reproducible even though the selector itself is mutable.

## Context

Today every `-select` entry must be `<component>@<tag|hash>`; a bare name fails with
`Invalid -select entry 'app': expected <component>@<commit-ish>.`, and branches are rejected on purpose so that a
run only ever starts from an immutable reference. In practice the most common baseline is simply "the latest main",
which forces a `component tag` (or looking up a hash) before every run.

Related records: `bassia-8ab66e1d` (agentic runs), `bassia-0b658261` (select by hash), `bassia-f552a672`
(integration, which already defaults to the component's default branch).

## Acceptance criteria

- [x] A `-select` entry without `@` (e.g. `-select app,lib`) resolves to the commit at the tip of the component's
      main branch in its source-of-truth repo; the run checks that commit out on its run branch as usual.
- [x] Bare and explicit entries can be mixed (`-select app,lib@v1`).
- [x] The run record keeps the selection as given (`select = "app,lib"`) and records, per component, the
      selector it resolved (the branch name) together with the resolved commit hash.
- [x] A component whose source repo has a detached `HEAD` or an unborn default branch fails before anything is materialized, with an error
      naming the component and suggesting `<component>@<tag|hash>`.
- [x] The full-closure requirement is unchanged: a bare entry still has to be listed for every referenced
      component.
- [x] `<component>@` (empty selector) and `@<x>` still fail as invalid entries; tag and hash selection behave as
      before.
- [x] `-detach` reports a missing default branch straight away, like any other selection error.
- [x] Help text and README describe the bare-name form.

## Approach

Make the commit-ish optional in `ComponentSelection` for `-select` only (a bare name means "default-branch tip"; `integration start -onto` keeps requiring `@`), and resolve it in
`AgentCommand.ResolveSelectionAsync` before the tag/hash rules: read the branch from the source repo's `HEAD` (`symbolic-ref`), resolve it to
a commit with `rev-parse --verify refs/heads/<branch>^{commit}`, and record the branch name as the component's
`commit_ish`. Everything downstream already works with the resolved commit.

## Decisions

- "Main branch" means the component's default branch - the branch its source-of-truth repo's `HEAD` names - not
  literally `main`; this is the rule `integration start` already uses for its base, and works for `master`/`trunk`.
- The full-closure requirement stays: a referenced component is never pulled in implicitly, even at its tip; a bare
  name per component is enough (`-select app,lib`).
- `-select a, b` (space after the comma, so two shell arguments) is not absorbed; write `-select a,b`,
  `-select "a, b"` or repeat the switch.
- A bare name does not weaken reproducibility: the resolved commit is recorded as the run's provenance, the branch
  name as its selector. `<component>@<branch>` stays rejected so a pinned-looking selector is never mutable.

## Progress

- [x] Selection parsing and resolution
- [x] Tests
- [x] Help and README

## Validation

- `dotnet test Bassia.slnx --filter "Category!=EndToEnd"`: 256 passed, 1 skipped. `SelectDefaultBranchTests` covers
  bare and mixed selection, a non-`main` default branch, unborn and detached `HEAD`, the closure rule, `-detach`,
  and the recorded provenance; `SelectionParsing_RejectsMalformedAndDuplicateEntries` covers parsing.
- The two `Category=EndToEnd` tests clone `jk09/example` from GitHub and fail identically without this change where
  that clone is not reachable.
- `bassia help run start` shows the `component[@tag|hash]` form and the bare-name example.
