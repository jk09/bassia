namespace Bassia.Core.Storage;

using Bassia.Core.Model;

/// <summary>A workspace as the index knows it: what its record said at <see cref="Head"/>, and its reconciled candidate, if any.</summary>
public sealed record WorkspaceSummary(
	string Id,
	string Name,
	string Status,
	string Owner,
	string? Parent,
	long BaseSeq,
	DateTimeOffset Created,
	DateTimeOffset? Closed,
	ObjectId Head,
	ObjectId Base,
	ObjectId Working,
	ObjectId? Candidate = null)
{
	public bool IsOpen => WorkspaceStatus.IsOpen(Status);

	/// <summary>What a record says at a head. Times are cut to the millisecond a stored record keeps, so a summary made from a record in hand equals one read back.</summary>
	public static WorkspaceSummary Of(ObjectId head, WorkspaceRecord record) =>
		new(record.Id, record.Name, record.Status, record.Owner, record.Parent, record.BaseSeq, Milliseconds(record.Created),
			record.Closed is { } closed ? Milliseconds(closed) : null, head, record.Base, record.Working);

	private static DateTimeOffset Milliseconds(DateTimeOffset time) => DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds());
}

/// <summary>A workspace's reconciled candidate as the index knows it: the commits it was computed for, the mainline seq, and the paths the workspace touches.</summary>
public sealed record CandidateSummary(ObjectId Id, ObjectId Base, ObjectId Working, long MainSeq, IReadOnlyList<string> Touched);

/// <summary>
/// The workspace registry: for every workspace head (<c>ws/&lt;id&gt;</c>) what its record says, and for every reconciled
/// candidate (<c>rc/&lt;id&gt;</c>) the paths its workspace touches - so workspaces are listed, found by name and found
/// by the paths a landing changed without reading the operation behind every head. It is derived from the heads and
/// the objects they point at and can be rebuilt from them; it moves the workspace and candidate heads itself, so a
/// head and its row change together.
/// </summary>
public interface IWorkspaceIndex
{
	/// <summary>Compare-and-swap of the head <c>ws/&lt;id&gt;</c> to <paramref name="next"/>'s head, recording the summary with it. A workspace that is no longer open loses its touched paths.</summary>
	ValueTask<HeadUpdate> AdvanceAsync(ObjectId? expected, WorkspaceSummary next, CancellationToken ct = default);

	/// <summary>
	/// Compare-and-swap of the head <c>rc/&lt;workspace&gt;</c> to <paramref name="next"/>, replacing the workspace's
	/// touched paths with it; a null <paramref name="next"/> removes the candidate (<paramref name="expected"/> is then required).
	/// </summary>
	ValueTask<HeadUpdate> AdvanceCandidateAsync(string workspace, ObjectId? expected, CandidateSummary? next, CancellationToken ct = default);

	/// <summary>Removes the head <c>ws/&lt;workspace&gt;</c> if it still points at <paramref name="expected"/>, and everything recorded for it (retention of closed workspaces).</summary>
	ValueTask<bool> RemoveAsync(string workspace, ObjectId expected, CancellationToken ct = default);

	/// <summary>
	/// Open workspaces, oldest first (<paramref name="open"/>), or closed ones, newest first, created before
	/// <paramref name="before"/> when it is given; at most <paramref name="limit"/>.
	/// </summary>
	Task<IReadOnlyList<WorkspaceSummary>> ListAsync(bool open, int limit = int.MaxValue, DateTimeOffset? before = null, CancellationToken ct = default);

	/// <summary>The workspaces with these ids; unknown ids are left out.</summary>
	Task<IReadOnlyList<WorkspaceSummary>> GetAsync(IReadOnlyList<string> ids, CancellationToken ct = default);

	/// <summary>
	/// What a reference to a workspace may mean, oldest first: every workspace named exactly <paramref name="reference"/>,
	/// and the open workspaces whose id or name starts with it.
	/// </summary>
	Task<IReadOnlyList<WorkspaceSummary>> FindAsync(string reference, CancellationToken ct = default);

	/// <summary>
	/// The open workspaces that are not reconciled with mainline at <paramref name="seq"/>, oldest first: those without
	/// a candidate, with a candidate for other commits, or with a touched path (or <paramref name="policyPath"/>) that
	/// changed on mainline after their candidate's seq, up to <paramref name="seq"/>.
	/// </summary>
	Task<IReadOnlyList<string>> StaleAsync(long seq, string policyPath, CancellationToken ct = default);

	/// <summary>The workspace and candidate heads the index does not describe as they are now (after a crash, an older build, or before the first backfill).</summary>
	Task<IReadOnlyList<(HeadName Name, ObjectId Head)>> UnindexedAsync(CancellationToken ct = default);

	/// <summary>Records what a head says without moving it (backfill and repair): a workspace and, when it has one, its candidate.</summary>
	ValueTask RecordAsync(WorkspaceSummary summary, CandidateSummary? candidate, CancellationToken ct = default);

	/// <summary>Rows whose workspace head is gone are dropped (after heads were deleted without the index).</summary>
	ValueTask PruneAsync(CancellationToken ct = default);
}

/// <summary>The registry in memory, over a view store whose workspace and candidate heads it moves under one lock.</summary>
public sealed class MemoryWorkspaceIndex(IViewStore views, IPathIndex paths) : IWorkspaceIndex
{
	private sealed record Entry(WorkspaceSummary Summary, CandidateSummary? Candidate);

	private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
	private readonly SemaphoreSlim gate = new(1, 1);

	private async Task<T> LockedAsync<T>(Func<Task<T>> work, CancellationToken ct)
	{
		await gate.WaitAsync(ct);
		try
		{
			return await work();
		}
		finally
		{
			gate.Release();
		}
	}

	public async ValueTask<HeadUpdate> AdvanceAsync(ObjectId? expected, WorkspaceSummary next, CancellationToken ct = default) => await LockedAsync(async () =>
	{
		var update = await views.AdvanceHeadAsync(HeadName.Workspace(next.Id), expected, next.Head, ct);
		if (update.Success)
		{
			var candidate = entries.TryGetValue(next.Id, out var entry) ? entry.Candidate : null;
			entries[next.Id] = new Entry(next with { Candidate = candidate?.Id }, next.IsOpen ? candidate : candidate is null ? null : candidate with { Touched = [] });
		}

		return update;
	}, ct);

	public async ValueTask<HeadUpdate> AdvanceCandidateAsync(string workspace, ObjectId? expected, CandidateSummary? next, CancellationToken ct = default) => await LockedAsync(async () =>
	{
		var name = HeadName.Candidate(workspace);
		HeadUpdate update;
		if (next is null)
		{
			var removed = expected is { } head && await views.DeleteHeadAsync(name, head, ct);
			update = new HeadUpdate(removed, removed ? null : await views.GetHeadAsync(name, ct));
		}
		else
		{
			update = await views.AdvanceHeadAsync(name, expected, next.Id, ct);
		}

		if (update.Success && entries.TryGetValue(workspace, out var entry))
		{
			entries[workspace] = new Entry(entry.Summary with { Candidate = next?.Id }, next is null || entry.Summary.IsOpen ? next : next with { Touched = [] });
		}

		return update;
	}, ct);

	public async ValueTask<bool> RemoveAsync(string workspace, ObjectId expected, CancellationToken ct = default) => await LockedAsync(async () =>
	{
		if (!await views.DeleteHeadAsync(HeadName.Workspace(workspace), expected, ct))
		{
			return false;
		}

		entries.Remove(workspace);
		return true;
	}, ct);

	public Task<IReadOnlyList<WorkspaceSummary>> ListAsync(bool open, int limit = int.MaxValue, DateTimeOffset? before = null, CancellationToken ct = default) => LockedAsync(() =>
	{
		var matching = entries.Values.Select(entry => entry.Summary).Where(summary => summary.IsOpen == open && (before is null || summary.Created < before));
		var ordered = open
			? matching.OrderBy(summary => summary.Created).ThenBy(summary => summary.Id, StringComparer.Ordinal)
			: matching.OrderByDescending(summary => summary.Created).ThenByDescending(summary => summary.Id, StringComparer.Ordinal);
		return Task.FromResult<IReadOnlyList<WorkspaceSummary>>(ordered.Take(limit).ToList());
	}, ct);

	public Task<IReadOnlyList<WorkspaceSummary>> GetAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => LockedAsync(() =>
		Task.FromResult<IReadOnlyList<WorkspaceSummary>>(ids.Distinct().Where(entries.ContainsKey).Select(id => entries[id].Summary).ToList()), ct);

	public Task<IReadOnlyList<WorkspaceSummary>> FindAsync(string reference, CancellationToken ct = default) => LockedAsync(() =>
		Task.FromResult<IReadOnlyList<WorkspaceSummary>>(entries.Values.Select(entry => entry.Summary)
			.Where(summary => summary.Name == reference
				|| (summary.IsOpen && (summary.Id.StartsWith(reference, StringComparison.Ordinal) || summary.Name.StartsWith(reference, StringComparison.Ordinal))))
			.OrderBy(summary => summary.Created).ThenBy(summary => summary.Id, StringComparer.Ordinal).ToList()), ct);

	public Task<IReadOnlyList<string>> StaleAsync(long seq, string policyPath, CancellationToken ct = default) => LockedAsync(async () =>
	{
		var stale = new List<string>();
		foreach (var (summary, candidate) in entries.Values.Where(entry => entry.Summary.IsOpen).OrderBy(entry => entry.Summary.Created).ThenBy(entry => entry.Summary.Id, StringComparer.Ordinal))
		{
			if (candidate is null || candidate.Base != summary.Base || candidate.Working != summary.Working)
			{
				stale.Add(summary.Id);
			}
			else if (seq > candidate.MainSeq
				&& (await paths.GetManyAsync(candidate.Touched.Append(policyPath).ToList(), seq, ct)).Any(version => version is not null && version.Seq > candidate.MainSeq))
			{
				stale.Add(summary.Id);
			}
		}

		return (IReadOnlyList<string>)stale;
	}, ct);

	public Task<IReadOnlyList<(HeadName Name, ObjectId Head)>> UnindexedAsync(CancellationToken ct = default) => LockedAsync(async () =>
	{
		var unindexed = new List<(HeadName, ObjectId)>();
		await foreach (var (name, head) in views.ListHeadsAsync(HeadName.WorkspacePrefix, ct))
		{
			if (!entries.TryGetValue(name.WorkspaceId, out var entry) || entry.Summary.Head != head)
			{
				unindexed.Add((name, head));
			}
		}

		await foreach (var (name, head) in views.ListHeadsAsync(HeadName.CandidatePrefix, ct))
		{
			if (!entries.TryGetValue(name.WorkspaceId, out var entry) || entry.Summary.Candidate != head)
			{
				unindexed.Add((name, head));
			}
		}

		return (IReadOnlyList<(HeadName, ObjectId)>)unindexed;
	}, ct);

	public async ValueTask RecordAsync(WorkspaceSummary summary, CandidateSummary? candidate, CancellationToken ct = default) => await LockedAsync(() =>
	{
		entries[summary.Id] = new Entry(summary with { Candidate = candidate?.Id }, candidate is null || summary.IsOpen ? candidate : candidate with { Touched = [] });
		return Task.FromResult(0);
	}, ct);

	public async ValueTask PruneAsync(CancellationToken ct = default) => await LockedAsync(async () =>
	{
		foreach (var id in entries.Keys.ToList())
		{
			if (await views.GetHeadAsync(HeadName.Workspace(id), ct) is null)
			{
				entries.Remove(id);
			}
		}

		return 0;
	}, ct);
}
