# Views: metadata clones, config specs and on-demand hydration

## Outcome

Bassia behaves like a client-server VCS with a virtual filesystem (ClearCase, Perforce) without a filesystem driver.
`bassia clone` makes a local monorepo that is a cache of another one (the *depot*): the meta-repo and every component's
history, but no file content. A *view*, described by a ClearCase-like *config spec*, maps chosen components and the
components they reference onto any folder of the worker's filesystem; only the files its `load` rules name are
downloaded and materialized, and `bassia view load <path>` hydrates more while an agent works. An agentic run can start
from a config spec, so a cloud worker downloads only what its agent reads, and its results are written through to the
depot.

## Context

Since `bassia-0cd6f969` (storage contract, see "Architecture" in `docs/reference.md`) the component graph, agentic runs,
integration and locking reach component histories only through `Bassia/Storage` (`IComponentStorage`,
`IComponentStore`, `IComponentWorkspace`), implemented by `Storage/Git` (bare repositories next to `.bassia`) and
`Storage/Memory` (the unit tests' reference). Agentic runs materialize every selected component in full
(`Workspaces`), side by side, with references nested as links. There is no way yet to clone a monorepo, and every store
holds its whole content.

Git supports the mechanics: a partial clone (`--filter=blob:none`) holds commits and trees but no blobs, fetching blobs
from its promisor remote on demand, and a sparse checkout materializes only some files. Two caveats shape the design:
git does not lazily fetch *through* a partial clone (a working copy cloned from a partial store cannot get a blob the
store lacks), and `git ls-tree -l` fetches every blob to report sizes.

## Acceptance criteria

### Storage contract

- [x] `IComponentStorage.ImportHistoryAsync(name, url)` creates a store with the history (commits, branches, tags,
      file listings) of `url` but no file content; `url` is its upstream. Git: a bare partial clone (`blob:none`).
- [x] `IComponentStore` reports and fetches content: `MissingContentAsync(commit, paths)` lists the files of a commit
      whose content is not held; `HydrateAsync(commit, paths)` fetches them from the upstream and fails with a
      `StorageException` when it cannot. Reading a file (`ReadFileAsync`, merges) hydrates what it needs. A full store
      has nothing missing. `ListFilesAsync` reports size -1 for a file whose content is not held, without fetching it.
- [x] `IComponentStore.FetchUpstreamAsync()` brings the upstream's branches and tags (history only, for a partial
      store) into the store; `PushUpstreamAsync(branch, tags)` publishes a branch and tags to the upstream.
- [x] `IComponentStore.CheckoutAsync(commit, directory, branch, load)` with load patterns makes a sparse working copy:
      only the files matching the patterns (split-plan path patterns) are materialized, their content hydrated first.
      `IComponentWorkspace.LoadAsync(patterns)` materializes more files and returns them. Files that were never loaded
      are unchanged in every commit of the working copy; files the agent creates anywhere are committed as usual.
- [x] Both backends implement all of it; the memory backend counts hydrated objects so tests can prove laziness.

### Depot clones

- [x] `bassia clone -from <depot> [-path <dir>] [-lazy]` clones the depot's meta-repo and imports each registered
      component's history without content (with `-lazy`, none: a view imports what it maps). `depot.url` is recorded in
      the clone's user configuration layer. A `<depot>` is a monorepo root: a folder or a URL base under which
      `.bassia` and `<component>` are repositories.
- [x] `bassia fetch` updates the meta-repo (fast-forward) and every component store from the depot.
- [x] Failure: a target that is not empty, a depot without a meta-repo, a component that cannot be imported; nothing
      is left behind.

### Config specs and views

- [x] A config spec is a text of line rules, `#` comments: `map <component>[,...]` (the component and, transitively,
      what it references), `select <component-pattern> <version>` (branch, tag or commit hash; the first matching rule
      wins; a mapped component no rule matches uses its default branch's tip), `load [!]<component>/<path-pattern>`
      (what to materialize; `load app` loads all of `app`; `!` excludes). Errors name the line.
- [x] `bassia view create -name <view> -spec <file|-> [-path <dir>]` resolves the spec, imports missing component
      histories from the depot, checks out every mapped component side by side under the view folder (default
      `<workspace>/view-<name>`) with references nested as in runs, and materializes only the loaded files. The view
      folder holds `.bassia-view.toml` (the manifest), so view commands work from inside it, also outside the monorepo.
- [x] `bassia view list`, `view show [-name]`, `view ls [-path <pattern>]` (every mapped file with its component and
      whether it is loaded, from history only) are read-only and fetch no content.
- [x] `bassia view load -path <path|pattern>,...` (relative to the view folder or the working directory; a path inside
      a nested reference belongs to the referenced component) hydrates and materializes the files, records the rule in
      the manifest and lists what it loaded. A path that matches no mapped file fails and changes nothing.
- [x] `bassia view update [-name]` fetches the mapped components from the depot, re-resolves the spec and moves every
      component whose version moved to its new commit, keeping the load rules; a component with local changes is not
      moved and is reported.
- [x] `bassia view remove -name <view>` deletes the view's folder and registration.

### Agentic runs

- [x] `bassia agent start -spec <file|-> ...` runs on the spec's mapping: the selection is the mapped closure pinned
      at the resolved commits, the working copies are sparse with the spec's load rules, the run folder is a view
      (`bassia view load` works inside it), and the run guide (`AGENTS.md`/`CLAUDE.md`) tells the agent how to load
      files. `-load <component/pattern,...>` makes any `-select` run sparse the same way. `-detach` keeps both.
- [x] In a clone (`depot.url` set), a run's result branch and tags are also pushed to the depot (write-through); a
      failed push leaves the component `failed` and `agent retry` pushes again. Run records stay local.
- [x] Runs without `-spec`/`-load` and monorepos without a depot behave exactly as before.

### Documentation and tests

- [x] `docs/reference.md` (a "Views" section and the architecture) and help describe the commands and the format.
- [x] Core tests on the memory backend cover spec parsing and errors, resolution, sparse materialization, loading,
      update, laziness (nothing hydrated beyond the load rules) and write-through; CLI tests cover clone, fetch, views
      and a sparse spec run against a local git depot, proving the clone holds no blobs it was not asked for.

## Approach

- **Storage**: extend the contract as above. Git: partial bare clones over `file://` (local paths) with an upload-pack
  that allows filters and arbitrary wants; missing content found with `rev-list --objects --missing=print --no-walk`;
  hydration with `fetch --filter=blob:none --stdin origin <oids>`; sparse working copies as partial clones of the store
  with a non-cone sparse-checkout of the exact matching files, hydrating the store first (git cannot fetch through it).
  Memory: a partial store keeps the set of held objects and checks its upstream is reachable when it hydrates.
- **Core** (`Core/Views`): `ConfigSpec` (parse), `ViewResolver` (closure, versions, load patterns per component),
  `ViewManifest` (the `.bassia-view.toml` file), `Views` (create, load, ls, update, remove over `MonorepoGraph` and an
  optional depot that imports missing histories). Sparse materialization joins `Workspaces`; `AgentRuns` takes load
  patterns and a write-through flag.
- **Commands**: `clone`, `fetch`, `view create|list|show|ls|load|update|remove`, `agent start -spec|-load`, config key
  `depot.url`.

## Decisions

- Results of a run in a clone are written through to the depot (branch and result tags); run records stay local.
  Integration happens where the depot is. (User decision.)
- The config spec is line-based and ClearCase-like rather than TOML. (User decision.)
- Mapping a component pulls in the components it references, transitively - the same closure runs use; referrers are
  not mapped. (User decision.)
- No filesystem driver: unloaded files are absent from disk; `view ls` shows the whole mapped tree and `view load`
  materializes on request. No stub files, which agents would read as content. (User decision.)
- Load patterns use the split-plan path patterns (`**`, `*`, `?`, a folder matches what is below it, `!` excludes), as
  lock and merge settings do, and git gets the exact matching file list rather than translated patterns, so both
  backends agree on what a pattern loads.
- A view's version rules accept branches (followed by `view update`), unlike `-select`; a run from a spec pins each
  component to the resolved commit hash, so its record stays reproducible.
- Views are local working state: registered in the clone (`.views/<name>.toml`), never in the meta-repo.
- The run's code boundary check still reads the files it scans, hydrating them in a partial store.
- Integration and lock catch-up are not made sparse: they need content a clone may lack, so they belong on the depot
  (or a full monorepo). A view's working copies are on branch `view/<name>`; changes made in a view are not committed
  by Bassia - agentic runs are the write path.
- `-load` is also offered on `-select` runs, so any run can be sparse; a spec run without load rules is recorded with
  `load = "!*/**"` (loads nothing) so a detached run keeps being sparse.

## Progress

- [x] Storage contract, git and memory backends
- [x] Config spec, resolver, views and sparse materialization in Core
- [x] Depot clone, fetch and write-through
- [x] Commands, help and documentation
- [x] Tests written and executed (`ViewTests`, `ViewCommandTests`; full suite green)
- [x] Manual check: clone of a local depot holds no blobs; view create, ls, load (through a nested reference), a
      sparse `-spec` run writing through to the depot, fetch, view update, a lazy clone and view remove

## Validation

```sh
dotnet build
dotnet test --filter "Category!=EndToEnd"
```

Plus a manual check: `bassia clone` of a local monorepo, `git -C <clone>/<component> rev-list --objects --missing=print
--all` lists every blob as missing; after `view create` with one `load` rule only those blobs are present.
