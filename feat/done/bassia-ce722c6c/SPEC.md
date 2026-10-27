# Native cloud storage for component histories: S3 and DynamoDB

Phase 1, part 2 of 4 of hosting Bassia as a scalable service. Builds on `bassia-e13e1548` (shared-state contracts);
used by `bassia-f245cd5f` (Bassia server) and deployed by `bassia-b9581afe` (AWS).

## Outcome

A third implementation of the storage contract (`IComponentStorage`, `IComponentStore`) keeps component histories in
AWS instead of git repositories on a disk: objects in S3 packs, refs and pack lists in DynamoDB. It holds git's own
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

- [x] Objects are git objects (blob, tree, commit, annotated tag) with git's ids (SHA-1; SHA-256 repositories are
      refused with a clear error until supported). They live in S3 in self-contained packfiles with their indexes, per
      component (`components/<component>/packs/<pack>.pack|.idx`), never rewritten in place; DynamoDB lists each
      component's packs.
- [x] Refs (branches, tags, the default branch) are DynamoDB items of the component; every update is a conditional
      write, so `SetBranchAsync(branch, commit, expected)` is compare-and-swap with no lock, and a tag never changes once
      created. A ref is written only after the pack holding every object it reaches that the stored refs do not is
      uploaded and listed.
- [x] Several components' refs move in one transaction (`TransactWriteItems`) or not at all: `IComponentStorage` gains
      `MoveBranchesAsync`, which the integration's advance uses. A batch larger than one transaction allows is refused
      before anything moves. Git and memory storages check every expectation before they move anything.

### The storage contract

- [x] Every member of `IComponentStorage` and `IComponentStore` is implemented by `CloudComponentStorage`: create,
      import from a URL (its packs uploaded), import history only (a cloud store holds all content, so it imports
      everything), fork (the parent's packs copied server side within S3), split, delete, staging, branches, tags,
      commits, files, nested units, merges, merge bases, diffs, hydration (nothing to fetch), upstream fetch/push,
      history queries, working copies.
- [x] Git runs on a cache: every server node keeps a bare repository per component under a cache folder, brought up
      to the stored state (missing packs downloaded, refs and default branch set from DynamoDB) when a store is opened;
      reads and computations (log, merge-tree, diffs, splits) run on it, so results equal what `Storage/Git` computes
      for the same inputs. The cache is disposable: deleting it loses nothing.
- [x] Working copies (the integration's and verification's checkouts) are clones of the cache; publishing from one
      publishes to the store, through the same write path.

### Serving git

- [x] A git smart-HTTP gateway (`GitGateway`, ASP.NET endpoints) serves every component of a `CloudComponentStorage`:
      protocol v2 and v0 fetch and clone, filters such as `blob:none` and objects by id for partial clones (git's own
      upload-pack on the cache, reusing the stored packs), and push (git's receive-pack on the cache; the new objects and
      ref updates are then published with compare-and-swap, and a push that loses a race is refused as a whole). It can
      serve a meta-repo folder read-only beside the components, so `bassia clone -from <url>` works against it, and
      Bassia's views and sparse runs with write-through work unchanged.

### Operations

- [x] Garbage collection (`CloudGarbageCollector`): a component's objects reachable from its refs are repacked into one
      pack; packs older than a grace period (default 14 days, so objects of a write in flight survive) are then
      deleted. A lease item in DynamoDB keeps two collections of one component from running at once.
- [x] Durability and recovery: the configuration check of the AWS implementation requires S3 versioning and DynamoDB
      point-in-time recovery; the restore procedure (both to the same point in time) is documented.
- [x] Performance: proposed targets are recorded below, with a load-test script to run against a staging deployment
      (`bassia-b9581afe`), which runs it and records the results. (User decision: the load test runs in staging.)
- [x] Cost: requests are batched (whole packs, not loose objects; refs of a component read with one query), and an
      estimate of storage and request costs per 1,000 components and per 1,000 runs is recorded.

### Tests and docs

- [x] S3 and DynamoDB are reached through two small internal interfaces (`IObjectStore`, `IRecordTable`) with AWS SDK
      implementations and in-memory fakes. A storage contract test suite runs against `Storage/Memory`, `Storage/Git`
      and this backend on the fakes in the regular suite, and on DynamoDB Local and an S3 emulator when
      `BASSIA_CLOUD_TEST_ENDPOINT` is set. (User decision.)
- [x] Equivalence tests: the same sequence of operations on `Storage/Git` and on this backend gives the same commit
      ids, trees, merge results and diffs.
- [x] Gateway tests: clone, partial clone, fetch, push, a push that loses a race, and `bassia clone` + a sparse run
      writing through, against a gateway on a local port.
- [x] `docs/reference.md` ("Architecture") describes the backend: data model, consistency rules, cache, GC, limits.

## Approach

`Storage/Cloud`: `IObjectStore` and `IRecordTable` with AWS (S3, DynamoDB) and in-memory implementations;
`CloudComponentStorage` and `CloudComponentStore` wrapping `Storage/Git` on the cache folder - reads delegate after the
cache is brought up to date, writes compute on the cache and then publish: `git pack-objects` of what the new ref
values reach and the stored ones do not, upload, list the pack, move the refs conditionally (one transaction).
`GitGateway` maps the smart-HTTP endpoints onto `git upload-pack|receive-pack --stateless-rpc` on the cache.

## Decisions

- Histories go straight to S3 and DynamoDB in Phase 1, rather than git repositories on EBS first. (User decision.)
- Keep git's object format and ids: Bassia stays git-compatible, imports and the gateway need no translation, and
  clients keep using git locally.
- Git runs on a per-node cache filled from S3; S3 and DynamoDB are the durable truth. No object index and no
  commit-graph table: reads are served by git from the cache, which costs a download of a component's packs on a cold
  node. (User decision, replacing native reads.)
- The gateway is part of this record, in front of the cloud storage; the server record adds authentication. (User
  decision.)
- Packs per component, never shared across components: the component stays the unit of access, and a component can
  be deleted or exported on its own. A fork copies its parent's packs within S3 (no download), so the parent's GC can
  never break it.
- A cloud store is the source of truth and holds every object: `ImportHistoryAsync` imports everything, and nothing
  is ever missing.
- SHA-256 repositories are refused for now. (User decision.)
- One organization per deployment, so the tables and bucket carry no tenant key. (User decision.)
- Tests run against in-memory fakes of `IObjectStore`/`IRecordTable`, and against emulators when
  `BASSIA_CLOUD_TEST_ENDPOINT` (DynamoDB) and `BASSIA_CLOUD_TEST_S3_ENDPOINT` (S3) are set. (User decision.)
- The load test runs in staging: this record proposes the targets and ships the script. (User decision.)

Made during implementation:

- A cloud store's `Location` (and `LocationOf`) is its cache folder: callers that run git on a store's location (the
  splitter, the unwinder, integration records) keep working; the durable location is the table and bucket.
- A store is brought up to the table once, when first used; every write reads the records fresh and is conditional,
  so a store opened before another node's write can be stale for reads but never overwrites that write.
- The gateway runs receive-pack on a scratch repository (alternates to the cache, the cache's refs) instead of a hook,
  and writes git's report-status itself: refs reach readers only once published, and a push refused by the
  compare-and-swap is reported `ng` for every ref without a callback from a hook process.
- Garbage collection deletes a pack only once it has been superseded (or seen unlisted) for the grace period, not
  when the pack is older than it: an old pack may still hold objects a write in flight relies on.
- moto (`moto_server`, from PyPI) stands in for DynamoDB Local and MinIO locally: both were unreachable from the build
  environment. The contract suite passed against it (S3 and DynamoDB transactions).

## Performance targets (proposed; measured in staging)

| Operation | p50 | p99 |
| --- | --- | --- |
| Ref update (compare-and-swap, one component) | 20 ms | 100 ms |
| Atomic landing of 10 components | 50 ms | 250 ms |
| History-only clone of a component with 10,000 commits, warm cache | 2 s | 10 s |
| Fetch of one blob by id (partial clone), warm cache | 50 ms | 300 ms |
| Push of a run's result (one commit, a few files) | 300 ms | 1.5 s |

Throughput: 500 concurrent agents each pushing a result every 5 minutes and fetching on demand, over 2,000 components,
without errors and within the latencies above.

## Cost estimate (us-east-1 list prices, on-demand DynamoDB)

Assumptions: a component averages 200 MB packed; a run pushes one small pack and moves a branch and a tag; nodes sync
a component with one query per request; ten nodes each download a new pack once.

| Item | Per 1,000 components (month) | Per 1,000 runs |
| --- | --- | --- |
| S3 storage ($0.023/GB) | 200 GB: $4.60, plus up to ~50% for superseded packs within the grace and noncurrent versions | - |
| S3 PUT ($0.005/1k): pack + index per write | - | 2,000: $0.01 |
| S3 GET ($0.0004/1k): cold nodes downloading new packs | - | ~20,000: $0.01 |
| DynamoDB transactional writes (2 WRU per item, $0.625/M WRU): pack, branch, tag | - | ~6,000 WRU: <$0.01 |
| DynamoDB strongly consistent queries ($0.125/M RRU) | - | ~20,000 RRU: <$0.01 |
| DynamoDB storage + PITR ($0.25 + $0.20/GB) | <1 GB: <$0.50 | - |

Storage and requests are cents. What costs money is compute (the Fargate nodes and their cache volumes,
`bassia-b9581afe`) and traffic leaving AWS ($0.09/GB: a full clone of a 200 MB component costs ~$0.02; history-only
clones and blob-by-id fetches are much smaller). Requests stay batched: whole packs, never loose objects, and one query
per component and request.

## Progress

- [x] Object store and record table (AWS and in memory); AWS configuration check
- [x] `CloudComponentStorage`/`CloudComponentStore`/`CloudWorkspace` on the git cache; import, fork, split, staging
- [x] Write path: pack upload, conditional single-transaction ref writes, tags, `MoveBranchesAsync` (also git/memory)
      used by the integration's advance
- [x] Git gateway: clone and fetch (v0/v2), filters and objects by id, push with compare-and-swap, meta-repo read-only
- [x] GC with lease and grace; restore procedure; cost estimate; load-test script (`scripts/Invoke-CloudLoadTest.ps1`,
      to run in staging - pwsh is not available in the build environment, so it was not run here)
- [x] Storage contract suite (memory, git, cloud on fakes; on moto emulators), equivalence, gateway tests; docs

## Validation

```sh
dotnet test --filter "Category!=EndToEnd"
dotnet test --filter "Category=StorageContract"     # every backend, the cloud one on in-memory fakes
moto_server -p 4566 &                               # or DynamoDB Local + MinIO
BASSIA_CLOUD_TEST_ENDPOINT=http://127.0.0.1:4566 BASSIA_CLOUD_TEST_S3_ENDPOINT=http://127.0.0.1:4566 \
  dotnet test --filter "Category=StorageContract"   # the cloud one on the emulators
```

Plus the load test at the target scale on a staging deployment (`bassia-b9581afe`), with results recorded here.
