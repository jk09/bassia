# Azure backing store: Blob Storage and Azure Database for PostgreSQL, with lean client sync

## Outcome

`bassia-server` can keep a monorepo in Azure: objects (chunks, trees, commits, operations, views) in an Azure Blob
Storage container, and heads, the path index and leases in Azure Database for PostgreSQL, so several server nodes can
serve one monorepo. Clients synchronize efficiently: they fetch only the objects a command needs, in few round trips,
compressed where it helps, with large chunks moving between client and Blob Storage directly on SAS URLs, and nothing
fetched twice.

## Context

- The version core reaches storage only through `IObjectStore`, `IViewStore`, `IPathIndex` and `ILeaseStore`. The
  server today offers a local-disk blob container or S3 for objects, and SQLite for the rest (single node).
- The design document names Azure Blob Storage with SQL as the natural first cloud pair for a Microsoft stack, the
  ViewStore and PathIndex sharing one database, and presigned direct transfers for chunks.
- Client reads today: every tree, commit or small chunk the client lacks is one HTTP request; checkout fetches file
  content one chunk at a time; uploads of structural objects are one request each. The server already has a batch
  endpoint (`/objects/batch`) no client uses.
- Authentication and multi-tenancy stay out of scope (design non-goal); the server keeps its optional shared token.

## Acceptance criteria

Storage

- [x] `AzureBlobObjectStore` implements `IObjectStore` and `IObjectStoreMaintenance` on a container (objects keyed by
      hash prefix under a configurable prefix): has, stat, ranged read, idempotent put, SAS URLs for direct upload
      and download, listing with write times, delete.
- [x] It authenticates with a connection string (account key, also Azurite) or with Microsoft Entra ID
      (`DefaultAzureCredential`, e.g. a managed identity) and a container URL; SAS URLs use the account key, or a user
      delegation key under Entra ID.
- [x] `PostgresState` implements `IViewStore`, `IPathIndex` and `ILeaseStore` on PostgreSQL (Azure Database for
      PostgreSQL flexible server): heads by compare-and-swap, the path index with byte-order (`COLLATE "C"`) path
      ranges, leases with a fencing sequence and FIFO waiting, schema created on start; safe for several server
      nodes at once.
- [x] Both pass the existing store contract tests (memory, SQLite, and now PostgreSQL; memory, disk, and now Azure
      Blob via Azurite), and concurrent compare-and-swap through two `PostgresState` instances lets exactly one win.
- [x] `bassia-server -storage azure (-azure-connection <string> | -azure-url <container url>) [-azure-prefix <p>]
      -state postgres -postgres <connection string>`, each also from `BASSIA_SERVER_*`; `gc` and `scrub` work on them.

Client sync

- [x] Checking out a tree prefetches its structural objects level by level in batches (`/objects/batch`), so a
      checkout costs about one request per tree level plus one per batch of missing objects, not one per tree.
- [x] Small chunks (under the direct-transfer threshold) come in the same batches; large chunks still move on SAS
      URLs, several at a time.
- [x] Structural objects written by a snapshot or a landing are uploaded in one batched request after one `Have`.
- [x] Server responses with JSON or object bytes are compressed (Brotli or gzip) when the client accepts it.
- [x] A second workspace on the same machine downloads no object the first one fetched (shared cache).
- [x] Tests count server requests for a checkout of a wide tree (local-disk and Azure backends) and hold them to these
      bounds.

Docs and CI

- [x] CI runs the PostgreSQL and Azure contract and server tests against service containers (PostgreSQL 16, Azurite).
- [x] `docs/reference.md` documents the Azure configuration, the sync protocol and its costs.

## Approach

`Bassia/Stores/AzureBlobObjectStore.cs` (Azure.Storage.Blobs, Azure.Identity), `Bassia/Stores/PostgresState.cs`
(Npgsql). The server's state becomes an `IServerState` (the three traits) chosen by `-state sqlite|postgres`; the object
store by `-storage disk|s3|azure`. Read URLs come from the store (S3, Azure) or the server's signer (disk).

Client: `IObjectBatchSource` lets a store fetch many objects in one call; `RemoteObjectStore` implements it over
`/objects/batch` (extended to include small chunks), `CachingObjectStore` forwards the missing ids and caches results.
`Trees` gains a prefetch hook (`IObjectPrefetcher`) the checkout calls per tree level; content of a level's files is
prefetched in batches before files are written, and large chunks are fetched in parallel. `ObjectAccess` buffers
structural writes during a snapshot and flushes them with one `Have` and one batched put (`/objects/batch-put`).
ASP.NET Core response compression for `application/json` and `application/octet-stream`.

## Decisions

- PostgreSQL schema mirrors SQLite's; `COLLATE "C"` keeps path ranges in byte order; lease grants serialize through a
  transaction-scoped advisory lock; fencing tokens come from a sequence.
- Landing stays a compare-and-swap on the head followed by the path index (with catch-up from the operation log), as
  for every backend; one transaction for both is left as an optimization.
- No deployment templates (Bicep/Terraform) in this run: configuration and docs only.
- Entra ID for PostgreSQL (token-based passwords) is not in this run; connection strings (password, `SSL Mode=Require`).
- Landing indexes through the operation log's catch-up instead of applying its own seq alone: with several nodes, the
  previous landing may not be indexed yet, and the path index must stay gap-free and in seq order.
- Batching uses a binary frame format (`application/vnd.bassia.objects`: id, length, bytes) rather than base64 JSON.
- A client's writes during a snapshot are held in a write batch (async-local) and flushed right before the workspace
  head moves, so a head never points at an object that is not stored.

## Progress

- [x] Azure blob store, PostgreSQL state, server options.
- [x] Batched, compressed, parallel client sync: a 221-file checkout in at most 16 object requests (was ~260), a
      second checkout with no object reads, a snapshot in one `Have` and at most a few batched puts.
- [x] Tests pass locally (157) against PostgreSQL 16 and Azurite 3.37; docs written.
- [x] CI runs them against its PostgreSQL service and Azurite.

Not verified here: Microsoft Entra ID authentication to Blob Storage (`-azure-url`, user delegation SAS) needs a
real Azure account; the code path is the documented SDK one, and connection-string authentication is what the tests
exercise.

## Validation

- `dotnet test` with `BASSIA_TEST_POSTGRES` (a PostgreSQL connection string) and `BASSIA_TEST_AZURE_BLOB` (an Azurite
  connection string): contracts, a server on Azure + PostgreSQL with two clones, request-count bounds.
- The same in CI with service containers.
