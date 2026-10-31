namespace Bassia.Core.Storage;

using Bassia.Core.Model;

/// <summary>A byte range of an object: <see cref="Length"/> bytes from <see cref="Offset"/>.</summary>
public readonly record struct ByteRange(long Offset, long Length);

/// <summary>What a store knows about an object without its payload.</summary>
public sealed record ObjectStat(ObjectId Id, long Length);

/// <summary>Where to send a large chunk directly (a presigned URL), or null when the client puts it through the store.</summary>
public sealed record UploadTicket(ObjectId Id, Uri? Url, DateTimeOffset Expires);

/// <summary>
/// Immutable, content-addressed objects: chunks and the canonical encodings of structural objects, keyed by the hash of
/// their bytes. Read-after-write per key is all an implementation must give; nothing ever needs a listing to find an
/// object.
/// </summary>
public interface IObjectStore
{
	ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default);

	ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default);

	/// <summary>The object's bytes, or the given range of them; null when it is not stored.</summary>
	ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default);

	/// <summary>Stores an object; idempotent. The id must be the hash of the bytes.</summary>
	ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);

	/// <summary>Where to upload a large chunk directly, for stores that offer presigned URLs; otherwise a ticket without a URL.</summary>
	ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default);
}

/// <summary>
/// A store that reads many objects in one round trip: those it returns are whole and verified by the caller; ids it
/// leaves out (missing, or too large to batch) are read one by one.
/// </summary>
public interface IObjectBatchReader
{
	Task<IReadOnlyDictionary<ObjectId, ReadOnlyMemory<byte>>> ReadManyAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default);
}

/// <summary>A store that writes many objects in one round trip.</summary>
public interface IObjectBatchWriter
{
	Task PutManyAsync(IReadOnlyList<(ObjectId Id, ReadOnlyMemory<byte> Bytes)> objects, CancellationToken ct = default);
}

/// <summary>A store with a local cache that can be filled ahead of reads, in as few round trips as it can.</summary>
public interface IObjectPrefetcher
{
	Task PrefetchAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default);
}

/// <summary>A stored blob as maintenance sees it: id, length and when it was written.</summary>
public sealed record BlobInfo(ObjectId Id, long Length, DateTimeOffset Written);

/// <summary>What garbage collection and scrubbing need besides <see cref="IObjectStore"/>: listing and deleting.</summary>
public interface IObjectStoreMaintenance
{
	IAsyncEnumerable<BlobInfo> ListAsync(CancellationToken ct = default);

	ValueTask DeleteAsync(ObjectId id, CancellationToken ct = default);
}

/// <summary>A named head: <c>main</c>, or <c>ws/&lt;id&gt;</c> for a workspace.</summary>
public readonly record struct HeadName(string Value)
{
	public static readonly HeadName Main = new("main");

	public const string WorkspacePrefix = "ws/";

	public static HeadName Workspace(string id) => new(WorkspacePrefix + id);

	public bool IsWorkspace => Value.StartsWith(WorkspacePrefix, StringComparison.Ordinal);

	public string WorkspaceId => IsWorkspace ? Value[WorkspacePrefix.Length..] : throw new InvalidOperationException($"'{Value}' is not a workspace head.");

	public override string ToString() => Value;
}

/// <summary>The outcome of a compare-and-swap on a head: whether it moved, and where the head is now.</summary>
public sealed record HeadUpdate(bool Success, ObjectId? Current);

/// <summary>
/// The only mutable pointers: named heads into the operation log. Each head moves by compare-and-swap alone.
/// </summary>
public interface IViewStore
{
	ValueTask<ObjectId?> GetHeadAsync(HeadName name, CancellationToken ct = default);

	/// <summary>Atomic compare-and-swap; on a stale <paramref name="expected"/> it returns the current head instead of failing.</summary>
	ValueTask<HeadUpdate> AdvanceHeadAsync(HeadName name, ObjectId? expected, ObjectId next, CancellationToken ct = default);

	/// <summary>Every head whose name starts with <paramref name="prefix"/>, with the operation it points at.</summary>
	IAsyncEnumerable<(HeadName Name, ObjectId Head)> ListHeadsAsync(string prefix = "", CancellationToken ct = default);

	/// <summary>Removes a head if it still points at <paramref name="expected"/> (retention of closed workspaces).</summary>
	ValueTask<bool> DeleteHeadAsync(HeadName name, ObjectId expected, CancellationToken ct = default);
}

/// <summary>One version of a path on mainline: what it held from <see cref="Seq"/> on; a null leaf is a deletion.</summary>
public sealed record PathVersion(string Path, long Seq, Leaf? Leaf);

/// <summary>A path's new content at a landing; a null leaf deletes it.</summary>
public sealed record PathChange(string Path, Leaf? Leaf);

/// <summary>A child of a directory as of some seq: a file (with its leaf) or a directory.</summary>
public sealed record PathListing(string Path, bool IsDirectory, PathVersion? Version);

/// <summary>
/// Mainline's path index: for every file path, its versions by seq. Derived from trees and rebuildable from the
/// operation log; only the landing service applies changes to it.
/// </summary>
public interface IPathIndex
{
	/// <summary>The latest version of a path at or before <paramref name="seq"/>, or null if it never existed by then.</summary>
	ValueTask<PathVersion?> GetAsync(string path, long seq, CancellationToken ct = default);

	/// <summary>The files and directories directly in <paramref name="dir"/> as of <paramref name="seq"/>.</summary>
	IAsyncEnumerable<PathListing> ListAsync(string dir, long seq, CancellationToken ct = default);

	/// <summary>The latest version of every path under <paramref name="prefix"/> that changed after <paramref name="afterSeq"/>.</summary>
	IAsyncEnumerable<PathVersion> ChangedSinceAsync(string prefix, long afterSeq, CancellationToken ct = default);

	/// <summary>Records the changes of the landing numbered <paramref name="seq"/>; applying a seq again is a no-op.</summary>
	ValueTask ApplyAsync(long seq, IReadOnlyList<PathChange> changes, CancellationToken ct = default);

	/// <summary>The highest seq applied so far (0 when none).</summary>
	ValueTask<long> IndexedSeqAsync(CancellationToken ct = default);
}

/// <summary>Exclusive leases exclude every overlapping lease; intent leases coexist and only announce work.</summary>
public enum LeaseMode
{
	Exclusive,
	Intent
}

/// <summary>A request for a lease on a path prefix, for an owner (a workspace), with a time to live and how long to wait.</summary>
public sealed record LeaseRequest(string Prefix, LeaseMode Mode, string Owner, TimeSpan Ttl, TimeSpan Wait = default, string? Run = null);

/// <summary>A granted lease. The fencing token grows with every grant, so a re-granted prefix never accepts an old holder.</summary>
public sealed record Lease(string Id, string Prefix, LeaseMode Mode, string Owner, string? Run, long Token, DateTimeOffset Granted, DateTimeOffset Expires);

/// <summary>The answer to an acquire: the lease, or the leases in the way.</summary>
public sealed record LeaseResult(Lease? Lease, IReadOnlyList<Lease> Blocking)
{
	public bool Granted => Lease is not null;
}

/// <summary>Advisory path leases with fencing tokens.</summary>
public interface ILeaseStore
{
	ValueTask<LeaseResult> AcquireAsync(LeaseRequest request, CancellationToken ct = default);

	/// <summary>Extends a lease; null when it expired or was taken over.</summary>
	ValueTask<Lease?> RenewAsync(string id, long token, TimeSpan ttl, CancellationToken ct = default);

	ValueTask ReleaseAsync(string id, long token, CancellationToken ct = default);

	/// <summary>The live leases (not expired), optionally only those overlapping a prefix.</summary>
	ValueTask<IReadOnlyList<Lease>> ListAsync(string? prefix = null, CancellationToken ct = default);

	/// <summary>A live lease by id, or null.</summary>
	ValueTask<Lease?> GetAsync(string id, CancellationToken ct = default);
}

/// <summary>A failure of a store or of a version operation, reported to the user as an error rather than a crash.</summary>
public class VersionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An object that should exist is missing from the store.</summary>
public sealed class MissingObjectException(ObjectId id) : VersionException($"Object {id.Short} is missing from the store.")
{
	public ObjectId Id { get; } = id;
}
