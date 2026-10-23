using Bassia.Prompt;
using Bassia.Tutorial;

namespace Bassia.Tests;

/// <summary>
/// The guide: a bare <c>bassia</c> at a terminal and <c>bassia tutorial</c> - the setup of a new monorepo from
/// scripted answers, its refusals, <c>?</c> help, and the menu inside an existing monorepo.
/// </summary>
public class TutorialTests
{
	/// <summary>Runs a command in this process and puts back what a command line changes: the directory and the console.</summary>
	private sealed class InProcessExecutor : ICommandExecutor
	{
		public List<string> Executed { get; } = [];

		public async Task<CommandOutcome> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellation)
		{
			Executed.Add(string.Join(' ', args));
			var directory = Environment.CurrentDirectory;
			var originalOut = Console.Out;
			var originalError = Console.Error;
			var output = new StringWriter();
			var error = new StringWriter();
			Console.SetOut(output);
			Console.SetError(error);
			try
			{
				var exitCode = await ProgramCli.RunAsync([.. args]);
				return new CommandOutcome(exitCode, output.ToString(), error.ToString());
			}
			finally
			{
				Console.SetOut(originalOut);
				Console.SetError(originalError);
				Environment.CurrentDirectory = directory;
			}
		}
	}

	private sealed class CannedBackend(string reply) : ILlmBackend
	{
		public List<LlmRequest> Requests { get; } = [];

		public string Name => "canned";

		public string Description => "canned";

		public Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellation)
		{
			Requests.Add(request);
			return Task.FromResult(reply);
		}
	}

	/// <summary>Runs a command line with <paramref name="answers"/> on stdin, commands in-process and an LLM that is not there.</summary>
	private static async Task<(int ExitCode, string Output, string Error, InProcessExecutor Executor)> RunAsync(string directory, string answers,
		bool interactive = false, ILlmBackend? llm = null, params string[] args)
	{
		var executor = new InProcessExecutor();
		var originalIn = Console.In;
		var originalExecutor = TutorialCommand.ExecutorFactory;
		var originalLlm = TutorialCommand.LlmFactory;
		Console.SetIn(new StringReader(answers));
		TutorialCommand.ExecutorFactory = _ => executor;
		TutorialCommand.LlmFactory = _ => llm ?? throw new PromptException("no LLM in the tests");
		ProgramCli.Interactive = () => interactive;
		try
		{
			var (exitCode, output, error) = await TestEnvironment.RunInDirectoryAsync(directory, args);
			return (exitCode, output, error, executor);
		}
		finally
		{
			Console.SetIn(originalIn);
			TutorialCommand.ExecutorFactory = originalExecutor;
			TutorialCommand.LlmFactory = originalLlm;
			ProgramCli.Interactive = () => false;
		}
	}

	/// <summary>A git repository with one commit, standing in for one of the user's repositories.</summary>
	private static async Task<string> UpstreamAsync(string parent, string name)
	{
		var path = Path.Combine(parent, name);
		Directory.CreateDirectory(path);
		await TestEnvironment.GitAsync(path, "init", "--quiet", "--initial-branch=main");
		await File.WriteAllTextAsync(Path.Combine(path, "README.md"), $"# {name}\n");
		await TestEnvironment.GitAsync(path, "add", "--all");
		await TestEnvironment.GitAsync(path, "commit", "--quiet", "-m", "Initial commit");
		return path;
	}

	private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

	// ----- a bare bassia -----

	[Fact]
	public async Task BareBassia_NotInteractive_PrintsTheFullHelpAndReadsNothing()
	{
		using var temp = new TempDirectory();
		var (exitCode, output, error, executor) = await RunAsync(temp.Path, "y\n");

		Assert.Equal(0, exitCode);
		Assert.StartsWith(TomlResult.Marker, output);
		Assert.Contains("COMMAND", output);
		Assert.Empty(error);
		Assert.Empty(executor.Executed);
		Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
	}

	[Fact]
	public async Task BareBassia_Interactive_PrintsAShortHelpAndOffersASetup_ANoChangesNothing()
	{
		using var temp = new TempDirectory();
		var (exitCode, output, error, executor) = await RunAsync(temp.Path, "n\n", interactive: true);

		Assert.Equal(0, exitCode);
		Assert.Contains("bassia tutorial", output);
		Assert.DoesNotContain(TomlResult.Marker, output);
		Assert.Contains("You are not inside a Bassia monorepo.", error);
		Assert.Contains("Set up a new Bassia monorepo now? [y/N]", error);
		Assert.Contains("No monorepo was set up", error);
		Assert.Empty(executor.Executed);
		Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
	}

	[Fact]
	public async Task BareBassia_InteractiveWithHumanOrHelp_PrintsHelpOnly()
	{
		using var temp = new TempDirectory();
		var (_, help, helpError, _) = await RunAsync(temp.Path, "y\n", interactive: true, args: "help");
		var (_, human, humanError, _) = await RunAsync(temp.Path, "y\n", interactive: true, args: "-human");

		Assert.Contains("COMMAND", help);
		Assert.Contains("COMMAND", human);
		Assert.Empty(helpError + humanError);
	}

	// ----- a new monorepo -----

	[Fact]
	public async Task Tutorial_FromScriptedAnswers_SetsUpTheMonorepoWithItsDependencies()
	{
		using var temp = new TempDirectory();
		var app = await UpstreamAsync(Path.Combine(temp.Path, "upstream"), "app");
		var lib = await UpstreamAsync(Path.Combine(temp.Path, "upstream"), "lib");
		var work = Directory.CreateDirectory(Path.Combine(temp.Path, "work")).FullName;

		var (exitCode, output, error, executor) = await RunAsync(work, Lines(
			app, "",            // app, with the name it is offered
			lib, "core",        // lib, renamed
			"",                 // no more repositories
			"core", "",         // app needs core; core needs nothing
			"mono",             // the root folder, relative to the current one
			"y"), args: "tutorial");

		Assert.True(exitCode == 0, error);
		var root = Path.Combine(work, "mono");
		var monorepo = Monorepo.Load(root);
		Assert.Equal(["core", "app"], monorepo.Components.Select(component => component.Name));
		Assert.Equal(["core"], monorepo.FindComponent("app")!.References.Select(reference => reference.Name));
		Assert.Equal(
			[$"init -path {root}", $"-C {root} component add -url {lib} -name core", $"-C {root} component add -url {app} -references core"],
			executor.Executed);
		Assert.True(error.Contains("> bassia init -path"), error);
		Assert.Contains("Your monorepo is ready", error);
		Assert.Contains("bassia run start -select app", error);
		var result = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(output);
		Assert.Equal(true, result["ok"]);
		Assert.Equal("tutorial", result["command"]);
		Assert.Equal(root, result["root"]);
		Assert.Equal(3, ((Tomlyn.Model.TomlArray)result["commands"]).Count);
	}

	[Fact]
	public async Task Tutorial_RefusesAnUnreadableRepositoryACycleAndANonEmptyFolder_AndAsksAgain()
	{
		using var temp = new TempDirectory();
		var app = await UpstreamAsync(Path.Combine(temp.Path, "upstream"), "app");
		var lib = await UpstreamAsync(Path.Combine(temp.Path, "upstream"), "lib");
		var work = Directory.CreateDirectory(Path.Combine(temp.Path, "work")).FullName;
		Directory.CreateDirectory(Path.Combine(work, "full", "something"));

		var (exitCode, _, error, executor) = await RunAsync(work, Lines(
			Path.Combine(temp.Path, "nothing-here"),
			app, "",
			lib, "app", "",     // a name that is taken, then the offered one
			"",
			"2", "1",           // app needs lib, and lib needs app: a cycle
			"",                 // lib needs nothing after all
			"full",             // not empty
			"mono",
			"n"), args: "tutorial");

		Assert.True(exitCode == 0, error);
		Assert.Contains("There is no git repository Bassia can read", error);
		Assert.Contains("There is already a component named 'app'", error);
		Assert.Contains("That would be a cycle: lib -> app -> lib.", error);
		Assert.Contains("is not empty", error);
		Assert.Contains($"bassia -C {Path.Combine(work, "mono")} component add -url {app} -references lib", error);
		Assert.Contains("Nothing was changed", error);
		Assert.Empty(executor.Executed);
		Assert.False(Directory.Exists(Path.Combine(work, "mono")));
	}

	[Fact]
	public async Task Tutorial_QuestionMark_ExplainsTheStep_AndAsksTheLlmWhenThereIsOne()
	{
		using var temp = new TempDirectory();

		var (_, _, withoutLlm, _) = await RunAsync(temp.Path, Lines("?", "? what is a component"), args: "tutorial");
		var llm = new CannedBackend("A component is one of your repositories.");
		var (exitCode, output, withLlm, _) = await RunAsync(temp.Path, Lines("? what is a component"), llm: llm, args: "tutorial");

		Assert.Contains("Components: Give each repository", withoutLlm);
		Assert.Contains("The LLM could not answer (no LLM in the tests)", withoutLlm);
		Assert.Contains("A component is one of your repositories.", withLlm);
		Assert.Contains("what is a component", Assert.Single(llm.Requests).Prompt);
		Assert.Equal(0, exitCode);
		Assert.Contains("Stopped; nothing was changed.", output);
		Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
	}

	// ----- an existing monorepo -----

	[Fact]
	public async Task Tutorial_InAMonorepo_ShowsItAndSetsADependencyFromTheMenu()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");

		var (exitCode, _, error, executor) = await RunAsync(monorepo.Root, Lines(
			"3",                // the status
			"2", "app", "lib", "y",
			"q"), args: "tutorial");

		Assert.True(exitCode == 0, error);
		Assert.Contains($"You are in the Bassia monorepo at {monorepo.Root}.", error);
		Assert.Contains("2 component(s)", error);
		Assert.Contains("4. Start an agentic run", error);
		Assert.Contains($"-C {monorepo.Root} status -human", executor.Executed);
		Assert.Contains($"-C {monorepo.Root} component set -name app -references lib", executor.Executed);
		Assert.Equal(["lib"], Monorepo.Load(monorepo.Root).FindComponent("app")!.References.Select(reference => reference.Name));
	}

	[Fact]
	public async Task Session_InAnEmptyMonorepo_PointsToAddingComponents_AndHandsOverToTheDashboard()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		var output = new StringWriter();
		var session = new TutorialSession(new StringReader(Lines("6")), output, new InProcessExecutor(),
			() => throw new PromptException("no LLM"), monorepo.Root);

		var outcome = await session.RunAsync(offer: true, CancellationToken.None);

		Assert.Contains("no components yet: start with 1", output.ToString());
		Assert.Equal(["-C", monorepo.Root, "web"], outcome.Then);
	}
}
