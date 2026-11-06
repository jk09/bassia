# Local cloud server: a bassia-server on Azurite and PostgreSQL, initialized from GitHub repositories

## Outcome

A `/bassia-cloud-server` skill starts a long-running `bassia-server` on the local Azurite and PostgreSQL emulators
(`/bassia-local-cloud`), clones it, and imports GitHub (or any git) repositories into it, so a developer gets a
working client-server monorepo with a dashboard in one step. It also reports, stops and resets that server.

## Context

- `scripts/bassia-local-cloud.ps1` (bassia-d345202e) runs the emulators and a throwaway smoke-test server; starting a
  lasting server, cloning and importing are manual steps today (`env | Invoke-Expression`, `dotnet run`, `bassia clone`,
  `bassia import`).
- The release packages (`/bassia-install`) hold the CLI only; `bassia-server` is built from the clone.
- Processes started from a script must outlive it (agents' tool sessions and CI steps end their processes): the script
  already detaches Azurite (WMI on Windows, `setsid` elsewhere).

## Acceptance criteria

- [x] `bassia-local-cloud.ps1 serve` publishes `bassia-server` (Release) into the state folder (or takes `-Server`),
      creates its PostgreSQL database (default `bassia`) and starts it detached on Azurite (container, default
      `bassia`) + PostgreSQL at `http://127.0.0.1:<port>` (default 8080), optionally with `-Token`; waits until it
      answers; is idempotent; fails clearly when the emulators are down or the port is taken.
- [x] `import -Git <url>,...` (with `-History`, `-Limit`, `-Prefix`) makes or reuses a clone of the server (default
      `~/bassia-monorepo`, or `-Clone <dir>`) and imports the repositories through it with the installed `bassia` (or
      `-Bassia`, else a build of this clone); it starts the server first if needed, and prints the mainline seq, each
      repository's commits and files, the clone and the dashboard URL.
- [x] `server-status` prints the URL, mainline seq, data folder, database, container and clone, and exits 1 when the
      server is not running; `server-stop` stops it; `server-reset` stops it and deletes its data folder, its database,
      its blobs and the clone, so the next `serve`/`import` starts from an empty monorepo.
- [x] `.claude/skills/bassia-cloud-server/SKILL.md` maps arguments (repository URLs, `history`, `limit <n>`, `token`,
      `status`, `stop`, `reset`) onto the script; `docs/reference.md` documents the commands.
- [x] CI's `Local cloud` jobs run `serve`, `import` (a local git repository, with history), `server-status`,
      `server-reset` and `server-stop`.

## Approach

Extend `bassia-local-cloud.ps1` (its state, emulator connection strings, psql, and detached start); the server's own
state goes to `<state>/server.json`. The detached start used for Azurite becomes one function shared with the server.
Blobs are deleted by `bassia-server gc -grace-hours 0` against the fresh, empty database: nothing is referenced, so
every blob in the container is swept.

## Decisions

- One script and its state rather than a second script: the server depends on the emulators' ports and mode.
- Publish the server into the state folder instead of running it from `bin/`: a rebuild of the clone does not fight
  a running server's locked files (Windows), and the server keeps running from a stable copy.
- The import's clone is user-visible (`~/bassia-monorepo`) since it is the monorepo the user works in.
- One tracked server at a time (`server.json`); its database, container, data folder, port and token are remembered
  between runs. The token is stored in that per-user file in clear text: it is a local development server.
- `server-reset` empties rather than drops: it recreates the database and leaves the initial (empty) monorepo that gc
  writes, so the next `serve` starts at seq 0.

## Progress

- [x] Script commands and skill.
- [x] Validated locally in native mode: serve (twice), import of https://github.com/octocat/Hello-World.git plus a local
      repository with history (seq 5), server-status, server-stop, import restarting it and reusing the clone,
      server-reset (83 of 87 blobs swept; the 4 left are the new empty monorepo), status exit 1, serve after reset
      (seq 0), down stopping the server; and the CI step's script end to end.
- [x] CI `Local cloud` jobs (Docker on Linux, native on Windows) pass the new step.

## Validation

- `serve`, `serve` again, `import -Git <two local repos> -History`, `server-status`, `server-stop`, `import` (restarts
  it), `server-reset`, `server-status` (exit 1).
