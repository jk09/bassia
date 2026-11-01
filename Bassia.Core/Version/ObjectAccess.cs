namespace Bassia.Core.Version;

using System.Collections.Concurrent;
using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>
/// Typed access to an <see cref="IObjectStore"/>: decoded structural objects (cached, since they never change), file
/// content assembled from chunks, and writing files and objects with <c>Have</c> first so only missing chunks move.
/// </summary>
public sealed class ObjectAccess(IObjectStore store, ChunkParameters? chunks = null)
{
	private const int CacheLimit = 50_000;
	private const int UploadBatch = 16;

	private readonly ConcurrentDictionary<ObjectId, StoredObject> cache = new();

	/// <summary>Forgets the decoded objects held in memory (the store, and a clone's disk cache, keep them).</summary>
	public void ClearCache() => cache.Clear();

	public IObjectStore Store => store;

	public Chunker Chunker { get; } = new(chunks);

	public async Task<T> ReadAsync<T>(ObjectId id, CancellationToken ct = default) where T : StoredObject
	{
		if (cache.TryGetValue(id, out var cached))
		{
			return cached as T ?? throw new VersionException($"Object {id.Short} is a {cached.GetType().Name}, not a {typeof(T).Name}.");
		}

		var bytes = await store.ReadAsync(id, null, ct) ?? throw new MissingObjectException(id);
		StoredObject value;
		try
		{
			value = ObjectCodec.Decode(bytes);
		}
		catch (InvalidDataException ex)
		{
			throw new VersionException($"Object {id.Short} is malformed: {ex.Message}", ex);
		}

		if (cache.Count > CacheLimit)
		{
			cache.Clear();
		}

		cache[id] = value;
		return value as T ?? throw new VersionException($"Object {id.Short} is a {value.GetType().Name}, not a {typeof(T).Name}.");
	}

	public Task<Tree> ReadTreeAsync(ObjectId id, CancellationToken ct = default) => ReadAsync<Tree>(id, ct);

	public Task<Commit> ReadCommitAsync(ObjectId id, CancellationToken ct = default) => ReadAsync<Commit>(id, ct);

	public Task<Operation> ReadOperationAsync(ObjectId id, CancellationToken ct = default) => ReadAsync<Operation>(id, ct);

	public Task<View> ReadViewAsync(ObjectId id, CancellationToken ct = default) => ReadAsync<View>(id, ct);

	/// <summary>The objects written inside <see cref="BatchAsync{T}"/>, sent together by <see cref="FlushAsync"/>.</summary>
	private readonly AsyncLocal<List<(ObjectId Id, byte[] Bytes)>?> deferred = new();

	/// <summary>
	/// Runs <paramref name="work"/> with its writes held back and sends them together at the end (or at the first
	/// <see cref="FlushAsync"/>): one <c>Have</c> and one batched put for a remote store, instead of a round trip per object.
	/// </summary>
	public async Task<T> BatchAsync<T>(Func<Task<T>> work)
	{
		if (deferred.Value is not null)
		{
			return await work();
		}

		deferred.Value = [];
		try
		{
			var result = await work();
			await FlushAsync();
			return result;
		}
		finally
		{
			deferred.Value = null;
		}
	}

	/// <summary>Sends the writes held back so far; a head may only point at objects that are stored.</summary>
	public async Task FlushAsync(CancellationToken ct = default)
	{
		if (deferred.Value is not { Count: > 0 } pending)
		{
			return;
		}

		var items = pending.DistinctBy(item => item.Id).ToList();
		pending.Clear();
		await StoreMissingAsync(items, ct);
	}

	/// <summary>Objects at least this large go one by one (on direct URLs, for a remote store); smaller ones travel in batches.</summary>
	public const int BatchLimit = 256 * 1024;

	/// <summary>Fetches objects ahead of reads, in batches, when the store keeps a local cache; otherwise does nothing.</summary>
	public async Task PrefetchAsync(IEnumerable<ObjectId> ids, CancellationToken ct = default)
	{
		if (store is IObjectPrefetcher prefetcher)
		{
			var missing = ids.Where(id => !cache.ContainsKey(id)).Distinct().ToList();
			if (missing.Count > 0)
			{
				await prefetcher.PrefetchAsync(missing, ct);
			}
		}
	}

	/// <summary>
	/// Fetches the content of file leaves ahead of a checkout: their file objects first (one batch), then all their
	/// chunks (small ones in batches, large ones several at a time).
	/// </summary>
	public async Task PrefetchContentAsync(IEnumerable<Leaf> leaves, CancellationToken ct = default)
	{
		if (store is not IObjectPrefetcher)
		{
			return;
		}

		var list = leaves.Where(leaf => leaf.IsFile || leaf.Kind == EntryKind.Symlink).ToList();
		await PrefetchAsync(list.Where(leaf => leaf.Kind == EntryKind.Chunked).Select(leaf => leaf.Id), ct);
		var chunks = new List<ObjectId>();
		foreach (var leaf in list)
		{
			chunks.AddRange((await ChunksAsync(leaf, ct)).Select(chunk => chunk.Id));
		}

		await PrefetchAsync(chunks, ct);
	}

	/// <summary>Stores several structural objects in one round trip where the store can; returns their ids in order.</summary>
	public async Task<IReadOnlyList<ObjectId>> WriteManyAsync(IReadOnlyList<StoredObject> values, CancellationToken ct = default)
	{
		var encoded = values.Select(value => (Value: value, Bytes: ObjectCodec.Encode(value))).Select(item => (item.Value, Id: ObjectId.Of(item.Bytes), item.Bytes)).ToList();
		var fresh = encoded.Where(item => !cache.ContainsKey(item.Id)).DistinctBy(item => item.Id).ToList();
		if (deferred.Value is { } pending)
		{
			pending.AddRange(fresh.Select(item => (item.Id, item.Bytes)));
		}
		else if (store is IObjectBatchWriter batches && fresh.Count > 1)
		{
			await batches.PutManyAsync(fresh.Select(item => (item.Id, (ReadOnlyMemory<byte>)item.Bytes)).ToList(), ct);
		}
		else
		{
			foreach (var item in fresh)
			{
				await store.PutAsync(item.Id, item.Bytes, ct);
			}
		}

		foreach (var item in encoded)
		{
			cache.TryAdd(item.Id, item.Value);
		}

		return encoded.Select(item => item.Id).ToList();
	}

	public async Task<ObjectId> WriteAsync(StoredObject value, CancellationToken ct = default)
	{
		var bytes = ObjectCodec.Encode(value);
		var id = ObjectId.Of(bytes);
		if (deferred.Value is { } pending)
		{
			if (!cache.ContainsKey(id))
			{
				pending.Add((id, bytes));
			}
		}
		else
		{
			await store.PutAsync(id, bytes, ct);
		}

		cache.TryAdd(id, value);
		return id;
	}

	/// <summary>The chunk list of a file leaf (one chunk for a small file or a symlink).</summary>
	public async Task<IReadOnlyList<ChunkRef>> ChunksAsync(Leaf leaf, CancellationToken ct = default) => leaf.Kind switch
	{
		EntryKind.File or EntryKind.Symlink => [new ChunkRef(leaf.Id, leaf.Size)],
		EntryKind.Chunked => (await ReadAsync<FileObject>(leaf.Id, ct)).Chunks,
		_ => throw new VersionException($"A {leaf.Kind} entry has no content.")
	};

	/// <summary>The whole content of a file or symlink leaf.</summary>
	public async Task<byte[]> ReadContentAsync(Leaf leaf, CancellationToken ct = default)
	{
		var parts = await ChunksAsync(leaf, ct);
		var buffer = new byte[parts.Sum(part => part.Length)];
		var offset = 0;
		foreach (var part in parts)
		{
			var bytes = await store.ReadAsync(part.Id, null, ct) ?? throw new MissingObjectException(part.Id);
			bytes.Span.CopyTo(buffer.AsSpan(offset));
			offset += bytes.Length;
		}

		return buffer;
	}

	/// <summary>Writes a file's content to <paramref name="destination"/>, chunk by chunk.</summary>
	public async Task CopyContentAsync(Leaf leaf, Stream destination, CancellationToken ct = default)
	{
		foreach (var part in await ChunksAsync(leaf, ct))
		{
			var bytes = await store.ReadAsync(part.Id, null, ct) ?? throw new MissingObjectException(part.Id);
			await destination.WriteAsync(bytes, ct);
		}
	}

	/// <summary>A byte range of a file, reading only the chunks it overlaps.</summary>
	public async Task<byte[]> ReadRangeAsync(Leaf leaf, long offset, int length, CancellationToken ct = default)
	{
		var result = new MemoryStream();
		long position = 0;
		foreach (var part in await ChunksAsync(leaf, ct))
		{
			var end = position + part.Length;
			if (end > offset && position < offset + length)
			{
				var from = Math.Max(offset, position) - position;
				var to = Math.Min(offset + length, end) - position;
				var bytes = await store.ReadAsync(part.Id, new ByteRange(from, to - from), ct) ?? throw new MissingObjectException(part.Id);
				await result.WriteAsync(bytes, ct);
			}

			position = end;
		}

		return result.ToArray();
	}

	/// <summary>The id a file's content would get, without storing anything.</summary>
	public Leaf Identify(ReadOnlySpan<byte> content, bool executable = false)
	{
		var parts = new List<ChunkRef>();
		var offset = 0;
		while (offset < content.Length)
		{
			var length = Chunker.Cut(content[offset..]);
			parts.Add(new ChunkRef(ObjectId.Of(content.Slice(offset, length)), length));
			offset += length;
		}

		if (parts.Count <= 1)
		{
			return new Leaf(EntryKind.File, parts.Count == 0 ? ObjectId.Of([]) : parts[0].Id, content.Length, executable);
		}

		var file = new FileObject(content.Length, parts);
		return new Leaf(EntryKind.Chunked, ObjectId.Of(ObjectCodec.Encode(file)), content.Length, executable);
	}

	/// <summary>Stores a file's content: chunks it, asks which chunks the store has, and uploads only the others.</summary>
	public async Task<Leaf> WriteContentAsync(ReadOnlyMemory<byte> content, bool executable = false, CancellationToken ct = default)
	{
		await using var stream = new MemoryStream(content.ToArray(), false);
		return await WriteContentAsync(stream, executable, ct);
	}

	public async Task<Leaf> WriteContentAsync(Stream content, bool executable = false, CancellationToken ct = default)
	{
		var parts = new List<ChunkRef>();
		var pending = new List<(ObjectId Id, byte[] Bytes)>();
		long size = 0;
		await Chunker.SplitAsync(content, async chunk =>
		{
			var id = ObjectId.Of(chunk.Span);
			parts.Add(new ChunkRef(id, chunk.Length));
			size += chunk.Length;
			pending.Add((id, chunk.ToArray()));
			if (pending.Count >= UploadBatch)
			{
				await UploadMissingAsync(pending, ct);
				pending.Clear();
			}
		}, ct);

		if (parts.Count == 0)
		{
			var empty = ObjectId.Of([]);
			pending.Add((empty, []));
			parts.Add(new ChunkRef(empty, 0));
		}

		await UploadMissingAsync(pending, ct);
		if (parts.Count == 1)
		{
			return new Leaf(EntryKind.File, parts[0].Id, size, executable);
		}

		var id = await WriteAsync(new FileObject(size, parts), ct);
		return new Leaf(EntryKind.Chunked, id, size, executable);
	}

	/// <summary>Stores a symlink's target.</summary>
	public async Task<Leaf> WriteSymlinkAsync(string target, CancellationToken ct = default)
	{
		var bytes = System.Text.Encoding.UTF8.GetBytes(target);
		var id = ObjectId.Of(bytes);
		await UploadMissingAsync([(id, bytes)], ct);
		return new Leaf(EntryKind.Symlink, id, bytes.Length);
	}

	private async Task UploadMissingAsync(IReadOnlyList<(ObjectId Id, byte[] Bytes)> chunks, CancellationToken ct)
	{
		if (chunks.Count == 0)
		{
			return;
		}

		if (deferred.Value is { } pending)
		{
			pending.AddRange(chunks);
			return;
		}

		await StoreMissingAsync(chunks.DistinctBy(chunk => chunk.Id).ToList(), ct);
	}

	/// <summary>Asks which objects the store lacks, then sends those: small ones in batches, large ones several at a time.</summary>
	private async Task StoreMissingAsync(IReadOnlyList<(ObjectId Id, byte[] Bytes)> items, CancellationToken ct)
	{
		var missing = new List<(ObjectId Id, byte[] Bytes)>();
		foreach (var part in items.Chunk(1000))
		{
			var have = await store.HasAsync(part.Select(item => item.Id).ToList(), ct);
			missing.AddRange(part.Where((_, index) => !have[index]));
		}

		var small = missing.Where(item => item.Bytes.Length < BatchLimit).ToList();
		if (store is IObjectBatchWriter batches && small.Count > 1)
		{
			await batches.PutManyAsync(small.Select(item => (item.Id, (ReadOnlyMemory<byte>)item.Bytes)).ToList(), ct);
			missing = missing.Except(small).ToList();
		}

		await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
			async (item, token) => await store.PutAsync(item.Id, item.Bytes, token));
	}
}
