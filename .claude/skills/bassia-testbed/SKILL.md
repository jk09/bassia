---
name: bassia-testbed
description: Recreate the bassia-testbed monorepo - a Bassia monorepo whose components are all the git repos under C:\Users\jozef\Development (submodules as referenced components), used as a testbed for developing Bassia. Use when asked to (re)create, rebuild, reset or refresh the testbed.
---

# bassia-testbed

A Bassia monorepo at `C:\Users\jozef\Development\bassia-testbed`, empty except for 85 registered components: every git
repo in `Development`, including those nested in non-git folders (e.g. `JavaScript\my-app`).

## Recreate

1. Build the CLI: `dotnet build Bassia/Bassia.csproj` (the script uses the Debug `Bassia.exe`).
2. Remove or rename an existing `bassia-testbed` (the script refuses to overwrite). Look at it first.
3. Run `.claude/skills/bassia-testbed/New-Testbed.ps1` (`-Dev`, `-Root`, `-Exe` override the defaults). It takes a few
   minutes: it clones ~85 repos locally with `bassia component add`.
4. Verify with `bassia -C <root> status` (85 components, clean meta-repo) and `bassia -C <root> graph`.

## Rules the script encodes

- **Excluded:** `bassia-testbed` itself, `bassia`, `jkmonorepo` (an older Bassia monorepo), non-repos, and git
  worktrees (`vibrowse-agent-runs`, `ContractExpressions.worktrees\*` - their branches are already in the main repo).
- **Source:** components are cloned from the local folders (`component add -url <local path>`), so unpushed commits and
  local branches come along; uncommitted working-tree changes do not. Each bare repo's `origin` is then set to the
  source's real remote (origin, else the first remote); `components.toml` keeps the local path as the url.
- **Trunk:** the bare repo's HEAD is set to the trunk, picked per repo (`main`/`master`, or the only branch). Odd
  cases: `Infobip` master (not `development`), `ConwaysLife`/`EfCoreinAction-SecondEdition` master, `WebbCompare`
  gh-pages, `eShopOnContainers` dev, `color-thief` color-thief-main (the branch the parent pins).
- **Names:** folder names, spaces to `-`; collisions renamed (`JavaScript-my-app`, `react-sandbox-my-app`,
  `ReactActivities-act21`).
- **Submodules** become components, added first, and the parent references them at the submodule path
  (`-references name:path`): `ContractExpressions` -> `CodeContracts`; `EdgeTabHandler` -> `color-thief`; `UnitGen` ->
  `FluentTerminal` (the existing component, which contains the pinned commit), `ILSpy`, `CodeContracts`,
  `UnitGen-FsCheck` under `ExternalPrograms/`. `UnitGen-FsCheck` is separate because the top-level `FsCheck` lacks the
  pinned commit. `snappymail`'s `.gitmodules` entry has no gitlink, so it is not a submodule.

## When the set of repos changed

The component list and trunks are hard-coded in the script. Re-survey `Development` (top-level folders, nested `.git`
folders to depth 3, `.gitmodules` + `git ls-files -s | grep ^160000` for real submodules, `.git` files for worktrees),
update the script's tables, then recreate. The script verifies each trunk branch exists in its source and stops if not.
