# Install and manage Bassia versions with skills

## Outcome

Anyone with a clone of the Bassia sources can set up the local Bassia client by asking an AI agent to run a skill. The
skill builds the latest tagged release (or a chosen tag, branch or commit; Release or Debug) after checking that the
machine has a suitable .NET SDK, and installs it for the current user. Several versions can be installed side by side,
one is the default, and any other can be used per shell or per project. Installing, removing and selecting versions
need no elevated privileges.

## Context

Until now Bassia was used straight from `bin/Debug` of a working clone (`bassia.cmd`, `scripts/Bassia.common.ps1`).
Several developers and agents need the newest version while others keep using older ones, much like nvm (side-by-side
Node versions with a switchable default) and venv (activation in one shell). The CLI is a .NET 10 console app
(`Bassia/Bassia.csproj`, ASP.NET Core framework reference) and needs git at run time. `bassia version` already prints
the assembly's informational version. The repository had no release tags when this was written.

## Acceptance criteria

- [x] `/bassia-install` without arguments resolves the newest `v<major>.<minor>.<patch>` release tag (pre-releases only
      with `prerelease`), after fetching tags from `origin`; a version argument picks a tag (`v0.2.0` or `0.2.0`), a
      branch or a commit instead.
- [x] Before building, the requirement (.NET SDK from `global.json`, else from the CLI's target framework) is checked
      against `dotnet --list-sdks` and git. When nothing suitable is installed, it prints a per-user (non-elevated)
      SDK install command and stops.
- [x] The build is Release by default and Debug on request. It is built from the exact commit in a temporary git
      worktree, leaving the clone's working tree untouched. It is stamped with the version (`bassia version` reports
      e.g. `0.2.0+e23d631`) and smoke-tested before it is installed. A failed build installs nothing and leaves no
      worktree behind.
- [x] Each version installs into its own folder under a per-user root (`$BASSIA_HOME`, `%LOCALAPPDATA%\Bassia`,
      `~/.local/share/bassia`), with a record of ref, commit, configuration and SDK. Several versions coexist.
- [x] A `current` link (a junction on Windows, a symlink elsewhere) names the default. Only it is put on the user's
      PATH, once (the user PATH in the registry on Windows, a sourced `env.sh` line in the shell profiles elsewhere).
      `/bassia-use <version>` switches the default without touching PATH again.
- [x] `activate.ps1` / `activate.sh` put another version on PATH in one shell only (`bassia_deactivate` undoes it).
      Without an argument they read the nearest `.bassia-version` file.
- [x] `/bassia-uninstall <version>` removes one version and refuses to remove the default unless forced.
      `/bassia-uninstall all` removes every version, the link, the scripts and the PATH registration.
- [x] No step requires administrator or root rights.

## Approach

One PowerShell 7 script, `scripts/bassia-versions.ps1` (`check`, `available`, `install`, `list`, `use`, `uninstall`),
holds all the logic. Three thin skills drive it: `.claude/skills/bassia-install`, `bassia-use` and `bassia-uninstall`.
Their arguments map onto the script's switches, and they tell the agent when to ask the user (installing an SDK,
removing the default, removing everything). Each version is a `dotnet publish` output in `versions/<id>`. The id is
the tag, or `<ref>-<short commit>` for an untagged build, with `-debug` appended for a Debug build. Publishing goes to a
staging folder that is moved into place only after the smoke test passes, so an install is all-or-nothing.

## Decisions

- **Release tags are `v<semver>`** (`v0.1.0`, `v1.2.0-rc.1`), ordered by semver precedence. `latest` skips
  pre-releases unless asked. The skill creates no tags: the maintainer tags releases (the first one, `v0.1.0`, after
  this lands).
- **Framework-dependent publish by default** (about 5 MB per version, using the .NET runtime the required SDK brings),
  with `-SelfContained` for a version that must outlive the installed runtime (about 100 MB).
- **Three skills over one script** rather than one skill with subcommands: each skill's description triggers on its
  own intent, and the logic stays in one place.
- **Versioned folders plus a `current` link on PATH**, the nvm-windows / rustup-toolchain pattern, rather than
  `dotnet tool install` (global tools allow one version at a time; local tool manifests need a NuGet feed and run as
  `dotnet bassia`) or shim executables (startup cost, argument quoting and Ctrl+C issues with `.cmd` shims).
  Junctions and user symlinks need no elevation. The per-shell override is opt-in activation, as with venv.
- **Build from a temporary worktree of the exact commit**, so local changes never leak into an installed version and
  the build's sources match the recorded commit.

## Progress

- [x] Version manager script with check/available/install/list/use/uninstall
- [x] Skills `bassia-install`, `bassia-use`, `bassia-uninstall`; README "Installing" section
- [x] Validated on Linux (pwsh 7.6, .NET SDK 10.0.112) against a scratch clone with test tags
- [ ] Windows run (junction, user PATH in the registry) by the maintainer, on the first real release tag

## Validation

On Linux, against a scratch clone with local tags `v0.1.0`, `v0.2.0`, `v0.3.0-rc.1`, `v0.3.0-rc.2`, `v0.10.0-alpha`,
a non-release tag and a deliberately broken `v9.0.0`, with `HOME` pointed at a scratch folder:

- `available` / `available -Prerelease`: semver order (`v0.10.0-alpha` above `v0.3.0-rc.2`), `latest` = `v0.2.0`.
- `check`: exit 0 with SDK 10.0.112. With no `dotnet` on PATH: exit 1 and the `dotnet-install.sh` hint. Unknown
  version: exit 1, listing the release tags.
- `install`: `v0.2.0` became the default and `~/.profile` sources `env.sh`. A login shell resolves `bassia` to
  `current/bassia`, and `bassia version` prints `0.2.0+e23d631`. `install v0.1.0 -Configuration Debug -NoUse`,
  `install 0.3.0-rc.2 -NoUse` and `install HEAD~2 -NoUse` installed side by side and the default was kept.
  Re-installing an existing version is a no-op without `-Force`. `-SelfContained` produced a 107 MB version that runs
  without `DOTNET_ROOT`. The broken tag failed with its compiler errors, and no version folder or worktree was left.
- `activate.sh` / `activate.ps1`: an explicit version, and `.bassia-version` from a parent folder, override the
  default in that shell. `bassia_deactivate` restores it. An unknown version lists the installed ones.
- `use`: retargets `current`. `uninstall` of the default is refused with a suggestion, then removed with `-Force`.
  `uninstall -All` removed the versions, scripts, root and profile line.
