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

- [ ] A context-spec is an ordered list of rules in today's load-rule grammar (literal paths and directories,
      `*`/`?`/`**` globs, trailing `/` for directories only, `!` exclusion, last match wins) plus `@profile`
      references to `.bassia.toml` `[context.profiles]`. `all` (or no rules) is the whole monorepo. `.bassia.toml` is
      always in context. An invalid rule or unknown profile fails, names it, and changes nothing.
- [ ] The context-spec is stored on the workspace record (`WorkspaceRecord.Context`), so it is versioned in the
      workspace's operation log and the same on every client, in Studio and over the server API. A record without it
      decodes as `all`.
- [ ] `workspace create|clone|checkout` and `agent start|replay` take `-context <rules>`. Default, first to last:
      `-context`; the source's context-spec for a stacked (`-on`) or cloned workspace and for a replay;
      `.bassia.toml` `[context] default`; `all`.
- [ ] `bassia workspace context [<w>]` shows the spec; `-set <rules>`, `-add <rules>`, `-remove <rules>` edit it as a
      new workspace operation (`workspace log` lists it, `workspace undo|restore` reverts it). Narrowing the spec so
      that it drops paths the workspace changed fails and names them. Results report `context` and
      `paths_in_context`.
- [ ] Load rules are gone: `-load`, `workspace load` and the `workspace.load` setting fail with exit code 2 naming the
      context-spec equivalent, and a `[load]` table in `.bassia.toml` is a policy error naming `[context]`. The tree
      state no longer stores rules.
- [ ] Out-of-context directories appear in the folder as empty directories at the edge of the context, so an agent
      sees the monorepo's shape; out-of-context files at in-context levels are not shown.
- [ ] Boundary: a snapshot that finds a new, changed or deleted path outside the context-spec fails with an error
      naming the paths and how to widen the context (`workspace context -add`); nothing is snapshotted. `check` and
      `land` fail the same way. Sync and landing carry out-of-context paths along unchanged.
- [ ] Studio shows and edits a workspace's context-spec; the run guide written for a run states the context-spec.

### Phase 2 - lazy materialization through a virtual file system

- [ ] A client-side file system provider abstraction (in the `Bassia` frontend, not `Bassia.Core`) projects a
      workspace folder: listings come from trees (in-context entries, plus empty out-of-context directories); a
      file's content is fetched (through the client's cache) and written on first open, never before.
- [ ] Windows: a ProjFS provider (the `Client-ProjFS` optional feature) backs the workspace folder; placeholders
      hydrate on first read, and writes mark the path modified for the next snapshot.
- [ ] Linux and macOS: a FUSE provider (libfuse 3 on Linux; macFUSE or FUSE-T on macOS) mounts the workspace folder,
      served by a per-workspace `bassia` process that `workspace create|checkout` starts and `workspace close` stops;
      `bassia workspace mount|unmount` manage it explicitly.
- [ ] Snapshots read only hydrated or written paths (the provider reports them), so an unread file is never hashed,
      fetched, or taken as deleted.
- [ ] Where no provider is available (driver missing, no permission), checkout fails naming the missing component,
      unless `-materialize full` is given, which writes every in-context file eagerly (phase 1 behavior).
- [ ] A test proves laziness: reading one file of a large in-context directory fetches exactly that file's chunks
      (counted on the object store).

### Phase 3 - documentation and live validation

- [ ] `docs/reference.md` replaces "Load rules" with "Context-specs" (grammar, `[context]`, commands, the boundary, the
      file system providers and their requirements); `bassia help` lists the concept.
- [ ] A live run on the local cloud whose context is one project of an imported monorepo: the client's cache holds
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
- The context-spec lives on the workspace record, not in the client's tree state: it defines the workspace, so every
  client, Studio and a replay agree on it (unlike load rules, which were per checkout).

## Progress

- [x] Specification agreed (open questions answered).
- [ ] Phase 1: context-spec on the workspace, load rules removed, boundary.
- [ ] Phase 2: ProjFS and FUSE providers, laziness test.
- [ ] Phase 3: documentation, live validation.

## Validation

- `dotnet test` with `BASSIA_TEST_POSTGRES` set: unit tests for the spec grammar and propagation, workspace tests for
  lazy materialization counting fetched objects, boundary enforcement, undo of a context change; a CLI test; a server
  test for the record round-trip.
- A run on the local cloud (`bassia-local-cloud` skill) whose workspace's context is one project of an imported
  monorepo: the client's cache holds only what the agent opened.
