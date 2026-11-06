namespace Bassia.Studio;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Text;
using Bassia.Core.Version;

/// <summary>An edit refused because the path changed since the user loaded it: their text is kept, nothing is lost.</summary>
public sealed class StaleEditException(string message) : VersionException(message);

/// <summary>How to resolve a stored conflict: keep the workspace's side, take mainline's, or write merged content.</summary>
public enum ConflictChoice
{
	Workspace,
	Mainline,
	Merged
}

/// <summary>A path stored as a conflict in a workspace, with its sides.</summary>
public sealed record StoredConflict(string Path, Leaf? Base, Leaf? Workspace, Leaf? Mainline);

/// <summary>
/// What Studio changes, on behalf of its user: the same core operations the <c>bassia</c> command line performs
/// (<see cref="WorkspaceService"/>, the landing service, the lease store), so a change made here is the change the CLI
/// would make. Studio has no workspace folder: edits apply to the working commit directly, each one a snapshot.
/// A contributor acts on their own workspaces and leases; an admin on anyone's; a reader on nothing.
/// </summary>
public sealed class StudioActions(MonorepoClient client, StudioUser user)
{
	public StudioUser User => user;

	private async Task<(Repository Repo, WorkspaceService Service)> OpenAsync(CancellationToken ct)
	{
		var repo = await client.OpenAsync(ct);
		return (repo, new WorkspaceService(repo));
	}

	private void EnsureCanChange()
	{
		if (!user.CanChange)
		{
			throw new StudioForbiddenException($"'{user.Name}' is a reader: changing the monorepo needs the contributor role.");
		}
	}

	/// <summary>The workspace, if the user may change it: their own as a contributor, any as an admin.</summary>
	private async Task<WorkspaceState> OwnedAsync(Repository repo, string reference, CancellationToken ct)
	{
		EnsureCanChange();
		var state = await repo.ResolveWorkspaceAsync(reference, ct);
		if (!user.IsAdmin && state.Record.Owner != user.Name)
		{
			throw new StudioForbiddenException($"Workspace '{state.Name}' belongs to '{state.Record.Owner}'; only its owner or an admin may change it.");
		}

		return state;
	}

	// ----- workspaces -----

	/// <summary>A new workspace on mainline, or stacked on another workspace.</summary>
	public async Task<WorkspaceState> CreateAsync(string name, string intent = "", string? on = null, CancellationToken ct = default)
	{
		EnsureCanChange();
		var (_, service) = await OpenAsync(ct);
		return await service.CreateAsync(name.Trim(), user.Name, string.IsNullOrWhiteSpace(on) ? null : on, intent, ct: ct);
	}

	/// <summary>A new workspace with the same base and work as another one, as a new change.</summary>
	public async Task<WorkspaceState> CloneAsync(string source, string name, CancellationToken ct = default)
	{
		EnsureCanChange();
		var (_, service) = await OpenAsync(ct);
		return await service.CloneAsync(source, name.Trim(), user.Name, ct: ct);
	}

	public async Task<WorkspaceState> DescribeAsync(string workspace, string intent, CancellationToken ct = default)
	{
		var (repo, service) = await OpenAsync(ct);
		var state = await OwnedAsync(repo, workspace, ct);
		return await service.DescribeAsync(state.Id, intent, user.Name, ct);
	}

	/// <summary>
	/// Applies path changes to the working commit as one snapshot. <paramref name="expected"/> holds, per path, the
	/// content the user saw (null: the path did not exist); if any path has changed since, nothing is written.
	/// </summary>
	public async Task<WorkspaceState> EditAsync(string workspace, IReadOnlyList<PathChange> changes, IReadOnlyDictionary<string, ObjectId?>? expected,
		string description, CancellationToken ct = default)
	{
		var (repo, _) = await OpenAsync(ct);
		var state = await OwnedAsync(repo, workspace, ct);
		if (!state.IsOpen)
		{
			throw new VersionException($"Workspace '{state.Name}' is {state.Record.Status}; only an open workspace can be edited.");
		}

		foreach (var change in changes)
		{
			PathNames.Normalize(change.Path);
		}

		return await repo.UpdateWorkspaceAsync(state.Id, async current =>
		{
			var working = await repo.Objects.ReadCommitAsync(current.Record.Working, ct);
			foreach (var (path, seen) in expected ?? new Dictionary<string, ObjectId?>())
			{
				var now = (await repo.Trees.GetAsync(working.Tree, path, ct))?.Id;
				if (now != seen)
				{
					throw new StaleEditException(seen is null
						? $"'{path}' was created in workspace '{current.Name}' since you opened it; your text is kept - reload to see theirs."
						: $"'{path}' changed in workspace '{current.Name}' since you opened it; your text is kept - reload to see the new version.");
				}
			}

			var tree = await repo.Trees.EditAsync(working.Tree, changes, ct);
			return tree == working.Tree ? null : (current.Record with { Working = await repo.AmendAsync(working, tree, ct) }, description);
		}, user.Name, null, ct);
	}

	/// <summary>Writes a file (text or bytes) into a workspace; <paramref name="seen"/> is the version the user edited.</summary>
	public async Task<WorkspaceState> SaveFileAsync(string workspace, string path, byte[] content, ObjectId? seen, bool executable = false, CancellationToken ct = default)
	{
		var (repo, _) = await OpenAsync(ct);
		path = PathNames.Normalize(path);
		var leaf = await repo.Objects.WriteContentAsync(content, executable, ct);
		return await EditAsync(workspace, [new PathChange(path, leaf)], new Dictionary<string, ObjectId?> { [path] = seen },
			seen is null ? $"add {path}" : $"edit {path}", ct);
	}

	public Task<WorkspaceState> SaveTextAsync(string workspace, string path, string text, ObjectId? seen, CancellationToken ct = default) =>
		SaveFileAsync(workspace, path, Encoding.UTF8.GetBytes(text), seen, ct: ct);

	public async Task<WorkspaceState> DeleteAsync(string workspace, string path, ObjectId seen, CancellationToken ct = default)
	{
		path = PathNames.Normalize(path);
		return await EditAsync(workspace, [new PathChange(path, null)], new Dictionary<string, ObjectId?> { [path] = seen }, $"delete {path}", ct);
	}

	/// <summary>Moves a file or folder to a new path that does not exist yet.</summary>
	public async Task<WorkspaceState> RenameAsync(string workspace, string from, string to, CancellationToken ct = default)
	{
		var (repo, _) = await OpenAsync(ct);
		from = PathNames.Normalize(from);
		to = PathNames.Normalize(to);
		var state = await repo.ResolveWorkspaceAsync(workspace, ct);
		var tree = (await repo.Objects.ReadCommitAsync(state.Record.Working, ct)).Tree;
		var leaf = await repo.Trees.GetAsync(tree, from, ct) ?? throw new VersionException($"'{from}' does not exist in workspace '{state.Name}'.");
		if (from == to || PathNames.IsUnder(to, from))
		{
			throw new VersionException($"'{from}' cannot move to '{to}'.");
		}

		return await EditAsync(workspace, [new PathChange(from, null), new PathChange(to, leaf)],
			new Dictionary<string, ObjectId?> { [from] = leaf.Id, [to] = null }, $"rename {from} -> {to}", ct);
	}

	/// <summary>Rebases the workspace onto its newest base; what the policy cannot merge is stored as conflicts.</summary>
	public async Task<SyncResult> SyncAsync(string workspace, CancellationToken ct = default)
	{
		var (repo, service) = await OpenAsync(ct);
		var state = await OwnedAsync(repo, workspace, ct);
		return await service.SyncAsync(state.Id, null, user.Name, ct);
	}

	public async Task<WorkspaceState> UndoAsync(string workspace, CancellationToken ct = default)
	{
		var (repo, service) = await OpenAsync(ct);
		var state = await OwnedAsync(repo, workspace, ct);
		return (await service.UndoAsync(state.Id, user.Name, ct)).State;
	}

	public async Task<WorkspaceState> RestoreAsync(string workspace, ObjectId operation, CancellationToken ct = default)
	{
		var (repo, service) = await OpenAsync(ct);
		var state = await OwnedAsync(repo, workspace, ct);
		return (await service.RestoreAsync(state.Id, operation, user.Name, ct: ct)).State;
	}

	public async Task<WorkspaceState> CloseAsync(string workspace, bool abandon, CancellationToken ct = default)
	{
		var (repo, service) = await OpenAsync(ct);
		var state = await OwnedAsync(repo, workspace, ct);
		return await service.CloseAsync(state.Id, abandon, user.Name, ct);
	}

	/// <summary>Whether the workspace can land now (a dry run of the landing): readers may ask too.</summary>
	public async Task<LandResult> CheckAsync(string workspace, CancellationToken ct = default)
	{
		var (repo, _) = await OpenAsync(ct);
		var state = await repo.ResolveWorkspaceAsync(workspace, ct);
		return await client.Landing.LandAsync(new LandRequest(state.Id, DryRun: true, Agent: user.Name), ct);
	}

	/// <summary>
	/// The live conflict overview the server keeps: every open workspace reconciled with mainline, their overlaps and
	/// the hot paths, in one call instead of a dry run per workspace.
	/// </summary>
	public async Task<ConflictOverview> OverviewAsync(CancellationToken ct = default)
	{
		await OpenAsync(ct);
		return await client.Overview.OverviewAsync(ct);
	}

	/// <summary>Lands the workspace on mainline, holding the leases its owner has on the paths it touches.</summary>
	public async Task<LandResult> LandAsync(string workspace, CancellationToken ct = default)
	{
		var (repo, _) = await OpenAsync(ct);
		var state = await OwnedAsync(repo, workspace, ct);
		var leases = (await repo.Leases.ListAsync(null, ct)).Where(lease => lease.Owner == state.Id).Select(lease => new LeaseToken(lease.Id, lease.Token)).ToList();
		return await client.Landing.LandAsync(new LandRequest(state.Id, Leases: leases, Agent: user.Name), ct);
	}

	// ----- conflicts -----

	/// <summary>The paths a workspace stores as conflicts (after a sync the policy could not merge).</summary>
	public async Task<IReadOnlyList<StoredConflict>> ConflictsAsync(string workspace, CancellationToken ct = default)
	{
		var (repo, _) = await OpenAsync(ct);
		var state = await repo.ResolveWorkspaceAsync(workspace, ct);
		var working = (await repo.Objects.ReadCommitAsync(state.Record.Working, ct)).Tree;
		var conflicts = new List<StoredConflict>();
		await foreach (var (path, leaf) in repo.Trees.WalkAsync(working, "", ct))
		{
			if (leaf.Kind == EntryKind.Conflict)
			{
				var conflict = await repo.Objects.ReadAsync<ConflictObject>(leaf.Id, ct);
				conflicts.Add(new StoredConflict(path, conflict.Bases.FirstOrDefault(), conflict.Sides.ElementAtOrDefault(0), conflict.Sides.ElementAtOrDefault(1)));
			}
		}

		return conflicts;
	}

	/// <summary>A conflict as text with markers (workspace side, base, mainline side): the start of a merged edit.</summary>
	public async Task<string> ConflictTextAsync(StoredConflict conflict, CancellationToken ct = default)
	{
		async Task<string?> Text(Leaf? side) => side is { IsFile: true } ? Encoding.UTF8.GetString(await client.Repo.Objects.ReadContentAsync(side, ct)) : null;
		await client.OpenAsync(ct);
		return TextMerge.Materialize(await Text(conflict.Base), [("workspace", await Text(conflict.Workspace)), ("mainline", await Text(conflict.Mainline))]);
	}

	/// <summary>Resolves a stored conflict: one side as it is, or merged text the user wrote.</summary>
	public async Task<WorkspaceState> ResolveAsync(string workspace, string path, ConflictChoice choice, string? merged = null, CancellationToken ct = default)
	{
		var (repo, _) = await OpenAsync(ct);
		path = PathNames.Normalize(path);
		var conflict = (await ConflictsAsync(workspace, ct)).FirstOrDefault(item => item.Path == path)
			?? throw new VersionException($"'{path}' is not a conflict in workspace '{workspace}'.");
		var leaf = choice switch
		{
			ConflictChoice.Workspace => conflict.Workspace,
			ConflictChoice.Mainline => conflict.Mainline,
			_ => await repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes(merged ?? throw new VersionException("Merged content is required.")), ct: ct)
		};
		var current = await repo.ResolveWorkspaceAsync(workspace, ct);
		var seen = (await repo.Trees.GetAsync((await repo.Objects.ReadCommitAsync(current.Record.Working, ct)).Tree, path, ct))?.Id;
		return await EditAsync(workspace, [new PathChange(path, leaf)], new Dictionary<string, ObjectId?> { [path] = seen },
			$"resolve {path} ({choice.ToString().ToLowerInvariant()})", ct);
	}

	// ----- leases -----

	/// <summary>Leases a path prefix for a workspace (like a Perforce lock): exclusive, or intent to announce work.</summary>
	public async Task<LeaseResult> AcquireLeaseAsync(string prefix, LeaseMode mode, string workspace, TimeSpan ttl, CancellationToken ct = default)
	{
		var (repo, _) = await OpenAsync(ct);
		var state = await OwnedAsync(repo, workspace, ct);
		return await repo.Leases.AcquireAsync(new LeaseRequest(PathNames.Normalize(prefix), mode, state.Id, ttl), ct);
	}

	/// <summary>Releases a lease: one held for the user's own workspace, or any as an admin.</summary>
	public async Task ReleaseLeaseAsync(string id, CancellationToken ct = default)
	{
		var (repo, _) = await OpenAsync(ct);
		EnsureCanChange();
		var lease = await repo.Leases.GetAsync(id, ct) ?? throw new VersionException($"No live lease '{id}'.");
		var owner = await repo.FindWorkspaceAsync(lease.Owner, ct);
		if (!user.IsAdmin && owner?.Record.Owner != user.Name)
		{
			throw new StudioForbiddenException($"The lease on '{lease.Prefix}' is held for workspace '{owner?.Name ?? lease.Owner}' of '{owner?.Record.Owner ?? "unknown"}'; only its owner or an admin may release it.");
		}

		await repo.Leases.ReleaseAsync(lease.Id, lease.Token, ct);
	}
}
