# Component fork

## Outcome

`bassia component fork` replaces branching. Every component has a single main branch; to work in parallel, a component is forked into a new registered component that carries the parent's main-branch history up to the fork point and then evolves on its own main branch.

## Context

Bassia components are bare git repos registered in `.bassia/components.toml`. `bassia branch` was already removed (see `CommandTable.Replaced`); this feature provides the replacement. Forks are ordinary components, so runs, tags, integration and the graph work on them unchanged.

## Acceptance criteria

- [ ] `bassia component fork -name <component> -as <fork> [-ref <commit-ish>] [-url <url>] [-references <c[:path]>,...]` creates a bare repository for `<fork>` next to the meta-repo and registers it.
- [ ] The fork has exactly one branch, named like the parent's main branch, pointing at the fork point (the parent's main-branch head, or `-ref`); the full history up to it is retained with identical commit ids.
- [ ] Tags on that history are carried over; run and integration tags of the parent, and tags beyond the fork point, are not.
- [ ] The fork is independent: no `origin` pointing at the parent, and later commits in either component do not affect the other.
- [ ] `components.toml` records `fork_of` and `fork_commit`; references default to the parent's and `-references` / `-url` override or add to them. The parent and its referrers are unchanged.
- [ ] `component list` and `component show` report the fork's origin.
- [ ] Failure: unknown parent, name already registered or directory existing, invalid name, unresolvable `-ref`, unregistered reference; nothing is left behind and the meta-repo is not changed.
- [ ] The meta-repo is committed once. `bassia branch` now points users to `component fork`.
- [ ] Documented in `docs/reference.md` and help.

## Approach

`ComponentCommands.ForkAsync` clones the parent single-branch and bare, pins the branch to the fork point, drops foreign tags and the `origin` remote, then registers the component via `ComponentsFile.Add` (extended with optional lineage keys) and commits the meta-repo. Failure removes the new directory.

## Decisions

- Command is `component fork` to match the existing `<noun> <verb>` grammar.
- A fork has no upstream URL unless `-url` is given (then it becomes `origin` and the registered `url`).
- Referrers of the parent are not repointed: a fork is a new variant, adopted via `component set`.
- Merging a fork back goes through the existing run/integration flow; no new merge command here.

## Progress

- [x] Implementation and help
- [x] Tests written
- [ ] Tests executed (no .NET SDK in the authoring environment; CI must run them)

## Validation

`dotnet test` (see `ComponentForkTests`).
