namespace Bassia.Core.Version;

using System.Security.Cryptography;
using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>What a sync did: the workspace's new state, the trees before and after, conflicts stored and paths merged.</summary>
public sealed record SyncResult(WorkspaceState Workspace, ObjectId OldTree, ObjectId NewTree, long FromSeq, long ToSeq,
	IReadOnlyList<string> Conflicts, IReadOnlyList<string> Merged, bool Moved);

/// <summary>
/// Workspace operations on the version core: create (on mainline or stacked on another workspace), clone, amend the
/// working commit, sync (rebase onto the newest base), undo and restore, record a run, close. Each one writes the
/// workspace's new record as one operation on the workspace's own head, so workspaces never contend with each other.
/// </summary>
public sealed class WorkspaceService(Repository repo)
{
	public Repository Repository => repo;

	public static string NewId() => "w" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));

	public async Task<WorkspaceState> CreateAsync(string name, string owner, string? on = null, string intent = "", ReplayRecipe? recipe = null,
		ChangeId? changeId = null, string? path = null, string? host = null, string? replayOf = null, CancellationToken ct = default)
	{
		CheckName(name);
		ObjectId baseId;
		long baseSeq;
		string? parent = null;
		if (on is null)
		{
			var main = await repo.GetMainAsync(ct);
			baseId = main.CommitId;
			baseSeq = main.Seq;
		}
		else
		{
			var parentState = await repo.ResolveWorkspaceAsync(on, ct);
			if (!parentState.IsOpen)
			{
				throw new VersionException($"Workspace '{parentState.Name}' is {parentState.Record.Status}; stack on an open workspace or on mainline.");
			}

			baseId = parentState.Record.Working;
			baseSeq = parentState.Record.BaseSeq;
			parent = parentState.Id;
		}

		var baseCommit = await repo.Objects.ReadCommitAsync(baseId, ct);
		var working = new Commit(baseCommit.Tree, [baseId], changeId ?? ChangeId.New(), new Author(owner), intent, repo.Clock(), recipe);
		var workingId = await repo.Objects.WriteAsync(working, ct);
		var record = new WorkspaceRecord(NewId(), name, parent, baseId, baseSeq, workingId, WorkspaceStatus.Open, repo.Clock(), owner, path, host,
			ReplayOf: replayOf);
		return await repo.CommitWorkspaceAsync(null, record, parent is null ? $"create on seq {baseSeq}" : $"create stacked on {parent}", owner, null, ct);
	}

	/// <summary>A new workspace with the same base and the same work as <paramref name="source"/>, as a new change.</summary>
	public async Task<WorkspaceState> CloneAsync(string source, string name, string owner, string? path = null, string? host = null, CancellationToken ct = default)
	{
		CheckName(name);
		var from = await repo.ResolveWorkspaceAsync(source, ct);
		var working = await repo.Objects.ReadCommitAsync(from.Record.Working, ct);
		var copy = working with { ChangeId = ChangeId.New(), Author = new Author(owner), Time = repo.Clock() };
		var copyId = await repo.Objects.WriteAsync(copy, ct);
		var record = new WorkspaceRecord(NewId(), name, from.Record.Parent, from.Record.Base, from.Record.BaseSeq, copyId, WorkspaceStatus.Open,
			repo.Clock(), owner, path, host);
		return await repo.CommitWorkspaceAsync(null, record, $"clone of {from.Name}", owner, null, ct);
	}

	/// <summary>Amends the working commit with a new tree (a snapshot); returns the state unchanged if the tree is the same.</summary>
	public Task<WorkspaceState> SetTreeAsync(string id, ObjectId tree, string description, string? agent, string? run, CancellationToken ct = default) =>
		repo.UpdateWorkspaceAsync(id, async current =>
		{
			var working = await repo.Objects.ReadCommitAsync(current.Record.Working, ct);
			if (working.Tree == tree)
			{
				return null;
			}

			var amended = await repo.AmendAsync(working, tree, ct);
			return (current.Record with { Working = amended }, description);
		}, agent, run, ct);

	/// <summary>Changes the working commit's intent: the text a landing records as its message.</summary>
	public Task<WorkspaceState> DescribeAsync(string id, string intent, string? agent, CancellationToken ct = default) =>
		repo.UpdateWorkspaceAsync(id, async current =>
		{
			var working = await repo.Objects.ReadCommitAsync(current.Record.Working, ct);
			var amended = await repo.Objects.WriteAsync(working with { Intent = intent, Time = repo.Clock() }, ct);
			return (current.Record with { Working = amended }, "describe");
		}, agent, null, ct);

	/// <summary>Replaces the working commit with <paramref name="amend"/>'s version of it (tree, intent, author).</summary>
	public Task<WorkspaceState> AmendWorkingAsync(string id, Func<Commit, Commit> amend, string description, string? agent, CancellationToken ct = default) =>
		repo.UpdateWorkspaceAsync(id, async current =>
		{
			var working = await repo.Objects.ReadCommitAsync(current.Record.Working, ct);
			var amended = await repo.Objects.WriteAsync(amend(working) with { ChangeId = working.ChangeId, Parents = working.Parents }, ct);
			return amended == current.Record.Working ? null : (current.Record with { Working = amended }, description);
		}, agent, null, ct);

	/// <summary>Sets the replay recipe of the working commit (the task that made it).</summary>
	public Task<WorkspaceState> SetRecipeAsync(string id, ReplayRecipe recipe, Author author, CancellationToken ct = default) =>
		repo.UpdateWorkspaceAsync(id, async current =>
		{
			var working = await repo.Objects.ReadCommitAsync(current.Record.Working, ct);
			var amended = await repo.Objects.WriteAsync(working with { Recipe = recipe, Author = author, Time = repo.Clock() }, ct);
			return (current.Record with { Working = amended }, "record the task");
		}, author.Agent, author.Run, ct);

	/// <summary>
	/// Rebases the working commit onto the newest base: the parent workspace's working commit while the parent is open,
	/// otherwise mainline's head. Paths both sides changed go through <paramref name="resolver"/>; what it cannot merge is
	/// stored as conflicts in the workspace.
	/// </summary>
	public async Task<SyncResult> SyncAsync(string id, ConflictResolver? resolver, string? agent, CancellationToken ct = default)
	{
		for (var attempt = 0; ; attempt++)
		{
			var current = await repo.FindWorkspaceAsync(id, ct) ?? throw new VersionException($"No workspace '{id}'.");
			var record = current.Record;
			if (!current.IsOpen)
			{
				throw new VersionException($"Workspace '{record.Name}' is {record.Status}; only an open workspace can sync.");
			}

			ObjectId newBase;
			long newSeq;
			string? parent = record.Parent;
			if (parent is not null && await repo.FindWorkspaceAsync(parent, ct) is { IsOpen: true } parentState)
			{
				newBase = parentState.Record.Working;
				newSeq = parentState.Record.BaseSeq;
			}
			else
			{
				var main = await repo.GetMainAsync(ct);
				newBase = main.CommitId;
				newSeq = main.Seq;
				parent = null;
			}

			var working = await repo.Objects.ReadCommitAsync(record.Working, ct);
			if (newBase == record.Base)
			{
				return new SyncResult(current, working.Tree, working.Tree, record.BaseSeq, record.BaseSeq, [], [], false);
			}

			var oldBase = await repo.Objects.ReadCommitAsync(record.Base, ct);
			var target = await repo.Objects.ReadCommitAsync(newBase, ct);
			resolver ??= new ConflictResolver(repo.Objects, await RepoPolicy.LoadAsync(repo.Trees, target.Tree, ct));
			var merge = await repo.Trees.MergeAsync(oldBase.Tree, working.Tree, target.Tree,
				(path, b, o, t) => resolver.ResolveAsync(path, b, o, t, ct), ct);
			var rebased = await repo.Objects.WriteAsync(working with
			{
				Tree = merge.Tree,
				Parents = [newBase],
				Time = repo.Clock(),
				MachineMerged = working.MachineMerged || resolver.MachineMerged
			}, ct);
			var next = record with { Base = newBase, BaseSeq = newSeq, Working = rebased, Parent = parent, Status = WorkspaceStatus.Open };
			try
			{
				var state = await repo.CommitWorkspaceAsync(current, next,
					$"sync onto {(parent is null ? $"seq {newSeq}" : $"workspace {parent}")}{(merge.Conflicts.Count > 0 ? $", {merge.Conflicts.Count} conflict(s)" : "")}",
					agent, null, ct);
				return new SyncResult(state, working.Tree, merge.Tree, record.BaseSeq, newSeq, merge.Conflicts,
					merge.Resolved.Select(item => $"{item.Path} ({item.How})").ToList(), true);
			}
			catch (ConcurrentUpdateException) when (attempt < 20)
			{
			}
		}
	}

	/// <summary>Restores the record of the operation before the head: a new operation, so undo itself can be undone.</summary>
	public async Task<(WorkspaceState State, ObjectId OldTree, ObjectId NewTree)> UndoAsync(string id, string? agent, CancellationToken ct = default)
	{
		var current = await repo.ResolveWorkspaceAsync(id, ct);
		if (current.Operation.Parents.Count == 0)
		{
			throw new VersionException($"Workspace '{current.Name}' has nothing to undo: its first operation created it.");
		}

		return await RestoreAsync(current.Id, current.Operation.Parents[0], agent, $"undo {current.Operation.Description}", ct);
	}

	/// <summary>Restores the workspace to what operation <paramref name="op"/> of its own log recorded.</summary>
	public async Task<(WorkspaceState State, ObjectId OldTree, ObjectId NewTree)> RestoreAsync(string id, ObjectId op, string? agent,
		string? description = null, CancellationToken ct = default)
	{
		var current = await repo.ResolveWorkspaceAsync(id, ct);
		var target = await repo.WorkspaceAtAsync(op, ct);
		if (target.Id != current.Id)
		{
			throw new VersionException($"Operation {op.Short} belongs to another workspace.");
		}

		var oldTree = (await repo.Objects.ReadCommitAsync(current.Record.Working, ct)).Tree;
		var restored = target.Record with { Path = current.Record.Path, Host = current.Record.Host, Run = current.Record.Run };
		var state = await repo.CommitWorkspaceAsync(current, restored, description ?? $"restore operation {op.Short}", agent, null, ct);
		var newTree = (await repo.Objects.ReadCommitAsync(restored.Working, ct)).Tree;
		return (state, oldTree, newTree);
	}

	public Task<WorkspaceState> CloseAsync(string id, bool abandon, string? agent, CancellationToken ct = default) =>
		repo.UpdateWorkspaceAsync(id, current =>
		{
			var status = abandon ? WorkspaceStatus.Abandoned : current.Record.Status is WorkspaceStatus.Landed or WorkspaceStatus.Replaced
				? current.Record.Status
				: WorkspaceStatus.Closed;
			return Task.FromResult<(WorkspaceRecord, string)?>((current.Record with { Status = status, Closed = current.Record.Closed ?? repo.Clock() },
				abandon ? "abandon" : "close"));
		}, agent, null, ct);

	public Task<WorkspaceState> SetStatusAsync(string id, string status, string description, string? agent, CancellationToken ct = default) =>
		repo.UpdateWorkspaceAsync(id, current => Task.FromResult<(WorkspaceRecord, string)?>(
			(current.Record with { Status = status, Closed = WorkspaceStatus.IsOpen(status) ? null : repo.Clock() }, description)), agent, null, ct);

	public Task<WorkspaceState> RecordRunAsync(string id, RunRecord run, string description, CancellationToken ct = default) =>
		repo.UpdateWorkspaceAsync(id, current => Task.FromResult<(WorkspaceRecord, string)?>((current.Record with { Run = run }, description)),
			run.Agent, run.RunId, ct);

	public Task<WorkspaceState> SetPathAsync(string id, string? path, string? host, CancellationToken ct = default) =>
		repo.UpdateWorkspaceAsync(id, current => Task.FromResult<(WorkspaceRecord, string)?>(
			current.Record.Path == path && current.Record.Host == host ? null : (current.Record with { Path = path, Host = host }, "move")), null, null, ct);

	private static void CheckName(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'))
		{
			throw new VersionException($"'{name}' is not a valid workspace name (1-100 characters, no slashes or colons).");
		}
	}
}
