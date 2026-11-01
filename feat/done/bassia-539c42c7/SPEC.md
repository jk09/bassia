# Lean proof: landing keeps the workspace's tree and every earlier tree

## Outcome

A machine-checked [Lean 4](https://lean-lang.org/) model of how Bassia lands work on a component branch, with proofs
that landing - in the absence of merge commits - makes the branch tip carry exactly the tree the workspace produced,
and keeps every commit (and so every tree) the branch reached before, unchanged and still reachable.

## Context

"Landing" in Bassia moves a component branch to commits made elsewhere, and is the only way history reaches a base
branch:

- **Publishing a working copy** (`IComponentWorkspace.PublishAsync`, e.g. `MemoryWorkspace`): the commits the workspace
  made are added to the store's object space and the branch moves to the workspace head - refused unless the current
  tip is an ancestor of the head (fast-forward only).
- **Advancing an integration** (`IntegrationRunner.AdvanceAsync` -> `IComponentStorage.MoveBranchesAsync`, the
  `BranchMoves.InSequenceAsync` path of `Storage/Git` and `Storage/Memory`): every base branch moves to its result
  commit only while it still points at the commit the integration was built on (a compare-and-swap) - all branches or
  none.
- A commit (`MemoryComponentStore.Add`, `MemoryWorkspace.CommitAsync`) records its parents and a snapshot of the
  working files (its tree); objects are content-addressed and never rewritten.

The claim that "landing never loses history and lands exactly what was built" is load-bearing for agent runs and
integrations but has so far only been tested by examples. A formal model makes the invariant explicit and checkable.

## Acceptance criteria

- [x] A Lean 4 project (no Mathlib, pinned toolchain) under `proofs/` builds with `lake build` and contains no `sorry`
      or added axioms.
- [x] The model covers: commits with parents and a tree, a store (append-only object map + branches), a workspace
      started at a base commit that commits a working tree, and landing = publish with compare-and-swap and
      fast-forward check, and the multi-component all-or-none branch move.
- [x] Proved: after a successful land, the branch tip's tree is the workspace's tree (the tree of its last commit).
- [x] Proved: every commit present before landing is present after it with the same parents and tree, and every
      commit reachable from the old tip is reachable from the new tip (old trees are retained).
- [x] Proved: for a workspace without merge commits (every commit made with a single parent), the first-parent
      history of the new tip is the workspace's commits (newest first) followed by the old tip's history, so the
      list of trees is the new trees followed by exactly the old ones.
- [x] Proved: a land refused by the compare-and-swap or the fast-forward check leaves the store unchanged; a
      multi-component move either applies every move or changes nothing.
- [x] CI checks the proofs on every pull request.
- [x] `docs/reference.md` says what is proved, what is modeled and what is not (the C# code is not extracted from
      the model; the correspondence is by review).

## Approach

- A small, self-contained Lean model (`proofs/Bassia/*.lean`): `Id`, `Tree` abstract (any type), `Commit`, `Store`
  as total functions with `Option`, a `Workspace` holding its base, its local commits and its head, and pure `land`
  / `moveBranches` functions returning `Option Store` (`none` = refused).
- Freshness of new commit ids is an explicit hypothesis (content addressing in git; the model does not hash).
- Reachability as an inductive relation over parents; first-parent history with fuel equal to the workspace length
  plus the old history, avoiding well-founded recursion on a cyclic-in-principle graph.
- A CI job installs elan and runs `lake build` in `proofs/`.

## Decisions

- "Landing a workspace" covers both paths: publishing a working copy (fast-forward) and advancing an integration
  (compare-and-swap, all or none) - the user's choice.
- The proofs are checked in CI (`.github/workflows/proofs.yml`, triggered by changes under `proofs/`): installing the
  Lean toolchain in the authoring sandbox was not permitted - the user's choice.
- Lean 4 core only (no Mathlib), toolchain pinned to `v4.15.0`, so the build is fast and stable.
- The store never rewrites an id it holds (`overlay` gives the store priority), matching content-addressed objects.
- "In the absence of merge commits" is the `Built` predicate (commits only); tree identity and history retention are
  also proved with merges (`Made`), only the shape of the first-parent history needs `Built`.

## Progress

- [x] Specification agreed.
- [x] Model and theorems (verified by the Proofs workflow: Lean v4.15.0 builds every module; the main theorems rest only on propext, Classical.choice and Quot.sound).
- [x] CI job and docs.

## Validation

- `cd proofs && lake build` (in CI, and locally where elan is installed).
- `grep -rn "sorry\|axiom" proofs/Bassia` finds nothing.
