# Task result cache

## Outcome

A task whose inputs have not changed is not run again. A task marked `cache = true` in its component's `bassia.toml`
is looked up in a local cache under the workspace before it runs. The cache key covers its command, its declared
outputs, the platform, and the content of its component and of every component that component references. On a
hit, Bassia restores the task's declared `outputs` and its log, and reports the task as `cached`. Successful runs are
stored. `bassia task run`, tasks in a run folder and the integration's `merge.verify` gate all share the cache.

## Context

Follows `feat/done/bassia-ff58a69e` (tasks, `affected`, the gate), whose Decisions left caching for later. nx caches
by hashing a task's inputs and restores its outputs on a hit; a task that is not cacheable (side effects, network)
must not be. Every `task run` and every gate ran every task again, so an integration rebuilt and retested unchanged
upstream components each time.

## Acceptance criteria

- [x] `[tasks.<name>]` accepts `cache = true|false` (default false) and `outputs = [patterns]` (paths relative to the
      component, in the split plans' pattern syntax). An invalid value is rejected naming the component and task.
- [x] The key of a cacheable task is a SHA-256 over: the task's name, command, `depends_on` and `outputs`; the platform
      (the runtime identifier, e.g. `linux-x64`); and, for the component and each component in its closure (sorted by name), a content
      fingerprint. The fingerprint is the commit for a checkout made from a commit (`task run -select`, the gate), or a
      hash of the files for a run folder's working copy, where uncommitted changes count.
- [x] On a hit, the outputs are restored into the component's folder, the log is copied to the task's log path, the
      task counts as passed, and its result says `cached = true` (with the original duration). On a miss, the task
      runs; if it passes, its log and its outputs are stored under the key. Failed runs are never stored.
- [x] `task run -no-cache` runs everything. `bassia task cache` reports the cache's location, entries and size;
      `-clear` empties it.
- [x] The integration's gate uses the same cache: with the merged components' commits and the base tips as
      fingerprints, an unchanged upstream component's cacheable tasks are restored, not rerun. The verification
      record notes `cached` per task.
- [x] Tasks without `cache = true` behave exactly as before.
- [x] Documented in `docs/reference.md` and the help.

## Approach

- `Core/TaskCache`: the key, `TryRestoreAsync`, `StoreAsync` and `Clear` over a folder (`<workspace>/.task-cache/<key>/`
  holding `result.toml`, `task.log` and `outputs/`), plus the folder fingerprint (a hash of the files' paths and
  contents, leaving out `.git` and the nested reference paths).
- `TaskRunner.RunAsync` takes an optional cache and a fingerprint per component. `TaskResult.Cached` is recorded in
  `integration.toml` and in `task run` results.
- Callers supply the fingerprints: `task run -select` uses the resolved commits; a run folder uses folder hashes;
  the gate uses the merged checkouts' heads and the base tips.

## Decisions

- Caching is opt-in per task (`cache = true`), as in nx: only the component's author knows whether a task is
  deterministic and which outputs a later task needs. A cached task whose outputs are undeclared is skipped, so a
  `build` that others depend on must declare its outputs.
- The cache is local to the workspace. A shared remote cache would need storage and trust decisions the monorepo does
  not make yet.
- Commit and folder fingerprints are not interchangeable. A run folder and a checkout of the same commit get
  different keys, which costs a rebuild but never restores a stale result.

## Progress

- [x] Task file keys, cache, runner
- [x] CLI (`-no-cache`, `task cache`), gate
- [x] Docs, help, tests

Validated: `dotnet build` clean; `dotnet test` 532 passed, 1 skipped (as before), including the cache cases of
`Core/TaskTests` (parsing, keys, restore of outputs and log, no stored failures, the gate restoring an unchanged
upstream build) and `TaskCommandTests` (real shell tasks: a second `task run` restores lib's build and its output
without running it; a change to what lib references reruns it; `-no-cache`; `task cache` and `-clear`).

## Validation

- `dotnet build`, `dotnet test` (cache tests on the memory storage; CLI tests with real shell tasks: a second
  `task run` restores the outputs without running the command, a change to a referenced component reruns it).
