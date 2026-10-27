namespace Bassia.Tests.Core;

using Bassia.Storage.Memory;
using Bassia.Integration;
using Bassia.Locks;
using Bassia.Split;
using Bassia.Storage;
using static MemoryMonorepo;

/// <summary>
/// Integration (triage, merges, the resolver, publishing, advancing) and lock catch-up through the storage contract, on
/// in-memory storage: no git, no shell, no weave. The memory backend merges at file granularity, so two runs conflict
/// exactly when they change the same file differently.
/// </summary>
public class IntegrationAndLocksTests
{
	/// <summary>lib, tagged v1, with three files.</summary>
	private static async Task<MemoryMonorepo> LibAsync()
	{
		var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib", null, ("a.txt", "a\n"), ("b.txt", "b\n"), ("notes.md", "notes\n"));
		return monorepo;
	}

	/// <summary>A completed run on lib@v1 whose agent writes <paramref name="content"/> to <paramref name="path"/>.</summary>
	private static async Task<RunMetadata> RunAsync(MemoryMonorepo monorepo, string path, string content)
	{
		var outcome = await monorepo.Runs(metadata =>
		{
			File.WriteAllText(Path.Combine(metadata.WorkspacePath, "lib", path), content);
			return 0;
		}).StartAsync("lib@v1", $"agent \"write {content.Trim()} to {path}\"", Quiet());
		Assert.True(outcome.Ok, outcome.Message);
		return outcome.Metadata;
	}

	private static IntegrationSettings Settings(MemoryMonorepo monorepo, Func<ResolverRequest, int?>? resolver = null, MergePolicy? policy = null) => new()
	{
		WorkspaceDir = monorepo.WorkspaceDir,
		MergePolicyFor = _ => policy ?? MergePolicy.Default,
		RunResolver = request => Task.FromResult(resolver is null ? throw new InvalidOperationException("no resolver expected") : resolver(request))
	};

	private static Task<IReadOnlyList<ComponentIntegration>> PlanAsync(MemoryMonorepo monorepo, IReadOnlyList<RunMetadata> runs, IntegrationSettings settings, IntegrationChoices? choices = null) =>
		IntegrationPlanner.PlanAsync(monorepo.Graph, settings, runs, choices);

	private static async Task<IntegrationOutcome> IntegrateAsync(MemoryMonorepo monorepo, IReadOnlyList<RunMetadata> runs, IntegrationSettings settings, IntegrationChoices? choices = null) =>
		await IntegrationRunner.RunAsync(monorepo.Graph, settings, monorepo.Records, runs, await PlanAsync(monorepo, runs, settings, choices), "fake-resolver",
			new IntegrationContext { OnProgress = _ => { } }, structural: choices?.Structural);

	private static string Text(MemoryMonorepo monorepo, string commit, string path) =>
		System.Text.Encoding.UTF8.GetString(monorepo.Storage["lib"].Get(commit).Files[path].Content);

	// ----- the triage -----

	[Fact]
	public async Task Plan_ChainsResultsOntoEachOther_AndSendsAConflictToTheResolver()
	{
		using var monorepo = await LibAsync();
		var first = await RunAsync(monorepo, "a.txt", "a by first\n");
		var second = await RunAsync(monorepo, "b.txt", "b by second\n");
		var third = await RunAsync(monorepo, "a.txt", "a by third\n");

		var steps = Assert.Single(await PlanAsync(monorepo, [first, second, third], Settings(monorepo))).Steps;

		var byRun = steps.ToDictionary(step => step.RunId);
		Assert.Equal((Triage.FastForward, MergeStrategy.Syntactic), (byRun[first.RunId].Triage, byRun[first.RunId].Strategy));
		Assert.Equal((Triage.Clean, MergeStrategy.Syntactic), (byRun[second.RunId].Triage, byRun[second.RunId].Strategy));
		Assert.Equal((Triage.Conflict, MergeStrategy.Semantic), (byRun[third.RunId].Triage, byRun[third.RunId].Strategy));
		Assert.Equal(["a.txt"], byRun[third.RunId].Conflicts);
		Assert.Contains(first.RunId, byRun[third.RunId].ConflictsWith);
		Assert.Equal(third.RunId, steps[^1].RunId);
	}

	[Fact]
	public async Task Plan_ManualPathsAndSkips_FollowThePolicyAndTheChoices()
	{
		using var monorepo = await LibAsync();
		var first = await RunAsync(monorepo, "a.txt", "a by first\n");
		var second = await RunAsync(monorepo, "a.txt", "a by second\n");
		var third = await RunAsync(monorepo, "b.txt", "b by third\n");
		var policy = MergePolicy.Default with { ManualPaths = new PathPatterns(["*.txt"]) };

		var steps = Assert.Single(await PlanAsync(monorepo, [first, second, third], Settings(monorepo, policy: policy), new IntegrationChoices
		{
			Strategies = new Dictionary<(string, string), MergeStrategy> { [("*", third.RunId)] = MergeStrategy.Skip }
		})).Steps.ToDictionary(step => step.RunId);

		Assert.Equal(MergeStrategy.Manual, steps[second.RunId].Strategy);
		Assert.Contains("merge.manual_paths", steps[second.RunId].Note);
		Assert.Equal(MergeStrategy.Skip, steps[third.RunId].Strategy);
	}

	[Fact]
	public async Task Plan_WithAStructuralDriver_MergesWhatItCan()
	{
		using var monorepo = await LibAsync();
		monorepo.Storage.Drivers[StructuralMerge.DriverName] = (_, ours, theirs, _) => (true, ours + theirs, "weave-warning: combined both sides\n");
		var first = await RunAsync(monorepo, "notes.md", "first\n");
		var second = await RunAsync(monorepo, "notes.md", "second\n");

		var steps = Assert.Single(await PlanAsync(monorepo, [first, second], Settings(monorepo, policy: MergePolicy.Default with { Warnings = WarningsPolicy.Accept }),
			new IntegrationChoices { Structural = StructuralMerge.Of("fake-weave") })).Steps;

		var structured = steps.Single(step => step.RunId == second.RunId);
		Assert.Equal((Triage.Conflict, StructuralTriage.Warnings, MergeStrategy.Structural), (structured.Triage, structured.Structural, structured.Strategy));
		Assert.Equal(["combined both sides"], structured.StructuralWarnings);
	}

	// ----- running an integration -----

	[Fact]
	public async Task Run_MergesPublishesAndTags_AndAdvanceMovesTheBase()
	{
		using var monorepo = await LibAsync();
		var first = await RunAsync(monorepo, "a.txt", "a by first\n");
		var second = await RunAsync(monorepo, "b.txt", "b by second\n");
		var store = monorepo.Storage["lib"];
		var main = (await store.BranchTipAsync("main"))!;

		var outcome = await IntegrateAsync(monorepo, [first, second], Settings(monorepo));

		Assert.True(outcome.Ok, outcome.Message);
		Assert.Equal("completed", outcome.Record.Status);
		var component = Assert.Single(outcome.Record.Components);
		Assert.Equal((ResultStatus.Pushed, $"{IntegrationRecord.RefBase(outcome.Record.IntegrationId)}/0"), (component.ResultStatus, component.ResultTag));
		Assert.All(component.Steps, step => Assert.Equal(StepOutcome.Merged, step.Outcome));
		Assert.Equal(component.ResultCommit, (await store.GetTagAsync(component.ResultTag!))!.Commit);
		Assert.Equal(("a by first\n", "b by second\n"), (Text(monorepo, component.ResultCommit!, "a.txt"), Text(monorepo, component.ResultCommit!, "b.txt")));
		Assert.Equal(main, await store.BranchTipAsync("main"));
		Assert.Equal(["started", "completed"], monorepo.Records.Versions(IntegrationRecord.RefBase(outcome.Record.IntegrationId))
			.Select(text => IntegrationRecord.FromToml(text).Status));

		var advanced = await IntegrationRunner.AdvanceAsync(monorepo.Graph, monorepo.Records, outcome.Record);

		Assert.True(advanced.Ok, advanced.Message);
		Assert.Equal(component.ResultCommit, await store.BranchTipAsync("main"));
		Assert.Contains("nothing left to advance", (await IntegrationRunner.AdvanceAsync(monorepo.Graph, monorepo.Records, outcome.Record)).Message);
	}

	[Fact]
	public async Task Advance_WhenTheBaseMoved_IsRefused()
	{
		using var monorepo = await LibAsync();
		var run = await RunAsync(monorepo, "a.txt", "a by run\n");
		var outcome = await IntegrateAsync(monorepo, [run], Settings(monorepo));
		monorepo.Storage["lib"].Commit("main", "Someone else's change", new Dictionary<string, string?> { ["c.txt"] = "c" });

		var advanced = await IntegrationRunner.AdvanceAsync(monorepo.Graph, monorepo.Records, outcome.Record);

		Assert.False(advanced.Ok);
		Assert.Contains("moved since the integration was built", advanced.Message);
	}

	[Fact]
	public async Task Run_TheResolverResolvesAConflict_AsAMergeCommitOfBothSides()
	{
		using var monorepo = await LibAsync();
		var first = await RunAsync(monorepo, "a.txt", "a by first\n");
		var second = await RunAsync(monorepo, "a.txt", "a by second\n");
		ResolverRequest? seen = null;

		var outcome = await IntegrateAsync(monorepo, [first, second], Settings(monorepo, request =>
		{
			seen = request;
			Assert.True(File.ReadAllText(Path.Combine(request.Directory, "a.txt")).Contains("<<<<<<<"));
			File.WriteAllText(Path.Combine(request.Directory, "a.txt"), "a by both\n");
			return 0;
		}));

		Assert.True(outcome.Ok, outcome.Message);
		var resolved = outcome.Record.Components.Single().Steps.Single(step => step.RunId == second.RunId);
		Assert.Equal(StepOutcome.Resolved, resolved.Outcome);
		Assert.Contains("a.txt", seen!.Brief);
		Assert.Equal(second.RunId, seen.Environment["BASSIA_RUN_ID"]);
		var commit = (await monorepo.Storage["lib"].GetCommitAsync(resolved.Commit!))!;
		Assert.Equal(2, commit.Parents.Count);
		Assert.Equal("a by both\n", Text(monorepo, resolved.Commit!, "a.txt"));
	}

	[Theory]
	[InlineData(0, "conflicts remain in a.txt")]
	[InlineData(3, "the resolver exited with code 3")]
	public async Task Run_AResolverThatDoesNotResolve_FailsTheStepAndPublishesTheRest(int exitCode, string reason)
	{
		using var monorepo = await LibAsync();
		var first = await RunAsync(monorepo, "a.txt", "a by first\n");
		var second = await RunAsync(monorepo, "a.txt", "a by second\n");

		var outcome = await IntegrateAsync(monorepo, [first, second], Settings(monorepo, _ => exitCode));

		Assert.Equal("partial", outcome.Record.Status);
		var component = outcome.Record.Components.Single();
		var failed = component.Steps.Single(step => step.RunId == second.RunId);
		Assert.Equal(StepOutcome.Failed, failed.Outcome);
		Assert.Contains(reason, failed.Note);
		Assert.Equal("a by first\n", Text(monorepo, component.ResultCommit!, "a.txt"));
	}

	[Fact]
	public async Task Run_WithAStructuralDriver_MergesStructurally()
	{
		using var monorepo = await LibAsync();
		monorepo.Storage.Drivers[StructuralMerge.DriverName] = (_, ours, theirs, _) => (true, ours + theirs, "");
		var first = await RunAsync(monorepo, "notes.md", "first\n");
		var second = await RunAsync(monorepo, "notes.md", "second\n");

		var outcome = await IntegrateAsync(monorepo, [first, second], Settings(monorepo), new IntegrationChoices { Structural = StructuralMerge.Of("fake-weave") });

		Assert.True(outcome.Ok, outcome.Message);
		var component = outcome.Record.Components.Single();
		Assert.Equal((MergeStrategy.Structural, StepOutcome.Merged), (component.Steps[^1].Strategy, component.Steps[^1].Outcome));
		Assert.Equal("first\nsecond\n", Text(monorepo, component.ResultCommit!, "notes.md"));
	}

	// ----- lock catch-up -----

	/// <summary>A lock table in the monorepo's temporary folder, with the released holds and result of <paramref name="run"/> on lib.</summary>
	private static async Task<ILockStore> TableAsync(MemoryMonorepo monorepo, RunMetadata run, string path)
	{
		var table = new MemoryLockStore(TimeSpan.FromMinutes(2));
		var lib = run.Components.Single();
		var entry = RunLocks.NewEntry(run.RunId, RunMetadata.Timestamp(), 1, "0", RunMetadata.Timestamp());
		entry.State = LockEntry.ReleasedState;
		entry.Released = RunMetadata.Timestamp();
		entry.Holds.Add(new LockHold(new LockKey("lib", path, LockKind.File), LockHold.Changed, RunMetadata.Timestamp()));
		entry.Results.Add(new LockResult("lib", lib.ResultTag!, lib.ResultCommit!));
		await table.UpdateAsync(session => session.Save(entry));
		return table;
	}

	[Fact]
	public async Task CatchUp_BringsAReleasedResultIntoAWorkingCopy()
	{
		using var monorepo = await LibAsync();
		var holder = await RunAsync(monorepo, "a.txt", "a by holder\n");
		var table = await TableAsync(monorepo, holder, "a.txt");
		var store = monorepo.Storage["lib"];
		var checkout = Path.Combine(monorepo.WorkspaceDir, "waiter", "lib");
		await store.CheckoutAsync(holder.Components.Single().Commit, checkout, "agent-run/waiter");
		File.WriteAllText(Path.Combine(checkout, "b.txt"), "b by waiter\n");
		var keys = new[] { new LockKey("lib", "a.txt", LockKind.File) };

		Assert.Equal(["b.txt"], await LockCheckouts.ChangedFilesAsync(monorepo.Storage, "lib", checkout));
		var behind = Assert.Single(await LockCheckouts.BehindAsync(monorepo.Storage, table, "waiter", "lib", checkout, keys));
		var caughtUp = await LockCheckouts.BringInAsync(monorepo.Storage, checkout, behind);

		Assert.Null(caughtUp.Error);
		Assert.Equal(["a.txt"], caughtUp.Files);
		Assert.Equal("a by holder\n", File.ReadAllText(Path.Combine(checkout, "a.txt")));
		Assert.Equal("b by waiter\n", File.ReadAllText(Path.Combine(checkout, "b.txt")));
		Assert.Empty(await LockCheckouts.BehindAsync(monorepo.Storage, table, "waiter", "lib", checkout, keys));
	}

	[Fact]
	public async Task CatchUp_ThatWouldOverwriteLocalChanges_IsUndoneAndKeepsThem()
	{
		using var monorepo = await LibAsync();
		var holder = await RunAsync(monorepo, "a.txt", "a by holder\n");
		var table = await TableAsync(monorepo, holder, "a.txt");
		var checkout = Path.Combine(monorepo.WorkspaceDir, "waiter", "lib");
		await monorepo.Storage["lib"].CheckoutAsync(holder.Components.Single().Commit, checkout, "agent-run/waiter");
		File.WriteAllText(Path.Combine(checkout, "a.txt"), "a by waiter\n");

		var behind = Assert.Single(await LockCheckouts.BehindAsync(monorepo.Storage, table, "waiter", "lib", checkout, [new LockKey("lib", "a.txt", LockKind.File)]));
		var caughtUp = await LockCheckouts.BringInAsync(monorepo.Storage, checkout, behind);

		Assert.Contains("would be overwritten", caughtUp.Error);
		Assert.Equal("a by waiter\n", File.ReadAllText(Path.Combine(checkout, "a.txt")));
		Assert.False(await monorepo.Storage.OpenWorkspace("lib", checkout).MergeInProgressAsync());
	}

	[Fact]
	public async Task ForgetLanded_DropsResultsOnTheDefaultBranch()
	{
		using var monorepo = await LibAsync();
		var holder = await RunAsync(monorepo, "a.txt", "a by holder\n");
		var table = await TableAsync(monorepo, holder, "a.txt");

		await LockCheckouts.ForgetLandedAsync(monorepo.Graph, table);
		Assert.True(await table.HasAsync(holder.RunId));

		await monorepo.Storage["lib"].SetBranchAsync("main", holder.Components.Single().ResultCommit!);
		await LockCheckouts.ForgetLandedAsync(monorepo.Graph, table);
		Assert.False(await table.HasAsync(holder.RunId));
	}
}
