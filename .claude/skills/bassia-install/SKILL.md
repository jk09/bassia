---
name: bassia-install
description: Build and install a version of the Bassia CLI for the current user from this clone, side by side with other installed versions (nvm-style, no elevation). Picks the latest v<semver> release tag unless a version is given; checks the .NET SDK requirement first. Use when asked to install, set up, build-and-install, upgrade or update the local bassia client. Accepts optional arguments - a version (tag such as v0.2.0, `latest`, or a branch/commit) and the words `debug`, `self-contained`, `prerelease`, `keep-default`, `force`.
---

# bassia-install

Installs Bassia with `scripts/bassia-versions.ps1`, the version manager shared by `/bassia-install`, `/bassia-use` and
`/bassia-uninstall`. Every version gets its own folder under the install root (`$BASSIA_HOME`, else
`%LOCALAPPDATA%\Bassia` on Windows, `~/.local/share/bassia` elsewhere). The link `<root>/current` points at the
default version, and only that link is on the user's PATH. No step needs administrator or root rights.

Run the script with PowerShell 7: `pwsh -NoProfile -File scripts/bassia-versions.ps1 ...`, from the root of this clone.
On Windows without `pwsh`, use `powershell -NoProfile -ExecutionPolicy Bypass -File ...` instead.

## Arguments

Map the skill's arguments onto the script's parameters:

| Skill argument | Script |
| --- | --- |
| a version: `v0.2.0`, `0.2.0`, `latest` (the default), or a branch/commit such as `main` | `install <version>` |
| `debug` | `-Configuration Debug` (Release is the default); the version id gets a `-debug` suffix |
| `self-contained` | `-SelfContained`: about 100 MB, no dependency on an installed .NET runtime |
| `prerelease` | `-Prerelease`: `latest` may pick a pre-release tag such as `v0.3.0-rc.1` |
| `keep-default` | `-NoUse`: install it without making it the default |
| `force` | `-Force`: rebuild a version that is already installed |

For example: `/bassia-install` installs the latest release, and `/bassia-install v0.2.0 debug keep-default` installs a
Debug build of v0.2.0 without changing the default.

## Steps

1. **Pick the version.** If none was given, run `available` and say which tag `latest` resolves to. If the repository
   has no `v<major>.<minor>.<patch>` tag, say so and offer to install `main` instead. Don't create a tag yourself:
   releases are tagged by the maintainers.
2. **Check the requirements.** Run `check [<version>]` (add `-Prerelease` if it was given). It reads the SDK the
   version needs (from `global.json`, else from the CLI's target framework) and lists the SDKs `dotnet --list-sdks`
   reports. If it exits 1, tell the user what is missing and show the per-user install command it printed. Install
   the SDK only if the user agrees, then run the check again.
3. **Install.** Run `install [<version>]` with the mapped switches. It fetches tags from `origin`, checks the version
   out into a temporary git worktree (the clone's working tree is left alone), runs `dotnet publish` into a staging
   folder, smoke-tests the result with `bassia version`, and only then moves it into `versions/<id>`. A failed build
   leaves nothing installed and prints the compiler errors; report them.
4. **Report** the installed id, the version `bassia version` prints, and whether it is now the default. The first
   install registers `<root>/current` on the user's PATH: the user PATH in the registry on Windows, or a line in
   `~/.profile` (plus `~/.bashrc` and `~/.zshrc` if they exist) that sources `<root>/env.sh` elsewhere. Remind the user
   to open a new terminal. To run the new version right away, call `<root>/current/bassia version`.

Other installed versions stay usable: see `/bassia-use` to switch the default, or to use another version in one shell
only.
