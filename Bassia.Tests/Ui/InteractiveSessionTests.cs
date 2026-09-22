using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Ui;
using Spectre.Console.Testing;

namespace Bassia.Tests.Ui;

public class InteractiveSessionTests
{
	/// <summary>A session over the fixture, driven by a scripted console. Menus use search, so a choice is typed by name.</summary>
	private sealed class Harness
	{
		public TestConsole Console { get; } = new();
		public List<(string Select, string Command)> StartedRuns { get; } = [];
		public InteractiveSession Session { get; }
		public Monorepo Monorepo { get; }

		public Harness(MonorepoFixture fixture, Func<string, string, Task<AgentRunOutcome>>? startRun = null)
		{
			Console.Profile.Capabilities.Interactive = true;
			Console.Width(200);
			Monorepo = Monorepo.Load(fixture.Root);
			var git = new GitClient(fixture.Root);
			var store = new RunMetadataStore(git, Monorepo.RunsRepoDir);
			Session = new InteractiveSession(Console, Monorepo, store, startRun ?? ((select, command) =>
			{
				StartedRuns.Add((select, command));
				return AgentCommand.StartRunAsync(git, Monorepo, select, command);
			}));
		}

		public Harness Choose(params string[] choices)
		{
			foreach (var choice in choices)
			{
				Console.Input.PushTextWithEnter(choice);
			}

			return this;
		}

		public Harness Enter(int times = 1)
		{
			for (var i = 0; i < times; i++)
			{
				Console.Input.PushKey(ConsoleKey.Enter);
			}

			return this;
		}

		public async Task<string> RunAsync()
		{
			await Session.RunAsync();
			return Console.Output;
		}
	}

	[Fact]
	public async Task Components_ListsEveryRegisteredComponentWithSourceAndReferences()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("app");
		await fixture.AddComponentAsync("lib");
		await fixture.SetReferencesAsync("app", ("lib", "libs/lib"));
		var harness = new Harness(fixture).Choose("Components", "Back", "Quit");

		var output = await harness.RunAsync();

		Assert.Contains("app", output);
		Assert.Contains("lib (at libs/lib)", output);
		Assert.Contains(harness.Monorepo.FindComponent("lib")!.Url, output);
	}

	[Fact]
	public async Task Graph_RendersTheDagAndExportsMermaidAndSvgToTheConfirmedPaths()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("app");
		await fixture.AddComponentAsync("lib");
		await fixture.SetReferencesAsync("app", "lib");
		var harness = new Harness(fixture)
			.Choose("Components", "Show dependency graph", "Export as Markdown").Enter()
			.Choose("Export as SVG").Enter()
			.Choose("Back", "Back", "Quit");

		var output = await harness.RunAsync();

		Assert.Contains("└─ lib", output);
		var markdown = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "components.md"));
		Assert.Contains("c_app --> c_lib", markdown);
		var svg = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "components.svg"));
		Assert.Contains("<line ", svg);
		Assert.Contains("Written " + Path.Combine(fixture.Root, "components.svg"), output);
	}

	[Fact]
	public async Task Component_ShowsRefsAndGitTreeAndCreatesAnAnnotatedTagThatAppearsWithoutRestarting()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "tag", "light", "main");
		var harness = new Harness(fixture)
			.Choose("Components", "Open example", "Create annotated tag", "main (", "v1", "second baseline", "Back", "Back", "Quit");

		var output = await harness.RunAsync();

		Assert.Contains("annotated tag", output);
		Assert.Contains("lightweight tag", output);
		Assert.Contains("Initial commit", output);
		Assert.Contains("Created annotated tag 'v1' at main.", output);
		Assert.Equal("tag", await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "cat-file", "-t", "v1"));
		Assert.Equal("second baseline", await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "tag", "-l", "--format=%(contents:subject)", "v1"));
		// The refreshed view after the action lists the new tag.
		Assert.Contains("v1", output[output.LastIndexOf("Branches and tags", StringComparison.Ordinal)..]);
	}

	[Fact]
	public async Task Runs_ListsRecordedRunsAndTheLifecycleStubsChangeNothing()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var (_, result, error) = await fixture.AgentAsync("-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/a.txt", "a"));
		var runId = TestEnvironment.RunIdOf(result.Length > 0 ? result : error);
		var tagsBefore = await TestEnvironment.GitAsync(fixture.RunsRepo, "tag", "--list");
		var harness = new Harness(fixture)
			.Choose("Agentic runs", $"Open {RunMetadata.ShortKey(runId)}", "Stop", "Suspend", "Resume", "Hand off", "Integrate results", "Back", "Back", "Quit");

		var output = await harness.RunAsync();

		Assert.Contains("completed", output);
		Assert.Contains("example@v0", output);
		Assert.Contains(RunMetadata.TagName(runId, 0), output);
		foreach (var action in new[] { "Stop", "Suspend", "Resume", "Hand off", "Integrate results" })
		{
			Assert.Contains($"Not implemented yet: {action}.", output);
		}

		Assert.Equal(tagsBefore, await TestEnvironment.GitAsync(fixture.RunsRepo, "tag", "--list"));
		Assert.Equal(RunMetadata.TagName(runId, 0), await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "tag", "--list", "agent/*"));
	}

	[Fact]
	public async Task Component_ShowsTheRunsThatTouchedItWithTheirResultTags()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await fixture.AddComponentAsync("other");
		var (_, output, error) = await fixture.AgentAsync("-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/a.txt", "a"));
		var runId = TestEnvironment.RunIdOf(output.Length > 0 ? output : error);

		var touched = await new Harness(fixture).Choose("Components", "Open example", "Back", "Back", "Quit").RunAsync();
		var untouched = await new Harness(fixture).Choose("Components", "Open other", "Back", "Back", "Quit").RunAsync();

		Assert.Contains(RunMetadata.ShortKey(runId), touched);
		Assert.Contains(RunMetadata.TagName(runId, 0), touched);
		Assert.Contains("pushed", touched);
		Assert.DoesNotContain(RunMetadata.ShortKey(runId), untouched[untouched.IndexOf("Agentic runs touching", StringComparison.Ordinal)..]);
	}

	[Fact]
	public async Task StartRun_ComposesSelectionAndCommandFromThePromptsAndShowsTheOutcome()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("app");
		await fixture.AddComponentAsync("lib", "lib-base");
		await fixture.SetReferencesAsync("app", "lib");
		var harness = new Harness(fixture);
		harness.Console.Input.PushTextWithEnter("Start an agentic run");
		harness.Console.Input.PushKey(ConsoleKey.Spacebar); // select "app"; "lib" follows through the reference
		harness.Console.Input.PushKey(ConsoleKey.Enter);
		harness.Choose("v0 (", "lib-base (", "write hello world", "sonnet", "Agent default", "keep it short");
		harness.Enter(); // agent command: keep the default
		harness.Console.Input.PushTextWithEnter(TestEnvironment.WriteFileCommand("app/hello.txt", "hi")); // replace the composed command
		harness.Choose("y");
		harness.Enter(); // continue past the outcome
		harness.Choose("Quit");

		var output = await harness.RunAsync();

		var (select, command) = Assert.Single(harness.StartedRuns);
		Assert.Equal("app@v0,lib@lib-base", select);
		Assert.Equal(TestEnvironment.WriteFileCommand("app/hello.txt", "hi"), command);
		Assert.Contains($"{InteractiveSession.DefaultAgentCommand} --model sonnet \"write hello world Context: keep it short\"", output);
		Assert.Contains("completed", output);
		Assert.Contains("pushed", output);
		var runs = await new RunMetadataStore(new GitClient(fixture.Root), harness.Monorepo.RunsRepoDir).ListLatestAsync();
		Assert.Equal("app@v0,lib@lib-base", Assert.Single(runs).Select);
	}

	[Fact]
	public async Task StartRun_ComponentWithoutAnnotatedTag_ExplainsAndReturnsToTheMenu()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "tag", "-d", "v0");
		var harness = new Harness(fixture);
		harness.Console.Input.PushTextWithEnter("Start an agentic run");
		harness.Console.Input.PushKey(ConsoleKey.Spacebar);
		harness.Console.Input.PushKey(ConsoleKey.Enter);
		harness.Enter().Choose("Quit");

		var output = await harness.RunAsync();

		Assert.Contains("Component 'example' has no annotated tags", output);
		Assert.Empty(harness.StartedRuns);
	}

	[Fact]
	public async Task GitFailure_IsShownInAppAndTheSessionContinues()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var harness = new Harness(fixture)
			.Choose("Components", "Open example", "Create annotated tag", "Enter a commit hash", "0000000", "v1", "message")
			.Enter() // past the error
			.Choose("Quit");

		var output = await harness.RunAsync();

		Assert.Contains("Error", output);
		Assert.Contains("git tag -a v1", output);
		Assert.Equal("", await TestEnvironment.GitAsync(fixture.SourceRepo("example"), "tag", "--list", "v1"));
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
