# Storage-independent component graph

## Outcome

The central idea of Bassia - a monorepo as a graph of components (program units) maintained by a meta-repo - is a
layer of its own, independent of how each component's history is stored. `git` becomes one implementation of a small
storage contract (a DAG of commits, branches and annotated tags, working copies, plus the history operations split and
unwrap need), so a later backend - e.g. blobs in cloud storage held together by a commit DAG in a cloud database - only
has to implement that contract. The graph API (component creation, import, removal, references, tagging, forking,
splitting, unwrapping and agentic runs) is unit tested against an in-memory backend, with no `git` involved.

Agentic-run commands are renamed from `bassia run ...` to `bassia agent ...`.

## Context

Before this change every command talked to `git` directly (`GitClient` in commands, splitter, unwinder, run engine),
and the meta-repo's registry (`components.toml`) was edited and committed as files by whoever needed it. The only way to
test graph behaviour was end-to-end through real repositories. `bassia agent` was a long-removed command name kept in
`CommandTable.Replaced`.

## Acceptance criteria

- [x] A storage contract (`Bassia.Storage`) defines what a component backend provides: commits forming a DAG,
      branches, annotated tags, file listings, nested units (submodules), working copies (checkout, commit, tag,
      publish), staging of imports, fork and split of histories. It mentions no git concepts beyond these.
- [x] The meta-repo is a unit of its own (`Bassia.Core.MetaRepo`): it owns the validated component graph
      (`ComponentRegistry`: unique valid names, registered references, valid nesting paths, no cycles, closure,
      referrers, dependency order) and persists every change through an `IMetaStore` as one revision with a message.
- [x] A component-graph API (`Bassia.Core.MonorepoGraph`) creates (new, empty) and imports components, removes them,
      sets references, tags, forks, splits and unwraps them, and runs agentic runs (resolve a selection, materialize
      the closure with nested references, run the agent, commit/tag/publish results, retry, abandon), using only the
      storage contract and the meta-repo.
- [x] Git implements the contract (`Bassia.Storage.Git`); the CLI commands `component add|show|remove|set|tag|fork|
      split|unwind`, `component add -unwind` and `agent start|retry|abandon` go through the graph API, and the existing
      end-to-end tests keep passing.
- [x] `bassia component create -name <c> [-references ...] [-branch <b>] [-url <url>]` creates a new component whose
      history starts with one commit without files, so a run can select it at once; a taken or invalid name, or an
      unregistered reference, fails and leaves nothing behind.
- [x] An in-memory implementation (`Bassia.Storage.Memory`) implements the same contract; unit tests cover the graph
      API (registry validation, meta-repo persistence, create/import/remove/references/tag/fork, split, unwrap,
      agentic runs including failures, retry and abandon) without git.
- [x] `bassia agent start|list|show|logs|wait|stop|retry|abandon|diff` replace `bassia run ...`; `bassia run` answers
      with the replacement (like other replaced commands). Help, docs, README, scripts, tutorial, web dashboard texts and
      tests use the new name.

### Integration and locks (follow-up)

- [x] The storage contract covers what integration and lock catch-up need: a three-way merge of two commits without a
      working copy (clean or conflicted paths, an opaque merged snapshot, driver warnings), committing a snapshot with
      given parents, merge base, diff text and changed paths between commits; and in a working copy: head, merge
      (fast-forward or not, committing or leaving the merge open), conflicted paths, merge in progress, reset to a
      commit, uncommitted changed paths, containment of a commit, fetching a store tag, and concluding an open merge.
- [x] A file-level merge driver (weave) is a storage-neutral `FileMergeDriver` (command, file extensions, warning
      prefix); the git backend turns it into its merge-driver configuration, the memory backend runs it per file.
- [x] Integration records go through a generic record store (`IRecordStore`, which `IRunRecordStore` extends);
      `IntegrationPlanner`, `IntegrationRunner` (run, advance) and `IntegrationStore` use only the graph, the storage
      contract, the record store and injected settings (workspace, merge/lock policy per component, the resolver).
- [x] `LockCheckouts` (changed files, containment, catch-up merge, forgetting landed results) uses only working copies
      and stores from the storage contract.
- [x] Unit tests on the memory backend cover the integration triage (fast-forward, clean, conflict, chained results,
      manual paths, skip), running an integration (git-like merges, a fake resolver resolving a conflict, a resolver
      that fails, publish and tag, advance and a moved base) and lock catch-up (changed files, behind, bringing a result
      in, a catch-up that would overwrite local changes, forgetting landed results); the existing end-to-end tests pass.

## Approach

Three layers, outermost depending on the inner ones only:

1. `Bassia.Storage` - the contract (`IComponentStorage`, `IComponentStore`, `IComponentWorkspace`, `IStagingArea`,
   `IMetaStore`, `IRunRecordStore`) and value records. Backends: `Git` (wrapping the existing plumbing: bare repos next
   to `.bassia`, `components.toml` edited in place, history rewriting via fast-export/fast-import) and `Memory`.
2. `Bassia.Core` - `ComponentRegistry` (pure, immutable graph), `MetaRepo` (registry + `IMetaStore`), `MonorepoGraph`
   (component operations), `AgentRuns` (run engine with injectable agent process and lock session), and the split and
   unwrap engines rewritten against the contract.
3. CLI, web and integration - build a `MonorepoGraph` for the monorepo on disk (`Monorepo.Graph()`) and format results.

## Decisions

- Scope of the abstraction: the graph operations named in the request are storage-independent; the follow-up moved
  integration and lock catch-up behind the contract too. `bassia log`, `agent diff`, `component survey` and the web
  dashboard's history views still use git directly.
- Commit names (`-ref` of `component tag|fork`) are resolved by `Core.Revisions` through the contract: `HEAD`, branch
  and tag names, full ids or unique prefixes, with `~n` / `^n` steps - not every git revision expression.
- A created component starts with one commit without files rather than an unborn branch, so every component always
  has a tip to select and the run engine needs no special case for empty histories.
- Component names are validated by the registry for every new component (no whitespace, separators, colon or leading
  dot), as `component fork` already did; two references at the same path are rejected.
- Registry changes are persisted as operations (`RegistryChange`: add, remove, set references) rather than whole
  documents, so the git store can keep editing `components.toml` line by line (hand-written comments survive) and a
  database store can apply them transactionally.
- The in-memory backend lives in the main assembly as the reference implementation of the contract, not only as a
  test double; it materializes working copies into real folders so agentic runs behave as with git.
- `bassia agent` was listed as replaced (by `bassia run`); the entry now points the other way, `bassia run` -> `bassia
  agent`. The run's job kind on disk, `-run <command>` switches and the `agent-run/` tag names are unchanged.

- Follow-up scope: "locks behind the storage contract" means the git work locks do in run checkouts and component
  stores (`LockCheckouts`). The lock table (`.locks`) is cross-process coordination state, not component history, and
  stays a file store; `bassia log`, `agent diff`, `component survey` and the dashboard's history views still read git.
- Integration probes a merge whose head exists only in its working copy (a merge it made) by merging in the working
  copy and resetting, rather than asking the store, which does not have that commit yet.
- A lock catch-up that fails aborts the merge (`AbortMergeAsync`) instead of resetting, so the agent's uncommitted
  changes survive.
- The memory backend merges at file granularity: a file both sides changed differently conflicts (whole-file conflict
  markers) unless a `FileMergeDriver` handles its extension. It is a reference for the contract, not a merge engine.

## Progress

- [x] Storage contract, memory and git backends
- [x] Registry, meta-repo, graph API, agent-run engine, split and unwrap on the contract
- [x] CLI rewired; existing tests pass
- [x] Unit tests for the graph API against the memory backend
- [x] `run` -> `agent` rename across code, help, docs and tests
- [x] Merge, diff and working-copy merge operations in the contract, git and memory backends
- [x] Integration planner, runner, records and lock catch-up on the contract
- [x] Unit tests for integration and locks on the memory backend; existing tests pass

## Validation

- `dotnet build -c Release` and `dotnet test -c Release` (with `BASSIA_E2E_COMPONENT_URL` pointing at a local upstream
  as in CI): all tests pass, including the new `Bassia.Tests/Core/*` unit tests that use only `Bassia.Storage.Memory`.
- `bassia run start ...` reports the replacement; `bassia help agent start` shows the renamed command.
