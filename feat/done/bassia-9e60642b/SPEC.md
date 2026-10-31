# Workspaces on a Windows Dev Drive: block-cloned checkouts

## Outcome

On Windows, when a monorepo folder and its workspaces sit on a [Dev Drive](https://learn.microsoft.com/en-us/windows/dev-drive/)
(or any ReFS volume with block cloning), creating, syncing or restoring a workspace no longer writes every file's bytes.
Bassia keeps one copy of each file's content in a local clone cache on the same volume and block-clones it into
workspaces: a metadata-only, copy-on-write copy, so many agent workspaces over a large monorepo cost little time, I/O
and disk space. Everywhere else nothing changes. `bassia devdrive status` tells whether a folder qualifies and why.

## Context

- Workspaces are plain checkouts (`CheckoutProvider` in `Bassia.Core/Workspaces`): every file of the tree is written
  from the object store into each workspace. With a workspace per agentic run, the same files are written again and
  again.
- Dev Drive is a ReFS volume for developer workloads. ReFS shares clusters between files (block cloning,
  `FSCTL_DUPLICATE_EXTENTS_TO_FILE`) where the volume reports `FILE_SUPPORTS_BLOCK_REFCOUNTING`; on Dev Drive that is
  Windows 11 24H2 / Windows Server 2025 and later. Writes to a cloned file reallocate only the clusters written, so
  clones are isolated. Clone ranges must be cluster aligned; the destination needs its final size first.
- `FSCTL_QUERY_PERSISTENT_VOLUME_STATE` reports `PERSISTENT_VOLUME_STATE_DEV_VOLUME` (0x2000) and
  `PERSISTENT_VOLUME_STATE_TRUSTED_VOLUME` (0x4000).
- The core may not reference platform code beyond the workspace layer (design); Windows interop belongs in `Bassia`.

## Acceptance criteria

- [x] The workspace provider takes an optional file materializer (`IFileMaterializer` in `Bassia.Core`): a file leaf
      is handed to it first, and written from the store as before when it declines or fails.
- [x] A clone cache (`<monorepo>/.bassia/clones`, one read-only file per content id) materializes files of at least 8
      KiB by block cloning from the cache into the workspace; a missing cache entry is written once from the store.
      Smaller files, symlinks and conflicts are written as before.
- [x] Cloning is used only on Windows, when `workspace.clone` is `auto` (the default), the workspace folder is on
      the same volume as the cache, and the volume supports block cloning. A failed clone (e.g. too many references to
      a cluster) falls back to a copy, then to writing; nothing fails because cloning is unavailable. `off` disables it.
- [x] A cloned file edited in one workspace changes neither the cache nor another workspace (copy-on-write).
- [x] `workspace create`, `checkout` and `clone` report how many files were cloned and written.
- [x] `bassia devdrive status [-path <dir>]` reports for a folder: the file system, whether it is a Dev Drive and
      trusted, whether it supports block cloning, whether workspaces of this monorepo there will be cloned (and why
      not), and the clone cache's size. Off Windows it says cloning is Windows-only.
- [x] `bassia gc` (local monorepos and clones) prunes cache entries no local workspace and no file of mainline's head
      uses; in a clone it prunes only the cache.
- [x] Unit tests cover the cache, fallbacks, isolation and pruning on every platform with a stand-in cloner; a
      Windows CI job creates a Dev Drive (ReFS VHD) and runs the real block cloning tests.
- [x] `docs/reference.md` and `README.md` describe Dev Drive use.

## Approach

`Bassia.Core`: `IFileMaterializer` and its use in `CheckoutProvider.WriteAsync`. `Bassia/Platform`: `IFileCloner`
(`WindowsBlockCloner`: `GetVolumeInformationByHandleW` for the file system and `FILE_SUPPORTS_BLOCK_REFCOUNTING`,
`GetVolumePathNameW` for same-volume checks, `FSCTL_DUPLICATE_EXTENTS_TO_FILE` in cluster-aligned ranges of at most
1 GiB after setting the destination's size, `FSCTL_QUERY_PERSISTENT_VOLUME_STATE` for Dev Drive and trust),
`CloneCache` (implements `IFileMaterializer`; temp-then-rename writes, read-only entries, statistics, pruning).
`Monorepo` wires the cache into the providers it creates. CLI: the `devdrive status` command, statistics in results,
the `workspace.clone` setting, pruning in `gc`. CI: a `windows-2025` job that creates and formats a VHD as Dev Drive
(falling back to plain ReFS) and runs tests in `Category=DevDrive` against it.

## Decisions

- No extra package: block cloning is one `DeviceIoControl` call; Microsoft's `CopyOnWrite` library adds nothing we
  need, and on 24H2 `CopyFile` (so `File.Copy`) clones by itself on Dev Drive, which is the fallback path.
- The cache lives in the monorepo folder (`.bassia/clones`): put the monorepo folder on the Dev Drive; workspaces
  created on another volume are written as before.
- Files under 8 KiB are written directly: a clone is not cheaper than writing a file of a cluster or two.
- `workspace.clone` is `auto` or `off`: an `on` that forces cloning where the volume cannot clone would only fail.
- Bassia does not create or configure Dev Drives (that needs an administrator); the docs point to Windows' own tools.

## Progress

- [x] Core hook, cloner, cache, wiring.
- [x] CLI: status command, statistics, setting, gc.
- [x] Tests with a stand-in cloner pass on Linux (149 tests); the Windows CI job and docs are written.
- [x] Real block cloning verified by the `Dev Drive (Windows)` CI job on windows-2025: a VHD formatted as a trusted Dev
      Drive (ReFS, block cloning supported); clone isolation and block-cloned workspaces pass.

## Validation

- `dotnet test` on Linux (stand-in cloner) and the `Dev Drive (Windows)` CI job (real cloning on a ReFS/Dev Drive VHD).
- Manual on a Windows 11 24H2 machine with a Dev Drive: `bassia devdrive status`, then `workspace create` twice and
  compare times and `fsutil volume diskfree`.
