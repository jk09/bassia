namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: a person sets up a client of a Bassia server and finds their way around - clone, status, version, help,
/// configuration, and the result contract every command keeps (TOML, exit codes, -human).
/// </summary>
[Trait("Scenario", "MonorepoSetup")]
public sealed class MonorepoSetupScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	[ScenarioFact]
	public async Task CloningAServer_GivesAnEmptyClient_AtSeqZero()
	{
		var alice = await CloneAsync("alice");

		var status = await alice.OkAsync("status");
		Assert.True(status.Bool("in_monorepo"));
		Assert.Equal("remote", status.Str("kind"));
		Assert.Equal(0L, status.Long("seq"));
		Assert.Equal(0L, status.Long("open_workspaces"));
		Assert.Equal("alice", status.Str("user"));
		Assert.True(Directory.Exists(Path.Combine(alice.Root, ".bassia")));
	}

	[ScenarioFact]
	public async Task Status_OutsideAMonorepo_SaysSo()
	{
		var alice = await CloneAsync("alice");
		var outside = Path.Combine(Folder, "elsewhere");
		Directory.CreateDirectory(outside);

		var result = await alice.RunInAsync(outside, "status");
		Assert.False(result.Result.TryGetValue("in_monorepo", out var inside) && inside is true);
	}

	[ScenarioFact]
	public async Task EveryCommand_AnswersInToml_WithOkCommandAndMessage()
	{
		var alice = await CloneAsync("alice");

		var version = await alice.RunAsync("version");
		Assert.Equal(0, version.ExitCode);
		Assert.StartsWith(CliResult.Marker, version.Stdout.TrimStart());
		Assert.True(version.Result.Bool("ok"));
		Assert.Equal("version", version.Result.Str("command"));
		Assert.False(string.IsNullOrEmpty(version.Result.Str("version")));

		// A failure is a result on stderr with exit code 1 and an error.
		var failed = await alice.RunAsync("workspace", "show", "no-such-workspace");
		Assert.Equal(1, failed.ExitCode);
		Assert.Contains(CliResult.Marker, failed.Stderr);
		Assert.False(failed.Result.Bool("ok"));
		Assert.Contains("no-such-workspace", failed.Result.Str("error"));

		// A command line that does not parse exits 2.
		Assert.Equal(2, (await alice.RunAsync("no-such-command")).ExitCode);
		Assert.Equal(2, (await alice.RunAsync("workspace", "create", "x", "-no-such-switch", "1")).ExitCode);
	}

	[ScenarioFact]
	public async Task Human_PrintsMarkdownInsteadOfToml_ForTheSameResult()
	{
		var alice = await CloneAsync("alice");
		await alice.LandFilesAsync("readme", "add a readme", new Dictionary<string, string?> { ["README.md"] = "hello\n" });

		var human = await alice.RunAsync("mainline", "log", "-human");
		Assert.Equal(0, human.ExitCode);
		Assert.DoesNotContain(CliResult.Marker, human.Stdout);
		Assert.Contains("add a readme", human.Stdout);
	}

	[ScenarioFact]
	public async Task Help_ListsEveryCommand_AndExplainsEachOne()
	{
		var alice = await CloneAsync("alice");

		var help = await alice.OkAsync("help");
		var commands = help.Tables("commands");
		Assert.NotEmpty(commands);
		var names = commands.Select(command => command.Str("name")).ToList();
		foreach (var expected in new[] { "init", "clone", "status", "workspace create", "workspace land", "workspace conflicts", "mainline log",
			"lease acquire", "agent start", "agent replay", "import", "export" })
		{
			Assert.Contains(expected, names);
		}

		// Every command explains itself, and says whether it only reads.
		foreach (var command in commands)
		{
			Assert.False(string.IsNullOrWhiteSpace(command.Str("summary")), command.Str("name"));
			Assert.True(command.ContainsKey("read_only"), command.Str("name"));
			var one = await alice.RunAsync([.. command.Str("name").Split(' '), "-help"]);
			Assert.True(one.ExitCode == 0, $"{command.Str("name")} -help exited {one.ExitCode}");
		}

		Assert.True(commands.Single(command => command.Str("name") == "mainline log").Bool("read_only"));
		Assert.False(commands.Single(command => command.Str("name") == "workspace land").Bool("read_only"));
	}

	[ScenarioFact]
	public async Task Configuration_IsSetReadAndReset_PerClient()
	{
		var alice = await CloneAsync("alice");
		var bob = await CloneAsync("bob");

		var listed = await alice.OkAsync("config", "list");
		Assert.Contains(listed.Tables("setting"), setting => setting.Str("key") == "agent.command");

		await alice.OkAsync("config", "set", "-key", "agent.model", "-value", "some-model");
		Assert.Equal("some-model", (await alice.OkAsync("config", "get", "-key", "agent.model")).Str("value"));

		// Settings belong to the client folder, not the monorepo.
		Assert.NotEqual("some-model", (await bob.OkAsync("config", "get", "-key", "agent.model")).StrOrNull("value"));

		await alice.OkAsync("config", "unset", "-key", "agent.model");
		Assert.NotEqual("some-model", (await alice.OkAsync("config", "get", "-key", "agent.model")).StrOrNull("value"));

		// An unknown setting is refused.
		Assert.NotEqual(0, (await alice.RunAsync("config", "set", "-key", "no.such.setting", "-value", "1")).ExitCode);
	}

	[ScenarioFact]
	public async Task ALocalMonorepo_WorksWithoutAServer()
	{
		// bassia init: the core in-process over a local-disk blob store (by design) and PostgreSQL.
		var local = await InitLocalAsync("local");
		Assert.Equal("local", (await local.OkAsync("status")).Str("kind"));

		var seq = await local.LandFilesAsync("first", "first landing", new Dictionary<string, string?> { ["a.txt"] = "a\n" });
		Assert.Equal(1L, seq);
		Assert.Equal("a\n", await local.CatAsync("a.txt"));
		Assert.Contains((await local.OkAsync("mainline", "log")).Tables("landing"), landing => landing.Long("seq") == 1 && landing.Str("intent") == "first landing");
	}
}
