# Use an installed Bassia version in this shell only (bash, zsh); the default (<root>/current) stays as it is.
# Usage: . <root>/activate.sh [<version>]   - without a version, the nearest .bassia-version file names it.
# Undo with: bassia_deactivate
_bassia_root=__ROOT__
_bassia_version="${1:-}"
if [ -z "$_bassia_version" ]; then
	_bassia_dir="$PWD"
	while [ -n "$_bassia_dir" ]; do
		if [ -f "$_bassia_dir/.bassia-version" ]; then
			_bassia_version="$(head -n 1 "$_bassia_dir/.bassia-version" | tr -d ' \t\r')"
			break
		fi
		if [ "$_bassia_dir" = "/" ]; then break; fi
		_bassia_dir="$(dirname "$_bassia_dir")"
	done
	unset _bassia_dir
fi
if [ -z "$_bassia_version" ]; then
	echo "bassia: name a version, or add a .bassia-version file to this folder or a parent." >&2
elif [ ! -d "$_bassia_root/versions/$_bassia_version" ]; then
	echo "bassia: version '$_bassia_version' is not installed. Installed: $(ls "$_bassia_root/versions" 2>/dev/null | tr '\n' ' ')" >&2
else
	if command -v bassia_deactivate >/dev/null 2>&1; then bassia_deactivate; fi
	_BASSIA_OLD_PATH="$PATH"
	PATH="$_bassia_root/versions/$_bassia_version:$PATH"
	BASSIA_VERSION="$_bassia_version"
	export PATH BASSIA_VERSION
	bassia_deactivate() {
		PATH="$_BASSIA_OLD_PATH"
		export PATH
		unset BASSIA_VERSION _BASSIA_OLD_PATH
		unset -f bassia_deactivate
	}
	echo "bassia: using $_bassia_version in this shell (bassia_deactivate to undo)"
fi
unset _bassia_root _bassia_version
