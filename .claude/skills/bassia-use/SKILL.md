---
name: bassia-use
description: List the installed Bassia CLI versions and switch the default one (nvm-style), or explain how to use another version in a single shell or project via activate / .bassia-version. Use when asked which bassia versions are installed or available, which one is active, or to switch, select, pin or downgrade the bassia version. Accepts an optional argument - the version id to make the default (e.g. v0.2.0, v0.1.0-debug); without one, it lists the versions.
---

# bassia-use

Selects among the Bassia versions that `/bassia-install` installed, using `scripts/bassia-versions.ps1`. Run it with
PowerShell 7 from the root of this clone: `pwsh -NoProfile -File scripts/bassia-versions.ps1 ...` (on Windows without
`pwsh`: `powershell -NoProfile -ExecutionPolicy Bypass -File ...`). No step needs elevation.

## Without an argument

Run `list` and show the result: every installed version id with its commit, configuration and install date. `*` marks
the default and `>` the version activated in the current shell. Also run `available` to show the release tags that
could still be installed (`/bassia-install <tag>`).

## With a version

1. Run `use <version>` (`0.2.0` also matches `v0.2.0`). It retargets the link `<root>/current`, which is the only
   Bassia entry on PATH, so every new and existing terminal runs the new default at its next `bassia` call. PATH
   itself is not changed.
2. If the version is not installed, the script lists the installed ones. Offer `/bassia-install <version>` instead.
3. Confirm with `<root>/current/bassia version`.

## One shell or one project only

Explain the override in the style of venv/nvm when the user wants a version without changing the default:

- PowerShell: `. "$env:BASSIA_HOME/activate.ps1" v0.1.0` (on Windows, `BASSIA_HOME` is set at install; otherwise use
  `%LOCALAPPDATA%\Bassia`). bash/zsh: `. "$BASSIA_HOME/activate.sh" v0.1.0`. This puts that version first on PATH for
  the current shell only and sets `BASSIA_VERSION`. Undo it with `bassia_deactivate`.
- A `.bassia-version` file holding a version id pins a project: running `activate` without a version in that folder,
  or any folder below it, uses the pinned version.

Skills run in their own shell, so they cannot activate a version in the user's terminal. Give the user the command
to run instead.
