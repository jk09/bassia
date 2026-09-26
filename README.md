# `Bassia`

`Bassia` is a small command-line interface built on top of Git. It delegates repository storage, history, branching, and commit behavior to the installed Git executable instead of reimplementing Git internals.

## Requirements

- .NET SDK 10.0 or later
- Git available on `PATH`

## Command line

`bassia` is operated mainly by AI agents, so the command line is its primary interface: everything `bassia ui` and
`bassia web` can do has a command, and every command answers in TOML.

```
bassia [-C <path>] <command> [<subcommand>] [-switch [value]]...
```

- Switches are case-insensitive and may be written `-name` or `--name`. Lists are comma-separated
  (`-select app@v1,lib@v1`) or given by repeating the switch.
- A command's main argument may be given without its switch: `bassia run show 3f2a91c4` is
  `bassia run show -id 3f2a91c4`.
- A rest-of-line switch (`-run`, `-resolve`) takes everything after it verbatim, so it comes last.
- `-C <path>`, before the command, runs `bassia` as if it had been started in `<path>`, same as `git -C <path>`. It
  can be repeated, each occurrence relative to the previous one.
- Runs and integrations are named by their full id, the `<id>` part, or any prefix of at least four digits such as
  the 8-digit short id every listing shows.

`bassia help` lists every command, `bassia help <command> [<subcommand>]` (or `-help` on any command) explains one:
its usage, every switch and examples. Help is TOML too.

| Area | Commands |
| --- | --- |
| Monorepo | `init [-path <dir>]`, `status`, `version` |
| Configuration | `config list`, `config get -key <k>`, `config set -key <k> -value <v>` |
| Components | `component list`, `component add -url <url> [-name <n>] [-references <c[:path]>,...]`, `component show -name <c>`, `component set -name <c> -references ...\|-clear-references`, `component remove -name <c> [-purge]`, `component tag -name <c> -tag <t> [-ref <commit-ish>] [-message <m>]` |
| Dependencies and history | `graph [-name <c>] [-format board\|tree\|mermaid\|svg] [-out <file>]`, `log [-component <c>,...] [-only] [-limit <n>] [-page <n>]` |
| Agentic runs | `run start -select <c@tag>,... [-detach] (-prompt <text> [-agent] [-model] [-effort] [-context] \| -run <command...>)`, `run list [-status <s>] [-component <c>]`, `run show`, `run logs [-tail <n>]`, `run wait [-timeout <s>]`, `run stop`, `run retry`, `run abandon`, `run diff [-component <c>] [-patch]` |
| Integration (merging) | `integration plan -runs <ids>\|all [-onto] [-semantic] [-skip]`, `integration start ... [-detach] [-resolve <command...>]`, `integration list`, `integration show`, `integration logs`, `integration wait`, `integration stop`, `integration advance` |
| Frontends | `ui`, `web [-port <n>] [-no-open]` |

The commands this replaced - `agent`, `add-component`, `integrate`, `commit` and `branch` - fail with exit code 2 and
name their successor.

A typical agent session over a fresh monorepo:

```sh
bassia init -path R:\
bassia -C R:\ component add -url https://github.com/myrepo/lib.git
bassia -C R:\ component add -url https://github.com/myrepo/app.git -references lib
bassia -C R:\ component tag -name lib -tag v1
bassia -C R:\ component tag -name app -tag v1
bassia -C R:\ run start -select app@v1,lib@v1 -detach -prompt "add a changelog"     # returns run_id at once
bassia -C R:\ run show 3f2a91c4                                                     # live? last output line
bassia -C R:\ run wait 3f2a91c4 -timeout 1800                                       # ok when completed
bassia -C R:\ run diff 3f2a91c4 -patch                                              # what it changed
bassia -C R:\ integration plan -runs all
bassia -C R:\ integration start -runs all
bassia -C R:\ integration advance 5e11aa00                                          # fast-forward main
```

### Results

Every command prints its result as TOML — on stdout with exit code 0 when it succeeded, on stderr with exit code 1
when it failed, and exit code 2 when the command line itself does not parse (unknown command, subcommand or switch,
a missing value). An agent or a script reads it without scraping prose. TOML is what `Bassia` stores everything else
in (`config.toml`, `components.toml`, the `run.toml` records), so a caller uses one parser and one vocabulary of
`snake_case` keys throughout.

A result always opens with the comment line `# bassia result`, then `ok`, the `command` (e.g. `run start`), and either
`message` (success) or `error` (failure); commands that have more to report add their own keys after those, and lists
come as arrays of tables (`[[component]]`, `[[run]]`, ...), named as in the stored records. The marker exists because
`bassia run start` without `-detach` lets the agent command inherit stdout: a caller takes the last marker line as the
start of the result and treats anything before it as agent output. Values TOML cannot express are simply absent keys —
there is no `null`.

Pictures are part of the result, as multi-line literal strings in plain ASCII, so the output stays one valid TOML
document: `graph` for the dependency graph (`bassia graph`, `status`, `component show`) and for history (`log`), `table`
for listings, `card` for a run, `steps` for an integration, `output` for a detached job's log, and `stat`/`patch` for
`run diff`.

```toml
# bassia result
ok = true
command = "graph"
message = "Dependency graph of 2 component(s) (board)."
format = "board"
graph = '''
+- app ----------------------+
| 1 tags - 1 branches        |
| needs: lib                 |
| used by: -                 |
| 3 runs - v1                |
+----------------------------+
               |
               v
+- lib ----------------------+
| 2 tags - 1 branches        |
| needs: -                   |
| used by: app               |
| 3 runs - v1                |
+----------------------------+
'''
```

`bassia log -component app` merges the history of `app` and everything it depends on into one timeline, one lane per
component; `-only` (or a component without dependencies) shows git's own commit graph instead:

```
app lib
     *   2026-09-21 14:02  129b996f87  integrate(fb66859a): add the changelog  (integration/fb66859a.../0)
 *   |   2026-09-21 13:49  85ca329ea7  agent(ed931d08): add the changelog  (agent/run-ed931d08.../0)
     *   2026-09-21 13:40  b90be7506c  fix the parser
```

### Live runs: detached, watched, stopped

`run start` without `-detach` behaves like a classic command: the agent's output streams to the terminal and the
result follows when the run ends; Ctrl-C stops the run and records it as `cancelled`. With `-detach` the selection is
checked first (a mistake is reported at once), then the run continues in a background `bassia` process with none of
the caller's standard handles, and the result — `run_id`, `pid`, `log` — is printed immediately. `integration start
-detach` works the same way.

Whoever executes a run or integration - a foreground `bassia`, a detached one, or `bassia ui`/`bassia web` - registers
it as a job in `<workspace>/.jobs/`. That is what lets another process, usually another agent:

- see that it is **live** (`run list -status live`, `run show`: `live`, `pid`, `last_output`, and an ASCII `card`),
- read a detached job's **output** as it grows (`run logs`; the result goes to a separate `result_file`),
- **wait** for it (`run wait`, `integration wait`, with an optional `-timeout`),
- **stop** it (`run stop`, `integration stop`): the job is asked to stop, which kills the agent's (or resolver's)
  process tree and records the work as `cancelled`; a job that does not react within `-timeout` seconds has its
  process killed - never the process of `bassia ui`/`bassia web`, which stop their own runs.

A run recorded as `started` whose process is gone (killed, crashed, machine restarted) shows as `stale`; `run stop`
records it as `cancelled`.

### Configuration

`bassia config list|get|set` reads and writes the settings of `.bassia/config.toml`, keeping its comments, and commits
each change to the meta-repo:

| Key | Default | Meaning |
| --- | --- | --- |
| `workspace.path` | `.workspace` | Folder of the run and integration checkouts (and of `.jobs`) |
| `agent.command` | `claude -p --permission-mode acceptEdits` | Agent command of a run started from `-prompt`, `bassia ui` or `bassia web` |
| `agent.commit.subject` | `agent({short_id}): {summary}` | Subject of a run's result commits |
| `integration.resolver` | `claude -p --permission-mode acceptEdits` | Command that resolves a semantic merge |

`component add|set|remove` edit `.bassia/components.toml` the same way and reject a change that would leave a cycle
or a reference to an unregistered component.

### Agentic runs

```powershell
bassia component tag -name app -tag v0                # -select needs an annotated tag per component
bassia component tag -name lib -tag v0
bassia run start -select app@v0,lib@v0 -run claude -p "add a hello world script" --permission-mode acceptEdits
bassia run retry <id>                                 # re-run a failed commit/tag/push sequence
bassia run abandon <id>                               # discard the run's folder
```

For a quick hand-check against the Debug build, `scripts/` has two helpers: `New-TestMonorepo.ps1` creates a throwaway monorepo in `%TEMP%` with the [jk09/example](https://github.com/jk09/example) component added and its HEAD tagged `tag-base`, and returns the path; `Invoke-AgentRun.ps1 -Monorepo <path> -Prompt <prompt>` runs `claude -p --permission-mode acceptEdits "<prompt>"` over it (`-Select` and `-Agent` override the defaults) and prints where each result landed.

```powershell
$r = ./scripts/New-TestMonorepo.ps1
./scripts/Invoke-AgentRun.ps1 -Monorepo $r -Prompt 'write hello world in C# as example/HelloWorld.cs'
```

`run start -select <component@tag>[,...] -run <command>` runs an agent on an isolated copy of the selected monorepo state:

1. Every `-select` entry names a component registered in `.bassia/components.toml` and an **annotated tag** in its source-of-truth repo (`R:\<component>`); the tag is the immutable provenance recorded for the run. Unregistered names, branches and bare commits are rejected.
2. The selection must cover the full transitive closure of the reference graph (`references = ["lib"]` or `references = [{ name = "lib", path = "libs/lib" }]` in `components.toml`; the graph must be acyclic). Selecting `app` without the `lib` it references fails before anything is materialized — a run pins every component it touches, so nothing is ever resolved from a mutable reference.
3. The run gets a random id, `agent-run-<id>` with `<id>` a GUID (32 hex digits), and the folder `.workspace/agent-run-<id>/`. The id is unique without any coordination, so runs from different workspaces or machines never collide on the branches and tags they push to a shared component repo. Every selected component is cloned from its source-of-truth repo and checked out there, side by side, on the run branch `agent/run-<id>`. Where one component references another, the referenced component appears inside it as a junction to that sibling checkout, at the subfolder `components.toml` records; the nested path is excluded from the referring repo's index, so each repo only ever commits its own files.
4. The `-run` command is started by the platform shell (`cmd.exe` / `sh`) in `.workspace/agent-run-<id>/`, with `BASSIA_ROOT`, `BASSIA_RUN_ID` and `BASSIA_RUN_DIR` set.
5. Before the agent starts and after it finishes, the run record (`run.toml`: selection, resolved commits, command, timestamps, status, per-component results) is committed to the bare repo `.agentic-runs/.git`, at the monorepo root alongside `.bassia` and `.workspace`, with plumbing commands only (no checkout, no branch) and tagged `agent/run-<id>/<lineage>`. It lives outside both the meta-repo and the workspace, so the run folder can be discarded once its results are pushed while the record survives.
6. When the agent exits successfully, each changed component is committed on its run branch, tagged `agent/run-<id>/<counter>`, and branch + tag are pushed to its source-of-truth repo. The counter is the next unused index among the component's existing `agent/run-<id>/*` tags — all of them sit on the run branch, so the checkout's own tag list is the whole sequence; a run's first result is `/0`. Nothing is merged or checked out there. The sequence is not transactional: a failed component leaves the run `partial`; `run retry` finishes the remaining steps and `run abandon` discards the run folder without touching anything already pushed.

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

Every successful run leaves its changes in each component it touched as a result tag `agent/run-<id>/<n>` - the same name in every component. `integration start` consolidates the results of several runs, component by component, into one integration:

```powershell
bassia integration plan -runs all                                # the triage only; nothing changes
bassia integration start -runs 3f2a91c4,91ab22cd                 # runs by (short) id
bassia integration start -runs all -onto lib@v1 -skip 5e11aa00 -semantic 91ab22cd -resolve claude -p --permission-mode acceptEdits --model opus
bassia integration advance <id>                                  # fast-forward the base branches to the result
```

1. **Triage.** For each component the runs changed, the results are considered oldest run first against a base: the component's default branch (usually `main`), or the branch or tag `-onto` names. Git classifies each one without a working tree (`git merge-tree`): `up_to_date` (already contained), `fast_forward`, `clean` (a three-way merge without conflicts) or `conflict`. The results git can merge are chained onto a simulated head in order, so a result that merges cleanly onto the base but collides with an earlier one is caught here too. Every step is then `syntactic` (git merges it), `semantic` (the resolver merges it) or `skip`. `-semantic` sends a result to the resolver even without a textual conflict, for a semantic review; `-skip` leaves it out. The triage also lists, for each result, the other runs it conflicts with directly.
2. **Syntactic steps first.** Each component is merged in its own checkout under `.workspace/integration-<id>/<component>`, on the branch `integration/<id>` started at the base. Every syntactic step is a `git merge --no-ff` of the run's result tag. If one conflicts after all, it moves to the back of the semantic queue.
3. **Semantic steps next.** For each remaining step Bassia starts the merge with `diff3` conflict markers, so the common ancestor is visible, and writes a **semantic brief**: the incoming run's prompt, baseline, command and commit records; which runs are already integrated and why; the history and diff of both sides since their common ancestor; the conflicted files; and instructions. The resolver command (by default `claude -p --permission-mode acceptEdits`, configurable as `[integration] resolver` in `config.toml`) runs in the component's working tree with the brief on stdin and these environment variables: `BASSIA_MERGE_BRIEF` (the brief's path), `BASSIA_COMPONENT`, `BASSIA_RUN_ID`, `BASSIA_INTEGRATION_ID` and `BASSIA_ROOT`. When it exits with code 0 and no conflict marker is left, Bassia commits the merge. A non-zero exit or leftover markers abort that step, which is recorded as `failed`; the other steps still go ahead.
4. **Result.** Each merge commit's message is a subject and an `[integration]` TOML record (run, source tag, strategy, rationale, conflicts, resolver). The result is tagged `integration/<id>/<n>` - one identically named annotated tag across the components - and branch and tag are pushed to the component's source-of-truth repo. That tag is a baseline like any other: `run start -select app@integration/<id>/0,...` starts the next run from it. Nothing else moves: `integration advance <id>` fast-forwards each component's base branch to the result, and refuses if the branch moved since the integration was built on it, or if the base was a tag.

The integration's record (`integration.toml`: runs, resolver, and per component its base, every step's triage, strategy, conflicts, outcome, merge commit and brief) is committed to `.agentic-runs` next to the run records and tagged `integration/<id>/<lineage>`. Status is `completed`, `partial` (a step or component failed; what did merge is still published), or `cancelled`.

### Interactive frontend

```powershell
bassia -C R:\ ui
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

**Starting a run** (`n`) asks for components (the reference closure is completed automatically) and an annotated tag for each, then the prompt, model, effort and context; the composed `-run` command (default agent command `claude -p --permission-mode acceptEdits`) can be edited before the run starts through the same path as `bassia run start -select ... -run ...`. The wizard then hands the run to the background and the board comes straight back, so the next run can be started while the first is still going. A background run's output is captured onto its card instead of reaching the screen.

**Stopping a run** (`x`) kills the agent's whole process tree, records the run with status `cancelled` and keeps its run folder for inspection; nothing is committed into any component, and `bassia run retry` refuses a cancelled run the same way it refuses a failed one. Runs and integrations started here are registered as jobs, so `bassia run stop` from another shell stops them too. Quitting while runs are still going offers to stop them and waits, rather than orphaning the agent processes.

### Web dashboard

```powershell
bassia -C R:\ web                        # http://127.0.0.1:8080/, opened in the browser
bassia -C R:\ web -port 9000 -no-open
```

`web` serves a dashboard over the monorepo, in the spirit of [Fossil](https://fossil-scm.org)'s built-in web
interface. It listens on 127.0.0.1 only. If the port is taken it uses the next free one, and it prints the URL. It
runs until Ctrl-C, which also stops (and records as `cancelled`) any run it started. Every page is plain HTML with
a menu, and works without JavaScript; the script only makes live parts update in place.

- **Home**: counts of components, live, completed and failed runs, and integrations. Below them are the component
  graph (the same SVG as the graph export in `bassia ui`, with every box a link) and the latest runs and
  integrations.
- **Components**: each component with what it needs, what uses it, its annotated tags, branches and runs. A
  component's page lists its branches and tags, and the runs that touched it with their result tags.
- **Timeline**: one chronological list of commits across the components you choose. Each commit shows its
  component, hash, subject, refs and author. The components a choice depends on join it automatically, so a
  library comes along with the application that references it. The whole monorepo is never logged: without a
  choice the page asks for one, and each component's log is read only as far as the current page needs (`?n=` sets
  the page size). Agent and integration tags link to the run or integration that made them, and every commit opens
  with its full message and diffstat.
- **Runs**: the runs started from this dashboard, updating live, then every recorded run. A run's page shows its
  record and results. For a live run it also streams the agent's output as it happens and has a **Stop** button.
- **New run**: a Claude-like prompt. Tick the components and pick an annotated tag for each; components they
  depend on join at the tag chosen for them. Then write the prompt, where enter sends and shift+enter adds a line,
  and optionally choose the model, effort and extra context. The composed `-run` command is previewed. Type your
  own command to run exactly that. Sending starts the run in the background, through the same path as `bassia
  run start`, and opens its live page.
- **Integrations**: every integration with its runs and result tags. Each opens to its per-component steps
  (triage, strategy, conflicts, outcome) and the paths of its semantic briefs. **Preview triage** shows how
  chosen runs would integrate, like `integration plan`; performing an integration stays with `bassia integration
  start` and `bassia ui`.

Forms carry a per-server token, and requests must be addressed to `127.0.0.1` or `localhost`. So a web page
open in the same browser can neither start runs through the dashboard nor read it through DNS rebinding.

## Build

```powershell
dotnet build
dotnet run --project Bassia -- help           # or put the built Bassia executable (or bassia.cmd) on PATH
```

## Test

```powershell
dotnet test                                   # everything
dotnet test --filter Category!=EndToEnd       # skip the tests that need network access
dotnet test --filter Category=EndToEnd --logger "console;verbosity=detailed"
```

Most tests drive the CLI in-process against components created locally. `CliSurfaceTests` covers the grammar, the
help of every command and the monorepo, configuration, component, graph and log commands; `RunCommandTests` the run
and integration commands, including detached runs and stopping them, which start the built `bassia` apphost as a real
background process. `ParallelAgentRunEndToEndTests`
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

Future features can add workflow-specific behavior while continuing to use Git for repository compatibility.