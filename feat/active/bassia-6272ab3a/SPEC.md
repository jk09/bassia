# Landing gate: plan outside it, hold it only to commit

Status: active. Second scalability step (after `bassia-1def6a48`, `bassia-c7ba795f`, `bassia-ff9140e9`); measured
with the load harness.

## Outcome

A landing that needs a merge driver or the validation hook no longer holds up every other landing: mainline is locked
only for the moment a landing checks that its plan still holds and moves the head. A landing that loses a race to
another node repeats that check, not its merges and validation.

## Context

- `LandingService.LandAsync` takes a process-wide gate (`SemaphoreSlim`) around the whole of `LandOnceAsync`: resolving
  the workspace, diffing its tree, the path-index lookups, conflict policies, merge drivers (processes), the
  validation hook (`land.validate`, a shell command that may run a build), writing the commit and the compare-and-swap.
- The reconciled-candidate fast path skips planning, but a landing whose candidate is stale, strict landings, and
  every landing on a local monorepo take the slow path inside the gate.
- Across server nodes only the compare-and-swap of `main` orders landings; a lost race `continue`s the loop and plans
  again from the comparison, up to 100 attempts.
- A machine-merged tree is valid only on the mainline it was validated on (the rule the fast path already follows).
- The baseline (`bassia-1def6a48`): 18.5 statements per landing, flat in the size of the state; nothing measures
  landings that validate, because the harness runs without a validator.

## Acceptance criteria

- [ ] While one landing runs its validation hook, a landing of a workspace on other paths completes (a test with a
      validator that blocks until the second landing has landed).
- [ ] A plan made on mainline seq N is committed on seq M > N without planning again when none of its paths, nor
      `.bassia.toml`, changed in between and it is not machine-merged; otherwise it is planned again on M.
- [ ] A machine-merged plan lands only on the mainline it was validated on.
- [ ] Leases are checked against the mainline the landing commits on, as today (required prefixes, lapsed tokens).
- [ ] Every outcome, message and recorded landing of `LandingTests`, `ReconcilerTests`, `ServerTests` and the swarm
      tests is unchanged; no update is lost with several landing services over one store.
- [ ] A landing that keeps losing gives up after a bounded number of attempts with today's message.
- [ ] The same workspace landed by several calls at once lands once; the others are refused.
- [ ] Load harness: the statements per landing rise by no more than the two reads the commit step repeats (mainline's
      head and the workspace's), and landing throughput does not fall; recorded here.

## Approach

Split `LandOnceAsync` into **plan** (no lock: refusal, candidate or touched paths, policy, `PlanAsync` with merges and
validation, against a mainline snapshot) and **commit** (under the gate: read `main`; if it moved since the snapshot,
ask the path index whether any planned path or the policy file changed after the snapshot's seq - one `GetManyAsync`;
if not, apply the plan's changes to the new tree with `EditAsync`, check leases, write commit and operation, advance
`main`). A moved path, a machine-merged plan on a moved mainline, or a path clash sends the landing back to plan.
The gate stays in-process; across nodes the compare-and-swap decides, and a lost race now costs one commit attempt.

## Decisions

Taken unattended, as the user asked (the recommended options):

- **No cross-node lock.** A PostgreSQL advisory lock would need a new trait for a section that is now a few
  statements long; the compare-and-swap already keeps nodes correct, and a lost race is cheap once plans are reused.
- **The validation hook runs outside the gate**, so two landings may validate at once; each validates the tree it would
  land, and a machine-merged plan is thrown away if mainline moved.
- **No validator option in the harness.** The validation hook runs only for machine-merged landings (the `semantic`
  policy with a merge driver), which the harness's replay-policy scenario never produces; a test with a validator
  that blocks shows the property directly, and the harness shows that ordinary landings cost the same.
- **A lost compare-and-swap is retried inside the commit step** (up to 8 times) before the landing is planned again:
  the plan is checked against the mainline that won, which costs one path lookup.
- **Recording the outcome on the workspace stays under the gate**, and the commit step checks that the workspace is
  still what was planned - so two calls landing the same workspace cannot both land it.
- **Leases are checked before planning only when the landing presents some or is a dry run**; otherwise once, in the
  commit step, against the mainline it lands on.

## Progress

- [ ] Plan/commit split in `LandingService`
- [ ] Tests: blocked validator, plan reuse, machine-merged replan, the same workspace landed twice
- [ ] Harness before and after

## Validation

`dotnet test`; `bassia-loadtest run` (small and medium) before and after, recorded here.
