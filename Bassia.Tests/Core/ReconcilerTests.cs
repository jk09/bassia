namespace Bassia.Tests.Core;

using System.Diagnostics;
using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

/// <summary>Pre-reconciled landings (bassia-f162a7fd): candidates, the landing's fast path and the conflict overview.</summary>
public class ReconcilerTests
{
	private static Dictionary<string, string?> Files(params (string Path, string? Text)[] files) => files.ToDictionary(file => file.Path, file => file.Text);

	private sealed class ConcatDriver : IMergeDriver
	{
		public string Name => "concat";

		public Task<byte[]?> MergeAsync(string path, byte[] baseContent, byte[] ours, byte[] theirs, CancellationToken ct = default) =>
			Task.FromResult<byte[]?>([.. theirs, .. ours]);
	}

	/// <summary>A validation hook that takes its time, like a build would, and counts its runs.</summary>
	private sealed class SlowValidator(TimeSpan delay) : IValidationHook
	{
		private int calls;

		public int Calls => calls;

		public async Task<(bool Ok, string Message)> ValidateAsync(ObjectId tree, CancellationToken ct = default)
		{
			Interlocked.Increment(ref calls);
			await Task.Delay(delay, ct);
			return (true, "ok");
		}
	}

	[Fact]
	public void ACandidate_RoundTripsThroughTheCodec()
	{
		var leaf = new Leaf(EntryKind.File, ObjectId.Of("x"u8), 1);
		var candidate = new Candidate("ws1", ObjectId.Of("b"u8), ObjectId.Of("w"u8), 7, ObjectId.Of("m"u8), LandOutcome.NeedsReplay, "replay it",
			["a.txt", "gone.txt"], [new CandidateChange("a.txt", leaf), new CandidateChange("gone.txt", null)],
			[new CandidateConflict("b.txt", "both-rewrote", ConflictPolicy.Replay, "mainline changed it")], ["c.txt (semantic)"], true,
			DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
		var bytes = ObjectCodec.Encode(candidate);
		Assert.Equal("candidate", ObjectCodec.TypeOf(bytes));
		var decoded = ObjectCodec.Decode<Candidate>(bytes);
		Assert.Equal(bytes, ObjectCodec.Encode(decoded));
		Assert.Equal(candidate.Changes, decoded.Changes);
		Assert.Equal(candidate.Conflicts, decoded.Conflicts);
		Assert.Equal(candidate.Touched, decoded.Touched);
	}

	[Fact]
	public async Task AReconciledWorkspace_LandsOnTheFastPath_EvenAfterUnrelatedLandings()
	{
		var repo = await TestRepo.CreateAsync();
		var a = await repo.WorkspaceAsync("a", Files(("src/a.cs", "a\n")));
		var candidate = await repo.Landing.Reconciler.ReconcileAsync(a.Id);
		Assert.Equal(LandOutcome.WouldLand, candidate!.Outcome);
		Assert.Equal(["src/a.cs"], candidate.Touched);

		await repo.CommitAsync(Files(("docs/b.md", "b\n")), "other");
		var result = await repo.LandAsync(a);
		Assert.Equal(LandOutcome.Landed, result.Outcome);
		Assert.True(result.Reconciled);
		var files = await repo.MainFilesAsync();
		Assert.Equal("a\n", files["src/a.cs"]);
		Assert.Equal("b\n", files["docs/b.md"]);
	}

	[Fact]
	public async Task ACandidateWhosePathMoved_IsNotUsed_AndTheLandingDecidesAgain()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(Files(("src/a.cs", "base\n")));
		var a = await repo.WorkspaceAsync("a", Files(("src/a.cs", "mine\n")));
		Assert.Equal(LandOutcome.WouldLand, (await repo.Landing.Reconciler.ReconcileAsync(a.Id))!.Outcome);

		await repo.CommitAsync(Files(("src/a.cs", "theirs\n")), "other");
		Assert.Equal([a.Id], await repo.Landing.Reconciler.AffectedAsync((await repo.Repo.GetMainAsync()).Seq));
		var result = await repo.LandAsync(a);
		Assert.Equal(LandOutcome.NeedsReplay, result.Outcome);
		Assert.False(result.Reconciled);
	}

	[Fact]
	public async Task ACandidateForOlderCommits_IsNotUsed()
	{
		var repo = await TestRepo.CreateAsync();
		var a = await repo.WorkspaceAsync("a", Files(("one.txt", "1\n")));
		await repo.Landing.Reconciler.ReconcileAsync(a.Id);
		a = await repo.EditAsync(a, Files(("two.txt", "2\n")));

		var result = await repo.LandAsync(a);
		Assert.Equal(LandOutcome.Landed, result.Outcome);
		Assert.False(result.Reconciled);
		Assert.Equal("2\n", (await repo.MainFilesAsync())["two.txt"]);
	}

	[Fact]
	public async Task APolicyChange_MakesEveryCandidateStale()
	{
		var repo = await TestRepo.CreateAsync();
		var a = await repo.WorkspaceAsync("a", Files(("src/Billing/a.cs", "a\n")));
		await repo.Landing.Reconciler.ReconcileAsync(a.Id);
		await repo.CommitAsync(Files((RepoPolicy.FileName, "[leases]\nrequired = [\"src/Billing\"]\n")), "policy");

		Assert.Contains(a.Id, await repo.Landing.Reconciler.AffectedAsync((await repo.Repo.GetMainAsync()).Seq));
		var refused = await repo.LandAsync(a);
		Assert.Equal(LandOutcome.Rejected, refused.Outcome);
		Assert.Equal("lease", refused.Conflicts.Single().Case);
	}

	[Fact]
	public async Task AStrictLanding_NeverTakesTheFastPath()
	{
		var repo = await TestRepo.CreateAsync();
		var a = await repo.WorkspaceAsync("a", Files(("a.txt", "a\n")));
		await repo.Landing.Reconciler.ReconcileAsync(a.Id);
		var result = await repo.Landing.LandAsync(new LandRequest(a.Id, Strict: true, ReadSet: ["lib"]));
		Assert.Equal(LandOutcome.Landed, result.Outcome);
		Assert.False(result.Reconciled);
	}

	[Fact]
	public async Task AMachineMergedCandidate_IsValidatedOnce_AndLandsWithoutValidatingAgain()
	{
		var repo = await TestRepo.CreateAsync();
		var validator = new SlowValidator(TimeSpan.FromMilliseconds(400));
		var landing = new LandingService(repo.Repo, new ConcatDriver(), validator);
		await repo.CommitAsync(Files(("src/a.cs", "a\n"), (RepoPolicy.FileName, "[conflicts]\ndefault = \"semantic\"\n")));
		var one = await repo.WorkspaceAsync("one", Files(("src/a.cs", "one\n")));
		var two = await repo.WorkspaceAsync("two", Files(("src/a.cs", "two\n")));
		Assert.Equal(LandOutcome.Landed, (await landing.LandAsync(new LandRequest(one.Id))).Outcome);

		var candidate = await landing.Reconciler.ReconcileAsync(two.Id);
		Assert.Equal(LandOutcome.WouldLand, candidate!.Outcome);
		Assert.True(candidate.MachineMerged);
		Assert.Equal(1, validator.Calls);

		var clock = Stopwatch.StartNew();
		var result = await landing.LandAsync(new LandRequest(two.Id));
		clock.Stop();
		Assert.Equal(LandOutcome.Landed, result.Outcome);
		Assert.True(result.Reconciled);
		Assert.Equal(1, validator.Calls);
		Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(400), $"the landing waited for validation ({clock.ElapsedMilliseconds} ms)");
		Assert.Equal("one\ntwo\n", (await repo.MainFilesAsync())["src/a.cs"]);
		Assert.True((await repo.Repo.Objects.ReadCommitAsync(result.Commit!.Value)).MachineMerged);
	}

	[Fact]
	public async Task AMachineMergedCandidate_IsValidatedAgain_WhenMainlineMoved()
	{
		var repo = await TestRepo.CreateAsync();
		var validator = new SlowValidator(TimeSpan.Zero);
		var landing = new LandingService(repo.Repo, new ConcatDriver(), validator);
		await repo.CommitAsync(Files(("src/a.cs", "a\n"), (RepoPolicy.FileName, "[conflicts]\ndefault = \"semantic\"\n")));
		var one = await repo.WorkspaceAsync("one", Files(("src/a.cs", "one\n")));
		var two = await repo.WorkspaceAsync("two", Files(("src/a.cs", "two\n")));
		await landing.LandAsync(new LandRequest(one.Id));
		await landing.Reconciler.ReconcileAsync(two.Id);
		await repo.CommitAsync(Files(("docs/x.md", "x\n")), "unrelated");

		var result = await landing.LandAsync(new LandRequest(two.Id));
		Assert.Equal(LandOutcome.Landed, result.Outcome);
		Assert.False(result.Reconciled);
		Assert.Equal(2, validator.Calls);
	}

	[Fact]
	public async Task ValidationInTheReconciler_DoesNotHoldUpOtherLandings()
	{
		var repo = await TestRepo.CreateAsync();
		var validator = new SlowValidator(TimeSpan.FromSeconds(2));
		var landing = new LandingService(repo.Repo, new ConcatDriver(), validator);
		await repo.CommitAsync(Files(("src/a.cs", "a\n"), (RepoPolicy.FileName, "[conflicts]\ndefault = \"semantic\"\n")));
		var one = await repo.WorkspaceAsync("one", Files(("src/a.cs", "one\n")));
		var two = await repo.WorkspaceAsync("two", Files(("src/a.cs", "two\n")));
		var disjoint = await repo.WorkspaceAsync("disjoint", Files(("docs/d.md", "d\n")));
		await landing.LandAsync(new LandRequest(one.Id));

		var reconciling = landing.Reconciler.ReconcileAsync(two.Id);
		var clock = Stopwatch.StartNew();
		var result = await landing.LandAsync(new LandRequest(disjoint.Id));
		clock.Stop();
		Assert.Equal(LandOutcome.Landed, result.Outcome);
		Assert.False(reconciling.IsCompleted);
		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"the landing waited for another workspace's validation ({clock.ElapsedMilliseconds} ms)");
		await reconciling;
	}

	[Fact]
	public async Task TheOverview_ShowsEachWorkspaceAgainstMainline_TheirOverlaps_AndHotPaths()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(Files(("shared.txt", "base\n"), ("main.txt", "base\n")));
		var a = await repo.WorkspaceAsync("a", Files(("shared.txt", "a\n"), ("a.txt", "a\n")));
		var b = await repo.WorkspaceAsync("b", Files(("shared.txt", "b\n")));
		var c = await repo.WorkspaceAsync("c", Files(("main.txt", "c\n")));
		await repo.CommitAsync(Files(("main.txt", "mainline\n")), "mainline");
		repo.Landing.Reconciler.ReconcileOnRead = true;

		var overview = await repo.Landing.Reconciler.OverviewAsync();
		var rows = overview.Workspaces.ToDictionary(row => row.Name);
		Assert.Equal(LandOutcome.WouldLand, rows["a"].Outcome);
		Assert.Equal(LandOutcome.WouldLand, rows["b"].Outcome);
		Assert.Equal(LandOutcome.NeedsReplay, rows["c"].Outcome);
		Assert.Equal("main.txt", rows["c"].Conflicts.Single().Path);
		Assert.All(overview.Workspaces, row => Assert.True(row.Current));
		Assert.Equal(["shared.txt"], rows["a"].Overlaps);
		var overlap = Assert.Single(overview.Overlaps);
		Assert.Equal("shared.txt", overlap.Path);
		Assert.Equal(new[] { a.Id, b.Id }.Order(), overlap.Workspaces.Order());
		Assert.Contains(overview.HotPaths, hot => hot.Path == "main.txt" && hot.Conflicts == 1);
		Assert.Contains(overview.HotPaths, hot => hot.Path == "shared.txt" && hot.Workspaces == 2);
		Assert.Equal(2, overview.CanLand);
		Assert.Equal(1, overview.Blocked);

		// After a lands, b's candidate is stale (shared.txt moved) and the overview says so.
		repo.Landing.Reconciler.ReconcileOnRead = false;
		await repo.LandAsync(a);
		var after = (await repo.Landing.Reconciler.OverviewAsync()).Workspaces.ToDictionary(row => row.Name);
		Assert.False(after.ContainsKey("a"));
		Assert.False(after["b"].Current);
		Assert.True(after["c"].Current);
		_ = c;
	}

	[Fact]
	public async Task AClosedWorkspace_LosesItsCandidate_AndGarbageCollectionKeepsOnlyOpenOnes()
	{
		var repo = await TestRepo.CreateAsync();
		var validator = new SlowValidator(TimeSpan.Zero);
		var landing = new LandingService(repo.Repo, new ConcatDriver(), validator);
		await repo.CommitAsync(Files(("a.cs", "a\n"), (RepoPolicy.FileName, "[conflicts]\ndefault = \"semantic\"\n")));
		var one = await repo.WorkspaceAsync("one", Files(("a.cs", "one\n")));
		var two = await repo.WorkspaceAsync("two", Files(("a.cs", "two\n")));
		var gone = await repo.WorkspaceAsync("gone", Files(("g.cs", "g\n")));
		await landing.LandAsync(new LandRequest(one.Id));
		var merged = await landing.Reconciler.ReconcileAsync(two.Id);
		await landing.Reconciler.ReconcileAsync(gone.Id);
		await repo.Workspaces.CloseAsync(gone.Id, abandon: true, "test");

		// The merge result exists only in the candidate; collection keeps it while the workspace is open.
		var mergedLeaf = merged!.Changes.Single(change => change.Path == "a.cs").Leaf!;
		var collector = new GarbageCollector(repo.Repo, (IObjectStoreMaintenance)repo.Objects);
		await collector.CollectAsync(TimeSpan.Zero, TimeSpan.FromDays(14));
		Assert.Equal("one\ntwo\n", Encoding.UTF8.GetString(await repo.Repo.Objects.ReadContentAsync(mergedLeaf)));
		Assert.NotNull(await repo.Views.GetHeadAsync(HeadName.Candidate(two.Id)));
		Assert.Null(await repo.Views.GetHeadAsync(HeadName.Candidate(gone.Id)));

		Assert.Null(await landing.Reconciler.ReconcileAsync(gone.Id));
	}
}
