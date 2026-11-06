# Path index at scale: batched lookups for landing; ChangedSince and listings without full-history scans

## Outcome

A landing that touches many paths asks the path index about all of them at once instead of one query per path. A
strict read set, or `mainline changed -since`, costs what changed since the given seq rather than the whole history
under the prefix.

## Context

- `LandingService` calls `IPathIndex.GetAsync(path, mainSeq)` once per touched path. On PostgreSQL every call is a
  round trip, so a 50,000-path rewrite makes 50,000 sequential queries per attempt (minutes against Azure Database for
  PostgreSQL), and a longer attempt is more likely to lose the mainline compare-and-swap. The server's `/paths/stat`
  endpoint already accepts many paths, but it too answers them one query at a time.
- PostgreSQL's `ChangedSinceAsync` first takes the latest version of every path under the prefix across all seqs, and
  only then filters on `seq > after`. Its cost therefore grows with all history under the prefix.
- PostgreSQL's `ListAsync(dir, seq)` (the server's `/paths/list`) reads the latest version of every descendant of the
  directory, then keeps the direct children: listing the root reads the whole table.

## Acceptance criteria

- [x] `IPathIndex.GetManyAsync(paths, seq)` returns, in order, what `GetAsync` returns for each path, duplicates and
      paths that never existed included. Memory, SQLite, PostgreSQL and the remote index implement it.
- [x] PostgreSQL answers it in one statement per batch of up to 10,000 paths (`unnest` with a lateral latest-version
      lookup on the `(path, seq)` key). The remote index sends batches of 5,000 paths to `/paths/stat`, and the server
      answers each batch with one `GetManyAsync`.
- [x] Landing makes one batched lookup per attempt for all touched paths. Outcomes are unchanged: every existing
      landing test passes.
- [x] PostgreSQL's `ChangedSinceAsync` filters on `seq > after` before taking each path's latest version, and returns
      the same result as before (the latest version of each path changed after the seq).
- [x] PostgreSQL's `ListAsync` finds children by a skip scan (a recursive query that hops along the `(path, seq)` key
      past a file's versions and past whole subdirectories). A file is kept when its latest version at the seq is live,
      a directory when some descendant's is (an early-exit `EXISTS`). Results are unchanged: deleted files and fully
      deleted subtrees are left out, and a re-created subtree comes back.
- [x] Contract tests cover `GetManyAsync`, `ChangedSinceAsync` and `ListAsync` with history, on memory, SQLite and
      PostgreSQL.
- [x] A benchmark on local PostgreSQL measures a 50,000-path lookup, a `ChangedSince` and listings over deep history,
      before and after the change.

## Approach

`GetManyAsync` is a default interface method that calls `GetAsync` for each path, so an index without a batch form
keeps working; each store overrides it. PostgreSQL uses
`SELECT ... FROM unnest(@paths) WITH ORDINALITY AS q(path, n) CROSS JOIN LATERAL (SELECT ... WHERE path = q.path AND
seq <= @seq ORDER BY seq DESC LIMIT 1)`; a path without a row stays null by its ordinal. SQLite answers all paths through one prepared command on one connection.
Landing prefetches the versions of all touched paths before its loop.

## Decisions

- Fix the slow queries; keep the schema. Storing ids as bytea, partitioning by seq and pruning old history are left for
  later.
- Batch sizes are bounded (10,000 per SQL statement, 5,000 per HTTP request) so requests and statements stay small.

## Progress

- [x] Implementation and tests: 201 tests pass, including the new contract tests on memory, SQLite and PostgreSQL
      (more paths than one statement or request takes, duplicates, missing and deleted paths, history).
- [x] Benchmark on local PostgreSQL 16 (loopback, so round trips cost ~0.25 ms; against a remote server each costs
      1-2 ms or more). 50,000 paths with 20 versions each (1,000,100 rows):
      - lookup of all 50,000 paths: 12,678 ms one by one (the old landing) vs 407 ms batched (31x faster);
      - `ChangedSince("src", after 20)`: 4,789 ms with the old query vs 1 ms (100 rows either way).
      - listings at seq 21 (`src/m0` deleted): root 1,646 ms vs 28 ms; `src` 1,223 ms vs 31 ms (199 children); a
        250-file directory 9 ms vs 4 ms.
- [x] CI green on the PR for the batched lookup and ChangedSince (Test with PostgreSQL and Azurite, Dev Drive, both
      Local cloud jobs, packages).
- [x] CI green with the listing change (every check); 203 tests pass locally with PostgreSQL and Azurite.

## Validation

- `dotnet test` with and without `BASSIA_TEST_POSTGRES`, plus the benchmark numbers recorded under Progress.
