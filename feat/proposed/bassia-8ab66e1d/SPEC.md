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

For the purposes of this section, the entire agentic run folder structure is (expanded from [previous feature section Context](../../done/bassia-d3226a2c/SPEC.md)):

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

Create a shell process in the folder `.workspace/agentic-run-1/` and run the `<agent command>` (from previously defined `bassia agent -select <component 1 commit-ish>, <component 2 commit-ish> -run <agent command>`). The `<agent command>` may the Claude Code CLI, Copilot CLI, or similar. The agent run arguments (prompt, model, context, commit-ish used for creation  etc.) will be recorded in the `R:\.bassia` meta-repo folder. The information will allow to monitor the state of the agentic run, and support suspend, resume, and handover of the agentic run. Since there may be many parallel agentic runs, the information will be committed without branch check-out, so as not to impose a `git` index lock. Instead, a commit will be created, tagged with an  annotated tag, and committed into the meta-repo. If the state of the agentic run changes, a new child commit will be created and tagged. Example:

- starting an `agentic-run-1` in the workspace
- create an information about the agentic run - model, prompt, monorepo selector (the commit-ish from the `-select` parameter), etc. - and commit it to the `.workspace/.agentic-runs` bare repo.
- IMPORTANT: DO NOT CHECK OUT a branch in the `.workspace/.agentic-run` bare repo. This will create the aforementioned `git` index lock, and may become a problem when many agents run in parallel. Instead, create a commit using `git` plumbing commands, and tag it with an annotated tag. When a new addition to the agentic run info needs to be committed, e.g. when the agentic run completes, create a child commit in the same manner, and tag it again. The names of the tags are `<prefix>/<unique part same for parent and children>/<lineage part>`, so that we can map the tags to agentic runs. Example: `agent/agentic-run-1/0`, `agent/agentic-run-1/1`. THIS PATTERN WILL BE REPEATED where applicable and parallel commits are needed, such as when committing results of agentic runs.

After the agent completes, the changes will be committed. This presents a challenge, considering that:

1. we have several components (`git` repositories), each with its own history and objects
2. a checked out component can contain folder junctions to other checked out components, recursively

Consider first the point 1., without the point 2.

```
R:\
|__ .bassia
    |__ .git
|__ .workspace
    |__ .agentic-runs
        |__ .git              <-- bare repo which holds the information about the agentic runs
    |__ .agentic-run-1
        |__ component-1       <-- https://github.com/examplename/component-1.git @ tag-1
        |   |__ script1.cs
        |__ component-2       <-- https://github.com/examplename/component-2.git @ tag-2 
            |__ script2.cs
```

The agentic run modifies the files `script1.cs`, `script2.cs`, and commits in both repos local to the agentic run.  Each commit messages will contain the summary of the agentic run, including the tags of the agentic run metadata (see above). Each commit will be tagged with annotated tag. The tag names will uniquely identify that the commits belong together and to a particular agentic run. These commits will involve checked out branches (created uniquely for this agentic run, and tied together by their names), since we obviously need a checkout to have files to operate on. This is not a problem, as there is only a single agentic run editing each component's index, and the plumbing commits without a checkout are not needed for performance. The commit sequence is not transactional, so one commit can succeed and other can fail (NOT for reasons like incorrect merges, which cannot happen in this scenario, just for low level technical repo failures). In this case we may retry, or ultimately abandon the entire agentic run, and discarding the workspace repos.

Once each component's changes are committed and tagged, the respective tags are pushed into local component repos at the top level (the `R:\` volume in the terminology used here), which serve as the sources of truth.
In other words, the repos `R:\component-1/.git`, `R:\component-2/.git` are updated.
 NO MERGE OR CHECKOUT IS PERFORMED, this will be a separate feature. The top-level components repos are gradually populated by tagged commits , and it can be inferred from the tags and the agentic run metadata in `R:\.workspace/.agentic-runs` which agentic run is responsible for what.

Consider now the general case, the point 1 and the point 2 also. The assumed monorepo structure will be

```
R:\
|__ .bassia
    |__ .git
|__ .workspace
    |__ .agentic-runs
        |__ .git              
    |__ .agentic-run-1
        |__ component-1       
        |   |__ script1.cs
            |__ component-2/     <-- junction to a cached folder, the `component-2.git` repo checked out at the tag-2 per `-select` argument
                |__ script2.cs
```

The `component-1` includes `component-2` as a junctioned subfolder. What if we change both `script1.cs`, `script2.cs` and want to commit? The `component-1` repo only receives the changes from `script1.cs`, and the changes of `script2.cs` are `git`-ignored (dynamically?). The `component-2` repo then receives changes in `script2.cs`, e.g. by performing the commit on the cache which backs up the junctioned folder. As before, we commit without checking out, with tags, and synchronize to the source-of-truth repo at `R:\`.


## Acceptance criteria


- [ ] Initialize a `Bassia` monorepo in the temp folder (called here `R:\` to remain consistent).  The monorepo will have a single component `https://github.com/jk09/example.git`. Run the command `bassia agent -select <example component@HEAD> -run <claude, add C# "hello, world" script, no csproj>`. Verify that at the end there's a tagged commit in `R:\example\.git` with this content.

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