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

`init [directory]` initializes a `Bassia` monorepo in the given, empty folder (default: the current directory): a `.bassia` meta-repo (with its own Git history and `config.toml`/`components.toml`) and a `.workspace` folder for agent run folders. `add-component <url> [name]` clones a repository as a bare component alongside the meta-repo and registers it in `.bassia/components.toml`, under the given logical name or, by default, one inferred from the URL. Both commands print a JSON result on stdout (success) or stderr (failure) for machine consumption.

`-C <path>`, given before the command, runs `bassia` as if it had been started in `<path>` instead of the current directory, same as `git -C <path>`. It can be repeated, with each occurrence resolved relative to the directory left by the previous one.

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

### Interactive frontend

```powershell
dotnet run -- -C R:\ ui
```

`ui` opens a menu-driven session over the monorepo the current directory belongs to (it fails with the usual JSON error outside one, or when the terminal is not interactive). It is a layer over the same model and commands as above, not a second implementation:

- **Components**: every component from `components.toml` with its source and references; the dependency graph drawn in the console, exportable as Markdown with a Mermaid block (`components.md`) or as SVG (`components.svg`).
- **A component**: its branches and tags (annotated tags marked, since only those can be selected for a run), its git tree, and every agentic run that touched it with the result tag it left. *Create annotated tag* runs `git tag -a` on a chosen branch, tag or commit and the view refreshes.
- **Agentic runs**: every run recorded in `.agentic-runs`, newest first, with status, selection and per-component results. Stop / suspend / resume / hand off / integrate results are listed but not implemented yet; they report so and change nothing.
- **Start an agentic run**: pick components (the reference closure is completed automatically) and an annotated tag for each, then the prompt, model, effort and context; the composed `-run` command (default agent command `claude -p --permission-mode acceptEdits`) can be edited before the run starts through the same path as `bassia agent -select ... -run ...`.

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