# `Bassia` - agentic monorepo

`Bassia` is my own version control implementation suited for 1. agentic development, 2. scalable monorepo support, 3. `git` compatibility (as an import and export format).

This branch (`main-client-server`) is the client-server rewrite following the design document
[Design: Cloud-Backed Version Control for Agentic Development](https://claude.ai/artifact/V2nkDzNs8XAsx1E9PpgwM7). The
git-component Bassia (components, meta-repo, component graph) lives on `main`; it is deprecated and removed here.

## Architecture

The monorepo is one tree versioned as immutable, content-addressed objects (chunks, trees, commits, operations, views)
in cloud blob storage. Users and agents never handle branches, tags or commits: each agentic run gets a *workspace*
over the monorepo (stacked on or cloned from other workspaces for branch-like work), its commits are implicit
snapshots, and workspaces *land* on the single trunk (mainline), which is linear and numbered by seq. Conflicts
follow per-path policies in the versioned `.bassia.toml`; the default re-runs the task on the new trunk (replay)
instead of merging text.

The version core (`Bassia.Core`) reaches storage only through four traits - `IObjectStore`, `IViewStore`, `IPathIndex`,
`ILeaseStore` - and may not reference an HTTP client, a cloud SDK or a database driver. The same core runs in a local
monorepo (a local-disk blob store standing in for S3, and SQLite) and in `bassia-server`; a clone of a server uses
remote implementations of the traits. See "Architecture" in `docs/reference.md`.

## Tech stack

.NET 10 / C#: the `bassia` command-line frontend (TOML results, `-human` Markdown), `bassia-server` (ASP.NET Core,
SQLite, the local-disk blob store or S3 via the AWS SDK, OpenTelemetry metrics), and a server-rendered web dashboard.
BLAKE3 (`Blake3` package) and canonical CBOR (`System.Formats.Cbor`) define object ids. A separately installed `git` is
needed only for `bassia import` and `bassia export`; weave's `weave-driver` is the default semantic merge driver.
Tests (xUnit) run without a network or a cloud account.

## Agentic workflow

### Specification first

Before each agentic run, create a specification and base all following work on it:

1. Run `./feat/new-feature.ps1` to create `feat/proposed/bassia-<id>/SPEC.md` from `feat/template.md` (see `feat/README.md`). If the request continues an existing feature record under `feat/`, update that record instead of creating a new one.
2. Fill in the specification from the request and the codebase: outcome, context, verifiable acceptance criteria, approach, and validation.
3. List any open questions that cannot be reasonably inferred from the request or the codebase, and ask the user to resolve them before implementing. Record the answers under **Decisions**.
4. Move the record to `feat/active/` when implementation starts, implement against its acceptance criteria, and keep its **Progress** and checkboxes current. Move it to `feat/done/` once every acceptance criterion is verified.

Do **not** create a specification for minor tasks that can be resolved quickly and pushed straight to `main` (e.g. typo fixes, small doc or instruction edits, one-line bug fixes, trivial tooling tweaks). Use `n/a` as the spec reference for those.

### Pull request and auto-merge

Finish each spec-based agentic run with a pull request instead of pushing to `main`:

1. Commit on a feature branch (see **Commits**), push it, and open a pull request against `main` (on this branch line: against `main-client-server`) whose title is the commit subject and whose body links the specification.
2. Enable auto-merge on the pull request (merge commit), so it merges into `main` as soon as all of its checks pass. If auto-merge cannot be enabled, watch the pull request and merge it yourself once every check has passed.
3. Do not notify the user about a pull request that is progressing or merges cleanly. Notify the user only when:
    - a check fails, naming the failing check and its cause, or
    - the pull request has a merge conflict with `main`, naming the conflicting files.

   In both cases, leave the pull request unmerged until the failure or conflict is resolved.

### Commits

Commit the working tree after each agentic run (i.e. once the requested change is complete), using this commit message format:

```
feat|chore|fix|refactor(<area>): <short summary>

<spec reference>

<agentic run summary>
```

- Pick the single type that matches the change: 
    - `feat` for a new capability
    - `fix` for a bug fix
    - `refactor` for internal restructuring with no behavior change 
    - `chore` for everything else (docs, tooling, feature-record bookkeeping).
- `<area>` names the affected component or area (e.g. `setup`, `cli`, `docs`).
- `<short summary>` is a one-line summary of the changes
- `<spec reference>` points to the driving feature record, e.g. `bassia-d3226a2c/SPEC.md`, or `n/a` when there is none.
- `<agentic run summary>` recaps what the run did and validated. Preferably copy the agentic summary verbatim in Markdown format.
