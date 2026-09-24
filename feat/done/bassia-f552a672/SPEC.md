# Integration of agentic run results: syntactic first, then semantic

## Outcome

`bassia integrate` combines the results of several agentic runs, component by component, into one integration.
A triage decides which results git's syntax-based merge can combine on its own. Those are merged first. The rest
go to an LLM resolver, which gets a semantic brief on why each side changed, not just which lines it touched. The
result is one annotated tag with the same name in every component, `integration/<id>/<n>`, which can be the
baseline of the next run. It can also be advanced onto the base branch. The frontend has a control panel for all
of this (view `3`).

## Context

Every successful run pushes a result tag `agent/run-<id>/<n>` into each component it changed, all with the same
name (see [bassia-8ab66e1d](../bassia-8ab66e1d/SPEC.md) and [bassia-3a5b27a6](../bassia-3a5b27a6/SPEC.md)). Each
run branches off an annotated baseline tag, so parallel runs produce diverging subsets of changes. Until now,
nothing combined them: the frontend's *Integrate results* entry was a stub
([bassia-a7b47671](../../active/bassia-a7b47671/SPEC.md), [bassia-4f1c8e73](../bassia-4f1c8e73/SPEC.md)).

Two kinds of merge are needed. Most results touch different lines, and git's three-way merge combines them
reliably and cheaply. Some collide. Only something that understands the intent of both changes - the prompt that
drove each run, and its history - can combine those, and that is an LLM's job. The LLM is slow and costly, so it
should only see what git could not do, and it should see it on top of everything git could do.

## Acceptance criteria

- [x] `bassia integrate -runs <ids>|all` merges every selected run's result into each component it changed, oldest
      run first, on the branch `integration/<id>` started at a base: the component's default branch, or `-onto
      <component@branch-or-tag>`.
- [x] A triage classifies each result against the integration head without a working tree and without moving a
      ref: `up_to_date`, `fast_forward`, `clean` or `conflict`. Results git can merge are chained in order, so a
      result that is clean against the base but collides with an earlier one is still found. `-plan` prints the
      triage and changes nothing.
- [x] Every step git can merge runs before any step that needs the resolver. A step that conflicts during
      execution even though the triage predicted otherwise moves to the semantic queue instead of failing.
- [x] A semantic step starts the merge with diff3 markers and runs the resolver command (default `claude -p
      --permission-mode acceptEdits`, configurable as `[integration] resolver`, overridable with `-resolve`) in the
      working tree. The brief goes to the resolver on stdin and is kept on disk. It carries both sides' rationale,
      run records, history and diff, plus the conflicted files and the runs the result collides with.
- [x] Exit code 0 with no conflict markers left commits the merge. A non-zero exit or leftover markers fail the
      step, and the integration continues and ends `partial`.
- [x] `-semantic <run>` sends a cleanly merging result to the resolver for a semantic review; `-skip <run>` leaves
      a result out.
- [x] Each merge commit carries an `[integration]` TOML record. The result is tagged `integration/<id>/<n>`,
      identically across components, and branch and tag are pushed to the source of truth. `main` does not move.
- [x] The integration is recorded as `integration.toml` in `.agentic-runs`, tagged `integration/<id>/<lineage>`,
      with every step's triage, strategy, conflicts, outcome, commit and brief.
- [x] `bassia integrate advance <id>` fast-forwards each component's base branch to the result with a
      compare-and-swap. It refuses when the branch moved since the integration or the base was a tag.
- [x] The frontend's view `3` is a control panel. It lists the integrable runs to choose from and shows the live
      triage. It lets the user put a component onto another base, override a step's strategy, and start the
      integration in the background with live per-step progress. It can stop the integration (killing the
      resolver), open a recorded integration's steps and briefs, and advance. `i` on a run chooses it there. The
      *Integrate results* stub is gone.
- [x] `bassia agent`'s behaviour, results and records are unchanged.

## Approach

- `IntegrationPlanner` is the triage. `MergeProbe` wraps `git merge-base --is-ancestor` and `git merge-tree
  --write-tree` in the component's source repo. Clean merges are chained through unreferenced `commit-tree`
  objects, so the plan is exactly what git will do.
- `IntegrationRunner` executes a plan in `.workspace/integration-<id>/<component>` checkouts, writes the briefs
  (`MergeBrief`), and tags and pushes the results, following the run pipeline's pattern of workspace checkout and
  then push. It also implements `advance`.
- `IntegrationRecord` and `IntegrationStore` keep the record. The run-record store was generalized to record
  primitives (`LatestIndexAsync`, `ListKeysAsync`, `ReadLatestAsync`, `WriteAsync`), which both kinds of record
  share.
- `ShellCommand` is the shell runner, extracted from `AgentCommand` so the agent and the resolver share
  cancellation (process-tree kill), output capture and stdin.
- In the frontend, `IntegrationSupervisor` runs one integration in the background, handing out immutable
  snapshots the way `RunSupervisor` does, and `IntegrationPanel` renders it.

## Decisions

- **Git's merges run first, then the resolver's, not in run order.** The resolver then works on a head that
  already holds everything mechanical, and is asked only what git could not decide. Run order is kept within each
  group. Rejected: strict run order with the resolver interleaved, which would make it resolve against a partial
  integration and re-resolve as later clean merges land.
- **The triage simulates the merge chain instead of probing each result against the base alone.** Two runs that
  each edit the same line of the baseline are both clean against the base but conflict with each other. That is
  the common case for parallel runs, and the triage must predict it rather than discover it at execution. The
  simulation writes unreferenced objects into the source repo; git's garbage collection removes them.
- **The brief is text on stdin plus a file, not a command-line argument.** A brief with two diffs exceeds command
  line limits and would need shell quoting. `claude -p` reads its prompt from stdin, and any other resolver can
  read `BASSIA_MERGE_BRIEF`.
- **The resolver edits and Bassia commits.** Bassia checks the result (exit code, conflict markers, no unmerged
  index entries) and writes the merge commit with its TOML record, so every integration commit is uniform. If a
  resolver commits on its own, that is accepted when the result is in the history.
- **Every integrated run gets its own `--no-ff` merge commit**, even when a fast-forward was possible, so the
  component history shows each run coming in with its `[integration]` record.
- **Partial results are published.** A failed semantic step does not throw away what git merged; the integration
  is `partial`, the tag holds everything that merged, and the failed run can be integrated again onto that tag.
- **Integration never moves a branch by itself.** Like runs, it only adds its own branch and tag. Advancing is an
  explicit, compare-and-swap step, and a tag base is never moved.
- **Components are integrated independently, without the reference junctions a run uses.** A merge changes one
  repo; nested components are integrated as their own components in the same integration.
- **A failure stays where it happened, and is always recorded.** A step whose brief cannot be written or whose
  resolver cannot start fails that step. Any other failure in a component fails that component. Either way the
  integration still gets its final record, so it is never left `started` after components were pushed. A failed
  step puts the checkout back exactly at the previous head (reset and clean), so files a failed resolver created
  never reach the next step's commit. Only a real conflict (unmerged paths) sends a syntactic step to the resolver;
  any other `git merge` failure fails the step with git's message. A background run or integration that fails
  unexpectedly ends its card rather than staying live.
- **One integration at a time in the frontend.** Two concurrent integrations over the same components would only
  race each other for the same conflicts.

## Progress

- [x] Triage (`IntegrationPlanner`, `MergeProbe`) with chained simulation and pairwise collisions.
- [x] Execution (`IntegrationRunner`): syntactic queue, semantic queue with brief and resolver, demotion, publish,
      record, cancellation, `advance`.
- [x] `bassia integrate` CLI with `-plan`, `-onto`, `-semantic`, `-skip`, `-resolve`, `advance`; `[integration]
      resolver` in `config.toml`.
- [x] Store generalized; shell runner shared with `bassia agent`.
- [x] Frontend control panel (view `3`), run detail `i`, help, README.
- [x] Tests: 15 CLI/record tests and 7 frontend tests.

## Validation

```powershell
dotnet build
dotnet test --filter Category!=EndToEnd
dotnet test --filter Category=EndToEnd      # with BASSIA_E2E_COMPONENT_URL pointing at a local mirror when offline
```

Hand-check: three runs over one component, two of which rewrite the same function differently. `bassia integrate
-runs all -plan` shows two SYNTAX steps and one SEMANTIC step that conflicts with the first run. `bassia integrate
-runs all` merges the two, writes `app.<run>.merge.md` with both prompts and diffs, and commits the resolver's
merge. `git log --graph integration/<id>/0` shows one merge commit per run.
