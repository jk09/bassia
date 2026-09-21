# Unique agentic run names and TOML commit messages

Status: done

## Outcome

Every agentic run is identified by a random id, so run folders, run branches and result tags never collide across workspaces or machines that share the same component repositories. Commits made by an agentic run carry a structured TOML record of the run in their message, with the subject line configurable in `config.toml`.

## Context

Run ids were sequential (`agentic-run-N`), allocated by scanning the workspace, the run-metadata store and every selected source-of-truth repo for ids already in use. That scan can only see repos on the local machine: two workspaces pushing to the same shared component repo can still both allocate `agentic-run-1` and then collide on the branch `agent/agentic-run-1` and the tag `agent/agentic-run-1/0`.

Commits made by a run had a free-text message (`agent(<run-id>): <summary>` plus `Key: value` lines). Tooling that reads a component's history to find out which run produced a commit, with which command and from which baseline, needs a format it can parse reliably.

## Acceptance criteria

- [x] A run's id is `agent-run-<id>` where `<id>` is a fresh GUID (32 lowercase hex digits); the run folder is `.workspace/agent-run-<id>/`.
- [x] Every component checkout of the run is on the branch `agent/run-<id>`.
- [x] A component's result commit is tagged `agent/run-<id>/<counter>`, and branch + tag are pushed to the source-of-truth repo. `<counter>` is the next unused index among that component's existing `agent/run-<id>/*` tags (0 for the first result).
- [x] Two runs in the same monorepo get different ids without consulting the source-of-truth repos; the id-allocation scan is gone.
- [x] `agent retry <run-id>` and `agent abandon <run-id>` accept the full run id and, for convenience, the bare `<id>`.
- [x] The message of a result commit is a subject line, a blank line, and a TOML document describing the run (id, command, summary, selection, timestamps, run-record tag) and the component (name, selected tag, base commit, branch, result tag). The body round-trips through a TOML parser.
- [x] The whole message is a template read from `[agent.commit] message` in `.bassia/config.toml`, with `{run_id}`, `{short_id}`, `{summary}`, `{component}` and `{metadata}` (the TOML record) placeholders; `bassia init` writes the default template.

## Approach

- **Ids**: `RunMetadata` owns the naming: `NewRunId()`, `RefBase(runId)` (`agent/run-<id>`), `TagName(runId, index)`. The run-metadata store keeps its own lineage tags in the same shape, in its own repo.
- **Counter**: before committing, list the checkout's `agent/run-<id>/*` tags and take the highest index + 1. The tag name is fixed before the commit is created so the commit message can name its own tag; a retry reuses the tag recorded in the run record.
- **Commit message**: a `ResultCommitMessage` builder renders the configured template in one pass; `{metadata}` expands to a record serialized with Tomlyn, so that block is always valid TOML. `Monorepo` reads the template from `config.toml` with a built-in default.

## Decisions

- **Full GUID, not a short prefix.** Uniqueness has to hold across machines that share a component repo, where nothing can be scanned for collisions, so the id carries enough entropy that no check is needed. Human-facing places (the commit subject) can use `{short_id}`, the first 8 hex digits, in the spirit of the `feat/bassia-<8 hex>` record ids.
- **The counter is local to the run branch.** All `agent/run-<id>/*` tags of a component sit on commits of the branch `agent/run-<id>` in that component's checkout (and, once pushed, in its source-of-truth repo, which the checkout was cloned from). The next counter is therefore `max + 1` over the tags visible in the checkout, with no cross-run or cross-component coordination. Today a run makes at most one result commit per component, so the counter is always 0; it exists so that further result commits on the same run branch (continuing a run) can be tagged in sequence without renaming.
- **The whole message is templated; the record's schema is fixed.** The template decides where the subject, the record and any trailers go, while `{metadata}` always expands to a serializer-produced block with a fixed set of keys, so the part meant to be parsed cannot be broken by the template. Placeholders are substituted in a single pass, so a value containing a placeholder is never expanded again. Omitting `{metadata}` is allowed and simply drops the record. (Initially only the subject line was templated; that was widened on request.)
- **No local paths in the commit message.** The previous message named the metadata repo's directory; a result commit is pushed to a repo that may be cloned anywhere, so the message names the run-record tag instead.
- **Lineage tags in the run-metadata repo keep the `agent/run-<id>/<n>` shape.** They live in a different repo than the result tags, so identical names do not collide, and one naming scheme is easier to remember than two.

## Progress

- [x] Random run ids, `agent/run-<id>` branches and `agent/run-<id>/<counter>` tags.
- [x] TOML commit messages with a configurable subject.
- [x] Tests updated to read the run id from the command output; README updated.

## Validation

- `dotnet test Bassia.Tests` — all tests pass, including the new id-uniqueness and commit-message tests.
- Manual: `bassia agent -select ... -run ...` twice in one monorepo; `git -C <component> log -1 --format=%B agent/run-<id>/0` shows the subject plus a TOML body that `Tomlyn` parses.
