namespace Bassia.Core.Version;

using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>
/// The workspace registry for stores that keep none: every question is answered by listing the heads and reading the
/// operation and view behind each. Correct anywhere and as slow as the number of workspaces ever kept - what a
/// <see cref="Repository"/> falls back to when it is given no <see cref="IWorkspaceIndex"/>.
/// </summary>
public sealed class ScanningWorkspaceIndex(IViewStore views, ObjectAccess objects, IPathIndex paths) : IWorkspaceIndex
{
	public ValueTask<HeadUpdate> AdvanceAsync(ObjectId? expected, WorkspaceSummary next, CancellationToken ct = default) =>
		views.AdvanceHeadAsync(HeadName.Workspace(next.Id), expected, next.Head, ct);

	public async ValueTask<HeadUpdate> AdvanceCandidateAsync(string workspace, ObjectId? expected, CandidateSummary? next, CancellationToken ct = default)
	{
		var name = HeadName.Candidate(workspace);
		if (next is not null)
		{
			return await views.AdvanceHeadAsync(name, expected, next.Id, ct);
		}

		var removed = expected is { } head && await views.DeleteHeadAsync(name, head, ct);
		return new HeadUpdate(removed, removed ? null : await views.GetHeadAsync(name, ct));
	}

	public ValueTask<bool> RemoveAsync(string workspace, ObjectId expected, CancellationToken ct = default) =>
		views.DeleteHeadAsync(HeadName.Workspace(workspace), expected, ct);

	private async Task<WorkspaceSummary> ReadAsync(ObjectId head, CancellationToken ct)
	{
		var operation = await objects.ReadOperationAsync(head, ct);
		var view = await objects.ReadViewAsync(operation.View, ct);
		return WorkspaceSummary.Of(head, view.Workspace ?? throw new VersionException($"Operation {head.Short} has no workspace."));
	}

	private async Task<List<WorkspaceSummary>> AllAsync(CancellationToken ct)
	{
		var all = new List<WorkspaceSummary>();
		await foreach (var (_, head) in views.ListHeadsAsync(HeadName.WorkspacePrefix, ct))
		{
			all.Add(await ReadAsync(head, ct));
		}

		return all.OrderBy(summary => summary.Created).ThenBy(summary => summary.Id, StringComparer.Ordinal).ToList();
	}

	private async Task<WorkspaceSummary> WithCandidateAsync(WorkspaceSummary summary, CancellationToken ct) =>
		summary with { Candidate = await views.GetHeadAsync(HeadName.Candidate(summary.Id), ct) };

	public async Task<IReadOnlyList<WorkspaceSummary>> ListAsync(bool open, int limit = int.MaxValue, DateTimeOffset? before = null, CancellationToken ct = default)
	{
		var matching = (await AllAsync(ct)).Where(summary => summary.IsOpen == open && (before is null || summary.Created < before)).ToList();
		if (!open)
		{
			matching.Reverse();
		}

		var page = new List<WorkspaceSummary>();
		foreach (var summary in matching.Take(limit))
		{
			page.Add(open ? await WithCandidateAsync(summary, ct) : summary);
		}

		return page;
	}

	public async Task<IReadOnlyList<WorkspaceSummary>> GetAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
	{
		var found = new List<WorkspaceSummary>();
		foreach (var id in ids.Distinct())
		{
			if (await views.GetHeadAsync(HeadName.Workspace(id), ct) is { } head)
			{
				found.Add(await WithCandidateAsync(await ReadAsync(head, ct), ct));
			}
		}

		return found;
	}

	public async Task<IReadOnlyList<WorkspaceSummary>> FindAsync(string reference, CancellationToken ct = default) =>
		(await AllAsync(ct)).Where(summary => summary.Name == reference
			|| (summary.IsOpen && (summary.Id.StartsWith(reference, StringComparison.Ordinal) || summary.Name.StartsWith(reference, StringComparison.Ordinal)))).ToList();

	public async Task<IReadOnlyList<string>> StaleAsync(long seq, string policyPath, CancellationToken ct = default)
	{
		var stale = new List<string>();
		foreach (var summary in (await AllAsync(ct)).Where(summary => summary.IsOpen))
		{
			var candidate = await views.GetHeadAsync(HeadName.Candidate(summary.Id), ct) is { } head ? await objects.ReadCandidateAsync(head, ct) : null;
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

		return stale;
	}

	public Task<IReadOnlyList<(HeadName Name, ObjectId Head)>> UnindexedAsync(CancellationToken ct = default) =>
		Task.FromResult<IReadOnlyList<(HeadName, ObjectId)>>([]);

	public ValueTask RecordAsync(WorkspaceSummary summary, CandidateSummary? candidate, CancellationToken ct = default) => ValueTask.CompletedTask;

	public ValueTask PruneAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
}
