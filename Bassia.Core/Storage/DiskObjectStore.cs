namespace Bassia.Core.Storage;

using System.Runtime.CompilerServices;
using Bassia.Core.Model;

/// <summary>
/// A blob container on local disk, laid out like a cloud bucket keyed by hash prefix: <c>&lt;root&gt;/ab/cdef...</c>.
/// It stands in for S3 or Azure Blob Storage, so a monorepo, a server and every test run without a cloud account; the
/// server hands out signed URLs for it as a cloud store would hand out presigned ones. Writes go to a temporary file
/// first and are renamed into place, so a reader never sees a partial object.
/// </summary>
public sealed class DiskObjectStore(string root) : IObjectStore, IObjectStoreMaintenance
{
	public string Root { get; } = Path.GetFullPath(root);

	/// <summary>Builds the signed upload URL for a chunk, when a server fronts this store; null lets clients put through the store.</summary>
	public Func<ObjectId, long, UploadTicket>? UploadUrls { get; set; }

	public string PathOf(ObjectId id) => Path.Combine(Root, id.Hex[..2], id.Hex[2..]);

	public ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default) =>
		ValueTask.FromResult<IReadOnlyList<bool>>(ids.Select(id => File.Exists(PathOf(id))).ToList());

	public ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default)
	{
		var info = new FileInfo(PathOf(id));
		return ValueTask.FromResult(info.Exists ? new ObjectStat(id, info.Length) : null);
	}

	public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default)
	{
		var path = PathOf(id);
		try
		{
			if (range is not { } r)
			{
				return await File.ReadAllBytesAsync(path, ct);
			}

			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, true);
			var offset = Math.Clamp(r.Offset, 0, stream.Length);
			var length = (int)Math.Clamp(r.Length, 0, stream.Length - offset);
			var buffer = new byte[length];
			stream.Position = offset;
			await stream.ReadExactlyAsync(buffer, ct);
			return buffer;
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			return null;
		}
	}

	public async ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
	{
		var path = PathOf(id);
		if (File.Exists(path))
		{
			return;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
		await File.WriteAllBytesAsync(temp, bytes.ToArray(), ct);
		try
		{
			File.Move(temp, path, overwrite: false);
		}
		catch (IOException) when (File.Exists(path))
		{
			// Another writer stored the same object first: content addressing makes both equal.
			File.Delete(temp);
		}
	}

	public ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default) =>
		ValueTask.FromResult(UploadUrls?.Invoke(id, length) ?? new UploadTicket(id, null, DateTimeOffset.MaxValue));

	public async IAsyncEnumerable<BlobInfo> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
	{
		if (!Directory.Exists(Root))
		{
			yield break;
		}

		foreach (var dir in Directory.EnumerateDirectories(Root))
		{
			var prefix = Path.GetFileName(dir);
			if (prefix.Length != 2)
			{
				continue;
			}

			foreach (var file in Directory.EnumerateFiles(dir))
			{
				ct.ThrowIfCancellationRequested();
				if (ObjectId.TryParse(prefix + Path.GetFileName(file), out var id))
				{
					var info = new FileInfo(file);
					yield return new BlobInfo(id, info.Length, info.LastWriteTimeUtc);
				}
			}
		}

		await Task.CompletedTask;
	}

	public ValueTask DeleteAsync(ObjectId id, CancellationToken ct = default)
	{
		File.Delete(PathOf(id));
		return ValueTask.CompletedTask;
	}
}

/// <summary>
/// A client's cache in front of a remote store: a local content-addressed directory shared by every workspace on the
/// machine. Objects never change, so a cached object is valid forever; reads fill the cache, writes go through.
/// </summary>
public sealed class CachingObjectStore(IObjectStore cache, IObjectStore remote) : IObjectStore, IObjectPrefetcher, IObjectBatchWriter
{
	/// <summary>Objects read one by one (large chunks, on their direct URLs) at the same time.</summary>
	private const int ParallelReads = 8;

	public IObjectStore Remote => remote;

	/// <summary>
	/// Fills the cache with every object of <paramref name="ids"/> it lacks: small ones in batches when the remote reads
	/// many at once, the rest (large chunks) several at a time. Every object is verified against its id before it is cached.
	/// </summary>
	public async Task PrefetchAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var distinct = ids.Distinct().ToList();
		if (distinct.Count == 0)
		{
			return;
		}

		var cached = await cache.HasAsync(distinct, ct);
		var missing = distinct.Where((_, index) => !cached[index]).ToList();
		if (missing.Count == 0)
		{
			return;
		}

		var remaining = new HashSet<ObjectId>(missing);
		if (remote is IObjectBatchReader batches)
		{
			foreach (var part in missing.Chunk(500))
			{
				foreach (var (id, bytes) in await batches.ReadManyAsync(part, ct))
				{
					if (ObjectId.Of(bytes.Span) != id)
					{
						throw new VersionException($"Object {id.Short} read from the server does not match its id.");
					}

					await cache.PutAsync(id, bytes, ct);
					remaining.Remove(id);
				}
			}
		}

		await Parallel.ForEachAsync(remaining, new ParallelOptions { MaxDegreeOfParallelism = ParallelReads, CancellationToken = ct },
			async (id, token) => await ReadAsync(id, null, token));
	}

	public async Task PutManyAsync(IReadOnlyList<(ObjectId Id, ReadOnlyMemory<byte> Bytes)> objects, CancellationToken ct = default)
	{
		if (remote is IObjectBatchWriter batches)
		{
			await batches.PutManyAsync(objects, ct);
		}
		else
		{
			foreach (var (id, bytes) in objects)
			{
				await remote.PutAsync(id, bytes, ct);
			}
		}

		foreach (var (id, bytes) in objects)
		{
			await cache.PutAsync(id, bytes, ct);
		}
	}

	public async ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default) =>
		await remote.HasAsync(ids, ct);

	public async ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default) =>
		await cache.StatAsync(id, ct) ?? await remote.StatAsync(id, ct);

	public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default)
	{
		if (await cache.ReadAsync(id, range, ct) is { } cached)
		{
			return cached;
		}

		if (range is not null)
		{
			// A ranged read of an uncached chunk goes straight to the remote: hydrating a whole chunk is the caller's choice.
			return await remote.ReadAsync(id, range, ct);
		}

		if (await remote.ReadAsync(id, null, ct) is not { } bytes)
		{
			return null;
		}

		if (ObjectId.Of(bytes.Span) != id)
		{
			throw new VersionException($"Object {id.Short} read from the server does not match its id.");
		}

		await cache.PutAsync(id, bytes, ct);
		return bytes;
	}

	public async ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
	{
		await remote.PutAsync(id, bytes, ct);
		await cache.PutAsync(id, bytes, ct);
	}

	public ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default) =>
		remote.BeginUploadAsync(id, length, ct);
}

/// <summary>Counts what reaches a store: how many reads and how many bytes. Tests assert laziness with it.</summary>
public sealed class CountingObjectStore(IObjectStore inner) : IObjectStore
{
	private long reads;
	private long bytesRead;
	private long puts;

	public long Reads => Interlocked.Read(ref reads);

	public long BytesRead => Interlocked.Read(ref bytesRead);

	public long Puts => Interlocked.Read(ref puts);

	public ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default) => inner.HasAsync(ids, ct);

	public ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default) => inner.StatAsync(id, ct);

	public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default)
	{
		var result = await inner.ReadAsync(id, range, ct);
		Interlocked.Increment(ref reads);
		Interlocked.Add(ref bytesRead, result?.Length ?? 0);
		return result;
	}

	public ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
	{
		Interlocked.Increment(ref puts);
		return inner.PutAsync(id, bytes, ct);
	}

	public ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default) => inner.BeginUploadAsync(id, length, ct);
}

/// <summary>Re-hashes every whole object it reads and refuses puts whose id is not the hash of the bytes.</summary>
public sealed class VerifyingObjectStore(IObjectStore inner) : IObjectStore
{
	public ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default) => inner.HasAsync(ids, ct);

	public ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default) => inner.StatAsync(id, ct);

	public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default)
	{
		var result = await inner.ReadAsync(id, range, ct);
		if (range is null && result is { } bytes && ObjectId.Of(bytes.Span) != id)
		{
			throw new VersionException($"Object {id.Short} does not match its id: the stored bytes are corrupt.");
		}

		return result;
	}

	public ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
		ObjectId.Of(bytes.Span) == id ? inner.PutAsync(id, bytes, ct) : throw new VersionException($"Refused to store {id.Short}: the bytes hash differently.");

	public ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default) => inner.BeginUploadAsync(id, length, ct);
}
