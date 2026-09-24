# `Bassia` - agentic monorepo

`Bassia` is my own version control implementation suited for 1. agentic development, 2. scalable monorepo support, 3. `git` compatibility.

## Architecture

`Bassia` is a collection of individual `git` repos, acting as components (in the spirit of [BitKeeper](https://www.bitkeeper.org/) components - "submodules done right"). Each component encapsulates an architecturally distinct part: library, frontend, backend, project, executable application etc. The components are registered with the meta-repo, which acts as the front and root of the `Bassia` monorepo. The meta-repo maintains metadata, including *component dependencies*. Component dependencies allow to transparently limit the amount of repos which need to be cloned (in addition to the meta-repo) when working on a particular project. In other words, a component is an unit of scalability, modularity, and also security delineation.

## Tech stack

The `Bassia` initially consists of a command-line frontend written in .NET which relies on a separately installed `git` to maintain the components and meta-repo. 

## Agentic workflow

### Specification first

Before each agentic run, create a specification and base all following work on it:

1. Run `./feat/new-feature.ps1` to create `feat/proposed/bassia-<id>/SPEC.md` from `feat/template.md` (see `feat/README.md`). If the request continues an existing feature record under `feat/`, update that record instead of creating a new one.
2. Fill in the specification from the request and the codebase: outcome, context, verifiable acceptance criteria, approach, and validation.
3. List any open questions that cannot be reasonably inferred from the request or the codebase, and ask the user to resolve them before implementing. Record the answers under **Decisions**.
4. Move the record to `feat/active/` when implementation starts, implement against its acceptance criteria, and keep its **Progress** and checkboxes current. Move it to `feat/done/` once every acceptance criterion is verified.

Do **not** create a specification for minor tasks that can be resolved quickly and pushed straight to `main` (e.g. typo fixes, small doc or instruction edits, one-line bug fixes, trivial tooling tweaks). Use `n/a` as the spec reference for those.

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
