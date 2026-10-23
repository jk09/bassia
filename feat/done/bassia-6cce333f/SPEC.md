# Locking files and folders across parallel agentic runs

## Outcome

Parallel agentic runs no longer rewrite the same files - or files of the same folder - at the same time without
knowing it. A **lock filter** (in the monorepo's configuration, per component, and per run) says which paths are
exclusive and whether a file is locked on its own or with its whole folder. While a run's agent works, Bassia sees
which files it changes and **advertises** that the run holds them; any other run - usually its agent - can ask who
holds a path, claim one before writing, or **wait** until the holder is done and then continue from the holder's
result. A run that changed a path anyway is recorded with a **lock conflict**, and the integration treats its result
by a lock policy (reviewed by the resolver by default) instead of trusting git's line merge with two whole-file rewrites.
Crashed, stopped and suspended runs never hold anything forever. The command line and the web dashboard show who
holds what, who waits for whom, and which results collided on a lock.

## Context

Bassia runs agents in parallel, each in its own run folder (`.workspace/agent-run-<key>/`), and merges their results
later in three tiers: git, weave's structural merge, then the LLM resolver (`bassia integration`, see
`feat/done/bassia-f552a672` and `bassia-c041cc76`). Agents tend to rewrite large parts of a file, so two runs that
touched the same file often produce results no merge tier can combine faithfully, even when git reports the merge as
clean. Avoiding the collision is cheaper than resolving it, but run folders are isolated: there is no shared view of
who is changing what.

Constraints from the codebase:

- Every run is executed by one `bassia` process (foreground or detached) registered in the job registry
  (`<workspace>/.jobs`, pid + process start time). The agent is a child process started by the platform shell with
  `BASSIA_ROOT`, `BASSIA_RUN_ID` and `BASSIA_RUN_DIR`; it can call `bassia` itself.
- Agents do not commit: Bassia commits each component checkout's working tree when the agent exits.
- Run records are durable (`.agentic-runs`), the job registry is disposable, and a user may move their workspace with
  `config.user.toml`, so state shared by every run of a monorepo must live at the monorepo root.
- Path patterns already exist (`PathPatterns`, used by split plans and `merge.manual_paths`), and so does layered,
  per-component configuration (`merge.component.<c>.<setting>`).
- Component names never start with a dot, so a root-level `.locks` folder cannot collide with a component.

## Acceptance criteria

- [x] **Filter.** `lock.files` and `lock.folders` (path patterns as in split plans) set which paths are locked, for
      every component or one (`lock.component.<c>.files|folders`). A changed file below a folder a `lock.folders`
      pattern names is held as that folder (the nearest one); otherwise a file `lock.files` matches is held on its
      own; other files are not held. `run start -lock-files <[c:]pattern,...> -lock-folders <[c:]pattern,...>` adds to
      the configured filter for that run (a `c:` prefix limits an entry to one component). Unset, nothing is held and
      runs behave exactly as before.
- [x] **Implicit holds.** While a run's agent works, Bassia polls its component checkouts every few seconds and holds
      every changed path the run's filter covers, in `<root>/.locks/<run-id>.toml`, with a heartbeat. A reverted change
      is let go. Holds are visible to every `bassia` process of the monorepo, whatever its workspace.
- [x] **Shared understanding.** `bassia lock list` shows every hold (component, path, file/folder, holder, since,
      live/expired, waiters); `bassia lock check -path <p>,...` tells whether paths are free for a run, who holds them,
      and which finished runs' results changed them that the run's checkout does not have yet. Paths are given as the
      agent sees them (relative to its working directory inside the run folder, through junctions into nested
      components), or as `<component>/<path>`.
- [x] **Claim and wait.** `bassia lock claim -path <p>,...` holds paths before writing them (all or none, failing with
      the holders). `bassia lock wait -path <p>,... [-timeout <s>]` waits until no other live run holds them, then holds
      them and brings the results of the runs that changed them into the run's checkout, reporting the files that
      changed meanwhile (the agent's context changed: re-read them). A wait that would deadlock fails at once; a timeout
      reports `timed_out`. `bassia lock release` lets go early; `-run <id>` and `BASSIA_RUN_ID` name the run.
- [x] **Failed, stopped and suspended runs.** A hold is live only while its run's bassia process renews it within
      `lock.ttl` seconds (default 120) and, on the same machine, that process is alive. A crashed or suspended run's
      holds expire and stop blocking. When a suspended run comes back, every hold another run took meanwhile is lost to
      that run and recorded as a conflict. `run stop` of a stale run and `run abandon` remove its holds;
      `lock release -run <id> -force` lets a person break the holds of a live but stuck run.
- [x] **After a run.** When a run ends, its holds are released. If it pushed results, they are remembered as
      *released* holds until those results land on their component's default branch (or the run is abandoned): another
      run that changes such a path without the result in its checkout is behind, and `lock wait` brings it in at once.
- [x] **Conflicts.** A run that changes a path another live run holds (`held`), or a path a released hold covers
      without that run's result in its checkout (`stale`), records the conflict in its run record (component, path,
      kind, the other run) and in its result. `run show` lists holds and conflicts.
- [x] **Integration.** `lock.conflict` (`resolver` (default) | `manual` | `merge`, per component as
      `lock.component.<c>.conflict`) decides what `integration plan|start` does with a result that has a lock conflict: leave it for a human, send it
      to the resolver even when git merges it cleanly (its brief names the conflict), or merge it like any other.
      `-semantic`, `-manual` and `-skip` still win. A result that brought another run's result in is integrated after it.
- [x] **UI.** The web dashboard has a **Locks** page (holds by component, waits, expired holds, conflicts, the filter
      per component and the commands), shows holds and waits on live run cards and run pages, and marks lock conflicts in
      the merge queue and its triage. `bassia status` summarizes the locks. Every new command answers in TOML (and
      `-human`), is in help and in the catalog `bassia prompt` uses (`lock list` and `lock check` read-only).
- [x] Documented in `docs/reference.md`; unit and CLI tests cover the filter, the lock table (expiry, loss, deadlock,
      release), implicit holds of parallel runs, wait with catch-up, conflicts and the integration policy, and the
      dashboard pages. The existing tests still pass.

## Approach

- `Bassia/Locks`: `LockFilter` (effective patterns per run and component, the lock key of a path), `LockTable` (the
  `.locks` folder: one TOML file per run with its holds, waits, conflicts and catch-ups; decisions serialized by an
  exclusive OS file lock on `.locks/.mutex`, which the OS releases when a process dies; liveness, expiry, deadlock
  detection), `RunLocks` (the polling and heartbeat loop around the agent process, stale detection, release), and
  `LockCheckouts` (changed files, "behind", the catch-up as a `git merge` of another run's result tag in the run's
  checkout, forgetting released results once they landed).
- `AgentCommand.StartRunAsync` starts the watcher with the agent, polls once more when the agent exits, adds `stale`
  conflicts, copies holds, conflicts and catch-ups into the run record, and releases after the results are pushed.
- `LockCommands` for `bassia lock list|check|claim|wait|release`; `ConfigFile` learns `lock.*` keys and generalizes
  per-component keys from `merge.component.*` to `lock.component.*`.
- `IntegrationPlanner` applies `lock.conflict` per step and orders a result after the results it brought in.
- Dashboard: a Locks page, plus holds and conflicts on the overview, runs, run, component, queue and config pages.

## Decisions

- Two lock granularities, as two filters: `lock.files` (each file on its own) and `lock.folders` (a whole folder). A
  folder pattern names folders (`src/*` is every folder directly under `src`, `**` the folder of every file); a file is
  held as the nearest folder above it that a folder pattern names, else as itself if a file pattern matches. The
  coarser lock wins.
- Holds conflict when their keys overlap (the same file, or a folder and anything inside it). What another run holds
  binds every run, whatever that run's own filter: the filter only decides what a run itself holds.
- A run's filter adds to the configured one; it cannot switch the monorepo's locks off.
- Lock state lives at the monorepo root (`.locks/`), not in the workspace (which a user may move), and is plain TOML
  files written atomically, so it is readable by any process and by the dashboard. The run record keeps the durable
  summary (holds, conflicts, catch-ups).
- Locks are advisory while the agent works: Bassia cannot stop an agent from writing, so it records what collided and
  the integration enforces the policy. Agents are expected to `lock check` / `lock wait` before rewriting a file.
- A lease (heartbeat + `lock.ttl`) rather than process liveness alone decides expiry, so a suspended process (or one on
  another machine sharing the root) does not hold forever, and a resumed run learns what it lost.
- Holds are released when the run ends; released holds with pushed results are remembered until the results land, so a
  later run is told to catch up instead of being blocked.
- (User) `lock.conflict` defaults to `resolver`: a result with a lock conflict goes to the resolver, briefed on the
  conflict, even when git or weave would merge it cleanly. `manual` and `merge` are the alternatives.
- (User) Nothing is locked by default: `lock.files` and `lock.folders` are empty until configured, so existing
  monorepos behave exactly as before.
- (User) `lock wait` merges the results of the runs that changed the waited paths into the waiting run's checkout and
  reports the changed files; `-no-refresh` only reports.
- (User) When locking applies to a run, Bassia writes the lock protocol into `AGENTS.md` and `CLAUDE.md` at the root of
  the run folder (outside every component checkout, so never committed).
- A folder pattern that covers a file without naming one of its folders holds the file itself, so `**` also locks the
  files at a component's root (one by one) instead of locking the whole component.
- A conflict counts only where the run's final result still changes the path: a change the agent reverted merges
  nothing. A run that lost a hold while suspended and another run's hold on the same path are recorded as `held`.
- A result that brought other runs' results in carries their commits, so the planner merges it after them and gives it
  the stricter of their strategies: `manual` when one of them is left for a human or skipped, `semantic` when one goes
  to the resolver. Otherwise merging it would land their commits unreviewed.
- `run diff` of a run that caught up compares its result with its first parent, so it shows only the run's own change.
- The steps picture of `integration plan|start` drew a `manual` step as `SKIP`; it now says `MANUAL`, which lock-manual
  steps need to be readable.
- Nothing is written to `.locks` while no run locks anything: the watcher only polls once a run has a filter or the
  table has entries.

## Progress

- [x] Specification and decisions
- [x] Lock filter, table and watcher
- [x] `bassia lock` commands and run integration
- [x] Integration policy
- [x] Dashboard and status
- [x] Documentation
- [x] Tests written and passing

## Validation

```sh
dotnet build
dotnet test --filter "Category!=EndToEnd"
dotnet test --filter "FullyQualifiedName~Lock"
```

Manual: two detached runs over the same component with `lock.files = **`; `bassia lock list` shows the first run's
hold while it works; the second run's `lock wait` returns once the first finishes, with the first run's change in its
checkout; `bassia web` shows both on the Locks page.

Verified: `dotnet test --filter "Category!=EndToEnd"` passes (the new `LockTests` and the dashboard's
`Locks_ShowWhoHoldsWhatWhoWaitsAndTheFilter` included; the weave test is skipped without weave installed), and so do
both `Category=EndToEnd` tests, run against a local stand-in for jk09/example (`BASSIA_E2E_COMPONENT_URL`), since
github.com is not reachable from the build sandbox. The manual
check above was done with the built apphost on Linux: implicit holds, a `stale` conflict for a run that rewrote a file
without a released result, `lock wait` bringing two results in, `integration plan` under each `lock.conflict` value,
and the Locks, Runs, run, component and merge-queue pages with live holds and a waiting run.
