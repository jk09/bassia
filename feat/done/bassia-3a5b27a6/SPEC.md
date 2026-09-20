# Flat agentic run workspace with a fully selected closure

## Outcome

An agentic run materializes every component it touches as a sibling checkout in one run folder, `.workspace/agentic-run-N/<component>`. Components nest into each other only as junctions pointing at those siblings, at the positions recorded in `components.toml`. `-select` names the complete transitive closure, each component at an annotated tag, so no part of a run is ever resolved from a mutable reference. Run metadata lives in the `.bassia` meta-repo, so the run folder is pure scratch and can be discarded once the results are pushed.

## Context

The first agentic-run implementation ([bassia-8ab66e1d](../../done/bassia-8ab66e1d/SPEC.md)) split materialization in two: components named in `-select` were checked out in the run folder, while components reached only transitively went into a separate per-run cache (`.cache/<run-id>/<component>`) and were pinned with a second option, `-pin`. A transitive component that was neither selected nor pinned fell back to the `HEAD` of its source-of-truth repo.

Two problems follow:

- **Mutable provenance.** The `HEAD` fallback makes a run's input depend on when it started. A run record must be reproducible from immutable identifiers alone, which is exactly why `-select` demands annotated tags.
- **An artificial distinction.** Cache and workspace differ only in where a checkout lands. Both are clones of the same source-of-truth repo, both are checked out on the run branch, and both are committed, tagged and pushed back. The split forces two options (`-select`/`-pin`), two directory trees, two configuration keys and two materialization modes to express one idea: *these components, at these tags, for this run*.

The dependency graph in `components.toml` already determines which components a run needs. Making `-select` state the whole closure keeps that explicit at the command line and in the run record. Selections may become unwieldy for large graphs; logical tags (a monorepo-level analogue of `HEAD`) are the intended answer and are deliberately out of scope here.

## Acceptance criteria

- [x] `bassia agent -select A@t1,B@t2,C@t3 -run <command>` with `A -> B -> C` checks out `A`, `B` and `C` side by side in `.workspace/agentic-run-N/`, each a real checkout (no reparse point) on the identically named run branch, off its own tag.
- [x] Each component's references appear inside its checkout as junctions to the sibling checkouts, at the paths recorded in `components.toml` (`A/<path-to-B> -> B`, `B/<path-to-C> -> C`).
- [x] Changes made through a junction are committed by the nested component's own repo; the referencing component's commit ignores them.
- [x] Each changed component is committed on the run branch, tagged with the run's identically named tag, and branch + tag are pushed to the source-of-truth repo at the monorepo root. No merge or checkout happens there.
- [x] Selecting a component without its transitive closure fails before anything is materialized, naming the missing components and the referrer that requires them.
- [x] A component's commit-ish must be an annotated tag; there is no `HEAD` fallback for any component in the run.
- [x] The run record (selection, resolved commits, per-component result commits and tags, status) is stored in the `.bassia` meta-repo and survives deleting the run folder.
- [x] `-pin`, the run cache and the `[workspace] cache` configuration key no longer exist.

## Approach

- **Selection**: parse `-select` into `<component>@<tag>` pairs, compute the reference-graph closure, and reject the run when the closure is not covered by the selection. Resolve every commit-ish as an annotated tag before touching the filesystem.
- **Materialization**: one clone + checkout per component into `.workspace/<run-id>/<component>`, then one junction per reference edge pointing at the sibling checkout, with the nested path added to the owner's `.git/info/exclude`.
- **Run metadata**: keep the plumbing-only, tag-per-state store, relocated to `.bassia/.agentic-runs/.git` and excluded from the meta-repo's own tree.
- **Results**: unchanged from the previous feature — commit on the run branch, annotated tag, push branch + tag to the source of truth, non-transactional with `retry`/`abandon`.

## Decisions

- `-select` must name the full transitive closure of the components it selects. The command no longer infers versions for anything, which removes the `HEAD` fallback and makes a run record reproducible from its tags alone. The cost is longer command lines for deep graphs; logical tags are the planned mitigation and are not part of this record.
- `-pin` is removed. With the closure fully selected there is no second class of component left for it to describe.
- The per-run cache is removed: every component of a run is a checkout in the run folder. Components are shared *within* a run by junctioning siblings, so the consistency the cache provided (one copy of a component per run, whoever nests it) is preserved. Cross-run sharing was never possible anyway, since nested components receive commits during a run and so were already keyed by run id.
- Run metadata moves from `.workspace/.agentic-runs/.git` to `.bassia/.agentic-runs/.git`. The workspace is scratch that a user may delete or relocate to another volume; the mapping from a run to the tags it created in the top-level components is durable monorepo state and belongs with the meta-repo. `.bassia/.gitignore` keeps that bare repo out of the meta-repo's own history.
- The `Materialization` distinction (checkout vs. cache) disappears from the run record, since every component is now materialized the same way.

## Progress

- [x] Flat run folder: closure members cloned and checked out side by side, references junctioned to siblings (`AgentCommand.cs`, `Monorepo.cs`).
- [x] Closure-complete selection enforced before materialization; annotated tag required for every component (`AgentCommand.cs`).
- [x] `-pin`, `Materialization`, the run cache and `[workspace] cache` removed (`AgentCommand.cs`, `RunMetadata.cs`, `Monorepo.cs`).
- [x] Run metadata store relocated to `.bassia/.agentic-runs/.git`, ignored by the meta-repo (`Monorepo.cs`, `InitCommand.cs`).
- [x] Tests updated for the flat layout and the new selection rule, including a three-level `A -> B -> C` chain and the incomplete-selection failure.

## Validation

- `dotnet test` — covers the flat layout, junction targets, per-component commits through junctions, incomplete selection, annotated-tag enforcement, metadata lineage in `.bassia`, retry/abandon.
- `dotnet run -- agent -select app@v0,lib@v0 -run "..."` in a scratch monorepo, then inspect `.workspace/agentic-run-1`: both components are plain directories, `app/lib` is a reparse point resolving to the sibling `lib` checkout.
- `git -C .bassia/.agentic-runs/.git tag --list` after deleting `.workspace/agentic-run-1` — the run record and its tags are still there.
- `git -C .bassia status` — the meta-repo tree stays clean; `.agentic-runs` is ignored.
