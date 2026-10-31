namespace Bassia.Core.Storage;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Bassia.Core.Model;

/// <summary>Objects in memory: the reference implementation the unit tests run against.</summary>
public sealed class MemoryObjectStore : IObjectStore, IObjectStoreMaintenance
{
	private readonly ConcurrentDictionary<ObjectId, (byte[] Bytes, DateTimeOffset Written)> objects = new();

	public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

	public int Count => objects.Count;

	public ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default) =>
		ValueTask.FromResult<IReadOnlyList<bool>>(ids.Select(objects.ContainsKey).ToList());

	public ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default) =>
		ValueTask.FromResult(objects.TryGetValue(id, out var entry) ? new ObjectStat(id, entry.Bytes.Length) : null);

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default)
	{
		if (!objects.TryGetValue(id, out var entry))
		{
			return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
		}

		return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Slice(entry.Bytes, range));
	}

	public ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
	{
		objects.TryAdd(id, (bytes.ToArray(), Clock()));
		return ValueTask.CompletedTask;
	}

	public ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default) =>
		ValueTask.FromResult(new UploadTicket(id, null, DateTimeOffset.MaxValue));

	public async IAsyncEnumerable<BlobInfo> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
	{
		foreach (var (id, entry) in objects)
		{
			yield return new BlobInfo(id, entry.Bytes.Length, entry.Written);
		}

		await Task.CompletedTask;
	}

	public ValueTask DeleteAsync(ObjectId id, CancellationToken ct = default)
	{
		objects.TryRemove(id, out _);
		return ValueTask.CompletedTask;
	}

	/// <summary>Replaces an object's bytes without checking them: lets tests corrupt a blob for the scrubber to find.</summary>
	public void Corrupt(ObjectId id, byte[] bytes) => objects[id] = (bytes, Clock());

	internal static ReadOnlyMemory<byte> Slice(byte[] bytes, ByteRange? range)
	{
		if (range is not { } r)
		{
			return bytes;
		}

		var offset = (int)Math.Clamp(r.Offset, 0, bytes.Length);
		var length = (int)Math.Clamp(r.Length, 0, bytes.Length - offset);
		return bytes.AsMemory(offset, length);
	}
}

/// <summary>Heads in memory, each swapped under a lock.</summary>
public sealed class MemoryViewStore : IViewStore
{
	private readonly Dictionary<string, ObjectId> heads = new(StringComparer.Ordinal);
	private readonly Lock gate = new();

	public ValueTask<ObjectId?> GetHeadAsync(HeadName name, CancellationToken ct = default)
	{
		lock (gate)
		{
			return ValueTask.FromResult<ObjectId?>(heads.TryGetValue(name.Value, out var head) ? head : null);
		}
	}

	public ValueTask<HeadUpdate> AdvanceHeadAsync(HeadName name, ObjectId? expected, ObjectId next, CancellationToken ct = default)
	{
		lock (gate)
		{
			ObjectId? current = heads.TryGetValue(name.Value, out var head) ? head : null;
			if (current != expected)
			{
				return ValueTask.FromResult(new HeadUpdate(false, current));
			}

			heads[name.Value] = next;
			return ValueTask.FromResult(new HeadUpdate(true, next));
		}
	}

	public async IAsyncEnumerable<(HeadName Name, ObjectId Head)> ListHeadsAsync(string prefix = "", [EnumeratorCancellation] CancellationToken ct = default)
	{
		List<(HeadName, ObjectId)> list;
		lock (gate)
		{
			list = heads.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
				.OrderBy(pair => pair.Key, StringComparer.Ordinal)
				.Select(pair => (new HeadName(pair.Key), pair.Value)).ToList();
		}

		foreach (var item in list)
		{
			yield return item;
		}

		await Task.CompletedTask;
	}

	public ValueTask<bool> DeleteHeadAsync(HeadName name, ObjectId expected, CancellationToken ct = default)
	{
		lock (gate)
		{
			if (heads.TryGetValue(name.Value, out var head) && head == expected)
			{
				heads.Remove(name.Value);
				return ValueTask.FromResult(true);
			}

			return ValueTask.FromResult(false);
		}
	}
}

/// <summary>The path index in memory: per path its versions in seq order.</summary>
public sealed class MemoryPathIndex : IPathIndex
{
	private readonly SortedDictionary<string, List<PathVersion>> paths = new(StringComparer.Ordinal);
	private readonly Lock gate = new();
	private long indexed;

	public ValueTask<PathVersion?> GetAsync(string path, long seq, CancellationToken ct = default)
	{
		lock (gate)
		{
			return ValueTask.FromResult(Latest(path, seq));
		}
	}

	private PathVersion? Latest(string path, long seq) =>
		paths.TryGetValue(path, out var versions) ? versions.LastOrDefault(version => version.Seq <= seq) : null;

	public async IAsyncEnumerable<PathListing> ListAsync(string dir, long seq, [EnumeratorCancellation] CancellationToken ct = default)
	{
		List<PathListing> result;
		lock (gate)
		{
			result = PathIndexListing.Children(dir, paths.Keys.Select(path => Latest(path, seq)).OfType<PathVersion>()).ToList();
		}

		foreach (var item in result)
		{
			yield return item;
		}

		await Task.CompletedTask;
	}

	public async IAsyncEnumerable<PathVersion> ChangedSinceAsync(string prefix, long afterSeq, [EnumeratorCancellation] CancellationToken ct = default)
	{
		List<PathVersion> result;
		lock (gate)
		{
			result = paths.Where(pair => PathNames.IsUnder(pair.Key, prefix))
				.Select(pair => pair.Value[^1])
				.Where(version => version.Seq > afterSeq)
				.ToList();
		}

		foreach (var item in result)
		{
			yield return item;
		}

		await Task.CompletedTask;
	}

	public ValueTask ApplyAsync(long seq, IReadOnlyList<PathChange> changes, CancellationToken ct = default)
	{
		lock (gate)
		{
			if (seq <= indexed)
			{
				return ValueTask.CompletedTask;
			}

			foreach (var change in changes)
			{
				if (!paths.TryGetValue(change.Path, out var versions))
				{
					paths[change.Path] = versions = [];
				}

				versions.Add(new PathVersion(change.Path, seq, change.Leaf));
			}

			indexed = seq;
		}

		return ValueTask.CompletedTask;
	}

	public ValueTask<long> IndexedSeqAsync(CancellationToken ct = default)
	{
		lock (gate)
		{
			return ValueTask.FromResult(indexed);
		}
	}
}

/// <summary>How a listing is derived from the latest version of every path.</summary>
public static class PathIndexListing
{
	public static IEnumerable<PathListing> Children(string dir, IEnumerable<PathVersion> latest)
	{
		dir = dir.Trim('/');
		var directories = new SortedSet<string>(StringComparer.Ordinal);
		var files = new List<PathListing>();
		foreach (var version in latest)
		{
			if (version.Leaf is null || !PathNames.IsUnder(version.Path, dir) || version.Path == dir)
			{
				continue;
			}

			var rest = dir.Length == 0 ? version.Path : version.Path[(dir.Length + 1)..];
			var slash = rest.IndexOf('/');
			if (slash < 0)
			{
				files.Add(new PathListing(version.Path, false, version));
			}
			else
			{
				directories.Add(PathNames.Join(dir, rest[..slash]));
			}
		}

		return directories.Select(path => new PathListing(path, true, null))
			.Concat(files.OrderBy(file => file.Path, StringComparer.Ordinal));
	}
}

/// <summary>Leases in memory, with a FIFO queue of waiters.</summary>
public sealed class MemoryLeaseStore : ILeaseStore
{
	private readonly List<Lease> leases = [];
	private readonly LinkedList<LeaseRequest> queue = new();
	private readonly Lock gate = new();
	private long token;

	public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

	public async ValueTask<LeaseResult> AcquireAsync(LeaseRequest request, CancellationToken ct = default)
	{
		var deadline = Clock() + request.Wait;
		LinkedListNode<LeaseRequest>? ticket = null;
		try
		{
			while (true)
			{
				lock (gate)
				{
					var now = Clock();
					leases.RemoveAll(lease => lease.Expires <= now);
					var blocking = LeaseRules.Blocking(request, leases);
					var ahead = ticket is null
						? queue.Where(waiting => LeaseRules.Conflicts(request, waiting)).ToList()
						: QueuedBefore(ticket).Where(waiting => LeaseRules.Conflicts(request, waiting)).ToList();
					if (blocking.Count == 0 && ahead.Count == 0)
					{
						var lease = new Lease(Guid.NewGuid().ToString("N"), PathNames.Prefix(request.Prefix), request.Mode, request.Owner, request.Run,
							++token, now, now + request.Ttl);
						leases.Add(lease);
						return new LeaseResult(lease, []);
					}

					if (now >= deadline)
					{
						return new LeaseResult(null, blocking.Count > 0 ? blocking : []);
					}

					ticket ??= queue.AddLast(request);
				}

				await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
			}
		}
		finally
		{
			if (ticket is not null)
			{
				lock (gate)
				{
					queue.Remove(ticket);
				}
			}
		}
	}

	private IEnumerable<LeaseRequest> QueuedBefore(LinkedListNode<LeaseRequest> ticket)
	{
		for (var node = queue.First; node is not null && node != ticket; node = node.Next)
		{
			yield return node.Value;
		}
	}

	public ValueTask<Lease?> RenewAsync(string id, long token, TimeSpan ttl, CancellationToken ct = default)
	{
		lock (gate)
		{
			var now = Clock();
			var index = leases.FindIndex(lease => lease.Id == id && lease.Token == token && lease.Expires > now);
			if (index < 0)
			{
				return ValueTask.FromResult<Lease?>(null);
			}

			leases[index] = leases[index] with { Expires = now + ttl };
			return ValueTask.FromResult<Lease?>(leases[index]);
		}
	}

	public ValueTask ReleaseAsync(string id, long token, CancellationToken ct = default)
	{
		lock (gate)
		{
			leases.RemoveAll(lease => lease.Id == id && lease.Token == token);
		}

		return ValueTask.CompletedTask;
	}

	public ValueTask<IReadOnlyList<Lease>> ListAsync(string? prefix = null, CancellationToken ct = default)
	{
		lock (gate)
		{
			var now = Clock();
			return ValueTask.FromResult<IReadOnlyList<Lease>>(leases
				.Where(lease => lease.Expires > now && (prefix is null || PathNames.Overlap(lease.Prefix.TrimEnd('/'), prefix.TrimEnd('/'))))
				.OrderBy(lease => lease.Prefix, StringComparer.Ordinal).ToList());
		}
	}

	public ValueTask<Lease?> GetAsync(string id, CancellationToken ct = default)
	{
		lock (gate)
		{
			var now = Clock();
			return ValueTask.FromResult(leases.FirstOrDefault(lease => lease.Id == id && lease.Expires > now));
		}
	}
}

/// <summary>
/// Hierarchical lease compatibility. An exclusive lease on <c>src/Billing/</c> holds X on that prefix and IX
/// (intent) on <c>src/</c> and the root; an intent lease holds IX on its prefix and its ancestors. X conflicts with any
/// mode on the same node, IX with IX does not. Leases of the same owner never conflict.
/// </summary>
public static class LeaseRules
{
	private static IEnumerable<(string Node, bool Exclusive)> Nodes(string prefix, LeaseMode mode)
	{
		prefix = PathNames.Prefix(prefix);
		foreach (var ancestor in PathNames.Ancestors(prefix))
		{
			if (ancestor != prefix)
			{
				yield return (ancestor, false);
			}
		}

		yield return (prefix, mode == LeaseMode.Exclusive);
	}

	public static bool Conflicts(string prefixA, LeaseMode modeA, string ownerA, string prefixB, LeaseMode modeB, string ownerB)
	{
		if (ownerA == ownerB)
		{
			return false;
		}

		var a = PathNames.Prefix(prefixA);
		var b = PathNames.Prefix(prefixB);

		// An exclusive lease also covers everything below its prefix.
		if (modeA == LeaseMode.Exclusive && b.StartsWith(a, StringComparison.Ordinal) && a != b)
		{
			return true;
		}

		if (modeB == LeaseMode.Exclusive && a.StartsWith(b, StringComparison.Ordinal) && a != b)
		{
			return true;
		}

		var nodesB = Nodes(b, modeB).ToList();
		foreach (var (node, exclusive) in Nodes(a, modeA))
		{
			foreach (var (other, otherExclusive) in nodesB)
			{
				if (node == other && (exclusive || otherExclusive))
				{
					return true;
				}
			}
		}

		return false;
	}

	public static bool Conflicts(LeaseRequest request, LeaseRequest other) =>
		Conflicts(request.Prefix, request.Mode, request.Owner, other.Prefix, other.Mode, other.Owner);

	public static IReadOnlyList<Lease> Blocking(LeaseRequest request, IEnumerable<Lease> held) =>
		held.Where(lease => Conflicts(request.Prefix, request.Mode, request.Owner, lease.Prefix, lease.Mode, lease.Owner)).ToList();

	/// <summary>Whether an exclusive lease on <paramref name="prefix"/> covers <paramref name="path"/>.</summary>
	public static bool Covers(string prefix, string path) => PathNames.IsUnder(path, prefix.TrimEnd('/'));
}
