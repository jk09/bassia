# Installing Bassia

This package holds everything Bassia needs that a machine with `dotnet` and `git` does not already have:

- the Bassia command-line client (`bassia/`), built for one platform;
- [weave](https://github.com/Ataraxy-Labs/weave)'s `weave-driver`, the entity-level merge driver Bassia's integration
  tries before the LLM resolver (`bassia/weave-driver`, licensed MIT or Apache-2.0, see `third-party/weave/`);
- the installer and the version manager (`scripts/bassia-versions.ps1`).

## Requirements

The installer checks these first and installs nothing when one is missing:

- **git**, on `PATH`.
- **The .NET runtime and the ASP.NET Core runtime** of the version in `package.toml` (`dotnet = "10.0"`): 10.0 or a
  later 10.x. A .NET SDK of that version includes both. Without them, install the ASP.NET Core runtime (it brings the
  .NET runtime along), for the current user:
  - Windows: `winget install --scope user Microsoft.DotNet.AspNetCore.10`
  - Linux, macOS: `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --runtime aspnetcore --channel 10.0`,
    then add `~/.dotnet` to `PATH` and set `DOTNET_ROOT=~/.dotnet`. Or use your package manager.
- **The right package for the machine**: `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` or `osx-arm64`.

Optional: [Claude Code](https://claude.com/claude-code) (`claude`) is the default agent and semantic merge resolver.
Any other agent can be configured (`bassia config set -key agent.command ...`).

## Install

Unpack the archive and run the installer from its folder. Nothing needs administrator or root rights.

```sh
# Linux, macOS
tar -xzf bassia-<version>-<platform>.tar.gz
./bassia-<version>-<platform>/install.sh            # --check only verifies the requirements
```

```powershell
# Windows: unzip, then
.\bassia-<version>-win-x64\install.cmd               # or install.ps1; -Check only verifies the requirements
```

On macOS, a package downloaded with a browser is quarantined, and Gatekeeper refuses its unsigned programs; clear
that first with `xattr -dr com.apple.quarantine bassia-<version>-<platform>` (downloads with `curl` are not affected).

The version goes into its own folder under the install root (`$BASSIA_HOME`, else `%LOCALAPPDATA%\Bassia` on Windows,
`~/.local/share/bassia` elsewhere), becomes the default (`<root>/current`), and `<root>/current` is put on `PATH` (the
user PATH on Windows; a line sourcing `<root>/env.sh` in `~/.profile`, `~/.bashrc` and `~/.zshrc` elsewhere). Open a
new terminal and run `bassia version`.

Installer options: `--root DIR` / `-Root`, `--no-use` / `-NoUse` (keep the current default), `--no-modify-path` /
`-NoModifyPath`, `--force` / `-Force` (reinstall).

## Several versions

Installing another package adds a version beside the others. To list, switch or remove versions, use
`scripts/bassia-versions.ps1 list | use <id> | uninstall <id>` (PowerShell), or on Linux and macOS without PowerShell:

```sh
ls ~/.local/share/bassia/versions                    # installed versions
./install.sh --uninstall <id>                        # remove one; --uninstall all removes Bassia completely
. ~/.local/share/bassia/activate.sh <id>             # use another version in this shell only
```

## For maintainers

CI (`.github/workflows/ci.yml`) builds these packages for every platform on each push and pull request, installs
each one on a machine of its platform to check it, and attaches them to a GitHub release when a `v<semver>` tag is
pushed. To build one locally (PowerShell 7 and the .NET SDK):

```powershell
./packaging/New-BassiaPackage.ps1 -Rid linux-x64     # -> dist/bassia-<id>-linux-x64.tar.gz
```

The bundled weave release and its checksums are pinned in `packaging/weave.toml`.
