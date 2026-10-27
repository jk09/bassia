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

- [ ] The server serves the dashboard to authenticated users (OIDC or GitHub sign-in in the browser), filtered by
      their permissions: a component they may not read appears nowhere - overview, graph, components, runs, locks,
      tags, merge queue.
- [ ] Live updates (runs starting and finishing, run output, locks, the merge queue) are pushed over server-sent events
      or WebSocket, across server instances.
- [ ] Metrics in OpenTelemetry format: request rate and latency per endpoint, queue depths (merge queue), lock
      decisions and conflicts, git traffic (fetches, pushes, bytes), database and storage latency.
- [ ] Tests for filtering and live updates; docs in `docs/reference.md`.

## Approach

Reuse the dashboard's pages with a permission filter over the data they read; push changes from the database stores
(a change feed) to connected browsers; instrument the server with `System.Diagnostics.Metrics` and the OpenTelemetry
exporter.

## Decisions

- Split from `bassia-f245cd5f` so the server and its clients ship first. (User decision.)

## Progress

- [ ] Dashboard behind sign-in, filtered
- [ ] Live updates
- [ ] Metrics
- [ ] Tests and docs

## Validation

```sh
dotnet test --filter "Category=Server"
```
