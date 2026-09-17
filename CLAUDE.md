# `Bassia` - agentic monorepo

`Bassia` is my own version control implementation suited for 1. agentic development, 2. scalable monorepo support, 3. `git` compatibility.

## Architecture

`Bassia` is a collection of individual `git` repos, acting as components (in the spirit of [BitKeeper](https://www.bitkeeper.org/) components - "submodules done right"). Each component encapsulates an architecturally distinct part: library, frontend, backend, project, executable application etc. The components are registered with the meta-repo, which acts as the front and root of the `Bassia` monorepo. The meta-repo maintains metadata, including *component dependencies*. Component dependencies allow to transparently limit the amount of repos which need to be cloned (in addition to the meta-repo) when working on a particular project. In other words, a component is an unit of scalability, modularity, and also security delineation.

## Tech stack

The `Bassia` initially consists of a command-line frontend written in .NET which relies on a separately installed `git` to maintain the components and meta-repo. 

## Agentic workflow

Commit the working tree after each agentic run (i.e. once the requested change is complete), using this commit message format:

```
feat|chore|fix|refactor(<code part>): <one line description>

<spec reference>

<agentic run summary>
```

- Pick the single type that best matches the change: `feat` for new capability, `fix` for a bug fix, `refactor` for internal restructuring with no behavior change, `chore` for everything else (docs, tooling, feature-record bookkeeping).
- `<code part>` names the affected component or area (e.g. `setup`, `cli`, `docs`).
- `<spec reference>` points to the driving feature record, e.g. `feat/done/bassia-d3226a2c/SPEC.md`, or `n/a` when there is none.
- `<agentic run summary>` briefly recaps what the run did and validated, in a few sentences.
