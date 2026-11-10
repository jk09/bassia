namespace Bassia.Core.Version;

using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>A lease presented at landing: its id and the fencing token it was granted with.</summary>
public sealed record LeaseToken(string LeaseId, long Token);

/// <summary>
/// A request to land a workspace's working commit on mainline. <see cref="Strict"/> also rejects the landing when a
/// path under <see cref="ReadSet"/> changed since the workspace's base. <see cref="Keep"/> leaves the workspace open on
/// the new head with a fresh change; otherwise it is marked landed. <see cref="DryRun"/> only reports what would happen.
/// </summary>
public sealed record LandRequest(
	string Workspace,
	bool Strict = false,
	IReadOnlyList<string>? ReadSet = null,
	IReadOnlyList<LeaseToken>? Leases = null,
	bool Keep = false,
	bool DryRun = false,
	string? Agent = null);

/// <summary>Why a path keeps a workspace from landing, and what its policy did or would do.</summary>
public sealed record PathConflict(string Path, string Case, string Policy, string Detail);

/// <summary>Outcomes of a landing.</summary>
public static class LandOutcome
{
	public const string Landed = "landed";
	public const string WouldLand = "would-land";
	public const string Nothing = "nothing";
	public const string NeedsReplay = "needs-replay";
	public const string Conflict = "conflict";
	public const string Rejected = "rejected";

	public static bool IsSuccess(string outcome) => outcome is Landed or WouldLand or Nothing;
}

/// <summary>
/// The outcome of a landing: on success the new seq and commit; otherwise the conflicting paths and the current
/// mainline head the workspace has to catch up with (sync, or replay on it). <see cref="Reconciled"/>: the landing
/// applied the workspace's reconciled candidate instead of planning (the fast path).
/// </summary>
public sealed record LandResult(
	string Outcome,
	string Message,
	long? Seq,
	ObjectId? Commit,
	long MainSeq,
	ObjectId MainCommit,
	IReadOnlyList<PathConflict> Conflicts,
	int Touched,
	IReadOnlyList<string> Merged,
	IReadOnlyList<string> Warnings,
	int Attempts,
	bool Reconciled = false);

/// <summary>A landing as a watcher sees it: the new seq and the top-level prefixes it touched.</summary>
public sealed record LandedEvent(long Seq, ObjectId Commit, string Workspace, IReadOnlyList<string> Prefixes, DateTimeOffset Time);

/// <summary>Lands workspaces on mainline: in-process for a local monorepo, on the server for a clone.</summary>
public interface ILanding
{
	Task<LandResult> LandAsync(LandRequest request, CancellationToken ct = default);
}

/// <summary>
/// The landing service: the only writer of <c>main</c> and of the path index. A landing compares the workspace's
/// touched paths with the path index since the workspace's base seq, applies its changes onto the current mainline
/// tree, writes the rebased commit (same ChangeId, new CommitId) and advances <c>main</c> by compare-and-swap; a lost race
/// repeats from the comparison, which costs only the touched paths.
///
/// When the workspace has a current reconciled candidate (<see cref="Reconciler"/>) - computed for its commits, and
/// none of its paths (nor <c>.bassia.toml</c>) changed on mainline since - the landing skips the diff, the conflict
/// policies, the merge drivers and validation: it applies the candidate's changes, checks leases and advances
/// <c>main</c>. A machine-merged candidate is used only on the very mainline it was validated on.
/// </summary>
public sealed class LandingService : ILanding
{
	private const int MaxAttempts = 100;

	private readonly SemaphoreSlim gate = new(1, 1);

	private readonly Repository repo;

	public LandingService(Repository repo, IMergeDriver? semantic = null, IValidationHook? validator = null)
	{
		this.repo = repo;
		Planner = new LandingPlanner(repo, semantic, validator);
		Reconciler = new Reconciler(repo, Planner);
	}

	/// <summary>The decision a landing makes, shared with the reconciler.</summary>
	public LandingPlanner Planner { get; }

	/// <summary>Keeps workspaces reconciled ahead of their landings, with the same planner.</summary>
	public Reconciler Reconciler { get; }

	public event Action<LandedEvent>? Landed;

	/// <summary>What a landing decided before it takes the gate: the plan for a workspace's commits on one mainline.</summary>
	private sealed record Prepared(WorkspaceRecord Record, MainState Main, RepoPolicy Policy, IReadOnlyList<string> Paths, LandingPlan Plan, ObjectId Tree, bool Reconciled);

	public async Task<LandResult> LandAsync(LandRequest request, CancellationToken ct = default)
	{
		for (var attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			// Deciding - the diff, the conflict policies, merge drivers and validation - holds no lock: a landing that
			// validates for a minute keeps no other landing waiting.
			var (result, prepared) = await PrepareAsync(request, attempt, ct);
			if (result is not null && request.DryRun)
			{
				return result;
			}

			// One landing at a time commits in this process; the compare-and-swap keeps several processes or nodes correct.
			await gate.WaitAsync(ct);
			try
			{
				result ??= await CommitAsync(request, prepared!, attempt, ct);
				if (result is not null)
				{
					await RecordAsync(request, result, ct);
					return result;
				}
			}
			finally
			{
				gate.Release();
			}

			// Mainline (or the workspace) moved under the plan: decide again.
		}

		var main = await repo.GetMainAsync(ct);
		var gaveUp = new LandResult(LandOutcome.Rejected, $"Mainline kept moving; gave up after {MaxAttempts} attempts.", null, null, main.Seq, main.CommitId, [], 0, [], [], MaxAttempts);
		if (!request.DryRun)
		{
			await RecordAsync(request, gaveUp, ct);
		}

		return gaveUp;
	}

	/// <summary>
	/// Decides a landing on the current mainline, without a lock: a result when it is refused, has nothing to land or
	/// is a dry run; otherwise the plan to commit.
	/// </summary>
	private async Task<(LandResult? Result, Prepared? Prepared)> PrepareAsync(LandRequest request, int attempt, CancellationToken ct)
	{
		var workspace = await repo.ResolveWorkspaceAsync(request.Workspace, ct);
		var record = workspace.Record;
		var main = await repo.GetMainAsync(ct);
		LandResult Fail(string outcome, string message, IReadOnlyList<PathConflict>? conflicts = null, int touchedCount = 0, int attempts = 0) =>
			new(outcome, message, null, null, main.Seq, main.CommitId, conflicts ?? [], touchedCount, [], [], attempts);

		if (await Planner.RefusalAsync(workspace, ct) is { } refusal)
		{
			return (Fail(refusal.Outcome, refusal.Message), null);
		}

		// A candidate for exactly these commits that would land replaces the diff and the planning below.
		var candidate = await Reconciler.GetAsync(record.Id, ct);
		if (request.Strict || !Reconciler.Matches(candidate, record) || candidate!.Outcome != LandOutcome.WouldLand)
		{
			candidate = null;
		}

		IReadOnlyList<TreeChange>? touched = null;
		async Task<LandResult?> PlanFromScratchAsync()
		{
			touched = await Planner.TouchedAsync(record, ct);
			return LandingPlanner.RefusalFor(record, touched) is { } refused
				? refused.Outcome == LandOutcome.Nothing
					? new LandResult(LandOutcome.Nothing, refused.Message, null, null, main.Seq, main.CommitId, [], 0, [], [], 0)
					: Fail(refused.Outcome, refused.Message, refused.Conflicts, touched.Count)
				: null;
		}

		if (candidate is null && await PlanFromScratchAsync() is { } early)
		{
			return (early, null);
		}

		await repo.EnsureIndexedAsync(main, ct);
		var policy = await RepoPolicy.LoadAsync(repo.Trees, main.Tree, ct);

		// The fast path: the candidate holds while none of its paths moved since it was reconciled.
		LandingPlan? plan = null;
		if (candidate is not null)
		{
			if (await Reconciler.MovedSinceAsync(candidate, main.Seq, ct) || (candidate.MachineMerged && candidate.MainSeq != main.Seq))
			{
				candidate = null;
			}
			else
			{
				var changes = candidate.Changes.Select(change => new PathChange(change.Path, change.Leaf)).ToList();
				try
				{
					var edited = await repo.Trees.EditAsync(main.Tree, changes, ct);
					plan = new LandingPlan(LandOutcome.WouldLand, edited == main.Tree
							? $"Workspace '{record.Name}' would land without changing mainline: its changes are already there."
							: $"Workspace '{record.Name}' can land on seq {main.Seq}: {candidate.Touched.Count} touched path(s), no conflicts (reconciled at seq {candidate.MainSeq}).",
						changes, [], candidate.Merged, candidate.MachineMerged, edited);
				}
				catch (PathClashException)
				{
					candidate = null;
				}
			}

			if (candidate is null && await PlanFromScratchAsync() is { } refused)
			{
				return (refused, null);
			}
		}

		var paths = candidate?.Touched ?? touched!.Select(change => change.Path).ToList();
		var warnings = new List<string>();
		if (request.DryRun || request.Leases is { Count: > 0 })
		{
			// A landing that presents leases, or only asks, hears about them before any merge or validation runs.
			var problems = await CheckLeasesAsync(record, request, paths, policy, warnings, ct);
			if (problems.Count > 0)
			{
				return (Fail(LandOutcome.Rejected, "The landing does not hold the leases it needs.", problems, paths.Count, attempt), null);
			}
		}

		plan ??= await Planner.PlanAsync(record, touched!, main, policy, request.Strict, request.ReadSet, ct);
		if (plan.Tree is not { } tree)
		{
			return (Fail(plan.Outcome, plan.Message, plan.Conflicts, paths.Count, attempt), null);
		}

		return request.DryRun
			? (new LandResult(LandOutcome.WouldLand, plan.Message, null, null, main.Seq, main.CommitId, [], paths.Count, plan.Merged, warnings, attempt, candidate is not null), null)
			: (null, new Prepared(record, main, policy, paths, plan, tree, candidate is not null));
	}

	/// <summary>
	/// Commits a plan, under the gate: on the mainline it was made for, or on a newer one that changed none of its
	/// paths (nor the policy file) - the plan's changes then apply as they are. Null when the plan no longer holds
	/// (a path moved, a machine-merged or strict plan met a newer mainline, the workspace changed, a lost race): the
	/// landing decides again.
	/// </summary>
	private async Task<LandResult?> CommitAsync(LandRequest request, Prepared prepared, int attempt, CancellationToken ct)
	{
		// A compare-and-swap lost to another node is tried again here: the plan holds or not on the mainline that won.
		for (var race = 0; race < CommitRaces; race++)
		{
			if (await CommitOnceAsync(request, prepared, attempt, ct) is var (result, lostRace) && !lostRace)
			{
				return result;
			}
		}

		return null;
	}

	/// <summary>How often one plan is committed again after losing the compare-and-swap of main, before it is planned again.</summary>
	private const int CommitRaces = 8;

	private async Task<(LandResult? Result, bool LostRace)> CommitOnceAsync(LandRequest request, Prepared prepared, int attempt, CancellationToken ct)
	{
		var record = prepared.Record;
		var plan = prepared.Plan;
		var paths = prepared.Paths;
		var main = await repo.GetMainAsync(ct);
		var tree = prepared.Tree;
		if (main.Head != prepared.Main.Head)
		{
			// A machine-merged tree was validated on the mainline it was planned on, and a strict landing reads more than it touches.
			if (plan.MachineMerged || request.Strict)
			{
				return (null, false);
			}

			await repo.EnsureIndexedAsync(main, ct);
			var versions = await repo.Paths.GetManyAsync(paths.Append(RepoPolicy.FileName).ToList(), main.Seq, ct);
			if (versions.Any(version => version is not null && version.Seq > prepared.Main.Seq))
			{
				return (null, false);
			}

			try
			{
				tree = await repo.Trees.EditAsync(main.Tree, plan.Changes, ct);
			}
			catch (PathClashException)
			{
				return (null, false);
			}
		}

		// The workspace must still be what was planned: not landed by another call, not snapshotted since.
		if (await repo.FindWorkspaceAsync(record.Id, ct) is not { IsOpen: true } current || current.Record.Working != record.Working || current.Record.Base != record.Base)
		{
			return (null, false);
		}

		LandResult Fail(string outcome, string message, IReadOnlyList<PathConflict> conflicts) =>
			new(outcome, message, null, null, main.Seq, main.CommitId, conflicts, paths.Count, [], [], attempt);

		// Leases are checked against the policy of the mainline this lands on (the policy file did not move, or the
		// plan would not hold), right before main moves: merging and validation took time.
		var warnings = new List<string>();
		var leaseProblems = await CheckLeasesAsync(record, request, paths, prepared.Policy, warnings, ct);
		if (leaseProblems.Count > 0)
		{
			return (Fail(LandOutcome.Rejected, "The landing does not hold the leases it needs.", leaseProblems), false);
		}

		var working = await repo.Objects.ReadCommitAsync(record.Working, ct);
		var commit = working with
		{
			Tree = tree,
			Parents = [main.CommitId],
			Time = repo.Clock(),
			MachineMerged = working.MachineMerged || plan.MachineMerged
		};
		var commitId = await repo.Objects.WriteAsync(commit, ct);
		var seq = main.Seq + 1;
		var view = await repo.Objects.WriteAsync(new View(new Dictionary<string, ObjectId> { ["main"] = commitId }), ct);
		var op = await repo.Objects.WriteAsync(new Operation([main.Head], view, $"land {record.Name}: {FirstLine(working.Intent)}", record.Id,
			request.Agent ?? working.Author.Agent, working.Author.Run, repo.Clock(), seq), ct);
		var update = await repo.Views.AdvanceHeadAsync(HeadName.Main, main.Head, op, ct);
		if (!update.Success)
		{
			return (null, true);
		}

		// Index through the operation log's catch-up rather than applying this seq alone: with several landing
		// services (server nodes), the landing before this one may not be indexed yet, and seqs go in order.
		var applied = await repo.Trees.DiffAsync(main.Tree, tree, "", ct);
		await repo.EnsureIndexedAsync(await repo.MainAtAsync(op, ct), ct);
		var prefixes = applied.Select(change => PathNames.Split(change.Path)[0]).Distinct(StringComparer.Ordinal).ToList();
		Landed?.Invoke(new LandedEvent(seq, commitId, record.Id, prefixes, repo.Clock()));
		return (new LandResult(LandOutcome.Landed,
			$"Landed workspace '{record.Name}' at seq {seq} ({paths.Count} touched path(s){(plan.Merged.Count > 0 ? $", {plan.Merged.Count} merged" : "")}{(prepared.Reconciled ? ", pre-reconciled" : "")}).",
			seq, commitId, seq, commitId, [], paths.Count, plan.Merged, warnings, attempt, prepared.Reconciled), false);
	}

	/// <summary>
	/// The lease problems of a landing under <paramref name="policy"/>, the policy of the mainline it is about to land on:
	/// presented leases that lapsed, and touched paths under a required prefix that no presented exclusive lease covers.
	/// </summary>
	private async Task<List<PathConflict>> CheckLeasesAsync(WorkspaceRecord record, LandRequest request, IReadOnlyList<string> touched,
		RepoPolicy policy, List<string> warnings, CancellationToken ct)
	{
		var (presented, problems) = await PresentedLeasesAsync(request, ct);
		var held = await repo.Leases.ListAsync(null, ct);
		foreach (var path in touched)
		{
			var covered = presented.Any(lease => lease.Mode == LeaseMode.Exclusive && LeaseRules.Covers(lease.Prefix, path));
			if (policy.EnforcementFor(path) == LeaseEnforcement.LeaseRequired && !covered)
			{
				problems.Add(new PathConflict(path, "lease", LeaseEnforcement.LeaseRequired, "this prefix requires an exclusive lease, and the landing presents none covering it"));
			}

			var others = held.Where(lease => lease.Owner != record.Id && lease.Mode == LeaseMode.Exclusive && LeaseRules.Covers(lease.Prefix, path)).ToList();
			foreach (var other in others)
			{
				warnings.Add($"{path} is under '{other.Prefix}', leased exclusively by workspace {other.Owner}.");
			}
		}

		return problems;
	}

	/// <summary>The presented leases that are still live with their fencing tokens, and a problem for each that is not.</summary>
	private async Task<(List<Lease> Live, List<PathConflict> Lapsed)> PresentedLeasesAsync(LandRequest request, CancellationToken ct)
	{
		var live = new List<Lease>();
		var lapsed = new List<PathConflict>();
		foreach (var token in request.Leases ?? [])
		{
			var lease = await repo.Leases.GetAsync(token.LeaseId, ct);
			if (lease is null || lease.Token != token.Token)
			{
				lapsed.Add(new PathConflict(lease?.Prefix ?? token.LeaseId, "lease", LeaseEnforcement.LeaseRequired,
					$"lease {token.LeaseId} with token {token.Token} expired or was granted to someone else"));
			}
			else
			{
				live.Add(lease);
			}
		}

		return (live, lapsed);
	}

	private async Task RecordAsync(LandRequest request, LandResult result, CancellationToken ct)
	{
		if (result.Outcome == LandOutcome.Nothing && !request.Keep)
		{
			return;
		}

		var workspace = await repo.ResolveWorkspaceAsync(request.Workspace, ct);
		await repo.UpdateWorkspaceAsync(workspace.Id, async current =>
		{
			var record = current.Record;
			var land = new LandRecord(result.Outcome, repo.Clock(), result.Seq, result.Commit,
				result.Conflicts.Select(conflict => conflict.Path).ToList(), result.Message);
			if (result.Outcome != LandOutcome.Landed)
			{
				var status = result.Outcome == LandOutcome.NeedsReplay ? WorkspaceStatus.NeedsReplay : record.Status;
				if (!WorkspaceStatus.IsOpen(record.Status))
				{
					return null;
				}

				return (record with { LastLand = land, Status = status == WorkspaceStatus.NeedsReplay || record.Status == WorkspaceStatus.Open ? status : record.Status },
					$"land refused: {result.Outcome}");
			}

			if (request.Keep)
			{
				var landed = await repo.Objects.ReadCommitAsync(result.Commit!.Value, ct);
				var fresh = await repo.Objects.WriteAsync(new Commit(landed.Tree, [result.Commit.Value], ChangeId.New(), landed.Author, "", repo.Clock(), landed.Recipe), ct);
				return (record with
				{
					Base = result.Commit.Value,
					BaseSeq = result.Seq!.Value,
					Working = fresh,
					Parent = null,
					Status = WorkspaceStatus.Open,
					LastLand = land
				}, $"landed at seq {result.Seq}; continue with a new change");
			}

			return (record with { Status = WorkspaceStatus.Landed, LastLand = land, Closed = repo.Clock() }, $"landed at seq {result.Seq}");
		}, request.Agent, null, ct);
	}

	private static string FirstLine(string text)
	{
		var line = text.Split('\n')[0].Trim();
		return line.Length == 0 ? "(no intent)" : line;
	}
}
