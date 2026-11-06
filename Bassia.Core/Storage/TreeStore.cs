namespace Bassia.Core.Storage;

using System.Collections.Concurrent;
using Bassia.Core.Model;

/// <summary>
/// Tree objects (directories) as structure rather than bytes. A relational implementation answers a path lookup, a
/// subtree walk or a reachability question in one query instead of one blob read per directory. Ids are those of the
/// canonical encoding (<see cref="ObjectCodec"/>), so a tree has the same id wherever it is kept.
/// </summary>
public interface ITreeStore
{
	/// <summary>Which of the trees are stored.</summary>
	ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default);

	/// <summary>The stored trees among <paramref name="ids"/>; ids it lacks are left out.</summary>
	Task<IReadOnlyDictionary<ObjectId, Tree>> GetManyAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default);

	/// <summary>
	/// <paramref name="root"/> and the trees below it down to <paramref name="depth"/> levels (0: the root alone), at most
	/// <paramref name="limit"/> trees. Equal subtrees are returned once.
	/// </summary>
	Task<IReadOnlyDictionary<ObjectId, Tree>> GetSubtreesAsync(ObjectId root, int depth, int limit, CancellationToken ct = default);

	/// <summary>
	/// The trees along a path: <paramref name="root"/>, then the subtree named by each of <paramref name="names"/> in turn,
	/// stopping where a name is absent or not a directory. A lookup at any depth is one call.
	/// </summary>
	Task<IReadOnlyDictionary<ObjectId, Tree>> GetPathAsync(ObjectId root, IReadOnlyList<string> names, CancellationToken ct = default);

	/// <summary>Stores trees, idempotently. Each id must be the hash of the tree's canonical encoding.</summary>
	Task PutManyAsync(IReadOnlyList<(ObjectId Id, Tree Tree)> trees, CancellationToken ct = default);
}

/// <summary>A stored tree as maintenance sees it.</summary>
public sealed record TreeInfo(ObjectId Id, DateTimeOffset Written);

/// <summary>Everything reachable from some trees: the trees themselves and the entries that are not trees.</summary>
public sealed record TreeReach(HashSet<ObjectId> Trees, List<Leaf> Leaves);

/// <summary>What garbage collection, scrubbing and migration need of a tree store besides reading and writing.</summary>
public interface ITreeStoreMaintenance
{
	/// <summary>The trees reachable from <paramref name="roots"/> (roots included) and their non-directory entries, computed by the store.</summary>
	Task<TreeReach> ReachAsync(IReadOnlyList<ObjectId> roots, CancellationToken ct = default);

	IAsyncEnumerable<TreeInfo> ListAsync(CancellationToken ct = default);

	Task DeleteAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default);
}

/// <summary>
/// Trees kept as ordinary objects in an <see cref="IObjectStore"/>: the way trees were stored before tree stores, and
/// still the reference for unit tests. Walks cost one object read per tree.
/// </summary>
public sealed class ObjectBackedTreeStore(IObjectStore store) : ITreeStore
{
	public ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default) => store.HasAsync(ids, ct);

	public async Task<IReadOnlyDictionary<ObjectId, Tree>> GetManyAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var result = new Dictionary<ObjectId, Tree>();
		foreach (var id in ids.Distinct())
		{
			if (await ReadAsync(id, ct) is { } tree)
			{
				result[id] = tree;
			}
		}

		return result;
	}

	public async Task<IReadOnlyDictionary<ObjectId, Tree>> GetSubtreesAsync(ObjectId root, int depth, int limit, CancellationToken ct = default)
	{
		var result = new Dictionary<ObjectId, Tree>();
		var level = new List<ObjectId> { root };
		for (var current = 0; level.Count > 0 && current <= depth && result.Count < limit; current++)
		{
			var next = new List<ObjectId>();
			foreach (var (id, tree) in await GetManyAsync(level.Where(id => !result.ContainsKey(id)).ToList(), ct))
			{
				if (result.Count >= limit)
				{
					break;
				}

				result[id] = tree;
				next.AddRange(tree.Entries.Where(entry => entry.Kind == EntryKind.Tree).Select(entry => entry.Id));
			}

			level = next;
		}

		return result;
	}

	public async Task<IReadOnlyDictionary<ObjectId, Tree>> GetPathAsync(ObjectId root, IReadOnlyList<string> names, CancellationToken ct = default)
	{
		var result = new Dictionary<ObjectId, Tree>();
		var id = root;
		for (var index = 0; ; index++)
		{
			if (await ReadAsync(id, ct) is not { } tree)
			{
				break;
			}

			result[id] = tree;
			if (index == names.Count || tree.Find(names[index]) is not { Kind: EntryKind.Tree } entry)
			{
				break;
			}

			id = entry.Id;
		}

		return result;
	}

	public async Task PutManyAsync(IReadOnlyList<(ObjectId Id, Tree Tree)> trees, CancellationToken ct = default)
	{
		foreach (var (id, tree) in trees)
		{
			await store.PutAsync(id, ObjectCodec.Encode(tree), ct);
		}
	}

	private async Task<Tree?> ReadAsync(ObjectId id, CancellationToken ct) =>
		await store.ReadAsync(id, null, ct) is { } bytes ? ObjectCodec.Decode<Tree>(bytes) : null;
}

/// <summary>Trees in memory, with the reachability a relational store offers: the reference for tree-store contract tests.</summary>
public sealed class MemoryTreeStore : ITreeStore, ITreeStoreMaintenance
{
	private readonly ConcurrentDictionary<ObjectId, (Tree Tree, DateTimeOffset Written)> trees = new();

	public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

	public int Count => trees.Count;

	public ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default) =>
		ValueTask.FromResult<IReadOnlyList<bool>>(ids.Select(trees.ContainsKey).ToList());

	public Task<IReadOnlyDictionary<ObjectId, Tree>> GetManyAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default) =>
		Task.FromResult<IReadOnlyDictionary<ObjectId, Tree>>(ids.Distinct().Where(trees.ContainsKey).ToDictionary(id => id, id => trees[id].Tree));

	public Task<IReadOnlyDictionary<ObjectId, Tree>> GetSubtreesAsync(ObjectId root, int depth, int limit, CancellationToken ct = default)
	{
		var result = new Dictionary<ObjectId, Tree>();
		var level = new List<ObjectId> { root };
		for (var current = 0; level.Count > 0 && current <= depth; current++)
		{
			var next = new List<ObjectId>();
			foreach (var id in level.Distinct())
			{
				if (result.Count < limit && !result.ContainsKey(id) && trees.TryGetValue(id, out var entry))
				{
					result[id] = entry.Tree;
					next.AddRange(entry.Tree.Entries.Where(e => e.Kind == EntryKind.Tree).Select(e => e.Id));
				}
			}

			level = next;
		}

		return Task.FromResult<IReadOnlyDictionary<ObjectId, Tree>>(result);
	}

	public Task<IReadOnlyDictionary<ObjectId, Tree>> GetPathAsync(ObjectId root, IReadOnlyList<string> names, CancellationToken ct = default)
	{
		var result = new Dictionary<ObjectId, Tree>();
		var id = root;
		for (var index = 0; trees.TryGetValue(id, out var current); index++)
		{
			result[id] = current.Tree;
			if (index == names.Count || current.Tree.Find(names[index]) is not { Kind: EntryKind.Tree } entry)
			{
				break;
			}

			id = entry.Id;
		}

		return Task.FromResult<IReadOnlyDictionary<ObjectId, Tree>>(result);
	}

	public Task PutManyAsync(IReadOnlyList<(ObjectId Id, Tree Tree)> items, CancellationToken ct = default)
	{
		foreach (var (id, tree) in items)
		{
			trees.TryAdd(id, (tree, Clock()));
		}

		return Task.CompletedTask;
	}

	public Task<TreeReach> ReachAsync(IReadOnlyList<ObjectId> roots, CancellationToken ct = default)
	{
		var reach = new TreeReach([], []);
		var pending = new Stack<ObjectId>(roots);
		var leaves = new HashSet<Leaf>();
		while (pending.TryPop(out var id))
		{
			if (!trees.TryGetValue(id, out var entry) || !reach.Trees.Add(id))
			{
				continue;
			}

			foreach (var e in entry.Tree.Entries)
			{
				if (e.Kind == EntryKind.Tree)
				{
					pending.Push(e.Id);
				}
				else if (leaves.Add(e.Leaf))
				{
					reach.Leaves.Add(e.Leaf);
				}
			}
		}

		return Task.FromResult(reach);
	}

	public async IAsyncEnumerable<TreeInfo> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
	{
		foreach (var (id, entry) in trees)
		{
			yield return new TreeInfo(id, entry.Written);
		}

		await Task.CompletedTask;
	}

	public Task DeleteAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		foreach (var id in ids)
		{
			trees.TryRemove(id, out _);
		}

		return Task.CompletedTask;
	}

	/// <summary>Every tree held, for tests that compare two stores.</summary>
	public IReadOnlyDictionary<ObjectId, Tree> Snapshot() => trees.ToDictionary(pair => pair.Key, pair => pair.Value.Tree);
}

/// <summary>
/// A clone's trees: a local tree store in front of the server's. Trees never change, so whatever a call brings back is
/// kept and answers every later read; the clone holds only the subtrees its work touched, not the whole monorepo.
/// </summary>
public sealed class CachingTreeStore(ITreeStore local, ITreeStore remote) : ITreeStore
{
	public async ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var have = (await local.HasAsync(ids, ct)).ToList();
		var missing = ids.Where((_, index) => !have[index]).ToList();
		if (missing.Count > 0)
		{
			var remoteHave = await remote.HasAsync(missing, ct);
			var next = 0;
			for (var index = 0; index < have.Count; index++)
			{
				if (!have[index])
				{
					have[index] = remoteHave[next++];
				}
			}
		}

		return have;
	}

	public async Task<IReadOnlyDictionary<ObjectId, Tree>> GetManyAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var result = new Dictionary<ObjectId, Tree>(await local.GetManyAsync(ids, ct));
		var missing = ids.Distinct().Where(id => !result.ContainsKey(id)).ToList();
		if (missing.Count > 0)
		{
			var fetched = await remote.GetManyAsync(missing, ct);
			await local.PutManyAsync(fetched.Select(pair => (pair.Key, pair.Value)).ToList(), ct);
			foreach (var (id, tree) in fetched)
			{
				result[id] = tree;
			}
		}

		return result;
	}

	public async Task<IReadOnlyDictionary<ObjectId, Tree>> GetSubtreesAsync(ObjectId root, int depth, int limit, CancellationToken ct = default)
	{
		// A subtree is complete locally only if every tree below the root is; asking the server once is cheaper than checking.
		var fetched = await remote.GetSubtreesAsync(root, depth, limit, ct);
		await local.PutManyAsync(fetched.Select(pair => (pair.Key, pair.Value)).ToList(), ct);
		return fetched;
	}

	public async Task<IReadOnlyDictionary<ObjectId, Tree>> GetPathAsync(ObjectId root, IReadOnlyList<string> names, CancellationToken ct = default)
	{
		var here = await local.GetPathAsync(root, names, ct);
		if (here.Count == names.Count + 1)
		{
			return here;
		}

		var fetched = await remote.GetPathAsync(root, names, ct);
		await local.PutManyAsync(fetched.Select(pair => (pair.Key, pair.Value)).ToList(), ct);
		return fetched;
	}

	public async Task PutManyAsync(IReadOnlyList<(ObjectId Id, Tree Tree)> trees, CancellationToken ct = default)
	{
		await remote.PutManyAsync(trees, ct);
		await local.PutManyAsync(trees, ct);
	}
}
