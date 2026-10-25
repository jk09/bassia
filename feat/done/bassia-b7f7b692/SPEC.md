# Component tags, visibility and boundary rules

## Outcome

The meta-repo enforces the monorepo's architectural decomposition the way nx's module boundaries do. Each component can
carry tags (`type:lib`, `scope:billing`), a visibility (who may reference it) and owners. Boundary rules in
`components.toml` say which tagged components may reference which, with wildcards. A change to the graph that adds a
violation is refused. A violation already in the graph, from a hand edit or from a rule added later, is reported as a
warning. `bassia boundary check` lists every violation.

## Context

Until now the component graph's only rules were valid names, registered references and no cycles
(`Core/ComponentRegistry`). Any component could reference any other. This is steps 1–2 of the plan to close the gap to
nx/Bazel-style architectural enforcement; later steps (public API, dependency detection, enforcement in runs and
integration, tasks, generators, ownership) build on these tags and rules.
`ComponentRegistry` is the immutable, validated graph; `MetaRepo` checks every `RegistryChange` against it before an
`IMetaStore` persists it (`Storage/Git/GitMetaStore` edits `components.toml` line by line, `Storage/Memory` keeps it in
memory).

## Acceptance criteria

- [x] `[[component]]` entries in `components.toml` accept `tags = [...]`, `visibility = [...]` and `owners = [...]`; they
      are read, written, kept by every registry change, and reported by `component list` and `component show`.
- [x] `[[boundary]]` tables in `components.toml` hold the rules: `source` (a tag pattern), `allow` and/or `deny` (tag
      patterns), and an optional `description`. Patterns use `*` (any characters) and `?` (one character). `$1`, `$2`, ...
      in `allow`/`deny` stand for what the source pattern's wildcards matched. Every component also has the implicit tag
      `component:<name>`.
- [x] Rule semantics: a reference `A -> B` is checked against every rule whose `source` matches a tag of `A`, once per
      matching tag. With `allow`, some tag of `B` must match an allow pattern. With `deny`, no tag of `B` may match a deny
      pattern. A component with `visibility` may be referenced only by components with a tag that matches one of its
      visibility patterns; without `visibility` it is public, and an empty list makes it private.
- [x] `component add`, `component create`, `component fork` (default: the parent's tags, visibility and owners) and
      `component set` take `-tags`, `-visibility` and `-owners`; `component set` also takes `-clear-tags`, `-public`,
      `-private` and `-clear-owners`, and applies everything it is given as one revision, refused as a whole.
- [x] A registry change (add, create, fork, set references, set tags or visibility, split) that would add a violation
      not already in the graph is refused with every new violation named, and nothing changes. Violations already
      present are kept and reported as `warnings`. Unwinding records submodule structure as it finds it: its new
      violations are reported as warnings, not refused.
- [x] Split parts take the source's tags, visibility and owners unless a `[[part]]` sets its own `tags`,
      `visibility` or `owners`.
- [x] `bassia boundary list | add | remove | check`: list the rules, add one (`-source`, `-allow`, `-deny`,
      `-description`), remove one by `-index`, and check the whole graph. Adding or removing a rule is never refused for
      the violations it reveals; it reports them as warnings. `boundary check` exits 0 with the violations listed;
      with `-strict`, it fails when there is any.
- [x] Invalid rules (no `source`, neither `allow` nor `deny`, a `$n` beyond the source's wildcards, a non-string
      pattern) are rejected when read or added, with a message naming the rule.
- [x] `status` and `graph` report the number of violations. A monorepo without tags and rules behaves exactly as before.
- [x] Documented in `docs/reference.md` and the CLI help.

## Approach

- `Core/Boundaries`: tag patterns (glob with captures, `$n` substitution), `BoundaryRule`, and a checker that
  returns `BoundaryViolation`s (referrer, referenced, kind = rule|visibility, the rule, a message) for a registry.
- `ComponentDefinition` gains `Tags`, `Visibility` and `Owners`. `ComponentRegistry` gains `Boundaries` and
  `Violations()`. New `RegistryChange`s are `SetTags`, `SetVisibility`, `SetOwners`, `AddBoundary` and `RemoveBoundary`.
- `MetaRepo.CommitAsync` compares the violations before and after the change. New violations from a graph change throw
  `BoundaryViolationException`, unless the caller allows them (unwind) or the change is a rule change. It returns the
  revision and the warnings.
- `IMetaStore` loads the rules next to the components. `GitMetaStore`/`ComponentsFile` edit the new keys and
  `[[boundary]]` blocks line by line. `MemoryMetaStore` keeps them in memory.
- CLI: `boundary` commands in `CliCommands/BoundaryCommands.cs`; new switches on the component commands; `warnings`
  in the results of commands that change the graph.

## Decisions

- Rules live in `components.toml` (user's decision), as `[[boundary]]` tables next to `[[component]]`, so the
  meta-repo's history versions the policy together with the graph.
- Existing violations warn (user's decision); new ones from a graph change are refused. A rule change is never refused,
  so a rule can be introduced into a monorepo that does not yet follow it, and from then on no new violation can be
  added.
- Rules allow wildcards (user's decision): `*` and `?` globs, with `$n` captures in the style of nx's
  `scope:*` → `scope:$1` idiom.
- A violation is identified by referrer, referenced component, kind and rule text, so "new" means not present
  before the change (by that identity).
- A reference is checked against every applicable rule (AND), once per matching source tag, as in nx's
  `depConstraints`.
- `component:<name>` is an implicit tag, so visibility and rules can name individual components without tagging them.
- The architecture tags keep the nx name `tags` in `components.toml` and the switches; the `component list` table heads
  them `ARCH TAGS` because its `TAGS` column already counts annotated git tags.
- `component add -unwind` does not take `-tags`/`-visibility`/`-owners`; set them afterwards with `component set`.
- Unwind does not refuse new violations: it mirrors existing submodule structure, and refusing would make unwinding
  impossible in a tagged monorepo. It reports them instead.

## Progress

- [x] Model, checker and registry changes with unit tests
- [x] Storage (git, memory) and CLI with tests
- [x] Docs and help

Validated: `dotnet build` clean; `dotnet test` 480 passed, 1 skipped (the weave test, as before), including the new
`Core/BoundaryTests` (24) and `BoundaryCommandTests` (11); manual run over a fresh monorepo as described below.

## Validation

- `dotnet build` and `dotnet test` (new `BoundaryTests`, `BoundaryCommandTests`; the existing suite unchanged).
- By hand: a monorepo with `app -> lib`, rule `type:app` allow `type:lib`; untagged `lib` gives a warning in
  `boundary check`; `component set -name lib -tags type:lib` clears it; `component set -name lib -references app2`
  with a violating edge is refused.
