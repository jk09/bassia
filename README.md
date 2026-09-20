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
dotnet run -- setup add-component https://github.com/myrepo/component_1.git
```

`init [directory]` initializes a `Bassia` monorepo in the given, empty folder (default: the current directory): a `.bassia` meta-repo (with its own Git history and `config.toml`/`components.toml`) and a `.workspace` folder for agent worktrees. `setup add-component <url>` clones a repository as a bare component alongside the meta-repo and registers it in `.bassia/components.toml`. Both commands print a JSON result on stdout (success) or stderr (failure) for machine consumption.

### Agentic runs

```powershell
git -C R:\example tag -a v0 -m "baseline"          # -select needs an annotated tag
dotnet run -- agent -select example@v0 -run "claude -p 'add a hello world script' --permission-mode acceptEdits"
dotnet run -- agent retry agentic-run-1              # re-run a failed commit/tag/push sequence
dotnet run -- agent abandon agentic-run-1            # discard the run's workspace and cache
```

`agent -select <component@tag>[,...] [-pin <component@tag>[,...]] -run <command>` runs an agent on an isolated copy of the selected monorepo state:

1. Every `-select` entry names a component registered in `.bassia/components.toml` and an **annotated tag** in its source-of-truth repo (`R:\<component>`); the tag is the immutable provenance recorded for the run. Unregistered names, branches and bare commits are rejected.
2. The run gets a unique id (`agentic-run-N`) and folder `.workspace/agentic-run-N/`. Selected components are cloned from their source-of-truth repos and checked out there on the run branch `agent/agentic-run-N`.
3. Components that are only *referenced* by a selected component (`references = ["lib"]` or `references = [{ name = "lib", path = "libs/lib" }]` in `components.toml`; the graph must be acyclic) are checked out once into the run's cache (`.cache/agentic-run-N/<component>`) and junctioned into the referencing checkout at the expected subfolder. They use their source repo's `HEAD` unless pinned with `-pin`. A component that is both selected and referenced stays a direct checkout; the junction points at it.
4. The `-run` command is started by the platform shell (`cmd.exe` / `sh`) in `.workspace/agentic-run-N/`, with `BASSIA_ROOT`, `BASSIA_RUN_ID` and `BASSIA_RUN_DIR` set.
5. Before the agent starts and after it finishes, the run record (`run.toml`: selection, resolved commits, command, timestamps, status, per-component results) is committed to the bare repo `.workspace/.agentic-runs/.git` with plumbing commands only (no checkout, no branch) and tagged `agent/agentic-run-N/<lineage>`.
6. When the agent exits successfully, each changed component (direct or cached) is committed on its run branch, tagged `agent/agentic-run-N/0`, and branch + tag are pushed to its source-of-truth repo. Nothing is merged or checked out there. The sequence is not transactional: a failed component leaves the run `partial`; `agent retry` finishes the remaining steps and `agent abandon` discards the workspace and cache without touching anything already pushed.

The workspace and cache locations can be moved (for example to another volume) via `.bassia/config.toml`:

```toml
[workspace]
path = 'D:\bassia-workspace'
cache = 'D:\bassia-cache'
```

`Bassia` passes arguments to Git without invoking a shell. This keeps commit messages and paths from being interpreted as shell commands.

## Build

```powershell
dotnet build
```

The current command surface is intentionally small. Future features can add workflow-specific behavior while continuing to use Git for repository compatibility.