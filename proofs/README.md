# Proofs: landing keeps what was built and everything before it

A [Lean 4](https://lean-lang.org/) model of how Bassia lands work on a component branch, with machine-checked proofs.
It needs only Lean (no Mathlib); the toolchain is pinned in `lean-toolchain`.

```sh
cd proofs
lake build                  # checks every proof
lake env lean Axioms.lean   # what the main theorems rest on (no sorryAx)
```

CI runs both on every change under `proofs/` (`.github/workflows/proofs.yml`).

## What is modeled

| Lean | Bassia |
| --- | --- |
| `Store` (`objects`, `branches`) | a component store (`IComponentStore`; `MemoryComponentStore`) |
| `Commit` (`parents`, `tree`) | a commit: its parents and a snapshot of the files |
| `Workspace`, `Workspace.commit`, `Workspace.merge` | a working copy (`IComponentWorkspace`): `CommitAsync`, a merge it concludes |
| `overlay` | adding the working copy's commits to the store; an id the store holds is never rewritten |
| `publish` | `IComponentWorkspace.PublishAsync`: fast-forward only |
| `moveBranches` | `IComponentStorage.MoveBranchesAsync` (`IntegrationRunner.AdvanceAsync`): compare-and-swap, all or none |

Trees are abstract (any type). Commit ids of the commits a working copy made are assumed new to the store (`Fresh`):
ids are content hashes. The store is assumed to hold every parent of what it holds (`Closed`).

## What is proved

- `landing_correct` (`Publish.lean`) - a working copy checked out at the tip of a branch that made commits only (no
  merge commits), the last committing the working tree `t`, lands; afterwards the branch points at that commit, whose
  tree is `t`; every commit the store held is unchanged; everything the old tip reached, the new tip reaches; and the
  branch's first-parent history is the committed trees, newest first, followed by every earlier tree in order.
- `publish_tip_commit`, `publish_keeps_objects`, `publish_keeps_reachable` - the same for any working copy, merge
  commits included (except the shape of the history); `publish_refused` - a land that is not a fast-forward changes
  nothing.
- `advance_correct` (`Advance.lean`) - when an integration lands, every base branch points at its result commit,
  unchanged, and reaches everything the branch reached before; `moveBranches_refused` - one branch that moved
  meanwhile refuses the whole landing; `moveBranches_other` - branches no move names stay.

## What is not

The C# code is not extracted from the model nor verified against it; the correspondence is by review (the docstrings
name the code each definition stands for). File contents, merges' trees, tags, upstreams and concurrency inside a
backend are out of scope: the model takes `MoveBranchesAsync`'s all-or-none contract as given.
