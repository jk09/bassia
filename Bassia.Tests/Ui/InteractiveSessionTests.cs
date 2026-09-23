using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Integration;
using Bassia.Ui;
using Spectre.Console.Testing;

namespace Bassia.Tests.Ui;

public class InteractiveSessionTests
{
	/// <summary>
	/// A session over the fixture, driven by a scripted console. The boards are keys, so a command is one
	/// character; the wizards and other prompts still read whole lines, which <see cref="Text"/> pushes.
	/// </summary>
	private sealed class Harness : IAsyncDisposable
	{
		public TestConsole Console { get; } = new();
		public List<(string Select, string Command)> StartedRuns { get; } = [];
		public InteractiveSession Session { get; }
		public RunSupervisor Supervisor { get; }
		public IntegrationSupervisor Integrations { get; }
		public Monorepo Monorepo { get; }

		public Harness(MonorepoFixture fixture, Func<string, string, AgentRunContext, Task<AgentRunOutcome>>? startRun = null)
		{
			Console.Profile.Capabilities.Interactive = true;
			Console.Width(200);
			Monorepo = Monorepo.Load(fixture.Root);
			var git = new GitClient(fixture.Root);
			var store = new RunMetadataStore(git, Monorepo.RunsRepoDir);
			Supervisor = new RunSupervisor((select, command, context) =>
			{
				lock (StartedRuns)
				{
					StartedRuns.Add((select, command));
				}

				return (startRun ?? ((s, c, x) => AgentCommand.StartRunAsync(git, Monorepo, s, c, x)))(select, command, context);
			});

			Integrations = new IntegrationSupervisor((runs, plan, resolver, context) => IntegrationRunner.RunAsync(git, Monorepo, runs, plan, resolver, context));
			Session = new InteractiveSession(Console, Monorepo, store, Supervisor, Integrations)
			{
				// A script that forgets to quit must fail the test, not hang the run.
				IdleTimeout = TimeSpan.FromSeconds(30)
			};
		}

		/// <summary>Board commands: one keystroke each.</summary>
		public Harness Press(params char[] keys)
		{
			foreach (var key in keys)
			{
				Console.Input.PushCharacter(key);
			}

			return this;
		}

		public Harness Key(params ConsoleKey[] keys)
		{
			foreach (var key in keys)
			{
				Console.Input.PushKey(key);
			}

			return this;
		}

		/// <summary>Prompt answers: a whole line followed by enter.</summary>
		public Harness Text(params string[] values)
		{
			foreach (var value in values)
			{
				Console.Input.PushTextWithEnter(value);
			}

			return this;
		}

		public Harness Enter(int times = 1) => Key(Enumerable.Repeat(ConsoleKey.Enter, times).ToArray());

		public async Task<string> RunAsync()
		{
			await Session.RunAsync();
			return Console.Output;
		}

		public async ValueTask DisposeAsync()
		{
			Supervisor.CancelAll();
			Integrations.Cancel();
			await Supervisor.WhenAllSettledAsync();
			await Integrations.WhenSettledAsync();
			Supervisor.Dispose();
			Integrations.Dispose();
		}
	}

	[Fact]
	public async Task ComponentsBoard_DrawsACardPerComponentWithItsFactsAndTheDependencyLines()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("app");
		await fixture.AddComponentAsync("lib");
		await fixture.SetReferencesAsync("app", ("lib", "libs/lib"));
		await using var harness = new Harness(fixture).Press('q');

		var output = await harness.RunAsync();

		Assert.Contains("app", output);
		Assert.Contains("needs: lib@libs/lib", output);
		Assert.Contains("used by: app", output);
		Assert.Contains("1 tags", output);
		// The cards are rectangles joined by an ASCII edge that ends in an arrow head at the referenced component.
		Assert.Contains("┌─", output);
		Assert.Contains("▼", output);
	}

	[Fact]
	public async Task Views_SwitchWithASingleKeyAndTheTabBarFollows()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await using var harness = new Harness(fixture).Press('2', '1', 'q');

		var output = await harness.RunAsync();

		Assert.Contains("1 Components (1)", output);
		Assert.Contains("2 Agentic runs", output);
		Assert.Contains("no runs recorded in", output);
	}

	[Fact]
	public async Task RunsBoard_DrawsACardPerRecordedRunWithItsStatusAndCommand()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var (_, result, error) = await fixture.AgentAsync("-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/a.txt", "a"));
		var runId = TestEnvironment.RunIdOf(result.Length > 0 ? result : error);
		await using var harness = new Harness(fixture).Press('2', 'q');

		var output = await harness.RunAsync();

		Assert.Contains(RunMetadata.ShortKey(runId), output);
		Assert.Contains("COMPLETED", output);
		Assert.Contains("example: pushed", output);
	}

	[Fact]
	public async Task Component_OpensWithRefsGitTreeAndTheRunsThatTouchedIt()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.AddComponentAsync("other");
		var (_, result, error) = await fixture.AgentAsync("-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/a.txt", "a"));
		var runId = TestEnvironment.RunIdOf(result.Length > 0 ? result : error);

		await using var touchedHarness = new Harness(fixture).Enter().Press('q', 'q');
		var touched = await touchedHarness.RunAsync();
		// "other" is the second card, so one step right selects it.
		await using var untouchedHarness = new Harness(fixture).Key(ConsoleKey.RightArrow).Enter().Press('q', 'q');
		var untouched = await untouchedHarness.RunAsync();

		Assert.Contains("Initial commit", touched);
		Assert.Contains("annotated tag", touched);
		Assert.Contains(RunMetadata.ShortKey(runId), touched);
		Assert.Contains(RunMetadata.TagName(runId, 0), touched);
		Assert.Contains("pushed", touched);
		Assert.DoesNotContain(RunMetadata.ShortKey(runId), untouched[untouched.IndexOf("Agentic runs touching", StringComparison.Ordinal)..]);
	}

	[Fact]
	public async Task Tag_CreatesAnAnnotatedTagFromTheBoardAndTheCardCountsItImmediately()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await using var harness = new Harness(fixture).Press('t').Text("main (", "v1", "second baseline").Press('q');

		var output = await harness.RunAsync();

		Assert.Contains("Created annotated tag 'v1' at main.", output);
		Assert.Equal("tag", await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "cat-file", "-t", "v1"));
		Assert.Equal("second baseline", await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "tag", "-l", "--format=%(contents:subject)", "v1"));
		// The board is redrawn after the action, and the card's tag count is refreshed with it.
		Assert.Contains("2 tags", output[output.LastIndexOf("1 Components (1)", StringComparison.Ordinal)..]);
	}

	[Fact]
	public async Task Graph_RendersTheTreeAndExportsMermaidAndSvgToTheConfirmedPaths()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("app");
		await fixture.AddComponentAsync("lib");
		await fixture.SetReferencesAsync("app", "lib");
		await using var harness = new Harness(fixture).Press('g', 'm').Enter().Press('v').Enter().Press('q', 'q');

		var output = await harness.RunAsync();

		Assert.Contains("└─ lib", output);
		var markdown = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "components.md"));
		Assert.Contains("c_app --> c_lib", markdown);
		var svg = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "components.svg"));
		Assert.Contains("<line ", svg);
		Assert.Contains("Written " + Path.Combine(fixture.Root, "components.svg"), output);
	}

	[Fact]
	public async Task Run_OpensWithItsRecordAndTheRemainingLifecycleStubsChangeNothing()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var (_, result, error) = await fixture.AgentAsync("-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/a.txt", "a"));
		var runId = TestEnvironment.RunIdOf(result.Length > 0 ? result : error);
		var tagsBefore = await TestEnvironment.GitAsync(fixture.RunsRepo, "tag", "--list");

		await using var harness = new Harness(fixture);
		harness.Press('2').Enter();
		foreach (var stub in new[] { "Suspend", "Resume", "Hand off" })
		{
			harness.Press('m').Text(stub);
		}

		var output = await harness.Press('q', 'q').RunAsync();

		Assert.Contains("example@v0", output);
		Assert.Contains(RunMetadata.TagName(runId, 0), output);
		foreach (var stub in new[] { "Suspend", "Resume", "Hand off" })
		{
			Assert.Contains($"Not implemented yet: {stub}.", output);
		}

		Assert.Equal(tagsBefore, await TestEnvironment.GitAsync(fixture.RunsRepo, "tag", "--list"));
		Assert.Equal(RunMetadata.TagName(runId, 0), await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "tag", "--list", "agent/*"));
	}

	[Fact]
	public async Task StartRun_ComposesSelectionAndCommandFromThePromptsAndRecordsTheRun()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("app");
		await fixture.AddComponentAsync("lib", "lib-base");
		await fixture.SetReferencesAsync("app", "lib");
		await using var harness = new Harness(fixture);
		harness.Press('n');
		harness.Key(ConsoleKey.Spacebar, ConsoleKey.Enter); // select "app"; "lib" follows through the reference
		harness.Text("v0 (", "lib-base (", "write hello world", "sonnet", "Agent default", "keep it short");
		harness.Enter(); // agent command: keep the default
		harness.Text(TestEnvironment.WriteFileCommand("app/hello.txt", "hi")); // replace the composed command
		harness.Text("y");

		var running = harness.RunAsync();
		await SettleAsync(harness, runs: 1);
		harness.Press('q');
		var output = await running;

		var (select, command) = Assert.Single(harness.StartedRuns);
		Assert.Equal("app@v0,lib@lib-base", select);
		Assert.Equal(TestEnvironment.WriteFileCommand("app/hello.txt", "hi"), command);
		Assert.Contains($"{InteractiveSession.DefaultAgentCommand} --model sonnet \"write hello world Context: keep it short\"", output);
		Assert.Contains("in the background", output);
		var run = Assert.Single(await new RunMetadataStore(new GitClient(fixture.Root), harness.Monorepo.RunsRepoDir).ListLatestAsync());
		Assert.Equal("app@v0,lib@lib-base", run.Select);
		Assert.Equal("completed", run.Status);
	}

	[Fact]
	public async Task StartRun_TheBoardStaysUsableWhileTheAgentIsStillRunning()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await using var harness = new Harness(fixture);
		StartOn(harness, component: 0, TestEnvironment.SleepCommand(60));

		var running = harness.RunAsync();
		await WaitForAsync(() => harness.Supervisor.Cards().Any(card => card.Phase == AgentRunPhase.Agent));

		// While the agent is still working the frontend keeps taking keys: switch to the components board, back
		// to the runs board, then stop the run from there.
		harness.Press('1');
		await WaitForAsync(() => harness.Console.Output.Contains("1 running"));
		harness.Press('2', 'x');
		await SettleAsync(harness, runs: 1);
		harness.Press('q');
		var output = await running;

		Assert.Contains("RUNNING", output);
		Assert.Contains("the agent process tree is being killed", output);
		// The components card counted the live run against its component while it was going.
		Assert.Contains("● 1 running", output);
	}

	[Fact]
	public async Task StartRun_TwoRunsAtOnce_BothExecuteInParallelAndBothAreRecorded()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("app");
		await fixture.AddComponentAsync("lib");
		await using var harness = new Harness(fixture);

		StartOn(harness, component: 0, TestEnvironment.WriteFileCommand("app/first.txt", "x"));
		StartOn(harness, component: 1, TestEnvironment.WriteFileCommand("lib/second.txt", "x"));

		var running = harness.RunAsync();
		await SettleAsync(harness, runs: 2);
		harness.Press('q');
		await running;

		Assert.Equal(2, harness.StartedRuns.Count);
		var runs = await new RunMetadataStore(new GitClient(fixture.Root), harness.Monorepo.RunsRepoDir).ListLatestAsync();
		Assert.Equal(2, runs.Count);
		Assert.All(runs, run => Assert.Equal("completed", run.Status));
		Assert.Equal(["app@v0", "lib@v0"], runs.Select(run => run.Select).Order().ToArray());
	}

	/// <summary>Drives the start-a-run wizard for the <paramref name="component"/>-th component on the board.</summary>
	private static void StartOn(Harness harness, int component, string command)
	{
		harness.Press('n');
		for (var i = 0; i < component; i++)
		{
			harness.Key(ConsoleKey.DownArrow);
		}

		harness.Key(ConsoleKey.Spacebar, ConsoleKey.Enter);
		harness.Text("v0 (", "do it", "Agent default", "Agent default", ""); // tag, prompt, model, effort, context
		harness.Enter(); // agent command: keep the default
		harness.Text(command, "y");
	}

	[Fact]
	public async Task StopRun_KillsTheAgentAndRecordsTheRunAsCancelled()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await using var harness = new Harness(fixture);
		StartOn(harness, component: 0, TestEnvironment.SleepCommand(60));

		var running = harness.RunAsync();
		await WaitForAsync(() => harness.Supervisor.Cards().Any(card => card.Phase == AgentRunPhase.Agent));
		harness.Press('x');
		await SettleAsync(harness, runs: 1);
		harness.Press('q');
		var output = await running;

		Assert.Contains("the agent process tree is being killed", output);
		var run = Assert.Single(await new RunMetadataStore(new GitClient(fixture.Root), harness.Monorepo.RunsRepoDir).ListLatestAsync());
		Assert.Equal("cancelled", run.Status);
		Assert.All(run.Components, component => Assert.Equal(ResultStatus.Pending, component.ResultStatus));
		Assert.Equal("", await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "tag", "--list", "agent/*"));
	}

	[Fact]
	public async Task GitFailure_IsShownInAppAndTheSessionContinues()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await using var harness = new Harness(fixture)
			.Press('t').Text("Enter a commit hash", "0000000", "v1", "message")
			.Enter() // past the error
			.Press('q');

		var output = await harness.RunAsync();

		Assert.Contains("Error", output);
		Assert.Contains("git tag -a v1", output);
		Assert.Equal("", await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "tag", "--list", "v1"));
	}

	[Fact]
	public async Task Help_ListsTheKeys()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await using var harness = new Harness(fixture).Press('?').Enter().Press('q');

		var output = await harness.RunAsync();

		Assert.Contains("switch between the components board", output);
		Assert.Contains("kills the agent process tree", output);
	}

	// ----- integration control panel -----

	private static IntegrationStore IntegrationStoreOf(MonorepoFixture fixture) =>
		new(new RunMetadataStore(new GitClient(fixture.Root), fixture.RunsRepo));

	/// <summary>Waits until the integration the panel started has ended.</summary>
	private static async Task IntegratedAsync(Harness harness)
	{
		await WaitForAsync(() => harness.Integrations.Current is { IsLive: false });
		await harness.Integrations.WhenSettledAsync();
	}

	[Fact]
	public async Task IntegrationPanel_ShowsTheTriageOfTheChosenRunsWithoutChangingAnything()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var first = await fixture.RunWritingAsync("example", "same.txt", "from-first");
		var second = await fixture.RunWritingAsync("example", "same.txt", "from-second");
		await fixture.RunWritingAsync("example", "other.txt", "other");
		var refsBefore = await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "for-each-ref");
		await using var harness = new Harness(fixture).Press('3', 'a', 'q');

		var output = await harness.RunAsync();

		Assert.Contains("3 Integration (3 chosen)", output);
		Assert.Contains("Triage", output);
		Assert.Contains("SYNTAX", output);
		Assert.Contains("SEMANTIC", output);
		Assert.Contains("conflict", output);
		Assert.Contains($"same.txt vs {RunMetadata.ShortKey(first)}", output);
		Assert.Contains("2 by git (syntax)", output);
		Assert.Contains("1 by the resolver (semantic)", output);
		Assert.Contains(RunMetadata.ShortKey(second), output);
		Assert.Equal(refsBefore, await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "for-each-ref"));
		Assert.Empty(await IntegrationStoreOf(fixture).ListLatestAsync());
	}

	[Fact]
	public async Task IntegrationPanel_IntegratesInTheBackgroundGitFirstThenTheResolver()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "same.txt", "from-first");
		await fixture.RunWritingAsync("example", "same.txt", "from-second");
		await using var harness = new Harness(fixture);
		harness.Press('3', 'a', 'i').Text(TestEnvironment.WriteFileCommand("same.txt", "merged"), "y");

		var running = harness.RunAsync();
		await IntegratedAsync(harness);
		harness.Press('q');
		var output = await running;

		Assert.Contains("Integrating in the background", output);
		var record = Assert.Single(await IntegrationStoreOf(fixture).ListLatestAsync());
		Assert.Equal("completed", record.Status);
		Assert.Equal([StepOutcome.Merged, StepOutcome.Resolved], record.AllSteps.Select(step => step.Outcome));
		Assert.Equal("merged", (await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "show", $"{IntegrationRecord.TagName(record.IntegrationId, 0)}:same.txt")).Trim());
		// The finished integration stays on the panel, and the history lists it.
		Assert.Contains("resolved", output);
		Assert.Contains(IntegrationRecord.ShortKey(record.IntegrationId), output);
	}

	[Fact]
	public async Task IntegrationPanel_AStrategyOverrideSkipsAStep()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "a.txt", "a");
		var second = await fixture.RunWritingAsync("example", "b.txt", "b");
		await using var harness = new Harness(fixture);
		harness.Press('3', 'a', 's').Text(RunMetadata.ShortKey(second), "Skip");
		harness.Press('i').Text("y");

		var running = harness.RunAsync();
		await IntegratedAsync(harness);
		harness.Press('q');
		var output = await running;

		Assert.Contains("SKIP", output);
		var record = Assert.Single(await IntegrationStoreOf(fixture).ListLatestAsync());
		Assert.Equal(StepOutcome.Skipped, record.AllSteps.Single(step => step.RunId == second).Outcome);
		Assert.Equal("README.md\na.txt", (await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "ls-tree", "--name-only", "-r",
			IntegrationRecord.TagName(record.IntegrationId, 0))).Replace("\r", ""));
	}

	[Fact]
	public async Task IntegrationPanel_StopKillsTheResolverAndPublishesNothing()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "same.txt", "from-first");
		await fixture.RunWritingAsync("example", "same.txt", "from-second");
		await using var harness = new Harness(fixture);
		harness.Press('3', 'a', 'i').Text(TestEnvironment.SleepCommand(60), "y");

		var running = harness.RunAsync();
		await WaitForAsync(() => harness.Integrations.Current?.Message.Contains("asking the resolver") == true);
		harness.Press('x');
		await IntegratedAsync(harness);
		harness.Press('q');
		var output = await running;

		Assert.Contains("Stopping the integration", output);
		var record = Assert.Single(await IntegrationStoreOf(fixture).ListLatestAsync());
		Assert.Equal("cancelled", record.Status);
		Assert.Equal("", await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "tag", "--list", "integration/*"));
	}

	[Fact]
	public async Task RunDetail_IntegrateKey_ChoosesTheRunOnTheIntegrationPanel()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var run = await fixture.RunWritingAsync("example", "a.txt", "a");
		await using var harness = new Harness(fixture).Press('2').Enter().Press('i', 'q');

		var output = await harness.RunAsync();

		Assert.Contains($"Run {RunMetadata.ShortKey(run)} is chosen for integration", output);
		Assert.Contains("3 Integration (1 chosen)", output);
		Assert.Contains("fast-forward", output);
	}

	[Fact]
	public async Task IntegrationPanel_AdvanceFastForwardsTheBaseBranch()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "a.txt", "a");
		Assert.Equal(0, (await fixture.IntegrateAsync("-runs", "all")).ExitCode);
		var record = Assert.Single(await IntegrationStoreOf(fixture).ListLatestAsync());
		await using var harness = new Harness(fixture).Press('3', 'v').Text(IntegrationRecord.ShortKey(record.IntegrationId), "y").Press('q');

		var output = await harness.RunAsync();

		Assert.Contains("Advanced example:main", output);
		Assert.Equal(await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "rev-parse", $"{IntegrationRecord.TagName(record.IntegrationId, 0)}^{{commit}}"),
			await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "rev-parse", "main"));
	}

	[Fact]
	public async Task IntegrationPanel_DetailsShowTheStepsAndTheirBriefs()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.RunWritingAsync("example", "same.txt", "from-first");
		await fixture.RunWritingAsync("example", "same.txt", "from-second");
		Assert.Equal(0, (await fixture.IntegrateAsync("-runs", "all", "-resolve", TestEnvironment.WriteFileCommand("same.txt", "merged"))).ExitCode);
		var record = Assert.Single(await IntegrationStoreOf(fixture).ListLatestAsync());
		await using var harness = new Harness(fixture).Press('3', 'd').Text(IntegrationRecord.ShortKey(record.IntegrationId)).Press('q', 'q');

		var output = await harness.RunAsync();

		Assert.Contains($"Integration {IntegrationRecord.ShortKey(record.IntegrationId)}", output);
		Assert.Contains("brief:", output);
		Assert.Contains(".merge.md", output);
		Assert.Contains(IntegrationRecord.TagName(record.IntegrationId, 0), output);
	}

	/// <summary>Waits until <paramref name="runs"/> runs have been started and every one of them has ended.</summary>
	private static async Task SettleAsync(Harness harness, int runs)
	{
		await WaitForAsync(() => harness.Supervisor.Cards().Count == runs);
		await harness.Supervisor.WhenAllSettledAsync();
	}

	private static async Task WaitForAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "the condition was not reached in time");
			await Task.Delay(50);
		}
	}

	[Theory]
	[InlineData(null, null, "", "claude -p \"do it\"")]
	[InlineData("opus", "high", "", "claude -p --model opus --effort high \"do it\"")]
	[InlineData(null, null, "see README", "claude -p \"do it Context: see README\"")]
	public void ComposeCommand_AppendsFlagsAndQuotesThePrompt(string? model, string? effort, string context, string expected)
	{
		Assert.Equal(expected, InteractiveSession.ComposeCommand("claude -p", model, effort, "do it", context));
	}

	[Fact]
	public void ComposeCommand_EscapesQuotesInsideThePrompt()
	{
		Assert.Equal("claude -p \"say \\\"hi\\\"\"", InteractiveSession.ComposeCommand("claude -p", null, null, "say \"hi\"", null));
	}
}
