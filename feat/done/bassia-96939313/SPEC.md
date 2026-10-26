# Users: who holds a lock, ran an agent, made a tag or a commit

## Outcome

Every lock, agentic run, integration, tag and commit Bassia makes is attributable to a concrete user. The monorepo
keeps a registry of its users (id, name, email, contact, an optional public key) in the meta-repo, versioned like the
component graph and independent of the storage backend. Each bassia process knows who it acts for, from `BASSIA_USER`,
the user's own configuration or their git email. It records that user on what it creates, and when the work is an
agentic run, the agent it was done through as well. When a path is held, `lock list`, `lock check` and `lock wait` say
which user holds it and how to reach them, so the people involved can negotiate outside Bassia.

## Context

So far a lock entry (`.locks/<run>.toml`) named only the host and process of its run, run and integration records named
no one, and commits and tags carried whatever git identity the machine had configured, or the fixed `Bassia` identity
of a snapshot commit. With several people and their agents sharing one monorepo root, nobody could tell whose run held
a file. The storage contract (`Bassia/Storage`) is what makes the graph independent of git, so the user concept must
not depend on git either. Decisions made with the user are listed under **Decisions**.

## Acceptance criteria

- [x] The meta-repo holds the user registry (`users.toml`: one `[[user]]` per user with `id`, `name`, `email`, optional
      `contact` and `key`). It is read and changed through `IMetaStore` (git and memory), each change one revision.
      Ids follow component-name rules, and ids and emails are unique.
- [x] `bassia user add -id <id> -name <name> -email <email> [-contact <text>] [-key <public key>]`, `user list`, `user show`,
      `user set`, `user remove`; `bassia whoami` reports the current user and where it came from.
- [x] The current user resolves, in order, from `BASSIA_USER`; `user.id` in the user's own configuration layer
      (`config.user.toml`, set by `bassia user use -id <id>`); or a registered user whose email equals the git email
      configured for the meta-repo. An id that is not registered is an error.
- [x] With registered users, a command that changes the monorepo fails when the current user is unknown. The message
      says how to become known. `user add`, `user use`, `init`, `help`, `version` and read-only commands are exempt.
      Without registered users, Bassia behaves as before and attributes nothing.
- [x] Attribution through the storage contract: everything a command writes is authored by the current user. In git:
      the author, committer and tagger of commits, annotated tags and meta-repo revisions. In memory: the stores record
      the author and tagger. `StoredCommit.Author` and `StoredTag.Tagger` report them for both backends.
- [x] Agentic runs: the run record (`run.toml`) and its result commits' TOML record name the user (`user`). The lock
      entry names the user. Result commits are authored by the user and committed as `<agent> via bassia`, with the
      agent named after the run's command (e.g. `claude`). The agent process gets `BASSIA_USER`, so `bassia lock ...`
      inside the run acts for the same user.
- [x] Integrations: the record names the user (`user`); merge commits and integration tags are attributed to them.
- [x] Coordination: `lock list` shows each holder's user, and `lock check`, `lock wait` and `lock claim` name the
      holding run's user with name, email and contact. The agent's guide (`AGENTS.md`) lists who holds what and how to
      reach them. `agent list|show` and `integration list|show` report the user.
- [x] Documented in `docs/reference.md` and the help.

## Approach

- `Core/Users`: `UserDefinition` and `UserDirectory` (validated, immutable), kept by `MetaRepo` next to the component
  registry and changed by `RegistryChange.SetUser` / `RemoveUser`. `IMetaStore.LoadUsersAsync`; `GitMetaStore` keeps
  `.bassia/users.toml`.
- `Core/Actor`: who the current code acts for (a user, plus the agent when it is a run), held in an `AsyncLocal` set
  once per command. Storage backends read it when they write: `GitClient` and `GitBinary` set
  `GIT_AUTHOR_*`/`GIT_COMMITTER_*` for every git process, and the memory store stamps its commits and tags.
- `CliCommands/UserCommands`: the user commands, resolution of the current user, and the check in `ProgramCli` that
  sets the actor (and refuses an unknown user) before a changing command runs.
- Runs, locks and integrations record the actor's user id; the lock commands look the holder up in the registry.

## Decisions

- Identity is declared, not proven (user's decision): a user registers with id, name and email, like a git author. The
  optional `key` field lets signing be added later without changing the registry.
- An agentic run's work is attributed to the user, via the agent (user's decision): author = user, committer =
  `<agent> via bassia` with the user's email, so both are visible and the email still leads to the user.
- Coordination shows whom to contact (user's decision): Bassia attributes locks and runs to users and shows their
  contact details; people negotiate outside Bassia. No request/grant protocol and no notifications.
- One shared monorepo root (user's decision): the registry lives in the meta-repo and the lock table stays where it
  is; no sync protocol.
- An ambient actor rather than an identity parameter on every storage method: every write a command makes is
  attributed without widening the contract's signatures. Each backend decides how to record it.
- The actor is resolved asynchronously but set by the command dispatcher itself: an `AsyncLocal` assigned inside an
  awaited method does not outlive that method.
- The web dashboard only reads, so it needs no actor.
- A monorepo without users keeps working unattributed, so existing monorepos and tests are unaffected until someone
  registers the first user.

## Progress

- [x] Registry, storage (git, memory), actor
- [x] Resolution, user commands, enforcement
- [x] Runs, locks, integrations, tags, commits attributed
- [x] Docs, help, tests

Validated: `dotnet build` clean; `dotnet test` 557 passed, 1 skipped (as before), including `Core/UserTests` (registry
rules, revisions, agent names, a run's commit author and tag tagger via the agent, an integration's user, merge commit
and tag) and `UserCommandTests` (git: users.toml and the registration's author, `user use`/`whoami`, an unknown or
unregistered user refused while reads still work, a run's author, committer and tagger, `lock check`/`lock list` naming
the holder's user and contact).

## Validation

- `dotnet build`, `dotnet test` (registry and attribution on the memory storage; git: authors and taggers of a run's
  result commit, tag and the meta-repo revision; `lock check` naming the holder's user and contact; an unknown user
  refused).
