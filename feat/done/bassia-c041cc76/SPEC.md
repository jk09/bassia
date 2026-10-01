# Structural merge tier: weave between git and the resolver

## Outcome

`bassia integration` resolves more agentic run results without an LLM. Between git's line-based merge and the
semantic resolver, the triage tries a deterministic, entity-level merge with
[weave](https://github.com/Ataraxy-Labs/weave) (`weave-driver`, a git merge driver that merges functions, classes
and keys parsed with tree-sitter instead of lines). A result git cannot merge but weave can becomes a `structural`
step: merged deterministically, chained like a syntactic one, and never sent to the resolver. What weave cannot
merge either still goes to the resolver, which now starts from weave's reduced, entity-labelled conflicts instead
of git's raw ones.

## Context

The integration ([bassia-f552a672](../bassia-f552a672/SPEC.md)) triages each run's result with `git
merge-tree`: what git can merge is `syntactic`, everything that conflicts is `semantic` and costs an LLM call.
Parallel agentic runs very often add or change *different* functions, methods or keys in the *same* file. Git
reports these as conflicts when the hunks touch or are adjacent, although nothing collides. Weave was built for
exactly that case (two agents adding different functions to one file merge cleanly; two agents changing the same
function still conflict), is deterministic and stateless like `git merge-file`, supports 38 languages plus JSON,
YAML, TOML, Markdown and falls back to a line merge for anything else.

Verified while specifying (git 2.43, weave 0.5.4): `git merge-tree --write-tree` honours a merge driver given only
through `-c merge.weave.driver=... -c core.attributesFile=<file>`, in normal and bare repos, so the triage can
probe the structural merge without a working tree and without writing anything into the component repos. A file
with two independently added Python functions conflicts under git and merges cleanly under weave.

The triage based on git's deterministic merge stays exactly as it is; weave is consulted only for results git
classifies as `conflict`.

## Acceptance criteria

- [x] The git triage is unchanged: every step still records git's verdict (`up_to_date`, `fast_forward`, `clean`,
      `conflict`) and git's conflicted files; results git merges stay `syntactic` and are merged by plain git.
- [x] For a result git triages as `conflict`, the triage probes the same merge with weave as the merge driver. If
      weave merges it cleanly, the step's strategy is `structural`, it is chained onto the simulated head like a
      syntactic step (so later results are triaged on top of it), and it is never sent to the resolver.
- [x] If weave merges it but warns (`weave-warning:`), the step is `semantic` (a review by the resolver, with the
      warnings in the brief); `structural = "warnings"` and `structural_warnings` are recorded.
- [x] If weave still conflicts, the step is `semantic` and the record lists the files that remain conflicted after
      weave (`structural_conflicts`) beside git's `conflicts`.
- [x] Execution order is syntactic and structural steps in run order, then semantic steps. A structural step is a
      `git merge --no-ff` with weave as the driver; if it conflicts (or warns) after all, it moves to the semantic
      queue, as a syntactic step does today.
- [x] A semantic step's merge is started with weave as the driver when weave is enabled, so the resolver sees only
      the conflicts weave could not resolve, with weave's entity-labelled markers. The brief says which files weave
      already merged and how to read its markers.
- [x] The pairwise "conflicts with" analysis uses the best enabled deterministic merge (weave when enabled).
- [x] Weave is optional. `[integration] weave` in `config.toml` (default `weave-driver`, settable with `bassia
      config`) names the driver; `off` disables it; `-weave <command>|off` overrides it per `integration plan` /
      `start` (and is forwarded to a `-detach`ed integration). When the configured driver is not found, the triage
      behaves exactly as before and the plan says weave is unavailable; nothing fails.
- [x] No component repo is modified to enable weave: the driver and attributes are passed with `-c`, and the
      attributes file lives in Bassia's workspace. A component's own `.gitattributes` still takes precedence.
- [x] Merge commit messages, `integration.toml`, TOML results and the web dashboard show the `structural` strategy,
      the driver used, and the structural conflicts. Records written before this change still load.
- [x] Tests cover: a git conflict that weave resolves (structural, no resolver call), a merge with warnings
      (resolver review), a conflict weave cannot resolve (semantic with structural conflicts), weave
      disabled/missing (old behaviour), config, and record round-trip including records written before.

## Approach

- `MergeProbe` gains an optional structural merge configuration (driver command + attributes file) that it turns
  into `-c` arguments for `merge-tree` and `merge`. `IntegrationPlanner` probes with git first, and only on a
  conflict probes again with weave.
- `MergeStrategy.Structural` joins `Syntactic`, `Semantic` and `Skip`. `IntegrationRunner` executes structural
  steps in the syntactic phase with the driver configured, and starts semantic merges with it too.
- A `StructuralMerge` helper resolves the configured driver (PATH lookup), writes the attributes file (weave's
  supported extensions, minus the ones weave itself declines) under the workspace, and renders the `-c` options.
- `IntegrationRecord` gains `structural_conflicts` per step and `structural_driver` per integration, both optional
  on load.
- Tests that need a real weave are skipped unless `weave-driver` is on `PATH`; the plumbing is also tested with a
  deterministic stand-in driver (`git merge-file --union`), so CI without weave still covers it.

## Decisions

- Weave is a third, deterministic tier, not a replacement for git's triage or for the resolver: git stays the first
  and authoritative probe (cheapest, and what the user asked to keep), weave only sees what git refused, and the
  resolver only sees what weave refused.
- Weave is integrated as a git merge driver passed via `-c`, not via `weave setup`, so integrating never edits a
  component's `.gitattributes` or `.git/info/attributes`. The attributes file (weave 0.5.4's extensions, as `weave
  setup` derives them) is written to the workspace and selected with `-c core.attributesFile`, which replaces a
  user-wide attributes file for those git commands only.
- **Weave-clean with warnings goes to the resolver for review** (user decision). "Clean" is not "safe": weave's
  warnings flag e.g. an entity whose dependency the other side changed, or a merged file that no longer parses.
  Only a weave-clean merge without warnings is merged without the resolver. Rejected: structural with warnings only
  recorded; structural unless the parse failed.
- **Weave is optional and auto-detected** (user decision): `[integration] weave` defaults to `weave-driver` and is
  used when found on `PATH`; otherwise everything behaves as before and the plan says why. Rejected: a required
  dependency.
- The triage keeps git's verdict in `triage` and `conflicts` and records weave's separately (`structural`,
  `structural_conflicts`, `structural_warnings`), so the git-based triage reads exactly as before and the record
  shows what weave added.
- Semantic merges start with weave as the driver too, so the resolver works on weave's smaller, entity-labelled
  conflicts. Weave's markers have no ancestor section (weave ignores `diff3`); the brief already carries both
  sides' diffs, and explains the markers. Weave's `weave explain` pointer line left in a file counts as an
  unresolved conflict, so it is never committed.
- The test fixture sets `integration.weave = off`, so whether a developer has weave installed never changes what
  the existing tests see; the structural tests opt in with `-weave` and a stand-in driver built from `git
  merge-file`, and one test uses the real `weave-driver`, skipped where it is not installed.

## Progress

- [x] Specification and open questions resolved.
- [x] Structural probe in the triage, structural execution, weave-backed semantic merge.
- [x] Record, results, brief, dashboard and docs updated.
- [x] Tests (stand-in driver, real weave when available) pass.

## Validation

- `dotnet build` and `dotnet test` (with and without `weave-driver` on `PATH`).
- Manual: two runs adding different functions to the same file; `bassia integration plan -runs all` shows one
  `structural` step, `integration start` merges it without starting the resolver (automated as
  `StructuralMergeTests`, with a stand-in driver and with the real weave 0.5.4 built from source).
- Verified: 252 tests pass with `weave-driver` on `PATH`; 251 pass and the real-weave test is skipped without it.
