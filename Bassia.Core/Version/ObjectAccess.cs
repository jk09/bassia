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

	public async Task<ObjectId> WriteAsync(StoredObject value, CancellationToken ct = default)
	{
		var bytes = ObjectCodec.Encode(value);
		var id = ObjectId.Of(bytes);
		await store.PutAsync(id, bytes, ct);
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

		var distinct = chunks.DistinctBy(chunk => chunk.Id).ToList();
		var have = await store.HasAsync(distinct.Select(chunk => chunk.Id).ToList(), ct);
		for (var index = 0; index < distinct.Count; index++)
		{
			if (!have[index])
			{
				await store.PutAsync(distinct[index].Id, distinct[index].Bytes, ct);
			}
		}
	}
}
