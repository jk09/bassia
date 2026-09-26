using Bassia.Cli;
using Tomlyn;
using Tomlyn.Model;

namespace Bassia.Tests;

/// <summary>The command line as a whole: grammar, help, errors, and the monorepo, config, component, graph and log commands.</summary>
public class CliSurfaceTests
{
	/// <summary>A printed result as a TOML table, read from the last result marker like a caller would.</summary>
	internal static TomlTable Result(string text)
	{
		var start = text.LastIndexOf(TomlResult.Marker, StringComparison.Ordinal);
		Assert.True(start >= 0, $"no result in: {text}");
		return TomlSerializer.Deserialize<TomlTable>(text[start..])!;
	}

	private static IEnumerable<TomlTable> Tables(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) ? ((TomlTableArray)value).Cast<TomlTable>() : [];

	private static string[] Strings(TomlTable table, string key) => ((TomlArray)table[key]).Cast<string>().ToArray();

	// ----- grammar and help -----

	public static TheoryData<string> CommandNames()
	{
		var data = new TheoryData<string>();
		foreach (var command in CommandTable.Commands)
		{
			data.Add(command.FullName);
		}

		return data;
	}

	[Theory]
	[MemberData(nameof(CommandNames))]
	public async Task Help_ForEveryCommand_IsTomlWithUsageSwitchesAndExamplesOfThatCommand(string command)
	{
		var (exitCode, output, error) = await TestEnvironment.RunAsync(["help", .. command.Split(' ')]);

		Assert.True(exitCode == 0, error);
		var help = Result(output);
		Assert.Equal("help", help["command"]);
		Assert.Equal(command, help["topic"]);
		Assert.StartsWith($"bassia {command}", (string)help["usage"]);
		var examples = Strings(help, "examples");
		Assert.NotEmpty(examples);
		if (command != "help")
		{
			Assert.All(examples, example => Assert.Matches($@"^bassia (-C \S+ )?{command.Split(' ')[0]}\b", example));
		}
		var spec = CommandTable.Commands.Single(candidate => candidate.FullName == command);
		Assert.Equal(spec.Switches.Count(option => !option.Hidden), Tables(help, "switch").Count());
	}

	[Fact]
	public async Task Help_Overview_ListsEveryCommand()
	{
		var (exitCode, output, _) = await TestEnvironment.RunAsync("help");

		Assert.Equal(0, exitCode);
		var names = Tables(Result(output), "entry").Select(entry => (string)entry["name"]).ToList();
		Assert.Equal(CommandTable.Commands.Select(command => command.FullName), names);
		Assert.Contains("COMMAND", (string)Result(output)["table"]);
	}

	[Theory]
	[InlineData("run", "-help")]
	[InlineData("run", "--help")]
	[InlineData("run")]
	public async Task Group_WithoutSubcommand_ShowsItsSubcommands(params string[] args)
	{
		var (exitCode, output, _) = await TestEnvironment.RunAsync(args);

		Assert.Equal(0, exitCode);
		var names = Tables(Result(output), "entry").Select(entry => (string)entry["name"]).ToList();
		Assert.Contains("run start", names);
		Assert.Contains("run stop", names);
	}

	[Fact]
	public async Task HelpSwitch_OnACommand_ShowsThatCommandsHelp()
	{
		var (exitCode, output, _) = await TestEnvironment.RunAsync("run", "start", "-help");

		Assert.Equal(0, exitCode);
		Assert.Equal("run start", Result(output)["topic"]);
	}

	[Fact]
	public async Task UnknownSwitch_IsAUsageErrorNamingItAndTheHelpCommand()
	{
		var (exitCode, output, error) = await TestEnvironment.RunAsync("run", "list", "-colour", "red");

		Assert.Equal(2, exitCode);
		Assert.Empty(output);
		var result = Result(error);
		Assert.False((bool)result["ok"]);
		Assert.Equal("run list", result["command"]);
		Assert.Contains("Unknown switch '-colour'", (string)result["error"]);
		Assert.Contains("bassia help run list", (string)result["error"]);
	}

	[Fact]
	public async Task UnknownSubcommand_IsAUsageErrorListingTheSubcommands()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("run", "frobnicate");

		Assert.Equal(2, exitCode);
		Assert.Contains("has no subcommand 'frobnicate'", error);
		Assert.Contains("start, list, show", error);
	}

	[Fact]
	public async Task SwitchWithoutValue_IsAUsageError()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("run", "list", "-limit");

		Assert.Equal(2, exitCode);
		Assert.Contains("-limit requires a value", error);
	}

	[Fact]
	public async Task Switches_AreCaseInsensitiveAndAcceptTwoDashes()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();

		var (exitCode, output, error) = await monorepo.BassiaAsync("config", "get", "--KEY", "agent.command");

		Assert.True(exitCode == 0, error);
		Assert.Equal("agent.command", Result(output)["key"]);
	}

	[Fact]
	public async Task Version_ReportsTheVersion()
	{
		var (exitCode, output, _) = await TestEnvironment.RunAsync("version");

		Assert.Equal(0, exitCode);
		Assert.False(string.IsNullOrWhiteSpace((string)Result(output)["version"]));
	}

	// ----- status and config -----

	[Fact]
	public async Task Status_SummarizesComponentsRunsAndTheDependencyTree()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		await monorepo.AddComponentAsync("app");
		await monorepo.SetReferencesAsync("app", "lib");
		await monorepo.RunWritingAsync("lib", "a.txt", "a");

		var (exitCode, output, error) = await monorepo.BassiaAsync("status");

		Assert.True(exitCode == 0, error);
		var status = Result(output);
		Assert.Equal(2L, status["components"]);
		Assert.Equal(1L, ((TomlTable)status["runs"])["total"]);
		Assert.Equal(1L, ((TomlTable)status["runs"])["completed"]);
		Assert.Equal("app\n`-- lib\n", status["graph"]);
		Assert.False((bool)status["meta_repo_clean"]);
	}

	[Fact]
	public async Task Config_SetKeepsCommentsCommitsAndIsReadBack()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		var configPath = Path.Combine(monorepo.MetaRepo, "config.toml");

		var (exitCode, output, error) = await monorepo.BassiaAsync("config", "set", "-key", "integration.resolver", "-value", "my-resolver --flag");

		Assert.True(exitCode == 0, error);
		Assert.Equal("config", Result(output)["source"]);
		var text = await File.ReadAllTextAsync(configPath);
		Assert.Contains("resolver = \"my-resolver --flag\"", text);
		Assert.Contains("# Command that resolves a semantic merge", text);
		Assert.Equal("", await TestEnvironment.GitAsync(monorepo.MetaRepo, "status", "--porcelain"));
		Assert.Equal("my-resolver --flag", Monorepo.Load(monorepo.Root).Resolver);

		var (_, get, _) = await monorepo.BassiaAsync("config", "get", "integration.resolver");
		Assert.Equal("my-resolver --flag", Result(get)["value"]);
	}

	[Fact]
	public async Task Config_SetOfAKeyWithoutASection_AddsTheSection()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		var workspace = Path.Combine(monorepo.Root, "elsewhere");

		var (exitCode, _, error) = await monorepo.BassiaAsync("config", "set", "-key", "workspace.path", "-value", workspace);

		Assert.True(exitCode == 0, error);
		Assert.Equal(workspace, Monorepo.Load(monorepo.Root).WorkspaceDir);
	}

	[Fact]
	public async Task Config_List_ShowsDefaultsAndSetValues()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();

		var (exitCode, output, _) = await monorepo.BassiaAsync("config", "list");

		Assert.Equal(0, exitCode);
		var settings = Tables(Result(output), "setting").ToDictionary(setting => (string)setting["key"]);
		Assert.Equal("default", settings["workspace.path"]["source"]);
		Assert.Equal("config", settings["agent.command"]["source"]);
	}

	[Fact]
	public async Task Config_UnknownKey_Fails()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();

		var (exitCode, _, error) = await monorepo.BassiaAsync("config", "set", "-key", "agent.colour", "-value", "red");

		Assert.Equal(1, exitCode);
		Assert.Contains("Unknown configuration key 'agent.colour'", error);
	}

	// ----- components -----

	[Fact]
	public async Task Component_ListShowAndTag_DescribeTheComponents()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		await monorepo.AddComponentAsync("app");
		await monorepo.SetReferencesAsync("app", "lib");

		var (tagExit, tagOutput, tagError) = await monorepo.BassiaAsync("component", "tag", "lib", "-tag", "v1", "-ref", "main");
		Assert.True(tagExit == 0, tagError);
		Assert.Equal("lib@v1", Result(tagOutput)["select"]);
		Assert.Equal("tag", await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "cat-file", "-t", "v1"));

		var (listExit, listOutput, _) = await monorepo.BassiaAsync("component", "list");
		Assert.Equal(0, listExit);
		var components = Tables(Result(listOutput), "component").ToDictionary(component => (string)component["name"]);
		Assert.Equal(["lib"], Strings(components["app"], "references"));
		Assert.Equal(["app"], Strings(components["lib"], "referenced_by"));
		Assert.Equal(2L, components["lib"]["annotated_tags"]);

		var (showExit, showOutput, _) = await monorepo.BassiaAsync("component", "show", "-name", "lib");
		Assert.Equal(0, showExit);
		var show = Result(showOutput);
		Assert.Equal("main", show["default_branch"]);
		Assert.Equal(["v0", "v1"], Tables(show, "tag").Select(tag => (string)tag["name"]).Order().ToArray());
		Assert.All(Tables(show, "tag"), tag => Assert.True((bool)tag["annotated"]));
	}

	[Fact]
	public async Task Component_AddWithReferences_RegistersAndCommits()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		var upstream = Path.Combine(monorepo.Root, "..", "upstream", "lib");

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "add", "-url", upstream, "-name", "app", "-references", "lib:vendor/lib");

		Assert.True(exitCode == 0, error);
		Assert.Equal(["lib:vendor/lib"], Strings(Result(output), "references"));
		var app = Monorepo.Load(monorepo.Root).FindComponent("app")!;
		Assert.Equal(new ComponentReference("lib", "vendor/lib"), Assert.Single(app.References));
		Assert.Equal("", await TestEnvironment.GitAsync(monorepo.MetaRepo, "status", "--porcelain"));
	}

	[Fact]
	public async Task Component_Set_ReplacesReferencesAndRejectsACycleWithoutWriting()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		await monorepo.AddComponentAsync("app");
		var componentsPath = Path.Combine(monorepo.MetaRepo, "components.toml");

		var (setExit, _, setError) = await monorepo.BassiaAsync("component", "set", "app", "-references", "lib");
		Assert.True(setExit == 0, setError);
		Assert.Equal("lib", Assert.Single(Monorepo.Load(monorepo.Root).FindComponent("app")!.References).Name);
		var before = await File.ReadAllTextAsync(componentsPath);

		var (cycleExit, _, cycleError) = await monorepo.BassiaAsync("component", "set", "lib", "-references", "app");

		Assert.Equal(1, cycleExit);
		Assert.Contains("cyclic component reference", cycleError);
		Assert.Equal(before, await File.ReadAllTextAsync(componentsPath));

		var (clearExit, _, clearError) = await monorepo.BassiaAsync("component", "set", "app", "-clear-references");
		Assert.True(clearExit == 0, clearError);
		Assert.Empty(Monorepo.Load(monorepo.Root).FindComponent("app")!.References);
	}

	[Fact]
	public async Task Component_Remove_RefusesAReferencedComponentAndPurgesOnRequest()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		await monorepo.AddComponentAsync("app");
		await monorepo.SetReferencesAsync("app", "lib");

		var (refusedExit, _, refusedError) = await monorepo.BassiaAsync("component", "remove", "lib");
		Assert.Equal(1, refusedExit);
		Assert.Contains("is referenced by app", refusedError);

		var (exitCode, _, error) = await monorepo.BassiaAsync("component", "remove", "app", "-purge");

		Assert.True(exitCode == 0, error);
		Assert.Null(Monorepo.Load(monorepo.Root).FindComponent("app"));
		Assert.False(Directory.Exists(monorepo.SourceRepo("app")));
		Assert.NotNull(Monorepo.Load(monorepo.Root).FindComponent("lib"));
	}

	[Theory]
	[InlineData("board", "+- app ")]
	[InlineData("tree", "app\n`-- lib")]
	[InlineData("mermaid", "graph TD")]
	[InlineData("svg", "<svg")]
	public async Task Graph_DrawsTheDependencies(string format, string expected)
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		await monorepo.AddComponentAsync("app");
		await monorepo.SetReferencesAsync("app", "lib");

		var (exitCode, output, error) = await monorepo.BassiaAsync("graph", "-format", format);

		Assert.True(exitCode == 0, error);
		var graph = (string)Result(output)["graph"];
		Assert.Contains(expected, graph);
		if (format is "board" or "tree")
		{
			Assert.All(graph, c => Assert.True(c is '\n' or (>= ' ' and <= '~'), $"not ASCII: '{c}'"));
		}
	}

	[Fact]
	public async Task Graph_WithOut_WritesTheFile()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		var file = Path.Combine(monorepo.Root, "components.md");

		var (exitCode, output, error) = await monorepo.BassiaAsync("graph", "-format", "mermaid", "-out", file);

		Assert.True(exitCode == 0, error);
		Assert.Contains("```mermaid", await File.ReadAllTextAsync(file));
		Assert.False(Result(output).ContainsKey("graph"));
	}

	// ----- history -----

	[Fact]
	public async Task Log_WithoutComponents_ShowsTheMetaRepoHistory()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");

		var (exitCode, output, error) = await monorepo.BassiaAsync("log");

		Assert.True(exitCode == 0, error);
		var log = Result(output);
		Assert.Contains("* ", (string)log["graph"]);
		Assert.Equal(["Add component 'lib' from '" + Path.Combine(Path.GetDirectoryName(monorepo.Root)!, "upstream", "lib") + "'", "Initialize Bassia meta-repo"],
			Tables(log, "commit").Select(commit => (string)commit["subject"]).ToArray());
	}

	[Fact]
	public async Task Log_OfAComponent_IncludesItsDependenciesInLanes()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		await monorepo.AddComponentAsync("app");
		await monorepo.SetReferencesAsync("app", "lib");

		var (exitCode, output, error) = await monorepo.BassiaAsync("log", "-component", "app");

		Assert.True(exitCode == 0, error);
		var log = Result(output);
		Assert.Equal(["app", "lib"], Strings(log, "components"));
		var graph = ((string)log["graph"]).Split('\n');
		Assert.Equal("app lib", graph[0]);
		Assert.Equal(["app", "lib"], Tables(log, "commit").Select(commit => (string)commit["component"]).Order().ToArray());

		var (onlyExit, onlyOutput, _) = await monorepo.BassiaAsync("log", "-component", "app", "-only");
		Assert.Equal(0, onlyExit);
		Assert.Equal(["app"], Strings(Result(onlyOutput), "components"));
		Assert.Contains("(HEAD -> main, tag: v0) Initial commit", (string)Result(onlyOutput)["graph"]);
	}
}
