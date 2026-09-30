# Split a component into several components, with history

## Outcome

`bassia component split` breaks one registered component into several new components according to a **split
plan**: a TOML file that allocates the component's files to the new parts by path patterns. The plan is meant to be
written by an LLM - `bassia component survey` gives it a machine-readable picture of the component (folders, sizes,
change frequency, which folders change together), and `component split -dry-run` checks a plan and reports exactly
what is wrong with it without changing anything, so an agent can iterate until the plan is valid. Each new part is a
new source-of-truth repository whose history is rewritten from the source's: every file keeps its full timeline
(across renames and moves), with the original authors, dates and messages, so `git log --follow` and `git blame`
work in the new component. The meta-repo registers the parts, rewires references, and retires the source.

## Context

A Bassia component is the unit of scalability: a run clones every component it selects, and git's cost grows with a
repository's size and history. As a monorepo evolves, a component that grew too large or mixes architecturally
distinct parts must be broken up to keep runs cheap and dependencies precise. Deciding the breakdown is judgement
work, well suited to an LLM; performing it - rewriting history, creating repositories, updating `components.toml` -
must be mechanical, deterministic and safe, which is the CLI's job. Only the command line is in scope (`bassia ui`
and `bassia web` are not changed).

Related records: `bassia-c17712cc` (the command line and its TOML results), `bassia-a7b47671`.

## Acceptance criteria

Plan and validation

- [x] `bassia component split -plan <file>` (or `-plan -` for stdin) reads a TOML plan: `source` (overridable with
      `-name`), optional `branch` (default: the source's default branch), optional `shared` and `drop` patterns, and one
      `[[part]]` per new component with `name`, `paths` (glob patterns; `**`, `*`, `?`, a folder name matches
      everything under it, `!pattern` excludes), optional `references` (default: the source's references) and
      optional `url`; optional `[[referrer]]` entries replace the references of components that referenced the source.
- [x] Every file at the tip of the split branch must be allocated to exactly one part, or match `shared` (copied
      into every part) or `drop` (left out of every part, history included). Unallocated files, files matched by several parts, parts that match no file, invalid or
      taken names, unknown references and cycles are all reported together as one TOML error, and nothing changes.
- [x] `-dry-run` validates the plan and reports the allocation (files and bytes per part, per-folder breakdown,
      the resulting dependency graph) without creating or changing anything.
- [x] A split is refused while a run on the source component is live.

History

- [x] Each part is a new bare repository `<root>/<part>/.git` whose default branch has the same name as the split
      branch. Its history contains every commit of the source that changed one of its files, rewritten to its files
      only, with the original author, committer, dates and message, plus a `Split-from: <source> <original commit>`
      trailer. Commits that change none of its files are dropped; merges that become redundant are dropped.
- [x] A file's history follows renames and moves: when a part's file was renamed (e.g. moved into its folder from
      a folder another part owns or nobody owns), its history under the earlier path(s) is part of the part's
      history too, so `git log --follow <file>` in the new repository reaches the file's first commit.
- [x] The tip of each part holds exactly the files allocated to it, with the content of the source's tip (verified
      before anything is registered; a mismatch aborts the split and removes the new repositories).
- [x] Tags of the source reachable from the split branch are carried into every part that has content at the
      tagged commit (annotated tags keep their tagger and message), so a baseline such as `v1` can be selected as
      `part@v1`.
- [x] Each part's branch ends with a split record commit (no file change) whose message is a subject and a TOML
      `[split]` record (id, source, source commit, part, paths, the other parts); it is tagged `split/<id>` in every
      part, and the source's split commit is tagged `split/<id>` in the source repository.

Meta-repo

- [x] The parts are registered in `components.toml` (with their references), components that referenced the
      source reference the parts instead (all of them, or as `[[referrer]]` says), and the source is unregistered;
      its repository is kept on disk untouched apart from the `split/<id>` tag. All of this is one meta-repo commit
      whose message carries the split record.
- [x] The result reports the split id, the parts (name, path, files, commits, tags), dropped tags, and the source's
      run results that are not in the split branch (they stay on the retired source).
- [x] If any step fails, the new repositories are removed and `components.toml` is left as it was.

Survey

- [x] `bassia component survey -name <c> [-depth <n>] [-branch <b>]` reports, as TOML, the component's folders to
      the given depth with file counts, bytes and number of commits touching them, the top-level files, the pairs of
      folders most often changed in the same commit, and a plan skeleton to start from.

## Approach

- A `Split` model separate from the command: plan parsing and validation (patterns compiled to regexes), history
  reading, rewriting, and repository creation; `ComponentCommands` only wires switches to it.
- History is read in one pass with `git fast-export --no-data --show-original-ids` (commits, parents, author and
  committer lines, messages, file changes relative to the first parent) and renames in one pass with
  `git log -M --diff-filter=R`. Byte-exact parsing (messages are counted in bytes).
- Ownership of a path: the tip allocation for files at the tip, the first matching part for other paths, every part
  for `shared` paths, plus rename claims: walking commits newest first, a rename `old -> new` where `new` belongs to
  a part makes `old` belong to that part too (only in commits that are ancestors of the rename when `old` is reused
  at the tip).
- Each part is written by `git fast-import` into a new bare repository that borrows the source's objects through
  `objects/info/alternates`; `git repack -a -d` then copies the reachable objects and the alternate is removed, so
  the new repository is self-contained and holds only its own files' objects. The source repository is never
  rewritten.
- Pruning follows `git filter-repo`: a commit with no changes for the part maps to its parent; merge parents that are
  ancestors of another parent are dropped; when the first parent changes, the changes are recomputed against the
  kept parent (`git diff-tree`).
- `components.toml` is changed through `ComponentsFile` in one transaction (restored on failure) and committed once.

## Decisions

- Plan format is TOML, like every other file and result of Bassia; paths keep their location in the new
  components (no re-rooting), so the history and nested references stay identical - moving files is an ordinary
  follow-up change.
- The source repository is never rewritten: its history, run tags and records stay valid.
- (asked) The source is **retired**: unregistered from `components.toml`, its repository kept on disk as an archive
  (only the `split/<id>` tag is added). Rejected: keeping it registered as the remainder (would rewrite its history
  and orphan its run tags); deleting it (loses the provenance of old runs).
- (asked) The parts carry the **split branch and the tags reachable from it**. Rejected: all branches and tags
  (unmerged `agent/run-*` and `integration/*` refs belong to the retired source and would clutter every part);
  the branch alone (baselines such as `v1` would be lost).
- A tip path reused by another file after its earlier file was renamed away is yielded by its owner in the commits up
  to that rename, so each part gets its own file's timeline and not the other's. A non-tip path matched by a part's
  patterns keeps its history in that part even after it moved to another part (like a folder-based filter).
- Tags reachable from the branch are carried as they are, including merged `agent/run-*` and `integration/*` result
  tags, which then mark where a run's change entered each part's timeline.
- (asked) Unallocated files at the tip are an **error**; files meant to be left out of every part must be named by
  the plan's `drop` patterns (their history is dropped too). Rejected: silently dropping unmatched files.

## Progress

- [x] Spec and open questions resolved
- [x] Plan parsing and validation, dry run
- [x] History rewrite and repository creation
- [x] Meta-repo update, result, rollback
- [x] Survey
- [x] Tests, README, help

## Validation

Verified on 2026-09-30 (Linux, git 2.43, .NET 10.0.112): `dotnet test --filter Category!=EndToEnd` passes except
`AgentCommandTests.Agent_ComponentChain_IsCheckedOutSideBySideAndJunctionedIntoItsReferrers`, which fails on Linux
before this change as well. A synthetic component of 4,980 commits (99 true merges, 3,000 files, folder moves between
parts) split into three parts in about 3.3 s; every part kept all 99 merges, its tip verified, and a sample of 180
files had `git log --follow` histories identical to the source's. A split was refused while a detached run on the
source was live.

- `dotnet build`
- `dotnet test --filter Category!=EndToEnd` - including new `ComponentSplitTests` that build a source component with
  a branchy history (moves between parts, a merge, tags, a deleted file) and check each part's `git log --follow`,
  tip tree, tags, record commit and trailers, plus plan errors, dry run, rollback and survey.
