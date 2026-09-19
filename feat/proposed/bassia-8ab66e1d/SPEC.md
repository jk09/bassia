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

#### 1. Select a monorepo state

An equivalent of checking out a branch (or a commit, with detached HEAD) in a normal repo. However, we have many components (individual repos), each with its own history and commits. Each agentic run must select a subset of commit-ish identifiers in all components which can be affected by the agentic run.

Example:

```sh
bassia agent -select <component 1 commit-ish>, <component 2 commit-ish> -run <agent command>
```
The commit-ish in the `-select` argument is preferably an annotated tag. While we can use branches or commit hashes, we need a descriptive and  immutable identifier, since such an information is to be logged in the agentic run metadata record. Needed: a syntax for selection of *component AND commit-ish. Each component has an unique logical name within the monorepo recorded in the meta-repo's `components.toml`, so it can be used here, following a suitable standard `git`-inspired syntax.

The argument of the `-run` is the actual agentic run command. 

#### 2. Create an isolated area in the `.workspace`

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

##### Component references

A component can refer to another component (e.g. a `.csproj` referencing a `.csproj` in a nested component). This reference is recorded in `components.toml` (located in the meta-repo, see [previous feature](../../done/bassia-d3226a2c/SPEC.md)) as a dependency edge between the two components' logical names. The reference graph formed by all such edges must be acyclic — a component cannot (transitively) depend on itself.

`components.toml` only records the dependency graph and logical-name registry; it does not pin a component to a specific commit-ish. The commit-ish pin is per agentic run, supplied via `-select` and logged in that run's metadata record (see "Select a monorepo state" above). Bassia keeps `components.toml` free of version state.

##### Materializing nested components: cache + junctions

To populate `.workspace/agentic-run-1/` with nested components efficiently and consistently, components are not cloned directly into the workspace. Instead:

1. Every component selected for the run (including transitive dependencies pulled in via the acyclic reference graph) is cloned and checked out once into a shared cache area (outside `.workspace`, keyed by agentic run id).
2. Inside `.workspace/agentic-run-1/`, each component's nested-component subfolders are created as filesystem junctions (e.g. Windows junctions/`mklink /J`, or symlinks on POSIX) pointing into the cache, rather than as copies or nested clones.

This gives two benefits:
- Speed: a component checked out once in the cache can be junctioned into many workspaces without re-cloning or copying.
- Consistency: if `component-1` and `component-2` both nest `component-lib`, both junctions resolve to the very same cache location and commit-ish, so the agentic run always sees one consistent copy of `component-lib`, never two divergent ones.


#### 3. Run the agent 

Create a shell process at the folder `.workspace/agentic-run-1/` and run the `<agent command>` (from previously defined `bassia agent -select <component 1 commit-ish>, <component 2 commit-ish> -run <agent command>`). The `<agent command>` may the the Claude Code CLI, Copilot CLI, or similar. The agent run arguments (prompt, model, context, commit-ish used for creation  etc.) will be recorded in the `R:\.bassia` meta-repo folder. The information will allow to monitor the state of the agentic run, and support suspend, resume, and handover of the agentic run. Since there may be many parallel agentic runs, the information will be committed without branch check-out, so as not to impose a `git` index lock. Instead, a commit will be created, tagged with an  annotated tag, and committed into the meta-repo. If the state of the agentic run changes, a new child commit will be created and tagged. Example:

- starting an `agentic-run-1` in the workspace
- create a `.toml` file with the pertinent information: 

After the agent completes, the changes will be committed. This presents a challenge, considering that:
1. we have several components (`git` repositories), each with its own history and objects
2. a checked out component can contain folder junctions to other checked out components, recursively

Consider first the point 1., without the point 2.. Suppose the entire agentic run folder structure is:

```
R:\
|__ .bassia
    |__ ...
|__ .workspace
    |__ .agentic-run-1
        |__ component-1       <-- https://github.com/examplename/component-1.git @ tag-1
        |   |__ script1.cs
        |__ component-2       <-- https://github.com/examplename/component-2.git @ tag-2 
            |__ script2.cs
```
The agentic run modifies the files `script1.cs`, `script2.cs`, and commits in both cached repos local to the agentic run. Each commit will contain a reference to the agentic

Regarding the monorepo folder structure in [previous feature section Context](../../done/bassia-d3226a2c/SPEC.md). The top-level component repos will receive the changes of agentic runs. To prevent `git` repo locking, each agentic run will generate in each affected repo in the `.workspace` 
```
R:\
|__ .bassia                   <-- meta-repo
    |__ .git                  <-- meta-repo storage. Prospective alternative: distributed database
    |__ config.toml         
    |__ components.toml       <-- monorepo components registration
|__ component_1               <-- CHANGE: NOT RELEVANT, will be cloned and checked out in the cache for each agentic run  
    |__ .git                  
|__ component_2               <-- CHANGE: NOT RELEVANT , same reason
    |__ .git
|__ .workspace                <-- workspace for agents, `git`  worktrees of components
    |__ ...

```

## Acceptance criteria


- [ ] Initialize a `Bassia` monorepo in the temp folder.  The monorepo will have a single component `https://github.com/jk09/example.git`. Run the command `bassia agent -select <example component@HEAD> -run <claude, add C# "hello, world" script, no csproj>`. Verify
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