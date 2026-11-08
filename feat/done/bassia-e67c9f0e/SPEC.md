# Unique workspace names

## Outcome

Two open workspaces can never share a name. Creating or cloning a workspace under a name an open workspace already
holds fails with a clear error in the CLI, in Studio and over the server API, even when two clients race.

## Context

Studio's Workspaces list showed two open workspaces both named `ws1`, created a minute apart. Nothing prevents it
today: `WorkspaceService.CreateAsync` and `CloneAsync` only check the name's syntax (`CheckName`), and workspace heads
are keyed by id (`ws/<id>`). `Repository.ResolveWorkspaceAsync` already assumes names are unique among open
workspaces: when several open workspaces share a name it refuses and asks for the id. A closed workspace's name may
be reused today, and when no open workspace has the name, resolution falls back to the latest closed one.

The only atomic primitive in `IViewStore` is a compare-and-swap on a named head, and `bassia-server` lets clients move
only `ws/` and `pin/` heads.

## Acceptance criteria

- [x] Creating a workspace with the name of an open workspace fails with "A workspace named '<name>' is already open
      (<id>)." and creates nothing. This holds for CLI `bassia workspace create`, Studio "New workspace" and the server
      API.
- [x] The same holds for cloning a workspace and for runs given an explicit name.
- [x] When two creates of the same name run concurrently, exactly one succeeds. Verified against the local PostgreSQL
      store and against `bassia-server`.
- [x] A name becomes free again once its workspace is landed or abandoned (Q1).
- [x] Names Bassia generates itself (`restore-<seq>`, git import, run names) never fail on a collision; they take a numeric suffix (Q3).
- [x] Existing duplicate open workspaces keep working by id (Q2).
- [x] Studio's New workspace form shows the error inline; the Overview and Workspaces pages are unaffected.

## Approach

- Reserve a name with a head `name/<name>` that points at the workspace's create operation, claimed by
  compare-and-swap from empty. If the head is taken but its workspace is no longer open (or no longer exists), take
  it over with a compare-and-swap from the current value; otherwise refuse. Landing and abandoning need no change, so
  older clients and retention stay correct.
- Claim the name after writing the workspace's first operation. If the claim fails, delete the new workspace head
  (no one else can have moved it yet) and raise the error.
- `bassia-server`: let clients move `name/` heads, as they already can `ws/` and `pin/` heads. The remote view store
  needs nothing new.
- On a collision, a generated name gets a numeric suffix (`-2`, `-3`, ...).
- Studio shows the `VersionException` message through the existing Banner.

## Decisions

- Q1 scope: names are unique among **open** workspaces; a name is freed when its workspace lands or is abandoned
  (matches `ResolveWorkspaceAsync`, which already prefers open workspaces and falls back to the latest closed one).
- Q2 existing duplicates: left as they are, with no migration. They keep working by id, and their name stays taken
  until all of them are closed.
- Q3 generated names (`restore-<seq>`, git import, run names derived from the task): on a collision, take the next
  free name with a numeric suffix (`-2`, `-3`, ...).
- Q4 case: names are case-sensitive (`WS1` and `ws1` differ), as name lookup already is.

## Progress

- [x] Questions answered
- [x] Reservation in the core (`Repository.ClaimWorkspaceNameAsync`, `OpenWorkspaceNamedAsync`, `FreeWorkspaceNameAsync`;
      `WorkspaceService` create/clone) + server head rule (`name/` heads accepted only when they point at a workspace
      of that name)
- [x] GC: name reservations are not roots; those of closed workspaces are dropped
- [x] Generated names (restore, git import, run names derived from the task, CLI replays) take a free suffix
- [x] CLI / Studio error paths (`WorkspaceNameTakenException`, a `VersionException`, shown by the Studio Banner)
- [x] Tests: `Core/WorkspaceNameTests` (7) and `ServerTests.WorkspaceNames_AreUniqueAmongOpenWorkspaces_AcrossClients`
- [x] Docs: `docs/reference.md` (workspace naming, garbage collection)

## Validation

- `dotnet test Bassia.Tests` with `BASSIA_TEST_POSTGRES` set, including new tests in the workspace, server and Studio
  suites.
- Manual: create `ws1` twice in Studio; the second attempt shows the error and the list has one `ws1`.

Result: the full suite passed (340 tests) against a local PostgreSQL 16. In Studio, against a fresh `bassia-server`,
the second `ws1` showed "A workspace named 'ws1' is already open (w686ffc8e68)." and the Workspaces list held one
`ws1`.
