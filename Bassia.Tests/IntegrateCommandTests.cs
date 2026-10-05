using Bassia.Git;
using Bassia.Integration;

namespace Bassia.Tests;

public class IntegrateCommandTests
{
	private static IntegrationStore Store(MonorepoFixture fixture) =>
		new(new RunMetadataStore(new GitClient(fixture.Root), fixture.RunsRepo));

	private static async Task<IntegrationRecord> SingleIntegrationAsync(MonorepoFixture fixture) =>
		Assert.Single(await Store(fixture).ListLatestAsync());

	private static string Tag(IntegrationRecord record) => IntegrationRecord.TagName(record.IntegrationId, 0);

	private static async Task<string> ShowAsync(MonorepoFixture fixture, string component, string revision, string file) =>
		(await TestEnvironment.GitAsync(fixture.SourceRepo(component), "show", $"{revision}:{file}")).Replace("\r", "");

	[Fact]
	public async Task Integrate_WithoutArguments_ReturnsUsage()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();

		var (exitCode, _, error) = await fixture.IntegrationStartAsync();

		Assert.Equal(2, exitCode);
		Assert.Contains("-runs is required", error);
		Assert.Contains("Usage: bassia integration start -runs", error);
	}

	[Fact]
	public async Task Integrate_UnknownRun_FailsWithoutRecordingAnything()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "a.txt", "a");

		var (exitCode, _, error) = await fixture.IntegrationStartAsync("-runs", "0000ffff");

		Assert.Equal(1, exitCode);
		Assert.Contains("Unknown agentic run '0000ffff'", error);
		Assert.Empty(await Store(fixture).ListLatestAsync());
	}

	[Fact]
	public async Task Integrate_RunThatPushedNothing_IsRejected()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var (_, _, error) = await fixture.RunStartAsync("-select", "example@v0", "-run", TestEnvironment.FailingCommand);
		var failedRun = TestEnvironment.RunIdOf(error);

		var (exitCode, _, integrateError) = await fixture.IntegrationStartAsync("-runs", failedRun);

		Assert.Equal(1, exitCode);
		Assert.Contains("has no result pushed to any component", integrateError);
	}

	[Fact]
	public async Task Integrate_IndependentRuns_AreMergedByGitAloneAndTaggedWithoutMovingMain()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var first = await fixture.RunWritingAsync("example", "a.txt", "a");
		var second = await fixture.RunWritingAsync("example", "b.txt", "b");
		var main = await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "rev-parse", "main");

		// A resolver that fails proves git did everything on its own.
		var (exitCode, output, error) = await fixture.IntegrationStartAsync("-runs", "all", "-resolve", TestEnvironment.FailingCommand);

		Assert.True(exitCode == 0, error);
		Assert.Contains("status = \"completed\"", output);
		Assert.Contains("2 result(s) merged by git, 0 resolved semantically", output);
		var record = await SingleIntegrationAsync(fixture);
		Assert.Equal([first, second], record.Runs);
		var component = Assert.Single(record.Components);
		Assert.Equal("main", component.BaseRef);
		Assert.All(component.Steps, step => Assert.Equal(StepOutcome.Merged, step.Outcome));
		Assert.All(component.Steps, step => Assert.Equal(MergeStrategy.Syntactic, step.Strategy));

		// The result tag holds both runs' files; main is where it was.
		var source = fixture.SourceRepo("example");
		Assert.Equal("README.md\na.txt\nb.txt", (await TestEnvironment.GitAsync(source, "ls-tree", "--name-only", "-r", Tag(record))).Replace("\r", ""));
		Assert.Equal("tag", await TestEnvironment.GitAsync(source, "cat-file", "-t", Tag(record)));
		Assert.Equal(main, await TestEnvironment.GitAsync(source, "rev-parse", "main"));
		Assert.Equal(await TestEnvironment.GitAsync(source, "rev-parse", $"{Tag(record)}^{{commit}}"),
			await TestEnvironment.GitAsync(source, "rev-parse", IntegrationRecord.RefBase(record.IntegrationId)));

		// Every run came in through its own merge commit, whose message says which run and how.
		var subjects = await TestEnvironment.GitAsync(source, "log", "--merges", "--format=%s", Tag(record));
		Assert.Contains(RunMetadata.Key(first), subjects);
		Assert.Contains(RunMetadata.Key(second), subjects);
		var message = await TestEnvironment.GitAsync(source, "log", "-1", "--format=%B", Tag(record));
		Assert.Contains("[integration]", message);
		Assert.Contains("strategy = \"syntactic\"", message);
		Assert.Contains($"run_id = \"{second}\"", message);
	}

	[Fact]
	public async Task Integrate_ConflictingRuns_GitMergesTheFirstAndTheResolverTheSecondWithASemanticBrief()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var first = await fixture.RunWritingAsync("example", "same.txt", "from-first");
		var second = await fixture.RunWritingAsync("example", "same.txt", "from-second");
		var stdin = Path.Combine(fixture.Root, "resolver-stdin.md");
		var resolver = $"{TestEnvironment.CaptureStdinCommand(stdin)} && {TestEnvironment.WriteFileCommand("same.txt", "merged")}";

		var (exitCode, output, error) = await fixture.IntegrationStartAsync("-runs", $"{first},{second}", "-resolve", resolver);

		Assert.True(exitCode == 0, error);
		Assert.Contains("1 result(s) merged by git, 1 resolved semantically", output);
		var record = await SingleIntegrationAsync(fixture);
		var steps = Assert.Single(record.Components).Steps;
		Assert.Equal([first, second], steps.Select(step => step.RunId));
		Assert.Equal((MergeStrategy.Syntactic, StepOutcome.Merged), (steps[0].Strategy, steps[0].Outcome));
		Assert.Equal((MergeStrategy.Semantic, StepOutcome.Resolved), (steps[1].Strategy, steps[1].Outcome));
		Assert.Equal(["same.txt"], steps[1].Conflicts);
		Assert.Equal([first], steps[1].ConflictsWith);

		Assert.Equal("merged", (await ShowAsync(fixture, "example", Tag(record), "same.txt")).Trim());
		var message = await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "log", "-1", "--format=%B", Tag(record));
		Assert.Contains("strategy = \"semantic\"", message);
		Assert.Contains("conflicts = [\"same.txt\"]", message);
		Assert.Equal(2, (await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "log", "-1", "--format=%P", Tag(record))).Split(' ').Length);

		// The brief went to the resolver on stdin and is kept next to the checkout; it carries both runs' intent.
		var brief = await File.ReadAllTextAsync(steps[1].Brief!);
		Assert.Equal(brief.Replace("\r", "").Trim(), (await File.ReadAllTextAsync(stdin)).Replace("\r", "").Trim());
		Assert.Contains("`same.txt`", brief);
		Assert.Contains($"`{second}`", brief);
		Assert.Contains($"Integrated: run `{first}`", brief);
		Assert.Contains("from-first", brief);
		Assert.Contains("from-second", brief);
	}

	[Fact]
	public async Task Integrate_ResolverThatLeavesConflictMarkers_FailsTheStepButPublishesWhatGitMerged()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var first = await fixture.RunWritingAsync("example", "same.txt", "from-first");
		var second = await fixture.RunWritingAsync("example", "same.txt", "from-second");
		var doNothing = OperatingSystem.IsWindows() ? "exit /b 0" : "true";

		var (exitCode, _, error) = await fixture.IntegrationStartAsync("-runs", "all", "-resolve", doNothing);

		Assert.Equal(1, exitCode);
		Assert.Contains("status = \"partial\"", error);
		var record = await SingleIntegrationAsync(fixture);
		Assert.Equal("partial", record.Status);
		var steps = Assert.Single(record.Components).Steps;
		Assert.Equal(StepOutcome.Merged, steps.Single(step => step.RunId == first).Outcome);
		var failed = steps.Single(step => step.RunId == second);
		Assert.Equal(StepOutcome.Failed, failed.Outcome);
		Assert.Contains("conflicts remain in same.txt", failed.Note);
		Assert.Equal("from-first", (await ShowAsync(fixture, "example", Tag(record), "same.txt")).Trim());
	}

	[Fact]
	public async Task Integrate_ResolverThatFails_FailsTheStep()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "same.txt", "from-first");
		await fixture.RunWritingAsync("example", "same.txt", "from-second");

		var (exitCode, _, _) = await fixture.IntegrationStartAsync("-runs", "all", "-resolve", TestEnvironment.FailingCommand);

		Assert.Equal(1, exitCode);
		var failed = (await SingleIntegrationAsync(fixture)).AllSteps.Single(step => step.Outcome == StepOutcome.Failed);
		Assert.Equal("the resolver exited with code 3", failed.Note);
	}

	[Fact]
	public async Task Plan_PrintsTheTriageAndChangesNothing()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var first = await fixture.RunWritingAsync("example", "same.txt", "from-first");
		var second = await fixture.RunWritingAsync("example", "same.txt", "from-second");
		var third = await fixture.RunWritingAsync("example", "other.txt", "other");
		var refsBefore = await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "for-each-ref");

		var (exitCode, output, error) = await fixture.BassiaAsync("integration", "plan", "-runs", "all");

		Assert.True(exitCode == 0, error);
		Assert.Contains("2 step(s) for git, 0 for the structural merge, 1 for the resolver, 0 for a human, 0 skipped. Structural merge: off. Nothing was changed.", output);
		// Git's steps come first, in run order; the run that collides with the first moves behind them.
		var order = new[] { first, third, second }.Select(run => output.IndexOf($"run_id = \"{run}\"", StringComparison.Ordinal)).ToList();
		Assert.All(order, index => Assert.True(index > 0));
		Assert.Equal(order.Order(), order);
		Assert.Contains("triage = \"conflict\"", output);
		Assert.Contains("conflicts = [\"same.txt\"]", output);
		Assert.Contains("triage = \"fast_forward\"", output);
		Assert.Equal(refsBefore, await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "for-each-ref"));
		Assert.Empty(await Store(fixture).ListLatestAsync());
	}

	[Fact]
	public async Task Integrate_ManualSemanticPolicy_LeavesTheConflictForAHumanAndAutoAdvanceMovesTheBase()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var first = await fixture.RunWritingAsync("example", "same.txt", "from-first");
		var second = await fixture.RunWritingAsync("example", "same.txt", "from-second");
		Assert.Equal(0, (await fixture.BassiaAsync("config", "set", "merge.component.example.semantic", "-value", "manual")).ExitCode);
		Assert.Equal(0, (await fixture.BassiaAsync("config", "set", "merge.advance", "-value", "auto", "-user")).ExitCode);

		var (planCode, plan, planError) = await fixture.BassiaAsync("integration", "plan", "-runs", "all");
		Assert.True(planCode == 0, planError);
		Assert.Contains("1 step(s) for git, 0 for the structural merge, 0 for the resolver, 1 for a human", plan);

		var (exitCode, output, error) = await fixture.IntegrationStartAsync("-runs", "all", "-resolve", TestEnvironment.FailingCommand);

		Assert.True(exitCode == 0, error + output);
		var record = await SingleIntegrationAsync(fixture);
		Assert.Equal(IntegrationRunner.NeedsAttentionStatus, record.Status);
		var steps = Assert.Single(record.Components).Steps;
		Assert.Equal([first, second], steps.Select(step => step.RunId));
		Assert.Equal([StepOutcome.Merged, StepOutcome.NeedsAttention], steps.Select(step => step.Outcome));
		Assert.Contains("merge.semantic = manual", steps[1].Note);
		Assert.True(record.Components[0].Advanced);
		Assert.Equal(record.Components[0].ResultCommit, (await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "rev-parse", "main")).Trim());
	}

	[Fact]
	public async Task Plan_ManualPathsAndTheManualSwitch_LeaveResultsForAHuman()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "db.sql", "from-first");
		await fixture.RunWritingAsync("example", "db.sql", "from-second");
		var third = await fixture.RunWritingAsync("example", "other.txt", "other");
		Assert.Equal(0, (await fixture.BassiaAsync("config", "set", "merge.manual_paths", "-value", "**/*.sql")).ExitCode);

		var (exitCode, output, error) = await fixture.BassiaAsync("integration", "plan", "-runs", "all", "-manual", RunMetadata.Key(third));

		Assert.True(exitCode == 0, error);
		Assert.Contains("1 step(s) for git, 0 for the structural merge, 0 for the resolver, 2 for a human", output);
		Assert.Contains("match merge.manual_paths", output);
	}

	[Fact]
	public async Task Integrate_SkipAndSemanticOverridesChangeTheStrategy()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var first = await fixture.RunWritingAsync("example", "a.txt", "a");
		var second = await fixture.RunWritingAsync("example", "b.txt", "b");
		var third = await fixture.RunWritingAsync("example", "c.txt", "c");
		var stdin = Path.Combine(fixture.Root, "review.md");

		var (exitCode, _, error) = await fixture.IntegrationStartAsync(
			"-runs", "all", "-skip", RunMetadata.Key(second), "-semantic", third, "-resolve", TestEnvironment.CaptureStdinCommand(stdin));

		Assert.True(exitCode == 0, error);
		var steps = Assert.Single((await SingleIntegrationAsync(fixture)).Components).Steps;
		Assert.Equal([first, third, second], steps.Select(step => step.RunId));
		Assert.Equal([StepOutcome.Merged, StepOutcome.Resolved, StepOutcome.Skipped], steps.Select(step => step.Outcome));
		Assert.True(steps[1].Overridden);
		Assert.Contains("semantic review", await File.ReadAllTextAsync(stdin));
		var record = await SingleIntegrationAsync(fixture);
		Assert.Equal("README.md\na.txt\nc.txt", (await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "ls-tree", "--name-only", "-r", Tag(record))).Replace("\r", ""));
	}

	[Fact]
	public async Task Integrate_RunsOverSeveralComponents_ShareOneIdenticallyNamedTagAcrossThem()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("app");
		await fixture.AddComponentAsync("lib");
		await fixture.AddComponentAsync("idle");
		var both = await fixture.RunStartAsync("-select", "app@v0,lib@v0", "-run",
			$"{TestEnvironment.WriteFileCommand("app/app.txt", "app")} && {TestEnvironment.WriteFileCommand("lib/lib.txt", "lib")}");
		Assert.True(both.ExitCode == 0, both.Error);
		await fixture.RunWritingAsync("lib", "more.txt", "more");

		var (exitCode, _, error) = await fixture.IntegrationStartAsync("-runs", "all");

		Assert.True(exitCode == 0, error);
		var record = await SingleIntegrationAsync(fixture);
		Assert.Equal(["app", "lib"], record.Components.Select(component => component.Name));
		Assert.Single(record.Components[0].Steps);
		Assert.Equal(2, record.Components[1].Steps.Count);
		foreach (var component in new[] { "app", "lib" })
		{
			Assert.Equal("tag", await TestEnvironment.GitAsync(fixture.SourceRepo(component), "cat-file", "-t", Tag(record)));
		}

		Assert.Equal("", await TestEnvironment.GitAsync(fixture.SourceRepo("idle"), "tag", "--list", "integration/*"));

		// The integration is a baseline like any other: the next run can start from it.
		var next = await fixture.RunStartAsync("-select", $"app@{Tag(record)},lib@{Tag(record)}", "-run", TestEnvironment.WriteFileCommand("lib/next.txt", "n"));
		Assert.True(next.ExitCode == 0, next.Error);
	}

	[Fact]
	public async Task Integrate_Onto_BuildsOnTheGivenTag()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var run = await fixture.RunWritingAsync("example", "a.txt", "a");

		var (exitCode, _, error) = await fixture.IntegrationStartAsync("-runs", run, "-onto", "example@v0");

		Assert.True(exitCode == 0, error);
		var component = Assert.Single((await SingleIntegrationAsync(fixture)).Components);
		Assert.Equal("v0", component.BaseRef);
		Assert.Equal(await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "rev-parse", "v0^{commit}"), component.BaseCommit);
	}

	[Fact]
	public async Task Integrate_ResultsAlreadyInTheBase_AreUpToDateAndNothingIsTagged()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var run = await fixture.RunWritingAsync("example", "a.txt", "a");
		Assert.Equal(0, (await fixture.IntegrationStartAsync("-runs", run)).ExitCode);
		var first = await SingleIntegrationAsync(fixture);

		var (exitCode, _, error) = await fixture.IntegrationStartAsync("-runs", run, "-onto", $"example@{Tag(first)}");

		Assert.True(exitCode == 0, error);
		var second = (await Store(fixture).ListLatestAsync()).Single(record => record.IntegrationId != first.IntegrationId);
		var component = Assert.Single(second.Components);
		Assert.Equal(ResultStatus.Unchanged, component.ResultStatus);
		Assert.Equal(StepOutcome.UpToDate, Assert.Single(component.Steps).Outcome);
		Assert.Null(component.ResultTag);
	}

	[Fact]
	public async Task Advance_FastForwardsTheBaseBranchButNeverMovesATag()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "a.txt", "a");
		await fixture.RunWritingAsync("example", "b.txt", "b");
		Assert.Equal(0, (await fixture.IntegrationStartAsync("-runs", "all")).ExitCode);
		var record = await SingleIntegrationAsync(fixture);
		var source = fixture.SourceRepo("example");

		var (exitCode, output, error) = await fixture.BassiaAsync("integration", "advance", record.IntegrationId);

		Assert.True(exitCode == 0, error);
		Assert.Contains("Advanced example:main", output);
		Assert.Equal(await TestEnvironment.GitAsync(source, "rev-parse", $"{Tag(record)}^{{commit}}"), await TestEnvironment.GitAsync(source, "rev-parse", "main"));
		Assert.True(Assert.Single((await SingleIntegrationAsync(fixture)).Components).Advanced);

		// An integration built on a tag has no branch to advance.
		var third = await fixture.RunWritingAsync("example", "c.txt", "c");
		Assert.Equal(0, (await fixture.IntegrationStartAsync("-runs", third, "-onto", "example@v0")).ExitCode);
		var stale = (await Store(fixture).ListLatestAsync()).Single(candidate => candidate.IntegrationId != record.IntegrationId);
		var (staleExit, _, staleError) = await fixture.BassiaAsync("integration", "advance", stale.IntegrationId);

		Assert.Equal(1, staleExit);
		Assert.Contains("is not a branch", staleError);
	}

	[Fact]
	public async Task Advance_RefusesWhenTheBranchMovedSinceTheIntegration()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var first = await fixture.RunWritingAsync("example", "a.txt", "a");
		var second = await fixture.RunWritingAsync("example", "b.txt", "b");
		Assert.Equal(0, (await fixture.IntegrationStartAsync("-runs", first)).ExitCode);
		var early = await SingleIntegrationAsync(fixture);
		Assert.Equal(0, (await fixture.IntegrationStartAsync("-runs", second)).ExitCode);
		var late = (await Store(fixture).ListLatestAsync()).Single(record => record.IntegrationId != early.IntegrationId);
		Assert.Equal(0, (await fixture.BassiaAsync("integration", "advance", late.IntegrationId)).ExitCode);

		var (exitCode, _, error) = await fixture.BassiaAsync("integration", "advance", early.IntegrationId);

		Assert.Equal(1, exitCode);
		Assert.Contains("'main' moved since the integration", error);
		Assert.Equal(await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "rev-parse", $"{Tag(late)}^{{commit}}"),
			await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "rev-parse", "main"));
	}

	[Fact]
	public void Record_RoundTripsThroughToml()
	{
		var record = new IntegrationRecord
		{
			IntegrationId = IntegrationRecord.NewId(),
			Status = "partial",
			Created = RunMetadata.Timestamp(),
			Resolver = "claude -p \"x\"",
			WorkspacePath = "/tmp/w"
		};
		record.Runs.Add("agent-run-1");
		var component = new ComponentIntegration { Name = "app", BaseRef = "main", BaseCommit = "abc", ResultTag = "integration/x/0", ResultStatus = ResultStatus.Pushed };
		var step = new IntegrationStep { RunId = "agent-run-1", Rationale = "say \"hi\"\nplease", SourceTag = "agent-run/1/0", SourceCommit = "def", Triage = Triage.FastForward, Strategy = MergeStrategy.Semantic, Outcome = StepOutcome.Failed, Note = "n" };
		step.Conflicts.Add("a.txt");
		step.ConflictsWith.Add("agent-run-2");
		component.Steps.Add(step);
		record.Components.Add(component);

		var copy = IntegrationRecord.FromToml(record.ToToml());

		Assert.Equal(record.ToToml(), copy.ToToml());
		var copied = Assert.Single(Assert.Single(copy.Components).Steps);
		Assert.Equal(Triage.FastForward, copied.Triage);
		Assert.Equal("say \"hi\"\nplease", copied.Rationale);
		Assert.Equal(["a.txt"], copied.Conflicts);
	}

	/// <summary>
	/// A resolver that behaves differently for one run: for <paramref name="runId"/> it runs <paramref name="special"/>,
	/// for every other run it writes <c>same.txt</c> as <c>merged</c>.
	/// </summary>
	private static string ResolverFor(string runId, string special) =>
		OperatingSystem.IsWindows()
			? $"if \"%BASSIA_RUN_ID%\"==\"{runId}\" ({special}) else (echo merged> same.txt)"
			: $"if [ \"$BASSIA_RUN_ID\" = \"{runId}\" ]; then {special}; else echo merged > same.txt; fi";

	[Fact]
	public async Task Integrate_BriefThatCannotBeWritten_FailsThatStepAndStillRecordsTheIntegration()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "same.txt", "from-first");
		var second = await fixture.RunWritingAsync("example", "same.txt", "from-second");
		var third = await fixture.RunWritingAsync("example", "same.txt", "from-third");

		// While resolving the second run, the resolver puts a folder where the third run's brief has to be written.
		var blockBrief = OperatingSystem.IsWindows()
			? $"mkdir ..\\example.{RunMetadata.Key(third)}.merge.md & echo merged> same.txt"
			: $"mkdir ../example.{RunMetadata.Key(third)}.merge.md && echo merged > same.txt";
		var (exitCode, _, error) = await fixture.IntegrationStartAsync("-runs", "all", "-resolve", ResolverFor(second, blockBrief));

		Assert.Equal(1, exitCode);
		Assert.Contains("status = \"partial\"", error);
		var record = await SingleIntegrationAsync(fixture);
		Assert.Equal("partial", record.Status);
		var steps = Assert.Single(record.Components).Steps;
		Assert.Equal(StepOutcome.Resolved, steps.Single(step => step.RunId == second).Outcome);
		var failed = steps.Single(step => step.RunId == third);
		Assert.Equal(StepOutcome.Failed, failed.Outcome);
		Assert.StartsWith("the resolver could not be run:", failed.Note);

		// What did merge is published, and advance accepts the finished integration.
		Assert.Equal("merged", (await ShowAsync(fixture, "example", Tag(record), "same.txt")).Trim());
		Assert.Equal(0, (await fixture.BassiaAsync("integration", "advance", record.IntegrationId)).ExitCode);
	}

	[Fact]
	public async Task Integrate_FilesAFailedResolverLeftBehind_AreNotCommittedByTheNextStep()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "same.txt", "from-first");
		var second = await fixture.RunWritingAsync("example", "same.txt", "from-second");
		var third = await fixture.RunWritingAsync("example", "same.txt", "from-third");
		var strayAndFail = OperatingSystem.IsWindows() ? "echo stray> stray.txt & exit /b 4" : "echo stray > stray.txt; exit 4";

		var (exitCode, _, _) = await fixture.IntegrationStartAsync("-runs", "all", "-resolve", ResolverFor(second, strayAndFail));

		Assert.Equal(1, exitCode);
		var record = await SingleIntegrationAsync(fixture);
		Assert.Equal(StepOutcome.Failed, record.AllSteps.Single(step => step.RunId == second).Outcome);
		Assert.Equal(StepOutcome.Resolved, record.AllSteps.Single(step => step.RunId == third).Outcome);
		Assert.Equal("README.md\nsame.txt", (await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "ls-tree", "--name-only", "-r", Tag(record))).Replace("\r", ""));
	}

	[Fact]
	public async Task Integrate_GitMergeFailingForAnotherReasonThanAConflict_FailsTheStepWithGitsMessageAndSkipsTheResolver()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "a.txt", "a");
		await fixture.RunWritingAsync("example", "b.txt", "b");

		// A pre-merge-commit hook, installed through a global git config, refuses every merge commit git makes.
		var hooks = Path.Combine(fixture.Root, "hooks");
		Directory.CreateDirectory(hooks);
		var hook = Path.Combine(hooks, "pre-merge-commit");
		await File.WriteAllTextAsync(hook, "#!/bin/sh\necho blocked by the hook >&2\nexit 1\n");
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		var globalConfig = Path.Combine(fixture.Root, "gitconfig");
		await File.WriteAllTextAsync(globalConfig, $"[core]\n\thooksPath = {hooks.Replace('\\', '/')}\n");
		var stdin = Path.Combine(fixture.Root, "resolver-called.md");
		var previous = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
		Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", globalConfig);
		(int ExitCode, string Output, string Error) result;
		try
		{
			result = await fixture.IntegrationStartAsync("-runs", "all", "-resolve", TestEnvironment.CaptureStdinCommand(stdin));
		}
		finally
		{
			Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previous);
		}

		Assert.Equal(1, result.ExitCode);
		Assert.False(File.Exists(stdin), "the resolver was called for a merge that did not conflict");
		var steps = (await SingleIntegrationAsync(fixture)).AllSteps.ToList();
		Assert.All(steps, step => Assert.Equal(StepOutcome.Failed, step.Outcome));
		Assert.All(steps, step => Assert.Equal(MergeStrategy.Syntactic, step.Strategy));
		Assert.All(steps, step => Assert.Contains("blocked by the hook", step.Note));
	}
}
