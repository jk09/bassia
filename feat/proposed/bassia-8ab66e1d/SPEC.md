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

Only *nested* components go through the cache; components named explicitly in `-select` do not.

- Components explicitly named in `-select` are cloned and checked out directly into `.workspace/agentic-run-1/`, one real checkout per component. This is a normal checkout on a unique branch, since the agent needs to commit to it directly (see "Run the agent" below).
- Components that are pulled in only transitively — reachable from a selected component via the acyclic reference graph in `components.toml`, but not themselves named in `-select` — are materialized via a shared cache instead:

  1. Each such nested component is cloned and checked out once into a shared cache area (outside `.workspace`, keyed by agentic run id).
  2. Inside `.workspace/agentic-run-1/`, that nested component appears as a filesystem junction (e.g. Windows junctions/`mklink /J`, or symlinks on POSIX) pointing into the cache, at the subfolder position its referencing component expects — rather than as a copy or a nested clone.

If a component is both explicitly selected and also reachable as a nested dependency of another selected component, the explicit selection wins: it remains a direct checkout in `.workspace/agentic-run-1/`, and any component that nests it gets a junction pointing at that same checkout instead of a separate cache copy.

This gives two benefits:
- Speed: a nested component checked out once in the cache can be junctioned into many workspaces without re-cloning or copying.
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
    |__ agentic-run-1
        |__ component-1       <-- https://github.com/examplename/component-1.git @ tag-1
        |   |__ script1.cs
        |__ component-2       <-- https://github.com/examplename/component-2.git @ tag-2 
            |__ script2.cs
```

The agentic run modifies the files `script1.cs`, `script2.cs`, and commits in both repos local to the agentic run.  Each commit messages will contain the summary of the agentic run, including the tags of the agentic run metadata (see above). Each commit will be tagged with annotated tag. The tag names will uniquely identify that the commits belong together and to a particular agentic run. These commits will involve checked out branches (created uniquely for this agentic run, and tied together by their names), since we obviously need a checkout to have files to operate on. This is not a problem, as there is only a single agentic run editing each component's index, and the plumbing commits without a checkout are not needed for performance. The commit sequence is not transactional, so one commit can succeed and other can fail (NOT for reasons like incorrect merges, which cannot happen in this scenario, just for low level technical repo failures). In this case we may retry, or ultimately abandon the entire agentic run, and discarding the workspace repos.

Once each component's changes are committed and tagged, the respective tags are pushed into local component repos at the top level (the `R:\` volume in the terminology used here), which serve as the sources of truth.
In other words, the repos `R:\component-1\.git`, `R:\component-2\.git` are updated.
 NO MERGE OR CHECKOUT IS PERFORMED, this will be a separate feature. The top-level components repos are gradually populated by tagged commits , and it can be inferred from the tags and the agentic run metadata in `R:\.workspace\.agentic-runs` which agentic run is responsible for what.

Consider now the general case, the point 1 and the point 2 also. The assumed monorepo structure will be

```
R:\
|__ .bassia
    |__ .git
|__ .workspace
    |__ .agentic-runs
        |__ .git              
    |__ agentic-run-1
        |__ component-1       
        |   |__ script1.cs
            |__ component-2/     <-- junction to a cached folder, the `component-2.git` repo checked out at the tag-2 per `-select` argument
                |__ script2.cs
```

The `component-1` includes `component-2` as a junctioned subfolder. What if we change both `script1.cs`, `script2.cs` and want to commit? The `component-1` repo only receives the changes from `script1.cs`, and the changes of `script2.cs` are `git`-ignored (dynamically?). The `component-2` repo then receives changes in `script2.cs`, e.g. by performing the commit on the cache which backs up the junctioned folder. As before, we commit without checking out, with tags, and synchronize to the source-of-truth repo at `R:\`.


## Acceptance criteria


- [ ] Initialize a `Bassia` monorepo in the temp folder (called here `R:\` to remain consistent). The monorepo will have a single component, cloned locally to `R:\example` from `https://github.com/jk09/example.git`. Create an annotated tag, e.g. `v0`, on the component's current commit — this is the immutable identifier `-select` requires (see "Select a monorepo state"), not `HEAD` or a branch. Run the command `bassia agent -select <example component@v0> -run <claude, add C# "hello, world" script, no csproj>`. Verify that:
  - at the end there's a new tagged commit in `R:\example\.git` containing the "hello, world" script;
  - the `v0` tag itself still points at the original, unmodified commit;
  - `R:\example` was populated as a direct checkout in `.workspace\agentic-run-1\example` (not a cache + junction), since it was explicitly named in `-select` and has no nested components.

## Approach

- **Selection layer**: parse `-select <component@commit-ish>` pairs against `components.toml`'s logical-name registry; resolve each to a concrete commit and record the literal commit-ish string (expected to be an annotated tag) for later logging. Reject unregistered logical names.
- **Workspace materialization layer**: given the resolved selection, compute the reference-graph closure from `components.toml`. For each explicitly selected component, clone/checkout directly into `.workspace/agentic-run-*/`. For each component that appears only as a transitive/nested dependency, clone+checkout once into the per-run cache and create a junction at the expected subfolder position. Detect and reject cyclic references before materializing anything.
- **Agent execution layer**: spawn the `-run` command as a child process rooted at `.workspace/agentic-run-*/`, without altering its working directory expectations.
- **Run-metadata layer**: before and after the agent runs, write a run-metadata record (model, prompt, `-select` value, timestamps, status) as a commit created via `git` plumbing directly into `.workspace/.agentic-runs` (no checkout of that bare repo), tagged with the `agent/<run-id>/<lineage-index>` convention; each subsequent state change is a new child commit + tag.
- **Result commit/push layer**: for each modified component (both direct checkouts and cache-backed nested checkouts), commit on a run-unique branch, tag the commit, then push the tag to that component's source-of-truth repo at `R:\<component>`. No merge or checkout is performed at the source-of-truth repo. Treat the multi-component commit/push sequence as non-transactional: on partial failure, support retrying the remaining components or abandoning the run and discarding the workspace/cache without touching sources of truth that already received a tag.

## Decisions

- `components.toml` records only the component dependency graph (acyclic) and logical names, not version pins; commit-ish pins are per agentic run via `-select`, not baked into the meta-repo.
- The `-select` commit-ish must be an annotated tag (not a branch or `HEAD`), since it is logged as the immutable provenance record for the run; callers are expected to create the tag ahead of time if one doesn't already exist.
- Only components reachable *transitively* (via the reference graph, not named directly in `-select`) are materialized through the shared cache + junction mechanism. Components named explicitly in `-select` are always direct checkouts in `.workspace/agentic-run-*/`, since the agent commits to them on a real checked-out branch. If a component is both explicitly selected and also referenced by another selected component, the explicit checkout wins and other components junction to it instead of to a separate cache copy.
- Nested components are materialized via a shared cache (one clone+checkout per component/commit-ish) plus filesystem junctions inside `.workspace/agentic-run-*`, rather than per-workspace clones, so shared nested components (e.g. a common `component-lib`) resolve to one consistent copy across all referencing components.
- Two distinct commit strategies are used and must not be conflated: agentic-run *metadata* (in `.workspace/.agentic-runs`) is committed via `git` plumbing with no branch checkout, to avoid index-lock contention across many parallel runs; component *content* changes are committed on a normal checked-out, run-unique branch, since editing files requires a working tree and each component's checkout is only ever touched by a single run.
- Tag names follow `<prefix>/<run-id>/<lineage-index>` (e.g. `agent/agentic-run-1/0`, `agent/agentic-run-1/1`) so that a tag can be mapped back to the run and to its position in that run's history, for both run-metadata tags and component-content tags.

## Progress

- [ ] Add only meaningful implementation and validation milestones.

## Validation

- `git clone https://github.com/jk09/example.git R:\example`, then `git -C R:\example tag -a v0 -m "baseline"` to create the immutable `-select` reference.
- Run `bassia agent -select "example@v0" -run "claude, add C# \"hello, world\" script, no csproj"`.
- `git -C R:\example log --oneline --decorate -1` — confirms a new commit exists on top of `v0`, tagged per the `agent/<run-id>/<lineage-index>` convention, containing the new script.
- `git -C R:\example show v0 --stat` — confirms the `v0` tag still points at the original commit, unaffected by the run.
- Inspect `.workspace\agentic-run-1\example` — confirms it is a normal directory/checkout, not a reparse point/junction (e.g. `fsutil reparsepoint query` reports it is not a reparse point).
- `git -C R:\.workspace\.agentic-runs log --all --oneline --decorate` — confirms run-metadata commits and their `agent/agentic-run-1/*` tags exist, and `git -C R:\.workspace\.agentic-runs symbolic-ref -q HEAD` (or checking no branch ref was updated) confirms no checkout occurred in that bare repo during the run.