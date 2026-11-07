# Remove `bassia web`: Bassia Studio is the only web UI

## Outcome

The `bassia` CLI has no `web` command and no built-in dashboard. Bassia Studio is the one web UI for a monorepo; the CLI
is the command line only.

## Context

- `bassia-de3319ff` removed the dashboard from `bassia-server` and kept `bassia web`, the read-only dashboard over a
  local monorepo or a clone on 127.0.0.1 (`Bassia/Web`: `Dashboard`, `Html`, `Charts`; `CliCommands/WebCommand`). The
  user decided Studio covers it as well.
- Studio (`bassia-681e8902`) connects to a `bassia-server`; a local monorepo made by `bassia init` has no server, so
  after this change it has no browser view. To see one in a browser, run `bassia-server` over it and point Studio at it.

## Acceptance criteria

- [x] `bassia web` is no longer a command: it exits with code 2 and an error that points to Bassia Studio, and it is
      gone from `bassia help` and the command catalog.
- [x] `Bassia/Web` and `WebCommand` are deleted, and the solution builds.
- [x] The README and `docs/reference.md` describe Studio as the web UI; the dashboard's screenshots are replaced by
      Studio's or removed; no doc, skill or instruction mentions `bassia web`.
- [x] The full test suite passes.

## Approach

Delete the command, its catalog entries and the `Web` folder; update CLAUDE.md, the
README, the reference and the cloud-server skill; replace the dashboard images in the README with Studio's.

## Decisions

- **Removed outright, no deprecation period.** The user asked for it, and it was read-only: nothing depended on it.
- **No browser view for a server-less local monorepo.** Studio needs a server; that is accepted.
- **`bassia web` and `bassia ui` answer with a pointer to Studio** (the retired-command hints), exit code 2.
- **The CLI keeps its `Microsoft.AspNetCore.App` framework reference** though no ASP.NET Core code is left. `Bassia.dll`
  is also started from Studio's folder (its local run host) and the tests' folder, where `Microsoft.Extensions.*` come
  from that shared framework; without it, `agent start` there fails to load `Microsoft.Extensions.Logging.Abstractions`
  (seen in `StudioRunTests` and `NewMonorepoTests`).

## Progress

- [x] Command and dashboard code removed.
- [x] Docs, skill, CLAUDE.md and images updated.
- [x] Build and full test suite pass.

## Validation

- `dotnet build Bassia.slnx`; `BASSIA_TEST_POSTGRES=... dotnet test Bassia.Tests`.
- `bassia web` exits 2 with the pointer to Studio (`CliTests.Status_Help_AndReplacedCommands`).
