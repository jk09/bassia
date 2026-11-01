# Bassia reference

The complete guide to the client-server `bassia`: the command line, workspaces and landing, conflict policies, leases,
agentic runs, git import and export, the server and the web dashboard. For what Bassia is and why, start with the
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
bassia web                                                      # watch all of it
```

## Command line

`bassia` is operated mainly by AI agents, so the command line is its primary interface: everything the dashboard shows
has a command, and every command answers in TOML.

```
bassia [-C <path>] <command> [<subcommand>] [-switch [value]]...
```

- Switches are case-insensitive and may be written `-name` or `--name`. Lists are comma-separated (`-git a,b`) or given
  by repeating the switch.
- A command's main argument may be given without its switch: `bassia workspace land fix-login` is
  `bassia workspace land -workspace fix-login`.
- A rest-of-line switch (`-run`, the message of `feedback send`) takes everything after it verbatim, so it comes last.
- `-C <path>`, before the command, runs `bassia` as if it had been started in `<path>`, same as `git -C <path>`.
- A workspace is named by its name, its id (`w3f9a1c2b0d`), or a unique prefix of either of at least four characters.
  Inside a workspace's folder the workspace switch can be left out.
- Runs are named by their id (`run-brave-otter-3f2a91`), a unique prefix of its key of at least four characters
  (`brave-ot`), or the name of their workspace.

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
| Monorepo | `init [-path <dir>]`, `clone -from <url> [-path <dir>] [-token <t>]`, `status`, `version`, `help` |
| Configuration | `config list`, `config get -key <k>`, `config set -key <k> -value <v>`, `config unset -key <k>` |
| Workspaces | `workspace create <name> [-path <dir>] [-on <workspace>] [-intent <text>] [-load <rules>]`, `workspace clone -from <w> -name <n> [-load <rules>]`, `workspace checkout <w> [-load <rules>]`, `workspace load [<w>] [-load <rules>]`, `workspace list [-all]`, `workspace show`, `workspace status`, `workspace diff [-patch] [-path <p>]`, `workspace snapshot`, `workspace describe -intent <text>`, `workspace check [-strict -reads <p>,...] [-lease-token <id:token>,...]`, `workspace land [-keep] [-strict -reads ...] [-lease-token ...]`, `workspace sync`, `workspace undo`, `workspace restore -op <op>`, `workspace log [-limit <n>]`, `workspace close [-abandon] [-remove]` |
| Mainline | `mainline log [-limit <n>] [-path <p>]`, `mainline show [-seq <n>] [-patch]`, `mainline ls [<path>] [-seq <n>]`, `mainline cat <path> [-seq <n>]`, `mainline changed -since <seq> [-prefix <p>]`, `mainline restore -seq <n>` |
| Leases | `lease list [-prefix <p>]`, `lease acquire <prefix> [-workspace <w>] [-mode exclusive\|intent] [-ttl <s>] [-wait <s>]`, `lease renew -id <id> -token <n>`, `lease release -id <id> -token <n>` |
| Agentic runs | `agent start (-prompt <text> \| -run <command...>) [-workspace <w>] [-name <n>] [-on <w>] [-model <m>] [-land] [-keep] [-lease <p>,...] [-lease-wait <s>] [-detach]`, `agent list [-status <s>]`, `agent show`, `agent logs [-tail <n>]`, `agent wait [-timeout <s>]`, `agent stop`, `agent replay <run\|workspace> [-land]` |
| Git | `import -git <url\|path>,... [-prefix <folder>,...] [-history] [-limit <n>]`, `export -git <dir> [-branch <b>]` |
| Dev Drive | `devdrive status [-path <dir>]` |
| Maintenance | `gc [-dry-run] [-grace-hours <n>] [-retention-days <n>]`, `scrub [-sample <n>]` |
| Dashboard | `web [-port <n>] [-no-open]` |
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
- **Changes.** `workspace status`, `workspace diff` and the dashboard list each changed path as `added`, `deleted`,
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

## Load rules: what a workspace fetches, caches and holds

A workspace's folder need not hold the whole monorepo. Its **load rules** select the paths it holds, like the load rules
of a ClearCase snapshot view's config spec. The client fetches the content of those paths only, and caches it in
`.bassia/cache`, shared by all workspaces of the client. It never reads the trees below a directory nothing of which is
loaded. Many clients can then each run agents on their own part of a large monorepo, each downloading only that part.

```sh
bassia workspace create api-fix -load "*,api/,libs/shared/,!api/testdata/"   # top-level files, two projects, minus fixtures
bassia agent start -load @api -prompt "..." -land                            # a profile from .bassia.toml
bassia workspace load api-fix -load "*,api/,web/"                            # load more (or less) later
bassia workspace load api-fix                                                # show the rules in force
```

- **Rules** are paths from the root of the tree. They are tried in order, and the last rule that matches a path
  decides:
  - `api` or `api/` loads that directory and everything below it; a literal path without a slash may also name a file.
  - `*`, `*.md`, `docs/*.md` load the files matching the glob at that depth. `*` stays within one name and `**`
    crosses folders (`**/*.proto`).
  - `src/*/`, with a trailing slash, loads the matching directories whole.
  - `!pattern` excludes what it matches.

  `all`, or no rules, loads everything. `.bassia.toml` is always loaded.
- **Defaults**, from first to last: `-load` on `workspace create|clone|checkout` and `agent start|replay`; this
  machine's `workspace.load` setting; mainline's `.bassia.toml` `[load] default`; everything. `@name` expands to a
  profile of `.bassia.toml`, and a replay loads what the original loaded.

  ```toml
  [load]
  default = ["*"]
  [load.profiles]
  api = ["*", "api/", "!api/testdata/"]
  web = ["*", "web/"]
  ```
- **What is not loaded stays as it is.** The workspace's tree still holds everything. A snapshot never reads an
  unloaded path as deleted, and sync and landing carry unloaded paths along unchanged. A directory that is not loaded
  shows as an empty folder, so an agent sees the monorepo's shape. A file an agent creates there is added like any
  other.
- **Changing the rules.** `workspace load` snapshots first, so edits to paths the new rules drop stay on the workspace.
  It then removes those paths from the folder, and writes the paths the new rules add.
- Results of `workspace create|clone|checkout|load` report `load` (the rules) and `files_loaded`.

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
or leases missing). A refused landing is recorded on the workspace (`last_land`), which the dashboard shows.

`-strict -reads <prefix>,...` also refuses the landing when anything under the declared read set changed since the
workspace's base - for a coherent rewrite that relied on an API elsewhere. `-keep` keeps the workspace open after it
landed, on the new head with a fresh change; by default it is marked `landed`.

`mainline restore -seq <n>` puts an earlier seq's tree back, as a new landing: history stays linear.

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
3. snapshots what the agent changed, stores the log with the run, and records how it ended (`succeeded`, `failed`,
   `stopped`);
4. with `-land`, lands the workspace when the agent succeeded.

`-detach` runs it in the background and returns at once; `agent list|show|logs|wait|stop` follow it from any shell.
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

`bassia-server` serves one monorepo to many clients and agents:

```sh
bassia-server -data /srv/bassia -url http://0.0.0.0:8080 -public-url https://bassia.example.com -token <shared-token>
bassia-server -data /srv/bassia -storage s3 -bucket acme-bassia -region eu-central-1      # blobs on S3
bassia-server gc -data /srv/bassia [-grace-hours 24] [-retention-days 14] [-dry-run]
bassia-server scrub -data /srv/bassia [-sample 1000]
```

### Azure: Blob Storage and Azure Database for PostgreSQL

For several server nodes behind a load balancer, keep objects in Azure Blob Storage and heads, the path index and
leases in Azure Database for PostgreSQL (flexible server):

```sh
bassia-server -data /var/lib/bassia -url http://0.0.0.0:8080 -public-url https://bassia.example.com -token <shared-token> \
  -storage azure -azure-url https://acmebassia.blob.core.windows.net/bassia \
  -state postgres -postgres "Host=acme-bassia.postgres.database.azure.com;Database=bassia;Username=bassia;Password=...;SSL Mode=Require"
```

- **Blob Storage**: `-azure-url <container url>` authenticates with Microsoft Entra ID (`DefaultAzureCredential`: a
  managed identity on Azure, else Azure CLI or environment credentials); the identity needs *Storage Blob Data
  Contributor* on the container, and *Storage Blob Delegator* on the account to sign SAS URLs with a user delegation
  key. `-azure-connection <string>` uses an account connection string instead (also Azurite's development string),
  and creates the container (`-azure-container`, default `bassia`) if it is missing. Objects are block blobs under
  `-azure-prefix` (default `objects/`), keyed by hash prefix. Chunks of 256 KiB and more move between clients and the
  container directly on SAS URLs valid for 30 minutes; the server never proxies them.
- **PostgreSQL**: `-state postgres -postgres <connection string>` (PostgreSQL 13 or later). The tables (`bassia_heads`,
  `bassia_paths`, `bassia_meta`, `bassia_leases`, `bassia_lease_queue`, sequence `bassia_fencing`) are created on start.
  Heads move by compare-and-swap, paths are compared in byte order (`COLLATE "C"`), lease grants serialize through a
  transaction-scoped advisory lock and take fencing tokens from the sequence, and the path index is filled in seq
  order from the operation log - so any number of nodes can share one database. Use `SSL Mode=Require` on Azure.
  A landing looks up every path it touched in one statement per 10,000 paths, and `mainline changed` and strict read
  sets read only the history after their seq, so neither grows with the size of the monorepo's history.
- Every switch also comes from its environment variable (`BASSIA_SERVER_STORAGE`, `BASSIA_SERVER_AZURE_URL`,
  `BASSIA_SERVER_AZURE_CONNECTION`, `BASSIA_SERVER_STATE`, `BASSIA_SERVER_POSTGRES`, ...). `bassia-server gc` and `scrub`
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

The data folder holds `state.db` (SQLite: heads, path index, leases) and, with the default `-storage disk`, the blob
container `objects/` - a local-disk stand-in for cloud blob storage, laid out by hash prefix, so a server and all of
its tests run without a cloud account. Every switch can also come from a `BASSIA_SERVER_<NAME>` environment variable.
Authentication is out of scope for this design: `-token` sets one shared bearer token for the API and the dashboard
(`?token=<token>` once in a browser sets a cookie).

The RPC surface under `/api/v1` is what a client's remote stores call:

| Call | Purpose |
| --- | --- |
| `GET /main` | GetHead: mainline's head operation, seq, commit and tree. |
| `GET /watch` (and `/events`) | A stream (server-sent events) of landings with the prefixes they touched, and of workspaces that moved. |
| `POST /objects/fetch` | GetObjects: many objects in one binary reply (structural objects and chunks under 256 KiB); what it leaves out is read on its direct URL. |
| `POST /objects/put` | Many small objects in one binary request, each checked like a single put. |
| `POST /objects/batch` | Structural objects by id as JSON, optionally with subtrees to a depth. |
| `POST /paths/stat`, `GET /paths/list`, `GET /paths/changed` | StatPaths, listing and ChangedSince from the path index. |
| `POST /objects/have` | Which ids the server already holds; asked before every upload. |
| `POST /objects/read-urls`, `POST /objects/upload-urls` | GetChunkUrls and BeginUpload: signed URLs for direct transfers. |
| `GET/PUT /objects/{id}` | One object; large chunks are redirected to their signed URL, structural objects are validated on upload. |
| `GET /heads...`, `POST /heads/advance` | Workspace heads, by compare-and-swap; `main` is refused (only landing moves it). |
| `POST /leases/acquire`, `renew`, `release`, `GET /leases` | Leases; waiting happens on the server. |
| `POST /land` | Land (or check, with `dry_run`) a workspace. |

Objects are immutable, so a client caches every object it reads in `.bassia/cache`, shared by all its workspaces, and
never asks again. Chunks of 256 KiB and more go between the client and the blob container directly on signed URLs
(S3 presigned URLs, or the server's own `/blobs/{id}?op=...&sig=...` for the disk container), so the server never
proxies file bytes. When `OTEL_EXPORTER_OTLP_ENDPOINT` is set the server exports OpenTelemetry metrics (landings by
outcome, objects stored, bytes served, redirected reads).

## Web dashboard

```sh
bassia web                       # http://127.0.0.1:8080/, opened in the browser
bassia web -port 9000 -no-open
```

`web` serves a read-only dashboard over the monorepo (a local monorepo or a client), on 127.0.0.1 only; `bassia-server` serves the
same pages at its root. Pages and pictures are drawn on the server and work without JavaScript; the script refreshes the
live parts (on the server, when it pushes a change). Every page shows the `bassia` commands for what it describes;
click one to copy it.

- **Overview**: mainline's seq, open workspaces, live runs, how many can land now and how many are blocked, leases;
  the live runs as cards; every open workspace with whether it can land; a donut of where workspaces stand; the
  latest landings.
- **Workspaces**: the stacks drawn left to right from mainline, each workspace coloured by whether it can land, and
  every workspace with its base, what it is stacked on, its run and intent. A workspace's page shows its record, the
  result of landing it now (with the conflicting paths and their policies), its changes and its operation log.
- **Runs**: live runs as animated cards, every run over time, runs by status; a run's page streams its output.
- **Mainline**: the landings by seq; a landing's page shows its commit, change id, intent, recipe and changes, and on
  request the hunks (rewritten files as a summary).
- **Conflicts**: every workspace blocked from landing with its conflicting paths, policies and the commands that deal
  with it, and the **hot paths** that keep colliding - candidates for an exclusive lease.
- **Leases**, **Files** (mainline's tree at any seq, from tree metadata) and **Config** (the policy in `.bassia.toml`
  and the folder's settings).

## Configuration

Settings of a client live in `.bassia/config.toml` (`config list|get|set|unset`); an environment variable,
where a setting has one, wins over the file. Repository policy is not a setting: it is versioned in `.bassia.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `user.id` (`BASSIA_USER`) | the login name | Who acts: the agent of operations, the owner of workspaces. |
| `agent.command` | `claude -p --permission-mode acceptEdits` | The agent a run starts; the prompt is appended as one quoted argument. To let Claude Code also run builds and tests, allow them and keep `-p` last, since `--allowedTools` takes a list that would swallow the prompt: `claude --permission-mode acceptEdits --allowedTools "Bash(dotnet:*)" -p`. |
| `agent.model` | | Passed to the agent as `--model`. |
| `server.url` | | The server a client talks to (set by `bassia clone`). |
| `server.token` (`BASSIA_SERVER_TOKEN`) | | The server's shared token. |
| `merge.semantic_driver` | `weave-driver` | The driver of the semantic policy, git merge-driver style (`%O %A %B`); `off` disables it. The release packages bundle it. |
| `land.validate` | | A shell command a machine-merged tree must pass before it lands (local monorepo). |
| `workspace.load` | | The load rules a new workspace gets on this machine when `-load` is not given (comma-separated, `@name` for a profile); empty: `.bassia.toml` `[load] default`, else everything. |
| `workspace.clone` | `auto` | On Windows, block-clone workspace files from `.bassia/clones` on a Dev Drive (or another block-cloning ReFS volume); `off` always writes them. |
| `workspace.dir` | `workspaces` | Where new workspaces' folders go. |
| `lease.ttl` | `600` | Seconds a lease lives unless renewed. |
| `gc.grace_hours` | `24` | Unreferenced blobs younger than this are kept (uploads for landings in flight). |
| `gc.retention_days` | `14` | How long a closed workspace's operations are kept. |

## Garbage collection and scrubbing

Rewrites create garbage quickly. `bassia gc` (local monorepo) and `bassia-server gc` mark every object reachable from
the retained operations - mainline's whole log (the audit trail), every workspace head, and heads named `pin/...` - and
sweep the blobs nothing reaches that are older than the grace period. A closed workspace's head is pruned
`gc.retention_days` after it closed, which makes its unlanded snapshots collectable. `scrub` re-hashes stored blobs (all,
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

Bassia has four projects; each depends only on the ones before it.

1. **`Bassia.Core`** - the version core. It may not reference an HTTP client, a cloud SDK or a database driver (a test
   checks its references).
   - `Model`: the objects - chunks (FastCDC, 256 KiB / 1 MiB / 4 MiB), `FileObject`, `Tree` (sorted entries with kind,
     id, size and mode), `ConflictObject` (bases and sides), `Commit` (tree, parents, change id, author, intent, replay
     recipe), `Operation` and `View` - with ids as BLAKE3-256 hashes of canonical CBOR (`ObjectCodec`).
   - `Storage`: the four traits - `IObjectStore` (immutable objects: has, stat, ranged read, put, begin upload),
     `IViewStore` (heads moved by compare-and-swap), `IPathIndex` (mainline's path versions by seq) and `ILeaseStore`
     (leases with fencing tokens) - with in-memory implementations, the local-disk blob store (`DiskObjectStore`) and
     decorators (caching, counting, verifying).
   - `Version`: `Repository` (mainline, workspace heads, operation logs, path index catch-up), `Trees` (lookup, edit,
     diff and three-way merge, all skipping equal subtrees), `Diffs` (renames, rewrites, hunks), `WorkspaceService`
     (create, stack, clone, amend, sync, undo, restore, close), `LandingService` (the only writer of `main`),
     `RepoPolicy` and `ConflictResolver` (the conflict policies), `GarbageCollector`.
   - `Workspaces`: `IWorkspaceProvider` - the plain-checkout `CheckoutProvider` (a ProjFS provider on Windows would
     implement the same interface), with an optional `IFileMaterializer` that creates files more cheaply than writing
     them - its tree state, and `WorkspaceClient`, which snapshots and keeps folders in step.
2. **`Bassia`** - the CLI: the commands (`Cli`, `CliCommands`), the store implementations that need more than the core
   may use (`Stores`: `SqliteState` and `PostgresState` for heads, path index and leases; `S3ObjectStore` and
   `AzureBlobObjectStore`; the remote stores and landing of a client over HTTP, with batched reads and writes), `Monorepo` (a local monorepo or a client, giving every command the same core), agentic runs
   (`Agents`), git interop and the merge driver (`Interop`), Windows block cloning and the clone cache for Dev Drive
   (`Platform`), and the dashboard (`Web`).
3. **`Bassia.Server`** - `bassia-server`: the same core over the server's stores, the RPC surface, signed blob URLs,
   the watch stream and the dashboard.
4. **`Bassia.Tests`** - unit tests of the core (property tests over the tree algebra, landing, leases, workspaces on
   disk, garbage collection), the store contract over memory and SQLite, the CLI in-process on local monorepos, the
   server in-process with clients over HTTP, and git interop.


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

Everything runs without a network or a cloud account. `Bassia.Tests/Core` tests the version core on the in-memory
stores: encoding and ids, chunking, property tests over the tree algebra (diff then apply reproduces the tree, merge
identities, equal-subtree pruning), every landing case and conflict policy, strict mode, stacked workspaces, sync,
undo and restore, the path index and its rebuild from the operation log, leases and fencing, a swarm of concurrent
agents landing through several landing services, a replay landing with the original change id, garbage collection
and scrubbing, and workspaces on disk (snapshots that hash only what changed, ignore rules, case collisions,
materialized conflicts, undo, and status reading no file bytes). `StoreContractTests` holds the in-memory and SQLite
stores to the same contract. `CliTests` drives the command line in-process on local monorepos. `ServerTests` starts
`bassia-server` in the test process over the local-disk blob storage and runs clients and raw HTTP clients against it: the
command line across two clients, a swarm of agents landing overlapping trees over HTTP, laziness of status against
the server, object validation, mainline's protection and signed blob URLs, and every dashboard page.
`PostgresStateTests`, `ObjectStoreContractTests` and `SyncEfficiencyTests` run against PostgreSQL
(`BASSIA_TEST_POSTGRES`, a connection string; every test gets its own schema) and Azure Blob Storage
(`BASSIA_TEST_AZURE_BLOB`, a connection string to Azurite; every test gets its own container) - CI provides both,
locally `/bassia-local-cloud` does (below), and
without them those tests check nothing. `SyncEfficiencyTests` counts the requests a client sends for a checkout, a
second checkout and a snapshot, on the disk backend and on Azure + PostgreSQL, and runs two clients end to end on the
latter.
`GitInteropTests` imports several repositories with interleaved history and exports mainline back, incrementally.
`CloneCacheTests` covers the clone cache with a stand-in cloner on every platform (clones, fallbacks, the setting,
pruning, status); `DevDriveTests` clone for real on the volume named by `BASSIA_TEST_DEVDRIVE_DIR` - the
`Dev Drive (Windows)` CI job formats a VHD as a Dev Drive (plain ReFS where it cannot) for them; without the variable
they check nothing.

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
