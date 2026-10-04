---
name: bassia-uninstall
description: Remove one installed version of the Bassia CLI, or remove Bassia completely (every version, the PATH entry and the activation scripts) for the current user, without elevation. Use when asked to uninstall, remove, delete or clean up bassia versions. Accepts an argument - the version id to remove (e.g. v0.1.0, v0.2.0-debug), or `all`; add `force` to remove the default version.
---

# bassia-uninstall

Removes versions that `/bassia-install` installed, using `scripts/bassia-versions.ps1`. Run it with PowerShell 7 from
the root of this clone: `pwsh -NoProfile -File scripts/bassia-versions.ps1 ...` (on Windows without `pwsh`:
`powershell -NoProfile -ExecutionPolicy Bypass -File ...`).

## Steps

1. Run `list` to see the installed versions and the default (`*`). If no version was given, or the one given matches
   nothing, show the list and ask which one to remove.
2. **One version:** run `uninstall <version>`. The script refuses to remove the default version and suggests another
   one to switch to. Ask the user whether to switch (`use <other>`, see `/bassia-use`) or remove it anyway
   (`uninstall <version> -Force`, which leaves no default until one is chosen). A version can't be removed while one of
   its processes is running (Windows locks the files). If that happens, say so.
3. **`all`:** confirm with the user first, then run `uninstall -All`. It removes every version, the `current` link, the
   activation scripts, the user PATH entry and `BASSIA_HOME` on Windows (or the `env.sh` line in the shell profiles
   elsewhere), and the install root if nothing else is left in it.
4. Report what was removed and run `list` again to show what remains. The clone and its tags are never touched.
