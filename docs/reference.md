# Bassia reference

The complete guide to the client-server `bassia`: the command line, workspaces and landing, conflict policies, leases,
agentic runs, git import and export, the server's Web API and Bassia Studio, its web UI. For what Bassia is and why, start with the
[README](../README.md); for the design it implements, see
[Design: Cloud-Backed Version Control for Agentic Development](https://claude.ai/artifact/V2nkDzNs8XAsx1E9PpgwM7).

Bassia keeps its own history: immutable, content-addressed objects in blob storage, and a few mutable pointers.
Git is an import and export format at the edge, not the storage.

## Requirements

- .NET SDK 10.0 or later (the runtime alone to run a build)
- Git on `PATH` for `bassia import` and `bassia export` only

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
version id. The build runs in a temporary git worktree, so the checkout's own working tree is never touched, and it
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


## Concepts

| Concept | What it is |
| --- | --- |
| monorepo | One tree of files, versioned as immutable objects in blob storage, held by a `bassia-server` (the **server**). |
| client | A folder connected to a server by `bassia clone` (say `D:\clients\a`): `.bassia/` holds its settings, run logs and the cache of objects it fetched, and `workspaces/` the workspaces made in it. Many clients share one server. `bassia init` makes a **local monorepo**: a client with its own storage and no server. Every command runs in a client (its folder, or `-C <folder>`). |
| mainline | The single trunk. Every landing is the next **seq** (1, 2, 3, ...) and its history is linear. There are no branches and no tags. |
| workspace | Where work happens instead of a branch, always made in a client: a folder (`<client>/workspaces/<name>`) holding a tree, plus a record on the server with its **base** (mainline at a seq, or another workspace it is **stacked** on) and its **working commit**. Every agentic run gets one. |
| implicit commits | Every command that reads a workspace snapshots its folder first. Nobody stages or commits. |
| landing | Moving a workspace's change onto mainline (`workspace land`); `workspace check` says whether it can land now. |
| change id | The stable identity of a unit of work. It survives snapshots, sync, landing and replay; the commit id (a hash) changes every time. |
| conflict policy | What happens to a path both a workspace and mainline rewrote: `replay` (default), `textmerge`, `semantic` or `conflict`, per path glob in `.bassia.toml`. |
| replay | Rebase by replay: run a workspace's recorded task again on mainline's head, keeping its change id (`agent replay`). |
| operation log | Every mutation of mainline or of a workspace is an operation pointing at a view; undo and restore write a new operation. |
| lease | An advisory hold on a path prefix, exclusive or intent, with a time to live and a fencing token. |

A typical session:

```sh
bassia init -path ~/acme                                        # or: bassia clone -from http://bassia.example.com:8080 -path ~/acme
cd ~/acme
bassia import -git https://github.com/acme/api.git,https://github.com/acme/web.git
bassia agent start -prompt "paginate the list endpoint" -detach  # a workspace per run
bassia agent start -prompt "add request logging" -detach
bassia workspace list                                           # which can land, which need a replay
bassia workspace land paginate-the-list-endpoint-4f2a91
bassia workspace check add-request-logging-9c1e07               # needs-replay? it touched what the first one rewrote
bassia agent replay add-request-logging-9c1e07 -land            # the same task on the new head, the same change id
bassia agent list                                               # watch all of it (or in Bassia Studio)
```

## Command line

`bassia` is operated mainly by AI agents, so the command line is its primary interface: everything Studio shows
has a command, and every command answers in TOML.

```
bassia [-C <path>] <command> [<subcommand>] [-switch [value]]...
```

- Switches are case-insensitive and may be written `-name` or `--name`. Lists are comma-separated (`-git a,b`) or given
  by repeating the switch.
- A command's main argument may be given without its switch: `bassia workspace land fix-login` is
  `bassia workspace land -workspace fix-login`.
- A rest-of-line switch (`-run`, the message of `feedback send`) takes everything after it verbatim, so it comes last.
  Given as one string, `-run` is one shell line. Given as several words, as a shell hands them over, they are
  joined back into one line. On Linux and macOS, a word that holds whitespace, a quote, a backslash or a backtick is
  single-quoted, so it reaches the agent's shell exactly as given. Other words (`&&`, `|`, `>`, globs, `$VAR`) stay
  shell syntax. On Windows, a word with whitespace is put in double quotes.
- `-C <path>` runs `bassia` as if it had been started in `<path>`, same as `git -C <path>`; it may repeat, each relative
  to the one before. `-human` and `-help` (`-h`, `--help`) are accepted anywhere on the line.
- A workspace is named by its name, its id (`w3f9a1c2b0d`), or a unique prefix of either of at least four characters.
  Inside a workspace's folder the workspace switch can be left out. Names are case-sensitive and unique among open
  workspaces: creating or cloning under the name of an open workspace fails, and a landed or abandoned workspace frees
  its name. The name is reserved atomically with a head `name/<name>` that points at the workspace's first operation,
  so of two concurrent creates only one succeeds. Names Bassia makes up itself (`restore-<seq>`, `import-<time>`, run
  and replay names) take the next free suffix instead (`restore-12-2`). A legacy duplicate (two open workspaces of one
  name, made before names were reserved) keeps working by id.
- Runs are named by their id (`run-brave-otter-3f2a91`), a unique prefix of its key of at least four characters
  (`brave-ot`), or the name of their workspace.

The command line is declared once, as a tree of [`System.CommandLine`](https://learn.microsoft.com/dotnet/standard/commandline/)
`Command`, `Option` and `Argument` objects in `Bassia/Cli/CommandTable.cs`: groups (`workspace`, `mainline`, `agent`, ...)
nest their subcommands, each command lists its switches (`Value`, `List`, `Flag`, `Rest`) and names the switch its bare
argument stands for. The parser, `bassia help`, the usage line of an error and the tests all read that tree, so to add a
command or a switch, declare it there and give the command a handler. A thin pass (`CommandLineNormalizer`) keeps the
grammar above in front of the library, which is case-sensitive and has no rest-of-line switch.

A bare `bassia` prints the full help as TOML, or a short help when a person is at a terminal. `bassia help` explains
the concepts (`[[concept]]`), the usual `workflow`, how to learn more (`discover`) and lists every command
(`[[commands]]`: name, summary, usage, `read_only`, `interactive`). `bassia help -all` describes every command in full,
`bassia help <command> [<subcommand>]` (or `-help` on any command) explains one.

### Results

Every command prints its result as TOML — on stdout with exit code 0 when it succeeded, on stderr with exit code 1
when it failed, and exit code 2 when the command line itself does not parse (unknown command, subcommand or switch, a
missing value). A result opens with the comment line `# bassia result`, then `ok`, the `command`, and `message` or
`error`; commands add their own keys after those, lists come as arrays of tables (`[[workspace]]`, `[[landing]]`,
`[[conflict]]`, ...), and pictures (tables, patches, output) are multi-line literal strings. Values TOML cannot express
are absent keys. Add `-human` to any command to print the result as Markdown for a person instead; only the format
changes.

```toml
# bassia result
ok = true
command = "workspace check"
message = "1 path(s) changed on mainline since seq 3; replay the task on seq 4 ('bassia agent replay') or sync and resolve."
outcome = "needs-replay"
main_seq = 4
touched = 2
workspace = "billing-fix"
can_land = false
next = "bassia agent replay billing-fix   (or: bassia workspace sync billing-fix, resolve, land)"

[[conflict]]
path = "src/Billing/Rounding.cs"
case = "both-rewrote"
policy = "replay"
detail = "mainline changed it at seq 4 after the workspace's base seq 3"
```

The commands removed with the component graph (`component`, `graph`, `tag`, `branch`, `commit`, `integration`,
`lock`, `view`, `boundary`, `conformance`, `task`, `log`, ...) fail with exit code 2 and name what to use instead.

| Area | Commands |
| --- | --- |
| Monorepo | `init [-path <dir>] [-postgres <connection>]`, `clone -from <url> [-path <dir>] [-token <t>] [-postgres <connection>]`, `status`, `version`, `help` |
| Configuration | `config list`, `config get -key <k>`, `config set -key <k> -value <v>`, `config unset -key <k>` |
| Workspaces | `workspace create <name> [-path <dir>] [-on <workspace>] [-intent <text>] [-context <rules>] [-materialize full\|lazy]`, `workspace clone -from <w> -name <n> [-context <rules>] [-materialize ...]`, `workspace checkout <w> [-materialize ...]`, `workspace context [<w>] [-set\|-add\|-remove <rules>]`, `workspace mount [<w>]`, `workspace unmount [<w>]`, `workspace list [-all [-limit <n>]]`, `workspace show`, `workspace status`, `workspace diff [-patch] [-path <p>]`, `workspace snapshot`, `workspace describe -intent <text>`, `workspace check [-strict -reads <p>,...] [-lease-token <id:token>,...]`, `workspace land [-keep] [-strict -reads ...] [-lease-token ...]`, `workspace sync`, `workspace undo`, `workspace restore -op <op>`, `workspace log [-limit <n>]`, `workspace close [-abandon] [-remove]` |
| Mainline | `mainline log [-limit <n>] [-path <p>]`, `mainline show [-seq <n>] [-patch]`, `mainline ls [<path>] [-seq <n>]`, `mainline cat <path> [-seq <n>]`, `mainline changed -since <seq> [-prefix <p>]`, `mainline restore -seq <n>` |
| Leases | `lease list [-prefix <p>]`, `lease acquire <prefix> [-workspace <w>] [-mode exclusive\|intent] [-ttl <s>] [-wait <s>]`, `lease renew -id <id> -token <n>`, `lease release -id <id> -token <n>` |
| Agentic runs | `agent start (-prompt <text> \| -run <command...>) [-workspace <w>] [-name <n>] [-on <w>] [-model <m>] [-land] [-keep] [-lease <p>,...] [-lease-wait <s>] [-detach]`, `agent list [-status <s>]`, `agent show`, `agent logs [-tail <n>]`, `agent wait [-timeout <s>]`, `agent stop`, `agent replay <run\|workspace> [-land]`, `agent job` (in a container: clone, then start or replay from `BASSIA_JOB_ARGS`) |
| Git | `import -git <url\|path>,... [-prefix <folder>,...] [-history] [-limit <n>]`, `export -git <dir> [-branch <b>]` |
| Dev Drive | `devdrive status [-path <dir>]` |
| Maintenance | `gc [-dry-run] [-grace-hours <n>] [-retention-days <n>]`, `scrub [-sample <n>]` |
| Feedback | `feedback send [-kind bug\|idea\|question] [-yes] <message...>`, `feedback reports [-mode off\|ask\|auto]`, `feedback pending [-send]` |

## Workspaces

`bassia workspace create <name>` opens a workspace: a folder (by default `<monorepo>/workspaces/<name>`) holding
mainline's newest tree, and a record with its base seq and its working commit. Edit the folder with any tool; the
workspace's root holds a `.bassia-workspace` marker naming it, which is never snapshotted.

- **Snapshots are implicit.** Every command that reads a workspace (`status`, `diff`, `check`, `land`, `sync`, `undo`,
  `show`) first snapshots its folder: only files whose size or modification time differ from the tree state are
  hashed, only chunks the store lacks are uploaded, the trees along changed paths are rebuilt, and the working commit
  is amended on the workspace's own head. Workspaces never contend with each other, however many agents snapshot at
  once.
- **Ignore rules.** Version control and build folders (`.git/`, `bin/`, `obj/`, `node_modules/`, caches) are never
  snapshotted unless already tracked; `.bassia.toml`'s `[ignore] paths` adds more. Two paths that differ only by case
  are refused, because Windows cannot hold both.
- **Changes.** `workspace status`, `workspace diff` and Studio list each changed path as `added`, `deleted`,
  `modified`, `renamed`, or `mode` - the same content with only the executable bit changed (git's `100755`/`100644`).
  On Windows, which has no executable bit, a file keeps the mode its tree recorded, so imported executables never show
  as changed.
- **Stacking and cloning instead of branches.** `-on <workspace>` stacks a new workspace on another one's work: it
  lands after it (`check` reports `rejected` until then) and `sync` follows it. `workspace clone` makes a new workspace
  with the same base and work as a new change.
- **Sync.** `workspace sync` rebases the working commit onto the newest base: the parent workspace's work while it is
  open, otherwise mainline's head. Untouched paths cost nothing; a path both sides changed follows its conflict policy,
  and what stays unresolved is stored as a conflict and written into the folder with markers
  (`<<<<<<< workspace` ... `||||||| base` ... `=======` ... `>>>>>>> mainline`). Editing the file resolves it.
- **Undo and restore.** Every operation is in the workspace's log (`workspace log`); `undo` restores the view before
  the last operation and `restore -op <seq|id>` any earlier one (by its seq, or 4+ characters of its id), each as a new operation, and the folder follows.
- **Close.** `workspace close [-abandon] [-remove]` ends a workspace; its operations stay recoverable until
  `gc.retention_days` after it closed.

## Context-specs: what a workspace contains

A workspace need not contain the whole monorepo. Its **context-spec** selects the paths it contains, like a ClearCase
config spec: an agentic run in it sees, reads and changes only those. The spec is stored on the workspace's record, so
every client, Bassia Studio and a replay see the same one. The client fetches the content of those paths only, and
caches it in `.bassia/cache`, shared by all workspaces of the client; it never reads the trees below a directory
nothing of which is in context. Many clients can then each run agents on their own part of a large monorepo, each
downloading only that part.

```sh
bassia workspace create api-fix -context "*,api/,libs/shared/,!api/testdata/"   # top-level files, two projects, minus fixtures
bassia agent start -context @api -prompt "..." -land                            # a profile from .bassia.toml
bassia workspace context api-fix -add web/                                      # widen it later (an operation: undo reverts it)
bassia workspace context api-fix -set @web                                      # replace it
bassia workspace context api-fix                                                # show it, and how many files it contains
```

- **Rules** are paths from the root of the tree. They are tried in order, and the last rule that matches a path
  decides:
  - `api` or `api/` contains that directory and everything below it; a literal path without a slash may also name a
    file.
  - `*`, `*.md`, `docs/*.md` contain the files matching the glob at that depth. `*` stays within one name and `**`
    crosses folders (`**/*.proto`).
  - `src/*/`, with a trailing slash, contains the matching directories whole.
  - `!pattern` excludes what it matches.

  `all`, or no rules, is everything. `.bassia.toml` is always in context. `@name` expands to a profile of
  `.bassia.toml`; the workspace records the expanded rules.
- **Defaults**, from first to last: `-context` on `workspace create|clone` and `agent start|replay`; the source's spec
  for a stacked (`-on`) or cloned workspace and for a replay; mainline's `.bassia.toml` `[context] default`; everything.

  ```toml
  [context]
  default = ["*"]
  [context.profiles]
  api = ["*", "api/", "!api/testdata/"]
  web = ["*", "web/"]
  ```
- **The boundary.** A snapshot that finds a path outside the context created, changed or deleted fails, names the
  paths and snapshots nothing; `status`, `check`, `land` and every other command that snapshots fail the same way, and
  so do Studio's edits. A run whose agent changed such a path fails, and nothing of its last edits is taken. Widen the
  context (`workspace context -add <rule>`) or undo those changes. A run sees its spec in `BASSIA_CONTEXT`.
- **What is outside stays as it is.** The workspace's tree still holds everything: sync and landing carry paths
  outside the context along unchanged. A directory outside the context shows as an empty folder, so an agent sees the
  monorepo's shape; files outside it at in-context levels are not shown.
- **Changing the spec** (`workspace context -set|-add|-remove`, or Studio's workspace page) is an operation of the
  workspace: `workspace log` lists it, `workspace undo` reverts it. It snapshots first; a spec that leaves out a path
  the workspace changed against its base is refused, naming the paths. Every client's folder follows the record: on
  the next command there, files the new spec drops leave the folder and files it adds are written.
- Results report `context` (the rules; `all` for everything) for every workspace, `files_in_folder` when a folder is
  made, and `paths_in_context` from `workspace context`.
- **Load rules are gone.** Context-specs replaced them: `-load`, `workspace load` and the `workspace.load` setting
  fail and name the replacement. A `[load]` table in `.bassia.toml` is no longer read (landings are unaffected); a new
  workspace without `-context` asks to rename it to `[context]`.

### Lazy folders: files fetched when first used

By default a workspace's folder holds every file in its context, fetched before the folder is made. A **lazy** folder
(`-materialize lazy` on `workspace create|clone|checkout` and `agent start|replay`, or the `workspace.materialize`
setting) holds placeholders instead: every file in context is listed with its size and mode, and nothing is fetched
until a file is first read or changed. An agent needs no cooperation - reading a file is enough - so a run over a large
context costs only what it touches.

```sh
bassia workspace create big-fix -context @platform -materialize lazy   # placeholders; the folder is served
cat workspaces/big-fix/platform/src/Main.cs                            # fetched now, and only this file
bassia workspace unmount big-fix                                       # stop serving it (files stay)
bassia workspace mount big-fix                                         # serve it again, e.g. after a restart
```

- **How.** The files and placeholders live in a backing directory (`.bassia/workspaces/<id>.files`), and a virtual
  file system projects it at the workspace's folder, served by a background `bassia workspace serve <w>` process (log:
  `.bassia/workspaces/<id>.mount.log`). `workspace create` and `checkout` start it, `agent start` and every snapshot
  make sure it runs, and `workspace close` stops it (`-remove` also deletes the backing directory).
  - On **Linux and macOS** it is a FUSE file system, a passthrough to the backing directory: a placeholder is hydrated -
    its content fetched through the client's cache - just before it is opened, truncated, renamed, linked, or has its
    mode or times set. Snapshots read the backing directory.
  - On **Windows** it is a ProjFS provider: the folder is a virtualization root, a file's content is hydrated when it
    is first read, and changes stay in the folder, which snapshots read. When a sync, undo or context change rewrites
    the backing directory, the command asks the server to drop those paths from the folder so it shows them anew.

  Listings and sizes never fetch anything.
- **Snapshots** treat an untouched or merely read placeholder as unchanged; edits, new files, renames and deletions
  through the folder are snapshotted as in a full folder, and the context boundary applies the same way.
  `.bassia.toml` is always written in full.
- **Requirements.** Linux: FUSE 3 (`apt install fuse3`, `/dev/fuse`). Windows: the Projected File System feature
  (`Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart`, as an administrator). macOS:
  macFUSE with libfuse 3 (not tested in CI). Where none is available, a lazy folder is refused with the reason, and a
  full one works as always.
- Results of `workspace create|clone|checkout` report `materialize` and, for a lazy folder, `files_placeholders`.

## Workspaces on a Dev Drive (Windows)

On Windows, put the client folder (and with it `workspaces/`) on a [Dev Drive](https://learn.microsoft.com/en-us/windows/dev-drive/)
- or any ReFS volume with block cloning - and workspaces are created by **block cloning** instead of writing files:

- Bassia keeps one copy of each file's content in `.bassia/clones` (read-only, by content id). The first workspace
  that needs a file writes it there once from the object store; every workspace then gets the file as a
  copy-on-write clone (`FSCTL_DUPLICATE_EXTENTS_TO_FILE`): its clusters are shared, no data is read or written.
- Editing a cloned file in a workspace reallocates only the clusters written; the cache and every other workspace
  keep their content. Snapshots, landing and everything else work as on any folder.
- Files under 8 KiB, symlinks and conflicts are written as before. A block clone the volume refuses (for example too many
  references to one cluster) falls back to a copy, and then to writing the file; cloning never makes a command fail.
- `workspace create`, `clone` and `checkout` report `files_cloned` and `files_written`.
- `bassia devdrive status [-path <dir>]` tells whether workspaces in a folder are cloned and why not: the file system,
  whether the volume is a Dev Drive and trusted (Defender's performance mode), whether it supports block cloning, and
  the clone cache's size.
- `workspace.clone = off` turns cloning off. `bassia gc` prunes cache entries that no workspace on this machine and no
  file of mainline's head uses.

Requirements: Windows 11 24H2 or Windows Server 2025 or later (block cloning on Dev Drive), and the workspace folder on
the same volume as the client folder (block clones never cross volumes). Creating a Dev Drive needs an administrator:
**Settings > System > Storage > Advanced storage settings > Disks & volumes > Create dev drive**, or
`Format-Volume -DriveLetter D -DevDrive`. Trusting it (`fsutil devdrv trust D:`) lets Defender scan it in
performance mode.

## Landing on mainline

`workspace check` reports whether a workspace can land now, `workspace land` lands it. The server (or, for a local
monorepo, the landing service in-process) is the only writer of mainline:

1. The workspace is snapshotted; its changes against its base are the **touched paths**.
2. Each touched path is looked up in the path index: if it did not change on mainline after the workspace's base seq,
   the workspace's version is taken. If mainline holds the same content, nothing needs doing; if mainline changed it
   and changed it back, the workspace's version is taken.
3. A path both rewrote follows its **conflict policy** (below).
4. The changes are applied onto mainline's current tree, the commit is written with the workspace's **change id** and a
   new commit id, and `main` advances by compare-and-swap. A landing that loses a race repeats from step 2, which costs
   only the touched paths.

Outcomes: `landed` (with the new seq), `would-land` (check), `nothing` (no changes), `needs-replay`, `conflict`
(the workspace holds unresolved conflicts, which mainline refuses), and `rejected` (stacked on an open workspace,
or leases missing). A refused landing is recorded on the workspace (`last_land`), which `workspace show` and Studio show.

`-strict -reads <prefix>,...` also refuses the landing when anything under the declared read set changed since the
workspace's base - for a coherent rewrite that relied on an API elsewhere. `-keep` keeps the workspace open after it
landed, on the new head with a fresh change; by default it is marked `landed`.

`mainline restore -seq <n>` puts an earlier seq's tree back, as a new landing: history stays linear.

### Pre-reconciled landings and the conflict overview

Deciding a landing - the diff of the workspace, the conflict policies, merge drivers and the validation hook - is
done ahead of time, as the workspace changes, so that landing it costs little more than the compare-and-swap of
`main`:

- **Edits reach the server as snapshots.** Every command that reads a workspace snapshots it, and while an agent runs,
  `bassia agent start` also snapshots its folder whenever the files have been quiet for `agent.snapshot_quiet` seconds
  (default 2; 0 turns it off). Each snapshot moves the workspace head.
- **The server reconciles.** The node that receives a snapshot, or performs a landing, reconciles the workspaces it
  affects in the background: it plans their landing on the current mainline, with the same planner a landing uses,
  and stores the result as a **candidate** - the commits it is for, the mainline seq, the touched paths, the resolved
  changes, conflicts, merged paths and the outcome a landing would have. A candidate is an object behind the head
  `rc/<workspace>`, so it lives wherever heads do (PostgreSQL on a server) and every node sees it. After a landing,
  only the workspaces whose touched paths (or `.bassia.toml`) moved are reconciled again. A node catches up on stale
  candidates when it starts. Clients cannot move or delete `rc/` heads.
- **A landing takes the fast path** when the workspace's candidate is for its current commits, would land, and none of
  its paths nor `.bassia.toml` changed on mainline since: it applies the candidate's changes to mainline's current
  tree, checks leases and advances `main`, with no diff, policies, merges or validation. A machine-merged candidate is
  used only on the very seq it was validated on; a `-strict` landing never uses one. Otherwise the landing plans as
  above. The outcome is the same either way; `reconciled = true` in the result (and the `reconciled` tag of the
  landing metric) says the fast path was taken.
- **The conflict overview** (`GET /api/v1/conflicts`) reads the candidates: every open workspace with what a landing
  would do now, whether its candidate is current, the **overlaps** - paths two or more open workspaces change, so
  whichever lands second will meet the first - and the **hot paths**. Studio's conflict page shows
  it and refresh on the `reconciled` watch event. A local monorepo runs nothing in the background: it reconciles stale
  candidates when the overview is read.

### Conflict policies: `.bassia.toml`

The repository's policy is versioned with the code, in `.bassia.toml` at the root of the tree, and read from mainline
at every landing:

```toml
[conflicts]
default = "replay"                 # replay | textmerge | semantic | conflict

[[conflicts.rule]]
paths = ["docs/**", "*.md"]        # gitignore-like globs; the first matching rule wins
policy = "textmerge"

[[conflicts.rule]]
paths = ["src/**/*.cs"]
policy = "semantic"

[leases]
required = ["src/Billing/"]        # a landing touching these needs a valid exclusive lease

[ignore]
paths = ["dist/", "*.log"]         # never snapshotted (besides the built-in build and VCS folders)

[textmerge]
min_similarity = 0.5               # both sides must keep this share of the base's lines
```

| Policy | When both rewrote a path |
| --- | --- |
| `replay` (default) | The landing is refused as `needs-replay` with the paths and the new head. `bassia agent replay` re-runs the task from the change's recipe on the new head, in a new workspace that keeps the change id. |
| `textmerge` | A line-based three-way merge, only while both sides stay similar to the base and the merge is clean; otherwise as `replay`. For hand-edited docs and configuration. |
| `semantic` | A language-aware merge driver (`merge.semantic_driver`, weave's `weave-driver` by default) merges edits to disjoint entities. At landing the result is machine-merged and lands only after the validation hook (`land.validate` locally, `-validate` on the server) passes; without a hook, as `replay`. `workspace sync` uses the driver too. |
| `conflict` | Stored as a conflict in the workspace by `sync`; mainline refuses conflicts until they are resolved. |

## Leases

A lease holds a path prefix for a workspace: `exclusive` (excludes every overlapping lease) or `intent` (announces
work; intent leases coexist). An exclusive lease on `src/Billing/` also takes intent on `src/` and the root, so
exclusive leases on disjoint prefixes never block each other. `lease acquire -wait <s>` waits in a first-in-first-out
line. Every grant carries a monotonically growing **fencing token**: a landing presenting a token whose lease expired
and was granted again is refused, so a stalled agent cannot overwrite its successor. Leases are advisory unless
`.bassia.toml` lists the prefix under `[leases] required`; then a landing touching it must present a valid exclusive
lease (`workspace land -lease-token <id>:<token>`). `agent start -lease <prefix>,...` acquires leases for a run, renews
them while it runs and presents them when it lands.

## Agentic runs

`bassia agent start -prompt <text>` runs a coding agent (`agent.command`, by default
`claude -p --permission-mode acceptEdits`, with `--model` from `-model` or `agent.model`) in a workspace of its own,
created on mainline (or stacked with `-on`, or an existing one with `-workspace`). `-run <command...>` runs any shell
command instead. Bassia:

1. records the task on the working commit as its **replay recipe** (prompt or command, agent, model) and the prompt
   as its intent;
2. records the run on the workspace (`running`, host, process) and starts the agent in the workspace's folder with
   `BASSIA_RUN_ID`, `BASSIA_WORKSPACE` and `BASSIA_MONOREPO` set, its output going to `.bassia/runs/<run>.log`;
3. snapshots the workspace whenever the agent's files settle (`agent.snapshot_quiet`), so a server reconciles it with
   mainline while the agent works (see "Pre-reconciled landings"); at the end snapshots what the agent changed, stores
   the log with the run, and records how it ended (`succeeded`, `failed`, `stopped`);
4. with `-land`, lands the workspace when the agent succeeded.

`-detach` runs it in the background and returns at once; `agent list|show|logs|wait|stop` follow it from any shell. `agent wait` returns once a `-land` run has also landed, because a run is recorded as done before it lands.
A run whose process is gone without a result shows as `lost`. When the landing reports `needs-replay`,
`bassia agent replay <workspace> [-land]` runs the same recipe on mainline's head in a new workspace with the same
change id, and marks the old workspace `replaced`. Text is never merged behind an agent's back.

## Git import and export

`bassia import -git <url|path>,...` imports git repositories, each under its own top-level folder (its name, or
`-prefix <folder>,...`). Without `-history` each repository becomes one baseline commit of its default branch; with
`-history` its first-parent history (at most `-limit` commits) is imported, the repositories' commits interleaved by
commit time. Every imported commit lands through the normal landing path, from an import workspace, with the git
message as its intent and the git author as its author. Submodules are skipped and listed.

`bassia export -git <dir> [-branch main]` writes mainline into a git repository with `git fast-import`, one git commit
per landing in seq order, each with the intent as its message, the agent as its author and `Change-Id:` and
`Bassia-Seq:` trailers. The last exported seq is kept in the repository's config (`bassia.exportedSeq`), so the next
export continues from there.

## Bassia server

`bassia-server` serves one monorepo to many clients and agents. It keeps heads, the path index and leases in
PostgreSQL, so `-postgres <connection string>` (or `BASSIA_SERVER_POSTGRES`) is required:

```sh
bassia-server -data /srv/bassia -postgres "Host=db;Database=bassia;Username=bassia;Password=..." \
  -url http://0.0.0.0:8080 -public-url https://bassia.example.com -token <shared-token>
bassia-server -data /srv/bassia -postgres "<connection string>" -storage s3 -bucket acme-bassia -region eu-central-1   # blobs on S3
bassia-server gc -data /srv/bassia -postgres "<connection string>" [-grace-hours 24] [-retention-days 14] [-dry-run] [-reconcile]
bassia-server scrub -data /srv/bassia -postgres "<connection string>" [-sample 1000]
bassia-server migrate -postgres "<connection string>" [-check]   # bring the schema to this build's version (or only check it)
```

### Schema versions and migrations

The PostgreSQL schema is versioned per component - `state` (heads, path index, meta, leases, lease queue, fencing
sequence, workspace registry, mainline index, object catalog) and `trees` - in `bassia_schema` (version and `compatible_from` per component) and `bassia_schema_history`,
in the connection's schema. Steps are numbered from 1, run in order, each once, in one transaction with the version
bump; migration 1 is the schema as it was created before versioning. On start the server (and every local monorepo
or clone command) reads the version in one query; only when it is behind does it take a session advisory lock keyed
by the schema name, so nodes starting together wait for one another and the steps run once. Tables made before
versioning are stamped as version 1 when they match what migration 1 creates, and refused, unchanged, when they differ.

`-migrate auto` (the default) migrates on start. For deployments that migrate as a release step, run
`bassia-server migrate` first and start the nodes with `-migrate verify`, which changes nothing and refuses a schema
that is behind. A database newer than the build is used as long as its `compatible_from` is at most the build's
latest version: steps that only add (a table, a column with a default, an index) keep older nodes running through a
rolling deploy, and a step that removes what older builds read raises `compatible_from`, so those builds refuse to start.
A clone's tree cache is rebuilt instead of refused when the build cannot use it. Design: `feat/done/bassia-5eee7287/DESIGN.md`.

### Azure: Blob Storage and Azure Database for PostgreSQL

For several server nodes behind a load balancer, keep objects in Azure Blob Storage and heads, the path index and
leases in Azure Database for PostgreSQL (flexible server):

```sh
bassia-server -data /var/lib/bassia -url http://0.0.0.0:8080 -public-url https://bassia.example.com -token <shared-token> \
  -storage azure -azure-url https://acmebassia.blob.core.windows.net/bassia \
  -postgres "Host=acme-bassia.postgres.database.azure.com;Database=bassia;Username=bassia;Password=...;SSL Mode=Require"
```

- **Blob Storage**: `-azure-url <container url>` authenticates with Microsoft Entra ID (`DefaultAzureCredential`: a
  managed identity on Azure, else Azure CLI or environment credentials); the identity needs *Storage Blob Data
  Contributor* on the container, and *Storage Blob Delegator* on the account to sign SAS URLs with a user delegation
  key. `-azure-connection <string>` uses an account connection string instead (also Azurite's development string),
  and creates the container (`-azure-container`, default `bassia`) if it is missing. Objects are block blobs under
  `-azure-prefix` (default `objects/`), keyed by hash prefix. Chunks of 256 KiB and more move between clients and the
  container directly on SAS URLs valid for 30 minutes; the server never proxies them.
- **PostgreSQL**: `-postgres <connection string>` (PostgreSQL 13 or later), the same as on any server. The tables (`bassia_heads`,
  `bassia_paths`, `bassia_meta`, `bassia_leases`, `bassia_lease_queue`, `bassia_workspaces`, `bassia_workspace_paths`, `bassia_mainline`, `bassia_objects`, sequence `bassia_fencing`) are created and migrated on start
  (see "Schema versions and migrations"); Terraform provisions the server, not the schema.
  Heads move by compare-and-swap, paths are compared in byte order (`COLLATE "C"`), lease grants serialize through a
  transaction-scoped advisory lock and take fencing tokens from the sequence, and the path index is filled in seq
  order from the operation log - so any number of nodes can share one database. Use `SSL Mode=Require` on Azure.
  A landing looks up every path it touched in one statement per 10,000 paths, and `mainline changed` and strict read
  sets read only the history after their seq, and a directory listing skips from child to child: none of them grows
  with the size of the monorepo's history.
- Every switch also comes from its environment variable (`BASSIA_SERVER_STORAGE`, `BASSIA_SERVER_AZURE_URL`,
  `BASSIA_SERVER_AZURE_CONNECTION`, `BASSIA_SERVER_POSTGRES`, ...). `bassia-server gc` and `scrub`
  run against the same backends.

### Client sync: what a client downloads and uploads

A client only ever fetches the objects a command needs, and caches each forever (`.bassia/cache`, shared by every
workspace on the machine: objects never change).

| Operation | What travels |
| --- | --- |
| Status, diff, listing, `check` | Trees, from the cache or fetched level by level; no file content. |
| Checkout, sync, undo | The trees that differ, one batched `/objects/fetch` per tree level; then the content of new or changed files: file objects in one batch, small chunks in batches of up to 500, chunks of 256 KiB and more in parallel straight from Blob Storage (or S3, or the server's signed URLs). A second workspace fetches nothing already cached. |
| Snapshot | One `Have` for every new object (changed files' chunks, rebuilt trees, commit, view, operation), then one batched `/objects/put` for the small ones and direct uploads for large chunks; then the workspace head moves. |
| Landing | One `/land` call; the server compares only the touched paths. |

Replies (JSON, object batches) are compressed (Brotli or gzip) when the client accepts it. Every object read from
the server is checked against its id before it is cached.

The state - heads, path index, leases and trees - lives in PostgreSQL only (local monorepos made by `bassia init` keep
theirs in a local PostgreSQL too); `-state postgres` is still accepted, any other `-state` is refused. The data folder holds the key that signs blob URLs
and, with the default `-storage disk`, the blob container `objects/` - a local-disk stand-in for cloud blob storage,
laid out by hash prefix, so a server and its tests run without a cloud account. Every switch can also come from a `BASSIA_SERVER_<NAME>` environment variable.

**Memory.** What one request can make the server hold is bounded by three switches, each in MiB: `-fetch-budget`
(default 32) - a batched fetch stops adding objects once its reply holds that much, and clients ask again for the
rest; `-put-limit` (default 32) - a larger request body is refused with a 413 before it is read (clients send small
objects in batches of 8 MiB and large chunks on their upload URLs); and `-object-cache` (default 256) - the decoded
objects the server keeps (commits, operations, views, trees, file objects, candidates), by approximate size, the
least recently used evicted first.

`bassia-server` is a Web API only: it serves no pages. Its web UI is Bassia Studio; the API is for Studio, `bassia`
clients and any other client, in the cloud or local. `GET /` answers a short JSON description (`service`, `version`,
`api`, `health`); any other path outside the API is a JSON 404.

The server is closed by default: the API needs one shared bearer token, `Authorization: Bearer <t>` - `-token <t>`, or
without it one the server generates on first start and keeps in `<data>/token` (owner-only on Unix). Its log names the
file, never the token. Clients pass it as `bassia clone -token <t>` (or `server.token`, `BASSIA_SERVER_TOKEN`), Studio
as `-token`. Neither a cookie nor a `?token=` query parameter authenticates. A call without the token is a JSON 401
with `WWW-Authenticate: Bearer`. `-no-auth` (`BASSIA_SERVER_NO_AUTH`) serves everyone, with a warning in the log.
`/healthz`, `/` and signed `/blobs` URLs need no token. The token travels in clear over plain HTTP: beyond one machine,
put TLS in front. People's own identities and roles are Studio's (`-auth entra`).

A browser application on another origin can call the API once the server lists its origin:
`-cors-origin https://app.example.com,http://localhost:5173` (`BASSIA_SERVER_CORS_ORIGIN`). Preflight requests then
pass without the token and replies carry `Access-Control-Allow-Origin` for those origins only; `*` is refused. Without
the switch the server sends no CORS headers (Studio calls it from its own process and needs none).

The RPC surface under `/api/v1` is what a client's remote stores call:

| Call | Purpose |
| --- | --- |
| `GET /main` | GetHead: mainline's head operation, seq, commit and tree. |
| `GET /main/log?from=&count=`, `GET /main/indexed` | The mainline index: the landings at and below a seq, newest first (operation, view, commit, tree, workspace, time), and how many it holds. |
| `GET /watch` | A stream (server-sent events) of landings with the prefixes they touched, of workspaces that moved, and of workspaces reconciled (`reconciled`). |
| `GET /conflicts` | The live conflict overview: every open workspace reconciled with mainline, overlaps between open workspaces, hot paths. |
| `POST /objects/fetch` | GetObjects: many objects in one binary reply (structural objects and chunks under 256 KiB); what it leaves out is read on its direct URL. A reply stops adding objects at its byte budget (`-fetch-budget`) and says in `X-Bassia-Truncated` how many of the last ids it did not look at; the client asks for those again. |
| `POST /objects/put` | Many small objects in one binary request, each checked like a single put. |
| `POST /objects/batch` | Structural objects by id as JSON, optionally with subtrees to a depth. |
| `POST /paths/stat`, `GET /paths/list`, `GET /paths/changed` | StatPaths, listing and ChangedSince from the path index. |
| `POST /objects/have` | Which ids the server already holds; asked before every upload. Answered from the object catalog, and counts as use: what it reports present is not swept by a collection already under way. |
| `POST /objects/read-urls`, `POST /objects/upload-urls` | GetChunkUrls and BeginUpload: signed URLs for direct transfers. |
| `GET/PUT /objects/{id}` | One object; large chunks are redirected to their signed URL, structural objects are validated on upload. |
| `GET /heads...`, `POST /heads/advance` | Workspace heads, by compare-and-swap; `main` is refused (only landing moves it), and so are the server's `rc/` candidate heads. A workspace head must point at an operation of that workspace; the server records it in the workspace registry from the operation it reads itself. |
| `GET /workspaces?open=&limit=&before=`, `GET /workspaces/find?reference=`, `POST /workspaces/get`, `GET /workspaces/stale?seq=` | The workspace registry: open workspaces (oldest first) or closed ones (newest first, in pages), the workspaces a name or prefix may mean, workspaces by id, and the open workspaces not reconciled with mainline at a seq. |
| `POST /leases/acquire`, `renew`, `release`, `GET /leases` | Leases; waiting happens on the server. |
| `POST /land` | Land (or check, with `dry_run`) a workspace. |

Objects are immutable, so a client caches every object it reads in `.bassia/cache`, shared by all its workspaces, and
never asks again. Chunks of 256 KiB and more go between the client and the blob container directly on signed URLs
(S3 presigned URLs, or the server's own `/blobs/{id}?op=...&sig=...` for the disk container), so the server never
proxies file bytes. When `OTEL_EXPORTER_OTLP_ENDPOINT` is set the server exports OpenTelemetry metrics (landings by
outcome, objects stored, bytes served, redirected reads).

## Bassia Studio (web client)

```sh
bassia-studio -server http://127.0.0.1:8080 [-token <t>] [-url http://127.0.0.1:5280] [-cache <folder>] [-label <text>]
```

`bassia-studio` (project `Bassia.Studio`, ASP.NET Core Blazor Server) is the read-write web client of a `bassia-server`,
in the role of a desktop version-control client such as TortoiseSVN or P4V, but deployed in the cloud next to the
server. It talks to the server as a clone does: the version core over the remote stores, with a local cache of the
immutable objects it reads. Switches can also come from `BASSIA_STUDIO_*` environment variables. Pages are rendered on
the server and then become interactive over a SignalR circuit.

Delivered in slices (feature record `bassia-681e8902`):

- **Overview**: open workspaces and the latest landings.
- **Depot browser**: mainline's tree at the latest or any seq, a file's content (download at `/raw?path=<p>[&seq=<n>]`)
  and its **history**: the landings that changed it, from the path index.
- **Mainline log**: the landings by seq, paged; a landing's commit, change id, intent, recipe and its diff, **unified
  or side by side**.
- **Workspaces**: open (or all) workspaces; **new workspace** on mainline, stacked on one, or cloned from one. A
  workspace's page shows its **context-spec** (with **Change**) and has the actions **Check** (dry-run landing),
  **Land**, **Sync**, **Undo**, **Describe**, **Close** and **Abandon**, the landing outcome with conflicting paths and policies, its stored conflicts (keep the workspace's side,
  take mainline's, or edit the merged text with markers), its changes and its operation log with **Restore**.
- **Files** of a workspace: browse its working tree, edit text in the browser, add, rename, delete and download files.
  Every save is a snapshot (an operation, so undo works); a save is refused, with the text kept, when the file changed
  since it was opened, or when the path is outside the workspace's context-spec.
- **Conflicts**: from the server's live conflict overview, every open workspace that cannot land now and why, the open
  workspaces that overlap on a path, and the hot paths; it refreshes as the server reconciles.
- **Leases**: live leases; acquire (exclusive or intent) for a workspace, release.

- **Agentic runs**: every run (those started here and those recorded on workspaces by the CLI); **New run** with a
  prompt or a command, in a new workspace (on mainline or stacked) or an existing one, with leases, a context-spec and
  land-when-done; a run's page streams its **output live** and offers **Stop**, **Land**, **Replay on mainline** (the
  recorded task again on the newest mainline, same change) and **Discard**.

Studio changes the monorepo through the same core operations as the command line, so the same edit gives the same
tree. Until sign-in is configured it acts as one user with one role: `-user <name>` (owner and agent of its changes;
default `studio`) and `-role reader|contributor|admin` (default `admin`). Readers only look; contributors change their
own workspaces and leases; admins anyone's.

For development, `-exit-button true` (or `BASSIA_STUDIO_EXIT_BUTTON=true`) adds an Exit button to the toolbar, shown to admins only, that stops Studio like Ctrl+C in the console. It is off by default.

An unreachable server or a missing path is shown as a message on the page.

Pages that show the monorepo's state (overview, log, workspaces, a workspace, conflicts, leases) refresh by
themselves: Studio follows the server's change stream (landings and workspace changes), and refreshes every 15 seconds
for what the stream does not carry (leases).

### Sign-in

`-auth none` (default) acts as one configured person (`-user`, `-role`). `-auth entra` signs people in with Microsoft
Entra ID (OpenID Connect, authorization code flow with PKCE, into a cookie session): `-entra-tenant <tenant id>`,
`-entra-client-id <app id>` and `-entra-client-secret` (better from `BASSIA_STUDIO_ENTRA_CLIENT_SECRET`); the app
registration's redirect URI is `<studio url>/signin-oidc`. A person is named by their `preferred_username` and gets the
strongest of the app roles `Reader`, `Contributor` and `Admin` they hold; signed in without one, they are a reader.
`/healthz` and the job reports (`/runs/report/<id>`, with each run's own token) need no sign-in.

### Deployment in Azure

`deploy/azure` is the reference deployment: bassia-server and Studio as Azure Container Apps, agentic runs as a
Container Apps job, Blob Storage, Azure Database for PostgreSQL, Key Vault, Container Registry, managed identities and
Studio's Entra ID app registration, in Terraform, deployed with `deploy/azure/deploy.sh` (see `deploy/azure/README.md`).
The images are `deploy/docker/{server,studio,worker}.Dockerfile`; `deploy/docker/smoke.sh` runs all three on one machine.

### Runs from Studio

`-run-host` decides where runs execute: `none` (default; runs are off), `local` (a process per run on Studio's machine:
`-bassia "<command line that starts bassia>"`, `-runs <folder>`) or `aca` (an Azure Container Apps job execution per run:
`-aca-job <job resource id>`, `-aca-image <worker image>`, `-aca-secrets NAME=secret,...`, default
`BASSIA_JOB_TOKEN=server-token,ANTHROPIC_API_KEY=anthropic-api-key`; Studio signs in to Azure with its managed identity).
`-public-url` is where jobs reach Studio and `-job-server` where they reach the server, when those differ from Studio's
own addresses.

Every job runs `bassia agent job`: it clones the server (`BASSIA_JOB_SERVER`, `BASSIA_JOB_TOKEN`) into a fresh folder and
runs the `agent start` or `agent replay` command in `BASSIA_JOB_ARGS` (a JSON array) as `BASSIA_USER`, so the run's
workspace, recipe, run record, leases and landing are the CLI's. With `BASSIA_REPORT_URL` and `BASSIA_REPORT_TOKEN` set,
`bassia agent start` and `replay` post their output there every half second and their final status at the end; the
answer can ask the run to stop, which then ends as `bassia agent stop` would. A job that does not stop within 30 seconds
is killed.

## Configuration

Settings of a client live in `.bassia/config.toml` (`config list|get|set|unset`); an environment variable,
where a setting has one, wins over the file. Repository policy is not a setting: it is versioned in `.bassia.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `user.id` (`BASSIA_USER`) | the login name | Who acts: the agent of operations, the owner of workspaces. |
| `agent.command` | `claude -p --permission-mode acceptEdits` | The agent a run starts; the prompt is appended as one quoted argument. To let Claude Code also run builds and tests, allow them and keep `-p` last, since `--allowedTools` takes a list that would swallow the prompt: `claude --permission-mode acceptEdits --allowedTools "Bash(dotnet:*)" -p`. |
| `agent.model` | | Passed to the agent as `--model`. |
| `agent.snapshot_quiet` | `2` | While an agent runs, snapshot its workspace once its files have been quiet this many seconds, so the server reconciles it before it lands; `0` turns it off. |
| `server.url` | | The server a client talks to (set by `bassia clone`). |
| `server.token` (`BASSIA_SERVER_TOKEN`) | | The server's shared token. |
| `merge.semantic_driver` | `weave-driver` | The driver of the semantic policy, git merge-driver style (`%O %A %B`); `off` disables it. The release packages bundle it. |
| `land.validate` | | A shell command a machine-merged tree must pass before it lands (local monorepo). |
| `workspace.clone` | `auto` | On Windows, block-clone workspace files from `.bassia/clones` on a Dev Drive (or another block-cloning ReFS volume); `off` always writes them. |
| `workspace.materialize` | `full` | How a new workspace folder gets its files: `full` (every file in its context written) or `lazy` (placeholders hydrated when first read or changed, by FUSE or ProjFS). `-materialize` overrides it. |
| `workspace.dir` | `workspaces` | Where new workspaces' folders go. |
| `lease.ttl` | `600` | Seconds a lease lives unless renewed. |
| `gc.grace_hours` | `24` | Unreferenced blobs younger than this are kept (uploads for landings in flight). |
| `gc.retention_days` | `14` | How long a closed workspace's operations are kept. |

## Garbage collection and scrubbing

Rewrites create garbage quickly. `bassia gc` (local monorepo) and `bassia-server gc` mark every object reachable from
the retained operations - mainline's whole log (the audit trail; taken from the mainline index, without reading the log), every workspace head, and heads named `pin/...` - and
the candidates of open workspaces (`rc/...`, whose merge results exist nowhere else; those of closed workspaces are
dropped), and sweep the blobs nothing reaches that are older than the grace period. A closed workspace's head is pruned
`gc.retention_days` after it closed, which makes its unlanded snapshots collectable. On a server the sweep goes through the
object catalog (below): in batches, each object deleted only if it was not put or asked about since the collection's
cutoff, so an old object a client's `Have` just found - and that its next snapshot will name - is left alone. Trees
are deleted under the same condition. Workspace name reservations
(`name/...`) are not roots; those whose workspace is closed are dropped. `scrub` re-hashes stored blobs (all,
or a `-sample`) and reports any whose bytes do not match their id.

## Feedback and crash reports

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

Bassia has five projects; each depends only on the ones before it.

1. **`Bassia.Core`** - the version core. It may not reference an HTTP client, a cloud SDK or a database driver (a test
   checks its references).
   - `Model`: the objects - chunks (FastCDC, 256 KiB / 1 MiB / 4 MiB), `FileObject`, `Tree` (sorted entries with kind,
     id, size and mode), `ConflictObject` (bases and sides), `Commit` (tree, parents, change id, author, intent, replay
     recipe), `Operation`, `View` and `Candidate` (a workspace reconciled with mainline) - with ids as BLAKE3-256 hashes of
     canonical CBOR (`ObjectCodec`).
   - `Storage`: the traits - `IObjectStore` (immutable objects: has, stat, ranged read, put, begin upload),
     `IViewStore` (heads moved by compare-and-swap), `IPathIndex` (mainline's path versions by seq), `ILeaseStore`
     (leases with fencing tokens) and `IWorkspaceIndex` (the workspace registry, below) - with in-memory implementations, the local-disk blob store (`DiskObjectStore`) and
     decorators (caching, counting, verifying).
   - `Version`: `Repository` (mainline, workspace heads, operation logs, path index catch-up), `Trees` (lookup, edit,
     diff and three-way merge, all skipping equal subtrees), `Diffs` (renames, rewrites, hunks), `WorkspaceService`
     (create, stack, clone, amend, sync, undo, restore, close), `LandingService` (the only writer of `main`, with the
     fast path for reconciled candidates), `LandingPlanner` (the decision of a landing, shared by the landing and the
     reconciler), `Reconciler` (candidates behind `rc/` heads and the conflict overview), `RepoPolicy` and
     `ConflictResolver` (the conflict policies), `GarbageCollector`.
   - `Workspaces`: `IWorkspaceProvider` - the plain-checkout `CheckoutProvider`, which writes files in full or, for a
     lazy folder, as placeholders that `Placeholders` hydrates on first use (behind `ILazyFolders`, the virtual file
     system the frontend implements), with an optional `IFileMaterializer` that creates files more cheaply than writing
     them - its tree state, `ContextSpec` (a workspace's context-spec, stored on its record, and the boundary its
     snapshots enforce), and `WorkspaceClient`, which snapshots and keeps folders in step with the record's tree and
     context.
2. **`Bassia`** - the CLI: the commands (`Cli`, `CliCommands`), lazy folders (`Lazy`: `FuseServer`, a libfuse 3
   passthrough over the backing directory that hydrates placeholders, and `ProjFsServer`, a ProjFS provider over it
   (the `Microsoft.Windows.ProjFS` package), each served by `workspace serve`), the store implementations that need more than the core
   may use (`Stores`: `PostgresState` (`bassia-server` and local monorepos) for heads, path index and leases and `PostgresTreeStore` for trees; `S3ObjectStore` and
   `AzureBlobObjectStore`; the remote stores and landing of a client over HTTP, with batched reads and writes), `Monorepo` (a local monorepo or a client, giving every command the same core), agentic runs
   (`Agents`), git interop and the merge driver (`Interop`), Windows block cloning and the clone cache for Dev Drive
   (`Platform`).
3. **`Bassia.Server`** - `bassia-server`: the same core over the server's stores, the Web API (the RPC surface),
   signed blob URLs and the watch stream.
4. **`Bassia.Tests`** - unit tests of the core (property tests over the tree algebra, landing, leases, workspaces on
   disk, garbage collection), the store contract over memory and PostgreSQL, the CLI in-process on local monorepos, the
   server in-process with clients over HTTP (on PostgreSQL), and git interop.
5. **`Bassia.LoadTest`** - `bassia-loadtest`, the load harness: it hosts a `bassia-server`, seeds a synthetic monorepo
   and measures what each operation costs under many agents (see "Load harness").


### Object catalog

Every object is a blob, and a blob store answers "do you have this?" one object at a time. On a server the **object
catalog** (`IObjectCatalog`; `bassia_objects`: id, size, state, last use) answers instead: `CatalogedObjectStore` sits
over the blob store, so `Have` is one statement for any number of ids and no blob call, a put is the blob and one
statement for the batch, and garbage collection lists the catalog, not the bucket.

The catalog also orders a collection's deletes against uploads of the same object. A row is *pending* (a direct
upload was announced; the first `Have` asks the blob store whether it arrived), *stored*, or *doomed* (a collection
chose it). `Have` and a put touch a stored row; a collection dooms only rows not touched since its cutoff, deletes
their blobs and then their rows, under a lock; a doomed object is not vouched for, and an upload of it waits for that
lock and writes the blob again. So an object reported present is never deleted by a collection that had already
marked.

The catalog is derived: the blobs are the truth. A new server's catalog covers its store from the first object. For
a store from before the catalog, ids it does not know are asked of the blob store and remembered, and
`bassia-server gc` reconciles it once, by one listing of the bucket (`-reconcile` forces that again, after a restore).
Local monorepos keep no catalog: their blob store is a local folder.

### Mainline index

Mainline is a chain of operations, each pointing at the one before; only its head has a name. The **mainline index**
keeps, for every seq, the landing itself - operation, view, commit, tree, the workspace that landed, the time and the
task spec its commit names - in `bassia_mainline`, written by `IPathIndex.ApplyAsync` in the same transaction as that
seq's path changes, so the two cover the same seqs. Mainline at a past seq (`mainline ... -seq`, Studio's tree, log
and path history at a seq) is then one lookup and the three objects it names, whatever the distance from the head, and
garbage collection marks all of mainline from one query instead of reading every operation, view and commit of the
log.

The index is derived: it is rebuilt from the operation log. Seqs landed before it existed (and seq 0, which has no
path changes) are recorded by a backfill that walks the log once from the lowest seq held down to 0, in batches, and
resumes where it stopped - in the background when a server starts, and when a local monorepo is opened. Until it
reaches a seq, and against a server or store that keeps no index, mainline at that seq is found by walking back from
the head, as before; garbage collection uses the index only when it holds every seq.

### Workspace registry

A workspace is a head `ws/<id>` pointing at an operation whose view holds its record; a reconciled candidate is a head
`rc/<id>`. The **workspace registry** (`IWorkspaceIndex`) is what answers questions about workspaces without reading
the operation behind every head: for each workspace head a row with what its record says (name, status, owner, base
seq, created, closed, its commits), and for each candidate the commits and mainline seq it was computed for and the
paths its workspace touches. It is derived, like the path index: dropped, it is rebuilt from the heads
(`Repository.SyncWorkspaceIndexAsync`).

The registry moves the workspace and candidate heads itself, so a head and its row change together - on PostgreSQL
(`bassia_workspaces`, `bassia_workspace_paths`) in one transaction with the compare-and-swap. A workspace that closes
loses its touched paths. A server records, at start, any head the registry does not describe as it is now (a database
from before the registry, or a head a node of an older build moved during a rolling deploy) and drops rows whose head
is gone; a local monorepo does so once, when it finds workspaces and an empty registry.

| Question | Cost |
| --- | --- |
| Open workspaces (`workspace list`, status, Studio) | one query for the open ones, then their operations and views in one batch each |
| Closed workspaces | a page of 100, newest first (`workspace list -all [-limit <n>]`, Studio's "Older") |
| A workspace by name or prefix; whether a name is free | one query, by name or over the open workspaces |
| The workspaces a landing made stale; the conflict overview | one query: open workspaces without a current candidate, or with a touched path (or `.bassia.toml`) that changed on mainline after their candidate's seq |

None of these grows with the closed workspaces kept (14 days by default). A clone asks the server
(`RemoteWorkspaceIndex`, the `/workspaces` calls). A store without a registry - an older client's remote stores, the
core over plain in-memory stores - falls back to `ScanningWorkspaceIndex`, which lists the heads and reads every
operation, as every question did before.

### Trees

Chunks, file objects, conflicts, commits, operations and views are blobs. **Trees are not**: a tree is stored as rows in
PostgreSQL (`ITreeStore`; `PostgresTreeStore` keeps `bassia_trees` and `bassia_tree_entries`, keyed by tree and entry
name with the entry's position, so a tree is rebuilt in canonical order). Ids do not change - a tree's id is still the
BLAKE3 hash of its canonical CBOR encoding, recomputed from the rows by `scrub` - so git import and export, clones and
history are unaffected. What this buys is traversal without a blob `GET` per directory:

| Operation | Cost |
| --- | --- |
| Path lookup at any depth (`Trees.GetAsync`) | one recursive query (`GetPathAsync`) |
| Walk of a subtree (`WalkAsync`, checkout) | one recursive query (`GetSubtreesAsync`, at most 20 000 trees), then the cache |
| Diff of two trees | one query per level for the levels that differ (equal subtrees pruned by id) |
| Garbage collection | trees are marked by one recursive query over all roots (`ReachAsync`); unreachable rows older than the grace period are deleted |

A clone keeps its own trees in its schema of the local PostgreSQL when one is configured (`-postgres`, `BASSIA_POSTGRES`), else in memory for the length of each command - what a job container does (`CachingTreeStore` over `RemoteTreeStore`): the
`/trees/{have,fetch,subtree,path,put}` calls return whole subtrees as canonical encodings, each verified against its id,
and only the subtrees the clone's work touched are kept - never the whole monorepo.

Trees are PostgreSQL-only: there is no blob mode and no migration of earlier data. Bassia is for new monorepos - a
server and a local monorepo start on PostgreSQL. Local monorepos need a PostgreSQL: `bassia init -postgres "<connection
string>"` (or `BASSIA_POSTGRES`, or the `postgres.connection` setting); each monorepo folder gets a schema of its own named
in `.bassia/repo.toml`.


### Formal model of landing

`proofs/` holds a [Lean 4](https://lean-lang.org/) model of the storage contract's landing operations - publishing a
working copy (`IComponentWorkspace.PublishAsync`, fast-forward only) and advancing an integration
(`IComponentStorage.MoveBranchesAsync`, compare-and-swap, all or none) - with machine-checked proofs that landing puts
exactly the committed tree at the branch's tip, never changes or loses a commit the store held, keeps everything the
old tip reached reachable, and, without merge commits, makes the branch's history the new trees followed by every
earlier one. `proofs/README.md` lists the theorems and what the model leaves out; CI checks them on every change under
`proofs/`.

## Build

```sh
dotnet build
dotnet run --project Bassia -- help                   # the CLI
dotnet run --project Bassia.Server -- -data ./data    # the server
```

### Packages

CI (`.github/workflows/ci.yml`) builds an installable package per platform (`win-x64`, `linux-x64`, `linux-arm64`,
`osx-x64`, `osx-arm64`) on every push and pull request: the CLI published framework-dependent, weave's `weave-driver`
pinned by checksum in `packaging/weave.toml`, `install.sh` or `install.ps1`/`install.cmd`, and the version manager. It
installs each package on its own platform with `packaging/Test-BassiaPackage.ps1` (the installer refuses a machine
without the .NET runtimes, installs, and the installed `bassia` imports a git repository, lands an agentic run and
merges a semantic conflict with the bundled weave), uploads the packages as artifacts, and publishes them as a GitHub
release when a `v<semver>` tag is pushed. Build one locally with `./packaging/New-BassiaPackage.ps1 -Rid <rid>`; see
[packaging/README.md](../packaging/README.md).

## Test

```sh
dotnet test
```

Everything runs without a network or a cloud account; the tests that start a server need a PostgreSQL. `Bassia.Tests/Core` tests the version core on the in-memory
stores: encoding and ids, chunking, property tests over the tree algebra (diff then apply reproduces the tree, merge
identities, equal-subtree pruning), every landing case and conflict policy, strict mode, stacked workspaces, sync,
undo and restore, the path index and its rebuild from the operation log, leases and fencing, a swarm of concurrent
agents landing through several landing services, a replay landing with the original change id, garbage collection
and scrubbing, and workspaces on disk (snapshots that hash only what changed, ignore rules, case collisions,
materialized conflicts, undo, and status reading no file bytes). `StoreContractTests` and `PostgresStateTests` hold the
in-memory and PostgreSQL stores to the same contract, and `TreeStoreTests` the tree stores (memory, blobs, PostgreSQL). `SchemaMigrationTests` covers schema versioning: a fresh schema, baselining, later steps, concurrent starters, a database newer than the build and the clone's cache. `ObjectCatalogTests` holds the object catalog to its contract in memory and on PostgreSQL, counts that `Have` behind it asks no blob store, reconciles a store from before it, and interleaves a client's snapshot between a collection's mark and its sweep - the object the client was told is there survives, as does a tree asked about; `ObjectCatalogSweepTests` uploads an object while it is being swept. `ObjectCacheTests` and `ServerMemoryTests` cover the bounds on the server's memory: the cache of decoded objects staying within its size and keeping what is used, a batched fetch that stops at its budget with the client asking for the rest (and a client that does not know of the budget), and a body over the limit refused with a 413. `MainlineIndexTests` holds the mainline index to its contract in memory and on PostgreSQL, counts that mainline at a past seq reads the same objects after 8 and after 120 landings, backfills and resumes a backfill, compares what garbage collection marks from the index with what it marks from the log, and lands from several services at once without leaving a seq out. `WorkspaceIndexTests` holds the workspace registry to its contract in memory and on PostgreSQL, compares a repository on it with one that reads every head, rebuilds it from the heads (several starters at once), and counts that finding workspaces costs the same with none and with sixty closed ones; `WorkspaceRegistryServerTests` runs a clone and an older client that lists heads against a server. `CliTests` drives the command line in-process on local monorepos. `ServerTests` starts
`bassia-server` in the test process over the local-disk blob storage and PostgreSQL and runs clients and raw HTTP clients against it: the
command line across two clients, a swarm of agents landing overlapping trees over HTTP, laziness of status against
the server, object validation, mainline's protection and signed blob URLs, background reconciliation and the conflict
overview over the API. `ServerAuthTests` covers the token, that the server serves no pages, and CORS. `ReconcilerTests` covers candidates, the landing's fast path (and when it must not
be taken), validation kept out of the landing, the overview and garbage collection of candidates; `AutoSnapshotTests`
the snapshots taken while an agent works.
Every test that starts a server (`ServerTests`, the Studio tests, `SyncEfficiencyTests` and the Azurite integration
tests) needs PostgreSQL and is reported as skipped without `BASSIA_TEST_POSTGRES`.
`PostgresStateTests`, `ObjectStoreContractTests` and `SyncEfficiencyTests` run against PostgreSQL
(`BASSIA_TEST_POSTGRES`, a connection string; every test gets its own schema) and Azure Blob Storage
(`BASSIA_TEST_AZURE_BLOB`, a connection string to Azurite; every test gets its own container) - CI provides both,
locally `/bassia-local-cloud` does (below), and
without them those tests check nothing. `SyncEfficiencyTests` counts the requests a client sends for a checkout, a
second checkout and a snapshot, on the disk backend and on Azure + PostgreSQL, and runs two clients end to end on the
latter.
`AzuriteIntegrationTests` (`Category=Azurite`) runs the whole Azure path in one scenario. It starts a server on Azurite
in a fresh container, with its state in PostgreSQL. One client imports
`BASSIA_TEST_GIT_URL` (default `https://github.com/jk09/example.git`), and the test compares the imported files byte for
byte with a git clone. Three clients then start eight agentic runs at once, each a separate `bassia agent start -land`
process in a workspace of its own. The agents are a scripted stand-in that waits at a barrier until all have started,
so the runs provably overlap. Six add their own files and land. Two append to the same README: one lands, the other is
refused as `needs-replay` and lands after `agent replay` with its change id. The test then checks mainline's log, a
fresh workspace, the blobs in the container and a scrub. Without Azurite it checks nothing, unless
`BASSIA_TEST_REQUIRE_AZURITE` is set, as in CI, where it runs as its own step of the `Test` job.
`jk09/example` is private, so CI imports it from GitHub only with the repository secret `BASSIA_EXAMPLE_REPO_TOKEN`
(a token that can read it). Without the secret, `scripts/Use-ExampleRepository.ps1` imports a local stand-in with the
same files and adds a warning to the run.
`NewMonorepoTests` (needs PostgreSQL) is a developer's first session on a local monorepo. It runs `bassia init`, then
one landing lays out three projects with a context profile each, `textmerge` for Markdown and `replay` for code. Seven
agents then start at once with `agent start -land -detach`, each its own process and workspace, and `agent wait`
follows them. A barrier proves the runs overlap. The project agents contain only their project and land; two edits to the
same source file end in a landing and a replay with the original change id; two edits to the README are text-merged.
The agent's arguments carry quotes, `$` and a backslash, which must reach it unchanged.
`GitInteropTests` imports several repositories with interleaved history and exports mainline back, incrementally.
`CloneCacheTests` covers the clone cache with a stand-in cloner on every platform (clones, fallbacks, the setting,
pruning, status); `DevDriveTests` clone for real on the volume named by `BASSIA_TEST_DEVDRIVE_DIR` - the
`Dev Drive (Windows)` CI job formats a VHD as a Dev Drive (plain ReFS where it cannot) for them; without the variable
they check nothing.

### Load harness

`Bassia.LoadTest` (`bassia-loadtest`) measures how `bassia-server` behaves at a chosen scale. It is not part of
`dotnet test`: CI runs only its smoke scenario, which fails when the harness breaks and never on timing. The `Load`
workflow runs any scenario on demand and keeps the report as an artifact.

```sh
dotnet run --project Bassia.LoadTest -c Release -- run                                  # the small scenario
dotnet run --project Bassia.LoadTest -c Release -- run -scenario medium -agents 100     # a scenario, with a parameter changed
dotnet run --project Bassia.LoadTest -c Release -- sweep -closed 100,1000,10000         # one parameter at several values
dotnet run --project Bassia.LoadTest -c Release -- smoke                                # seconds; what CI runs
dotnet run --project Bassia.LoadTest -c Release -- compare before.toml after.toml       # what got slower or cheaper
```

A run has three parts. **Seeding** builds the state through the version core alone (trees, commits and landings written
to the stores, no working folders): the monorepo's files as seq 1, then the history, one landing each, the closed
workspaces that history leaves behind (the rest abandoned) and open workspaces with unlanded work. **The agents** then
work in rounds against the server over HTTP, through the same remote stores a clone uses: in a round every agent
creates a workspace, takes an intent lease and snapshots, all at once; then all land - agents with paths of their own
at once, agents that meet on a shared file one after another in the order of their numbers, so the first lands and the
others are refused, abandon their workspace and replay on a new one. Odd-numbered agents sync before they land. **The
read operations** follow on the state that leaves, each on a client that has cached nothing (a job container that just
started): listing open workspaces, resolving one by name, the conflict overview, mainline at seq 1, a log page in the
middle of history and at the head, and - in a hosted server - the scan for stale workspaces the server makes after
every landing, the reconciliation a node does at start, and a dry run of garbage collection.

| Parameter | `small` | `medium` | `large` | Meaning |
| --- | ---: | ---: | ---: | --- |
| `-files` | 5,000 | 100,000 | 1,000,000 | Files in the seeded monorepo. |
| `-fanout` | 50 | 100 | 100 | Entries per directory (files in a leaf directory, directories above). |
| `-history` | 200 | 2,000 | 10,000 | Landings on mainline before measuring. |
| `-closed` | 200 | 4,000 | 20,000 | Closed workspaces left behind. |
| `-open` | 20 | 100 | 500 | Open, idle workspaces with unlanded work. |
| `-agents` | 8 | 50 | 500 | Agents working at once. |
| `-rounds` | 2 | 2 | 2 | Operations per agent: rounds of create, snapshot, land. |
| `-snapshots` | 2 | 2 | 2 | Snapshots per round. |
| `-changes` | 3 | 3 | 3 | Files changed per snapshot. |
| `-overlap` | 0.25 | 0.25 | 0.25 | Share of agents that also change a file other agents change. |
| `-probes` | 3 | 3 | 3 | Repetitions of each read operation. |
| `-seed` | 1 | 1 | 1 | Everything random follows from it. |

The same parameters and seed give the same tree, the same changes and the same order of landings on shared files:
two runs end on the same mainline tree (`tree` in the report). Commit and operation ids differ (they carry the time).

By default the harness **hosts the server** itself: local-disk blobs in a temporary folder and a schema of its own in
the PostgreSQL of `-postgres` (default `BASSIA_TEST_POSTGRES`), both dropped afterwards. Each of the server's two
stores holds at most 40 connections, so a default PostgreSQL is enough. `-data <folder>` keeps the seeded state (the
blobs, and the name of its schema) and a later run with the same folder and state parameters continues on it without
seeding. `-server <url> -token <t>` measures a server that is already running instead; it must be empty, or `-no-seed`
runs the agents on what is there. `-no-gc` skips the garbage-collection dry run.

The report goes to `-out` (default `loadtest-reports/`) as `<name>.toml` and `<name>.md`, and the Markdown to standard
output. Per operation it gives the count, the throughput while it ran, latency at p50, p95, p99 and the maximum
(measured at the client, from request to reply), and what one execution asked of the server on average: HTTP
**requests**, PostgreSQL **statements** and **blob calls**. Statements and blob calls come only from a hosted server,
which counts them per request (every Npgsql command; every object asked about, read or written in the blob store, as
a cloud blob store would be called) and returns them in the `X-Bassia-Statements` and `X-Bassia-Blob-Calls` response
headers. Work no request asked for - the server's background reconciliation - is reported per landing. `reconciled
after a snapshot` is the time from a snapshot to the `reconciled` event on the watch stream. Landings are listed by
outcome.

Read times with care and counts with confidence: times depend on the machine, counts do not. The counts of the read
operations are the same in every run; those of the agents' operations vary a little with timing (a landing takes the
fast path only when the server reconciled the workspace first).

A **sweep** runs the scenario once per value, each on a fresh state, and reports how each operation changed from the
smallest value to the largest. When the parameter sizes the state (`-files`, `-fanout`, `-history`, `-closed`, `-open`)
the work asked for is the same at every value, so an operation is marked **grows** when its statements, blob calls or
requests rose by half or its median latency tripled: it costs more only because the state is larger. The first value runs
in a process that has compiled nothing yet, so its times are high and time ratios below 1 mean nothing. `compare` lists
the operations whose p50, p95, requests, statements or blob calls differ between two reports by more than
`-threshold` percent (default 20), for a run or value by value for two sweeps.

### The cloud backend on this machine: Azurite and PostgreSQL

`scripts/bassia-local-cloud.ps1` (PowerShell 7; the `/bassia-local-cloud` skill drives it) emulates the Azure backend
locally, so the cloud tests and a server on it run without an Azure account:

```sh
pwsh scripts/bassia-local-cloud.ps1 check     # Docker? Node.js and PostgreSQL? git, .NET? which mode up would use
pwsh scripts/bassia-local-cloud.ps1 up        # -Mode auto|docker|native, -BlobPort 10000, -PostgresPort 55432
pwsh scripts/bassia-local-cloud.ps1 smoke     # server + two clients + git import, landing, git export; -Keep, -Bassia/-Server <exe>
pwsh scripts/bassia-local-cloud.ps1 test      # dotnet test with BASSIA_TEST_*; -Filter cloud for the cloud tests only
pwsh scripts/bassia-local-cloud.ps1 env       # the connection strings as BASSIA_TEST_* and BASSIA_SERVER_*; -Shell pwsh|sh|cmd
pwsh scripts/bassia-local-cloud.ps1 down      # stop; -Purge also deletes the data
```

- `docker` mode runs the containers `bassia-azurite` (`mcr.microsoft.com/azure-storage/azurite:3.37.0`, blob service
  only) and `bassia-postgres` (`postgres:16`), with named volumes. `native` mode installs Azurite 3.37.0 from npm into
  the state folder and runs it under Node.js, detached. It also runs the installed PostgreSQL server: `initdb` once,
  then `pg_ctl`; as root on Linux it runs as the `postgres` user, with its data under `/var/tmp/bassia-local-cloud`.
  `auto` picks Docker when its daemon answers.
- Both listen on 127.0.0.1 only. The state (mode, ports, data folders) is kept in `<root>/local-cloud`, `<root>` being
  the install root (`$BASSIA_HOME`, `%LOCALAPPDATA%\Bassia`, `~/.local/share/bassia`), so it outlives checkouts of this repository and never
  shows in `git status`. `up` is idempotent.
- `smoke` builds `bassia` and `bassia-server` (or takes `-Bassia`/`-Server` executables, e.g. an installed version).
  It then starts the server on Azurite + PostgreSQL in a fresh container and database. Two clients import two git
  repositories with interleaved history, land a change and see each other's landings, then export mainline to git
  twice, the second time incrementally. It checks the git history and files, the heads and path index in PostgreSQL,
  and every object in Azurite (`bassia-server scrub`). It exits 1 on the first failed check, keeping the folder and the
  database for inspection.
- `test` refuses to run while a service is down, since the cloud tests check nothing without them. The `Local cloud`
  CI jobs run `up`, `smoke`, `test -Filter cloud` and `down -Purge` in Docker on Linux and natively on Windows.

#### A lasting server on the emulators, initialized from git repositories

The same script runs a `bassia-server` on Azurite and PostgreSQL that keeps running, and imports git repositories
into it through a client (the `/bassia-cloud-server` skill drives it):

```sh
pwsh scripts/bassia-local-cloud.ps1 serve          # -Port 8080 -Database bassia -Container bassia [-Token <t>] [-Data <dir>]
pwsh scripts/bassia-local-cloud.ps1 import -Git https://github.com/acme/api.git,https://github.com/acme/web.git -History -Limit 200
pwsh scripts/bassia-local-cloud.ps1 server-status  # URL, mainline seq, data, database, container, clone; exit 1 when down
pwsh scripts/bassia-local-cloud.ps1 server-stop    # serve or import starts it again, with its data
pwsh scripts/bassia-local-cloud.ps1 server-reset   # deletes its data, database contents, blobs and the client
```

- `serve` publishes this checkout's `bassia-server` (Release) into `<state>/server-bin` (or runs `-Server <exe>`). It
  creates the PostgreSQL database and starts the server detached on the Azurite container, so the server outlives
  the script and its terminal. It is idempotent; the server's settings are kept in `<state>/server.json`.
- `import` starts the server if needed, makes the client (default `~/bassia-monorepo`, or `-Clone <dir>`) or reuses
  it, and runs `bassia import` through it. It uses the installed `bassia`, or `-Bassia`, or a build of this clone.
  `-Prefix` names the folders, `-History`/`-Limit` import first-parent history. Private repositories use the user's
  git credentials.
- `server-reset` sweeps the container's blobs with `bassia-server gc -grace-hours 0` against the emptied database
  (nothing is referenced), and deletes the data folder and the client. `down` also stops the server.
