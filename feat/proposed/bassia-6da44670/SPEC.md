# Wide directories: store a tree in pages, so a change rewrites a page

Status: proposed. Third scalability step; measured with the load harness (`bassia-1def6a48`).

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

- [ ] A tree's id and canonical encoding are unchanged: every existing test that compares ids, scrub, git export and a
      clone of a server pass without change.
- [ ] Writing a new version of a directory with 20,000 entries after changing one file inserts rows in proportion to
      one page of entries (a few hundred), not 20,000 (a test counts rows).
- [ ] Path lookup, subtree walk, diff and garbage collection's reachability return what they return today, each still
      in a bounded number of queries.
- [ ] Existing databases are migrated in place, in batches, resumably, with old and new nodes working during a rolling
      deploy; a clone's tree cache is rebuilt instead.
- [ ] Harness: the `-fanout` sweep above no longer marks snapshot and landing as growing; at 20 and 200 entries
      nothing gets slower.

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

Open questions:

1. Page size target (proposed: about 256 entries) and whether small trees (under one page) keep today's layout.
2. Whether the migration may run online in the background, as the mainline index's backfill does, or as a step of
   `bassia-server migrate`.

## Progress

- [ ] Page layout and store
- [ ] Migration
- [ ] Harness sweep before and after

## Validation

`dotnet test`; `bassia-loadtest sweep -files 20000 -fanout 20,200,2000,20000` before and after.
