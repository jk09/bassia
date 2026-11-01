# Bassia

**A client-server monorepo for fleets of coding agents: a workspace per run, one trunk to land on.**

Bassia is version control built for agents that rewrite whole files and directories in parallel. There are no
branches, tags or commits to manage: every agentic run gets its own **workspace** over the monorepo, in the client it
runs on, its work is
snapshotted implicitly, and you watch whether each workspace can **land** on the single trunk. When two agents rewrote
the same file, Bassia does not merge text nobody reviewed: it re-runs the later task on the new trunk.

![The Bassia overview: live runs, which workspaces can land, where they stand, and the latest landings](docs/images/overview.png)

- **[Workspaces instead of branches](#workspaces-instead-of-branches)**: one per run, stacked or cloned when work
  builds on work; commits are implicit.
- **[Landing is the only contention](#landing-is-the-only-contention)**: only the paths a workspace touched are
  compared with the trunk; disjoint work lands without any merge.
- **[Replay, not merge](#replay-not-merge)**: a conflict re-runs the task on the new trunk with the same change id;
  text, semantic and stored-conflict policies are there where you want them.
- **[Cheap rewrites, lazy reads](#cheap-rewrites-lazy-reads)**: content-defined chunks in cloud blob storage; status
  and listings come from hashes and metadata, file bytes only when something reads them.
- **[Client-server, testable on a laptop](#client-server-testable-on-a-laptop)**: the same core runs in a local
  monorepo and in `bassia-server`; a local-disk blob store, or Azurite and PostgreSQL, stand in for the cloud
  ([the local workflow](#the-local-workflow-a-server-on-emulated-cloud-storage-agents-on-several-clients)).
- **[A dashboard you look at](#a-dashboard-you-look-at)** and **[a CLI made for agents](#a-cli-made-for-agents)**.
- **[Bring your git repositories](#bring-your-git-repositories)**: import several at once, export the trunk back.

The design behind it: [Design: Cloud-Backed Version Control for Agentic Development](https://claude.ai/artifact/V2nkDzNs8XAsx1E9PpgwM7).
The whole guide: [docs/reference.md](docs/reference.md).

**Terms used throughout:**
- **server**: `bassia-server`, which holds the monorepo (objects in blob storage, heads in a database).
- **client**: a folder connected to a server by `bassia clone`, such as `D:\clients\a`. It holds `.bassia/` (the
  settings, run logs and the cache of objects it has fetched) and the workspaces made in it. `bassia init` makes a
  **local monorepo** instead: a client with its own storage and no server.
- **workspace**: always the unit of work. Each workspace is made in a client, by `bassia workspace create` or for each
  `bassia agent start`, in `<client>/workspaces/<name>`. It has a record on the server: its base, its work and its run.
- **mainline**: the trunk that workspaces land on, numbered by seq.

## The local workflow: a server on emulated cloud storage, agents on several clients

Everything below runs on one machine. Azurite and PostgreSQL stand in for Azure Blob Storage and Azure Database for
PostgreSQL, so nothing needs a cloud account. Run the commands from a checkout of this repository, in PowerShell 7.
Each step has a Claude Code skill that does the same.

1. **Install `bassia`**: `/bassia-install` (add `prerelease` for the latest pre-release), or
   `pwsh scripts/bassia-versions.ps1 install -Prerelease`.
2. **Start the containers**: `/bassia-local-cloud up`. Docker is used when it runs, local Node.js and PostgreSQL
   otherwise.

   ```powershell
   pwsh scripts/bassia-local-cloud.ps1 up
   ```

3. **Start the server and import repositories**: `/bassia-cloud-server <urls> history`. The server runs at
   http://127.0.0.1:8080 (also its dashboard) and keeps running after the terminal closes. The import goes through a
   first client.

   ```powershell
   pwsh scripts/bassia-local-cloud.ps1 import -Git https://github.com/acme/api.git,https://github.com/acme/web.git -History -Clone D:\clients\setup
   ```

4. **Connect more clients**, one per machine, agent pool or task area:

   ```powershell
   bassia clone -from http://127.0.0.1:8080 -path D:\clients\a
   bassia clone -from http://127.0.0.1:8080 -path D:\clients\b
   ```

   On Windows, put the clients on a [Dev Drive](https://learn.microsoft.com/en-us/windows/dev-drive/), as here. A
   client then creates its workspaces by block cloning from its own cache: each file's content is stored once and
   shared copy-on-write, so a new workspace costs metadata, not bytes. `bassia devdrive status -path D:\clients\a`
   tells whether a folder qualifies.
5. **Run agents in the clients.** Each run gets its own workspace in that client, landed when it succeeds. Load rules
   (`-load`) choose what the workspace holds, so the client fetches and caches only that: here, profiles defined in
   the monorepo's `.bassia.toml` ([load rules](docs/reference.md#load-rules-what-a-workspace-fetches-caches-and-holds)).

   ```powershell
   bassia -C D:\clients\a agent start -load "@api" -land -prompt "add multiply() to the calculator, with a test; run the tests"
   bassia -C D:\clients\b agent start -load "@web" -land -detach -prompt "add a farewell page, with a test; run the tests"
   ```

   To let Claude Code also build and run tests, allow the commands, and keep `-p` last:

   ```powershell
   bassia -C D:\clients\a config set -key agent.command -value 'claude --permission-mode acceptEdits --allowedTools "Bash(dotnet:*)" -p'
   ```

6. **Follow and synchronize.** A client has no push or pull: mainline and workspaces live on the server, and every
   client sees each landing at once. Use these commands to follow runs and land work:
   - `bassia -C D:\clients\b agent list` lists the client's runs.
   - `bassia -C D:\clients\b workspace list -all` lists every workspace, from all clients.
   - `workspace check <workspace>` and `workspace land <workspace>` land work that didn't land on its own.
   - `workspace sync <workspace>` moves an open workspace onto the newest mainline.
   - `agent replay <workspace> -land` re-runs a task whose files another landing changed first.
7. **Stop**: `pwsh scripts/bassia-local-cloud.ps1 down` stops the server and the containers and keeps their data.
   `server-reset` empties the monorepo, and `down -Purge` deletes everything.

## Workspaces instead of branches

```sh
bassia clone -from http://127.0.0.1:8080 -path ~/acme && cd ~/acme  # a client (or: bassia init, a local monorepo)
bassia workspace create fix-login -intent "fix the login redirect"   # a workspace: a folder holding the trunk's newest tree
$EDITOR workspaces/fix-login/src/auth/login.ts                        # any tool, any agent
bassia workspace status fix-login                                     # snapshotted on the way: no add, no commit
bassia workspace create fix-login-tests -on fix-login                 # stacked: starts from fix-login's work, lands after it
bassia workspace clone -from fix-login -name fix-login-alt            # the same work as a new change: try another way
```

On Windows, keep the client on a [Dev Drive](https://learn.microsoft.com/en-us/windows/dev-drive/) and its workspaces
are created by block cloning: each file's content is stored once and every workspace of the client shares it
copy-on-write, so a new workspace over a large monorepo costs metadata, not bytes (`bassia devdrive status` tells
whether a folder qualifies). Load rules (`-load api/,docs/` or `-load @profile`) keep a workspace to the part of the
monorepo its task needs: the client fetches and caches only that.

Every mutation is an operation in the workspace's own log, so `workspace undo` and `workspace restore -op <n>` recover
any earlier state, and an abandoned run is never lost. Workspaces never contend with each other, however many agents
snapshot at once.

![Workspaces: stacks drawn from the trunk, each coloured by whether it can land](docs/images/workspaces.png)

## Landing is the only contention

```sh
bassia workspace check fix-login        # would-land, or why not: the paths and what their policy does
bassia workspace land fix-login         # the next seq on the trunk, keeping the change id
bassia mainline log                     # one linear history, numbered by seq
```

A landing compares only the paths the workspace touched with the trunk since the workspace's base: paths nobody else
changed are taken as they are, equal results are accepted, and the commit lands by compare-and-swap. Losing a race
costs only those paths again. Leases (`bassia lease acquire src/Billing`) hold a prefix for a run with fencing tokens,
so a stalled agent cannot overwrite its successor; `.bassia.toml` can require them where it matters.

## Replay, not merge

When two agents rewrote the same file, a textual merge yields plausible but unreviewed code. The default policy is
**replay**: the landing is refused with the paths and the new trunk, and

```sh
bassia agent replay add-request-logging -land
```

runs the same task (prompt, agent, model, recorded with the change) again on the new trunk, in a new workspace that
keeps the change id. Per path glob, `.bassia.toml` can choose `textmerge` (line-based, while both sides stay similar),
`semantic` (weave's language-aware merge, landed only after your validation command passes) or `conflict` (stored
with markers in the workspace to resolve by hand).

![Conflicts: what keeps workspaces from landing, and the paths that keep colliding](docs/images/conflicts.png)

## Cheap rewrites, lazy reads

Every object is immutable and content-addressed (BLAKE3 over canonical CBOR). Files are cut into content-defined
chunks, so a rewrite that keeps part of a file uploads only the new part, after asking which chunks the server already
has. Comparing two trees skips every subtree with an equal id, tree entries carry size and kind, and a workspace
re-hashes only files whose size or time changed - so status, listings and diff statistics never read file content.
Diffs are derived, never stored; a file that shares little with its old version is reviewed as rewritten, side by
side, instead of as a wall of hunks.

## Client-server, testable on a laptop

```sh
bassia-server -data /srv/bassia -url http://0.0.0.0:8080 -token <token>          # blobs on local disk
bassia-server -data /srv/bassia -storage s3 -bucket acme-bassia -token <token>   # blobs on S3
bassia-server -data /srv/bassia -storage azure -azure-url https://acme.blob.core.windows.net/bassia -state postgres -postgres "<connection string>"
bassia clone -from http://bassia.example.com:8080 -path ~/acme -token <token>         # a client
```

The version core reaches storage only through four small interfaces - objects, heads, the path index and leases - so
the same core runs in a local monorepo (`bassia init`: a local-disk blob container and SQLite) and in `bassia-server`
(the same stores, S3, or Azure Blob Storage with Azure Database for PostgreSQL). A client talks to the server and
caches every object it reads, in batches, for all its workspaces. Large chunks travel between
clients and blob storage on signed URLs; the server never proxies file bytes. Every test runs without a cloud account.

## A dashboard you look at

`bassia web` (and the server's front page) shows the live runs, every workspace with whether it can land right now,
the stacks, the trunk's landings with their diffs, what blocks landings and which paths keep colliding, leases, the
files at any seq, and the policy. It is read-only: every page shows the commands that act on what it shows.

![Runs: live runs as cards, and every run over time](docs/images/runs.png)

## A CLI made for agents

Every command answers in TOML (`-human` for Markdown), with exit codes 0, 1 and 2. `bassia help -all` teaches an agent
the whole CLI in one call. Agentic runs are built in:

```sh
bassia agent start -prompt "paginate the list endpoint" -land -detach   # a workspace of its own in this client, landed when it succeeds
bassia agent list                                                       # live, succeeded, failed, lost
bassia agent logs paginate-the-list-endpoint-4f2a91 -tail 20
```

## Bring your git repositories

```sh
bassia import -git https://github.com/acme/api.git,https://github.com/acme/web.git -history
bassia export -git ../acme-mirror                                       # the trunk as git history, incrementally
```

Each repository goes into its own top-level folder, as a baseline or with its history interleaved by commit time; each
imported commit lands like any agent's work. The export writes one git commit per landing with a `Change-Id:` trailer.

## Install, build, test

Ask Claude Code to `/bassia-install` in a checkout of this repository, or build it yourself with the .NET 10 SDK:

```sh
dotnet build
dotnet test
dotnet run --project Bassia -- help
```

See [docs/reference.md](docs/reference.md) for installing side by side, the server, the policy file, every command,
the architecture and the tests.
