# Wide directories: store a tree in pages, so a change rewrites a page

Status: done - closed without implementation, by the user's decision to keep tree ids constant (see Decisions). Kept for its measurements. Third scalability step; measured with the load harness (`bassia-1def6a48`).

## Outcome

Changing one file in a directory with thousands of entries costs what changing one file in a small directory costs.
Tree ids, and so every commit, clone, git import and export, stay exactly as they are.

## Context

- A tree is stored as one row in `bassia_trees` and one row per entry in `bassia_tree_entries` (`PostgresTreeStore`).
  Every snapshot and landing writes a new version of each directory on the changed file's path, in full: a directory
  with N entries costs N rows written, and N rows read by whoever opens it.
- Measured (`bassia-loadtest sweep -files 20000 -fanout 20,200,2000,20000`, small scenario otherwise):

  | Entries per directory | snapshot p50 | land p50 | seeding 200 landings |
  | ---: | ---: | ---: | ---: |
  | 20 | 79 ms | 65 ms | 5.0 s |
  | 200 | 106 ms | 73 ms | 4.9 s |
  | 2,000 | 321 ms | 341 ms | 15.0 s |
  | 20,000 | 731 ms | 641 ms | 46.2 s |

  Statements, requests and blob calls do not change; the time is rows moved. Up to a few hundred entries nothing
  shows; at 2,000 a snapshot is four times slower.
- A tree's id is the BLAKE3 hash of its canonical CBOR encoding (all entries, sorted). Git import and export, clones
  and scrub depend on it; `/trees/fetch` returns that encoding.
- Trees are primary data in PostgreSQL (no copy in blob storage), and garbage collection walks them with recursive
  queries over `bassia_tree_entries` (`ReachAsync`).

## Acceptance criteria

None is met, and none will be: the outcome above needs a tree format whose id covers pages, which the user ruled out.
What was measured stays below, for whoever reopens the question.

## Approach

Split a tree's entries into pages by content-defined boundaries on entry names (so an insertion moves one boundary,
not all of them), store each page once under the hash of its entries, and keep per tree the ordered list of its pages.
A tree's id is still computed over all its entries; only storage changes. Lookups go tree -> page by name range ->
entry.

## Decisions

Taken unattended, as the user asked (the recommended options):

- **Storage only, ids untouched.** Changing the tree object itself (a real prolly tree or HAMT with ids per node)
  would change every commit id and the git mapping; nothing measured calls for that.
- **Not started in this pass.** It rewrites the primary store of trees and needs an in-place migration of data that
  exists nowhere else; it should be its own change with its own review, after the object catalog.

- **Blocked: storage-only paging is not worth its migration; the rest needs a new tree format.** Measured on
  `PostgresTreeStore` alone, one changed file in a directory costs, per new version:

  | Entries | Build and encode | Insert rows | Read back |
  | ---: | ---: | ---: | ---: |
  | 200 | 0.8 ms | 3 ms | 5 ms |
  | 2,000 | 10 ms | 26 ms | 16 ms |
  | 20,000 | 45 ms | 192 ms | 42 ms |

  Paging the rows (the approach above) removes the insert - about a quarter of a snapshot's 731 ms at 20,000 entries
  - and nothing else: the id is still the hash of the whole canonical encoding, so the client still builds and
  encodes all entries, sends them all in `/trees/put` (about 1.6 MB), and the server decodes them all. Removing those
  costs needs a tree object whose id covers pages (a prolly tree or a HAMT), which changes every tree and commit id
  of existing monorepos, the git mapping's tree side, and the clone protocol. That is a product decision, not one to
  take unattended: **may tree ids change for directories above a size, with existing monorepos converted (or kept
  on the current format)?** Until it is answered, nothing is implemented; directories of a few hundred entries -
  the common case - cost nothing measurable today.

- **Decided by the user: tree ids stay constant.** No tree format change, for any directory size. That leaves only
  storage-only paging, which removes the row insert - about a quarter of a snapshot at 20,000 entries - and nothing
  else, at the price of migrating trees that exist nowhere but PostgreSQL. Not worth it: the record is closed without
  implementation. Directories of a few hundred entries, the common case, cost nothing measurable.
- **If large directories become common**, cheaper steps that keep ids constant come first: sending a tree as its base
  and the changed entries, with the server rebuilding the encoding and checking the id (saves the upload, not the
  encoding or the insert), and copying unchanged rows inside PostgreSQL instead of sending them.

Questions this record had open (moot now):

0. May tree ids change for large directories? No (the user).
1. Page size target (proposed: about 256 entries) and whether small trees (under one page) keep today's layout.
2. Whether the migration may run online in the background, as the mainline index's backfill does, or as a step of
   `bassia-server migrate`.

## Progress

- [x] Measured (`-fanout` sweep, tree store probe); closed by decision, nothing implemented

## Validation

`dotnet test`; `bassia-loadtest sweep -files 20000 -fanout 20,200,2000,20000` before and after.
