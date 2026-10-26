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
- A command's main argument may be given without its switch: `bassia agent show brave-otter-3f2a91` is
  `bassia agent show -id brave-otter-3f2a91`.
- A rest-of-line switch (`-run`, `-resolve`) takes everything after it verbatim, so it comes last.
- `-C <path>`, before the command, runs `bassia` as if it had been started in `<path>`, same as `git -C <path>`. It
  can be repeated, each occurrence relative to the previous one.
- Runs and integrations are named by their full id (`agent-run-brave-otter-3f2a91`), its `<key>` part
  (`brave-otter-3f2a91`, the short id every listing shows), or any prefix of the key of at least four characters
  (`brave-ot`).

### A bare `bassia`: short help and the guide

`bassia` with no arguments prints the full help as TOML, as `bassia help` does. The exception is a person at a
terminal, when stdin, stdout and stderr are all terminals. Then it prints a short help, followed by the guide on
stderr. `bassia tutorial` runs the same guide on purpose, with `-C`, and reads its answers from stdin, so it can be
scripted. It ends with a TOML result whose `commands` lists every command it ran.

- **Outside a monorepo**, the guide explains what Bassia is and offers to set one up (`[y/N]`). The setup asks three
  things, each explained first:
  1. The repositories that become components: URLs or local folders, each checked with `git ls-remote` and named.
  2. Which component needs which, by number or name. Cycles are refused.
  3. The root folder, which must be empty or new and outside another monorepo.

  It then shows the plan as the `bassia` commands it will run (`init`, then `component add ... -references ...` in
  dependency order) and runs them once confirmed, showing each with its result. Nothing changes before that. If a
  command fails, it says what was done and which commands remain.
- **Inside a monorepo**, it prints the root, the status and the dependency tree, then a menu: add components, set a
  component's dependencies, show the status, start a detached run, and open the dashboard (hands over to
  `bassia web`). Each command that changes something is shown
  and confirmed first.

At any question, `?` explains the step from the built-in help (with a question after it, the same help and a pointer
to `bassia help`). Closing the input (Ctrl+D, Ctrl+Z) ends the guide.

### Help

`bassia help` explains the concepts (`[[concept]]`), the usual `workflow`, how to learn more (`discover`) and lists
every command (`[[commands]]`: name, full summary, usage, `read_only`, `interactive`). `bassia help -all` describes every
command in full in one result, `bassia help <command> [<subcommand>]` (or `-help` on any command) explains one: its
usage, details, whether it only reads, every switch and examples. Help is TOML too.

Results are TOML by default. Add `-human` to any command (`bassia -help -human`, `bassia component -help -human`,
`bassia status -human`) to print it for a person instead, as Markdown: the message (or `**error:** ...`), `- **key:** value`
bullets, a heading per list or nested table, a table for lists of entries and fenced code blocks for drawn text
(graphs, trees), without the `# bassia result` marker. Only the format changes; the exit codes and the stdout/stderr
split are the same. `-human` after a rest-of-line switch belongs to that command line.

The Markdown reads fine as it is; to render it in the terminal, pipe it to a Markdown reader:

```
bassia component -help -human | glow -                    # glow: https://github.com/charmbracelet/glow
bassia status -human | mdcat                              # mdcat
bassia help -human | bat -l md                            # bat: syntax highlighting only
bassia help -human | pandoc -f gfm -t plain | less        # pandoc: plain text, paged
bassia help -human | Out-String | Show-Markdown           # PowerShell 7: built-in, renders ANSI
```

Add `-p` to `glow` (or `| glow -p -`) to page long output. An error goes to stderr, so use `2>&1 |` to render it too.

| Area | Commands |
| --- | --- |
| Monorepo | `init [-path <dir>]`, `status`, `version`, `tutorial` |
| Users | `whoami`, `user list`, `user show -id <u>`, `user add -id <u> -name <n> -email <e> [-contact <text>] [-key <key>]`, `user set -id <u> ...`, `user remove -id <u>`, `user use -id <u>` |
| Configuration | `config list`, `config get -key <k>`, `config set -key <k> -value <v> [-user]`, `config unset -key <k> [-user]` |
| Components | `component list`, `component add -url <url> [-name <n>] [-references <c[:path]>,...] [-tags <t>,...] [-visibility <p>,...] [-owners <o>,...]`, `component create -name <c> [-references <c[:path]>,...] [-branch <b>] [-url <url>] [-template <t>] [-tags ...] [-visibility ...] [-owners ...]`, `component show -name <c>`, `component set -name <c> [-references ...\|-clear-references] [-tags ...\|-clear-tags] [-visibility ...\|-public\|-private] [-owners ...\|-clear-owners] [-exports ...\|-clear-exports]`, `component remove -name <c> [-purge]`, `component tag -name <c> -tag <t> [-ref <commit-ish>] [-message <m>]`, `component fork -name <c> -as <fork> [-ref <commit-ish>] [-url <url>] [-references <c[:path]>,...] [-tags ...] [-visibility ...] [-owners ...]` |
| Templates and conformance | `template list`, `component create -name <c> -template <t>`, `conformance list`, `conformance add -components <pattern> [-files <p>,...] [-tasks <t>,...] [-owners] [-exports] [-description <text>]`, `conformance remove -index <n>`, `conformance check [-name <c>] [-strict]` |
| Tasks | `task list [-name <c>]`, `task run -task <t>,... [-select <c[@ref]>,...] [-keep] [-no-cache]`, `task cache [-clear]`, `affected -runs <ids>\|all \| -components <c>,...` |
| Architectural boundaries | `boundary list`, `boundary add -source <pattern> [-allow <pattern>,...] [-deny <pattern>,...] [-description <text>]`, `boundary remove -index <n>`, `boundary check [-name <c>] [-code] [-strict]` |
| Submodules | `component add -url <url> -unwind`, `component unwind -name <c> [-dry-run]` |
| Tags across components | `tag list [-component <c>,...] [-prefix <p>] [-min <n>]`, `tag show -tag <t>`, `tag create -tag <t> -select <c[@ref]>,... [-message <m>]` |
| Splitting components | `component survey -name <c> [-depth <n>] [-limit <n>]`, `component split -plan <file>\|- [-name <c>] [-dry-run]` |
| Dependencies and history | `graph [-name <c>] [-format board\|tree\|mermaid\|svg] [-out <file>]`, `log [-component <c>,...] [-only] [-branch <b>] [-run <id>,...\|all] [-limit <n>] [-page <n>]` |
| Agentic runs | `agent start -select <c[@tag\|hash]>,... [-detach] [-lock-files <[c:]pattern,...>] [-lock-folders <[c:]pattern,...>] (-prompt <text> [-agent] [-model] [-effort] [-context] \| -run <command...>)`, `agent list [-status <s>] [-component <c>]`, `agent show`, `agent logs [-tail <n>]`, `agent wait [-timeout <s>]`, `agent stop`, `agent retry`, `agent abandon`, `agent diff [-component <c>] [-patch]` |
| Locks | `lock list [-component <c>] [-run <id>]`, `lock check -path <p>,...`, `lock claim -path <p>,...`, `lock wait -path <p>,... [-timeout <s>] [-no-refresh]`, `lock release -path <p>,...\|-all [-run <id>] [-force]` |
| Integration (merging) | `integration plan -runs <ids>\|all [-onto] [-semantic] [-skip] [-manual] [-weave <command>\|off]`, `integration start ... [-detach] [-resolve <command...>]`, `integration list`, `integration show`, `integration logs`, `integration wait`, `integration stop`, `integration advance` |
| Skills | `skill list`, `skill show -name <s>` |
| Web dashboard | `web [-port <n>] [-no-open]` |
| Feedback and crash reports | `feedback send [-kind bug\|idea\|question] [-yes] <message...>`, `feedback reports [-mode off\|ask\|auto]`, `feedback pending [-send]` |

The commands this replaced - `run` (now `agent`), `add-component`, `integrate`, `commit`, `branch`, `ui` and
`prompt` - fail with exit code 2 and name their successor.

A typical agent session over a fresh monorepo:

```sh
bassia init -path R:\
bassia -C R:\ component add -url https://github.com/myrepo/lib.git
bassia -C R:\ component add -url https://github.com/myrepo/app.git -references lib
bassia -C R:\ component tag -name lib -tag v1
bassia -C R:\ component tag -name app -tag v1
bassia -C R:\ agent start -select app@v1,lib@v1 -detach -prompt "add a changelog"     # returns run_id at once
bassia -C R:\ agent show brave-otter-3f2a91                                           # live? last output line
bassia -C R:\ agent wait brave-otter-3f2a91 -timeout 1800                             # ok when completed
bassia -C R:\ agent diff brave-otter-3f2a91 -patch                                    # what it changed
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

A result always opens with the comment line `# bassia result`, then `ok`, the `command` (e.g. `agent start`), and either
`message` (success) or `error` (failure); commands that have more to report add their own keys after those, and lists
come as arrays of tables (`[[component]]`, `[[run]]`, ...), named as in the stored records. The marker exists because
`bassia agent start` without `-detach` lets the agent command inherit stdout: a caller takes the last marker line as the
start of the result and treats anything before it as agent output. Values TOML cannot express are simply absent keys —
there is no `null`.

Pictures are part of the result, as multi-line literal strings in plain ASCII, so the output stays one valid TOML
document: `graph` for the dependency graph (`bassia graph`, `status`, `component show`) and for history (`log`), `table`
for listings, `card` for a run, `steps` for an integration, `output` for a detached job's log, and `stat`/`patch` for
`agent diff`.

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

`agent start` without `-detach` behaves like a classic command: the agent's output streams to the terminal and the
result follows when the run ends; Ctrl-C stops the run and records it as `cancelled`. With `-detach` the selection is
checked first (a mistake is reported at once), then the run continues in a background `bassia` process with none of
the caller's standard handles, and the result — `run_id`, `pid`, `log` — is printed immediately. `integration start
-detach` works the same way.

Whoever executes a run or integration - a foreground `bassia` or a detached one - registers
it as a job in `<workspace>/.jobs/`. That is what lets another process, usually another agent:

- see that it is **live** (`agent list -status live`, `agent show`: `live`, `pid`, `last_output`, and an ASCII `card`),
- read a detached job's **output** as it grows (`agent logs`; the result goes to a separate `result_file`),
- **wait** for it (`agent wait`, `integration wait`, with an optional `-timeout`),
- **stop** it (`agent stop`, `integration stop`): the job is asked to stop, which kills the agent's (or resolver's)
  process tree and records the work as `cancelled`; a job that does not react within `-timeout` seconds has its
  process killed.

A run recorded as `started` whose process is gone (killed, crashed, machine restarted) shows as `stale`; `agent stop`
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
component, is rejected and nothing changes.

| Key | Default | Meaning |
| --- | --- | --- |
| `workspace.path` | `.workspace` | Folder of the run and integration checkouts (and of `.jobs`) |
| `depot.url` | (none) | The monorepo this one is a clone of (set by `bassia clone` in the user layer): views import missing components from it, and runs push their results to it |
| `depot.lazy` | `false` | `true`: the clone imports a component's history only when a view maps it (`bassia clone -lazy`) |
| `agent.command` | `claude -p --permission-mode acceptEdits` | Agent command of a run started from `-prompt` |
| `agent.commit.subject` | `agent({short_id}): {summary}` | Subject of a run's result commits |
| `integration.resolver` | `claude -p --permission-mode acceptEdits` | Command that resolves a semantic merge |
| `integration.weave` | `weave-driver` | Structural merge driver tried where git conflicts, used when installed; `off` disables it |
| `merge.semantic` | `resolver` | A result neither git nor weave can merge goes to the resolver, or is left for a human (`manual`) |
| `merge.warnings` | `resolver` | A result weave merged with warnings: reviewed by the resolver, left for a human (`manual`), or weave's merge stands (`accept`) |
| `merge.manual_paths` | (none) | Path patterns, as in split plans (`**/*.csproj,db/migrations`), whose conflicts always need a human |
| `merge.boundaries` | `warn` | `manual`: a result that adds code boundary violations (see [Code boundaries](#code-boundaries-exports-indirect-use-and-paths-outside-a-component)) is left for a human; `warn` only records them |
| `merge.verify` | (none) | Tasks (`build,test`) the integration runs on every component it affects before publishing anything; see [Tasks](#tasks-affected-components-and-the-verification-gate) |
| `merge.owner_review` | `off` | `manual`: a result in a component with `owners` is left for a human, and the step names the owners to ask |
| `merge.advance` | `manual` | `auto`: a completed integration fast-forwards the base branches itself, as `integration advance` would |
| `lock.files` | (none) | Path patterns of files a run holds one by one while its agent changes them (see [Locking files and folders](#locking-files-and-folders)) |
| `lock.folders` | (none) | Path patterns of folders a run holds as a whole while its agent changes a file in them: `src/*` is each folder directly under `src`, `**` the folder of every file |
| `lock.conflict` | `resolver` | A result that changed a path another run held: reviewed by the `resolver` even when git merges it, left for a human (`manual`), or merged like any other (`merge`) |
| `lock.ttl` | `120` | Seconds a hold lasts without its run renewing it, so the holds of a crashed or suspended run expire |

Each `merge.*` key can be overridden for one component as `merge.component.<component>.<setting>` (a
`[merge.component.<component>]` table). It falls back to the global key, layer by layer:

```powershell
bassia config set merge.component.db.semantic -value manual        # db's unmergeable conflicts wait for a human
bassia config set merge.manual_paths -value "**/*.csproj"           # everyone: project files are never auto-resolved
bassia config set merge.advance -value auto -user                   # just me: land completed integrations at once
bassia config unset merge.advance -user
```

`lock.files`, `lock.folders` and `lock.conflict` can be set for one component the same way, as
`lock.component.<component>.<setting>`.

`component add|set|remove` edit `.bassia/components.toml` the same way and reject a change that would leave a cycle
or a reference to an unregistered component.

### Architectural boundaries

Components are the monorepo's architecture: a run sees only the components it selected and what they reference, so
nothing else is there to use. Boundaries say which references the architecture allows, in the spirit of nx's module
boundaries. Each component can carry **tags**, which place it in the architecture (`type:app`, `type:lib`,
`scope:billing`), a **visibility**, which says who may reference it, and **owners**. **Boundary rules** over the tags
say which components may reference which. All of it lives in `.bassia/components.toml`, versioned with the graph:

```toml
[[component]]
name = "billing-core"
tags = ["type:lib", "scope:billing"]
visibility = ["scope:billing", "component:admin"]   # only these may reference it; [] makes it private
owners = ["team-billing"]

[[boundary]]
description = "apps use libraries and UI, never other apps"
source = "type:app"
allow = ["type:lib", "type:ui"]

[[boundary]]
source = "scope:*"
allow = ["scope:$1", "scope:shared"]                # each scope only itself and the shared one
```

```powershell
bassia component set -name billing-core -tags type:lib,scope:billing -visibility scope:billing -owners team-billing
bassia boundary add -source type:lib -deny type:app -description "libraries never depend on apps"
bassia boundary list
bassia boundary check                 # every violation, as warnings; -strict fails on any (for CI)
```

- **Patterns.** In `source`, `allow`, `deny` and `visibility`, `*` matches any characters and `?` one character.
  `$1`, `$2`, ... in `allow` and `deny` stand for what the source's wildcards matched, so `scope:*` with
  `allow = ["scope:$1"]` keeps every scope to itself. Every component also has the implicit tag `component:<name>`,
  so a rule or a visibility can name a single component.
- **Rules.** A reference `A -> B` is checked against every rule whose `source` matches a tag of `A`, once per matching
  tag. With `allow`, some tag of `B` must match an allow pattern. With `deny`, no tag of `B` may match a deny pattern.
  A component no rule's source matches is unconstrained.
- **Visibility.** A component with `visibility` may be referenced only by components with a tag matching one of its
  patterns. Without it, it is public.
- **Enforcement.** Every change to the graph is checked: `component add`, `create`, `fork`, `set` and `split`. A
  change that would add a violation not already in the graph is refused with every new violation named, and nothing
  changes. Violations already there are kept and reported as `warnings` by the commands that change the graph, and
  counted by `status`, `graph`, `component list` and `component show`. They come from a hand edit, or from a rule added
  to a monorepo that does not follow it yet: adding or removing a rule is never refused. Unwinding submodules records
  the structure it finds, so its new violations are warnings too.
- **Defaults.** A fork takes its parent's tags, visibility and owners unless `-tags`, `-visibility` or `-owners` say
  otherwise. A split part takes the source's, unless its `[[part]]` sets `tags`, `visibility` or `owners`.

A monorepo without tags and rules behaves as before.

#### Code boundaries: exports, indirect use and paths outside a component

A run nests each referenced component whole, together with the components *it* nests, so the graph alone cannot stop
`app` from using `lib`'s internals or reaching `core` through `lib`. Bassia therefore also reads the code. A component
declares its public API with `exports` (the split plans' path patterns; without it, everything is exported):

```powershell
bassia component set -name lib -exports src/api,include
bassia boundary check -code            # graph and code; -strict fails on any violation (for CI)
```

The scanner finds path-like dependencies: relative JS/TS imports (`import ... from './x'`, `import()`, `require()`,
`export ... from`), quoted C/C++ `#include`s, MSBuild items and imports (`ProjectReference`, `Import`, `Compile`, ...)
and `.sln` projects, `file:`/`link:` packages in `package.json`, and CMake `add_subdirectory`/`include`. Each is resolved
against the file's folder, where the run nests the references. Namespace and module imports (C# `using`, Python, Go,
Java) name no folder, so they are not seen. A dependency is a violation when it:

- **private** - uses a path of a referenced component that its `exports` do not cover (a script import may leave out
  its extension or `/index`);
- **indirect** - reaches a component through another component's nested reference (`./lib/vendor/core/...`). The
  referrer should reference it itself and use its own copy, which stays at the version the referrer selects;
- **outside** - leaves the component's root (`../core/...`), which in a run folder lands in a sibling checkout.

`boundary check -code` scans every component at the tip of its default branch. Per component, it also lists the
references the code uses and the `unused_references`. The latter are information only, since namespace imports are not
seen.

- **Runs.** When a run has published a component's result, Bassia scans the result. The violations the commit the run
  started from did not have are recorded in `run.toml` (`[[component.boundary_violation]]`), named in the run's
  message and reported by `agent show`. The run still completes. When the monorepo has rules, or a selected component
  has tags, a visibility or exports, the run folder's `AGENTS.md`/`CLAUDE.md` explain the boundaries of the selected
  components, so the agent knows them before it starts.
- **Integration.** Each step records the code violations its result adds over the base (`boundary_violations` in
  `integration.toml`, `integration plan`/`show`). The semantic brief lists them. `merge.boundaries` decides what they
  mean: `warn` (the default) only records them, and `manual` leaves the result for a human. Like the other `merge.*`
  settings, it can be set per component (`merge.component.<component>.boundaries`).

#### Conformance

Conformance rules say what components with given tags must have. They are `[[conformance]]` tables in
`components.toml`:

```toml
[[conformance]]
description = "libraries are documented, tested and owned"
components = "type:lib"          # a tag pattern, as in boundary rules
files = ["README.md", "LICENSE"] # paths at the tip of the default branch (a folder: any file below it)
tasks = ["test"]                 # tasks its bassia.toml defines
owners = true
exports = true
```

```powershell
bassia conformance add -components type:lib -files README.md -tasks test -owners
bassia conformance check                # every unmet requirement, as warnings; -strict fails on any (for CI)
bassia conformance list
bassia conformance remove 1
```

Conformance is reported, never enforced on a change. What a component holds changes through runs; hold a result back
with `merge.verify`, or fail CI with `conformance check -strict`. The dashboard's components table shows each
component's tags and how many boundary and conformance findings it has; its page lists them, with its visibility,
exports and owners.

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
tags = ["type:ui"]                      # optional, like visibility and owners: default is the source's
exports = ["src/api"]                   # optional: the part's public API (default: everything)

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
  a baseline for the next run (`agent start -select app-core@split/<id>,...`).

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
  `agent start -select app,lib` works; history is kept, so the component stays compatible with its upstream. For a
  commit a submodule pins, the commit stays off main.
- **Linking tags.** Each main branch can express only one combination, while the same submodule may be pinned at
  different commits by different components (or by older commits). So every commit with submodules is linked with
  the commits its submodules pin (unwound in turn) by one annotated tag of the same name in every component involved,
  `unwind/<component>/<n>`: `agent start -select app@unwind/app/0,lib@unwind/app/0,core@unwind/app/0` starts from exactly
  the pinned combination. A component pinned twice at different commits within one tree keeps the nearest pin in the
  tag; the result lists the other as a conflict, and it stays reachable through its own parent's tag.
- **All or nothing.** The whole tree is planned first, new components are cloned into a staging folder and pinned
  commits missing from a repository are fetched from the submodule's URL. A pinned commit that cannot be found, a
  submodule without a URL, a cycle, or a path where the parent already nests another component fails the unwind and
  nothing changes (`component add -unwind` then adds nothing either). The meta-repo is committed once.

### Creating a component

```
bassia component create -name docs                          # a new, empty component with a main branch
bassia component create -name app -references lib,ui:vendor/ui
```

`component create` makes a component that did not exist anywhere before: its history starts with one commit without
files on its main branch (`-branch` names another), so an agentic run can select it at once (`-select docs`) and write
its first files. `-url` records the upstream it will be published to.

#### Templates

A template starts a component from the monorepo's conventions instead of from nothing. It is a folder in the meta-repo,
`.bassia/templates/<name>/`, committed with it:

```
.bassia/templates/lib/
  template.toml        description, tags, visibility, exports, owners, references (each optional)
  files/README.md      starter files; {{name}} in contents and paths becomes the component's name
  files/bassia.toml
```

```powershell
bassia template list
bassia component create -name parser -template lib            # tags, exports, owners and files from the template
bassia component create -name parser -template lib -owners me # a switch given wins over the template
```

The component gets its empty first commit and a second, `Apply template '<name>'`, with the files. An unknown template
fails before anything is created. Boundary rules check the new component as they check any other.

### Forking instead of branching

A component has a single main branch. To work in parallel, fork it:

```
bassia component fork -name app -as app-search            # fork at the head of app's main branch
bassia component fork -name app -as app-hotfix -ref v1    # fork at a tag or commit on that branch
```

The fork is a new component with its own repository and its own main branch (named like the parent's). It keeps
the parent's history up to the fork point with the same commit ids, and the tags on that history; the parent's run
and integration tags stay behind. Afterwards the two are independent. `components.toml` records `fork_of` and
`fork_commit`, and the fork references what the parent does unless `-references` says otherwise. Components that
referenced the parent keep doing so; point one at the fork with `component set`.

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
bassia agent start -select app@v0,lib@v0 -run claude -p "add a hello world script" --permission-mode acceptEdits
bassia agent retry <id>                                 # re-run a failed commit/tag/push sequence
bassia agent abandon <id>                               # discard the run's folder
```

For a quick hand-check against the Debug build, `scripts/` has two helpers: `New-TestMonorepo.ps1` creates a throwaway monorepo in `%TEMP%` with the [jk09/example](https://github.com/jk09/example) component added and its HEAD tagged `tag-base`, and returns the path; `Invoke-AgentRun.ps1 -Monorepo <path> -Prompt <prompt>` runs `claude -p --permission-mode acceptEdits "<prompt>"` over it (`-Select` and `-Agent` override the defaults) and prints where each result landed.

```powershell
$r = ./scripts/New-TestMonorepo.ps1
./scripts/Invoke-AgentRun.ps1 -Monorepo $r -Prompt 'write hello world in C# as example/HelloWorld.cs'
```

`agent start -select <component[@tag|hash]>[,...] -run <command>` runs an agent on an isolated copy of the selected monorepo state:

1. Every `-select` entry names a component registered in `.bassia/components.toml`, alone or with an **annotated tag** or a **commit hash** in its source-of-truth repo (`R:\<component>`). A **bare name** (`-select app,lib`) starts from the tip of the component's default branch - the branch its source-of-truth repo's `HEAD` names, usually `main` - resolved when the run starts; the run records the branch name as the selector and the tip commit as its provenance, so the record stays reproducible although the branch moves on. A source repo with a detached `HEAD` or an unborn default branch fails, asking for `<component>@<tag|hash>`. Bare and pinned entries mix (`-select app,lib@v1`). Unregistered names, `<component>@<branch>`, `HEAD` and lightweight tags are rejected. A hash is 6 to 40 lowercase hex digits (64 in a SHA-256 repo), the full hash or a prefix of it, and must name a commit (`app@3f9c2e1`). The selector has to mean exactly one thing: a prefix that several objects share fails with git's own `short object ID ... is ambiguous` error and its candidate list, and a hex name that is both a ref and a hash prefix (a tag called `c0ffee`, say) is rejected rather than resolved the way git would, in favour of the ref; spell it `app@refs/tags/c0ffee` or use a longer hash. Shorter or mixed-case hex is always read as a ref name.
2. The selection must cover the full transitive closure of the reference graph (`references = ["lib"]` or `references = [{ name = "lib", path = "libs/lib" }]` in `components.toml`; the graph must be acyclic). Selecting `app` without the `lib` it references fails before anything is materialized — a run never pulls in a component nobody named, so every version it starts from (tag, hash or default-branch tip) is an explicit choice, and every one is pinned to a commit in the run record.
3. The run gets a random id, `agent-run-<key>` with `<key>` an adjective, a noun and a 6-character slug of lowercase letters and digits (e.g. `agent-run-magical-otter-vt9j3p`), and the folder `.workspace/agent-run-<key>/`. The id is readable, yet random enough to be unique without any coordination, so runs from different workspaces or machines never collide on the branches and tags they push to a shared component repo. Every selected component is cloned from its source-of-truth repo and checked out there, side by side, on the run branch `agent-run/<key>`. Where one component references another, the referenced component appears inside it as a junction to that sibling checkout, at the subfolder `components.toml` records; the nested path is excluded from the referring repo's index, so each repo only ever commits its own files.
4. The `-run` command is started by the platform shell (`cmd.exe` / `sh`) in `.workspace/agent-run-<key>/`, with `BASSIA_ROOT`, `BASSIA_RUN_ID` and `BASSIA_RUN_DIR` set.
5. Before the agent starts and after it finishes, the run record (`run.toml`: selection, resolved commits, command, timestamps, status, per-component results) is committed to the bare repo `.agentic-runs/.git`, at the monorepo root alongside `.bassia` and `.workspace`, with plumbing commands only (no checkout, no branch) and tagged `agent-run/<key>/<lineage>`. It lives outside both the meta-repo and the workspace, so the run folder can be discarded once its results are pushed while the record survives.
6. When the agent exits successfully, each changed component is committed on its run branch, tagged `agent-run/<key>/<counter>`, and branch + tag are pushed to its source-of-truth repo. The counter is the next unused index among the component's existing `agent-run/<key>/*` tags — all of them sit on the run branch, so the checkout's own tag list is the whole sequence; a run's first result is `/0`. Nothing is merged or checked out there. The sequence is not transactional: a failed component leaves the run `partial`; `agent retry` finishes the remaining steps and `agent abandon` discards the run folder without touching anything already pushed.

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

### Clones and views: a virtual monorepo

Bassia can work like a client-server VCS with a virtual filesystem (ClearCase, Perforce) without a filesystem driver.
A *clone* is a monorepo that caches another one, its *depot*; a *view* maps the components a *config spec* names onto
any folder, and downloads a file's content only when it is loaded. That suits an agent working in an ad-hoc place - a
cloud worker - on a part of a large monorepo.

```sh
bassia clone -from /srv/monorepo -path work        # meta-repo + every component's history, no file content
bassia clone -from https://git.example.com/mono -lazy   # meta-repo only; a view imports what it maps
cd work
bassia view create -name parser -spec parser.spec  # map and load what the spec says
bassia view ls -path app/src -unloaded             # the whole mapped tree, from history only
bassia view load -path app/docs,lib/README.md      # download and write more
bassia agent start -spec parser.spec -prompt "fix the parser's error messages"
bassia fetch                                       # meta-repo and histories from the depot
bassia view update -name parser                    # move the view to what its spec names now
```

**Clones.** `bassia clone -from <depot> [-path <dir>] [-lazy]` clones the depot's meta-repo and imports every
registered component's history - commits, branches, tags, file listings - without any file content (git: a bare
partial clone, `--filter=blob:none`). A depot is a monorepo root: a folder, or a URL under which `.bassia` and each
component are repositories. The clone records it as `depot.url` (and `-lazy` as `depot.lazy`) in its own
`config.user.toml`. With `-lazy` no component is imported until a view maps it. `bassia fetch` fast-forwards the
meta-repo and fetches every component's branches and tags from the depot (the depot wins), importing new components
unless the clone is lazy. In a clone, an agentic run's result branch and tags are pushed to the depot as well as to
the clone's store (write-through); a push that fails leaves the component `failed`, and `agent retry` pushes again.
Run records stay in the clone; integration happens on the depot.

**Config specs.** A config spec is a text of line rules; `#` starts a comment:

```
# the parser and what it needs
map app                 # app and, transitively, every component it references
select lib v2           # lib at the tag v2
select * main           # anything else at the tip of main (the default: its default branch)
load app/src            # materialize app's src folder
load !app/src/generated # ... except this
load lib/include/**/*.h
```

- `map <component>[,<component>...]` maps the components and their reference closure, as runs select it.
- `select <component-pattern> <version>` picks a mapped component's version: a branch (followed by `view update`), a
  tag (annotated or not) or a commit hash (unambiguous). The first rule whose pattern (`*`, `?`) matches wins; a
  component no rule matches is at the tip of its default branch.
- `load [!]<component-pattern>[/<path-pattern>]` names the files to download and write, with the path patterns of
  split plans (`**` any folders, `*` within a segment, a folder is everything below it; `load app` is all of app); `!`
  leaves files out. A mapped component nothing loads is there without any file.

A rule that does not parse, a component that is not registered, a load rule that matches no mapped component and a
version that names no single commit are errors naming the spec's line.

**Views.** `bassia view create -name <view> -spec <file|-> [-path <dir>]` resolves the spec, imports missing
histories from the depot, and checks out every mapped component side by side in the view's folder (default
`<workspace>/view-<name>`), each referenced component nested inside its referrer as a link, as in a run. Every working
copy is sparse: only the files the load rules match are downloaded and written (git: a partial clone of the store with
a sparse checkout of exactly those files; the store fetches their content from the depot first, since git cannot fetch
through it). A file not loaded is absent from disk, never a stub. The folder holds `.bassia-view.toml` - the spec, each
component's version, commit and load rules, and the monorepo's root - so every view command also works from inside the
view, even outside the monorepo, and views are registered locally in `<root>/.views`, never in the meta-repo.

- `view list` and `view show [-name]` describe the views; `view ls [-path <component>[/<pattern>]] [-loaded|-unloaded]`
  lists every mapped file from the history - downloading nothing - with whether it is loaded and its size when the
  clone holds its content.
- `view load -path <path>,...` downloads and writes files, folders or patterns, relative to the working directory
  inside the view (else to the view's folder), as `<component>/<path>`; a path through a nested component
  (`app/lib/x.h`) loads from that component. Every path must match a mapped file, or nothing is loaded. The rule is
  added to the manifest.
- `view update [-name]` fetches the mapped components from the depot, resolves the spec again and checks out each
  component whose commit changed at its new commit with the same load rules; a component with uncommitted changes
  stays where it is and is reported.
- `view remove -name <view> [-force]` deletes the folder and the registration; changed files keep it unless `-force`.

**Sparse runs.** `agent start -spec <file|->` runs on a config spec's mapping: the selection is the mapped closure
pinned at the commits the versions resolved to (so the record stays reproducible) and the working copies are sparse
with the spec's load rules. `-select ... -load <[!]component/pattern,...>` makes any run sparse the same way. The run
folder is a view of kind `run`: its `AGENTS.md`/`CLAUDE.md` tell the agent to list the mapped files with `bassia view
ls` and to fetch what it needs with `bassia view load`. Files the run never loaded are unchanged in its result; files
the agent writes anywhere are committed as usual. The run record keeps the load rules (`load`). The code boundary check
after the run still reads the files it scans, so in a clone it downloads them.

### Users: who did what

With several people and their agents working in one monorepo, everything Bassia does is attributed to a user, so
whoever waits for a lock knows whom to ask. The users are registered in the meta-repo (`.bassia/users.toml`), and the
registry is versioned and read through the storage contract like the component graph:

```powershell
bassia user add -id jk -name "Jozef Kosik" -email jozef@example.com -contact "slack @jozef"
bassia user use -id jk          # who you are here: user.id in your own layer (config.user.toml), never committed
bassia whoami
bassia user list
```

- **Who you are.** Bassia takes `BASSIA_USER`, then `user.id` from your own configuration layer, then the registered
  user whose email is the git `user.email` configured for the meta-repo. Identity is declared, as a git author's is.
  A user may carry a public `key`, kept for signing later.
- **What is attributed.** Once a monorepo has users, a command that changes it needs a known user. Read-only commands,
  `user add|use`, `init`, the guide and feedback are exempt, and anyone may register themselves. Commits, annotated tags
  and meta-repo revisions are authored by the user. In git, the author, committer and tagger are set for every git
  process Bassia starts; the memory store records them too. Run records (`run.toml`), the result commits' TOML records,
  integration records and lock entries carry the user's id. `agent list|show` and `integration list|show` report it.
- **Runs.** A run's work is the user's, done through the agent. Its result commits are authored by the user and
  committed, like its tags, as `<agent> via bassia` with the user's email; the agent is the first word of the run's
  command, e.g. `claude`. The agent process gets `BASSIA_USER`, so its own `bassia lock ...` commands act for the same
  user.
- **Coordination.** `lock list` has a `USER` column. `lock check`, `lock claim` and `lock wait` name the user behind
  a holding run with their email and contact (`holder_users`), so the people involved can agree who goes first. The
  agent's `AGENTS.md` points it there.
- A monorepo without users works as before and attributes nothing.

### Locking files and folders

Agents tend to rewrite large parts of a file, and two rewrites of the same file rarely merge faithfully, even when
git reports a clean merge. Locking keeps parallel runs from changing the same files - or files of the same folder - at
the same time, and tells a run when another one changed what it is about to change. Every run works in its own
folder, but they all share one view of who holds what.

```powershell
bassia config set lock.folders -value "src/*"                         # every folder directly under src is one lock
bassia config set lock.component.docs.files -value "**/*.md"           # in docs, each Markdown file on its own
bassia agent start -select app,lib -lock-files "app:**/*.csproj" -prompt "upgrade the SDK"   # more locks for one run
bassia lock list                                                       # who holds what, and who waits
```

- **The filter.** `lock.files` holds each changed file that matches on its own; `lock.folders` holds the whole folder
  a pattern names when a file in it changes: `src/*` names every folder directly under `src`, `**` the folder of every
  file, `docs` the `docs` folder. A file is held as the nearest folder above it that a folder pattern names, else as
  itself (when a file pattern matches it, or a folder pattern covers it without naming one of its folders); other
  files are not held. Patterns are those of split plans. Both are set for every component or for one
  (`lock.component.<c>.files|folders`), and `agent start -lock-files` / `-lock-folders` add to them for one run - a
  `<component>:` prefix limits a pattern to one component. Unset, nothing is held and runs behave as before.
- **Holds are implicit.** While a run's agent works, the bassia process executing the run looks at the run's component
  checkouts every two seconds (`git status`) and holds every changed path its filter covers; a change the agent
  reverts is let go. Holds live in `<root>/.locks/<run-id>.toml`, at the monorepo root rather than in the workspace, so
  every `bassia` process of the monorepo sees them, whichever workspace it uses. Decisions that depend on what others
  hold are made under an exclusive OS file lock on `.locks/.mutex`, which the OS releases when a process dies.
- **What another run holds binds everyone.** A run that changes a path overlapping a live hold of another run - the
  same file, or a file in a held folder, or a folder containing a held file - whatever its own filter says, records a
  `held` **lock conflict**.
- **Asking first.** An agent (every agent command has `BASSIA_RUN_ID`) uses `bassia lock check -path <p>` before it
  rewrites a file: is it free, who holds it, and has a finished run changed it since the run's checkout was made?
  `bassia lock claim -path <p>` holds paths up front (all or none). `bassia lock wait -path <p> [-timeout <s>]` waits
  until no other live run holds them, then holds them and **brings the results of the runs that changed them into the
  run's checkout** (`git merge` of their result tags, a fast-forward while the run's branch is at its base); it lists
  the files that changed meanwhile under `changed`, since the agent's view of them is out of date. `-no-refresh` only
  reports. A wait that would close a cycle (the holder waits, directly or through others, for something the run holds)
  fails at once, as a deadlock. Paths are given as the agent sees them - relative to its working directory inside the
  run folder, through junctions into nested components - or as `<component>/<path>`; a folder ends with `/`.
- **Telling the agent.** When a run's filter locks anything, the run folder gets an `AGENTS.md` and a `CLAUDE.md`
  (outside every component checkout, so never committed) that explain the protocol, so Claude Code and agents that
  read `AGENTS.md` check and wait before they rewrite a locked file.
- **Failed and suspended runs.** A hold is live while its run's bassia process renews it at least every `lock.ttl`
  seconds and, on the same machine, that process lives. A crashed run's holds therefore stop blocking at once, a
  suspended one's (or one on another machine sharing the root) after `lock.ttl`. When a suspended run comes back,
  whatever another run took meanwhile is that run's: the returning run loses it and records a conflict. `agent stop` of a
  run whose process is gone, `agent stop` that had to kill a run, and `agent abandon` remove its holds; `lock release -run
  <id> -all` breaks those of a run that is not live, `-force` those of one that is (a stuck agent).
- **After a run.** When a run ends, it releases its holds. If it pushed results, its holds are remembered as
  *released* until those results land on their component's default branch: a later run that changes such a path
  without the result in its checkout records a `stale` lock conflict, and `lock wait` (or `lock check`, which reports
  it as `behind`) brings the result in at once instead of blocking.
- **The record.** A run's `run.toml` keeps, per component, what it `held`, its `lock_conflict`s (path, kind, the other
  run) and what it `caught_up` with; `agent start`, `agent show` and the run's card report them. `agent diff` of a run that
  caught up shows only its own change, from its result's first parent.

Locks are advisory while the agent works - Bassia cannot stop an agent from writing a file - so a collision is
recorded and the integration enforces `lock.conflict` (see below). The integration also merges a result after the
results it brought in, and treats it no less carefully than them.

### Tasks, affected components and the verification gate

A component defines its own tasks in a `bassia.toml` at its root, versioned with the code they build, as nx's
`project.json` is:

```toml
[tasks.build]
command = "dotnet build"
depends_on = ["^build"]      # first: the build of every component it references, transitively

[tasks.test]
command = "dotnet test --no-build"
depends_on = ["build"]       # first: its own build
```

```powershell
bassia task list                                  # every component's tasks on its main branch
bassia task run -task test -select app,lib@v2     # lib:build, app:build, app:test - in a checkout laid out as a run's
bassia affected -runs brave-otter                 # what the run changed, and everything that references it
bassia config set merge.verify -value build,test  # the integration's gate
```

- **Tasks.** `command` is a shell line run in the component's folder. In `depends_on`, `x` is the component's own
  task `x` and `^x` is task `x` of each component it references, transitively, that defines one. An unknown
  dependency or a cycle is an error naming the task.
- **`task run`** checks out the selection's closure under the workspace (`tasks-<key>`), with each reference nested
  as in a run. It runs the selected components' tasks and what they depend on, one after another in dependency order,
  and stops at the first failure. Each task's output goes to a log under `<workspace>/.task-logs`. The checkout is
  removed when every task passed, unless `-keep`. Without `-select`, every component is selected at its main-branch
  tip. Inside an agentic run (`BASSIA_RUN_DIR`), the run's own checkout is used instead, so an agent can build and
  test its change as it stands.
- **`affected`** takes `-runs` (their pushed results) or `-components`. It lists those components and every component
  that references one of them, transitively, in dependency order. It also gives the `-select` value covering their
  closure.
- **The gate.** `merge.verify` (a list of task names, also per component as `merge.component.<component>.verify`)
  makes the integration verify before it publishes. Every component is merged first. Then the base tips of the
  affected components it did not change are checked out next to the merged ones, the references are nested, and each
  affected component's `merge.verify` tasks run (with what they depend on). A component that defines none of them is
  not checked. If a task fails, nothing is published: the merged components are `failed` with the task named, the
  integration is `partial`, and `integration.toml` records every task that ran under `[[verification]]` (exit code,
  duration, log). The workspace is kept for inspection. Without `merge.verify`, the integration publishes as before.

#### The task cache

A task marked `cache = true` is not run again while its inputs are unchanged:

```toml
[tasks.build]
command = "dotnet build"
depends_on = ["^build"]
cache = true
outputs = ["bin", "obj"]     # what a restore puts back, as patterns relative to the component
```

- **The key** is a SHA-256 over the task's name, command, `depends_on` and `outputs`, the platform (OS and
  architecture), and the content of its component and of every component it references, transitively. In a checkout
  made from commits (`task run -select`, the gate), that content is each component's commit. In a run folder it is a
  hash of the files git would track, so uncommitted changes count. The two never match each other, which can cost a
  rebuild but never restores a stale result.
- **A hit** copies the cached outputs back into the component's folder and the cached log to the task's log path.
  The task counts as passed and is reported `cached`, in `task run`'s result and in the gate's `[[verification]]`.
  **A miss** runs the task, and a passed run stores its log and outputs. A failed run is never stored.
- Caching is opt-in, as in nx: only a task that gives the same result for the same inputs may be cached. A cached
  task whose outputs are undeclared is skipped without restoring anything, so a `build` that others depend on must
  declare its `outputs`.
- The cache is local to the workspace (`<workspace>/.task-cache`). `task run -no-cache` runs everything, and
  `bassia task cache` reports the entries and their size (`-clear` empties it).

### Integration: syntactic first, then structural, then semantic

Every successful run leaves its changes in each component it touched as a result tag `agent-run/<key>/<n>` - the same name in every component. `integration start` consolidates the results of several runs, component by component, into one integration:

```powershell
bassia integration plan -runs all                                # the triage only; nothing changes
bassia integration start -runs brave-otter,quiet-fern             # runs by (a prefix of their) key
bassia integration start -runs all -onto lib@v1 -skip brave-otter -semantic quiet-fern -resolve claude -p --permission-mode acceptEdits --model opus
bassia integration plan -runs all -weave off                     # the triage without the structural merge
bassia integration advance <id>                                  # fast-forward the base branches to the result
```

1. **Triage.** For each component the runs changed, the results are considered oldest run first against a base: the component's default branch (usually `main`), or the branch or tag `-onto` names. Git classifies each one without a working tree (`git merge-tree`): `up_to_date` (already contained), `fast_forward`, `clean` (a three-way merge without conflicts) or `conflict`. Where git conflicts, the **structural merge** is asked the same question (see below): `clean`, `warnings` (merged, but weave doubts the result means what both sides meant) or `conflict`, with the files still conflicted after it. The results git or the structural merge can merge cleanly are chained onto a simulated head in order, so a result that merges cleanly onto the base but collides with an earlier one is caught here too. Every step is then `syntactic` (git merges it), `structural` (git merges it with weave as the merge driver), `semantic` (the resolver merges it), `manual` (left for a human) or `skip`. The **merge policy** (the `merge.*` settings, see [Configuration](#configuration)) decides between the resolver and a human: with `merge.semantic = manual`, with `merge.warnings = manual`, or for conflicts in `merge.manual_paths`, a step is `manual`; with `merge.warnings = accept` weave's merge with warnings stands. A result that adds **code boundary violations** is `manual` with `merge.boundaries = manual`. With `merge.owner_review = manual`, a result in a component that has `owners` is `manual`, and its note names them (the component's `owners` are recorded with it). A result with a **lock conflict** (it changed a path another run held, see [Locking files and folders](#locking-files-and-folders)) goes by `lock.conflict`: to the resolver even when git or weave merge it cleanly (`resolver`, the default; its brief lists the conflicts), to a human (`manual`), or like any other result (`merge`). Results are taken oldest run first, except that a result which brought other runs' results in (`lock wait`) comes after them, and is `manual` or `semantic` when one of them is. `-semantic` sends a result to the resolver even without a textual conflict, for a semantic review, and wins over the policy; `-manual` leaves a result for a human; `-skip` leaves it out. The triage also lists, for each result, the other runs it conflicts with directly.
2. **Syntactic and structural steps first.** Each component is merged in its own checkout under `.workspace/integration-<key>/<component>`, on the branch `integration/<key>` started at the base. Every syntactic step is a `git merge --no-ff` of the run's result tag, every structural one the same with weave as the merge driver, in run order. If one conflicts after all (or the structural merge warns), it moves to the back of the semantic queue.
3. **Semantic steps next.** For each remaining step Bassia starts the merge - with weave as the merge driver when the structural merge is enabled, so only what weave could not merge is left conflicted, in entity-labelled conflicts - with `diff3` conflict markers for what git's own merge leaves, so the common ancestor is visible, and writes a **semantic brief**: the incoming run's prompt, baseline, command and commit records; which runs are already integrated and why; the history and diff of both sides since their common ancestor; the conflicted files (and, with the structural merge, which files git alone conflicted on, which of them weave merged, and weave's warnings); and instructions. The resolver command (by default `claude -p --permission-mode acceptEdits`, configurable as `[integration] resolver` in `config.toml`) runs in the component's working tree with the brief on stdin and these environment variables: `BASSIA_MERGE_BRIEF` (the brief's path), `BASSIA_COMPONENT`, `BASSIA_RUN_ID`, `BASSIA_INTEGRATION_ID` and `BASSIA_ROOT`. When it exits with code 0 and no conflict marker (nor weave's `weave explain` pointer) is left, Bassia commits the merge. A non-zero exit or leftover markers abort that step, which is recorded as `failed`; the other steps still go ahead.
4. **Result.** Each merge commit's message is a subject and an `[integration]` TOML record (run, source tag, strategy, rationale, conflicts, structural driver and conflicts, resolver). The result is tagged `integration/<key>/<n>` - one identically named annotated tag across the components - and branch and tag are pushed to the component's source-of-truth repo. That tag is a baseline like any other: `agent start -select app@integration/<key>/0,...` starts the next run from it. Nothing else moves: `integration advance <id>` fast-forwards each component's base branch to the result, and refuses if the branch moved since the integration was built on it, or if the base was a tag.

The integration's record (`integration.toml`: runs, resolver, structural driver, and per component its base, every step's triage, structural verdict, strategy, conflicts, outcome, merge commit and brief) is committed to `.agentic-runs` next to the run records and tagged `integration/<key>/<lineage>`. Status is `completed`, `needs_attention` (everything merged except the steps the policy left for a human; their outcome is `needs_attention`), `partial` (a step or component failed; what did merge is still published), or `cancelled`. With `merge.advance = auto`, a `completed` or `needs_attention` integration advances the base branches of the components whose policy says so.

#### The structural merge (weave)

Parallel agentic runs often add or change *different* functions, methods or keys of the *same* file. Git's line merge reports that as a conflict when the hunks touch, although nothing collides, and the integration would hand it to the LLM resolver. [weave](https://github.com/Ataraxy-Labs/weave) merges such files by entity instead of by line: it parses base, ours and theirs with tree-sitter (38 languages, plus JSON, YAML, TOML, Markdown and more), merges independent entities cleanly and only conflicts when both sides changed the same entity incompatibly. Like git's merge it is deterministic, so a result weave merges cleanly is a `structural` step that never reaches the resolver.

- The release packages bundle weave's `weave-driver` next to `bassia`, which uses that copy when no `weave-driver` is on `PATH`. Otherwise install it (`brew install weave`, or `cargo install --path crates/weave-driver` from a clone of the repository) and put it on `PATH`. Bassia uses it when it is found; otherwise the integration behaves exactly as without it, and `integration plan` reports `structural_merge = "unavailable: ..."`. Name another driver with `bassia config set integration.weave <command>` or `-weave <command>`; `off` disables it.
- Nothing in a component repo is changed: the driver and the attributes that assign it to weave's file types are passed to git with `-c` for each command, and the attributes file lives in the workspace (`.workspace/.structural-merge.gitattributes`). A component's own `.gitattributes` still takes precedence; the `-c core.attributesFile` replaces a user-wide attributes file for these merges only.
- A merge weave completes with warnings (`weave-warning:` on its stderr, e.g. an entity depends on another entity the other side changed, or the merged file no longer parses) goes to the resolver for review, with the warnings in its brief.
- What weave still conflicts on goes to the resolver as before, but the resolver starts from weave's entity-labelled conflicts (``<<<<<<< ours — function `process` ...`` with a `refused_by:` line) instead of git's.

### Driving Bassia from an agent

Bassia has no LLM of its own and never asks one about itself. An agent - Claude Code, Codex, or a script around any
model - stays outside and drives Bassia like a person would, through the command line. Because every command has
the same shape and answers in TOML, the agent needs no per-command glue and no documentation beyond what the CLI
prints:

1. `bassia` (without a terminal) or `bassia help`: the concepts (`[[concept]]`: monorepo, component, reference,
   meta-repo, tag, agentic run, agent command, lock, integration, configuration, skill), the usual `workflow`, how
   to learn more (`discover`), the grammar, the result format, the exit codes and every command (`[[commands]]`).
2. `bassia help -all`: every command in full - usage, details, switches (with `required` and `rest_of_line`) and
   examples - in one result; or `bassia help <command>` for one.
3. `read_only = true` marks the commands that change nothing in the monorepo (`status`, `config list|get`,
   `component list|show|survey`, `tag list|show`, `graph`, `log`, `agent list|show|logs|wait|diff`,
   `lock list|check`, `integration plan|list|show|logs|wait`, `skill list|show`, `help`, `version`, `web`): the agent
   may run them freely to explore, except `web`, which is interactive. Any other command changes something and is run only when that change is what the
   agent was asked for. `interactive = true` marks the commands meant for a person: `tutorial` reads answers from
   stdin, `feedback send` asks before anything leaves the machine (unless `-yes`), and `web` serves until Ctrl-C.
4. `bassia status`, `bassia config list` and `bassia skill list` describe the monorepo at hand.

An instruction such as "you can use the `bassia` CLI; start with `bassia help`" is all an agent needs. `bassia
prompt`, which planned commands with an LLM Bassia started itself (`llm.backend`, `llm.command`), was removed; an
`[llm]` table left in an older `config.toml` is ignored.

**Skills** are reusable instructions for recurring tasks, versioned in the meta-repo like `config.toml`, in the
shape Claude Code uses: `.bassia/skills/<name>/SKILL.md`, with optional front matter.

```markdown
---
name: release
description: Cut a release - tag every component release-<n> and integrate the pending runs first.
---
1. Run `integration plan -runs all`; stop and report when anything conflicts.
2. ...
```

`skill list` shows every skill's name and description, `skill show -name <skill>` its instructions, so an agent
reads only those it needs. They are added and changed as files and committed with `git -C .bassia commit`.

### Web dashboard

```powershell
bassia -C R:\ web                        # http://127.0.0.1:8080/, opened in the browser
bassia -C R:\ web -port 9000 -no-open
```

`web` serves a read-only dashboard over the monorepo, in the spirit of [Fossil](https://fossil-scm.org)'s built-in
web interface: a place to look at the monorepo, while the command line changes it. Every page
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
  finalizing, done) with the current step pulsing, a ticking clock, its components, the paths it holds and waits
  for, and the latest line of its output. They come from the job registry, so a run started from any terminal appears by itself. Below them, every
  run over time (live bars reach to now) and runs by status. A run's page streams its output while it is live and
  draws its path from its baselines to its results and the integrations that took them.
- **Locks**: who holds what, live: a flow from the runs waiting, through the paths they wait for and every held path,
  to the runs holding them, and a table of each hold (file or folder, live, released with its result tag, or expired,
  when it was last renewed and who waits for it). Then the lock filter of each component with what a lock conflict
  leads to, and every recorded lock conflict with where its result stands. A run's page adds what it held, its
  conflicts and what it caught up with; a component's page its filter and its holds; the component map a badge for
  held paths.
- **Merge queue**: every result not yet on its component's default branch: `waiting`, `integrated` (its base not
  advanced) or `needs attention`. For what waits, the triage of the next integration is drawn as one lane per
  component, in merge order, with each result coloured by who merges it: git, weave, the resolver, or a human. A
  bar per component shows the split, and a ring joins the runs whose results collide. A table gives each merge's
  meaning: why the run changed things (its prompt), its lock conflicts, what git found, how weave saw it, and why
  it goes where it goes; a result with a lock conflict is ringed in the lanes. Then come the integrations waiting for `integration advance`, and every merge that **needs attention**
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

### Feedback and crash reports

`bassia feedback send` files feedback - a bug, an idea or a question - as an issue in the Bassia repository
(`jk09/bassia`). It first shows the whole issue and asks you to confirm; `-yes` confirms in advance, and without a
terminal to ask on nothing is sent unless `-yes` is given.

```powershell
bassia feedback send status shows a stale run after a crash
bassia feedback send -kind idea -yes let tag create take a pattern
```

When Bassia itself crashes (an exception no command expects) it prints a short error, exit code 1, and can file the
crash as an issue. Nothing is sent until you have agreed, and what is sent follows your choice, shown and changed with
`bassia feedback reports [-mode off|ask|auto]`:

| Mode | What a crash does |
|------|-------------------|
| not chosen yet (the default) | Nothing is sent. At a terminal it shows the report and asks once: yes, *always*, no or *never*. |
| `ask` | At a terminal it shows the report and asks; without one nothing is sent. |
| `auto` | It files the report without asking, also in scripts and for agents. |
| `off` | Nothing is sent and nothing is asked. |

A report holds the Bassia version, the runtime and OS, the name of the failing command (never its arguments or prompt)
and the exception with its stack trace. The home folder, user and machine names, credentials in URLs and anything that
looks like a token are replaced first. The same crash is filed once per machine, and when an open issue already has it,
the report is added to that issue as a comment.

Issues are created with the GitHub token in `BASSIA_FEEDBACK_TOKEN` (else `GITHUB_TOKEN`); a fine-grained token that may
only write issues of the repository is enough. Without a token, or when GitHub cannot be reached, an approved report is
kept in `feedback/pending` under `BASSIA_HOME` and `bassia feedback pending -send` sends it later. A failed report never
changes the outcome of the command. The choice is stored in `feedback.toml` in the same folder.

## Architecture

Bassia has three layers; each depends only on the ones below it.

1. **Storage** (`Bassia/Storage`) - the contract a component backend implements: `IComponentStore` is one
   component's history (a DAG of commits, branches, annotated tags, file listings and nested units) with three-way
   merges of two commits, merge bases and diffs; working copies (`IComponentWorkspace`: check out, commit, tag,
   publish, merge - committed, fast-forwarded or left open with conflicts - reset, abort, changed and contained
   commits) are what agents and resolvers edit; `IComponentStorage` creates, imports, opens, forks, splits and deletes
   stores by component name and stages imports (`IStagingArea`); a `FileMergeDriver` (weave) merges single files for
   any backend. `IMetaStore` keeps the component registry with history, applying `RegistryChange`s (add, remove, set
   references) as revisions; `IRecordStore` keeps versioned records, and `IRunRecordStore` agentic runs on top of it.
   `Storage/Git` implements it on bare repositories next to `.bassia`, `components.toml` and `.agentic-runs`;
   `Storage/Memory` is the reference implementation, in memory with working copies in real folders, merging at file
   granularity.
2. **Component graph** (`Bassia/Core`) - `ComponentRegistry` is the immutable, validated graph of components and
   their references (unique valid names, references to registered components at paths inside the referrer, no
   cycles; closure and dependency order), with the components' tags, visibility and owners and the boundary rules
   (`Core/Boundaries`: tag patterns and the violations of a graph). `MetaRepo` maintains it: every change is checked against the graph and its
   boundaries (a change to the graph may not add a violation), then persisted as one revision. `MonorepoGraph` is the API over both: create, import, remove, reference, tag, fork,
   split and unwrap components, and `AgentRuns` runs agentic runs on it (selection, working copies with nested
   references, commit/tag/publish, records, retry, abandon), with the agent process and the lock session injected.
   Integration (`Bassia/Integration`: the triage, the merges, the resolver, publishing and advancing, with
   `IntegrationSettings` for policies and how the resolver runs) and lock catch-up (`LockCheckouts`) work on the same
   contract.
3. **Commands** (`Bassia/CliCommands`, `Bassia/Web`) - parse, call the graph for the monorepo on disk
   (`Monorepo.Graph()`), and format results.

A store may hold its history without the files' content (`IComponentStorage.ImportHistoryAsync`, a clone of a
depot): it lists files without fetching them (size -1), reports what it lacks (`MissingContentAsync`), fetches it from
its upstream on request (`HydrateAsync`) or when a file is read, and follows or publishes to its upstream
(`FetchUpstreamAsync`, `PushUpstreamAsync`). A checkout with load patterns is sparse and `IComponentWorkspace.LoadAsync`
materializes more; views (`Core/Views`: `ConfigSpec`, `Views`, `ViewManifest`, `Depot`) and sparse runs build on that.

A replacement for git implements the storage contract; nothing in the component graph, the integration or locking
changes. `bassia log`, `agent diff`, `component survey` and the dashboard's history views still read git directly; the
lock table (`.locks`) is a file store for coordinating processes, not component history.

## Build

```powershell
dotnet build
dotnet run --project Bassia -- help           # or put the built Bassia executable (or bassia.cmd) on PATH
```

### Packages

CI (`.github/workflows/ci.yml`) builds an installable package per platform (`win-x64`, `linux-x64`, `linux-arm64`,
`osx-x64`, `osx-arm64`) on every push and pull request: the CLI published framework-dependent, weave's `weave-driver`
pinned by checksum in `packaging/weave.toml`, `install.sh` or `install.ps1`/`install.cmd`, and the version manager. It
installs each package on its own platform with `packaging/Test-BassiaPackage.ps1` (the installer refuses a machine
without the .NET runtimes, installs, and the installed `bassia` merges with the bundled weave), uploads the packages as
artifacts, and publishes them as a GitHub release when a `v<semver>` tag is pushed. The installers check git and the
.NET and ASP.NET Core runtimes first, then install into the same side-by-side layout as `scripts/bassia-versions.ps1`.
Build one locally with `./packaging/New-BassiaPackage.ps1 -Rid <rid>`; see [packaging/README.md](../packaging/README.md).

## Test

```powershell
dotnet test                                   # everything
dotnet test --filter Category!=EndToEnd       # skip the tests that need network access
dotnet test --filter Category=EndToEnd --logger "console;verbosity=detailed"
```

The tests under `Bassia.Tests/Core` exercise the component graph (registry, meta-repo, create/import/remove/
reference/tag/fork, split, unwrap and agentic runs including failures, retry and abandon), the integration (triage,
merges, a resolver, structural merges, publishing, advancing) and lock catch-up on the in-memory storage, so
they need neither git nor a shell and run in well under a second. Most other tests drive the CLI in-process against
components created locally. `CliSurfaceTests` covers the grammar, the
help of every command and the monorepo, configuration, component, graph and log commands; `RunCommandTests` the run
and integration commands, including detached runs and stopping them, which start the built `bassia` apphost as a real
background process. `ComponentSplitTests` splits a component with a branchy history (moves into and between
parts, a merge, tags, a dropped folder, a path reused after its file moved away) and checks every part's files,
`git log --follow`, tags, record commit and registration, plus plan errors, dry runs, rollback and the survey.
`ViewTests` (on the in-memory storage) covers config specs, views and sparse runs in a clone, counting the content each
store fetched so laziness is proven, and write-through with its retry; `ViewCommandTests` clones a local depot and checks
with git which file contents each step downloaded. `LockTests` covers the lock filter and keys, the lock table's expiry, dead processes, deadlocks and a suspended run
losing its holds, and - with a detached run holding while a second run starts - implicit holds, conflicts and what
`lock.conflict` makes of them, `lock claim` and a timed-out `lock wait`, and `lock wait` bringing the holder's result in.
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
(`agent start -detach`, `agent wait`), wave 2 in the foreground - implementing an insertion sort library, a greeting
library together with a program in `apps` that uses it, a hello world program, a terminal program sorting its
arguments with `sortlib`, and a descending sort. Each session's agent is a deterministic stand-in that applies a
patch computed against its baseline. Two sessions edit the same lines of `apps/README.md`, so each wave's
`integration start` has git merge what it can and a deterministic resolver (a git union merge) the conflict, then
`integration advance` moves every `main`. `log -run` must report every result off `main` before the advance and
landed through the integration after it, in exactly the components the session edited; `log -component apps
-branch main` must attribute the mainline to all five sessions; `component show`, `agent list` and `integration list`
must agree. A last session selects the three `main` states and builds and runs the C programs (skipped without a C
compiler on `PATH`).

Both write a Markdown report of every commit, tag and record they verified, to the test output and to a file. Three
environment variables steer them:

- `BASSIA_E2E_PROOF` — where to write the report: a file, or a folder for one report per test (default: a
  timestamped file in the temp folder).
- `BASSIA_E2E_KEEP` — keep the monorepo after the test so the refs in the report can be inspected.
- `BASSIA_E2E_COMPONENT_URL` — clone from a local mirror instead of GitHub, to run offline.

Future features can add workflow-specific behavior while continuing to use Git for repository compatibility.