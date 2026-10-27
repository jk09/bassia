# Native cloud storage for component histories: S3 and DynamoDB

Phase 1, part 2 of 4 of hosting Bassia as a scalable service. Builds on `bassia-e13e1548` (shared-state contracts);
used by `bassia-f245cd5f` (Bassia server) and deployed by `bassia-b9581afe` (AWS).

## Outcome

A third implementation of the storage contract (`IComponentStorage`, `IComponentStore`) keeps component histories in
AWS instead of git repositories on a disk: content in S3, refs and the commit graph in DynamoDB. It holds git's own
objects under git's own ids, so a component's commits, tags and file ids are exactly those git computes, imports from
GitHub keep their history byte for byte, and any git client - Bassia's partial clones and views included - clones and
pushes through the server's git gateway. It scales out with the number of components and their traffic, has no
single disk or machine to outgrow, and lands several components' refs atomically.

## Context

The storage contract (`Bassia/Storage`, see "Architecture" in `docs/reference.md`) is implemented by `Storage/Git`
(bare repositories next to the meta-repo) and `Storage/Memory` (the unit tests' reference). `bassia-f66364b5` added
history-only stores, hydration and upstream fetch/push to it. Git repositories on a volume do not fit an organization
of ~1,000 users and ~500 concurrent agents well: one disk per repository set, no atomic update across repositories,
and replication and backup are ours to build. Prior art for the same shape: Meta's Mononoke (blob store plus SQL behind
Sapling), GitHub's Spokes and GitLab's Gitaly (git services in front of replicated storage).

On the client nothing changes: a clone's stores stay git partial clones whose upstream is the server's git gateway
(`bassia-f245cd5f`), so working copies, views and sparse runs keep using `Storage/Git` locally. This backend is the
server's storage.

## Acceptance criteria

### Data model

- [ ] Objects are git objects (blob, tree, commit, annotated tag) with git's ids (SHA-1; SHA-256 repositories are
      refused with a clear error until supported). They live in S3 in packfiles, per component
      (`s3://<bucket>/components/<component>/packs/<pack>.pack`), never rewritten in place; a DynamoDB object index maps
      `(component, object id)` to its pack, offset, size and type, so one object is one ranged GET.
- [ ] Refs (branches, tags, the default branch) are DynamoDB items `(component, ref) -> (commit, version)`; every update
      is a conditional write, so `SetBranchAsync(branch, commit, expected)` is compare-and-swap with no lock; a tag never
      changes once created. A ref is written only after every object it reaches is stored and indexed.
- [ ] A commit-graph table holds each commit's parents, tree and generation number, so `HistoryAsync`,
      `IsAncestorAsync`, `MergeBaseAsync`, `FindObjectsAsync` (id prefix) and ref resolution read no packs.
- [ ] Several components' refs move in one transaction (`TransactWriteItems`) or not at all: the integration's advance
      of several components is atomic. A batch larger than one transaction allows is refused before anything moves.

### The storage contract

- [ ] Every member of `IComponentStorage` and `IComponentStore` is implemented: create, import from a URL (a git fetch
      whose packs are uploaded and indexed), import history only (`ImportHistoryAsync`: the commit graph and trees,
      content fetched from the source when read), fork (new refs over the parent's objects, no copy), split, delete,
      staging, branches, tags, commits, files, nested units, merges, merge bases, diffs, hydration, upstream fetch/push.
- [ ] Operations that need git's algorithms (three-way merge with a merge driver, diff, split's history rewrite,
      commits without nested units) run in a scratch git repository on the server's ephemeral disk, filled from S3 with
      the objects they touch and discarded or kept as a size-bounded cache; their results are uploaded as packs. The
      result equals what `Storage/Git` computes for the same inputs.
- [ ] `CheckoutAsync` and the working-copy members run where a working copy can exist: on the server for the
      integration's and verification's checkouts (into the scratch area); clients never get a working copy from this
      backend directly, they clone through the gateway.

### Serving git

- [ ] A git smart-HTTP service (protocol v2: `ls-refs`, `fetch` with filters such as `blob:none`, objects by id for
      partial clones, `push` with atomic multi-ref updates) serves every component from this backend, building packs
      from the stored packs without re-compressing what it can reuse. Bassia's `clone`, views, sparse runs and
      write-through work against it unchanged.

### Operations

- [ ] Garbage collection: objects no ref reaches, older than a grace period (default 14 days, so concurrent pushes and
      runs are safe), are removed by a background job that repacks a component's live objects; it never runs on a
      component twice at the same time.
- [ ] Durability and recovery: S3 versioning and DynamoDB point-in-time recovery are required by the backend's
      configuration check; a restore procedure (both to the same point in time) is documented and tested.
- [ ] Performance at the target scale (~1,000 users, ~500 concurrent agents, thousands of components): a load test
      records ref update, clone (history only), blob fetch and push latencies at the 50th and 99th percentiles and
      throughput; targets are agreed in this record before implementation and met.
- [ ] Cost: requests are batched (packs, not loose objects; index lookups batched with `BatchGetItem`), and an estimate
      of storage and request costs per 1,000 components and per 1,000 runs is recorded.

### Tests and docs

- [ ] A contract test suite (one set of tests every backend must pass) runs against `Storage/Memory`, `Storage/Git`
      and this backend - in CI against local S3 and DynamoDB emulators (MinIO or LocalStack, DynamoDB Local).
- [ ] Equivalence tests: the same sequence of operations on `Storage/Git` and on this backend gives the same commit
      ids, trees, merge results and diffs.
- [ ] `docs/reference.md` ("Architecture") describes the backend: data model, consistency rules, GC, limits.

## Approach

`Storage/Cloud` with three parts: an object store (pack writer and reader, the DynamoDB object index, an in-memory LRU
cache), a ref and graph store (DynamoDB tables with conditional and transactional writes), and the compute area (a
scratch bare repository per component, filled on demand from S3, for the operations git does best). Packs arrive from
pushes, publishes and imports as they are; reads range-GET single objects or whole packs. Start with the read path and
ref store, so a component imported from git can be served and cloned; then writes (publish, tag, set branch), then the
computed operations, then GC.

## Decisions

- Histories go straight to S3 and DynamoDB in Phase 1, rather than git repositories on EBS first. (User decision.)
- Keep git's object format and ids: Bassia stays git-compatible, imports and the gateway need no translation, and
  clients keep using git locally.
- Packs per component, never shared across components: the component stays the unit of access, a component can be
  deleted or exported on its own. Forks may read their parent's packs.
- Merges, diffs and splits reuse git on a scratch copy instead of reimplementing them.
- One organization per deployment, so the tables and bucket carry no tenant key. (User decision.)

## Open questions

- Latency and throughput targets for the load test (to be agreed before implementation).
- SHA-256 repositories: refuse for now (proposed), or support from the start?

## Progress

- [ ] Object store and read path; import from git; ref and graph store
- [ ] Git gateway: clone, fetch with filters, objects by id
- [ ] Write path: push, publish, tags, conditional and atomic ref updates
- [ ] Computed operations in the scratch area
- [ ] GC, recovery, configuration checks
- [ ] Contract and equivalence suites in CI; load test; docs

## Validation

```sh
dotnet test --filter "Category!=EndToEnd"
dotnet test --filter "Category=StorageContract"     # every backend, cloud against MinIO/LocalStack + DynamoDB Local
```

Plus the load test at the target scale on a staging deployment (`bassia-b9581afe`), with results recorded here.
