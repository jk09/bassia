# Submodule unwinding and multi-component tags

## Outcome

`bassia` turns the git submodules of a component into components of their own ("unwinding"), recursively, and
registers each one as a reference of its parent at the submodule's path, so a run nests it as a junctioned folder like
any other referenced component. Unwinding is offered when a component is added and as a command for an existing
component. Where submodules pin commits that a component's single main branch cannot express, Bassia links the
matching commits across components with identically named tags. A new `bassia tag` command family tracks such
multi-component tags - for unwinding and for agentic runs and integrations that span components - and lists every
tag with the number of components it tags.

## Context

- One of Bassia's goals is to replace submodules. A git repo added as a component may still contain submodules
  (gitlinks, mode `160000`, described by `.gitmodules`), which hides part of the monorepo structure from
  `components.toml`, the graph, run selection and integration.
- Today a submodule can only be mapped by hand (`component add` the submodule, then `-references name:path`, as the
  `bassia-testbed` skill does). That mapping is not runnable: checking out the parent creates an empty folder at the
  gitlink path, and `run start` refuses to place a junction on an existing path.
- A submodule's identity is its remote address (e.g. `https://github.com/owner/repo`), so the same submodule found in
  several components - or nested at several depths - becomes one component.
- The same submodule may be pinned at different commits by different components, while each component has a single
  main branch. Main can therefore express at most one combination; the others are kept as tagged off-main states.
- Identically named tags across components already exist: integrations tag their results `integration/<key>/<n>` in
  every component they touch, and runs push result tags. Nothing lists them as a monorepo-wide set.
- Related records: `bassia-d3226a2c` (component add), the run start / integration records in `feat/done/`.

## Acceptance criteria

### Unwinding

- [x] `bassia component add -url <url> -unwind` adds the component and unwinds its submodules; without `-unwind`
      `component add` behaves exactly as today.
- [x] `bassia component unwind -name <c> [-dry-run]` unwinds the submodules of an existing component.
      `-dry-run` prints the plan (components to add or reuse, references, commits and tags to create) and changes
      nothing.
- [x] Unwinding is recursive: submodules of an unwound submodule are unwound too, at any depth.
- [x] A submodule's identity is its normalized remote URL: relative URLs (`../lib.git`) resolve against the parent
      component's URL; scheme (`https://`, `ssh://`, `git@host:`), user info, a trailing `.git` or `/`, and host case
      do not matter. A submodule whose identity matches a registered component's URL reuses that component; otherwise
      it is added as a new bare component named after its URL (a clash with an unrelated component of that name is
      resolved by prefixing the parent's name, `<parent>-<name>`).
- [x] After unwinding, the parent component's main branch has one new commit that removes the gitlinks and their
      `.gitmodules` entries (the file goes when it becomes empty), and `components.toml` records each submodule as a
      reference at its path (`-references <sub>:<path>`). `bassia run start -select <parent>` then materializes the
      submodule as a junctioned folder at that path.
- [x] Each pinned commit is linked to the parent's unwind commit by an identically named annotated tag
      `unwind/<parent>/<n>` in both components (and in every deeper component of a recursive unwind), so
      `run start -select <parent>@unwind/<parent>/<n>,<sub>@unwind/<parent>/<n>` reproduces exactly the pinned
      combination, whether or not it is on the submodule's main branch.
- [x] A pinned commit missing from the submodule's repository (after fetching it from the submodule's URL) fails the
      whole unwind before anything is changed, naming the submodule, path and commit. A `.gitmodules` entry without a
      gitlink is ignored, as git does.
- [x] A cycle (a submodule that, transitively, contains its parent) is rejected before anything changes.
- [x] The meta-repo is committed once per unwind (`components.toml` changes), and the result is TOML listing each
      unwound submodule: path, url, identity, component, added or reused, pinned commit, tag.

### Multi-component tags

- [x] `bassia tag list [-component <c>,...] [-prefix <p>] [-min <n>]` lists every tag across the components, one entry
      per tag name with the number of components it tags, those components and their commits, whether it is
      annotated, and its kind (`unwind`, `integration`, `run`, `split`, other). It is shown as a table and as `[[tag]]` entries,
      sorted by tag name; `-min 2` shows only multi-component tags.
- [x] `bassia tag show -tag <t>` shows one tag in every component that has it: commit, subject, tagger date, message.
- [x] `bassia tag create -tag <t> -select <c[@ref]>,... [-message <m>]` creates the same annotated tag in each selected
      component (default ref: its main tip) and creates none if it already exists in any of them.
- [x] Tags are read from the component repositories, which stay the single source of truth; no separate registry can
      drift from them.

### Compatibility

- [x] Existing commands, results and stored records are unchanged; help lists the new switches and commands.

## Approach

- **Survey.** Read `.gitmodules` (`git config --blob <main>:.gitmodules`) and the gitlinks
  (`git ls-tree -r <main>` mode `160000`) of the component's main branch tip in its bare repo.
- **Plan.** Build the whole unwind tree first (identity -> component, add or reuse, pinned commits, recursion),
  cloning new submodule components into a staging area and fetching missing pinned commits, and validate it
  (missing commits, cycles, name clashes). Nothing in the monorepo changes until the plan is complete; `-dry-run` stops
  here.
- **Apply.** Bottom-up: register new components, create the unwind commit in each parent (built with
  `git read-tree` / `update-index --force-remove` / `write-tree` / `commit-tree` on the bare repo, no checkout needed),
  advance main, create the `unwind/...` tags, set the references and commit the meta-repo once.
- **Tags.** A `MultiTag` reader over `git for-each-ref refs/tags` of every component repo, grouped by name; the
  `tag` commands and (later) the web dashboard use it.

## Decisions

- The unwind commit goes on top of the parent's main tip (history is kept, so the component stays git-compatible
  with its upstream). Older commits still contain the gitlink; they are reached through the `unwind/...` tags, whose
  parent commit is the unwind commit. Rejected: an off-main branch only (main could not be junctioned), rewriting
  the parent's whole history (every hash changes and the component diverges from upstream).
- Linking tags are created for every pinned commit, uniformly, not only when the pin differs from the submodule's
  main tip: a combination stays reproducible after either main moves on.
- Multi-component tags are derived from the component repositories (`refs/tags`), with no registry in the meta-repo.
- Tags are listed with a new `bassia tag list|show|create` command family rather than a `log` option.
- Every commit with submodules - a main tip or a pinned commit, at any depth - gets its own linking tag
  `unwind/<component>/<n>` over itself and everything it pins. A component pinned twice at different commits within
  one tree keeps the nearest pin in that tag and the other is reported as a conflict; it stays selectable through its
  own parent's tag. The main branch of every component in the tree is unwound too, so each is selectable by bare name.
- A pinned commit is fetched from the submodule's URL when the component's repository lacks it; new components are
  cloned into a staging folder under the monorepo root and moved into place only when the whole plan holds.
- Where a parent already references the submodule's component at the submodule's path (a hand-made mapping, as in
  the `bassia-testbed`), the reference is kept and only the gitlink goes.

## Progress

- [x] Specification and decisions.
- [x] Submodule survey, URL identity and unwind planning (with `-dry-run`).
- [x] Applying the unwind: components, references, unwind commits, tags.
- [x] `component add -unwind` and `component unwind`.
- [x] `tag list`, `tag show`, `tag create`.
- [x] Tests, README and help.

## Validation

Verified: `dotnet test` - every test passes except the two end-to-end tests that clone
`https://github.com/jk09/example.git`, which cannot authenticate to GitHub in the sandbox this ran in (unrelated to
this change).

- `Bassia.Tests/Unwind/RepoIdentityTests.cs`: URL identity and relative URL resolution.
- `Bassia.Tests/Unwind/UnwindCommandTests.cs`: nested submodules (`component add -unwind`), the same submodule
  pinned at different commits by two components (`component unwind`, reuse by identity, a relative URL, both
  combinations runnable from their tags, `tag list -min 2` counts), `-dry-run`, a missing pinned commit, a cycle, a
  name clash, a component without submodules, and `tag create|list|show`.

Planned before implementation:

- `dotnet test` with new tests that build repos with nested submodules (two parents pinning the same submodule at
  different commits, a nested submodule two levels deep, a relative submodule URL, a missing pinned commit), unwind
  them, and check `components.toml`, the unwind commits, the tags, `run start` junctions and `tag list` counts.
- Manually: `bassia component unwind -name UnitGen -dry-run` on the `bassia-testbed` monorepo.
