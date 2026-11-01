---
name: bassia-cloud-server
description: Run a lasting bassia-server on the local Azure emulators (Azurite for Blob Storage, PostgreSQL for Azure Database for PostgreSQL) and initialize it from GitHub or other git repositories - starts the emulators and the server, makes a clone, and imports the repositories through it. Also reports, stops and resets that server. Use when asked to set up, start, initialize or populate a local Bassia server, to import GitHub/GitLab/git repositories into a Bassia server or monorepo on Azurite/PostgreSQL, or to check, stop or wipe that server. Accepts optional arguments - repository URLs or paths, and the words `history`, `limit <n>`, `prefix <a,b>`, `clone <dir>`, `port <n>`, `token <t>`, `status`, `stop`, `reset`.
---

# bassia-cloud-server

Drives `scripts/bassia-local-cloud.ps1`, the same script as `/bassia-local-cloud`. That skill runs the emulators;
this one runs a `bassia-server` on them that keeps running, and imports git repositories into it. Run the script with
PowerShell 7 from the root of this clone: `pwsh -NoProfile -File scripts/bassia-local-cloud.ps1 <command> ...`.

## Arguments

| Skill argument | Script |
| --- | --- |
| repository URLs or paths (`https://github.com/acme/api.git`, `git@github.com:acme/web.git`, `../tools`) | `import -Git <a>,<b>,...` |
| `history` | `-History`: first-parent history of each, the repositories interleaved by commit time; without it, one snapshot each |
| `limit <n>` | `-Limit <n>`: at most n commits per repository with `history` (default 1000) |
| `prefix <a,b>` | `-Prefix a,b`: the top-level folder of each repository (default: its name) |
| `clone <dir>` | `-Clone <dir>`: the clone to import through (default `~/bassia-monorepo`) |
| `port <n>`, `token <t>` | `serve -Port <n>` (default 8080), `-Token <t>` (a shared token the API and dashboard then require) |
| no repositories | `serve`: start the server only |
| `status` / `stop` / `reset` | `server-status` / `server-stop` / `server-reset` |

## Steps

1. **Emulators.** Run `status`. If it exits 1, run `up` (see `/bassia-local-cloud`; `check` says what is missing if
   `up` fails).
2. **Server and import.** With repositories, run `import -Git ...` with the mapped switches. It first runs `serve`,
   which:
   - publishes this clone's `bassia-server` (Release) into the state folder,
   - creates its PostgreSQL database (`bassia`),
   - starts it detached at `http://127.0.0.1:8080` on the Azurite container `bassia`, so it keeps running after the
     script, the terminal or the agent session ends.

   It then makes the clone, or reuses it if it is already a clone of this server, and runs `bassia import` through it.
   That uses the installed `bassia` (`/bassia-install`), or a build of this clone when none is on PATH.
   - Private repositories use the user's own git credentials (Git Credential Manager, SSH keys): `bassia` runs `git`.
     If a fetch asks for credentials or fails with 403/404, tell the user to check access with
     `git ls-remote <url>`. Never put a token into the URL yourself.
   - Importing the same repository again adds its content again as new landings. To start over, use `reset`.
3. **Report** what the script prints:
   - each repository's folder, commits and files, and the mainline seq,
   - the clone (where `bassia workspace create <name>` and `bassia agent start` work),
   - the dashboard URL.
   For more history, suggest `bassia -C <clone> mainline log`.
4. **status / stop / reset.**
   - `server-status` prints the URL, mainline seq, data folder, database, container and clone, and exits 1 when the
     server is down.
   - `server-stop` stops the server; `serve` or `import` starts it again with its data.
   - `server-reset` deletes the server's data folder, its database contents, every blob in its container, and the
     clone. Ask the user before running it, naming what it deletes.

`down` (from `/bassia-local-cloud`) also stops the server. Other switches:
- `serve -Database <name> -Container <name> -Data <dir>`: where the server keeps its state; these are remembered for
  later runs. The script tracks one server at a time.
- `-Server <exe>`: run another build of `bassia-server`.
- `-NoBuild`: reuse the last published server.
- `-Bassia <exe>`: the `bassia` the import runs.
