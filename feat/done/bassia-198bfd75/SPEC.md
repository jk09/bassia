# Installable Bassia packages from CI

## Outcome

CI builds a deployable package of the whole Bassia app for every supported platform: the CLI, weave's
`weave-driver` and an installer. The package installs on a user's machine that has only `dotnet` (the runtime) and
`git`, which the installer verifies before it installs anything. Packages are CI artifacts on every push and pull
request, and a GitHub release when a `v<semver>` tag is pushed.

## Context

Until now Bassia was installed by building from a clone (`scripts/bassia-versions.ps1`, the `bassia-install` skill),
which needs the .NET SDK, and weave had to be installed separately for the structural merge. The repository had no
CI at all, so several feature records note that their tests were never executed. weave publishes prebuilt
`weave-driver` binaries for macOS (arm64, x64), Linux (arm64, x64; glibc 2.35+) and Windows x64 on its GitHub releases,
with checksums. The CLI targets .NET 10 and references the ASP.NET Core shared framework (`bassia web`).

## Acceptance criteria

- [x] A CI workflow runs on pushes to `main`, pull requests, `v*` tags and on demand. It builds and runs the test
      suite, and builds one package per platform: `win-x64` (zip), `linux-x64`, `linux-arm64`, `osx-x64`,
      `osx-arm64` (tar.gz), each with a `.sha256` file, uploaded as artifacts.
- [x] A package contains the CLI published framework-dependent for its platform, stamped with its version
      (`bassia version`), weave's `weave-driver` for the same platform, the installer(s), the version manager and its
      templates, a README with install instructions, Bassia's license and weave's licenses.
- [x] The bundled weave release is pinned (version and per-target SHA-256) in the repository; a download that does not
      match is refused and fails the build.
- [x] The installer (`install.sh` on Linux/macOS, `install.ps1`/`install.cmd` on Windows) first verifies git, the .NET
      and ASP.NET Core runtimes of the required major version, and that the package matches the machine's platform. If
      any is missing it names it, prints a per-user install hint, and installs nothing. `--check`/`-Check` only verifies.
- [x] The installer installs without elevation into the same side-by-side layout as `scripts/bassia-versions.ps1`
      (`<root>/versions/<id>`, `current`, activation scripts, PATH registration), smoke-tests `bassia version` and
      `weave-driver --version` before moving the version into place, and supports `--root`, `--no-use`,
      `--no-modify-path`, `--force`. `bassia-versions.ps1 list | use | uninstall` work on such installs, and
      `install.sh --uninstall <id>|all` removes them without PowerShell.
- [x] An installed Bassia uses the bundled `weave-driver` even when it is not on `PATH`; a `weave-driver` on `PATH` or
      a configured driver still takes precedence.
- [x] Each package is installed and exercised on a runner of its own platform before upload: refusal without
      runtimes, install, `bassia version`, `weave-driver --version`, a monorepo whose integration triage picks up the
      bundled weave, the version manager's list, and uninstall.
- [x] A `v<semver>` tag publishes the packages and a `SHA256SUMS` file as a GitHub release (a pre-release for a
      pre-release tag).

## Approach

`packaging/New-BassiaPackage.ps1` (PowerShell 7, runs on every runner and on a developer machine) publishes the CLI
for a RID, fetches weave through `packaging/Get-Weave.ps1` (pinned in `packaging/weave.toml`), stages the installers
and archives the package. The installers share the layout and the activation/env templates (moved out of
`bassia-versions.ps1` into `scripts/templates/`) with the version manager: Windows' `install.ps1` runs
`bassia-versions.ps1 install -Package`, and `install.sh` implements the same steps in bash, because PowerShell is not a
basic dependency on Linux or macOS. `StructuralMerge` falls back to a `weave-driver` next to the Bassia executable when
the configured bare driver name is not on `PATH`. `packaging/Test-BassiaPackage.ps1` is the per-platform install check
CI runs. `.github/workflows/ci.yml` wires it together.

## Decisions

- **Framework-dependent, not self-contained**: the user is assumed to have `dotnet`; the package stays ~9 MB plus
  weave instead of ~100 MB. "dotnet" means the .NET and ASP.NET Core runtimes (an SDK includes both); the installer
  checks `dotnet --list-runtimes` for both at the target major version.
- **Platforms are weave's**: weave has no Windows arm64 or musl build, so there are no such packages.
- **weave is pinned by checksum in the repository**, not trusted from its release's own `checksums.txt` at build time.
- **Per-platform native runners** build and verify each package (macOS apphosts must be signed on macOS; each package
  is smoke-tested where it will run).
- **Install layout reuses `bassia-versions.ps1`'s**, so packaged and source-built versions live side by side and the
  existing skills manage both.
- **Version ids follow `bassia-versions.ps1`**: the tag for a `v<semver>` tag build, `<branch>-<short commit>` otherwise.
- **Claude Code is not bundled**: it is the default agent/resolver but optional and configurable; the installer only
  notes when `claude` is not on `PATH`.
- **CI's end-to-end tests clone a local upstream** (`BASSIA_E2E_COMPONENT_URL`) because the default, `jk09/example`, is
  private; the pinned weave is put on `PATH` so the real-weave test runs too.
- **No open questions were raised**: platforms, trigger events and release-on-tag were inferred from the request and
  weave's supported targets.

## Progress

- [x] Packaging, installers, weave pinning, bundled-driver fallback and CI workflow.
- [x] Local validation on linux-x64: full test suite, package build, `Test-BassiaPackage.ps1`, `install -Package` via
      PowerShell, source install via `bassia-versions.ps1` after the template refactor.
- [x] CI green on every platform.

## Validation

- `dotnet test` (with `BASSIA_E2E_COMPONENT_URL` pointing at a local upstream and `weave-driver` on `PATH`).
- `./packaging/New-BassiaPackage.ps1 -Rid <rid>` then `./packaging/Test-BassiaPackage.ps1 -Archive <archive>`.
- The `CI` workflow on a pull request: `Test` and `Package (<rid>)` for all five platforms pass; on a `v*` tag the
  `Release` job publishes the packages.
