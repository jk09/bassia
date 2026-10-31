namespace Bassia.Core.Version;

using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>A path that a change cannot be applied to: it would be a file and a directory at once.</summary>
public sealed class PathClashException(IReadOnlyList<string> paths)
	: VersionException($"These paths cannot be both a file and a directory: {string.Join(", ", paths)}.")
{
	public IReadOnlyList<string> Paths { get; } = paths;
}

/// <summary>One leaf-level difference between two trees: a path's old and new content (null = absent).</summary>
public sealed record TreeChange(string Path, Leaf? Old, Leaf? New);

/// <summary>Reading, editing and comparing trees. Every walk skips subtrees whose ids are equal.</summary>
public sealed class Trees(ObjectAccess objects)
{
	public ObjectAccess Objects => objects;

	public async Task<ObjectId> EmptyTreeAsync(CancellationToken ct = default) => await objects.WriteAsync(Tree.Empty, ct);

	/// <summary>The leaf at <paramref name="path"/> (a file, symlink, conflict or a directory), or null.</summary>
	public async Task<Leaf?> GetAsync(ObjectId root, string path, CancellationToken ct = default)
	{
		path = PathNames.Normalize(path);
		if (path.Length == 0)
		{
			return new Leaf(EntryKind.Tree, root, (await objects.ReadTreeAsync(root, ct)).TotalSize);
		}

		var tree = await objects.ReadTreeAsync(root, ct);
		var parts = PathNames.Split(path);
		for (var index = 0; index < parts.Length; index++)
		{
			var entry = tree.Find(parts[index]);
			if (entry is null)
			{
				return null;
			}

			if (index == parts.Length - 1)
			{
				return entry.Leaf;
			}

			if (entry.Kind != EntryKind.Tree)
			{
				return null;
			}

			tree = await objects.ReadTreeAsync(entry.Id, ct);
		}

		return null;
	}

	/// <summary>Every non-directory leaf under <paramref name="tree"/>, depth first, paths prefixed with <paramref name="prefix"/>.</summary>
	public async IAsyncEnumerable<(string Path, Leaf Leaf)> WalkAsync(ObjectId tree, string prefix = "", [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
	{
		foreach (var entry in (await objects.ReadTreeAsync(tree, ct)).Entries)
		{
			var path = PathNames.Join(prefix, entry.Name);
			if (entry.Kind == EntryKind.Tree)
			{
				await foreach (var item in WalkAsync(entry.Id, path, ct))
				{
					yield return item;
				}
			}
			else
			{
				yield return (path, entry.Leaf);
			}
		}
	}

	/// <summary>
	/// Applies path changes (a null leaf deletes; a tree leaf sets a whole subtree) and writes the new trees along the
	/// changed paths only. Deleting a path that does not exist is a no-op; setting a path under a file, or a file where a
	/// directory with other changes is, throws <see cref="PathClashException"/>.
	/// </summary>
	public async Task<ObjectId> EditAsync(ObjectId root, IEnumerable<PathChange> changes, CancellationToken ct = default)
	{
		var list = changes.Select(change => (Parts: PathNames.Split(PathNames.Normalize(change.Path)), change.Leaf)).ToList();
		if (list.Any(change => change.Parts.Length == 0))
		{
			throw new VersionException("A change cannot replace the root itself.");
		}

		var clashes = new List<string>();
		var written = new List<StoredObject>();
		var result = await EditTreeAsync(root, "", list, 0, clashes, written, ct);
		if (clashes.Count > 0)
		{
			throw new PathClashException(clashes);
		}

		// Every new tree along the changed paths, in one write (one round trip for a remote store).
		await objects.WriteManyAsync(written, ct);
		return result?.Id ?? await EmptyTreeAsync(ct);
	}

	private async Task<(ObjectId Id, long Size)?> EditTreeAsync(ObjectId? treeId, string prefix, List<(string[] Parts, Leaf? Leaf)> changes, int depth,
		List<string> clashes, List<StoredObject> written, CancellationToken ct)
	{
		var tree = treeId is { } id ? await objects.ReadTreeAsync(id, ct) : Tree.Empty;
		var entries = tree.Entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
		foreach (var group in changes.GroupBy(change => change.Parts[depth], StringComparer.Ordinal))
		{
			var name = group.Key;
			var path = PathNames.Join(prefix, name);
			var exact = group.Where(change => change.Parts.Length == depth + 1).ToList();
			var deeper = group.Where(change => change.Parts.Length > depth + 1).ToList();
			entries.TryGetValue(name, out var existing);
			if (exact.Count > 1)
			{
				clashes.Add(path);
				continue;
			}

			Leaf? current = existing?.Leaf;
			if (exact.Count == 1)
			{
				if (deeper.Count > 0 && exact[0].Leaf is { Kind: not EntryKind.Tree })
				{
					clashes.Add(path);
					continue;
				}

				current = exact[0].Leaf;
			}

			if (deeper.Count > 0)
			{
				if (current is not null && current.Kind != EntryKind.Tree)
				{
					if (deeper.All(change => change.Leaf is null))
					{
						// Deleting below a file: nothing there to delete.
						deeper.Clear();
					}
					else
					{
						clashes.Add(path);
						continue;
					}
				}

				if (deeper.Count > 0)
				{
					var subtree = await EditTreeAsync(current?.Id, path, deeper, depth + 1, clashes, written, ct);
					current = subtree is { } sub ? new Leaf(EntryKind.Tree, sub.Id, sub.Size) : null;
				}
			}

			if (current is null)
			{
				entries.Remove(name);
			}
			else
			{
				entries[name] = TreeEntry.Of(name, current);
			}
		}

		if (entries.Count == 0)
		{
			return null;
		}

		var result = Tree.Of(entries.Values);
		written.Add(result);
		return (ObjectId.Of(ObjectCodec.Encode(result)), result.TotalSize);
	}

	/// <summary>
	/// The leaf-level differences between two trees (null = empty tree), pruning every subtree whose id is equal. A
	/// directory replaced by a file shows as the directory's files deleted and the file added.
	/// </summary>
	public async Task<IReadOnlyList<TreeChange>> DiffAsync(ObjectId? oldTree, ObjectId? newTree, string prefix = "", CancellationToken ct = default)
	{
		// Breadth first: all the subtrees of one level that differ are fetched together, so a remote store answers a
		// whole level in one round trip instead of one per tree.
		var changes = new List<TreeChange>();
		var level = new List<(ObjectId? Old, ObjectId? New, string Prefix)> { (oldTree, newTree, prefix) };
		while (level.Count > 0)
		{
			level = level.Where(pair => pair.Old != pair.New).ToList();
			await objects.PrefetchAsync(level.SelectMany(pair => new[] { pair.Old, pair.New }).OfType<ObjectId>(), ct);
			var next = new List<(ObjectId? Old, ObjectId? New, string Prefix)>();
			foreach (var (o, n, at) in level)
			{
				await DiffLevelAsync(o, n, at, changes, next, ct);
			}

			level = next;
		}

		changes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
		return changes;
	}

	private async Task DiffLevelAsync(ObjectId? oldTree, ObjectId? newTree, string prefix, List<TreeChange> changes,
		List<(ObjectId? Old, ObjectId? New, string Prefix)> next, CancellationToken ct)
	{
		var oldEntries = oldTree is { } o ? (await objects.ReadTreeAsync(o, ct)).Entries : [];
		var newEntries = newTree is { } n ? (await objects.ReadTreeAsync(n, ct)).Entries : [];
		int i = 0, j = 0;
		while (i < oldEntries.Count || j < newEntries.Count)
		{
			var order = i >= oldEntries.Count ? 1 : j >= newEntries.Count ? -1 : string.CompareOrdinal(oldEntries[i].Name, newEntries[j].Name);
			var oldEntry = order <= 0 ? oldEntries[i] : null;
			var newEntry = order >= 0 ? newEntries[j] : null;
			if (order <= 0)
			{
				i++;
			}

			if (order >= 0)
			{
				j++;
			}

			if (oldEntry is not null && newEntry is not null && oldEntry.Leaf == newEntry.Leaf)
			{
				continue;
			}

			var path = PathNames.Join(prefix, (oldEntry ?? newEntry)!.Name);
			var oldIsTree = oldEntry?.Kind == EntryKind.Tree;
			var newIsTree = newEntry?.Kind == EntryKind.Tree;
			if (oldIsTree || newIsTree)
			{
				if (!oldIsTree && oldEntry is not null)
				{
					changes.Add(new TreeChange(path, oldEntry.Leaf, null));
				}

				next.Add((oldIsTree ? oldEntry!.Id : null, newIsTree ? newEntry!.Id : null, path));
				if (!newIsTree && newEntry is not null)
				{
					changes.Add(new TreeChange(path, null, newEntry.Leaf));
				}
			}
			else
			{
				changes.Add(new TreeChange(path, oldEntry?.Leaf, newEntry?.Leaf));
			}
		}
	}

	/// <summary>
	/// Three-way merge of trees: what changed from <paramref name="baseTree"/> to <paramref name="ours"/> and to
	/// <paramref name="theirs"/>. Equal subtrees cost nothing; a path both sides changed differently goes to
	/// <paramref name="resolve"/>, which returns the merged leaf, or null to store a conflict.
	/// </summary>
	public async Task<TreeMergeResult> MergeAsync(ObjectId? baseTree, ObjectId? ours, ObjectId? theirs,
		Func<string, Leaf?, Leaf?, Leaf?, Task<MergeResolution>> resolve, CancellationToken ct = default)
	{
		var conflicts = new List<string>();
		var resolved = new List<(string Path, string How)>();
		var merged = await MergeLeafAsync("", Tree(baseTree), Tree(ours), Tree(theirs), resolve, conflicts, resolved, ct);
		var root = merged is { Kind: EntryKind.Tree } ? merged.Id : await EmptyTreeAsync(ct);
		return new TreeMergeResult(root, conflicts, resolved);

		static Leaf? Tree(ObjectId? id) => id is { } value ? new Leaf(EntryKind.Tree, value, 0) : null;
	}

	private async Task<Leaf?> MergeLeafAsync(string path, Leaf? b, Leaf? o, Leaf? t,
		Func<string, Leaf?, Leaf?, Leaf?, Task<MergeResolution>> resolve, List<string> conflicts, List<(string, string)> resolved, CancellationToken ct)
	{
		if (SameContent(o, b))
		{
			return t;
		}

		if (SameContent(t, b) || SameContent(o, t))
		{
			return o;
		}

		var allTrees = (b is null || b.Kind == EntryKind.Tree) && (o is null || o.Kind == EntryKind.Tree) && (t is null || t.Kind == EntryKind.Tree);
		if (allTrees)
		{
			var bt = b is null ? Model.Tree.Empty : await objects.ReadTreeAsync(b.Id, ct);
			var ot = o is null ? Model.Tree.Empty : await objects.ReadTreeAsync(o.Id, ct);
			var tt = t is null ? Model.Tree.Empty : await objects.ReadTreeAsync(t.Id, ct);
			var names = bt.Entries.Select(e => e.Name).Concat(ot.Entries.Select(e => e.Name)).Concat(tt.Entries.Select(e => e.Name))
				.Distinct(StringComparer.Ordinal).ToList();
			var entries = new List<TreeEntry>();
			foreach (var name in names)
			{
				var leaf = await MergeLeafAsync(PathNames.Join(path, name), bt.Find(name)?.Leaf, ot.Find(name)?.Leaf, tt.Find(name)?.Leaf,
					resolve, conflicts, resolved, ct);
				if (leaf is not null)
				{
					entries.Add(TreeEntry.Of(name, leaf));
				}
			}

			if (entries.Count == 0)
			{
				return null;
			}

			var tree = Model.Tree.Of(entries);
			return new Leaf(EntryKind.Tree, await objects.WriteAsync(tree, ct), tree.TotalSize);
		}

		var resolution = await resolve(path, b, o, t);
		if (resolution.Resolved)
		{
			resolved.Add((path, resolution.How));
			return resolution.Leaf;
		}

		conflicts.Add(path);
		var conflictId = await objects.WriteAsync(new ConflictObject([b], [o, t]), ct);
		return new Leaf(EntryKind.Conflict, conflictId, Math.Max(o?.Size ?? 0, t?.Size ?? 0));
	}

	/// <summary>Whether two leaves hold the same content (sizes of directories are not compared).</summary>
	public static bool SameContent(Leaf? a, Leaf? b) =>
		a is null || b is null ? a is null && b is null : a.Kind == b.Kind && a.Id == b.Id && a.Executable == b.Executable;
}

/// <summary>What a merge resolver decided for one path: a merged leaf (null deletes) and how, or unresolved.</summary>
public sealed record MergeResolution(bool Resolved, Leaf? Leaf, string How)
{
	public static readonly MergeResolution Unresolved = new(false, null, "");

	public static MergeResolution With(Leaf? leaf, string how) => new(true, leaf, how);
}

/// <summary>The merged tree, the paths stored as conflicts, and the paths a policy merged (and how).</summary>
public sealed record TreeMergeResult(ObjectId Tree, IReadOnlyList<string> Conflicts, IReadOnlyList<(string Path, string How)> Resolved);
