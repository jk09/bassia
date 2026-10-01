# Three-component end-to-end test with parallel agentic sessions, and a run-aware log

## Outcome

An end-to-end test builds a Bassia monorepo of three components - each a clone of
[jk09/example](https://github.com/jk09/example) under its own logical name - and drives it only through the `bassia`
executable: parallel agentic sessions (deterministic stand-ins for a coding agent) implement libraries in two
components and programs using them in the third, their results are integrated (git merges what it can, a
deterministic resolver merges a real conflict) and advanced onto `main`. Bassia's own CLI then proves where every
session's work ended up: `bassia log -run <id>` lists the commits a run produced or merged in every component and
whether each landed on the component's default branch, and `bassia log -branch main` shows a component's mainline
with every commit attributed to its run or integration. A last session builds and runs the programs from the
advanced `main` branches.

## Context

`ParallelAgentRunEndToEndTests` proves two single-component runs in parallel, but stops at the run's result tag: it
never integrates, never advances `main`, never nests components, and verifies everything with raw git. The CLI
cannot answer "where did run X's work end up?" across components: `bassia log` shows commits without saying which
run made them, cannot be restricted to a branch, and `run show` only knows the run's own result tags, not the
integration commits that carried them into `main`.

Running the existing suite on Linux shows a bug this scenario depends on: a referenced component is nested into its
referrer's checkout as a symlink, and the run excludes it from the referrer's index with `/<path>/`, a pattern that
matches only directories - git sees a symlink as a file, so the referrer commits the link
(`AgentCommandTests.Agent_ComponentChain_IsCheckedOutSideBySideAndJunctionedIntoItsReferrers` fails on Linux).

Related records: `bassia-8ab66e1d` (agentic runs), `bassia-3a5b27a6` (flat workspace, nesting),
`bassia-f552a672` (integration), `bassia-c17712cc` (command line).

## Acceptance criteria

Run-aware history

- [x] Every `[[commit]]` of `bassia log` (one component or several) carries `run_id` when its message holds an
      `[agentic_run]` record and `integration_id` (plus the merged `run_id`) when it holds an `[integration]`
      record, and `kind = "result" | "integration"`.
- [x] `bassia log -branch <branch>` limits each component's history to that branch (e.g. `main`); a component
      without the branch fails with an error naming it.
- [x] `bassia log -run <id>[,...]` lists, across every component the runs touched (or the `-component`s given),
      the commits that are the runs' results or the integration merges that brought them in, each with its
      component, `kind`, `run_id`, `integration_id` and `on_default_branch`; plus one `[[run]]` per run with, per
      component, its result tag and commit, the default branch, `landed` (the result is reachable from the default
      branch) and `merged_by` (the integrations whose merge of it is on that branch). Runs are named like everywhere
      else (full id, `<id>` part, or a prefix of 4+ digits); an unknown run fails.

Nesting fix

- [x] A run's referrer never commits the nested component, whether the nesting is a junction or a symlink; the
      chain test passes on Linux.

End-to-end test (`Category=EndToEnd`)

- [x] Three components (`sortlib`, `greetlib`, `apps`, the last nesting both libraries) are cloned from
      `jk09/example` (or `BASSIA_E2E_COMPONENT_URL`) and tagged through the CLI.
- [x] Wave 1 starts three sessions with `run start -detach` at once and waits for them with `run wait`: a hello
      world program in `apps`; an insertion sort library in `sortlib`; a greeting library in `greetlib` together
      with a program in `apps` that uses it. Their recorded intervals overlap.
- [x] Two wave-1 sessions edit the same lines of `apps/README.md`. `integration start` merges the rest with git and
      hands that conflict to a deterministic resolver (a git union merge); `integration advance` moves every `main`.
- [x] Wave 2 starts two foreground sessions in parallel from the advanced `main`: a sorting terminal program in
      `apps` that calls `sortlib`, and a descending sort added to `sortlib`; they are integrated and advanced too.
- [x] Verified through the CLI only: before `advance`, `log -run` reports every result `landed = false`; after it,
      `landed = true` with `merged_by` the integration, in exactly the components each session touched and no
      other; `log -component apps -branch main` attributes the mainline commits to all five runs; `component show`
      reports each `main` at the integration result; `run list` and `integration show` report everything completed.
- [x] A final session selects the three `main` states and builds and runs the programs (when a C compiler is on
      `PATH`): `hello, world`, the greeting, and the sorted numbers in both orders appear in its output.
- [x] The test writes a Markdown proof like the existing end-to-end test; README documents the new switches and test.

## Approach

- Commit provenance: one parser reads the TOML record in a commit message body (`[agentic_run]` or
  `[integration]`); the timeline reads bodies along with subjects (one `git log` per component, as now).
- `log -run`: per component, `git log --branches --tags -F --grep=<run id>` finds candidate commits, the parser
  confirms them, `git merge-base --is-ancestor` decides `on_default_branch` / `landed`. Lanes as for several
  components.
- `log -branch`: the branch replaces `--branches --tags` as the log scope.
- Nesting fix: exclude `/<path>` (no trailing slash), which matches the link whatever git takes it for.
- The test drives the built apphost as child processes (like `ParallelAgentRunEndToEndTests`). Each session's
  "agent" applies a pre-computed patch with `git -C <component> apply` - deterministic, cross-platform, and exactly
  what an agent's edit leaves in the run folder - after a short sleep so parallel sessions overlap. Patches are made
  by the test against the session's baseline. The resolver is `git merge-file --union` on the conflicted stages.

## Decisions

- Programs and libraries are in C: the example repository's `.gitignore` is a C one, so build outputs (`*.out`,
  `*.o`) stay out of the components; building is skipped (and reported) without a C compiler.
- Agent stand-ins apply patches rather than run a model, as allowed by the request: the proof is about Bassia.
- `-run` searches branches and tags of the components, which covers run branches, integration branches and `main`.
- `-run` cannot be combined with `-branch`: it always judges landing against each component's default branch.
- Commits made in the same second keep git's order within a component on the timeline (a stable sort replaces the
  hash tie-break), so a merge is never drawn below what it merges.
- `component show` reports full commit hashes for branches and tags, like every other command (it reported 7-digit
  abbreviations, which the end-to-end test could not compare with an integration's result); the web dashboard
  still displays them short.
- The end-to-end helpers (driving the apphost, reading results, the proof) are shared by both end-to-end tests;
  `BASSIA_E2E_PROOF` may name a folder, getting one report per test.

## Progress

- [x] Nesting fix
- [x] Commit provenance in `log`, `-branch`, `-run`
- [x] Three-component end-to-end test, iterated until green
- [x] README, full test suite

## Validation

- `dotnet build` without warnings; `dotnet test` all green on Linux (Ubuntu 24.04, .NET 10.0.112, git 2.43), the
  end-to-end tests cloning jk09/example from GitHub.
- `dotnet test --filter Category=EndToEnd --logger "console;verbosity=detailed"`: the three-component proof shows
  wave 1's `apps` README conflict resolved by the union-merge resolver, `log -run` reporting 0 of 4 results landed
  before `advance` and all after, `log -run all` reporting 6 of 6 results landed, and the final session printing
  `hello, world`, `Hello, Bassia!`, `1 3 5 9` and `9 5 3 1` from programs built from the advanced `main` branches.
- New fast tests: `CliSurfaceTests` (`log` provenance and `-branch`, `log -run` before and after advance, errors),
  `CommitProvenanceTests`; `AgentCommandTests.Agent_ComponentChain_...` passes on Linux with the nesting fix.
