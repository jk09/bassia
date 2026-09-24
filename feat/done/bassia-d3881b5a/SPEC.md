# Web dashboard: a local web counterpart of `bassia ui`

## Outcome

`bassia web` starts a local web server over the monorepo it is run in and serves a dashboard in the spirit of
Fossil's built-in web interface. Every page is plain server-rendered HTML with a top menu, readable without
JavaScript. The dashboard shows:

- the components, with the dependency graph drawn by the existing SVG export;
- agentic runs, live and finished;
- integrations (merge resolution) with their triage and steps.

It can start agentic runs from a Claude-like prompt and watch them stream. It also shows a **timeline**, a
combined git log of a chosen set of components treated as one unit, with dependent components included
automatically. The timeline is never the log of the whole monorepo.

## Context

`bassia ui` ([bassia-4f1c8e73](../../done/bassia-4f1c8e73/SPEC.md),
[bassia-f552a672](../../done/bassia-f552a672/SPEC.md)) is a terminal cockpit over the same model: components and
their refs, the `.agentic-runs` records, background runs through `RunSupervisor`, integrations through
`IntegrationRunner`/`IntegrationSupervisor`. A browser gives more room than a terminal for the things that grow:
the component graph, run transcripts, integration steps and history across components.

Fossil's web UI (`fossil ui`) is the model: a single executable serves a local site with a top menu (Home,
Timeline, Files, Branches, Tags, …), and the **timeline** is the centrepiece: one chronological list of check-ins,
each linked to its detail.

Constraints:

- The scriptable CLI and `bassia ui` stay unchanged; the web layer reads through the same model (`Monorepo`,
  `GitRef`, `RunMetadataStore`, `IntegrationStore`, `IntegrationPlanner`) and mutates through the same code paths
  (`AgentCommand.StartRunAsync`, `IntegrationRunner`), never a parallel implementation.
- A monorepo may be large: the timeline covers only a chosen unit of components and is paged. Nothing ever walks
  every component's full history.
- No new persistent state: everything shown is derived from `components.toml`, the component repos and
  `.agentic-runs`.

## Acceptance criteria

- [x] `bassia web [--port <n>] [--no-open]` starts the dashboard on `http://127.0.0.1:<port>/` (default port
      8080; the next free port if that is taken), prints the URL, opens the browser unless `--no-open` is given,
      and stops on Ctrl-C, stopping any runs it started (they are recorded `cancelled`, as in `bassia ui`). Outside
      a monorepo it fails with the usual TOML error and exit code 1.
- [x] The server listens on the loopback interface only.
- [x] A top menu on every page links to the Home, Components, Timeline, Runs, Integrations and New run pages.
      Every page renders as plain HTML without JavaScript; JavaScript only adds live updates.
- [x] **Home**: counts of components, live runs and runs by status, the latest integrations, and the component
      graph as the existing SVG (`ComponentGraph.ToSvg`), whose boxes link to the component pages.
- [x] **Components**: every registered component with its URL, references, referrers, annotated tags, branches and
      recorded runs. A component page shows its branches and tags (annotated ones marked), its runs with their
      result tags, and a link to its timeline.
- [x] **Timeline**: choose components with checkboxes; the unit is the choice plus the components it depends on
      (the closure used by `-select`). The page shows one chronological list of commits across the unit: date,
      component, short hash, subject, author, refs, with agent/integration tags linked to their run/integration.
      It is paged (default 50 per page). Each component's log is read with a limit, so the cost depends on the
      page, not on history size. With no choice it asks for one rather than logging everything. A commit links to
      a detail page with its full message and diffstat.
- [x] **Runs**: every run, live ones first, each with status, components, rationale and elapsed time. A run page
      shows the record, per-component results and, for a live run, the agent's output streaming as it happens. A
      live run can be stopped from it.
- [x] **New run**: a Claude-like prompt page. Choose components (the closure is completed) and an annotated tag
      for each, write the prompt in a chat-style box, and optionally pick the model and effort. The composed command
      is shown and can be edited. Submitting starts the run in the background, as `bassia ui` does, and goes to its
      live page.
- [x] **Integrations**: every integration with status, runs and result tags, each linked to a page with its steps
      (triage, strategy, conflicts, outcome, merge commit) and brief paths. A triage form previews the plan for
      chosen runs, like `integrate -plan`.
- [x] A git or model failure is shown as an error page or message, and the server keeps serving.
- [x] Tests cover the routes (status and content) against a fixture monorepo, the timeline unit and paging, and
      starting a run through the web endpoint.

## Approach

- **Hosting.** Use ASP.NET Core minimal APIs (Kestrel) inside the existing executable (`FrameworkReference
  Microsoft.AspNetCore.App`), bound to `127.0.0.1`. `WebCommand` builds the app; `DashboardApp` maps the routes, so
  tests can run it with `TestServer`-free plain `HttpClient` on an ephemeral port.
- **Rendering.** Pages are rendered on the server by a small HTML builder (`Web/Html.cs`, which escapes all
  values), plus one stylesheet in Fossil's spirit. There is no front-end build step.
- **Live updates.** A run page subscribes to a Server-Sent Events endpoint (`/runs/<key>/events`) fed by
  `RunSupervisor`. The Runs and Home pages refresh their live section by polling a fragment. Everything still works
  without JavaScript through reloads.
- **Timeline.** `Timeline.ReadAsync(monorepo, unit, page, pageSize)`:
  - Take `git log --format=<fields> -n <skip+pageSize>` from each unit member's source repo, starting from all its
    branches and tags.
  - Merge by committer date, then skip and take.
  - Tags come from `for-each-ref`, so agent and integration tags can be linked.
- **Shared model.** Reuse `ComponentGraph`, `GitRef`, `RunMetadataStore`, `IntegrationStore`, `IntegrationPlanner`,
  `RunSupervisor` and `InteractiveSession.ComposeCommand`, moving the latter to a shared place if needed.

## Decisions

- **The timeline unit is the choice plus what it depends on** (user decision). The unit is the reference closure
  that `-select` requires, so a timeline shows what a run over the same choice works on. Rejected: adding
  dependents, which would pull in everything that uses a low-level library.
- **Integrations are shown, not performed** (user decision). The dashboard lists integrations with their steps and
  previews the triage for chosen runs. Starting, stopping and advancing stay with `bassia integrate` and
  `bassia ui`.
- **Loopback only, no authentication** (user decision), like `fossil ui`. Exposing the dashboard would need
  authentication, which is out of scope.
- **ASP.NET Core minimal APIs rather than `HttpListener`.** The .NET SDK the README already requires ships the
  ASP.NET Core shared framework, so there is nothing more to install. Kestrel gives routing, form binding and
  Server-Sent Events without hand-written HTTP parsing.
- **Server-rendered HTML with progressive JavaScript, no front-end build.** This follows Fossil: every page works
  as a plain page, and the scripts only make live parts update in place.
- **A form token and a host check guard the loopback server.** Loopback alone does not stop a page on another site,
  open in the same browser, from posting a form to `127.0.0.1` (and a run is a shell command), or from reading the
  dashboard through DNS rebinding. Every form carries a per-server random token that each POST must match, and
  requests addressed to any host other than `127.0.0.1`/`localhost` are refused.
- **A live run page streams the whole kept output tail.** Replacing the tail each time it changes, rather than
  appending new lines, keeps the page right after lines fall off the front of the supervisor's bounded tail.

## Progress

- [x] Specification agreed.
- [x] Server, layout and read-only pages.
- [x] Timeline.
- [x] New run and live run pages.
- [x] Integration pages.
- [x] Tests, README.

## Validation

```powershell
dotnet build
dotnet test --filter Category!=EndToEnd
dotnet run --project Bassia -- -C <monorepo> web
```

- `dotnet test`: 146/147. The 12 new `DashboardTests` cover every menu page, the linked graph, component pages, the
  timeline unit (dependencies join, unrelated components stay out, no choice logs nothing), timeline merging and
  paging, commit pages (only a hash reaches git), starting a run from the prompt through to its record and SSE
  `done` event, form validation, command composition, the token and host guard, the integration pages with a
  triage preview that changes nothing, and `web` outside a monorepo. The one failure is the Linux-only
  `Agent_ComponentChain_...` test, which also fails on `main`.
- Hand-checked on a demo monorepo (app → lib, two runs, an integration) with headless Chromium:
  - every page renders;
  - a run sent from the prompt page streamed its output live and turned into its record;
  - with the requested port taken, the dashboard served on the next one;
  - Ctrl-C stopped the server with a TOML result and recorded a live run as `cancelled`.
