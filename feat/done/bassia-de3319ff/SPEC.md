# bassia-server: a Web API only, no dashboard

## Outcome

`bassia-server` serves only its HTTP API (`/api/v1/...`), the signed `/blobs` URLs and `/healthz`. The read-only HTML
dashboard it used to serve at its root, and the browser sign-in that came with it, are gone: Bassia Studio is the web UI
for a server, and the API is for Studio, `bassia` clients and other clients, deployed in the cloud or locally -
including browser applications on another origin, which the server can allow with CORS.

## Context

- The server mounted `Bassia/Web/Dashboard.cs` (`bassia-e636fe8f`) at `/`, with a `/login` form, a `bassia_token`
  cookie, `?token=` on page URLs and an `/events` alias of the watch stream for the dashboard's live refresh
  (`bassia-edb66a7f`). Bassia Studio (`bassia-681e8902`) is the read-write web client and makes the server's pages
  redundant.
- Studio calls the server from its own process with `Authorization: Bearer <token>`; so do the clients' remote stores.
  Neither uses the cookie, the query token, `/events` or any dashboard page.
- `bassia web` (the local, loopback-only dashboard over a local monorepo or a clone) is a CLI command, not part of the
  server; this change leaves it alone.

## Acceptance criteria

- [x] The server serves no HTML: `/mainline`, `/workspaces`, `/fragment/live`, `/style.css`, `/app.js`, `/login` and
      `/events` are not served - a JSON 404 with the token, a JSON 401 without it (the server stays closed and does
      not reveal which paths exist).
- [x] `GET /` answers, without a token, a small JSON description (`service`, `version`, `api` prefix, `health`) and
      nothing about the monorepo, so a person or probe opening the server's URL learns it is an API.
- [x] Authentication is the bearer token only: a cookie or a `?token=` query parameter does not authenticate. Without a
      valid token every API call is 401 with `WWW-Authenticate: Bearer` and a JSON error; `/healthz`, `/` and signed
      `/blobs` URLs need none. `-no-auth` still serves everyone.
- [x] Unknown paths get a JSON 404 (`{"error": ...}`), as API errors do - file-like paths (`/style.css`) included.
- [x] `-cors-origin <origin>[,<origin>...]` (`BASSIA_SERVER_CORS_ORIGIN`) lets browser clients on those origins call the
      API: preflight `OPTIONS` requests succeed without a token, and replies carry
      `Access-Control-Allow-Origin` for listed origins only. Without the switch the server sends no CORS headers.
- [x] Studio, clones, the watch stream (`/api/v1/watch`) and every existing API test keep working.
- [x] Docs (`docs/reference.md`, `README.md`, deploy READMEs, the local-cloud script and skill) describe the server as an
      API and Studio as its web UI.

## Approach

- `ServerApp`: drop the dashboard middleware and mapping, `SignIn.cs`, the cookie and query-token paths, the `/events`
  alias and HTML/CSS/JS compression types; add the root descriptor, a JSON fallback 404 and opt-in CORS (ASP.NET Core's
  CORS middleware, ahead of authorization so preflights pass).
- `ServerOptions`: `-cors-origin`; usage text and help updated.
- Tests: replace the dashboard and sign-in tests with tests of the API-only surface, bearer-only auth and CORS.

## Decisions

- **`bassia web` stays.** The request is about the server's web part; the local dashboard is a CLI command over a local
  monorepo or clone, with no server or Studio needed. Removing it is a separate decision.
- **No cookie or query token.** They existed only for browsers on the dashboard; a browser client of the API (via CORS)
  sends the bearer token like any other client, and a query token would leak into logs and history.
- **CORS is opt-in, by explicit origins.** No wildcard: the API takes a bearer token that can change the monorepo.
- **`/` answers without a token** with only static facts (the service name, version, API prefix), like `/healthz`.

## Progress

- [x] Server: API-only surface, bearer-only auth, root descriptor, JSON 404, CORS.
- [x] Tests updated and passing against a local PostgreSQL.
- [x] Docs, scripts and skill updated.

## Validation

- `BASSIA_TEST_POSTGRES=... dotnet test Bassia.Tests --filter "FullyQualifiedName~Server"` (ServerAuthTests,
  ServerTests and the server-backed tests), then the full suite.
