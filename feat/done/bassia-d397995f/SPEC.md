# Verified: a new monorepo, several projects, agents in parallel workspaces

## Outcome

A developer can set up a new Bassia monorepo with `bassia init`, lay out several projects in it, and run several
agents at once, each in a workspace of its own, and see them land on mainline. Disjoint work lands without a merge.
Two agents that rewrite the same code end with one landed and one replayed under its own change id. Concurrent
edits to the same doc are text-merged. An automated end-to-end test proves this on a local monorepo, and `-run`
passes each of the agent command's arguments to the shell unchanged.

## Context

- The Azurite scenario (`AzuriteIntegrationTests`) and the server swarm (`ServerTests`) cover parallel agents against
  `bassia-server`. Nothing covered the path a developer takes first: a **local** monorepo made by `bassia init`, a few
  projects with `.bassia.toml` load profiles and conflict rules, and runs started with `-detach` and followed with
  `agent wait`.
- A manual run of that scenario (7 scripted agents, `-land -detach`) failed every run. `agent start -run <words...>`
  re-joins its words into one shell line (`Invocation.JoinRest`) and wraps a word that contains whitespace in `"..."`
  without escaping. A word that holds `"`, `$`, `` ` `` or `\` was re-parsed by `/bin/sh`. Here that meant a syntax
  error, or a printf format split into commands. The agents that started waited at their barrier for the one that
  never came. Bassia itself behaved correctly: it recorded every failure and landed nothing.

## Acceptance criteria

- [x] `agent start -run <word> <word>...` on Linux and macOS: a word that holds whitespace or a quote or escape
      character (`'`, `"`, `\`, `` ` ``) reaches the agent's shell as exactly that one word (single-quoted).
      Other words stay shell syntax (`&&`, `|`, `>`, globs, `$VAR`), and a single `-run` string is still one shell
      line, as before. Windows quoting is unchanged.
- [x] An end-to-end test on a local monorepo (`NewMonorepoTests`, needs `BASSIA_TEST_POSTGRES`):
  - `bassia init`, then one landing that creates three projects (`api/`, `web/`, `cli/`), a root README and a
    `.bassia.toml` with a load profile per project, `textmerge` for Markdown and `replay` for the rest.
  - Seven agents started with `agent start -land -detach`, each its own `bassia` process and workspace. A barrier
    proves they all run at once.
  - One agent per project adds a file. Each workspace loads only its project (`-load @<project>`), and each
    lands.
  - Two agents edit the same `api` source file: one lands, the other is refused as `needs-replay`, and
    `agent replay -land` lands it with its original change id.
  - Two agents edit different lines of the root README: both land, the second one text-merged.
  - Mainline ends at the expected seq with a linear log. Every run that landed appears in that log. A fresh workspace
    holds every agent's edit.
  - One run's command passes arguments with quotes, `$` and a backslash (checked on Linux and macOS only).
- [x] `agent wait` on a run started with `-land` returns only after the landing. A run is recorded as done before
      it lands, so `wait` also waits while the run's own process on this machine is alive. A run on another host is
      taken as finished.
- [x] `docs/reference.md` describes how `-run` quotes its words and lists the new test.

## Approach

- Fix `Invocation.JoinRest`: on POSIX, single-quote a word that needs it (`'` written as `'\''`). Keep the Windows
  branch as it is.
- Add `Bassia.Tests/NewMonorepoTests.cs`. It drives the built apphost as child processes, like
  `AzuriteIntegrationTests`, because `-detach` and parallel runs need real processes. It uses a deterministic
  agent script with a barrier instead of a model.

## Decisions

- Agents in the test are scripted stand-ins, not `claude`. The test must run without a network or an API key
  (CLAUDE.md: tests run offline).
- The test uses a local monorepo, not a server. The server path is already covered by the Azurite and server tests.
- Bare words that need no quoting (operators, globs, `$VAR`) stay shell syntax, so `-run npm test && echo ok`
  keeps working. Only words whose characters must have been quoted for the user's own shell to produce them are
  re-quoted.

## Progress

- [x] Manual scenario run: found the `-run` quoting bug.
- [x] Quoting fix.
- [x] End-to-end test, passing on Linux with PostgreSQL 16 (11 s). It fails against the old `JoinRest`: the quoted
      line arrives corrupted. The whole suite passes: 276 tests.
- [x] Manual scenario re-run with the fix: all 7 detached runs succeed. Six land (one text-merged); `api-bump-a`
      needs a replay, and its replay keeps the change id and lands `nothing`, because its `sed` no longer matches the
      new head.
- [x] Docs.
- [x] CI found an `agent wait` race: it returned before a detached `-land` run had landed, so `last_land` was
      missing. Fixed in `agent wait` (`AgentRunner.IsFinishing`), with a deterministic unit test.

## Validation

- `BASSIA_TEST_POSTGRES=... dotnet test --filter "FullyQualifiedName~NewMonorepoTests|FullyQualifiedName~CliTests"`
- The manual scenario: `bassia init`, the bootstrap landing, then 7 `agent start -land -detach` runs, `agent wait`,
  `workspace list`, `mainline log`.
