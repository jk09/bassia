# Context-spec: the part of the monorepo a workspace can see

## Outcome

Every workspace carries an editable **context-spec**: a filter, modelled after a ClearCase config spec, that defines
which paths of the monorepo the workspace contains. An agentic run in the workspace sees, reads and edits only paths
inside its context-spec; nothing outside it is listed, fetched or materialized. Inside the context, files are
materialized lazily - only when the agent actually needs one for reading or editing - so a workspace over a very large
monorepo costs only what its run touches. The context-spec is part of the workspace's versioned record, so it follows
the workspace from client to client, into Studio, into stacked and cloned workspaces, and into replays.

## Context

- The monorepo is meant to grow beyond what one agent (or one machine) should hold. An agent's useful context is a
  small, task-shaped subset: its project, the libraries it depends on, shared build configuration.
- Load rules (`bassia-c0f09fd6`, `Bassia.Core/Workspaces/LoadRules.cs`, "Load rules" in `docs/reference.md`) already
  give a ClearCase-like filter, but with different semantics:
  - they are a property of one *checkout* on one client (stored in the client's tree state), not of the workspace;
    two machines can load one workspace differently;
  - they are *eager*: everything they select is fetched and written at checkout or `workspace load`;
  - they are *not a boundary*: unloaded directories show as empty folders, files may be created there, and the
    workspace's tree still holds - and lands - everything.
- The context-spec adds what load rules lack: a definition that belongs to the workspace (server-side, versioned in
  its operation log), a boundary on what the run may see and change, and lazy materialization inside that boundary.
  ProjFS-style fetch-on-open was explicitly left out of scope by `bassia-c0f09fd6`; this record takes it up.
- The workspace record (`WorkspaceRecord` in `Bassia.Core/Model/Objects.cs`) is held by the workspace head's `View`,
  so a field on it is versioned per operation, undoable (`workspace undo|restore`) and visible to every client and
  Studio through the existing stores - no new storage trait is needed.

## Acceptance criteria

Delivered in three phases, each a separate pull request against this record.

### Phase 1 - the context-spec on the workspace (replaces load rules)

- [x] A context-spec is an ordered list of rules in today's load-rule grammar (literal paths and directories,
      `*`/`?`/`**` globs, trailing `/` for directories only, `!` exclusion, last match wins) plus `@profile`
      references to `.bassia.toml` `[context.profiles]`. `all` (or no rules) is the whole monorepo. `.bassia.toml` is
      always in context. An invalid rule or unknown profile fails, names it, and changes nothing.
- [x] The context-spec is stored on the workspace record (`WorkspaceRecord.Context`), so it is versioned in the
      workspace's operation log and the same on every client, in Studio and over the server API. A record without it
      decodes as `all`.
- [x] `workspace create|clone` and `agent start|replay` take `-context <rules>` (`checkout` follows the record). Default, first to last:
      `-context`; the source's context-spec for a stacked (`-on`) or cloned workspace and for a replay;
      `.bassia.toml` `[context] default`; `all`.
- [x] `bassia workspace context [<w>]` shows the spec; `-set <rules>`, `-add <rules>`, `-remove <rules>` edit it as a
      new workspace operation (`workspace log` lists it, `workspace undo|restore` reverts it). Narrowing the spec so
      that it drops paths the workspace changed fails and names them. Results report `context` and
      `paths_in_context`.
- [x] Load rules are gone: `-load`, `workspace load` and the `workspace.load` setting fail with exit code 2 naming the
      context-spec equivalent. A `[load]` table in `.bassia.toml` is no longer read; a new workspace without
      `-context` fails, asking to rename it to `[context]` (see Decisions). The tree state stores a copy of the
      record's spec, not rules of its own.
- [x] Out-of-context directories appear in the folder as empty directories at the edge of the context, so an agent
      sees the monorepo's shape; out-of-context files at in-context levels are not shown.
- [x] Boundary: a snapshot that finds a new, changed or deleted path outside the context-spec fails with an error
      naming the paths and how to widen the context (`workspace context -add`); nothing is snapshotted. `check` and
      `land` fail the same way. Sync and landing carry out-of-context paths along unchanged.
- [x] Studio shows and edits a workspace's context-spec (and refuses its own edits outside it); a run sees its spec
      in `BASSIA_CONTEXT`, and a run whose agent changed a path outside it fails and lands nothing.

### Phase 2a - lazy materialization through FUSE (Linux, macOS)

- [x] A virtual file system abstraction (`ILazyFolders` in the core, implemented in the `Bassia` frontend) projects a
      lazy folder: its files and placeholders live in a backing directory; listings and sizes come from there, and a
      placeholder is hydrated (fetched through the client's cache) just before it is first opened or changed, never
      before.
- [x] Linux and macOS: a FUSE provider (libfuse 3) mounts the workspace folder, served by a per-workspace
      `bassia workspace serve` process that `workspace create|clone|checkout` starts, `agent start` ensures, and
      `workspace close` stops; `bassia workspace mount|unmount` manage it explicitly.
- [x] Snapshots read the backing directory; an untouched or only read placeholder is never hashed or taken as
      deleted, and edits, new files, renames and deletions through the mount are snapshotted.
- [x] Where no provider is available (driver or library missing), a lazy folder is refused with the reason; full
      materialization (`-materialize full`, the default `workspace.materialize`) works as before.
- [x] A test proves laziness: through a real FUSE mount, reading one file of a lazy folder fetches exactly that file's
      content, and nothing else's.

### Phase 2b - lazy materialization through ProjFS (Windows)

- [x] Windows: a ProjFS provider (the `Client-ProjFS` optional feature) projects the backing directory into the
      workspace folder (the virtualization root); a file is hydrated when first read, and changes stay in the folder,
      which snapshots read. A command that rewrites the backing directory asks the server to drop those paths from the
      folder, so it shows them anew.
- [x] The laziness and CLI tests pass on Windows in CI (`Dev Drive (Windows)` job, with ProjFS enabled).

### Phase 3 - documentation and live validation

- [x] `docs/reference.md` replaces "Load rules" with "Context-specs" (grammar, `[context]`, commands, the boundary, the
      file system providers and their requirements); `bassia help` lists the concept.
- [x] A live run on the local cloud whose context is one project of an imported monorepo: the client's cache holds
      only the files the agent opened, and an edit outside the context is refused.

## Approach

- **Core**: `ContextSpec` takes over `LoadRules`' matcher (renamed and moved; `Contains(path)`,
  `MayContainUnder(dir)`). `WorkspaceRecord` gains an optional `Context` (rule list; absent = `all`).
  `WorkspaceService` gains a set-context operation; create, clone, stack and replay propagate it. Tree walks prune
  out-of-context directories (`Trees.DiffAsync(..., descend)`), and the snapshot checks the boundary before writing
  anything.
- **Policy**: `RepoPolicy` reads `[context] default` and `[context.profiles]` instead of `[load]`.
- **Client**: checkout writes the out-of-context edge as empty directories and hands in-context content to the file
  system provider (phase 2) or writes it eagerly (phase 1, and `-materialize full`). The tree state records which
  in-context paths are materialized.
- **CLI / server / Studio**: `-context` switches and `workspace context` in `CommandTable`; the record field over the
  existing workspace endpoints; a Studio editor on the workspace page.
- **File systems**: ProjFS through its Win32 API (P/Invoke); FUSE through P/Invoke to libfuse 3. Both live in the
  `Bassia` frontend, keeping `Bassia.Core` free of platform code.

## Decisions

- **The context-spec replaces load rules** (user, 2026-11-09). One filter, owned by the workspace; "loaded" becomes
  "materialized so far". Rejected: load rules as a per-client prefetch inside the context (two filters to explain),
  and keeping both independent.
- **Lazy materialization by a virtual file system** - ProjFS on Windows, FUSE on Linux/macOS (user, 2026-11-09).
  Agents need no cooperation: reading a file is enough. Rejected: an explicit `workspace open` command (agents must
  know to call it) and placeholder stubs (they look like real, empty files to tools). Consequence: a driver per
  platform and a serving process per mounted workspace on Linux/macOS; `-materialize full` stays as the fallback.
- **Out-of-context directories show as empty directories** (user, 2026-11-09), as with load rules, so an agent sees
  the monorepo's shape and can ask for a wider context. Rejected: hiding them.
- **Writes outside the context are refused by the snapshot** (user, 2026-11-09), naming the paths; the agent widens
  the context explicitly. Rejected: ignoring them silently, and widening automatically (the spec would stop being a
  boundary).
- **A leftover `[load]` table is ignored, not a policy error** (phase 1). A policy error would make `RepoPolicy`
  refuse every landing - including the one that fixes the file. Instead `[load]` is not read, and resolving a new
  workspace's default context fails with a message to rename it to `[context]` (or give `-context`).
- **No run guide file exists**, so the run's context is exposed as the `BASSIA_CONTEXT` environment variable, and a
  boundary refusal ends the run as `failed` with the refusal as its last line (phase 1). Rewriting the prompt was
  rejected: it is the replay recipe.
- **`-add` appends; `-remove` removes exact rules.** On `all`, `-add` keeps `all` (it already contains everything);
  narrowing from `all` uses `-set`.
- **Lazy is opt-in (`workspace.materialize`, default `full`)** (phase 2a). A lazy folder needs a driver and a serving
  process per folder; making it the default would make every machine without FUSE/ProjFS fail. Rejected: failing
  checkout where no provider exists (the spec's first draft) and silently falling back (hides the cost).
- **Placeholders live in a backing directory, and FUSE is a passthrough over it** (phase 2a). A placeholder is a
  sparse file of the right size and mode with a modification time in the past; FUSE answers `getattr` with `lstat`
  of the backing file (no `struct stat` layout in managed code) and hydrates in place, keeping size and time, so a
  snapshot - which reads the backing directory, not the mount - sees an untouched or read placeholder as unchanged.
  Rejected: an in-memory projection (listings would need the trees on every call, and a crash would lose edits).
- **The FUSE binding is P/Invoke to libfuse 3's high-level API** with `UnmanagedCallersOnly` callbacks; no package
  is needed. macOS (macFUSE) shares the code but is not tested in CI.
- **ProjFS through the pure C# `Microsoft.Windows.ProjFS` package (2.1)** (phase 2b), projecting the same backing
  directory as FUSE. Unlike FUSE, ProjFS keeps writes in the virtualization root, so on Windows snapshots read the
  folder (and need it served), and tree changes under it go to the server as refresh requests in a control directory
  (`<id>.projfs/requests`), which deletes the on-disk state of those paths; a lock file held while serving tells
  whether it runs. Rejected: projecting from the tree state directly (two sources of truth for listings) and IPC over
  named pipes (a file protocol also queues requests while no server runs).
- The context-spec lives on the workspace record, not in the client's tree state: it defines the workspace, so every
  client, Studio and a replay agree on it (unlike load rules, which were per checkout).

## Progress

- [x] Specification agreed (open questions answered).
- [x] Phase 1: context-spec on the workspace, load rules removed, boundary; docs and help updated with it.
- [x] Phase 2a: FUSE provider (Linux; macOS untested), laziness tests through real mounts, CI installs FUSE 3.
- [x] Phase 2b: ProjFS provider (Windows), verified in CI (which found a test fake that mounted twice, a log file
      locked against readers, and a server that reported itself serving before ProjFS virtualized the folder).
- [x] Phase 3: documentation, live validation (see Validation; it also found that a landed workspace's lazy folder
      kept being served, now unmounted on landing).

## Validation

- `dotnet test` with `BASSIA_TEST_POSTGRES` set: unit tests for the spec grammar and propagation, workspace tests for
  lazy materialization counting fetched objects, boundary enforcement, undo of a context change; a CLI test; a server
  test for the record round-trip.
- A run on the local cloud (`bassia-local-cloud` skill) whose workspace's context is one project of an imported
  monorepo: the client's cache holds only what the agent opened.

### Live validation (2026-11-09)

On Linux, with Azurite (Blob Storage) and PostgreSQL behind a `bassia-server`, a git repository with `api/` (5 files
and a 2 MB `testdata/big.bin`), `web/` and `docs/` was imported through one client (seq 1). A second, fresh client ran
`agent start -context "src/api/,!src/api/testdata/" -materialize lazy -land` with a command that read and appended to
`src/api/src/a1.cs`, listed `src/api/src` and `src/web`, and printed `BASSIA_CONTEXT`:

- the run saw all five `api` files listed, `src/web` empty, and `BASSIA_CONTEXT=src/api/,!src/api/testdata/`; it
  landed at seq 2 with exactly one changed path;
- the server's log shows one hydration (`src/api/src/a1.cs`); the client's cache held 5.8 KB in 23 objects - trees and
  `a1.cs`, none of `a2..a5.cs`, `web/`, `docs/` or the 2 MB fixture;
- a run with `-context src/api/` writing `src/web/x.js` failed with the boundary refusal as its last line, and nothing
  reached mainline.

