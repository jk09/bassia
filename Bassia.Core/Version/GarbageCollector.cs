namespace Bassia.Core.Version;

using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>What a collection found and did.</summary>
public sealed record GcReport(int Roots, int Live, int Blobs, int Swept, long SweptBytes, int Young, int PrunedWorkspaces, bool DryRun, int TreesSwept = 0, int Trees = 0);

/// <summary>What a scrub checked and what was wrong.</summary>
/// <remarks><see cref="Checked"/> counts blobs; <see cref="TreesChecked"/> the trees checked in the tree store.</remarks>
public sealed record ScrubReport(int Checked, IReadOnlyList<ObjectId> Corrupt, IReadOnlyList<ObjectId> Unreadable, int TreesChecked = 0);

/// <summary>What moving trees into a tree store did.</summary>
public sealed record TreeMigrationReport(int Trees, int BlobsDeleted);

/// <summary>
/// Mark and sweep over the object store. Roots are every head - mainline (its operation log is the audit trail and is
/// kept), the workspaces, and pins (heads named <c>pin/...</c>). A closed workspace's head is pruned
/// <paramref name="retention"/> after it closed, which makes its unlanded snapshots collectable. Only blobs older than
/// the grace period are swept, so chunks uploaded for a landing that has not happened yet survive.
/// </summary>
public sealed class GarbageCollector(Repository repo, IObjectStoreMaintenance maintenance)
{
	/// <summary>Called for every tree a walk reaches (migrating trees out of blobs).</summary>
	public Action<ObjectId>? OnTree { get; set; }

	public async Task<GcReport> CollectAsync(TimeSpan grace, TimeSpan retention, bool dryRun = false, CancellationToken ct = default)
	{
		var now = repo.Clock();
		var pruned = 0;
		var roots = new List<ObjectId>();
		var candidates = new List<(HeadName Name, ObjectId Head)>();
		await foreach (var (name, head) in repo.Views.ListHeadsAsync("", ct))
		{
			// A candidate head points at a Candidate, not an operation; it is kept only while its workspace is open.
			if (name.IsCandidate)
			{
				candidates.Add((name, head));
				continue;
			}

			if (name.IsWorkspace)
			{
				var state = await repo.WorkspaceAtAsync(head, ct);
				if (!state.IsOpen && state.Record.Closed is { } closed && now - closed > retention)
				{
					if (!dryRun && await repo.Views.DeleteHeadAsync(name, head, ct))
					{
						pruned++;
						continue;
					}

					if (dryRun)
					{
						pruned++;
						continue;
					}
				}
			}

			roots.Add(head);
		}

		var live = new HashSet<ObjectId>();
		var treeRoots = new List<ObjectId>();
		await WalkAsync(roots.Select(op => (op, "op")), live, treeRoots, ct);
		foreach (var (name, head) in candidates)
		{
			if (await repo.FindWorkspaceAsync(name.WorkspaceId, ct) is { IsOpen: true })
			{
				await MarkCandidateAsync(head, live, treeRoots, ct);
			}
			else if (!dryRun)
			{
				await repo.Views.DeleteHeadAsync(name, head, ct);
			}
		}

		var reached = await MarkTreesAsync(live, treeRoots, ct);

		int blobs = 0, swept = 0, young = 0;
		long sweptBytes = 0;
		var doomed = new List<BlobInfo>();
		await foreach (var blob in maintenance.ListAsync(ct))
		{
			blobs++;
			if (live.Contains(blob.Id))
			{
				continue;
			}

			if (now - blob.Written < grace)
			{
				young++;
				continue;
			}

			doomed.Add(blob);
		}

		foreach (var blob in doomed)
		{
			if (!dryRun)
			{
				await maintenance.DeleteAsync(blob.Id, ct);
			}

			swept++;
			sweptBytes += blob.Length;
		}

		var treesSwept = 0;
		if (treeMaintenance is not null)
		{
			var doomedTrees = new List<ObjectId>();
			await foreach (var tree in treeMaintenance.ListAsync(ct))
			{
				if (!reached.Contains(tree.Id) && now - tree.Written >= grace)
				{
					doomedTrees.Add(tree.Id);
				}
			}

			if (!dryRun)
			{
				foreach (var part in doomedTrees.Chunk(1000))
				{
					await treeMaintenance.DeleteAsync(part, ct);
				}
			}

			treesSwept = doomedTrees.Count;
		}

		return new GcReport(roots.Count, live.Count, blobs, swept, sweptBytes, young, pruned, dryRun, treesSwept, reached.Count);
	}

	private ITreeStoreMaintenance? treeMaintenance => repo.Objects.TreeStore as ITreeStoreMaintenance;

	/// <summary>
	/// With a relational tree store, trees are not walked one by one: their roots are collected and the store computes
	/// everything reachable from them in one query. What it finds (files, chunks, conflicts) is walked in turn, and
	/// conflicts can lead to more trees, so this repeats until nothing new turns up. Returns the reachable trees.
	/// </summary>
	private async Task<HashSet<ObjectId>> MarkTreesAsync(HashSet<ObjectId> live, List<ObjectId> treeRoots, CancellationToken ct)
	{
		var reached = new HashSet<ObjectId>();
		while (treeMaintenance is { } maintenance && treeRoots.Where(id => !reached.Contains(id)).Distinct().ToList() is { Count: > 0 } roots)
		{
			treeRoots.Clear();
			var reach = await maintenance.ReachAsync(roots, ct);
			reached.UnionWith(reach.Trees);
			reached.UnionWith(roots);
			await WalkAsync(reach.Leaves.Select(LeafItem), live, treeRoots, ct);
		}

		return reached;
	}

	/// <summary>Marks a candidate and the leaves of its changes (merge results exist nowhere else).</summary>
	private async Task MarkCandidateAsync(ObjectId id, HashSet<ObjectId> live, List<ObjectId> treeRoots, CancellationToken ct)
	{
		if (!live.Add(id))
		{
			return;
		}

		var candidate = await repo.Objects.ReadCandidateAsync(id, ct);
		await WalkAsync(candidate.Changes.Select(change => change.Leaf).OfType<Leaf>().Select(LeafItem), live, treeRoots, ct);
	}

	/// <summary>Every object reachable from the given operations. Shared subtrees are walked once.</summary>
	public async Task<HashSet<ObjectId>> MarkAsync(IEnumerable<ObjectId> operations, CancellationToken ct = default)
	{
		var live = new HashSet<ObjectId>();
		var treeRoots = new List<ObjectId>();
		await WalkAsync(operations.Select(op => (op, "op")), live, treeRoots, ct);
		live.UnionWith(await MarkTreesAsync(live, treeRoots, ct));
		return live;
	}

	/// <summary>Adds to <paramref name="live"/> everything reachable from the given objects, skipping what it already holds.</summary>
	private async Task<HashSet<ObjectId>> WalkAsync(IEnumerable<(ObjectId Id, string Kind)> start, HashSet<ObjectId> live, List<ObjectId> treeRoots, CancellationToken ct)
	{
		var pending = new Stack<(ObjectId Id, string Kind)>(start);
		while (pending.TryPop(out var item))
		{
			if (!live.Add(item.Id))
			{
				continue;
			}

			switch (item.Kind)
			{
				case "op":
					var op = await repo.Objects.ReadOperationAsync(item.Id, ct);
					pending.Push((op.View, "view"));
					foreach (var parent in op.Parents)
					{
						pending.Push((parent, "op"));
					}

					break;
				case "view":
					var view = await repo.Objects.ReadViewAsync(item.Id, ct);
					foreach (var commit in view.Refs.Values)
					{
						pending.Push((commit, "commit"));
					}

					if (view.Workspace is { } workspace)
					{
						pending.Push((workspace.Base, "commit"));
						pending.Push((workspace.Working, "commit"));
						if (workspace.Run?.Log is { } log)
						{
							pending.Push((log, "chunk"));
						}

						if (workspace.LastLand?.Commit is { } landed)
						{
							pending.Push((landed, "commit"));
						}
					}

					break;
				case "commit":
					var commitObject = await repo.Objects.ReadCommitAsync(item.Id, ct);
					pending.Push((commitObject.Tree, "tree"));
					foreach (var parent in commitObject.Parents)
					{
						pending.Push((parent, "commit"));
					}

					if (commitObject.Recipe?.TaskSpec is { } spec)
					{
						pending.Push((spec, "chunk"));
					}

					break;
				case "tree":
					OnTree?.Invoke(item.Id);
					if (treeMaintenance is not null)
					{
						// The tree store walks the whole subtree itself (MarkTreesAsync); the id counts as live there.
						live.Remove(item.Id);
						treeRoots.Add(item.Id);
						break;
					}

					foreach (var entry in (await repo.Objects.ReadTreeAsync(item.Id, ct)).Entries)
					{
						pending.Push(LeafItem(entry.Leaf));
					}

					break;
				case "file":
					foreach (var chunk in (await repo.Objects.ReadAsync<FileObject>(item.Id, ct)).Chunks)
					{
						pending.Push((chunk.Id, "chunk"));
					}

					break;
				case "conflict":
					var conflict = await repo.Objects.ReadAsync<ConflictObject>(item.Id, ct);
					foreach (var leaf in conflict.Bases.Concat(conflict.Sides).OfType<Leaf>())
					{
						pending.Push(LeafItem(leaf));
					}

					break;
			}
		}

		return live;
	}

	private static (ObjectId, string) LeafItem(Leaf leaf) => leaf.Kind switch
	{
		EntryKind.Tree => (leaf.Id, "tree"),
		EntryKind.Chunked => (leaf.Id, "file"),
		EntryKind.Conflict => (leaf.Id, "conflict"),
		_ => (leaf.Id, "chunk")
	};

	/// <summary>Re-hashes a sample of stored blobs (every blob when <paramref name="sample"/> is null) and reports mismatches.</summary>
	public async Task<ScrubReport> ScrubAsync(int? sample = null, CancellationToken ct = default)
	{
		var all = new List<ObjectId>();
		await foreach (var blob in maintenance.ListAsync(ct))
		{
			all.Add(blob.Id);
		}

		var allTrees = new List<ObjectId>();
		if (treeMaintenance is { } treeStore)
		{
			await foreach (var tree in treeStore.ListAsync(ct))
			{
				allTrees.Add(tree.Id);
			}
		}

		var chosen = sample is { } count && count < all.Count ? all.OrderBy(_ => Random.Shared.Next()).Take(count).ToList() : all;
		var chosenTrees = sample is { } treeCount && treeCount < allTrees.Count ? allTrees.OrderBy(_ => Random.Shared.Next()).Take(treeCount).ToList() : allTrees;
		var corrupt = new List<ObjectId>();
		var unreadable = new List<ObjectId>();

		// A relational tree is checked by encoding its entries canonically and hashing the result against its id.
		foreach (var part in chosenTrees.Chunk(500))
		{
			var found = await repo.Objects.TreeStore.GetManyAsync(part, ct);
			foreach (var id in part)
			{
				if (!found.TryGetValue(id, out var tree))
				{
					unreadable.Add(id);
				}
				else if (ObjectId.Of(ObjectCodec.Encode(tree)) != id)
				{
					corrupt.Add(id);
				}
			}
		}

		foreach (var id in chosen)
		{
			var bytes = await repo.Store.ReadAsync(id, null, ct);
			if (bytes is null)
			{
				unreadable.Add(id);
			}
			else if (ObjectId.Of(bytes.Value.Span) != id)
			{
				corrupt.Add(id);
			}
		}

		return new ScrubReport(chosen.Count, corrupt, unreadable, chosenTrees.Count);
	}

	/// <summary>
	/// Moves every tree reachable from the heads out of blobs into <paramref name="destination"/> (the explicit step of
	/// switching a store to a relational tree store); with <paramref name="deleteBlobs"/>, the tree blobs are then removed.
	/// Run it on a repository that still reads trees from blobs.
	/// </summary>
	public async Task<TreeMigrationReport> MigrateTreesAsync(ITreeStore destination, bool deleteBlobs, CancellationToken ct = default)
	{
		var trees = new HashSet<ObjectId>();
		var previous = OnTree;
		OnTree = id => trees.Add(id);
		try
		{
			var live = new HashSet<ObjectId>();
			var ignored = new List<ObjectId>();
			var roots = new List<ObjectId>();
			await foreach (var (name, head) in repo.Views.ListHeadsAsync("", ct))
			{
				if (name.IsCandidate)
				{
					await MarkCandidateAsync(head, live, ignored, ct);
				}
				else
				{
					roots.Add(head);
				}
			}

			await WalkAsync(roots.Select(op => (op, "op")), live, ignored, ct);
		}
		finally
		{
			OnTree = previous;
		}

		foreach (var part in trees.Chunk(500))
		{
			var batch = new List<(ObjectId Id, Tree Tree)>();
			foreach (var id in part)
			{
				var tree = await repo.Objects.ReadTreeAsync(id, ct);
				if (ObjectId.Of(ObjectCodec.Encode(tree)) != id)
				{
					throw new VersionException($"Tree {id.Short} does not match its id; run a scrub before moving trees.");
				}

				batch.Add((id, tree));
			}

			await destination.PutManyAsync(batch, ct);
		}

		var deleted = 0;
		if (deleteBlobs)
		{
			foreach (var id in trees)
			{
				await maintenance.DeleteAsync(id, ct);
				deleted++;
			}
		}

		return new TreeMigrationReport(trees.Count, deleted);
	}
}
