# bassia-server requires a token by default

Status: done

## Outcome

Connecting to a `bassia-server`'s port no longer gets anyone in. A server started without `-token` generates its own
token instead of running open; browsers get a sign-in page, and API clients a 401, until they present it. Running open
takes an explicit `-no-auth`.

## Context

`ServerApp.AuthorizeAsync` lets every request through when `-token` is not given, and `bassia-local-cloud.ps1 serve`
does not give one, so the dashboard and the whole API at `:8080` are open to anything that can reach the port. With a
token, a browser sees a JSON 401 and signs in by putting the token in the URL (`?token=`), where it stays in the address
bar and history. Protecting the dashboard pages alone would not help: `/api/v1` serves the same data and takes writes.

## Acceptance criteria

- [x] Without `-token` or `-no-auth`, the server generates a random token on first start, keeps it in `<data>/token`
      (owner-only on Unix), reuses it on later starts, and logs the file's path but never the token.
- [x] Without a valid token: a browser opening a dashboard page is redirected to `/login`; the API, `/fragment` and
      non-browser requests get 401. `/healthz`, signed `/blobs` URLs and the stylesheet stay open.
- [x] `/login` is a form; the right token sets an HttpOnly, SameSite=Strict cookie (Secure over HTTPS) and redirects to
      the page asked for; a wrong one answers 401 and sets nothing; `next` cannot redirect to another site.
- [x] `?token=<token>` on a dashboard page still signs in, and redirects to the URL without the token.
- [x] `-no-auth` (or `BASSIA_SERVER_NO_AUTH`) runs open and logs a warning; together with `-token` it is refused.
- [x] `-token` behaves as before for clients, so the Azure deployment and the Docker smoke test need no change.
- [x] `bassia-local-cloud.ps1` works against the generated token: `serve`, `import`, `server-status`, `smoke`, and a new
      `server-token` that prints it for `bassia clone -token` and `bassia-studio -token`.

## Approach

The check stays in `AuthorizeAsync`, in front of the dashboard and the API. The token is resolved when the server
starts, not when options are parsed, so `gc` and `scrub` do not create one and code that builds `ServerOptions` directly
is closed by default too. Generated tokens are hex: the switch parsers treat a value starting with `-` as a new switch.

## Decisions

- Closed by default rather than refusing to start: `serve` keeps working with no setup, and no one is tempted to pass
  `-no-auth` just to get going.
- One token for the API and the dashboard, not a separate dashboard password: the API exposes the same data, and
  per-person identity is Studio's job (Entra ID).
- Not in scope: `bassia web` (a local process on 127.0.0.1 over the user's own files), rate limiting of sign-in (a
  generated token has 256 bits), sign-out, and TLS (put a proxy in front when listening beyond the machine).

## Progress

- [x] Server: token resolution, `-no-auth`, sign-in page.
- [x] Tests.
- [x] Script, docs and skills.

## Validation

- `dotnet test --filter "FullyQualifiedName~ServerAuthTests|FullyQualifiedName~ServerTests|FullyQualifiedName~Studio"`
  with `BASSIA_TEST_POSTGRES` set.
- `pwsh scripts/bassia-local-cloud.ps1 smoke`.
