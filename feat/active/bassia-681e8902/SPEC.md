# Bassia Studio: a web client for versions and agentic runs, deployed in Azure

## Outcome

People manage the cloud monorepo from a browser, the way they would with the desktop client of Subversion
(TortoiseSVN) or Perforce (P4V), but without installing anything: **Bassia Studio**, an ASP.NET Core web application
deployed in Azure next to `bassia-server`. It covers version management (browse the tree at any mainline seq, history,
diffs, workspaces, landing, conflicts, leases) and agentic run management (start, watch, stop, replay and land runs).
The reference deployment is Azure: Studio and `bassia-server` on Azure, objects in Blob Storage, state in Azure Database
for PostgreSQL, sign-in with Microsoft Entra ID; locally it runs against Azurite and PostgreSQL.

## Context

- `bassia-server` (`bassia-f245cd5f`) serves the HTTP API (`/api/...`) and a read-only dashboard (`Bassia/Web/Dashboard.cs`,
  `bassia-e636fe8f`) that deliberately changes nothing and points to `bassia` commands instead. Studio is the read-write
  counterpart.
- The Azure backing store exists (`bassia-323f4b4f`: Blob Storage + PostgreSQL, Entra ID for Blob Storage) and runs
  locally on Azurite/PostgreSQL (`bassia-d345202e`, `/bassia-local-cloud`, `/bassia-cloud-server`). There are no
  deployment templates yet (`bassia-323f4b4f` deferred them); `bassia-b9581afe` covers AWS.
- Agentic runs (`Bassia/Agents/AgentRunner.cs`) currently run on the machine of the `bassia` client that starts them:
  a workspace per run, the replay recipe on the working commit, the agent process in the workspace's folder. A
  browser cannot run an agent, so Studio needs runs to execute in the cloud (see Open questions).
- Version core rule: `Bassia.Core` reaches storage only through `IObjectStore`, `IViewStore`, `IPathIndex`,
  `ILeaseStore`. Studio uses the core through the remote stores (as a clone does) or the server API, never a cloud
  SDK directly.

## Acceptance criteria

Version management
- [x] **Repository browser**: a tree view of mainline at the latest or any chosen seq; open a file (text with syntax
      highlighting, binary as metadata and download); per-path history (the landings that touched it).
- [x] **Mainline log** (like SVN "Show log" / P4V "Submitted"): paged landings by seq with author, agent, intent,
      changed paths; a landing's diff (unified and side-by-side).
- [x] **Workspaces** (like P4V "Pending changelists"): list and filter; create on mainline, stacked on or cloned from
      another; view a workspace's changes against its base, its operation log, undo/restore to an operation, sync,
      describe, close/abandon.
- [x] **Edit in the browser**: edit, add, rename and delete files in a workspace (each save an implicit snapshot).
- [x] **Land**: land a workspace, showing the outcome (landed seq, replay requested, conflict with paths and policy).
- [x] **Conflicts**: list workspaces kept from landing and the paths in conflict; resolve per the path's policy
      (replay, take mine/theirs, edit a merged result).
- [x] **Leases** (like Perforce locks): list, acquire, release; an expired or foreign lease is shown and cannot be
      released by someone else unless they are an administrator.
- [x] Every action that changes the monorepo goes through the same core operations as the `bassia` CLI, so results
      are identical (verified by tests that do the same change from both).

Agentic runs
- [x] **Start a run**: prompt (or command), base (mainline or a workspace to stack on), model, load rules, leases,
      land-when-done; the run gets its own workspace.
- [x] **Watch runs**: list with status, live output streamed to the browser, changed paths, the resulting diff.
- [x] **Control runs**: stop, replay on the current mainline (same ChangeId), land, discard.
- [x] Runs execute in the cloud (see Decisions), not in the browser or on the user's machine; their logs are kept
      with the run.

Platform
- [ ] Sign-in with Microsoft Entra ID (OpenID Connect); locally a development sign-in or the server token.
- [x] Actions are recorded with the signed-in user as the author.
- [x] Live updates (runs, landings, leases) are pushed to open pages without reload.
- [ ] Deploys to Azure from versioned infrastructure code with one command; a documented local setup runs it on
      Azurite + PostgreSQL next to `bassia-server`.
- [x] Failure behavior: a server that is unreachable, a stale view (workspace head moved) and a rejected land are
      shown as clear messages, never as a lost edit.
- [ ] Tests run without a network or a cloud account (in-process server, local stores).

## Approach

1. New project `Bassia.Studio` (ASP.NET Core) that talks to `bassia-server` like a clone does: remote store
   implementations and the API, plus any missing write endpoints added to the server (workspace create/edit/land,
   runs) so the CLI and Studio share them.
2. UI: server-rendered pages with interactive parts (see Open questions for the UI stack), reusing the dashboard's
   styles and charts where they fit. Layout like a desktop VCS client: left tree (depot/workspaces), centre list
   (log/changes), bottom/right detail (diff, file, run output).
3. Run execution: a run service that starts agent workers in the cloud, records the run on its workspace, streams the
   log, and lands or requests a replay exactly as `AgentRunner` does; `AgentRunner` is refactored so both use the same
   steps.
4. Infrastructure as code for Azure and a deploy command; docs in `docs/reference.md` and README.
5. Deliver in slices (each a PR): (a) read-only browser + log + diffs, (b) workspaces + edit + land + conflicts +
   leases, (c) cloud runs, (d) Azure deployment + Entra ID.

## Decisions

User decisions (2026-11-05):
- **UI stack: Blazor Server** (interactive server components). The read-only dashboard stays as it is.
- **Runs execute as Azure Container Apps jobs**, one job execution per run; the agent's API key comes from Key Vault.
  Locally (and in tests) a process-based run host stands in for Container Apps behind the same interface.
- **Studio is a separate app** that talks to `bassia-server` over its API; **Terraform** deploys both.
- **Scope: all four slices**, one record, one PR per slice: (a) browse/log/diffs, (b) workspaces/edit/land/
  conflicts/leases, (c) cloud runs, (d) Azure deployment + Entra ID.

Slice (b):
- Until sign-in (slice d), Studio acts as one configured user (`-user`, default `studio`) with one role (`-role`,
  default `admin`, as a single local user). Contributors change only their own workspaces and leases.
- Studio has no workspace folder: edits write the working commit directly (the CLI snapshots a folder); the
  resulting trees are identical (parity test). Each save is checked against the version the user opened.
- Sync in Studio uses the repository policy without a semantic driver; mainline landing still uses the server's.

Slice (c):
- A run is the CLI's run: the job runs `bassia agent start|replay` in a fresh clone, so the workspace, recipe, run
  record, leases and landing are exactly the CLI's. Studio adds only launching, live output and stop.
- Live output goes from the job to Studio (`POST /runs/report/<id>` with a per-run token), not through the
  monorepo: an operation per output line would flood the workspace's log. The final log is still recorded on the
  run's workspace as before. Studio keeps live output in memory (one Studio replica; see slice d).
- Stop is cooperative (the answer to a report asks the run to stop, and it snapshots), with a kill after 30 s.
- On Container Apps an execution's template replaces the job's containers, so Studio adds the secret references
  (`-aca-secrets`, default server token and agent API key) itself; their values stay in the job (Key Vault).

Defaults chosen (not asked; revisit if wrong):
- Both apps run on Azure Container Apps (same environment as the run jobs), so Terraform manages one platform.
- Authorization by Entra ID app roles: `Reader` (browse), `Contributor` (workspaces, runs, land), `Admin` (others'
  leases, any workspace). Locally a development sign-in with a chosen role.

## Progress

- [x] Specification agreed.
- [x] Slice (a): `Bassia.Studio` (Blazor Server) over the remote stores - overview, depot browser at any seq with file
      content, download and per-path history, mainline log, landing diffs (unified and side by side), read-only
      workspace list and detail; unreachable server and missing paths as messages. `StudioTests` (8) green;
      checked in Chromium against a CLI-seeded `bassia-server` (interactive circuit, no console errors).
- [x] Slice (b): `StudioActions` over `WorkspaceService`, the landing service and the lease store - create (on
      mainline, stacked, cloned), describe, edit/add/rename/delete files (each a snapshot; refused without loss when
      the path changed since it was opened), check and land (with the owner's leases), sync, undo, restore, close and
      abandon; stored conflicts resolved by keeping a side or saving merged text; leases acquired and released;
      reader/contributor/admin roles (`-user`, `-role` until sign-in). Pages: new workspace, workspace toolbar and
      conflicts, workspace files and editor, conflicts overview, leases. 9 new tests (actions, stale edits, conflict
      resolution, undo/restore, stack/clone, roles, leases, CLI parity, pages); full flow driven in Chromium.
- [x] Slice (c): `bassia agent job` (clone the server, then `agent start|replay` from `BASSIA_JOB_ARGS`) and a run
      reporter in the CLI (`BASSIA_REPORT_URL`/`TOKEN`: output posted in batches, final status, stop on request);
      Studio's `RunService` over an `IRunHost` - `LocalProcessRunHost` and `ContainerAppsJobRunHost` (one job execution
      per run, the job's secrets as secret references); token-checked report endpoint; pages Runs, New run, Run
      (live output, stop, land, replay on mainline, discard), with links from workspaces and conflicts. 9 run tests with
      real job processes (land, live output, stop, replay with the same change, failure, report auth, permissions,
      arguments, the Azure execution template); driven in Chromium. A real Azure job execution waits for slice (d).
- [ ] Slice (d): Terraform for Azure (Container Apps, Blob Storage, PostgreSQL, Key Vault, Entra ID), sign-in and roles.

## Validation

- `dotnet test` (new Studio tests: in-process server + Studio, page and action tests, CLI/Studio parity).
- `/bassia-local-cloud smoke` extended: Studio against Azurite + PostgreSQL, browse, edit, land, start a run.
- Playwright smoke of the main pages (screenshots for review).
- Deployment validated with the IaC tool's what-if/plan; an actual Azure deployment only with an account.
