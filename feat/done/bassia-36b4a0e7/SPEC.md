# Visual web dashboard and layered configuration with a configurable merge policy

## Outcome

`bassia web` becomes a place to *look at* the monorepo rather than to drive it: graphs and live pictures of the
components and how they relate, of tags as the units of progress (which components each spans, which run or
integration made it, which runs started from it), of agentic runs (an animated overview of live and finished runs),
and of the merge queue (what is waiting to be integrated, how it would merge, what the merges mean and which ones
need a human). Controlling stays with the command line and `bassia prompt`. To that end the configuration becomes a
layered system - built-in defaults, the monorepo's `.bassia/config.toml`, and a per-user layer - that the CLI and the
LLM prompt can read and change, and that makes the merging process configurable.

## Context

- The dashboard (`Bassia/Web/Dashboard.cs`, `Html.cs`) is server-rendered HTML with a progressive script. Today it is
  mostly tables: a component graph SVG (`ComponentGraph.ToSvg`), a timeline, runs (with a "New run" form and a
  Stop button), and integrations with a triage preview.
- Tags across components are modelled by `MultiComponentTag` (`bassia tag list|show|create`), runs by `RunMetadata`
  (result tags `agent-run/<key>/<n>`, the baseline `CommitIsh` per component), integrations by `IntegrationRecord`
  (result tags `integration/<key>/<n>`, steps with triage, strategy, structural verdict, outcome, semantic brief).
- `bassia config list|get|set` already edits `.bassia/config.toml` (one layer, committed to the meta-repo). The keys
  are a fixed table (`ConfigFile.Keys`); `Monorepo.Load` reads each one by hand.
- The merge process (`IntegrationPlanner`, `IntegrationRunner`) is fixed: git, then weave (configurable driver),
  then the resolver; `-semantic`/`-skip` override it per run on the command line only.
- `bassia prompt` offers the LLM the command catalog generated from `CommandTable`, so new CLI commands and
  switches reach the LLM automatically.

## Acceptance criteria

### Configuration

- [x] Settings resolve in layers, lowest first: built-in default, monorepo (`.bassia/config.toml`), user layer (see
      Decisions). Every consumer (`Monorepo.Load`, integration, prompt, web) reads the effective value.
- [x] `bassia config list` shows each key's effective value and the layer it comes from; `config get` likewise.
- [x] `bassia config set -key k -value v [-user]` writes the monorepo layer (committed to the meta-repo, comments
      kept) or the user layer; `bassia config unset -key k [-user]` removes a setting so the next layer shows through.
- [x] Unknown keys and invalid values (e.g. a value outside a key's allowed set) are rejected with the allowed
      values, and the file is left unchanged.
- [x] `bassia prompt` gives the LLM the effective configuration with the layer of each value, and the `config`
      commands (catalog, read-only classification of `config list|get`) let it read and change settings.

### Configurable merging

- [x] New `merge.*` settings drive the integration (see Approach for the set), honoured by both
      `integration plan` and `integration start`; command-line switches still override them for one integration.
- [x] Steps the policy marks as needing manual attention are not merged automatically; the integration records them
      (status, note) and the plan reports them, so the CLI, the prompt and the web can list them.

### Web dashboard

- [x] **Components**: an overview graph of components and their dependencies, each node showing activity (live
      runs, results waiting in the merge queue, merges needing attention, latest tag); hovering or focusing a node
      highlights its dependency closure and dependents.
- [x] **Tags**: a component × tag chart of the tags that mark progress, coloured by kind (baseline, run result,
      integration, unwind, split), in time order; a tag's page shows the components it spans with their commits, the
      run or integration that made it, the runs started from it, and the lineage baseline → runs → result tags →
      integration → integration tag as a graph.
- [x] **Runs**: a time chart of runs (start to finish, coloured by status) with live runs animated (progress through
      their phases, elapsed time ticking, latest output line) and updating without reload; the per-run page shows the
      phase progression and the components it spanned.
- [x] **Merge queue**: the runs whose results are not yet integrated, per component lane, oldest first; the triage
      as a flow (git / weave / resolver / manual / skipped) and the conflicts between queued runs as a graph; each
      step's meaning (the run's rationale, conflicting files, weave's verdict and warnings, the semantic brief when one
      was written); a list of merges that need manual attention, with the CLI command that deals with each.
- [x] **Configuration**: the effective configuration per layer, read-only, with the command that changes each value.
- [x] Every page still renders without JavaScript (the pictures are server-side SVG/CSS); scripts only animate and
      refresh. No external assets are loaded (the dashboard works offline). The loopback/token guards stay.
- [x] Existing tests pass; new tests cover config layering, the merge policy in the planner, and the new pages.

## Approach

1. **Config layers.** `ConfigSnapshot` reads both files once and resolves every key (user, monorepo, default; a
   per-component key falls back to its global key). `Monorepo.Load` reads all settings through it and rejects values
   outside a key's choices. `ConfigFile` keeps its comment-preserving line editor, gains a layer argument and
   `Unset`, and quotes table names that need it (`[merge.component."my.lib"]`). The CLI gets `config set -user` and
   `config unset [-user]`; `config list|get` report the layer.
2. **Merge policy.** `merge.semantic` (resolver | manual), `merge.warnings` (resolver | manual | accept),
   `merge.manual_paths` (path patterns as in split plans) and `merge.advance` (manual | auto), each overridable per
   component. A new `MergeStrategy.Manual`, step outcome `needs_attention` and integration status `needs_attention`
   carry "leave this to a human" through the planner, the runner, the record and the views. `-manual` overrides it
   per run, as `-semantic` and `-skip` do. `merge.advance = auto` runs `integration advance` for those components.
3. **Web.** `Dashboard` is split into partial files per area (overview/components/timeline, runs, merge, tags,
   config). `Charts` draws the pictures as server-side SVG/CSS: the component map, flows (lineage), the run timeline,
   phase steps, donut, stacked bars, merge-queue lanes, the collision ring and the tag matrix. `WebData` derives
   live runs (job registry + log tail), the merge queue (git reachability from the default branch + the latest
   integration step) and tag stories. The in-process run supervisor and the New run form are gone.

## Decisions

- **User layer is `.bassia/config.user.toml`, never committed.** It is git-ignored in the meta-repo (`init` writes a
  `.gitignore`; an older meta-repo gets the entry in `.git/info/exclude` on the first `-user` write, so no commit is
  needed). It overrides `.bassia/config.toml`; it is per clone, which is per user. Rejected: committed per-user files
  (`.bassia/users/<email>.toml`) and a home-folder global file.
- **Merge policy is global with per-component overrides**: `merge.<setting>` and `merge.component.<name>.<setting>`
  (in TOML `[merge.component.<name>]`). A per-component key falls back to the global key, layer by layer (user
  component, monorepo component, user global, monorepo global, default). Per-path rules beyond `merge.manual_paths`
  were not wanted.
- **The web UI is read-only.** The New run form and the Stop button are removed; pages show the copyable `bassia`
  command for each action instead. The dashboard's in-process run supervisor goes with them: live runs are the jobs
  of `bassia run start` (detached or not) as `run list` sees them.
- **Server-side SVG and CSS** for every picture, animated with CSS and a small vanilla script; no CDN assets.

## Progress

- [x] Decisions resolved
- [x] Layered configuration and CLI
- [x] Merge policy in planner and runner
- [x] Web pages: components, tags, runs, merge queue, config
- [x] Tests, README

## Validation

- `dotnet build` (no warnings) and `dotnet test` on Linux with .NET SDK 10.0.112: 328 passed, 1 skipped, 2 failed.
  The 2 failures are the end-to-end tests that clone from `https://github.com/...`, which this sandbox cannot reach.
  They failed the same way before the change.
- New tests: config layering (user layer overrides, is never committed, `unset` falls back), per-component keys (and
  removal of their table), rejection of invalid choices and unregistered components, the merge policy in
  `integration plan|start` (`merge.semantic = manual` gives `needs_attention` and auto-advance moves the base;
  `merge.manual_paths` and `-manual`), and every dashboard page (component map, tag chart and story, run chart and
  flow, live runs from the job registry, merge queue lanes/ring/attention, config layers, read-only guard).
- A demo monorepo with five components, nine runs (one live, one failed, colliding results), baselines across
  components, one integration that left a merge for a human, and a user-layer setting. Every page was screenshot in
  headless Chromium and checked by eye.
