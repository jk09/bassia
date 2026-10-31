# Client-server Bassia: workspaces over a cloud-blob monorepo

## Outcome

On the branch `main-client-server`, Bassia is rewritten as a client-server version control system for fleets of coding
agents, following the design document
[Design: Cloud-Backed Version Control for Agentic Development](https://claude.ai/artifact/V2nkDzNs8XAsx1E9PpgwM7).
A user (person or agent) never handles branches, tags or explicit commits: they open a **workspace** over the monorepo
for each agentic run, the workspace's working commit is snapshotted implicitly, and they watch whether each workspace
can be **landed** on the single trunk (mainline). Workspaces can be stacked on and cloned from other workspaces, which
replaces branching. The version core reaches storage only through four traits (objects, views, path index, leases) that
assume cloud blobs; a local-disk mock blob store makes everything testable without a cloud account. Several git
repositories can be imported into one monorepo for initial testing.

## Context

- `main` holds the git-component Bassia: a meta-repo over one git repository per component, a component graph,
  integration (git, weave, LLM resolver), file locks and views. The design replaces that storage model, so this branch
  deprecates the component graph and removes it (decision below); `main` keeps it.
- Retained from `main`, as far as compatible with the design: the command line conventions (`bassia [-C <path>]
  <command> [<subcommand>] [-switch [value]]`, TOML results with `# bassia result`, `-human` Markdown, exit codes 0/1/2,
  help generated from one command table), the web dashboard's look and mechanics (server-rendered pages and SVG,
  live fragments, copyable commands), the technology stack (.NET 10, C#, ASP.NET Core, SQLite, Tomlyn, xUnit, AWS SDK
  for S3), agentic runs launched by Bassia (Claude Code by default), feedback and crash reports.
- The design leaves the cloud choice open; the existing stack is AWS, so the blob implementations are the local-disk
  mock and S3.
- ProjFS is Windows-only and out of scope for this run; workspaces use the plain-checkout provider behind the same
  `IWorkspaceProvider` interface.

## Acceptance criteria

Core and storage

- [x] A `Bassia.Core` library holds the object model (Chunk, File, Tree, Conflict, Commit, Operation, View), ids as
      BLAKE3-256 over deterministic (canonical) CBOR, FastCDC chunking (256 KiB / 1 MiB / 4 MiB) and the four traits
      `IObjectStore`, `IViewStore`, `IPathIndex`, `ILeaseStore`. It references no HTTP client, cloud SDK or database
      driver (checked by a test over its assembly references).
- [x] In-memory implementations of all four traits; a local-disk mock blob store (`DiskObjectStore`, content-addressed
      files keyed by hash prefix, with presigned-style signed URLs served by the server); SQLite implementations of the
      view store, path index and lease store; an S3 object store; a caching object store over a remote one.
- [x] Tree diff prunes equal subtree ids and yields added / deleted / modified / renamed records; renames are found by
      equal id, then chunk overlap; a modified file that shares little with its predecessor is marked `rewritten`.
      Unified hunks are produced lazily.
- [x] Snapshot hashes only dirty paths (size and modification time differ from the tree state), uploads only chunks the
      store lacks (`Have`), rebuilds trees along changed paths, keeps the ChangeId and advances only the workspace head.
      Two paths differing only by case are refused; ignore rules keep build output out.
- [x] Rebase is a three-way tree merge that costs nothing for untouched subtrees; paths changed on both sides go through
      the path's conflict policy; unresolved paths become Conflict entries in the workspace (materialized with markers).
- [x] Every mutation is an Operation pointing to a View; `undo` and `restore` write a new operation with an earlier view.
- [x] Property tests over the tree algebra pass (diff/apply round trip, merge identities, id determinism).

Landing, leases and conflicts

- [x] Land rebases a workspace's working commit onto the mainline head and advances `main` by compare-and-swap; only
      touched paths are compared against the path index since the workspace's base seq; a lost race retries.
      Mainline history is linear, every landing gets the next seq, keeps the ChangeId and gets a new CommitId.
- [x] Conflict cases follow the design: disjoint paths rebase trivially, equal results are accepted, an unchanged
      mainline side takes the workspace's version, and three distinct versions apply the path's policy: `replay`
      (default: rejected as needs-replay with the paths and the new head), `textmerge` (line-based three-way merge when
      hunks stay small), `semantic` (an external merge driver such as weave plus a validation hook), `conflict`
      (refused on mainline, stored as Conflict in the workspace). Policies are set per path glob in the versioned
      `.bassia.toml` at the tree root.
- [x] Strict validation rejects a landing when a declared read set changed since its base seq.
- [x] Leases on path prefixes: exclusive and intent modes with hierarchy, FIFO waiting with timeout, TTL renewal, and
      monotonic fencing tokens; a landing with an expired or re-granted token is refused; prefixes with
      `lease_required` refuse a landing without a valid exclusive lease.
- [x] A simulated swarm of concurrent agents landing overlapping changes through the server loses no update.
- [x] A replayed conflict lands with the original ChangeId.

Client, server and workspaces

- [x] `bassia init` creates a local monorepo (local-disk blob store and SQLite) that works without a server;
      `bassia clone -from <url>` creates a client of a `bassia-server` that runs the same core over the same stores.
      Commands behave the same in both modes.
- [x] `bassia-server` serves the RPC surface of the design (GetHead, Watch, GetObjects, StatPaths, ChangedSince, Have,
      GetChunkUrls, BeginUpload, Land, leases) plus the dashboard; large chunks move on signed URLs, structural objects
      are validated synchronously.
- [x] `workspace status` on a large tree reads no file bytes from the server (asserted by counting store reads).
- [x] Workspace commands: `create` (on mainline or stacked `-on` another workspace), `clone`, `list`, `show`, `status`,
      `snapshot`, `diff`, `check` (can it land now, and why not), `sync`, `land`, `undo`, `restore`, `log`, `close`.
      Commits are implicit: every command that reads a workspace snapshots it first.
- [x] `mainline log|show|ls|cat|changed|restore` read and revert the trunk; `lease list|acquire|renew|release`.
- [x] Agentic runs: `agent start` opens a workspace per run (or uses one), runs the agent there, snapshots and records
      the run on the workspace (prompt and agent configuration as the replay recipe), and optionally lands it;
      `agent list|show|logs|wait|stop|replay`.
- [x] `bassia import -git <url|path>,...` imports several git repositories, each under its own top-level folder, as a
      baseline commit each or with first-parent history; `bassia export -git <dir>` writes mainline incrementally into a
      git repository through `git fast-import` with a `Change-Id:` trailer.
- [x] `bassia gc` marks from retained operations and sweeps unreferenced blobs older than a grace period; `bassia scrub`
      re-hashes a sample of blobs and reports mismatches.

Conventions and UI

- [x] Command line conventions, TOML results, `-human`, exit codes and generated help are as on `main`; old commands
      fail with exit code 2 and name their successor where there is one.
- [x] The dashboard (`bassia web`, and the server's `/`) shows: overview, workspaces (stacks and landability), runs
      (live cards, timeline), mainline (landings), conflicts (what blocks landing, hot paths), leases and config.
- [x] `docs/reference.md` and `README.md` describe the new system; `dotnet build` and `dotnet test` pass in CI.

## Approach

Projects: `Bassia.Core` (library: model, encoding, chunking, traits, memory stores, version operations, landing service,
merge, diff, GC), `Bassia` (CLI: commands, local-disk / SQLite / S3 / remote stores, workspaces on disk, git interop,
agent runs, dashboard), `Bassia.Server` (`bassia-server`: RPC, signed blob URLs, dashboard), `Bassia.Tests`.

The core is a set of operations over the four traits; each ends by advancing one head with compare-and-swap. Heads are
`main` and `ws/<id>`; Operations and Views are ordinary objects. A workspace's view carries its record (name, parent
workspace, base commit and seq, working commit, status, last landing outcome, agent run). Landing runs in the
`LandingService`, in-process for a local monorepo and on the server for a clone; it is the only writer of `main` and of
the path index, which it rebuilds from the operation log when behind. Repository policy (conflict policies, lease
enforcement, ignore rules) lives in the versioned `.bassia.toml` at the root of the tree, read from mainline at landing.

## Decisions

- Old code: the component graph, split, unwind, integration (git/weave/resolver), file locks, views, git and cloud
  component storage and the AWS deployment of the old server are removed on this branch; `main` keeps them. (User,
  2026-10-31.)
- Scope: every milestone of the design except ProjFS: core, single-node server, lazy client, plain-checkout workspaces,
  leases and conflict policies (replay, textmerge, semantic, conflict), GC, scrub and git export. (User, 2026-10-31.)
- Verb: `land` (design's term); `workspace check` reports whether a workspace can land. "Projection" stays reserved for
  the ProjFS view of the design. (User, 2026-10-31.)
- Both modes: a local monorepo runs the core in-process over local-disk stores; a clone talks to `bassia-server`, which
  runs the same core over the same store implementations. (User, 2026-10-31.)
- Name: the product stays `bassia` (the design's working name "Stratum" is not used).
- Hash: BLAKE3 via the `Blake3` NuGet package (native, all CLI platforms); encoding: `System.Formats.Cbor` in canonical
  mode.
- Small files: a file that yields one chunk is referenced by its chunk id (entry kind `file`); larger ones by a File
  object (entry kind `chunked`).
- Semantic merges land only when the server has a validation hook configured; without one the policy behaves as replay.
- Authentication: out of scope per the design; the server accepts an optional shared bearer token.
- Repository policy (conflict policies per glob, lease-required prefixes, ignore rules) is versioned in `.bassia.toml`
  at the root of the tree and read from mainline at landing; client settings stay in `.bassia/config.toml`.
- Large reads are redirected to signed URLs (S3 presigned, or the server's HMAC-signed `/blobs/{id}` for the disk
  store); large writes use `BeginUpload` URLs; the server never proxies chunks of 256 KiB or more.
- Agentic runs are recorded on their workspace's view (run record, stored log), so runs need no store of their own and
  the server's dashboard sees runs from every client.
- Mainline undo is `mainline restore -seq <n>`: a new landing with the earlier tree, keeping history linear.

## Progress

- [x] Branch `main-client-server` created from `main`; old code removed (component graph, integration, locks, views,
      git and cloud component storage, the AWS deployment of the old server).
- [x] Core: model, encoding, chunking, traits, memory and local-disk stores, diff, merge, snapshot, rebase (sync),
      landing, leases, conflict policies, GC and scrub - 62 unit and property tests.
- [x] Stores: SQLite (heads, path index, leases), S3, caching, remote (HTTP) - contract tests over memory and SQLite.
- [x] CLI, workspaces on disk, agentic runs (foreground and detached), replay, git import/export.
- [x] Server (RPC, signed blob URLs, watch stream, OpenTelemetry metrics) and dashboard (overview, workspaces, runs,
      mainline, conflicts, leases, files, config).
- [x] Docs (`README.md`, `docs/reference.md`, `CLAUDE.md`), CI and the package self-test updated; 140 tests pass.

Known limits, left for follow-up records: `S3ObjectStore` compiles against the AWS SDK but was not exercised against
S3 or an emulator here; the server's SQL state is SQLite only (no PostgreSQL); ProjFS is out of scope; the AWS
deployment of the old server was removed and not replaced.

## Validation

- `dotnet build` and `dotnet test` (unit tests over memory and disk stores, property tests, CLI tests in local mode,
  server tests in-process over the local-disk mock storage, swarm landing test, git import/export round trip).
- Manual: `bassia init`, `bassia import -git`, `bassia workspace create`, edit, `workspace check`, `workspace land`,
  `bassia web`; the same against `bassia-server` with `bassia clone`.
