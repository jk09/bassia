# Bassia server and remote backend

Phase 1, part 3 of 4 of hosting Bassia as a scalable service. Depends on `bassia-e13e1548` (shared-state contracts)
and `bassia-ce722c6c` (S3 and DynamoDB storage for histories); deployed by `bassia-b9581afe` (AWS).

## Outcome

`bassia serve` runs Bassia as a multi-user service: it owns a monorepo (registry, records, locks, jobs, task cache and
the component histories through a storage backend), authenticates every caller, enforces who may read and change which
component, and serves the dashboard to many people. Clients - people's CLIs and cloud agent workers - connect with
`bassia clone -from https://<server>` and from then on work exactly as today: views, sparse runs, locks, integration
and the dashboard all go through the server, so any number of users and agents share one monorepo and see each other's
work live.

## Context

`bassia-f66364b5` gave clones, views, sparse runs and write-through against a depot that is a plain git host: a clone's
locks, jobs and run records stay on the clone, so two workers cannot see each other's holds or runs, and there is no
authentication beyond the git host's. `bassia-e13e1548` puts every piece of that state behind a contract, which this
record implements twice: on the server (durable, shared) and as a remote client of the server (`Storage/Remote`).
`bassia web` already is an ASP.NET Core app (`Web/Dashboard*.cs`), running in-process jobs ("hosted" jobs).

## Acceptance criteria

### Server

- [ ] `bassia serve -config <file>` starts a stateless HTTP server (several instances can run behind a load balancer)
      exposing the contracts of `bassia-e13e1548` as a versioned API (`/api/v1/...`), plus the git gateway of
      `bassia-ce722c6c` for every component (`/git/<component>`) and a git view of the meta-repo, so plain `git clone`
      and Bassia's partial clones work against it.
- [ ] Server-side state lives in a database through the contracts: registry and configuration (`IMetaStore`,
      `IConfigStore`), run and integration records (`IRecordStore`), lock entries (`ILockStore`, one lock decision per
      transaction), jobs (`IJobScheduler`), task cache index (`ITaskCache`, blobs in object storage). A PostgreSQL
      backend is provided; SQLite backs single-instance and test servers.
- [ ] Component histories are reached through `IComponentStorage` on the server: the S3 and DynamoDB backend of
      `bassia-ce722c6c` in production, `Storage/Git` on a local folder for development and tests.
- [ ] Concurrency: run branches and tags never contend; ref updates are compare-and-swap (`SetBranchAsync` with
      `expected`); landing results on a component's main branch is serialized per component (a merge queue), so
      integrations of different components proceed in parallel; an integration that advances several components does
      so atomically or not at all (the multi-component ref transaction of `bassia-ce722c6c`).
- [ ] One organization per deployment: no tenant key anywhere; another organization is another deployment.

### Identity and permissions

- [ ] Every request is authenticated: OIDC (authorization code + PKCE for the CLI's `bassia login`, client credentials
      for workers); tokens are short-lived and refreshable. `IIdentity` on the server is the token's subject, mapped to a
      registered user (`users.toml` becomes the server's user table, linked to the identity provider's subject).
- [ ] Permissions per component: `read`, `run` (start runs that change it), `integrate` (land on its main branch),
      `admin`; granted to users and groups, with owners (`owners` in the registry) holding `integrate`. A user sees only
      the components they may read - in `component list`, the graph, views, `log`, the dashboard and git - and a run or
      view mapping a component they may not read is refused before anything is materialized.
- [ ] A worker's credential is scoped to one run: the components it selected, `run` only, expiring with the run.
- [ ] Every change is audited (who, what, when, from where) in an append-only log readable by admins.

### Clients

- [ ] `Storage/Remote` implements every contract against the API, with retries, idempotency keys for writes and clear
      errors when the server cannot be reached; `bassia clone -from https://<server>` selects it, and `bassia login`
      stores the credential per server (OS keychain where available).
- [ ] In a clone of a server, locks, jobs, run records and integrations are the server's: `lock list`, `agent list`,
      `agent show/logs/wait/stop`, `integration ...` show and act on everyone's work; a run started on a worker is
      visible (live output included) to everyone allowed to read its components.
- [ ] Agentic runs execute on the client that starts them (a person's machine or a worker the operator provides); the
      server records, coordinates and stores. Running agents on server-provided compute is a later record.

### Dashboard, operations, docs

- [ ] The dashboard is served by the server for all users, filtered by their permissions, with live updates (runs,
      locks, merge queue) pushed over WebSocket or server-sent events.
- [ ] Health and readiness endpoints, structured logs, metrics (request rate and latency, queue depths, lock decisions,
      git traffic) in OpenTelemetry format.
- [ ] `docs/reference.md` has a "Bassia server" section (serve, login, clone from a server, permissions, the API);
      help covers the new commands.
- [ ] Tests: the contract suites of `bassia-e13e1548` run against the remote backend talking to an in-process server
      (SQLite); end-to-end tests run two concurrent clients against one server and check that holds, runs, records and
      integrations are shared and that permissions are enforced (a user without `read` sees nothing of a component, a
      worker cannot touch a component outside its run).

## Approach

Host the contracts in an ASP.NET Core app (`Bassia.Server`, or a `serve` mode of the CLI sharing Core) with
endpoint groups per contract; the request handlers call the same Core code the CLI runs (`MonorepoGraph`, `AgentRuns`
records, `IntegrationRunner`, `LockSession`), so behavior cannot drift. Add a database backend for the stateful
contracts (EF Core or Dapper; migrations versioned with the server). Put the git smart-HTTP endpoints in front of the
component storage, authorizing per component. Write `Storage/Remote` as typed HTTP clients generated from (or checked
against) the API description. Merge queues and multi-component landing build on the integration's existing advance
step, with ref updates in one database transaction when several components move.

## Decisions

- Agents run on clients in this phase (a person's machine, or workers the operator runs, e.g. on Fargate); the server
  coordinates. Server-scheduled sandboxes come later.
- The server reuses Core; there is one implementation of runs, integration and locking.
- The component is the unit of permission, as the architecture intends.
- One organization per deployment. (User decision.)
- Histories in S3 and DynamoDB from the start (`bassia-ce722c6c`). (User decision.)
- Scale target of the first deployment: ~1,000 users, ~500 concurrent agents, thousands of components. (User decision.)

## Open questions

- Which identity providers must work on day one (Cognito, IAM Identity Center, Okta, Entra ID, GitHub)?
- Largest component (files, history size) and peak pushes per second, to size the load test.

## Progress

- [ ] API and server skeleton (health, auth, one contract end to end)
- [ ] Database backends for the stateful contracts; git smart-HTTP
- [ ] Permissions and audit
- [ ] `Storage/Remote`, `login`, clone from a server
- [ ] Merge queues and atomic multi-component landing
- [ ] Dashboard for many users with live updates
- [ ] Docs; contract and two-client end-to-end tests

## Validation

```sh
dotnet test --filter "Category!=EndToEnd"
dotnet test --filter "Category=Server"         # contract suites over the remote backend, two-client scenarios
```

Plus a manual check: two clones of one local server, a run started in each holding overlapping paths: each sees the
other's hold, `lock wait` brings the first result into the second, and a user without `read` on a component cannot
list, clone or map it.
