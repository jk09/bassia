namespace Bassia.Core.Version;

using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>What a collection found and did.</summary>
public sealed record GcReport(int Roots, int Live, int Blobs, int Swept, long SweptBytes, int Young, int PrunedWorkspaces, bool DryRun);

/// <summary>What a scrub checked and what was wrong.</summary>
public sealed record ScrubReport(int Checked, IReadOnlyList<ObjectId> Corrupt, IReadOnlyList<ObjectId> Unreadable);

/// <summary>
/// Mark and sweep over the object store. Roots are every head - mainline (its operation log is the audit trail and is
/// kept), the workspaces, and pins (heads named <c>pin/...</c>). A closed workspace's head is pruned
/// <paramref name="retention"/> after it closed, which makes its unlanded snapshots collectable. Only blobs older than
/// the grace period are swept, so chunks uploaded for a landing that has not happened yet survive.
/// </summary>
public sealed class GarbageCollector(Repository repo, IObjectStoreMaintenance maintenance)
{
	public async Task<GcReport> CollectAsync(TimeSpan grace, TimeSpan retention, bool dryRun = false, CancellationToken ct = default)
	{
		var now = repo.Clock();
		var pruned = 0;
		var roots = new List<ObjectId>();
		await foreach (var (name, head) in repo.Views.ListHeadsAsync("", ct))
		{
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

		var live = await MarkAsync(roots, ct);
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

		return new GcReport(roots.Count, live.Count, blobs, swept, sweptBytes, young, pruned, dryRun);
	}

	/// <summary>Every object reachable from the given operations. Shared subtrees are walked once.</summary>
	public async Task<HashSet<ObjectId>> MarkAsync(IEnumerable<ObjectId> operations, CancellationToken ct = default)
	{
		var live = new HashSet<ObjectId>();
		var pending = new Stack<(ObjectId Id, string Kind)>(operations.Select(op => (op, "op")));
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

		static (ObjectId, string) LeafItem(Leaf leaf) => leaf.Kind switch
		{
			EntryKind.Tree => (leaf.Id, "tree"),
			EntryKind.Chunked => (leaf.Id, "file"),
			EntryKind.Conflict => (leaf.Id, "conflict"),
			_ => (leaf.Id, "chunk")
		};
	}

	/// <summary>Re-hashes a sample of stored blobs (every blob when <paramref name="sample"/> is null) and reports mismatches.</summary>
	public async Task<ScrubReport> ScrubAsync(int? sample = null, CancellationToken ct = default)
	{
		var all = new List<ObjectId>();
		await foreach (var blob in maintenance.ListAsync(ct))
		{
			all.Add(blob.Id);
		}

		var chosen = sample is { } count && count < all.Count ? all.OrderBy(_ => Random.Shared.Next()).Take(count).ToList() : all;
		var corrupt = new List<ObjectId>();
		var unreadable = new List<ObjectId>();
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

		return new ScrubReport(chosen.Count, corrupt, unreadable);
	}
}
