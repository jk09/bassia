# Select a component by commit hash

## Outcome

`bassia run start -select <component>@<commit-ish>` accepts a commit hash in place of an annotated tag: `-select app@3f9c2e1,lib@v1` starts a run with `app` at commit `3f9c2e1…` and `lib` at tag `v1`. Any hash prefix of at least 6 hex digits up to the full hash works. A selector that git cannot resolve to exactly one object, or that could mean both a ref and an object, is rejected before anything is materialized, with git's own error passed through where git reports the ambiguity.

## Context

`-select` demands an annotated tag for every component ([bassia-8ab66e1d](../../done/bassia-8ab66e1d/SPEC.md), [bassia-3a5b27a6](../../done/bassia-3a5b27a6/SPEC.md)) because the selector is the run's immutable, reproducible provenance. Branches and `HEAD` are mutable, so they stay rejected. A commit hash is just as immutable as an annotated tag, so requiring a tag first (`bassia component tag ...`) only adds a step. The condition is that the selector stays unambiguous: a short hash must name one object, and must not also be readable as a ref name.

## Acceptance criteria

- [x] `-select <c>@<hash>` with a hash prefix of 6 to 40 lowercase hex digits (64 for SHA-256 repos) that names a commit in the component's source-of-truth repo starts the run from that commit. The run record keeps the selector as given (`commitish`) and the full resolved commit (`commit`).
- [x] Annotated tags work as before, and can be mixed with hashes in one selection.
- [x] A hash prefix that matches more than one object fails the run before anything is materialized, and the message carries git's own ambiguity error (`short object ID … is ambiguous` with git's candidate list).
- [x] A selector made of hex digits that is both a ref name (for example a tag `abc123`) and the prefix of an object hash fails as ambiguous, naming the ref and pointing to `refs/tags/<name>` or a longer hash as the way out.
- [x] A hash that names no object fails with `does not exist`; a hash that names a tree, blob or tag object fails with `is a <type>, not a commit`.
- [x] Branches, `HEAD` and lightweight tags are still rejected; hex strings shorter than 6 digits are treated as ref names, never as hashes.
- [x] `run start -detach`, `help run start` and the README describe and accept the new selector.

## Approach

All resolution stays in `AgentCommand.ResolveCommitAsync`, the single point every `-select` path (`run start`, `-detach`, the web dashboard) goes through, so the rest of the run (materialization, record, results) is unchanged: it already works from the resolved commit.

A selector is treated as a hash candidate when it is 6–64 lowercase hex digits. For such a candidate:

1. Ask git whether it is also a ref (`rev-parse --symbolic-full-name`) and which objects carry it as a prefix (`rev-parse --disambiguate`).
2. A ref with no matching object: fall through to the annotated-tag path, exactly as before.
3. A ref and a matching object: reject as ambiguous.
4. No matching object: reject as missing.
5. Otherwise resolve with `rev-parse --verify --end-of-options <hash>`; when git fails (ambiguous prefix), propagate its stderr. The resolved object must be a commit.

## Decisions

- **Ambiguity is judged across all object types**, not only commits, matching how git abbreviates hashes in `git log`: a prefix git itself would print as unique always resolves, and a prefix shared with a blob or tree is rejected instead of silently picking the commit (`<hash>^{commit}` disambiguation would). This keeps a selector meaningful regardless of how git disambiguates it.
- **Ref/hash collisions are rejected, not resolved by precedence.** git silently prefers the ref (with only a warning); Bassia refuses, since a run selector must mean one thing. `refs/tags/<name>` and a longer hash remain unambiguous spellings.
- **Only commits.** A hash naming an annotated tag object, tree or blob is rejected; tags are selected by name.
- **Lowercase hex only, minimum 6 digits**, as requested. Mixed-case or shorter strings are ref names.
- **The record keeps the selector as typed.** The full commit hash is already recorded per component (`commit`), and nothing re-resolves `commitish` later, so a short hash that becomes ambiguous as the repo grows does not affect the recorded run.

## Progress

- [x] Hash resolution in `AgentCommand.ResolveCommitAsync`, with ref/hash collision and git error propagation.
- [x] Tests: short and full hash, mixed with tags, ambiguous prefix (crafted colliding commits), ref/hash collision, missing hash, non-commit object, too-short hex.
- [x] Help text and README.

## Validation

- `dotnet test Bassia.Tests --filter "FullyQualifiedName~SelectByHashTests|FullyQualifiedName~AgentCommandTests"`: the new `SelectByHashTests` cover every acceptance criterion against throwaway monorepos (6-digit and full hash mixed with a tag, two crafted commits sharing a 6-digit prefix, a hex-named tag colliding with a hash prefix, unknown hash, tree hash, short and non-colliding hex tag names); `AgentCommandTests` keep the tag-only rejections green.
- `dotnet test`: 226 of 228 pass. The two end-to-end scenarios that clone `https://github.com/jk09/example.git` cannot run without network access to GitHub and fail in `component add`, before any selection is resolved.
- `bassia help run start` shows `-select <component@tag|hash,...>` and a hash example.
