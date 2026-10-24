#!/usr/bin/env bash
# Installs the Bassia release package this script is part of, for the current user (no sudo), side by side with other
# versions: the same layout as scripts/bassia-versions.ps1, which can list, switch and remove versions afterwards.
#
#   <root>/versions/<id>/       this package's bassia/ folder (Bassia, weave-driver), plus bassia-install.toml
#   <root>/current -> versions/<id>   the default version; only it is on PATH (via env.sh, sourced from the profiles)
#   <root>/activate.sh, activate.ps1, env.sh
#
# <root> is --root, else $BASSIA_HOME, else ${XDG_DATA_HOME:-~/.local/share}/bassia.
set -euo pipefail

usage() {
	cat <<'EOF'
Usage: ./install.sh [--check] [--root DIR] [--no-use] [--no-modify-path] [--force]
       ./install.sh --uninstall <version-id>|all [--root DIR] [--force]

  --check            only verify the requirements (git, the .NET and ASP.NET Core runtimes, the platform)
  --root DIR         install root (default: $BASSIA_HOME, else ${XDG_DATA_HOME:-~/.local/share}/bassia)
  --no-use           keep the current default version instead of switching to this one
  --no-modify-path   do not source <root>/env.sh from ~/.profile, ~/.bashrc and ~/.zshrc
  --force            reinstall a version that is already installed; with --uninstall, remove the default version too
  --uninstall ID     remove one installed version; 'all' removes every version, the scripts and the PATH entry
EOF
}

here="$(cd "$(dirname "$0")" && pwd)"
marker='# bassia: added by bassia-versions.ps1'
root="" check_only=0 no_use=0 no_modify_path=0 force=0 uninstall=""
while [ $# -gt 0 ]; do
	case "$1" in
		--check) check_only=1 ;;
		--root) root="${2:?--root needs a directory}"; shift ;;
		--no-use) no_use=1 ;;
		--no-modify-path) no_modify_path=1 ;;
		--force) force=1 ;;
		--uninstall) uninstall="${2:?--uninstall needs a version id or all}"; shift ;;
		-h|--help) usage; exit 0 ;;
		*) echo "install.sh: unknown argument '$1'" >&2; usage >&2; exit 2 ;;
	esac
	shift
done

step() { echo "bassia-install: $*"; }
fail() { echo "bassia-install: error: $*" >&2; exit 1; }

if [ -z "$root" ]; then root="${BASSIA_HOME:-${XDG_DATA_HOME:-$HOME/.local/share}/bassia}"; fi
mkdir -p "$root" 2>/dev/null || true
root="$(cd "$root" 2>/dev/null && pwd || echo "$root")"
versions="$root/versions"
current="$root/current"

default_version() {
	if [ -L "$current" ]; then basename "$(readlink "$current")"; fi
}

# ----- uninstall ----------------------------------------------------------------------------------------------------

unregister_path() {
	for file in "$HOME/.profile" "$HOME/.bashrc" "$HOME/.zshrc"; do
		if [ -f "$file" ] && grep -qF "$marker" "$file"; then
			grep -vF "$marker" "$file" > "$file.bassia-tmp" || true
			cat "$file.bassia-tmp" > "$file" && rm -f "$file.bassia-tmp"
			step "removed the env.sh line from $file"
		fi
	done
}

if [ -n "$uninstall" ]; then
	if [ "$uninstall" = "all" ]; then
		[ -L "$current" ] && rm -f "$current"
		unregister_path
		if [ -d "$versions" ]; then
			for dir in "$versions"/* "$versions"/.staging-*; do
				[ -d "$dir" ] || continue
				rm -rf "$dir"
				case "$(basename "$dir")" in .*) ;; *) step "removed $(basename "$dir")" ;; esac
			done
			rmdir "$versions" 2>/dev/null || true
		fi
		rm -f "$root/activate.ps1" "$root/activate.sh" "$root/env.sh"
		rmdir "$root" 2>/dev/null || true
		step "Bassia is uninstalled from $root"
		exit 0
	fi
	id="$uninstall"
	if [ ! -d "$versions/$id" ] && [ -d "$versions/v$id" ]; then id="v$id"; fi
	case "$id" in ''|.*|*/*|*..*) fail "'$uninstall' is not a version id" ;; esac
	[ -d "$versions/$id" ] || fail "version '$uninstall' is not installed. Installed: $(ls "$versions" 2>/dev/null | tr '\n' ' ')"
	if [ "$id" = "$(default_version)" ]; then
		[ "$force" = 1 ] || fail "$id is the default version. Switch to another one first, or pass --force (or --uninstall all)."
		rm -f "$current"
		step "$id was the default; no version is the default now"
	fi
	rm -rf "${versions:?}/$id"
	step "removed $id"
	exit 0
fi

# ----- the package --------------------------------------------------------------------------------------------------

manifest="$here/package.toml"
[ -f "$manifest" ] || fail "$manifest is missing; run install.sh from an unpacked Bassia package."
field() { sed -n "s/^$1 = \"\(.*\)\"[[:space:]]*\$/\1/p" "$manifest" | head -n 1; }
id="$(field id)" version="$(field version)" commit="$(field commit)" ref="$(field ref)"
rid="$(field rid)" dotnet_required="$(field dotnet)" weave="$(field weave)"
case "$id" in ''|.*|*/*|*..*) fail "package.toml names no usable id ('$id')" ;; esac
[ -n "$rid" ] && [ -n "$dotnet_required" ] || fail "package.toml is incomplete (rid, dotnet)"
[ -x "$here/bassia/Bassia" ] || fail "the package has no bassia/Bassia"

# ----- requirements -------------------------------------------------------------------------------------------------

case "$(uname -s)" in Linux) os=linux ;; Darwin) os=osx ;; *) os="$(uname -s | tr '[:upper:]' '[:lower:]')" ;; esac
case "$(uname -m)" in x86_64|amd64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) arch="$(uname -m)" ;; esac
host_rid="$os-$arch"
# A Mac with Apple silicon reports x86_64 to a shell running under Rosetta.
if [ "$os" = osx ] && [ "$arch" = x64 ] && [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = 1 ]; then host_rid=osx-arm64; fi

ok=1
step "package $id ($version, $rid${weave:+, weave $weave})"
step "requires git and the .NET $dotnet_required and ASP.NET Core $dotnet_required runtimes"

if [ "$host_rid" != "$rid" ]; then
	step "missing: this machine is $host_rid; use the bassia-*-$host_rid package instead"
	ok=0
fi

if command -v git >/dev/null 2>&1; then
	step "found:   $(git --version)"
else
	step "missing: git (https://git-scm.com/downloads, or your package manager)"
	ok=0
fi

# Whether `dotnet --list-runtimes` has the framework at the required major version, at or above its minor.
runtimes=""
if command -v dotnet >/dev/null 2>&1; then runtimes="$(dotnet --list-runtimes 2>/dev/null || true)"; fi
required_major="${dotnet_required%%.*}" required_minor="${dotnet_required#*.}"
dotnet_root="" runtime_missing=0
for framework in Microsoft.NETCore.App Microsoft.AspNetCore.App; do
	best=""
	while read -r name ver location; do
		[ "$name" = "$framework" ] || continue
		major="${ver%%.*}" rest="${ver#*.}" minor="${rest%%.*}"
		if [ "$major" = "$required_major" ] && [ "$minor" -ge "$required_minor" ] 2>/dev/null; then
			best="$ver"
			# [/path/to/dotnet/shared/Microsoft.NETCore.App] -> /path/to/dotnet
			location="${location#[}" location="${location%]}"
			dotnet_root="${location%/shared/*}"
		fi
	done <<< "$runtimes"
	if [ -n "$best" ]; then
		step "found:   $framework $best"
	else
		step "missing: $framework $dotnet_required.x"
		ok=0 runtime_missing=1
	fi
done
if [ "$runtime_missing" = 1 ]; then
	step "         Install it for the current user (no sudo): curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --runtime aspnetcore --channel $dotnet_required"
	step "         then add ~/.dotnet to PATH and set DOTNET_ROOT=~/.dotnet; or use your package manager (https://learn.microsoft.com/dotnet/core/install)."
fi

if ! command -v claude >/dev/null 2>&1; then
	step "note:    'claude' (Claude Code) is not on PATH; it is the default agent and merge resolver (agent.command, merge.resolver)"
fi

[ "$ok" = 1 ] || fail "the requirements above are not met; nothing was installed."
step "ok:      the package runs here"
[ "$check_only" = 1 ] && exit 0

# ----- install ------------------------------------------------------------------------------------------------------

destination="$versions/$id"
if [ -d "$destination" ] && [ "$force" != 1 ]; then
	step "$id is already installed at $destination (pass --force to reinstall it)"
else
	mkdir -p "$versions"
	rm -rf "$versions"/.staging-*
	staging="$versions/.staging-$id-$$"
	trap 'rm -rf "$staging"' EXIT
	cp -R "$here/bassia" "$staging"

	if ! smoke="$("$staging/Bassia" version 2>&1)" || ! printf '%s\n' "$smoke" | grep -q '^ok = true$'; then
		echo "$smoke" >&2
		if [ -n "$dotnet_root" ] && [ -z "${DOTNET_ROOT:-}" ] && DOTNET_ROOT="$dotnet_root" "$staging/Bassia" version >/dev/null 2>&1; then
			fail "Bassia finds .NET only with DOTNET_ROOT set. Add 'export DOTNET_ROOT=$dotnet_root' to your profile, open a new terminal and run install.sh again."
		fi
		fail "the packaged bassia failed its smoke test (bassia version); nothing was installed."
	fi
	if [ -e "$staging/weave-driver" ] && ! "$staging/weave-driver" --version >/dev/null 2>&1; then
		fail "the packaged weave-driver does not run on this machine; nothing was installed."
	fi

	{
		echo '# Written by install.sh when this version was installed from a release package.'
		echo "id = \"$id\""
		echo "ref = \"${ref:-$id}\""
		echo "commit = \"$commit\""
		echo "version = \"$version\""
		echo 'configuration = "Release"'
		echo 'self_contained = false'
		echo "rid = \"$rid\""
		echo "weave = \"$weave\""
		echo "source = \"package $(basename "$here")\""
		echo "installed_at = \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\""
	} > "$staging/bassia-install.toml"

	rm -rf "$destination"
	mv "$staging" "$destination"
	trap - EXIT
	step "installed $id ($version) at $destination"
fi

# The activation scripts and env.sh, from the same templates bassia-versions.ps1 writes them from.
quoted_root="'$(printf '%s' "$root" | sed "s/'/'\\\\''/g")'"
cp "$here/scripts/templates/activate.ps1" "$root/activate.ps1"
for name in activate.sh env.sh; do
	awk -v r="$quoted_root" '{ gsub(/__ROOT__/, r); sub(/\r$/, ""); print }' "$here/scripts/templates/$name" > "$root/$name"
done

default="$(default_version)"
if [ "$no_use" != 1 ] || [ -z "$default" ] || [ ! -d "$versions/$default" ]; then
	rm -f "$current"
	ln -s "$destination" "$current"
	step "default version: $id"
else
	step "default version stays $default (ln -sfn $versions/$id $current, or bassia-versions.ps1 use $id, to switch)"
fi

if [ "$no_modify_path" != 1 ]; then
	line=". '$(printf '%s' "$root/env.sh" | sed "s/'/'\\\\''/g")' $marker"
	for file in "$HOME/.profile" "$HOME/.bashrc" "$HOME/.zshrc"; do
		[ "$file" = "$HOME/.profile" ] || [ -f "$file" ] || continue
		if [ -f "$file" ] && grep -qxF "$line" "$file"; then continue; fi
		if [ -f "$file" ]; then grep -vF "$marker" "$file" > "$file.bassia-tmp" || true; else : > "$file.bassia-tmp"; fi
		echo "$line" >> "$file.bassia-tmp"
		cat "$file.bassia-tmp" > "$file" && rm -f "$file.bassia-tmp"
		step "sourced env.sh from $file"
	done
	case ":$PATH:" in
		*":$current:"*) ;;
		*) step "open a new terminal (or log in again) to get 'bassia' on PATH; until then run $current/bassia" ;;
	esac
fi
