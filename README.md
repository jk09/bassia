# `Bassia`

`Bassia` is a small command-line interface built on top of Git. It delegates repository storage, history, branching, and commit behavior to the installed Git executable instead of reimplementing Git internals.

## Requirements

- .NET SDK 10.0 or later
- Git available on `PATH`

## Command line

`bassia` is operated mainly by AI agents, so the command line is its primary interface: everything `bassia web` can
do has a command, and every command answers in TOML.

```
bassia [-C <path>] <command> [<subcommand>] [-switch [value]]...
```

- Switches are case-insensitive and may be written `-name` or `--name`. Lists are comma-separated
  (`-select app@v1,lib@v1`) or given by repeating the switch.
- A command's main argument may be given without its switch: `bassia run show brave-otter-3f2a91` is
  `bassia run show -id brave-otter-3f2a91`.
- A rest-of-line switch (`-run`, `-resolve`) takes everything after it verbatim, so it comes last.
- `-C <path>`, before the command, runs `bassia` as if it had been started in `<path>`, same as `git -C <path>`. It
  can be repeated, each occurrence relative to the previous one.
- Runs and integrations are named by their full id (`agent-run-brave-otter-3f2a91`), its `<key>` part
  (`brave-otter-3f2a91`, the short id every listing shows), or any prefix of the key of at least four characters
  (`brave-ot`).

`bassia help` lists every command, `bassia help <command> [<subcommand>]` (or `-help` on any command) explains one:
its usage, every switch and examples. Help is TOML too.

| Area | Commands |
| --- | --- |
| Monorepo | `init [-path <dir>]`, `status`, `version` |
| Configuration | `config list`, `config get -key <k>`, `config set -key <k> -value <v>` |
| Components | `component list`, `component add -url <url> [-name <n>] [-references <c[:path]>,...]`, `component show -name <c>`, `component set -name <c> -references ...\|-clear-references`, `component remove -name <c> [-purge]`, `component tag -name <c> -tag <t> [-ref <commit-ish>] [-message <m>]` |
| Splitting components | `component survey -name <c> [-depth <n>] [-limit <n>]`, `component split -plan <file>\|- [-name <c>] [-dry-run]` |
| Dependencies and history | `graph [-name <c>] [-format board\|tree\|mermaid\|svg] [-out <file>]`, `log [-component <c>,...] [-only] [-branch <b>] [-run <id>,...\|all] [-limit <n>] [-page <n>]` |
| Agentic runs | `run start -select <c@tag\|hash>,... [-detach] (-prompt <text> [-agent] [-model] [-effort] [-context] \| -run <command...>)`, `run list [-status <s>] [-component <c>]`, `run show`, `run logs [-tail <n>]`, `run wait [-timeout <s>]`, `run stop`, `run retry`, `run abandon`, `run diff [-component <c>] [-patch]` |
| Integration (merging) | `integration plan -runs <ids>\|all [-onto] [-semantic] [-skip]`, `integration start ... [-detach] [-resolve <command...>]`, `integration list`, `integration show`, `integration logs`, `integration wait`, `integration stop`, `integration advance` |
| Web dashboard | `web [-port <n>] [-no-open]` |

The commands this replaced - `agent`, `add-component`, `integrate`, `commit`, `branch` and `ui` - fail with exit code
2 and name their successor.

A typical agent session over a fresh monorepo:

```sh
bassia init -path R:\
bassia -C R:\ component add -url https://github.com/myrepo/lib.git
bassia -C R:\ component add -url https://github.com/myrepo/app.git -references lib
bassia -C R:\ component tag -name lib -tag v1
bassia -C R:\ component tag -name app -tag v1
bassia -C R:\ run start -select app@v1,lib@v1 -detach -prompt "add a changelog"     # returns run_id at once
bassia -C R:\ run show brave-otter-3f2a91                                           # live? last output line
bassia -C R:\ run wait brave-otter-3f2a91 -timeout 1800                             # ok when completed
bassia -C R:\ run diff brave-otter-3f2a91 -patch                                    # what it changed
bassia -C R:\ integration plan -runs all
bassia -C R:\ integration start -runs all
bassia -C R:\ integration advance steady-heron-5e11aa                               # fast-forward main
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
     *   2026-09-21 14:02  129b996f87  integrate(steady-heron-k2m8qa): add the changelog  (integration/steady-heron-k2m8qa/0)
 *   |   2026-09-21 13:49  85ca329ea7  agent(brave-otter-3f2a91): add the changelog  (agent-run/brave-otter-3f2a91/0)
     *   2026-09-21 13:40  b90be7506c  fix the parser
```

Every `[[commit]]` names the run its message records: `kind = "result"` and `run_id` for a run's result commit,
`kind = "integration"`, `integration_id` and the merged `run_id` for an integration's merge commit. `-branch main`
narrows each component to that branch, which shows what is on the mainline and which runs put it there.

`bassia log -run <id>,...` (or `-run all`) answers where runs' work went: across every component the runs selected
(or the `-component`s given), their result commits and the integration merges that brought them in, each with
`on_default_branch`, and per run and component the result tag, `landed` (the result is reachable from the
component's default branch) and `merged_by` (the integrations whose merge of it is there):

```
2 commit(s) of 1 run(s) in lib, app; 1 of 1 result(s) landed on their component's default branch.
lib app
     *   2026-09-21 14:02  129b996f87  integrate(quiet-fern-5e11aa): brave-otter-3f2a91 add the changelog  (main, integration/quiet-fern-5e11aa/0)
     *   2026-09-21 13:49  85ca329ea7  agent(brave-otter-3f2a91): add the changelog  (agent-run/brave-otter-3f2a91/0)
```

### Live runs: detached, watched, stopped

`run start` without `-detach` behaves like a classic command: the agent's output streams to the terminal and the
result follows when the run ends; Ctrl-C stops the run and records it as `cancelled`. With `-detach` the selection is
checked first (a mistake is reported at once), then the run continues in a background `bassia` process with none of
the caller's standard handles, and the result — `run_id`, `pid`, `log` — is printed immediately. `integration start
-detach` works the same way.

Whoever executes a run or integration - a foreground `bassia`, a detached one, or `bassia web` - registers
it as a job in `<workspace>/.jobs/`. That is what lets another process, usually another agent:

- see that it is **live** (`run list -status live`, `run show`: `live`, `pid`, `last_output`, and an ASCII `card`),
- read a detached job's **output** as it grows (`run logs`; the result goes to a separate `result_file`),
- **wait** for it (`run wait`, `integration wait`, with an optional `-timeout`),
- **stop** it (`run stop`, `integration stop`): the job is asked to stop, which kills the agent's (or resolver's)
  process tree and records the work as `cancelled`; a job that does not react within `-timeout` seconds has its
  process killed - never the process of `bassia web`, which stops its own runs.

A run recorded as `started` whose process is gone (killed, crashed, machine restarted) shows as `stale`; `run stop`
records it as `cancelled`.

### Configuration

`bassia config list|get|set` reads and writes the settings of `.bassia/config.toml`, keeping its comments, and commits
each change to the meta-repo:

| Key | Default | Meaning |
| --- | --- | --- |
| `workspace.path` | `.workspace` | Folder of the run and integration checkouts (and of `.jobs`) |
| `agent.command` | `claude -p --permission-mode acceptEdits` | Agent command of a run started from `-prompt` or `bassia web` |
| `agent.commit.subject` | `agent({short_id}): {summary}` | Subject of a run's result commits |
| `integration.resolver` | `claude -p --permission-mode acceptEdits` | Command that resolves a semantic merge |

`component add|set|remove` edit `.bassia/components.toml` the same way and reject a change that would leave a cycle
or a reference to an unregistered component.

### Splitting a component

A component is the unit a run clones, so one that has grown too large, or mixes architecturally distinct parts, is
broken up to keep runs cheap and dependencies precise. Deciding the breakdown is judgement - typically an LLM's;
performing it is `bassia`'s:

```powershell
bassia component survey app                           # folders, sizes, change counts, co-changing folders, a plan skeleton
bassia component split -plan split.toml -dry-run      # check the plan; every problem is listed, nothing changes
bassia component split -plan split.toml               # do it
```

`survey` reports the component's folders (to `-depth`, default 2) with their files, bytes and the commits that touched
them, the pairs of folders most often changed in the same commit (a split between them turns one change into several),
and a `plan` skeleton. The plan is TOML:

```toml
source = "app"
branch = "main"                         # optional: the source's default branch
shared = ["LICENSE", "README.md"]       # optional: copied into every part, with history
drop = ["legacy"]                       # optional: left out of every part, with history
follow_renames = true                   # optional (default): files keep their history under earlier paths

[[part]]
name = "app-core"
paths = ["src/core/**", "tests/core"]
references = ["lib"]                    # optional: default is the source's references

[[part]]
name = "app-ui"
paths = ["src/ui", "!src/ui/generated"]
references = ["app-core", "lib"]
url = "https://github.com/myrepo/app-ui.git"   # optional: recorded in components.toml and as the repo's origin

[[referrer]]                            # optional: by default a referrer of the source references every part
name = "tool"
references = ["app-core"]
```

Patterns are relative to the component's root: `**` any folders, `*` within one folder, `?` one character; a pattern
matches everything below what it matches (`src/ui` is the folder), and `!pattern` excludes. Every file at the tip must
go to exactly one part, to `shared` or to `drop`; unallocated files, files several parts match, a part without files,
taken names, unknown references and cycles are reported together (`problems`, `unallocated`, `ambiguous`) and nothing
changes. A split is refused while a run on the source is live.

Each part becomes a new repository `<root>/<part>/.git` with the source's branch name:

- Its history is every source commit that changed one of its files, reduced to those files, with the original author,
  committer, dates and message plus a `Split-from: <source>@<commit>` trailer. Commits that changed none of its files
  are left out; merges stay merges where both sides changed the part and collapse otherwise.
- A file's history follows renames: a file moved into the part (from another part's folder or from nowhere) keeps its
  history under its earlier paths, so `git log --follow` and `git blame` reach its first commit. When an earlier path is
  used again at the tip by another file, each part gets its own file's timeline only (`follow_renames = false` turns
  this off).
- The tags reachable from the branch come along, on the part's commit for the tagged state (annotated ones keep their
  tagger and message), so a baseline such as `v1` is selectable as `app-core@v1`; tags from before the part had any
  file are dropped and reported.
- It ends with a split record commit (no change) whose message carries a TOML `[split]` record, tagged `split/<id>` -
  a baseline for the next run (`run start -select app-core@split/<id>,...`).

The tip of every part is verified against the source's tip (same files, modes and content) before anything is
registered. The history is read once (`git fast-export`), each part is written by `git fast-import` into a repository
that borrows the source's objects and then copies only what it reaches, so a part holds only its own files' objects.
Only then are the parts registered in `components.toml`, the referrers rewired and the source unregistered, in one
meta-repo commit carrying the split record. The source's repository is kept untouched apart from a `split/<id>` tag, so
its history, run tags and run records stay valid; results of runs that were never merged into the split branch stay
there and are listed as `unmerged_results`. If any step fails, the new repositories are removed and `components.toml`
is restored.

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

`run start -select <component@tag|hash>[,...] -run <command>` runs an agent on an isolated copy of the selected monorepo state:

1. Every `-select` entry names a component registered in `.bassia/components.toml` and an **annotated tag** or a **commit hash** in its source-of-truth repo (`R:\<component>`); either is the immutable provenance recorded for the run. Unregistered names, branches, `HEAD` and lightweight tags are rejected. A hash is 6 to 40 lowercase hex digits (64 in a SHA-256 repo), the full hash or a prefix of it, and must name a commit (`app@3f9c2e1`). The selector has to mean exactly one thing: a prefix that several objects share fails with git's own `short object ID ... is ambiguous` error and its candidate list, and a hex name that is both a ref and a hash prefix (a tag called `c0ffee`, say) is rejected rather than resolved the way git would, in favour of the ref; spell it `app@refs/tags/c0ffee` or use a longer hash. Shorter or mixed-case hex is always read as a ref name.
2. The selection must cover the full transitive closure of the reference graph (`references = ["lib"]` or `references = [{ name = "lib", path = "libs/lib" }]` in `components.toml`; the graph must be acyclic). Selecting `app` without the `lib` it references fails before anything is materialized — a run pins every component it touches, so nothing is ever resolved from a mutable reference.
3. The run gets a random id, `agent-run-<key>` with `<key>` an adjective, a noun and a 6-character slug of lowercase letters and digits (e.g. `agent-run-magical-otter-vt9j3p`), and the folder `.workspace/agent-run-<key>/`. The id is readable, yet random enough to be unique without any coordination, so runs from different workspaces or machines never collide on the branches and tags they push to a shared component repo. Every selected component is cloned from its source-of-truth repo and checked out there, side by side, on the run branch `agent-run/<key>`. Where one component references another, the referenced component appears inside it as a junction to that sibling checkout, at the subfolder `components.toml` records; the nested path is excluded from the referring repo's index, so each repo only ever commits its own files.
4. The `-run` command is started by the platform shell (`cmd.exe` / `sh`) in `.workspace/agent-run-<key>/`, with `BASSIA_ROOT`, `BASSIA_RUN_ID` and `BASSIA_RUN_DIR` set.
5. Before the agent starts and after it finishes, the run record (`run.toml`: selection, resolved commits, command, timestamps, status, per-component results) is committed to the bare repo `.agentic-runs/.git`, at the monorepo root alongside `.bassia` and `.workspace`, with plumbing commands only (no checkout, no branch) and tagged `agent-run/<key>/<lineage>`. It lives outside both the meta-repo and the workspace, so the run folder can be discarded once its results are pushed while the record survives.
6. When the agent exits successfully, each changed component is committed on its run branch, tagged `agent-run/<key>/<counter>`, and branch + tag are pushed to its source-of-truth repo. The counter is the next unused index among the component's existing `agent-run/<key>/*` tags — all of them sit on the run branch, so the checkout's own tag list is the whole sequence; a run's first result is `/0`. Nothing is merged or checked out there. The sequence is not transactional: a failed component leaves the run `partial`; `run retry` finishes the remaining steps and `run abandon` discards the run folder without touching anything already pushed.

The result commit's message is a subject line, a blank line, and a TOML record of the run and the component, so tooling can read a component's history back to the run that produced each commit:

```toml
agent(magical-otter-vt9j3p): add a hello world script

[agentic_run]
id = "agent-run-magical-otter-vt9j3p"
summary = "add a hello world script"
command = "claude -p \"add a hello world script\" --permission-mode acceptEdits"
select = "app@v0,lib@v0"
created = "2026-09-21T02:12:05.5578522+00:00"
finished = "2026-09-21T02:12:06.4559366+00:00"
record_tag = "agent-run/magical-otter-vt9j3p/0"
[agentic_run.component]
name = "app"
commitish = "v0"
base_commit = "350c164c94f720acf82b204da3a0a436b9270118"
branch = "agent-run/magical-otter-vt9j3p"
tag = "agent-run/magical-otter-vt9j3p/0"
```

The subject line is a template in `.bassia/config.toml` (written by `init`); the body's keys are fixed. `{run_id}` is the full `agent-run-<key>`, `{short_id}` the `<key>` (`magical-otter-vt9j3p`), `{summary}` the longest quoted part of the agent command (usually the prompt) or the command itself, `{component}` the component name:

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

Every successful run leaves its changes in each component it touched as a result tag `agent-run/<key>/<n>` - the same name in every component. `integration start` consolidates the results of several runs, component by component, into one integration:

```powershell
bassia integration plan -runs all                                # the triage only; nothing changes
bassia integration start -runs brave-otter,quiet-fern             # runs by (a prefix of their) key
bassia integration start -runs all -onto lib@v1 -skip brave-otter -semantic quiet-fern -resolve claude -p --permission-mode acceptEdits --model opus
bassia integration advance <id>                                  # fast-forward the base branches to the result
```

1. **Triage.** For each component the runs changed, the results are considered oldest run first against a base: the component's default branch (usually `main`), or the branch or tag `-onto` names. Git classifies each one without a working tree (`git merge-tree`): `up_to_date` (already contained), `fast_forward`, `clean` (a three-way merge without conflicts) or `conflict`. The results git can merge are chained onto a simulated head in order, so a result that merges cleanly onto the base but collides with an earlier one is caught here too. Every step is then `syntactic` (git merges it), `semantic` (the resolver merges it) or `skip`. `-semantic` sends a result to the resolver even without a textual conflict, for a semantic review; `-skip` leaves it out. The triage also lists, for each result, the other runs it conflicts with directly.
2. **Syntactic steps first.** Each component is merged in its own checkout under `.workspace/integration-<key>/<component>`, on the branch `integration/<key>` started at the base. Every syntactic step is a `git merge --no-ff` of the run's result tag. If one conflicts after all, it moves to the back of the semantic queue.
3. **Semantic steps next.** For each remaining step Bassia starts the merge with `diff3` conflict markers, so the common ancestor is visible, and writes a **semantic brief**: the incoming run's prompt, baseline, command and commit records; which runs are already integrated and why; the history and diff of both sides since their common ancestor; the conflicted files; and instructions. The resolver command (by default `claude -p --permission-mode acceptEdits`, configurable as `[integration] resolver` in `config.toml`) runs in the component's working tree with the brief on stdin and these environment variables: `BASSIA_MERGE_BRIEF` (the brief's path), `BASSIA_COMPONENT`, `BASSIA_RUN_ID`, `BASSIA_INTEGRATION_ID` and `BASSIA_ROOT`. When it exits with code 0 and no conflict marker is left, Bassia commits the merge. A non-zero exit or leftover markers abort that step, which is recorded as `failed`; the other steps still go ahead.
4. **Result.** Each merge commit's message is a subject and an `[integration]` TOML record (run, source tag, strategy, rationale, conflicts, resolver). The result is tagged `integration/<key>/<n>` - one identically named annotated tag across the components - and branch and tag are pushed to the component's source-of-truth repo. That tag is a baseline like any other: `run start -select app@integration/<key>/0,...` starts the next run from it. Nothing else moves: `integration advance <id>` fast-forwards each component's base branch to the result, and refuses if the branch moved since the integration was built on it, or if the base was a tag.

The integration's record (`integration.toml`: runs, resolver, and per component its base, every step's triage, strategy, conflicts, outcome, merge commit and brief) is committed to `.agentic-runs` next to the run records and tagged `integration/<key>/<lineage>`. Status is `completed`, `partial` (a step or component failed; what did merge is still published), or `cancelled`.

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
  graph (the same SVG as `bassia graph -format svg`, with every box a link) and the latest runs and
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
  start`.

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
background process. `ComponentSplitTests` splits a component with a branchy history (moves into and between
parts, a merge, tags, a dropped folder, a path reused after its file moved away) and checks every part's files,
`git log --follow`, tags, record commit and registration, plus plan errors, dry runs, rollback and the survey.
`ParallelAgentRunEndToEndTests`
(`Category=EndToEnd`) is the whole-pipeline proof and works differently: it builds a monorepo in the temp folder,
clones [jk09/example](https://github.com/jk09/example) twice as two components, tags each component's HEAD, and
starts one agentic run per component **at the same time**, each as its own `bassia` process. It then checks that
every run pushed an annotated result tag onto its own component's baseline, left the baseline tag and `main`
where they were, stayed out of the other component, and recorded both runs in `.agentic-runs`; the recorded
`created`/`finished` timestamps have to overlap, which is what makes it a parallel run rather than two runs in a
row. The run command is a deterministic shell command standing in for a coding agent, so the proof is about
Bassia and not about a model's output.

`ThreeComponentEndToEndTests` (`Category=EndToEnd`) follows the work all the way to `main` and checks it with
Bassia's own commands. Its monorepo holds three components cloned from jk09/example: the libraries `sortlib` and
`greetlib`, and `apps`, which nests both. Two waves of agentic sessions run in parallel - wave 1 detached
(`run start -detach`, `run wait`), wave 2 in the foreground - implementing an insertion sort library, a greeting
library together with a program in `apps` that uses it, a hello world program, a terminal program sorting its
arguments with `sortlib`, and a descending sort. Each session's agent is a deterministic stand-in that applies a
patch computed against its baseline. Two sessions edit the same lines of `apps/README.md`, so each wave's
`integration start` has git merge what it can and a deterministic resolver (a git union merge) the conflict, then
`integration advance` moves every `main`. `log -run` must report every result off `main` before the advance and
landed through the integration after it, in exactly the components the session edited; `log -component apps
-branch main` must attribute the mainline to all five sessions; `component show`, `run list` and `integration list`
must agree. A last session selects the three `main` states and builds and runs the C programs (skipped without a C
compiler on `PATH`).

Both write a Markdown report of every commit, tag and record they verified, to the test output and to a file. Three
environment variables steer them:

- `BASSIA_E2E_PROOF` — where to write the report: a file, or a folder for one report per test (default: a
  timestamped file in the temp folder).
- `BASSIA_E2E_KEEP` — keep the monorepo after the test so the refs in the report can be inspected.
- `BASSIA_E2E_COMPONENT_URL` — clone from a local mirror instead of GitHub, to run offline.

Future features can add workflow-specific behavior while continuing to use Git for repository compatibility.