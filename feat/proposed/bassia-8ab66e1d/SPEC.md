# Agentic run on the monorepo

## Outcome

The user initializes the `Bassia` monorepo and starts a LLM agent. The relevant components will be fetched, and checked out in the `.workspace` isolated area, where the agent will apply changes. The changes can be reviewed and integrated into the monorepo.

## Context

<!--  Explain the problem, relevant constraints, and links to dependent feature records or external issues. -->

We want to extend the typical `git`-based agentic development workflow:
- checkout a new branch in an isolated `git` worktree
- run the agent in the worktree
- merge the changes to the `main` branch
- delete the worktree, and prune from the root repo

Now we operate on a `Bassia` monorepo, which initially consists only of the comparatively small meta-repo, which contains the list of all components (separate `git` repos) in the form of their `git` clone paths. We do not initially care about any branch (or, generally, commit-ish) of the components.

### Agentic run
Suppose we want to run an agent on a certain monorepo state. Requirements:

#### Select a monorepo state

An equivalent of checking out a branch (or a commit, with detached HEAD) in a normal repo. However, we have many components (individual repos), each with its own history and commits. Each agentic run must select a subset of commit-ish identifiers in all components which can be affected by the agentic run.

Example:

```sh
bassia agent -select <component 1 commit-ish>, <component 2 commit-ish> -run <e.g. claude code cli command>
```
The commit-ish in the `-select` argument is preferably an annotated tag. While we can use branches or commit hashes, we need a descriptive and  immutable identifier, since such an information is to be logged in the agentic run metadata record. Needed: a syntax for selection of *component AND commit-ish. Each component has an unique logical name within the monorepo recorded in the meta-repo's `components.toml`, so it can be used here, following a suitable standard `git`-inspired syntax.

The argument of the `-run` is the actual agentic run command. 

#### Create an isolated area in the `.workspace`

Once we select the monorepo components, we create an isolated area in the `.workspace` folder. The `.workspace` folder is by default next to the meta-repo `.bassia`, but its location is configurable, possibly on a different volume or in the cloud. Creation of the isolated area is analogous of checking out a worktree  for a single `git` repo. Here we create an unique subfolder of `.workspace`, e.g. `.workspace/agentic-run-1/`.

The subfolder `.workspace/agentic-run-1/` must be populated by all selected components, but here's a catch: Each component can refer to another component by including it as  a subfolder, e.g.

```sh
R:\
|
|__ .workspace
    |__ agentic-run-1
        |__ component-1
            |__ project1.csproj        <-- refers to project2.csproj
            |__ ...
            |__ component-2
                |__ project2.csproj
                |__ ...
```
The meta-repo's `components.toml` will contain the information

#### Component references

A component can refer to another component (e.g. a `.csproj` referencing a `.csproj` in a nested component). This reference is recorded in `components.toml` as a dependency edge between the two components' logical names. The reference graph formed by all such edges must be acyclic — a component cannot (transitively) depend on itself.

`components.toml` only records the dependency graph and logical-name registry; it does not pin a component to a specific commit-ish. The commit-ish pin is per agentic run, supplied via `-select` and logged in that run's metadata record (see "Select a monorepo state" above). This is a deliberate difference from BitKeeper components, where a component's version is pinned by a delta in the parent product's own ChangeSet history: Bassia keeps `components.toml` free of version state so it doesn't need to be updated every time a component moves, at the cost of not getting an atomic, permanent cross-component version record for `main` "for free" — if that's later needed (e.g. to durably record which component versions were integrated together), it should be a separate lockfile written at integration time, not a change to `components.toml` itself.

#### Materializing nested components: cache + junctions

To populate `.workspace/agentic-run-1/` with nested components efficiently and consistently, components are not cloned directly into the workspace. Instead:

1. Every component selected for the run (including transitive dependencies pulled in via the acyclic reference graph) is cloned and checked out once into a shared cache area (outside `.workspace`, keyed by component + commit-ish).
2. Inside `.workspace/agentic-run-1/`, each component's nested-component subfolders are created as filesystem junctions (e.g. Windows junctions/`mklink /J`, or symlinks on POSIX) pointing into the cache, rather than as copies or nested clones.

This gives two benefits:
- Speed: a component checked out once in the cache can be junctioned into many workspaces without re-cloning or copying.
- Consistency: if `component-1` and `component-2` both nest `component-lib`, both junctions resolve to the very same cache location and commit-ish, so the agentic run always sees one consistent copy of `component-lib`, never two divergent ones.

## Acceptance criteria

- [ ] State observable behavior that can be verified.
- [ ] Include failure behavior and compatibility expectations where relevant.

## Approach

Describe the intended boundaries and major implementation steps. Avoid file-by-file instructions that will become stale.

## Decisions

- Record durable decisions and their rationale as they are made.
- `components.toml` records only the component dependency graph (acyclic) and logical names, not version pins; commit-ish pins are per agentic run via `-select`, not baked into the meta-repo.
- Nested components are materialized via a shared cache (one clone+checkout per component/commit-ish) plus filesystem junctions inside `.workspace/agentic-run-*`, rather than per-workspace clones, so shared nested components (e.g. a common `component-lib`) resolve to one consistent copy across all referencing components.

## Progress

- [ ] Add only meaningful implementation and validation milestones.

## Validation

List the commands or manual checks that prove the acceptance criteria.