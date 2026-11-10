namespace Bassia.Core.Version;

using System.Collections.Concurrent;
using Bassia.Core.Model;

/// <summary>
/// Decoded objects in memory, bounded by their approximate size: when the bound is passed, the objects used least
/// recently go, until a fifth of it is free again. Objects never change, so an entry is never wrong - only absent.
/// </summary>
public sealed class ObjectCache(long limitBytes)
{
	private sealed class Entry(StoredObject value, long size)
	{
		public StoredObject Value => value;

		public long Size => size;

		public long Used;
	}

	private readonly ConcurrentDictionary<ObjectId, Entry> entries = new();
	private readonly Lock evicting = new();
	private long bytes;
	private long clock;

	/// <summary>The bound, in approximate bytes of decoded objects.</summary>
	public long LimitBytes { get; set; } = limitBytes;

	/// <summary>The approximate bytes held.</summary>
	public long Bytes => Interlocked.Read(ref bytes);

	public int Count => entries.Count;

	public bool Contains(ObjectId id) => entries.ContainsKey(id);

	public bool TryGet(ObjectId id, out StoredObject value)
	{
		if (entries.TryGetValue(id, out var entry))
		{
			Volatile.Write(ref entry.Used, Interlocked.Increment(ref clock));
			value = entry.Value;
			return true;
		}

		value = null!;
		return false;
	}

	/// <summary>Keeps an object; one already held stays as it is.</summary>
	public void Add(ObjectId id, StoredObject value)
	{
		var entry = new Entry(value, SizeOf(value)) { Used = Interlocked.Increment(ref clock) };
		if (!entries.TryAdd(id, entry))
		{
			return;
		}

		if (Interlocked.Add(ref bytes, entry.Size) > LimitBytes)
		{
			Evict();
		}
	}

	public void Clear()
	{
		lock (evicting)
		{
			entries.Clear();
			Interlocked.Exchange(ref bytes, 0);
		}
	}

	private void Evict()
	{
		lock (evicting)
		{
			if (Bytes <= LimitBytes)
			{
				return; // another thread made room
			}

			var target = LimitBytes - LimitBytes / 5;
			foreach (var (id, entry) in entries.OrderBy(pair => Volatile.Read(ref pair.Value.Used)).ToList())
			{
				if (Bytes <= target)
				{
					break;
				}

				if (entries.TryRemove(id, out var removed))
				{
					Interlocked.Add(ref bytes, -removed.Size);
				}
			}
		}
	}

	/// <summary>What an object costs in memory, roughly: enough to bound the cache, not to account for it.</summary>
	public static long SizeOf(StoredObject value) => value switch
	{
		Tree tree => 96 + tree.Entries.Sum(entry => 120L + 2 * entry.Name.Length),
		FileObject file => 96 + 56L * file.Chunks.Count,
		Candidate candidate => 512 + candidate.Touched.Sum(path => 40L + 2 * path.Length) + candidate.Changes.Sum(change => 160L + 2 * change.Path.Length)
			+ candidate.Conflicts.Sum(conflict => 200L + 2 * (conflict.Path.Length + conflict.Detail.Length)) + candidate.Merged.Sum(path => 40L + 2 * path.Length),
		Commit commit => 512 + 2L * commit.Intent.Length,
		_ => 768
	};
}
