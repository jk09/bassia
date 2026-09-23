# `Bassia`

`Bassia` is a small command-line interface built on top of Git. It delegates repository storage, history, branching, and commit behavior to the installed Git executable instead of reimplementing Git internals.

## Requirements

- .NET SDK 10.0 or later
- Git available on `PATH`

## Run

From a Git repository:

```powershell
dotnet run -- --help
dotnet run -- status
dotnet run -- log
dotnet run -- branch
dotnet run -- commit -m "Describe the change"
dotnet run -- init
dotnet run -- add-component https://github.com/myrepo/component_1.git
dotnet run -- -C C:\temp\foo init
```

`init [directory]` initializes a `Bassia` monorepo in the given, empty folder (default: the current directory): a `.bassia` meta-repo (with its own Git history and `config.toml`/`components.toml`) and a `.workspace` folder for agent run folders. `add-component <url> [name]` clones a repository as a bare component alongside the meta-repo and registers it in `.bassia/components.toml`, under the given logical name or, by default, one inferred from the URL. Both commands print a result on stdout (success) or stderr (failure) for machine consumption; see [Results](#results).

`-C <path>`, given before the command, runs `bassia` as if it had been started in `<path>` instead of the current directory, same as `git -C <path>`. It can be repeated, with each occurrence resolved relative to the directory left by the previous one.

### Results

Every command prints its result as TOML — on stdout when it succeeded, on stderr when it failed — so an agent or a script can read it without scraping prose. TOML is what `Bassia` stores everything else in (`config.toml`, `components.toml`, the `run.toml` records), so a caller uses one parser and one vocabulary of `snake_case` keys throughout.

A result always opens with the comment line `# bassia result`, then `ok`, the `command`, and either `message` (success) or `error` (failure); commands that have more to report add their own keys after those, and `agent` adds a `[[component]]` section per component, named as in `run.toml`. The marker exists because `bassia agent` lets the agent command inherit stdout: a caller takes the last marker line as the start of the result and treats anything before it as agent output. Values TOML cannot express are simply absent keys — there is no `null`.

```toml
# bassia result
ok = true
command = "agent"
message = "Agentic run 'agent-run-c37ed8ae51f1420a9abee46a4f836af3' completed."
run_id = "agent-run-c37ed8ae51f1420a9abee46a4f836af3"
status = "completed"
workspace = "R:\\.workspace\\agent-run-c37ed8ae51f1420a9abee46a4f836af3"
agent_exit_code = 0
metadata_repo = "R:\\.agentic-runs\\.git"
metadata_tags = ["agent/run-c37ed8ae51f1420a9abee46a4f836af3/0", "agent/run-c37ed8ae51f1420a9abee46a4f836af3/1"]
[[component]]
name = "app"
commitish = "v0"
commit = "350c164c94f720acf82b204da3a0a436b9270118"
path = "R:\\.workspace\\agent-run-c37ed8ae51f1420a9abee46a4f836af3\\app"
branch = "agent/run-c37ed8ae51f1420a9abee46a4f836af3"
result_status = "pushed"
result_commit = "1f4a0c8d0f2f4a9b9d1a6f0b6f1c2d3e4a5b6c7d"
result_tag = "agent/run-c37ed8ae51f1420a9abee46a4f836af3/0"
```

### Agentic runs

```powershell
git -C R:\app tag -a v0 -m "baseline"                # -select needs an annotated tag per component
git -C R:\lib tag -a v0 -m "baseline"
dotnet run -- agent -select app@v0,lib@v0 -run "claude -p 'add a hello world script' --permission-mode acceptEdits"
dotnet run -- agent retry agent-run-<id>             # re-run a failed commit/tag/push sequence (the bare <id> works too)
dotnet run -- agent abandon agent-run-<id>           # discard the run's folder
```

For a quick hand-check against the Debug build, `scripts/` has two helpers: `New-TestMonorepo.ps1` creates a throwaway monorepo in `%TEMP%` with the [jk09/example](https://github.com/jk09/example) component added and its HEAD tagged `tag-base`, and returns the path; `Invoke-AgentRun.ps1 -Monorepo <path> -Prompt <prompt>` runs `claude -p --permission-mode acceptEdits "<prompt>"` over it (`-Select` and `-Agent` override the defaults) and prints where each result landed.

```powershell
$r = ./scripts/New-TestMonorepo.ps1
./scripts/Invoke-AgentRun.ps1 -Monorepo $r -Prompt 'write hello world in C# as example/HelloWorld.cs'
```

`agent -select <component@tag>[,...] -run <command>` runs an agent on an isolated copy of the selected monorepo state:

1. Every `-select` entry names a component registered in `.bassia/components.toml` and an **annotated tag** in its source-of-truth repo (`R:\<component>`); the tag is the immutable provenance recorded for the run. Unregistered names, branches and bare commits are rejected.
2. The selection must cover the full transitive closure of the reference graph (`references = ["lib"]` or `references = [{ name = "lib", path = "libs/lib" }]` in `components.toml`; the graph must be acyclic). Selecting `app` without the `lib` it references fails before anything is materialized — a run pins every component it touches, so nothing is ever resolved from a mutable reference.
3. The run gets a random id, `agent-run-<id>` with `<id>` a GUID (32 hex digits), and the folder `.workspace/agent-run-<id>/`. The id is unique without any coordination, so runs from different workspaces or machines never collide on the branches and tags they push to a shared component repo. Every selected component is cloned from its source-of-truth repo and checked out there, side by side, on the run branch `agent/run-<id>`. Where one component references another, the referenced component appears inside it as a junction to that sibling checkout, at the subfolder `components.toml` records; the nested path is excluded from the referring repo's index, so each repo only ever commits its own files.
4. The `-run` command is started by the platform shell (`cmd.exe` / `sh`) in `.workspace/agent-run-<id>/`, with `BASSIA_ROOT`, `BASSIA_RUN_ID` and `BASSIA_RUN_DIR` set.
5. Before the agent starts and after it finishes, the run record (`run.toml`: selection, resolved commits, command, timestamps, status, per-component results) is committed to the bare repo `.agentic-runs/.git`, at the monorepo root alongside `.bassia` and `.workspace`, with plumbing commands only (no checkout, no branch) and tagged `agent/run-<id>/<lineage>`. It lives outside both the meta-repo and the workspace, so the run folder can be discarded once its results are pushed while the record survives.
6. When the agent exits successfully, each changed component is committed on its run branch, tagged `agent/run-<id>/<counter>`, and branch + tag are pushed to its source-of-truth repo. The counter is the next unused index among the component's existing `agent/run-<id>/*` tags — all of them sit on the run branch, so the checkout's own tag list is the whole sequence; a run's first result is `/0`. Nothing is merged or checked out there. The sequence is not transactional: a failed component leaves the run `partial`; `agent retry` finishes the remaining steps and `agent abandon` discards the run folder without touching anything already pushed.

The result commit's message is a subject line, a blank line, and a TOML record of the run and the component, so tooling can read a component's history back to the run that produced each commit:

```toml
agent(c37ed8ae): add a hello world script

[agentic_run]
id = "agent-run-c37ed8ae51f1420a9abee46a4f836af3"
summary = "add a hello world script"
command = "claude -p \"add a hello world script\" --permission-mode acceptEdits"
select = "app@v0,lib@v0"
created = "2026-09-21T02:12:05.5578522+00:00"
finished = "2026-09-21T02:12:06.4559366+00:00"
record_tag = "agent/run-c37ed8ae51f1420a9abee46a4f836af3/0"
[agentic_run.component]
name = "app"
commitish = "v0"
base_commit = "350c164c94f720acf82b204da3a0a436b9270118"
branch = "agent/run-c37ed8ae51f1420a9abee46a4f836af3"
tag = "agent/run-c37ed8ae51f1420a9abee46a4f836af3/0"
```

The subject line is a template in `.bassia/config.toml` (written by `init`); the body's keys are fixed. `{run_id}` is the full `agent-run-<id>`, `{short_id}` the first 8 digits of `<id>`, `{summary}` the longest quoted part of the agent command (usually the prompt) or the command itself, `{component}` the component name:

```toml
[agent.commit]
subject = "agent({short_id}): {summary}"
```

The workspace location can be moved (for example to another volume) via the same file:

```toml
[workspace]
path = 'D:\bassia-workspace'
```

`Bassia` passes arguments to Git without invoking a shell. This keeps commit messages and paths from being interpreted as shell commands.

### Integration: syntactic first, then semantic

Every successful run leaves its changes in each component it touched as a result tag `agent/run-<id>/<n>` - the same name in every component. `integrate` consolidates the results of several runs, component by component, into one integration:

```powershell
dotnet run -- integrate -runs all -plan                          # the triage only; nothing changes
dotnet run -- integrate -runs 3f2a91c4,91ab22cd                  # runs by (short) id
dotnet run -- integrate -runs all -onto lib@v1 -skip 5e11aa00 -semantic 91ab22cd -resolve "claude -p --permission-mode acceptEdits --model opus"
dotnet run -- integrate advance integration-<id>                 # fast-forward the base branches to the result
```

1. **Triage.** For each component the runs changed, the results are considered oldest run first against a base: the component's default branch (usually `main`), or the branch or tag `-onto` names. Git classifies each one without a working tree (`git merge-tree`): `up_to_date` (already contained), `fast_forward`, `clean` (a three-way merge without conflicts) or `conflict`. The results git can merge are chained onto a simulated head in order, so a result that merges cleanly onto the base but collides with an earlier one is caught here too. Every step is then `syntactic` (git merges it), `semantic` (the resolver merges it) or `skip`. `-semantic` sends a result to the resolver even without a textual conflict, for a semantic review; `-skip` leaves it out. The triage also lists, for each result, the other runs it conflicts with directly.
2. **Syntactic steps first.** Each component is merged in its own checkout under `.workspace/integration-<id>/<component>`, on the branch `integration/<id>` started at the base. Every syntactic step is a `git merge --no-ff` of the run's result tag. If one conflicts after all, it moves to the back of the semantic queue.
3. **Semantic steps next.** For each remaining step Bassia starts the merge with `diff3` conflict markers, so the common ancestor is visible, and writes a **semantic brief**: the incoming run's prompt, baseline, command and commit records; which runs are already integrated and why; the history and diff of both sides since their common ancestor; the conflicted files; and instructions. The resolver command (by default `claude -p --permission-mode acceptEdits`, configurable as `[integration] resolver` in `config.toml`) runs in the component's working tree with the brief on stdin and these environment variables: `BASSIA_MERGE_BRIEF` (the brief's path), `BASSIA_COMPONENT`, `BASSIA_RUN_ID`, `BASSIA_INTEGRATION_ID` and `BASSIA_ROOT`. When it exits with code 0 and no conflict marker is left, Bassia commits the merge. A non-zero exit or leftover markers abort that step, which is recorded as `failed`; the other steps still go ahead.
4. **Result.** Each merge commit's message is a subject and an `[integration]` TOML record (run, source tag, strategy, rationale, conflicts, resolver). The result is tagged `integration/<id>/<n>` - one identically named annotated tag across the components - and branch and tag are pushed to the component's source-of-truth repo. That tag is a baseline like any other: `agent -select app@integration/<id>/0,...` starts the next run from it. Nothing else moves: `integrate advance <id>` fast-forwards each component's base branch to the result, and refuses if the branch moved since the integration was built on it, or if the base was a tag.

The integration's record (`integration.toml`: runs, resolver, and per component its base, every step's triage, strategy, conflicts, outcome, merge commit and brief) is committed to `.agentic-runs` next to the run records and tagged `integration/<id>/<lineage>`. Status is `completed`, `partial` (a step or component failed; what did merge is still published), or `cancelled`.

### Interactive frontend

```powershell
dotnet run -- -C R:\ ui
```

`ui` opens a live session over the monorepo the current directory belongs to (it fails with the usual TOML error outside one, or when the terminal is not interactive). It is a layer over the same model and commands as above, not a second implementation.

It has two boards and the integration control panel, always one keystroke apart; each board is a wallboard of rectangles carrying that item's own facts:

```
┌─ app ──────────────────────┐   ┌─ ▸ tool ───────────────────┐   ┌─ ▸ 3f2a91c4 ⠸ ─────────────────┐
│ 3 tags · 2 branches        │   │ 3 tags · 2 branches        │   │ RUNNING                   1:35 │
│ needs: lib, ui@vendor/ui   │   │ needs: lib                 │   │ app, lib                       │
│ used by: -                 │   │ used by: -                 │   │ ░░░▓▓░░░░░░░                   │
│ 7 runs · v1.2              │   │ 7 runs · v1.2              │   │ add the changelog              │
└────────────────────────────┘   └────────────────────────────┘   │ editing CHANGELOG.md           │
          │        │                            │                 └────────────────────────────────┘
          │        ├────────────────────────────┤
          ▼        ▼                            ▼
┌─ lib ──────────────────────┐   ┌─ ui ───────────────────────┐
│ ● 2 running · 7 runs       │   │ 7 runs · v1.2              │
└────────────────────────────┘   └────────────────────────────┘
```

- **`1` — Components**: one rectangle per component from `components.toml`, laid out in layers and joined by ASCII lines that follow `references`, so the list and the dependency graph are the same picture. A card shows its tags and branches, what it needs and what needs it, how many recorded runs touched it, and how many runs are working on it right now.
- **`2` — Agentic runs**: one rectangle per run — the ones this session started first, then everything recorded in `.agentic-runs`. A card shows the run's short id, phase, elapsed time, components, command and the agent's latest output line; a live run animates.
- **`3` — Integration**: the control panel for [integration](#integration-syntactic-first-then-semantic). It lists the runs that have results; `space` chooses one and `a` chooses all or none. Below that is the live triage of the chosen runs: per component, every step in execution order with its triage, strategy (`SYNTAX`, `SEMANTIC` or `SKIP`, starred when you chose it), conflicted files and the runs it collides with. `o` puts a component onto another branch or tag, `s` overrides a step's strategy (a conflicting step can only go to the resolver or be skipped), `c` clears both, and `i` confirms the resolver command and integrates in the background. While it runs the panel shows each step's outcome and the resolver's latest output line, and `x` stops it (kills a working resolver and publishes nothing further). `d` opens a recorded integration with its notes and briefs, and `v` advances its base branches. The recorded integrations are listed at the bottom.

Keys: `1`/`2`/`3` switch views from any screen, arrows move the selection, `enter` opens the selected card, `n` starts a run, `x` stops the selected run, `t` tags the selected component, `g` opens the dependency tree with its exports, `r` refreshes, `?` lists the keys and `q` quits. The detail screens behind `enter` are what the menu frontend showed:

- **A component**: its branches and tags (annotated tags marked, since only those can be selected for a run), its git tree, and every agentic run that touched it with the result tag it left. `t` runs `git tag -a` on a chosen branch, tag or commit and the view refreshes.
- **A run**: its record, per-component results, and the tail of the agent's output for a run this session started. `x` stops it; `i` chooses its results on the integration panel; `m` lists suspend / resume / hand off, which are not implemented yet and report so without changing anything.
- **The dependency tree** (`g`): the graph as a text tree, exportable as Markdown with a Mermaid block (`components.md`) or as SVG (`components.svg`).

**Starting a run** (`n`) asks for components (the reference closure is completed automatically) and an annotated tag for each, then the prompt, model, effort and context; the composed `-run` command (default agent command `claude -p --permission-mode acceptEdits`) can be edited before the run starts through the same path as `bassia agent -select ... -run ...`. The wizard then hands the run to the background and the board comes straight back, so the next run can be started while the first is still going. A background run's output is captured onto its card instead of reaching the screen.

**Stopping a run** (`x`) kills the agent's whole process tree, records the run with status `cancelled` and keeps its run folder for inspection; nothing is committed into any component, and `bassia agent retry` refuses a cancelled run the same way it refuses a failed one. Quitting while runs are still going offers to stop them and waits, rather than orphaning the agent processes.

## Build

```powershell
dotnet build
```

## Test

```powershell
dotnet test                                   # everything
dotnet test --filter Category!=EndToEnd       # skip the tests that need network access
dotnet test --filter Category=EndToEnd --logger "console;verbosity=detailed"
```

Most tests drive the CLI in-process against components created locally. `ParallelAgentRunEndToEndTests`
(`Category=EndToEnd`) is the whole-pipeline proof and works differently: it builds a monorepo in the temp folder,
clones [jk09/example](https://github.com/jk09/example) twice as two components, tags each component's HEAD, and
starts one agentic run per component **at the same time**, each as its own `bassia` process. It then checks that
every run pushed an annotated result tag onto its own component's baseline, left the baseline tag and `main`
where they were, stayed out of the other component, and recorded both runs in `.agentic-runs`; the recorded
`created`/`finished` timestamps have to overlap, which is what makes it a parallel run rather than two runs in a
row. The run command is a deterministic shell command standing in for a coding agent, so the proof is about
Bassia and not about a model's output.

It writes a Markdown report of every commit, tag and record it verified, to the test output and to a file. Three
environment variables steer it:

- `BASSIA_E2E_PROOF` — where to write the report (default: a timestamped file in the temp folder).
- `BASSIA_E2E_KEEP` — keep the monorepo after the test so the refs in the report can be inspected.
- `BASSIA_E2E_COMPONENT_URL` — clone from a local mirror instead of GitHub, to run offline.

The current command surface is intentionally small. Future features can add workflow-specific behavior while continuing to use Git for repository compatibility.