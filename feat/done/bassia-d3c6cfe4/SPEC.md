# Dashboard for many users and server metrics

Phase 1 follow-up of `bassia-f245cd5f` (Bassia server), split from it. (User decision: two deliveries.)

## Outcome

The Bassia server serves the dashboard to every user, showing only what they may read, with live updates of runs,
locks and the merge queue pushed to the browser; and it reports its health in metrics an operator can graph and alert
on.

## Context

`bassia web` (`Web/Dashboard*.cs`) is a single-user, loopback-only, read-only dashboard over a monorepo on disk that
polls for changes. `bassia-f245cd5f` adds the server, its permissions and the remote backend, but not the dashboard
for many users or metrics.

## Acceptance criteria

- [x] The server serves the dashboard to authenticated users (OIDC or GitHub sign-in in the browser), filtered by
      their permissions: a component they may not read appears nowhere - overview, graph, components, runs, locks,
      tags, merge queue.
- [x] Live updates (runs starting and finishing, run output, locks, the merge queue) are pushed over server-sent events
      or WebSocket, across server instances.
- [x] Metrics in OpenTelemetry format: request rate and latency per endpoint, queue depths (merge queue), lock
      decisions and conflicts, git traffic (fetches, pushes, bytes), database and storage latency.
- [x] Tests for filtering and live updates; docs in `docs/reference.md`.

## Approach

- **Pages.** The dashboard (`Bassia/Web`) gets its monorepo per request rather than from one root. `bassia web` still
  loads the monorepo from disk behind its loopback guard. The server mounts the same pages at `/` and gives each
  request a `Monorepo` over a read-only backend (`DashboardBackend`). That backend is the server's own stores, limited
  to what the caller may read:
  - the registry without unreadable components or references to them;
  - histories of readable components only;
  - records, lock entries and jobs only when every component they name is readable.

  Writes through it are refused. Filtering by data (not by page) means every page, the component graph included,
  shows only what the caller may read.
- **Sign-in.** `/login` lists the identity providers. `/login/<provider>` runs the authorization-code flow with PKCE
  and a state value kept in a short-lived cookie:
  - **OIDC:** the provider's `client_id`, and `client_secret` when it has one. The access token (or else the id
    token) is checked as the API's bearer tokens are.
  - **GitHub:** the web flow, which needs the OAuth app's `client_secret`. The account maps to a user as on
    `/auth/github`.

  The callback sets an HttpOnly, SameSite=Lax session cookie (Secure over https) holding the server's own token, with
  the identity provider's groups, for `session_hours` (default 12). There is no refresh token, which avoids
  concurrent page requests racing to redeem one. Every request is still checked against the registry and the grants.
  After the token expires the browser signs in again, usually without a prompt while the identity provider's session
  lasts. `/logout` clears the cookie. Redirect URLs are `<public_url>/login/<provider>/callback`. Unauthenticated page requests
  redirect to `/login`. A bearer token also works, for scripts and tests.
- **Live updates.** A `changes` table (sequence id, kind, subject, components, time) is written by the stores:
  record versions, lock decisions, job registration, output and finish, and landings. Browsers get only the kind of a
  change, never its subject. Each server
  instance polls it about once a second, re-reading a window behind the newest id so rows that commit late are not
  missed. It hands new rows to the browsers connected to that instance over server-sent events (`/events`). A change
  reaches a browser only when the viewer may read every component it names. The script refreshes the parts of the
  page the change concerns, with slow polling as the fallback. Old rows are pruned.
- **Metrics.** A `Bassia.Server` meter (`System.Diagnostics.Metrics`), plus ASP.NET Core's own request metrics, cover:
  - request duration by method, route and status, which gives request rate and latency per endpoint;
  - merge-queue depth (landings holding a slot or waiting for one);
  - lock decisions by result (committed or conflict) and lock waits;
  - git requests by service and result, and bytes in and out;
  - database duration by operation;
  - history storage duration by operation (S3 and DynamoDB in cloud mode).

  The OpenTelemetry OTLP exporter sends them when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. In AWS, the server task gets
  an ADOT collector sidecar that writes them to CloudWatch as embedded metrics (namespace `Bassia`). The
  observability stack graphs them and alarms on lock conflicts and on queue depth.

## Decisions

- Split from `bassia-f245cd5f` so the server and its clients ship first. (User decision.)
- Browser sign-in: the authorization-code flow run by the server (OIDC with an optional client secret, the GitHub web
  flow), with an HttpOnly session cookie holding the server's tokens. (User decision.)
- Live updates: server-sent events fed from a change table in the database, polled by every instance. This works the
  same on SQLite and PostgreSQL and needs no extra infrastructure. (User decision.)
- Metrics: `System.Diagnostics.Metrics` with the OTLP exporter. In AWS, an ADOT collector sidecar writes to
  CloudWatch, and the observability stack graphs the metrics and alarms on them. (User decision.)
- The server dashboard stays read-only like `bassia web`: every page shows the command that does what it describes.
  Actions such as stopping a run or landing belong to a later record. (User decision.)

## Progress

- [x] Dashboard behind sign-in, filtered
- [x] Live updates
- [x] Metrics
- [x] Tests and docs

## Validation

Results: `dotnet test --filter "Category!=EndToEnd"` gave 675 passed and 1 skipped (pre-existing). The server tests
(`Category=Server`) on PostgreSQL 16 gave 21 passed. In `deploy/aws`, `npm test` gave 23 passed, and `cdk synth` ran
for production and staging. A server started with `OTEL_EXPORTER_OTLP_ENDPOINT` delivered `/v1/metrics` to a local
OTLP receiver every interval, with the `bassia.*` metrics in it. Not run here: the collector in AWS (no account in the
build environment). The first staging run of `bassia-b9581afe` covers it.

```sh
dotnet test --filter "Category!=EndToEnd"
cd deploy/aws && npm test && npx cdk synth -c env=staging
```
