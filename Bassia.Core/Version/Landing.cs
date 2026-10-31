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
/// mainline head the workspace has to catch up with (sync, or replay on it).
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
	int Attempts);

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
/// </summary>
public sealed class LandingService(Repository repo, IMergeDriver? semantic = null, IValidationHook? validator = null) : ILanding
{
	private const int MaxAttempts = 100;

	private readonly SemaphoreSlim gate = new(1, 1);

	public event Action<LandedEvent>? Landed;

	public async Task<LandResult> LandAsync(LandRequest request, CancellationToken ct = default)
	{
		// One landing at a time in this process; the compare-and-swap keeps several processes or nodes correct.
		await gate.WaitAsync(ct);
		try
		{
			var result = await LandOnceAsync(request, ct);
			if (!request.DryRun)
			{
				await RecordAsync(request, result, ct);
			}

			return result;
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task<LandResult> LandOnceAsync(LandRequest request, CancellationToken ct)
	{
		var workspace = await repo.ResolveWorkspaceAsync(request.Workspace, ct);
		var record = workspace.Record;
		var main = await repo.GetMainAsync(ct);
		LandResult Fail(string outcome, string message, IReadOnlyList<PathConflict>? conflicts = null, int touched = 0, int attempts = 0) =>
			new(outcome, message, null, null, main.Seq, main.CommitId, conflicts ?? [], touched, [], [], attempts);

		if (!workspace.IsOpen)
		{
			return Fail(LandOutcome.Rejected, $"Workspace '{record.Name}' is {record.Status}; only an open workspace can land.");
		}

		if (record.Parent is { } parentId && await repo.FindWorkspaceAsync(parentId, ct) is { IsOpen: true } parent)
		{
			return Fail(LandOutcome.Rejected, $"Workspace '{record.Name}' is stacked on '{parent.Name}', which has not landed; land '{parent.Name}' first.");
		}

		var working = await repo.Objects.ReadCommitAsync(record.Working, ct);
		var baseCommit = await repo.Objects.ReadCommitAsync(record.Base, ct);
		var touched = await repo.Trees.DiffAsync(baseCommit.Tree, working.Tree, "", ct);
		if (touched.Count == 0)
		{
			return new LandResult(LandOutcome.Nothing, $"Workspace '{record.Name}' has no changes against its base.", null, null, main.Seq, main.CommitId, [], 0, [], [], 0);
		}

		var conflicted = touched.Where(change => change.New?.Kind == EntryKind.Conflict).Select(change => change.Path).ToList();
		if (conflicted.Count > 0)
		{
			return Fail(LandOutcome.Conflict, $"Workspace '{record.Name}' holds {conflicted.Count} unresolved conflict(s); mainline refuses conflicts. Resolve them, then land.",
				conflicted.Select(path => new PathConflict(path, "unresolved", ConflictPolicy.Conflict, "the workspace stores a conflict here")).ToList(), touched.Count);
		}

		var warnings = new List<string>();
		var leaseProblems = await CheckLeasesAsync(record, request, touched, warnings, ct);
		if (leaseProblems.Count > 0)
		{
			return Fail(LandOutcome.Rejected, "The landing does not hold the leases it needs.", leaseProblems, touched.Count);
		}

		for (var attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			main = await repo.GetMainAsync(ct);
			await repo.EnsureIndexedAsync(main, ct);
			var policy = await RepoPolicy.LoadAsync(repo.Trees, main.Tree, ct);
			var resolver = new ConflictResolver(repo.Objects, policy, validator is null ? null : semantic);
			var conflicts = new List<PathConflict>();
			var merged = new List<string>();
			var changes = new List<PathChange>();

			if (request.Strict)
			{
				foreach (var prefix in request.ReadSet ?? [])
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

			foreach (var change in touched)
			{
				var version = await repo.Paths.GetAsync(change.Path, main.Seq, ct);
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

			if (conflicts.Count > 0)
			{
				var replay = conflicts.Any(conflict => conflict.Policy is ConflictPolicy.Replay or ConflictPolicy.Semantic or "strict");
				return Fail(replay ? LandOutcome.NeedsReplay : LandOutcome.Conflict,
					replay
						? $"{conflicts.Count} path(s) changed on mainline since seq {record.BaseSeq}; replay the task on seq {main.Seq} ('bassia agent replay') or sync and resolve."
						: $"{conflicts.Count} path(s) conflict with mainline; sync the workspace to store the conflicts, resolve them, then land.",
					conflicts, touched.Count, attempt);
			}

			ObjectId tree;
			try
			{
				tree = await repo.Trees.EditAsync(main.Tree, changes, ct);
			}
			catch (PathClashException clash)
			{
				return Fail(LandOutcome.NeedsReplay, clash.Message,
					clash.Paths.Select(path => new PathConflict(path, "clash", ConflictPolicy.Replay, "a file and a directory at the same path")).ToList(), touched.Count, attempt);
			}

			if (resolver.MachineMerged && validator is not null)
			{
				var (ok, message) = await validator.ValidateAsync(tree, ct);
				if (!ok)
				{
					return Fail(LandOutcome.NeedsReplay, $"The semantic merge did not pass validation: {message}",
						merged.Select(path => new PathConflict(path, "validation", ConflictPolicy.Semantic, message)).ToList(), touched.Count, attempt);
				}
			}

			if (request.DryRun)
			{
				return new LandResult(LandOutcome.WouldLand, tree == main.Tree
					? $"Workspace '{record.Name}' would land without changing mainline: its changes are already there."
					: $"Workspace '{record.Name}' can land on seq {main.Seq}: {touched.Count} touched path(s), no conflicts.",
					null, null, main.Seq, main.CommitId, [], touched.Count, merged, warnings, attempt);
			}

			var commit = working with
			{
				Tree = tree,
				Parents = [main.CommitId],
				Time = repo.Clock(),
				MachineMerged = working.MachineMerged || resolver.MachineMerged
			};
			var commitId = await repo.Objects.WriteAsync(commit, ct);
			var seq = main.Seq + 1;
			var view = await repo.Objects.WriteAsync(new View(new Dictionary<string, ObjectId> { ["main"] = commitId }), ct);
			var op = await repo.Objects.WriteAsync(new Operation([main.Head], view, $"land {record.Name}: {FirstLine(working.Intent)}", record.Id,
				request.Agent ?? working.Author.Agent, working.Author.Run, repo.Clock(), seq), ct);
			var update = await repo.Views.AdvanceHeadAsync(HeadName.Main, main.Head, op, ct);
			if (!update.Success)
			{
				continue;
			}

			// Index through the operation log's catch-up rather than applying this seq alone: with several landing
			// services (server nodes), the landing before this one may not be indexed yet, and seqs go in order.
			var applied = await repo.Trees.DiffAsync(main.Tree, tree, "", ct);
			await repo.EnsureIndexedAsync(await repo.MainAtAsync(op, ct), ct);
			var prefixes = applied.Select(change => PathNames.Split(change.Path)[0]).Distinct(StringComparer.Ordinal).ToList();
			Landed?.Invoke(new LandedEvent(seq, commitId, record.Id, prefixes, repo.Clock()));
			return new LandResult(LandOutcome.Landed, $"Landed workspace '{record.Name}' at seq {seq} ({touched.Count} touched path(s){(merged.Count > 0 ? $", {merged.Count} merged" : "")}).",
				seq, commitId, seq, commitId, [], touched.Count, merged, warnings, attempt);
		}

		return Fail(LandOutcome.Rejected, $"Mainline kept moving; gave up after {MaxAttempts} attempts.", null, touched.Count, MaxAttempts);
	}

	private async Task<List<PathConflict>> CheckLeasesAsync(WorkspaceRecord record, LandRequest request, IReadOnlyList<TreeChange> touched,
		List<string> warnings, CancellationToken ct)
	{
		var problems = new List<PathConflict>();
		var presented = new List<Lease>();
		foreach (var token in request.Leases ?? [])
		{
			var lease = await repo.Leases.GetAsync(token.LeaseId, ct);
			if (lease is null || lease.Token != token.Token)
			{
				problems.Add(new PathConflict(lease?.Prefix ?? token.LeaseId, "lease", LeaseEnforcement.LeaseRequired,
					$"lease {token.LeaseId} with token {token.Token} expired or was granted to someone else"));
			}
			else
			{
				presented.Add(lease);
			}
		}

		var main = await repo.GetMainAsync(ct);
		var policy = await RepoPolicy.LoadAsync(repo.Trees, main.Tree, ct);
		var held = await repo.Leases.ListAsync(null, ct);
		foreach (var change in touched)
		{
			var covered = presented.Any(lease => lease.Mode == LeaseMode.Exclusive && LeaseRules.Covers(lease.Prefix, change.Path));
			if (policy.EnforcementFor(change.Path) == LeaseEnforcement.LeaseRequired && !covered)
			{
				problems.Add(new PathConflict(change.Path, "lease", LeaseEnforcement.LeaseRequired, "this prefix requires an exclusive lease, and the landing presents none covering it"));
			}

			var others = held.Where(lease => lease.Owner != record.Id && lease.Mode == LeaseMode.Exclusive && LeaseRules.Covers(lease.Prefix, change.Path)).ToList();
			foreach (var other in others)
			{
				warnings.Add($"{change.Path} is under '{other.Prefix}', leased exclusively by workspace {other.Owner}.");
			}
		}

		return problems;
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
