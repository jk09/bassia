# Bassia server and remote backend

Phase 1, part 3 of 4 of hosting Bassia as a scalable service. Depends on `bassia-e13e1548` (shared-state contracts)
and `bassia-ce722c6c` (S3 and DynamoDB storage for histories); deployed by `bassia-b9581afe` (AWS). The dashboard for
many users and the metrics follow in `bassia-d3c6cfe4`. (User decision: two deliveries.)

## Outcome

`bassia-server` runs Bassia as a multi-user service: it owns a monorepo (registry, records, locks, jobs, task cache and
the component histories through a storage backend), authenticates every caller, and enforces who may read and change
which component. Clients - people's CLIs and cloud agent workers - log in with `bassia login`, connect with
`bassia clone -from https://<server>` and from then on work as today: views, sparse runs, locks, run records,
integration and landing all go through the server, so any number of users and agents share one monorepo and see each
other's work.

## Context

`bassia-f66364b5` gave clones, views, sparse runs and write-through against a depot that is a plain git host: a clone's
locks, jobs and run records stay on the clone, so two workers cannot see each other's holds or runs, and there is no
authentication beyond the git host's. `bassia-e13e1548` put every piece of that state behind a contract, which this
record implements twice: on the server (durable, shared) and as a remote client of the server (`Storage/Remote`).
`bassia-ce722c6c` added the cloud history storage and a git smart-HTTP gateway (`GitGateway`) without authentication.

## Acceptance criteria

### Server

- [x] `bassia-server -config <file>` (project `Bassia.Server`) starts a stateless HTTP server - several instances can
      run behind a load balancer when the histories are in the cloud storage - exposing the contracts of
      `bassia-e13e1548` as a versioned API (`/api/v1/...`), plus the git gateway for every component
      (`/git/<component>`) and a git view of the meta-repo (`/git/.bassia`), so plain `git clone` and Bassia's partial
      clones work against it. Health (`/healthz`) and readiness (`/readyz`) endpoints; structured (JSON) request logs.
- [x] Server-side state lives in a SQL database: run and integration records (`IRecordStore`), lock entries
      (`ILockStore`, one lock decision per transaction, compare-and-swap from clients), jobs (`IJobScheduler`, with
      their output), the task cache index (`ITaskCache`, blobs in object storage or a folder), identities, grants,
      groups, refresh tokens, idempotency keys and the audit log. PostgreSQL and SQLite (single instance, tests) are
      provided, with versioned schema migrations.
- [x] The registry and the monorepo settings are a meta-repo history kept by the history storage, changed by
      compare-and-swap on its branch; component histories are reached through `IComponentStorage`: the S3 and
      DynamoDB backend of `bassia-ce722c6c` in production, `Storage/Git` on a local monorepo for development and tests.
- [x] Concurrency: run branches and tags never contend; ref updates are compare-and-swap; landing on components'
      main branches goes through a merge queue serialized per component, so landings on different components proceed
      in parallel; a landing that moves several components does so atomically or not at all.
- [x] One organization per deployment: no tenant key anywhere.

### Identity and permissions

- [x] Every request is authenticated. OIDC (any standards-compliant provider: discovery, JWT validation; authorization
      code + PKCE for `bassia login`, client credentials for workers) and GitHub (device flow for `bassia login`; the
      server checks the GitHub token and issues its own). Server tokens are short-lived and refreshable. The caller is
      mapped to a registered user (`users.toml`) by a stored link to the provider's subject, made on first login by
      the verified email. (User decision: OIDC + GitHub OAuth.)
- [x] Permissions per component: `read`, `run` (start runs and push run branches and tags), `integrate` (land on its
      main branch, push other tags), `admin` (change its registration); each implies the ones before it. Granted to
      users, groups (the server's own and the OIDC `groups` claim) and everyone; owners (`owners` in the registry) hold
      `integrate`; server admins (configured) hold everything. A user sees only the components they may read - in the
      registry their clone gets, `component list`, the graph, views, `log`, run and lock listings and git - and a
      view or run mapping a component they may not read is refused before anything is materialized.
- [x] A worker token is scoped to the components it names, `run` at most, and expires (`bassia token create`).
- [x] Every change is audited (who, what, when, from where) in an append-only log readable by admins
      (`bassia audit`).

### Clients

- [x] `Storage/Remote` implements the contracts against the API, with retries, idempotency keys for writes and clear
      errors when the server cannot be reached; `bassia clone -from https://<server>` selects it; `bassia login`
      stores the credential per server (the OS keychain where available, else a private file) and git gets it
      through `bassia credential` (a git credential helper).
- [x] In a clone of a server, locks, jobs, run records and integrations are the server's: `lock list`, `agent list`,
      `agent show/logs/wait/stop`, `integration ...` show and act on everyone's work; a run's output is uploaded as it
      goes, so `agent logs` follows a run started on another machine.
- [x] Agentic runs execute on the client that starts them; the server records, coordinates and stores.

### Docs and tests

- [x] `docs/reference.md` has a "Bassia server" section (running it, configuration, login, clone from a server,
      permissions, the API); help covers the new commands.
- [x] Tests (`Category=Server`): the contract suites run against the remote backend talking to an in-process server
      (SQLite); end-to-end tests run two clients against one server and check that holds, runs, records and
      integrations are shared and that permissions are enforced (a user without `read` sees nothing of a component,
      a worker cannot touch a component outside its token).

## Approach

`Bassia.Server` (an ASP.NET Core app referencing the Bassia project, so it reuses Core: one implementation of runs,
integration and locking) hosts endpoint groups per contract over a `ServerBackend`: the history storage (git or cloud),
the meta-repo history, and database stores for records, locks, jobs and the task cache. The git gateway of
`bassia-ce722c6c` is generalized over the history storage and given an authorization hook; the meta-repo is served
per user, filtered to the components they may read. `Storage/Remote` is a `RemoteBackend` for clones: the histories
stay git partial clones of the gateway (as in a depot clone), the registry is read from the clone's meta-repo and
changed through the API, and every stateful contract is an HTTP client. Lock decisions run on the client as today and
are written back with compare-and-swap on the lock table's version.

## Decisions

- Agents run on clients in this phase (a person's machine, or workers the operator runs); the server coordinates.
- The server reuses Core; there is one implementation of runs, integration and locking.
- The component is the unit of permission, as the architecture intends.
- One organization per deployment. (User decision.)
- Histories in S3 and DynamoDB from the start (`bassia-ce722c6c`). (User decision.)
- Scale target of the first deployment: ~1,000 users, ~500 concurrent agents, thousands of components. (User decision.)
- Identity providers on day one: any OIDC provider plus GitHub. (User decision.)
- The load test is sized in staging against real data (`bassia-b9581afe`), with the targets of `bassia-ce722c6c`.
  (User decision.)
- Two deliveries: this record; the dashboard for many users with live updates and the metrics in `bassia-d3c6cfe4`.
  (User decision.)
- The server is its own project, `Bassia.Server`, referencing the Bassia project rather than a library split out of it
  first. (User decision.) Its executable is `bassia-server` (not `bassia serve`), with `token`, `migrate` and `check`
  beside running.

Made during implementation:

- The registry and settings are not database tables but the meta-repo, kept by the history storage: the monorepo's own
  `.bassia` with git storage (one instance), a `.bassia` history in the cloud storage otherwise, changed through a
  working copy per instance and published as a fast-forward (compare-and-swap; a lost race is applied again). Core's
  meta-repo code and file formats stay the one implementation, and the gateway serves the meta-repo as it is.
- A user gets the meta-repo as a view: a single commit per revision and set of readable components (the registry
  without the others and without references to them), dated and authored as the revision so every instance makes the
  same commit. A history would show the whole registry. Clones reset to it on fetch instead of merging.
- Lock decisions stay on the client, as on every backend; the server's lock table has one version, and a decision is
  written only while the table is still at the version it was read at (`POST /locks`), else made again. One version
  for the whole table keeps decisions about overlapping paths of different components simple and correct; whether it
  contends at 500 agents is for the staging load test (`bassia-b9581afe`).
- A job registers before its run record exists; its components (for who may see it) are those of the run or
  integration record of the same id.
- A clone of a server keeps git partial clones of the gateway for histories (as a depot clone does); a branch or tag
  it lacks is fetched from the server when asked for, published working copies write through, and moving default
  branches is a landing on the server. Splitting a component from a clone is refused for now.
- Configured admins can act before they are registered users: a new cloud server's meta-repo has no users.
- Worker tokens are scoped to components and expire (at most a day); they are not tied to one run's id, which does not
  exist when the token is made.
- Keychains: macOS `security`, the Secret Service (`secret-tool`) and the Windows Credential Manager, falling back to a
  private file; the tests use the file (`BASSIA_CREDENTIAL_STORE=file`).

## Progress

- [x] Server skeleton: configuration, database and migrations (SQLite, PostgreSQL), health, authentication
- [x] Database stores: records, locks, jobs, task cache; the meta-repo history and per-user views
- [x] API endpoints, idempotency, audit; git gateway with authorization over git or cloud storage
- [x] Permissions, groups, worker tokens, merge queue and landing
- [x] `Storage/Remote`, `login`, `logout`, `credential`, `token create`, `access ...`, `audit`, clone and fetch from a server
- [x] Docs ("Bassia server" in docs/reference.md); contract and two-client tests - `Category=Server` and the lock
      contract pass on SQLite and on PostgreSQL 16 (`BASSIA_SERVER_TEST_POSTGRES`); CI runs them on SQLite

## Validation

```sh
dotnet test --filter "Category!=EndToEnd"
dotnet test --filter "Category=Server"         # contract suites over the remote backend, two-client scenarios
BASSIA_SERVER_TEST_POSTGRES="Host=127.0.0.1;Username=bassia;Database=postgres" \
  dotnet test --filter "Category=Server|FullyQualifiedName~BackendContractTests.LockStore"   # the same on PostgreSQL
```

Plus a manual check: two clones of one local server, a run started in each holding overlapping paths: each sees the
other's hold, `lock wait` brings the first result into the second, and a user without `read` on a component cannot
list, clone or map it.
