# Check leases against the mainline a landing commits on

## Outcome

A landing that depends on a lease holds that lease, under the policy of the mainline it lands on, at the moment it
commits. A lease that expires (and may be granted to someone else) while a landing retries its compare-and-swap, or a
`[leases] required` rule that lands concurrently, refuses the landing as `rejected` instead of letting it through.

## Context

`LandingService.LandOnceAsync` checked leases once, before its compare-and-swap retry loop, and read `.bassia.toml`
for that check from a separate read of `main`:

1. **Stale lease check.** A landing that lost the race for `main` (several server nodes, or a burst of landings)
   retried up to 100 times without looking at its leases again. A presented lease could expire and be granted to a
   successor in that time, and the landing still committed: the fencing token was never checked at commit time.
2. **Stale policy.** The `[leases] required` prefixes came from the `main` read before the loop, while the conflict
   policy came from the `main` of each attempt. A landing that added a required prefix between the two was ignored by
   the lease check, so a path that now needed an exclusive lease landed without one.

## Acceptance criteria

- [x] Every attempt of a landing checks its leases against the `main` and the `.bassia.toml` of that attempt.
- [x] A landing whose presented lease expires while it retries the compare-and-swap is refused as `rejected` with a
      `lease` conflict, and mainline does not change.
- [x] A landing that touches a prefix made `[leases] required` by a concurrent landing, without presenting a lease, is
      refused as `rejected` with a `lease` conflict.
- [x] The presented fencing tokens are checked again right before the compare-and-swap, after merging and validation.
- [x] Landings without leases, and the existing outcome order (`rejected` for leases before `needs-replay`), are
      unchanged; the existing test suite passes.

## Approach

Move the lease check into the retry loop of `LandOnceAsync`, after the attempt has read `main` and its `RepoPolicy`,
and pass that policy to the check instead of loading it again. Clear the attempt's lease warnings at the start of each
attempt. Just before writing the commit and advancing `main`, re-read every presented lease and refuse the landing if
any is gone or holds a different token; the required-lease coverage cannot change within an attempt, since it depends
only on the presented leases and the attempt's policy.

Tests simulate a lost race with a second landing service over the same stores, whose view store runs a hook before
the first compare-and-swap of `main`: the hook lands another change (and moves the lease clock), so the first attempt
loses and the second sees the new state.

## Decisions

- **Check, then re-check tokens; not one transaction.** Making the lease check and the head compare-and-swap atomic
  would need `IViewStore` to know about leases (or a transaction across two traits). Re-checking the tokens right
  before the compare-and-swap narrows the window to one round trip, which a lease TTL of minutes makes negligible.
  Revisit if leases ever get TTLs of seconds.
- **Keep the check at the start of each attempt** rather than only before the compare-and-swap, so a landing missing
  its leases is still `rejected` before conflicts are looked at, as before.

## Progress

- [x] Lease check inside the retry loop with the attempt's policy; token re-check before the compare-and-swap.
- [x] Tests for an expired lease and for a concurrently added required prefix during a lost race.
- [x] Full test suite.

## Validation

- `dotnet test Bassia.Tests --filter "FullyQualifiedName~LeaseTests"` - the new race tests fail before the change and
  pass after it.
- `dotnet test Bassia.Tests` - the whole suite.
