# Readable agentic run names

Status: done

## Outcome

Agentic runs and integrations get memorable, readable names instead of GUIDs: `agent-run-<adjective>-<noun>-<slug>`, e.g. `agent-run-magical-otter-vt9j3p`. The run's branch in every component is `agent-run/<key>` and its tags are `agent-run/<key>/<n>` (e.g. `agent-run/magical-otter-vt9j3p/0`), where `<key>` is the run id without the `agent-run-` prefix. Integrations follow suit: `integration-<key>`, branch `integration/<key>`, tags `integration/<key>/<n>`.

## Context

Since `bassia-5214a52f` a run's id is `agent-run-<32 hex digits>`, its branch `agent/run-<guid>` and its result / lineage tags `agent/run-<guid>/<n>`; integrations are `integration-<guid>`. The ids are collision-free across workspaces sharing a component repo, but hard to read, say or type; humans fall back on the 8-digit short id. The new names keep enough entropy to stay collision-free without coordination while being easy to recognize in `run list`, the dashboard, branch lists and commit subjects.

## Acceptance criteria

- [x] A new run's id is `agent-run-<adjective>-<noun>-<slug>`: two lowercase words from built-in lists and a 6-character slug of lowercase letters and digits (e.g. `agent-run-magical-otter-vt9j3p`); the run folder is `.workspace/<run id>/`.
- [x] Every component checkout of the run is on the branch `agent-run/<key>`; result tags in component repos and lineage tags in the run-metadata repo are `agent-run/<key>/<n>`.
- [x] Integration ids follow the same scheme: `integration-<key>`, branch `integration/<key>`, tags `integration/<key>/<n>`.
- [x] Commands that take a run or integration id accept the full id, the bare `<key>`, and a unique prefix (at least 4 characters) of the key.
- [x] The short id shown by `run list`, `integration list`, the dashboard, and the `{short_id}` placeholder of the commit subject is the full `<key>`.
- [x] Dashboard ref links (`agent-run/<key>/...`, `integration/<key>/...`) link to the record page; run and integration pages load for the new ids; anything not shaped like a key is never turned into a ref.
- [x] README, `init`'s config comment, CLI help examples and tests describe and exercise the new scheme; no `agent/run-` refs or GUID-shaped ids remain.

## Approach

- A shared `RecordName` owns the key: `NewKey()` draws an adjective, a noun and a 6-character base-36 slug from a cryptographic RNG; `IsKey()` validates the shape.
- `RunMetadata` (`RefPrefix` = `agent-run/`, `NewRunId`, `IsRunId`) and `IntegrationRecord` (`NewId`, `IsId`) build on it; `ShortKey` is gone, `Key` is the short id.
- The places that recognized a key by GUID shape (`Length == 32` / hex checks in the dashboard and ref links) use `IsKey` instead.
- The split's unmerged-ref listing and tests look under `refs/heads/agent-run/` and `refs/tags/agent-run/`.

## Decisions

- **Short id is the full key** (`magical-otter-vt9j3p`): readable enough to show everywhere, which is the point of the rename. Commands still accept any unique prefix of at least 4 characters.
- **No compatibility with GUID-named runs.** Records and refs under `agent/run-<guid>` are no longer recognized; existing monorepos such as the testbed are recreated.
- **Integrations get the same scheme** in this change, for consistency.
- **Adjective + noun word lists** (90 each), not names of people. With a 6-character base-36 slug that is about 1.8 × 10^13 combinations, ample for uncoordinated uniqueness across machines.
- **Branch and tag share the `agent-run/<key>` base.** A branch `refs/heads/agent-run/<key>` and tags `refs/tags/agent-run/<key>/<n>` live in different ref namespaces, so they do not collide.

## Progress

- [x] Naming in `RecordName`, `RunMetadata`, `IntegrationRecord` and their consumers.
- [x] Dashboard / ref-link recognition.
- [x] Docs, help examples and tests.

## Validation

- `dotnet build Bassia.slnx` succeeds without warnings.
- `dotnet test Bassia.slnx`: 240 passed, including the new `RecordNameTests` (id shape, distinctness, branch/tag names, key validation, prefix lookup, ref links) and the end-to-end tests, whose proofs show refs such as `agent-run/daring-dawn-mmdbfd/0` and `integration/sunny-meteor-a766fy/0`.
