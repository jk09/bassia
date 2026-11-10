namespace Bassia.Core.Version;

using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>
/// What a landing would do with a workspace on one mainline: the outcome and message, the changes it would apply
/// (conflict policies and merge drivers already resolved), what blocks it, the merged paths, and the tree it would land
/// (null when it cannot land).
/// </summary>
public sealed record LandingPlan(
	string Outcome,
	string Message,
	IReadOnlyList<PathChange> Changes,
	IReadOnlyList<PathConflict> Conflicts,
	IReadOnlyList<string> Merged,
	bool MachineMerged,
	ObjectId? Tree);

/// <summary>
/// The decision of a landing, shared by <see cref="LandingService"/> and the <see cref="Reconciler"/>: a workspace's
/// touched paths compared with mainline's path index since the workspace's base, conflict policies and merge drivers
/// applied, and a machine-merged tree validated. Both use this one planner, so a reconciled candidate says by
/// construction what a landing on the same mainline would decide.
/// </summary>
public sealed class LandingPlanner(Repository repo, IMergeDriver? semantic = null, IValidationHook? validator = null)
{
	/// <summary>
	/// What refuses a workspace before its paths are compared: not open, or stacked on a workspace that has not
	/// landed. Null when it may go on.
	/// </summary>
	public async Task<(string Outcome, string Message)?> RefusalAsync(WorkspaceState workspace, CancellationToken ct)
	{
		var record = workspace.Record;
		if (!workspace.IsOpen)
		{
			return (LandOutcome.Rejected, $"Workspace '{record.Name}' is {record.Status}; only an open workspace can land.");
		}

		if (record.Parent is { } parentId && await repo.FindWorkspaceAsync(parentId, ct) is { IsOpen: true } parent)
		{
			return (LandOutcome.Rejected, $"Workspace '{record.Name}' is stacked on '{parent.Name}', which has not landed; land '{parent.Name}' first.");
		}

		return null;
	}

	/// <summary>The paths a workspace changed against its base.</summary>
	public async Task<IReadOnlyList<TreeChange>> TouchedAsync(WorkspaceRecord record, CancellationToken ct)
	{
		var working = await repo.Objects.ReadCommitAsync(record.Working, ct);
		var baseCommit = await repo.Objects.ReadCommitAsync(record.Base, ct);
		return await repo.Trees.DiffAsync(baseCommit.Tree, working.Tree, "", ct);
	}

	/// <summary>What refuses a workspace for its touched paths alone: nothing changed, or unresolved conflicts. Null when it may go on.</summary>
	public static (string Outcome, string Message, IReadOnlyList<PathConflict> Conflicts)? RefusalFor(WorkspaceRecord record, IReadOnlyList<TreeChange> touched)
	{
		if (touched.Count == 0)
		{
			return (LandOutcome.Nothing, $"Workspace '{record.Name}' has no changes against its base.", []);
		}

		var conflicted = touched.Where(change => change.New?.Kind == EntryKind.Conflict).Select(change => change.Path).ToList();
		return conflicted.Count > 0
			? (LandOutcome.Conflict, $"Workspace '{record.Name}' holds {conflicted.Count} unresolved conflict(s); mainline refuses conflicts. Resolve them, then land.",
				conflicted.Select(path => new PathConflict(path, "unresolved", ConflictPolicy.Conflict, "the workspace stores a conflict here")).ToList())
			: null;
	}

	/// <summary>The plan for landing <paramref name="touched"/> on <paramref name="main"/> under <paramref name="policy"/>.</summary>
	public async Task<LandingPlan> PlanAsync(WorkspaceRecord record, IReadOnlyList<TreeChange> touched, MainState main, RepoPolicy policy,
		bool strict, IReadOnlyList<string>? readSet, CancellationToken ct)
	{
		var resolver = new ConflictResolver(repo.Objects, policy, validator is null ? null : semantic);
		var conflicts = new List<PathConflict>();
		var merged = new List<string>();
		var changes = new List<PathChange>();

		if (strict)
		{
			foreach (var prefix in readSet ?? [])
			{
				await foreach (var moved in repo.Paths.ChangedSinceAsync(PathNames.Normalize(prefix), record.BaseSeq, ct))
				{
					if (moved.Seq <= main.Seq)
					{
						conflicts.Add(new PathConflict(moved.Path, "read-set", "strict", $"changed at seq {moved.Seq}, after the workspace's base seq {record.BaseSeq}"));
					}
				}
			}
		}

		// Every touched path's mainline version in a few round trips, not one per path.
		var versions = await repo.Paths.GetManyAsync(touched.Select(change => change.Path).ToList(), main.Seq, ct);
		for (var index = 0; index < touched.Count; index++)
		{
			var change = touched[index];
			var version = versions[index];
			if (version is null || version.Seq <= record.BaseSeq)
			{
				changes.Add(new PathChange(change.Path, change.New));
				continue;
			}

			// The path moved on mainline after the workspace's base.
			var mainline = version.Leaf;
			if (Trees.SameContent(mainline, change.New))
			{
				continue;
			}

			if (Trees.SameContent(mainline, change.Old))
			{
				changes.Add(new PathChange(change.Path, change.New));
				continue;
			}

			var kind = policy.PolicyFor(change.Path);
			var resolution = await resolver.ResolveAsync(change.Path, change.Old, change.New, mainline, ct);
			if (resolution.Resolved)
			{
				changes.Add(new PathChange(change.Path, resolution.Leaf));
				merged.Add($"{change.Path} ({resolution.How})");
			}
			else
			{
				conflicts.Add(new PathConflict(change.Path, "both-rewrote", kind,
					$"mainline changed it at seq {version.Seq} after the workspace's base seq {record.BaseSeq}"));
			}
		}

		LandingPlan Refused(string outcome, string message, IReadOnlyList<PathConflict> blocking) =>
			new(outcome, message, changes, blocking, merged, resolver.MachineMerged, null);

		if (conflicts.Count > 0)
		{
			var replay = conflicts.Any(conflict => conflict.Policy is ConflictPolicy.Replay or ConflictPolicy.Semantic or "strict");
			return Refused(replay ? LandOutcome.NeedsReplay : LandOutcome.Conflict,
				replay
					? $"{conflicts.Count} path(s) changed on mainline since seq {record.BaseSeq}; replay the task on seq {main.Seq} ('bassia agent replay') or sync and resolve."
					: $"{conflicts.Count} path(s) conflict with mainline; sync the workspace to store the conflicts, resolve them, then land.",
				conflicts);
		}

		ObjectId tree;
		try
		{
			tree = await repo.Trees.EditAsync(main.Tree, changes, ct);
		}
		catch (PathClashException clash)
		{
			return Refused(LandOutcome.NeedsReplay, clash.Message,
				clash.Paths.Select(path => new PathConflict(path, "clash", ConflictPolicy.Replay, "a file and a directory at the same path")).ToList());
		}

		if (resolver.MachineMerged && validator is not null)
		{
			var (ok, message) = await validator.ValidateAsync(tree, ct);
			if (!ok)
			{
				return Refused(LandOutcome.NeedsReplay, $"The semantic merge did not pass validation: {message}",
					merged.Select(path => new PathConflict(path, "validation", ConflictPolicy.Semantic, message)).ToList());
			}
		}

		return new LandingPlan(LandOutcome.WouldLand, tree == main.Tree
				? $"Workspace '{record.Name}' would land without changing mainline: its changes are already there."
				: $"Workspace '{record.Name}' can land on seq {main.Seq}: {touched.Count} touched path(s), no conflicts.",
			changes, [], merged, resolver.MachineMerged, tree);
	}
}

/// <summary>An open workspace as the conflict overview shows it: its reconciled candidate, if any yet.</summary>
public sealed record WorkspaceReconciliation(
	string Workspace,
	string Name,
	string Owner,
	string Status,
	long BaseSeq,
	bool Running,
	string Outcome,
	string Message,
	long? ReconciledSeq,
	bool Current,
	int Touched,
	IReadOnlyList<PathConflict> Conflicts,
	IReadOnlyList<string> Merged,
	IReadOnlyList<string> Overlaps);

/// <summary>A path that two or more open workspaces change: whoever lands second will meet the first.</summary>
public sealed record PathOverlap(string Path, IReadOnlyList<string> Workspaces);

/// <summary>A path that keeps colliding: open workspaces touching it, current conflicts on it, and refused landings it caused.</summary>
public sealed record HotPath(string Path, int Workspaces, int Conflicts, int Refused);

/// <summary>
/// The live conflict overview: every open workspace reconciled with mainline (what a landing would do now), the
/// overlaps between open workspaces that have not landed, and the hot paths.
/// </summary>
public sealed record ConflictOverview(
	long MainSeq,
	IReadOnlyList<WorkspaceReconciliation> Workspaces,
	IReadOnlyList<PathOverlap> Overlaps,
	IReadOnlyList<HotPath> HotPaths)
{
	public int CanLand => Workspaces.Count(workspace => workspace.Outcome == LandOutcome.WouldLand);

	public int Blocked => Workspaces.Count(workspace => workspace.Outcome is LandOutcome.NeedsReplay or LandOutcome.Conflict or LandOutcome.Rejected);
}

/// <summary>Reads the conflict overview: in-process on a local monorepo or a server, over HTTP on a clone.</summary>
public interface IConflictOverviewSource
{
	Task<ConflictOverview> OverviewAsync(CancellationToken ct = default);
}

/// <summary>
/// Keeps open workspaces reconciled with mainline ahead of their landings. A workspace's <see cref="Candidate"/> is the
/// plan a landing would make on the mainline it was computed against, stored as an object behind the head
/// <c>rc/&lt;workspace&gt;</c> (and so persisted wherever heads are). A landing that finds the candidate current applies its
/// changes without planning again (<see cref="LandingService"/>).
/// </summary>
public sealed class Reconciler(Repository repo, LandingPlanner planner) : IConflictOverviewSource
{
	private const int StoreAttempts = 5;

	/// <summary>Reconciles stale candidates when the overview is read: for a local monorepo, which runs no background work.</summary>
	public bool ReconcileOnRead { get; set; }

	/// <summary>Raised after a candidate was stored (the server publishes it on the watch stream).</summary>
	public event Action<Candidate>? Reconciled;

	/// <summary>The stored candidate of a workspace, or null.</summary>
	public async Task<Candidate?> GetAsync(string workspaceId, CancellationToken ct = default) =>
		await repo.Views.GetHeadAsync(HeadName.Candidate(workspaceId), ct) is { } head ? await repo.Objects.ReadCandidateAsync(head, ct) : null;

	/// <summary>Mainline's current seq.</summary>
	public async Task<long> MainSeqAsync(CancellationToken ct = default) => (await repo.GetMainAsync(ct)).Seq;

	/// <summary>Whether a candidate was computed for the workspace's current base and working commits.</summary>
	public static bool Matches(Candidate? candidate, WorkspaceRecord record) =>
		candidate is not null && candidate.Base == record.Base && candidate.Working == record.Working;

	/// <summary>
	/// Reconciles a workspace with the current mainline and stores the candidate; drops the candidate of a workspace
	/// that is gone or no longer open. Returns the candidate stored (or kept, when a newer one was already there).
	/// </summary>
	public async Task<Candidate?> ReconcileAsync(string workspaceId, CancellationToken ct = default)
	{
		var name = HeadName.Candidate(workspaceId);
		for (var attempt = 1; attempt <= StoreAttempts; attempt++)
		{
			var existing = await repo.Views.GetHeadAsync(name, ct);
			var workspace = await repo.FindWorkspaceAsync(workspaceId, ct);
			if (workspace is null || !workspace.IsOpen)
			{
				if (existing is { } stale)
				{
					await repo.Workspaces.AdvanceCandidateAsync(workspaceId, stale, null, ct);
				}

				return null;
			}

			var main = await repo.GetMainAsync(ct);
			await repo.EnsureIndexedAsync(main, ct);

			// A candidate for the same commits stays good while none of its paths moved; a refusal (stacked on an open
			// parent) is decided again, since the parent's landing moves none of the child's paths.
			if (existing is { } head && await repo.Objects.ReadCandidateAsync(head, ct) is var stored
				&& Matches(stored, workspace.Record) && stored.Outcome != LandOutcome.Rejected && !await MovedSinceAsync(stored, main.Seq, ct))
			{
				return stored;
			}

			var candidate = await ComputeAsync(workspace, main, ct);
			var id = await repo.Objects.WriteAsync(candidate, ct);
			var summary = new CandidateSummary(id, candidate.Base, candidate.Working, candidate.MainSeq, candidate.Touched);
			if ((await repo.Workspaces.AdvanceCandidateAsync(workspaceId, existing, summary, ct)).Success)
			{
				Reconciled?.Invoke(candidate);
				return candidate;
			}
		}

		return await GetAsync(workspaceId, ct);
	}

	/// <summary>What a landing of the workspace would do on <paramref name="main"/>, as a candidate.</summary>
	private async Task<Candidate> ComputeAsync(WorkspaceState workspace, MainState main, CancellationToken ct)
	{
		var record = workspace.Record;
		Candidate Make(string outcome, string message, IReadOnlyList<TreeChange> touched, LandingPlan? plan, IReadOnlyList<PathConflict>? conflicts = null) =>
			new(record.Id, record.Base, record.Working, main.Seq, main.CommitId, outcome, message,
				touched.Select(change => change.Path).ToList(),
				plan?.Changes.Select(change => new CandidateChange(change.Path, change.Leaf)).ToList() ?? [],
				(conflicts ?? plan?.Conflicts ?? []).Select(conflict => new CandidateConflict(conflict.Path, conflict.Case, conflict.Policy, conflict.Detail)).ToList(),
				plan?.Merged ?? [], plan?.MachineMerged ?? false, repo.Clock());

		if (await planner.RefusalAsync(workspace, ct) is { } refusal)
		{
			return Make(refusal.Outcome, refusal.Message, [], null);
		}

		var touched = await planner.TouchedAsync(record, ct);
		if (LandingPlanner.RefusalFor(record, touched) is { } refused)
		{
			return Make(refused.Outcome, refused.Message, touched, null, refused.Conflicts);
		}

		var policy = await RepoPolicy.LoadAsync(repo.Trees, main.Tree, ct);
		var plan = await planner.PlanAsync(record, touched, main, policy, false, null, ct);
		return Make(plan.Outcome, plan.Message, touched, plan);
	}

	/// <summary>
	/// The open workspaces a landing at <paramref name="seq"/> made stale: those whose candidate is missing or does not
	/// match their commits, and those with a touched path (or <c>.bassia.toml</c>) that changed after their candidate's seq.
	/// </summary>
	public async Task<IReadOnlyList<string>> AffectedAsync(long seq, CancellationToken ct = default)
	{
		await repo.EnsureIndexedAsync(await repo.GetMainAsync(ct), ct);
		return await repo.Workspaces.StaleAsync(seq, RepoPolicy.FileName, ct);
	}

	/// <summary>Whether a touched path of the candidate, or the policy file, changed on mainline after the candidate's seq (up to <paramref name="seq"/>).</summary>
	public async Task<bool> MovedSinceAsync(Candidate candidate, long seq, CancellationToken ct = default)
	{
		if (seq <= candidate.MainSeq)
		{
			return false;
		}

		var paths = candidate.Touched.Append(RepoPolicy.FileName).ToList();
		var versions = await repo.Paths.GetManyAsync(paths, seq, ct);
		return versions.Any(version => version is not null && version.Seq > candidate.MainSeq);
	}

	public async Task<ConflictOverview> OverviewAsync(CancellationToken ct = default)
	{
		var main = await repo.GetMainAsync(ct);
		await repo.EnsureIndexedAsync(main, ct);

		// The registry names the open workspaces, their candidates and which of those mainline has overtaken.
		var summaries = await repo.Workspaces.ListAsync(true, ct: ct);
		var stale = (await repo.Workspaces.StaleAsync(main.Seq, RepoPolicy.FileName, ct)).ToHashSet(StringComparer.Ordinal);
		var open = await repo.StatesAsync(summaries, ct);
		var candidates = summaries.ToDictionary(summary => summary.Id, summary => summary.Candidate);
		await repo.Objects.PrefetchAsync(candidates.Values.OfType<ObjectId>(), ct);
		var rows = new List<(WorkspaceState Workspace, Candidate? Candidate, bool Current)>();
		foreach (var workspace in open)
		{
			var candidate = candidates[workspace.Id] is { } stored ? await repo.Objects.ReadCandidateAsync(stored, ct) : null;
			var current = Matches(candidate, workspace.Record) && !stale.Contains(workspace.Id);
			if (!current && ReconcileOnRead)
			{
				candidate = await ReconcileAsync(workspace.Id, ct);
				current = candidate is not null;
			}

			rows.Add((workspace, candidate, current));
		}

		// Overlaps: paths two or more open workspaces change, whoever lands first.
		var byPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var (workspace, candidate, _) in rows)
		{
			foreach (var path in candidate?.Touched ?? [])
			{
				if (!byPath.TryGetValue(path, out var touching))
				{
					byPath[path] = touching = [];
				}

				touching.Add(workspace.Id);
			}
		}

		var overlaps = byPath.Where(pair => pair.Value.Count > 1)
			.Select(pair => new PathOverlap(pair.Key, pair.Value))
			.OrderByDescending(overlap => overlap.Workspaces.Count).ThenBy(overlap => overlap.Path, StringComparer.Ordinal)
			.ToList();
		var overlapping = overlaps.SelectMany(overlap => overlap.Workspaces.Select(id => (id, overlap.Path)))
			.ToLookup(item => item.id, item => item.Path);

		var workspaces = rows.Select(row =>
		{
			var record = row.Workspace.Record;
			var candidate = row.Candidate;
			return new WorkspaceReconciliation(record.Id, record.Name, record.Owner, record.Status, record.BaseSeq, record.Run?.Status == RunStatus.Running,
				candidate?.Outcome ?? "pending", candidate?.Message ?? "Not reconciled yet.", candidate?.MainSeq, row.Current,
				candidate?.Touched.Count ?? 0,
				candidate?.Conflicts.Select(conflict => new PathConflict(conflict.Path, conflict.Case, conflict.Policy, conflict.Detail)).ToList() ?? [],
				candidate?.Merged ?? [], overlapping[record.Id].ToList());
		}).ToList();

		// Hot paths: touched by several open workspaces, in their conflicts now, or behind refused landings.
		var conflicts = workspaces.SelectMany(workspace => workspace.Conflicts.Select(conflict => conflict.Path)).ToLookup(path => path, StringComparer.Ordinal);
		var refused = open.SelectMany(workspace => workspace.Record.LastLand is { Outcome: not LandOutcome.Landed } land ? land.Conflicts : [])
			.ToLookup(path => path, StringComparer.Ordinal);
		var hot = overlaps.Select(overlap => overlap.Path).Concat(conflicts.Select(group => group.Key)).Concat(refused.Select(group => group.Key))
			.Distinct(StringComparer.Ordinal)
			.Select(path => new HotPath(path, byPath.TryGetValue(path, out var touching) ? touching.Count : 0, conflicts[path].Count(), refused[path].Count()))
			.OrderByDescending(path => path.Conflicts + path.Refused + path.Workspaces).ThenBy(path => path.Path, StringComparer.Ordinal)
			.Take(50)
			.ToList();

		return new ConflictOverview(main.Seq, workspaces, overlaps, hot);
	}
}
