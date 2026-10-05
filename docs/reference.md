# Bassia reference

The complete guide to the `bassia` command line, the configuration, agentic runs, integration and the web dashboard.
For what Bassia is and why, start with the [README](../README.md).

Bassia is built on top of Git. It delegates repository storage, history, branching, and commit behavior to the
installed Git executable instead of reimplementing Git internals.

## Requirements

- .NET SDK 10.0 or later
- Git available on `PATH`

## Installing

Clone this repository and ask Claude Code to `/bassia-install`. It builds the latest release tag (`v<major>.<minor>.<patch>`)
and installs it for the current user, without elevation. The `/bassia-use` and `/bassia-uninstall` skills then switch
between installed versions and remove them. All three run `scripts/bassia-versions.ps1` (PowerShell 7), which can also
be run by hand:

```sh
pwsh scripts/bassia-versions.ps1 check [<version>]           # the .NET SDK the version needs, and what is installed
pwsh scripts/bassia-versions.ps1 available                   # release tags, newest first
pwsh scripts/bassia-versions.ps1 install [<version>] [-Configuration Debug] [-SelfContained] [-Prerelease] [-NoUse] [-Force]
pwsh scripts/bassia-versions.ps1 list                        # installed versions; * marks the default
pwsh scripts/bassia-versions.ps1 use <version>               # switch the default
pwsh scripts/bassia-versions.ps1 uninstall <version> | -All
```

Versions are installed side by side, like nvm does it. `<version>` is a release tag (`v0.2.0` or `0.2.0`), `latest`
(the default), or a branch or commit, which is installed as `<ref>-<commit>`. A Debug build adds `-debug` to the
version id. The build runs in a temporary git worktree, so the clone's own working tree is never touched, and it
publishes a framework-dependent build (`-SelfContained` for one that doesn't need an installed .NET runtime). The
install root is `$BASSIA_HOME`, else `%LOCALAPPDATA%\Bassia` on Windows or `~/.local/share/bassia` elsewhere:

```
<root>/
  versions/v0.2.0/          one publish per version, plus bassia-install.toml (ref, commit, configuration, SDK)
  versions/v0.1.0-debug/
  current -> versions/v0.2.0  the default: a junction on Windows, a symlink elsewhere; the only entry on PATH
  activate.ps1, activate.sh   use another version in one shell only
```

The first install adds `<root>/current` to the user's PATH. On Windows that is the user PATH in the registry. Elsewhere
it is a line in `~/.profile` (plus `~/.bashrc` and `~/.zshrc` if they exist) that sources `<root>/env.sh`. Switching
the default retargets the link, so PATH stays as it is. To use another version in one shell only, as with venv, run
`. <root>/activate.ps1 <version>` (PowerShell) or `. <root>/activate.sh <version>` (bash/zsh), and `bassia_deactivate`
to undo it. Without a version, `activate` uses the one named in the nearest `.bassia-version` file, so a project can
pin a version.

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
| Configuration | `config list`, `config get -key <k>`, `config set -key <k> -value <v> [-user]`, `config unset -key <k> [-user]` |
| Components | `component list`, `component add -url <url> [-name <n>] [-references <c[:path]>,...]`, `component show -name <c>`, `component set -name <c> -references ...\|-clear-references`, `component remove -name <c> [-purge]`, `component tag -name <c> -tag <t> [-ref <commit-ish>] [-message <m>]` |
| Submodules | `component add -url <url> -unwind`, `component unwind -name <c> [-dry-run]` |
| Tags across components | `tag list [-component <c>,...] [-prefix <p>] [-min <n>]`, `tag show -tag <t>`, `tag create -tag <t> -select <c[@ref]>,... [-message <m>]` |
| Splitting components | `component survey -name <c> [-depth <n>] [-limit <n>]`, `component split -plan <file>\|- [-name <c>] [-dry-run]` |
| Dependencies and history | `graph [-name <c>] [-format board\|tree\|mermaid\|svg] [-out <file>]`, `log [-component <c>,...] [-only] [-branch <b>] [-run <id>,...\|all] [-limit <n>] [-page <n>]` |
| Agentic runs | `run start -select <c[@tag\|hash]>,... [-detach] (-prompt <text> [-agent] [-model] [-effort] [-context] \| -run <command...>)`, `run list [-status <s>] [-component <c>]`, `run show`, `run logs [-tail <n>]`, `run wait [-timeout <s>]`, `run stop`, `run retry`, `run abandon`, `run diff [-component <c>] [-patch]` |
| Integration (merging) | `integration plan -runs <ids>\|all [-onto] [-semantic] [-skip] [-manual] [-weave <command>\|off]`, `integration start ... [-detach] [-resolve <command...>]`, `integration list`, `integration show`, `integration logs`, `integration wait`, `integration stop`, `integration advance` |
| Natural language and skills | `prompt [-backend <name>] [-llm <command>] [-model <m>] [-skill <s>,...] [-yes] [-dry-run] [-max-rounds <n>] <ask...>`, `skill list`, `skill show -name <s>` |
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

Whoever executes a run or integration - a foreground `bassia` or a detached one - registers
it as a job in `<workspace>/.jobs/`. That is what lets another process, usually another agent:

- see that it is **live** (`run list -status live`, `run show`: `live`, `pid`, `last_output`, and an ASCII `card`),
- read a detached job's **output** as it grows (`run logs`; the result goes to a separate `result_file`),
- **wait** for it (`run wait`, `integration wait`, with an optional `-timeout`),
- **stop** it (`run stop`, `integration stop`): the job is asked to stop, which kills the agent's (or resolver's)
  process tree and records the work as `cancelled`; a job that does not react within `-timeout` seconds has its
  process killed.

A run recorded as `started` whose process is gone (killed, crashed, machine restarted) shows as `stale`; `run stop`
records it as `cancelled`.

### Configuration

Settings come in layers. The highest layer that sets a key wins:

1. **user**: `.bassia/config.user.toml`, your own settings for this monorepo. It is git-ignored in the meta-repo
   (`init` writes a `.gitignore`; an older meta-repo gets the entry in its local `info/exclude`), so it is never
   committed.
2. **monorepo**: `.bassia/config.toml`, committed to the meta-repo and shared by everyone.
3. **default**: built into Bassia.

`bassia config list|get` shows each key's effective value and the layer it comes from (`get` also shows the value in
each layer). `bassia config set -key <k> -value <v>` writes the monorepo layer, keeping its comments, and commits the
change. With `-user` it writes the user layer instead. `bassia config unset -key <k> [-user]` removes a key from a
layer, so the next layer down applies again. A value outside a key's choices, or a key naming an unregistered
component, is rejected and nothing changes. `bassia prompt` hands the LLM the effective configuration with each
value's layer, and it reads and changes settings through the same commands.

| Key | Default | Meaning |
| --- | --- | --- |
| `workspace.path` | `.workspace` | Folder of the run and integration checkouts (and of `.jobs`) |
| `agent.command` | `claude -p --permission-mode acceptEdits` | Agent command of a run started from `-prompt` |
| `agent.commit.subject` | `agent({short_id}): {summary}` | Subject of a run's result commits |
| `integration.resolver` | `claude -p --permission-mode acceptEdits` | Command that resolves a semantic merge |
| `integration.weave` | `weave-driver` | Structural merge driver tried where git conflicts, used when installed; `off` disables it |
| `merge.semantic` | `resolver` | A result neither git nor weave can merge goes to the resolver, or is left for a human (`manual`) |
| `merge.warnings` | `resolver` | A result weave merged with warnings: reviewed by the resolver, left for a human (`manual`), or weave's merge stands (`accept`) |
| `merge.manual_paths` | (none) | Path patterns, as in split plans (`**/*.csproj,db/migrations`), whose conflicts always need a human |
| `merge.advance` | `manual` | `auto`: a completed integration fast-forwards the base branches itself, as `integration advance` would |
| `llm.backend` | `claude` | LLM backend of `bassia prompt`: `claude` (Claude Code) or `command` |
| `llm.command` | `claude -p` | Command that backend runs; the prompt goes to its stdin |

Each `merge.*` key can be overridden for one component as `merge.component.<component>.<setting>` (a
`[merge.component.<component>]` table). It falls back to the global key, layer by layer:

```powershell
bassia config set merge.component.db.semantic -value manual        # db's unmergeable conflicts wait for a human
bassia config set merge.manual_paths -value "**/*.csproj"           # everyone: project files are never auto-resolved
bassia config set merge.advance -value auto -user                   # just me: land completed integrations at once
bassia config unset merge.advance -user
```

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

### Unwinding submodules

Bassia's components replace git submodules, but a repository added as a component may still have some. Unwinding
turns them into components of their own, so the monorepo's structure is all in `components.toml`:

```powershell
bassia -C R:\ component add -url https://github.com/myrepo/tool.git -unwind   # add and unwind at once
bassia -C R:\ component unwind -name app -dry-run                              # the plan; nothing changes
bassia -C R:\ component unwind -name app
```

- **Identity.** A submodule is identified by its URL: a relative URL (`../lib.git`) resolves against the parent's, and
  the scheme (`https://`, `ssh://`, `git@host:`), user, a `.git` suffix and case do not matter, so
  `git@github.com:Owner/Lib.git` is `github.com/owner/lib`. A registered component with that URL (or `origin`) is
  reused; otherwise the submodule is cloned as a new component named after its URL (`<parent>-<name>` when the name is
  taken by another repository).
- **Recursive.** The submodules of a submodule are unwound too, at every depth, both at the commit a parent pins and on
  the submodule component's own main branch.
- **References.** Each submodule becomes a reference of its parent at the submodule's path (`lib:vendor/lib`), so a run
  nests it as a junctioned folder like any other referenced component.
- **Commits.** Every commit with submodules gets one commit on top that removes the gitlinks and their `.gitmodules`
  sections (the file goes when nothing is left in it). On the main branch that commit becomes the new tip, so
  `run start -select app,lib` works; history is kept, so the component stays compatible with its upstream. For a
  commit a submodule pins, the commit stays off main.
- **Linking tags.** Each main branch can express only one combination, while the same submodule may be pinned at
  different commits by different components (or by older commits). So every commit with submodules is linked with
  the commits its submodules pin (unwound in turn) by one annotated tag of the same name in every component involved,
  `unwind/<component>/<n>`: `run start -select app@unwind/app/0,lib@unwind/app/0,core@unwind/app/0` starts from exactly
  the pinned combination. A component pinned twice at different commits within one tree keeps the nearest pin in the
  tag; the result lists the other as a conflict, and it stays reachable through its own parent's tag.
- **All or nothing.** The whole tree is planned first, new components are cloned into a staging folder and pinned
  commits missing from a repository are fetched from the submodule's URL. A pinned commit that cannot be found, a
  submodule without a URL, a cycle, or a path where the parent already nests another component fails the unwind and
  nothing changes (`component add -unwind` then adds nothing either). The meta-repo is committed once.

### Tags across components

Identically named tags tie commits of several components together: unwinding links pinned submodules with
`unwind/...` tags, an integration tags its result `integration/<key>/<n>` in every component, and a run that spans
components starts from a tag in each.

```powershell
bassia -C R:\ tag list                                  # every tag, with how many components it tags
bassia -C R:\ tag list -min 2 -prefix unwind/           # multi-component unwind tags
bassia -C R:\ tag show unwind/app/0                     # the tag in each component: commit, subject, message
bassia -C R:\ tag create release-3 -select app,lib@v2   # one annotated tag in each; none if any already has it
```

`tag list` reads the tags from the component repositories, which stay the only record of them, groups them by name
and reports per tag its kind (`unwind`, `integration`, `run`, `split` or `other`), the number of components and their
commits, and the `-select` value that starts a run from it in all of them.

### Agentic runs

```powershell
bassia component tag -name app -tag v0                # tag a baseline per component (or select bare names)
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

`run start -select <component[@tag|hash]>[,...] -run <command>` runs an agent on an isolated copy of the selected monorepo state:

1. Every `-select` entry names a component registered in `.bassia/components.toml`, alone or with an **annotated tag** or a **commit hash** in its source-of-truth repo (`R:\<component>`). A **bare name** (`-select app,lib`) starts from the tip of the component's default branch - the branch its source-of-truth repo's `HEAD` names, usually `main` - resolved when the run starts; the run records the branch name as the selector and the tip commit as its provenance, so the record stays reproducible although the branch moves on. A source repo with a detached `HEAD` or an unborn default branch fails, asking for `<component>@<tag|hash>`. Bare and pinned entries mix (`-select app,lib@v1`). Unregistered names, `<component>@<branch>`, `HEAD` and lightweight tags are rejected. A hash is 6 to 40 lowercase hex digits (64 in a SHA-256 repo), the full hash or a prefix of it, and must name a commit (`app@3f9c2e1`). The selector has to mean exactly one thing: a prefix that several objects share fails with git's own `short object ID ... is ambiguous` error and its candidate list, and a hex name that is both a ref and a hash prefix (a tag called `c0ffee`, say) is rejected rather than resolved the way git would, in favour of the ref; spell it `app@refs/tags/c0ffee` or use a longer hash. Shorter or mixed-case hex is always read as a ref name.
2. The selection must cover the full transitive closure of the reference graph (`references = ["lib"]` or `references = [{ name = "lib", path = "libs/lib" }]` in `components.toml`; the graph must be acyclic). Selecting `app` without the `lib` it references fails before anything is materialized — a run never pulls in a component nobody named, so every version it starts from (tag, hash or default-branch tip) is an explicit choice, and every one is pinned to a commit in the run record.
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

### Integration: syntactic first, then structural, then semantic

Every successful run leaves its changes in each component it touched as a result tag `agent-run/<key>/<n>` - the same name in every component. `integration start` consolidates the results of several runs, component by component, into one integration:

```powershell
bassia integration plan -runs all                                # the triage only; nothing changes
bassia integration start -runs brave-otter,quiet-fern             # runs by (a prefix of their) key
bassia integration start -runs all -onto lib@v1 -skip brave-otter -semantic quiet-fern -resolve claude -p --permission-mode acceptEdits --model opus
bassia integration plan -runs all -weave off                     # the triage without the structural merge
bassia integration advance <id>                                  # fast-forward the base branches to the result
```

1. **Triage.** For each component the runs changed, the results are considered oldest run first against a base: the component's default branch (usually `main`), or the branch or tag `-onto` names. Git classifies each one without a working tree (`git merge-tree`): `up_to_date` (already contained), `fast_forward`, `clean` (a three-way merge without conflicts) or `conflict`. Where git conflicts, the **structural merge** is asked the same question (see below): `clean`, `warnings` (merged, but weave doubts the result means what both sides meant) or `conflict`, with the files still conflicted after it. The results git or the structural merge can merge cleanly are chained onto a simulated head in order, so a result that merges cleanly onto the base but collides with an earlier one is caught here too. Every step is then `syntactic` (git merges it), `structural` (git merges it with weave as the merge driver), `semantic` (the resolver merges it), `manual` (left for a human) or `skip`. The **merge policy** (the `merge.*` settings, see [Configuration](#configuration)) decides between the resolver and a human: with `merge.semantic = manual`, with `merge.warnings = manual`, or for conflicts in `merge.manual_paths`, a step is `manual`; with `merge.warnings = accept` weave's merge with warnings stands. `-semantic` sends a result to the resolver even without a textual conflict, for a semantic review, and wins over the policy; `-manual` leaves a result for a human; `-skip` leaves it out. The triage also lists, for each result, the other runs it conflicts with directly.
2. **Syntactic and structural steps first.** Each component is merged in its own checkout under `.workspace/integration-<key>/<component>`, on the branch `integration/<key>` started at the base. Every syntactic step is a `git merge --no-ff` of the run's result tag, every structural one the same with weave as the merge driver, in run order. If one conflicts after all (or the structural merge warns), it moves to the back of the semantic queue.
3. **Semantic steps next.** For each remaining step Bassia starts the merge - with weave as the merge driver when the structural merge is enabled, so only what weave could not merge is left conflicted, in entity-labelled conflicts - with `diff3` conflict markers for what git's own merge leaves, so the common ancestor is visible, and writes a **semantic brief**: the incoming run's prompt, baseline, command and commit records; which runs are already integrated and why; the history and diff of both sides since their common ancestor; the conflicted files (and, with the structural merge, which files git alone conflicted on, which of them weave merged, and weave's warnings); and instructions. The resolver command (by default `claude -p --permission-mode acceptEdits`, configurable as `[integration] resolver` in `config.toml`) runs in the component's working tree with the brief on stdin and these environment variables: `BASSIA_MERGE_BRIEF` (the brief's path), `BASSIA_COMPONENT`, `BASSIA_RUN_ID`, `BASSIA_INTEGRATION_ID` and `BASSIA_ROOT`. When it exits with code 0 and no conflict marker (nor weave's `weave explain` pointer) is left, Bassia commits the merge. A non-zero exit or leftover markers abort that step, which is recorded as `failed`; the other steps still go ahead.
4. **Result.** Each merge commit's message is a subject and an `[integration]` TOML record (run, source tag, strategy, rationale, conflicts, structural driver and conflicts, resolver). The result is tagged `integration/<key>/<n>` - one identically named annotated tag across the components - and branch and tag are pushed to the component's source-of-truth repo. That tag is a baseline like any other: `run start -select app@integration/<key>/0,...` starts the next run from it. Nothing else moves: `integration advance <id>` fast-forwards each component's base branch to the result, and refuses if the branch moved since the integration was built on it, or if the base was a tag.

The integration's record (`integration.toml`: runs, resolver, structural driver, and per component its base, every step's triage, structural verdict, strategy, conflicts, outcome, merge commit and brief) is committed to `.agentic-runs` next to the run records and tagged `integration/<key>/<lineage>`. Status is `completed`, `needs_attention` (everything merged except the steps the policy left for a human; their outcome is `needs_attention`), `partial` (a step or component failed; what did merge is still published), or `cancelled`. With `merge.advance = auto`, a `completed` or `needs_attention` integration advances the base branches of the components whose policy says so.

#### The structural merge (weave)

Parallel agentic runs often add or change *different* functions, methods or keys of the *same* file. Git's line merge reports that as a conflict when the hunks touch, although nothing collides, and the integration would hand it to the LLM resolver. [weave](https://github.com/Ataraxy-Labs/weave) merges such files by entity instead of by line: it parses base, ours and theirs with tree-sitter (38 languages, plus JSON, YAML, TOML, Markdown and more), merges independent entities cleanly and only conflicts when both sides changed the same entity incompatibly. Like git's merge it is deterministic, so a result weave merges cleanly is a `structural` step that never reaches the resolver.

- Install weave's `weave-driver` (`brew install weave`, or `cargo install --path crates/weave-driver` from a clone of the repository) and put it on `PATH`. Bassia uses it when it is found; otherwise the integration behaves exactly as without it, and `integration plan` reports `structural_merge = "unavailable: ..."`. Name another driver with `bassia config set integration.weave <command>` or `-weave <command>`; `off` disables it.
- Nothing in a component repo is changed: the driver and the attributes that assign it to weave's file types are passed to git with `-c` for each command, and the attributes file lives in the workspace (`.workspace/.structural-merge.gitattributes`). A component's own `.gitattributes` still takes precedence; the `-c core.attributesFile` replaces a user-wide attributes file for these merges only.
- A merge weave completes with warnings (`weave-warning:` on its stderr, e.g. an entity depends on another entity the other side changed, or the merged file no longer parses) goes to the resolver for review, with the warnings in its brief.
- What weave still conflicts on goes to the resolver as before, but the resolver starts from weave's entity-labelled conflicts (``<<<<<<< ours — function `process` ...`` with a `refused_by:` line) instead of git's.

### Natural-language prompts

```powershell
bassia prompt initialize the monorepo at folder C:\mono
bassia -C C:\mono prompt -dry-run add https://github.com/myrepo/lib.git as a component and tag it v1
bassia -C C:\mono prompt which runs failed this week and why?
bassia -C C:\mono prompt -yes /release cut release 3 of app and lib
```

`prompt` is a thin LLM layer over the command line. Everything after its switches is the ask. Because every command
has the same shape and answers in TOML, there is no per-command glue:

1. The LLM gets the ask, the context (working directory, platform, the monorepo and its components), a catalog of
   every command generated from the command table (usage, switches, details, examples) and the skills' names and
   descriptions.
2. It answers in TOML: `[[command]]` entries with `args` (the words after `bassia`, as an array, so Windows paths
   need no shell quoting), `done`, `answer`, and optionally `load_skills`.
3. Bassia checks each command with the same parser the command line uses and runs it as its own `bassia` process -
   never through a shell. Its output is shown, and its TOML result goes back to the LLM for the next round until the
   LLM is done (at most `-max-rounds`, default 8). The LLM's `answer` becomes the result's `message`.

Read-only commands (`status`, `config list|get`, `component list|show|survey`, `tag list|show`, `graph` without
`-out`, `log`, `run list|show|logs|wait|diff`, `integration plan|list|show|logs|wait`, `skill list|show`, `help`,
`version`) run at once. Any other command is confirmed first (`y`es, `n`o, `a`ll); when stdin is not a terminal it
needs `-yes`. `-dry-run` changes nothing: it runs the read-only commands and stops with the plan at the first command
that would change something. `prompt` and `web` are never run from a prompt. The result lists every proposed command
as a `[[step]]` with its `command_line`, `status` (`ok`, `failed`, `rejected`, `declined`, `planned`, `skipped`) and
the command's own `message`.

**Backends.** The LLM is only asked for text, so any model fits. `llm.backend` (or `-backend`) picks one:

- `claude` (default) runs `llm.command` (default `claude -p`, i.e. [Claude Code](https://code.claude.com)) with its
  tools off (`--tools ""`; it plans, Bassia executes), text output and no saved session; `-model` becomes `--model`.
- `command` runs `llm.command` as it is - any command that reads the prompt on stdin and prints the reply, such as
  `ollama run {model}` or `llm -m {model}`; `{model}` is replaced by `-model`.

Another kind of backend (an HTTP API, say) is one class implementing `ILlmBackend` registered in `LlmBackends`.

**Skills** are reusable instructions for recurring asks, versioned in the meta-repo like `config.toml`, in the shape
Claude Code uses: `.bassia/skills/<name>/SKILL.md`, with optional front matter.

```markdown
---
name: release
description: Cut a release - tag every component release-<n> and integrate the pending runs first.
---
1. Run `integration plan -runs all`; stop and report when anything conflicts.
2. ...
```

The LLM always sees every skill's name and description and loads the instructions of those it needs, so many skills
cost little prompt. `-skill <name>,...`, or a `/<name>` word in the ask, hands it a skill up front. `skill list` and
`skill show -name <skill>` show them; they are added and changed as files and committed with `git -C .bassia commit`.

### Web dashboard

```powershell
bassia -C R:\ web                        # http://127.0.0.1:8080/, opened in the browser
bassia -C R:\ web -port 9000 -no-open
```

`web` serves a read-only dashboard over the monorepo, in the spirit of [Fossil](https://fossil-scm.org)'s built-in
web interface: a place to look at the monorepo, while the command line and `bassia prompt` change it. Every page
shows the `bassia` commands for what it describes (click one to copy it). It listens on 127.0.0.1 only and answers
only GET requests. If the port is taken it uses the next free one, and it prints the URL. It runs until Ctrl-C.
Pages and their pictures are drawn on the server (inline SVG and CSS, nothing loaded from elsewhere) and work
without JavaScript; the script animates and refreshes the live parts.

- **Overview**: counts (components, live runs, results on their way to their default branch, merges needing
  attention, tags across components, integrations) and the live runs. Next to the component map: what needs
  attention, the latest milestones (baseline and integration tags; a run's own result tag is not one) and runs by
  status. Below: the recent runs over time.
- **Components**: the component map. Components sit in layers below what references them, and arrows point at what
  each needs. Each box shows its live runs (pulsing), results waiting in the merge queue, merges needing attention
  and its latest tag. Hovering a box lights up everything it needs and everything that needs it. A component's page
  adds its merge policy, its results in the queue, its branches and tags, and the runs that touched it.
- **Tags**: tags as units of progress. A chart has one column per tag, in time order, and one row per component,
  with a mark where the tag is. The marks are joined when the tag spans several components, and coloured by kind:
  baseline, run result, integration, unwound submodules, split. You can filter by kind or by tags across
  components. A tag's page shows the components and commits it marks, what made it (a run, an integration, or by
  hand), the runs started from it and the integrations that took it in. Its lineage is drawn as a flow: started
  from → agentic runs → results → integrations → integrated as.
- **Runs**: the live runs as animated cards. Each card shows its phase as steps (preparing, agent working,
  finalizing, done) with the current step pulsing, a ticking clock, its components and the latest line of its
  output. They come from the job registry, so a run started from any terminal appears by itself. Below them, every
  run over time (live bars reach to now) and runs by status. A run's page streams its output while it is live and
  draws its path from its baselines to its results and the integrations that took them.
- **Merge queue**: every result not yet on its component's default branch: `waiting`, `integrated` (its base not
  advanced) or `needs attention`. For what waits, the triage of the next integration is drawn as one lane per
  component, in merge order, with each result coloured by who merges it: git, weave, the resolver, or a human. A
  bar per component shows the split, and a ring joins the runs whose results collide. A table gives each merge's
  meaning: why the run changed things (its prompt), what git found, how weave saw it, and why it goes where it
  goes. Then come the integrations waiting for `integration advance`, and every merge that **needs attention**
  (left for a human by the merge policy, or failed), each with its semantic brief when one was written and the
  commands that deal with it: hand it to the resolver, merge it by hand, or leave it out.
- **Integrations**: every integration with a bar of how its steps were merged. An integration's page draws its
  runs, results and result tags, lists its steps (triage, structural verdict, strategy, conflicts, outcome) and opens
  each semantic brief. **Preview triage** shows how chosen runs would integrate, like `integration plan`.
- **Timeline**: one chronological list of commits across the components you choose and the components they depend
  on (never the whole monorepo at once), paged. Without a choice it offers each component with what it needs as a
  one-click start; ticking a component reloads at once. Each component has a colour and a lane, and each commit is a
  dot in its component's lane: hollow for a plain commit, filled for a run's result, ringed for an integration's
  merge. A "Made by" column names the run or integration behind a commit. A run's branch is not repeated next to
  its tag. Every commit opens with its message and diffstat.
- **Config**: every setting with its effective value and which layer it comes from (default, monorepo, user), and
  the merge policy each component ends up with.

Requests must be addressed to `127.0.0.1` or `localhost`, so a web page open in the same browser cannot read the
dashboard through DNS rebinding.

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