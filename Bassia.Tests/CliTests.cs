namespace Bassia.Tests;

using Tomlyn.Model;
using static Bassia.Tests.TestEnvironment;

/// <summary>The command line over a local monorepo: workspaces, landing, conflicts, runs, mainline, leases, help.</summary>
public sealed class CliTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose() => temp.Dispose();

	private string Root => temp.Combine("repo");

	private string Workspace(string name) => Path.Combine(Root, "workspaces", name);

	private async Task InitAsync() => await OkAsync(temp.Path, "init", "-path", Root);

	[Fact]
	public async Task AWorkspace_LandsOnMainline_AsTheNextSeq()
	{
		await InitAsync();
		var created = await OkAsync(Root, "workspace", "create", "first", "-intent", "add a readme");
		Assert.Equal(0L, created["base_seq"]);
		await File.WriteAllTextAsync(Path.Combine(Workspace("first"), "README.md"), "hello\n");

		var status = await OkAsync(Workspace("first"), "workspace", "status");
		Assert.Equal("first", status["workspace"]);
		Assert.Contains("1 added", (string)status["message"]);

		var check = await OkAsync(Root, "workspace", "check", "first");
		Assert.Equal("would-land", check["outcome"]);
		Assert.Equal(true, check["can_land"]);

		var landed = await OkAsync(Root, "workspace", "land", "first");
		Assert.Equal(1L, landed["seq"]);
		var log = await OkAsync(Root, "mainline", "log");
		var landings = (TomlTableArray)log["landing"];
		Assert.Equal("add a readme", landings[0]["intent"]);
		var cat = await OkAsync(Root, "mainline", "cat", "README.md");
		Assert.Equal("hello\n", cat["content"]);
		var list = await OkAsync(Root, "workspace", "list");
		Assert.False(list.ContainsKey("workspace") && ((TomlTableArray)list["workspace"]).Count > 0);
	}

	[Fact]
	public async Task TwoWorkspacesRewritingAFile_TheSecondNeedsAReplay_OrSyncsAndResolves()
	{
		await InitAsync();
		await OkAsync(Root, "workspace", "create", "base");
		await File.WriteAllTextAsync(Path.Combine(Workspace("base"), "a.txt"), "base\n");
		await OkAsync(Root, "workspace", "land", "base");
		await OkAsync(Root, "workspace", "create", "one");
		await OkAsync(Root, "workspace", "create", "two");
		await File.WriteAllTextAsync(Path.Combine(Workspace("one"), "a.txt"), "one\n");
		await File.WriteAllTextAsync(Path.Combine(Workspace("two"), "a.txt"), "two\n");
		await OkAsync(Root, "workspace", "land", "one");

		var check = await OkAsync(Root, "workspace", "check", "two");
		Assert.Equal("needs-replay", check["outcome"]);
		Assert.Equal("a.txt", ((TomlTableArray)check["conflict"])[0]["path"]);
		var refused = await FailAsync(Root, "workspace", "land", "two");
		Assert.Equal("needs-replay", refused["outcome"]);

		var sync = await OkAsync(Root, "workspace", "sync", "two");
		Assert.Equal(new TomlArray { "a.txt" }, sync["conflicts"]);
		Assert.Contains("<<<<<<< workspace", await File.ReadAllTextAsync(Path.Combine(Workspace("two"), "a.txt")));
		await FailAsync(Root, "workspace", "land", "two");
		await File.WriteAllTextAsync(Path.Combine(Workspace("two"), "a.txt"), "one and two\n");
		await OkAsync(Root, "workspace", "land", "two");
		Assert.Equal("one and two\n", (await OkAsync(Root, "mainline", "cat", "a.txt"))["content"]);
	}

	[Fact]
	public async Task StackedAndClonedWorkspaces_StandInForBranches()
	{
		await InitAsync();
		await OkAsync(Root, "workspace", "create", "lib");
		await File.WriteAllTextAsync(Path.Combine(Workspace("lib"), "lib.cs"), "v2\n");
		var app = await OkAsync(Root, "workspace", "create", "app", "-on", "lib");
		Assert.True(File.Exists(Path.Combine(Workspace("app"), "lib.cs")));
		await File.WriteAllTextAsync(Path.Combine(Workspace("app"), "app.cs"), "uses v2\n");
		Assert.Equal("rejected", (await OkAsync(Root, "workspace", "check", "app"))["outcome"]);

		var clone = await OkAsync(Root, "workspace", "clone", "-from", "lib", "-name", "lib-alt");
		Assert.True(File.Exists(Path.Combine(Workspace("lib-alt"), "lib.cs")));
		Assert.NotEqual(app["change_id"], clone["change_id"]);

		await OkAsync(Root, "workspace", "land", "lib");
		await OkAsync(Root, "workspace", "land", "app");
		var ls = await OkAsync(Root, "mainline", "ls");
		Assert.Equal(new[] { "app.cs", "lib.cs" }, ((TomlTableArray)ls["entry"]).Select(entry => (string)entry["name"]));
	}

	[Fact]
	public async Task Undo_RestoresTheFolder_AndTheLogShowsOperations()
	{
		await InitAsync();
		await OkAsync(Root, "workspace", "create", "w");
		await File.WriteAllTextAsync(Path.Combine(Workspace("w"), "a.txt"), "1\n");
		await OkAsync(Root, "workspace", "snapshot", "w");
		await File.WriteAllTextAsync(Path.Combine(Workspace("w"), "a.txt"), "2\n");
		await OkAsync(Root, "workspace", "undo", "w");
		Assert.Equal("1\n", await File.ReadAllTextAsync(Path.Combine(Workspace("w"), "a.txt")));
		var log = await OkAsync(Root, "workspace", "log", "w");
		Assert.True(((TomlTableArray)log["operation"]).Count >= 3);
		await OkAsync(Root, "workspace", "restore", "w", "-op", "0");
		Assert.False(File.Exists(Path.Combine(Workspace("w"), "a.txt")));
	}

	[Fact]
	public async Task AgentStart_RunsInItsOwnWorkspace_RecordsTheRun_AndLands()
	{
		await InitAsync();
		var run = await OkAsync(Root, "agent", "start", "-name", "writer", "-land", "-run", WriteFileCommand("notes.txt", "from the agent"));
		Assert.Equal("writer", run["name"]);
		Assert.Equal("landed", ((TomlTable)run["land"])["outcome"]);
		var runId = (string)run["run_id"];
		var shown = await OkAsync(Root, "agent", "show", runId);
		Assert.Equal("succeeded", ((TomlTable)shown["run"])["status"]);
		var list = await OkAsync(Root, "agent", "list");
		Assert.Single((TomlTableArray)list["run"]);
		Assert.Contains("from the agent", (string)(await OkAsync(Root, "mainline", "cat", "notes.txt"))["content"]);
		var landing = await OkAsync(Root, "mainline", "show");
		Assert.Contains("notes.txt", ((TomlTable)landing["recipe"])["command"].ToString());

		var failed = await FailAsync(Root, "agent", "start", "-name", "broken", "-land", "-run", OperatingSystem.IsWindows() ? "exit /b 3" : "exit 3");
		Assert.Equal("failed", ((TomlTable)failed["run"])["status"]);
		Assert.False(failed.ContainsKey("land"));
	}

	[Fact]
	public async Task AgentReplay_ReRunsTheTaskOnTheNewHead_KeepingTheChangeId()
	{
		await InitAsync();
		await OkAsync(Root, "agent", "start", "-name", "seed", "-land", "-run", WriteFileCommand("a.txt", "seed"));
		var first = await OkAsync(Root, "agent", "start", "-name", "first", "-run", WriteFileCommand("a.txt", "first"));
		await OkAsync(Root, "agent", "start", "-name", "second", "-land", "-run", WriteFileCommand("a.txt", "second"));
		Assert.Equal("needs-replay", (await OkAsync(Root, "workspace", "check", "first"))["outcome"]);

		var replay = await OkAsync(Root, "agent", "replay", "first", "-land");
		Assert.Equal(first["change_id"], replay["change_id"]);
		Assert.Equal("landed", ((TomlTable)replay["land"])["outcome"]);
		var head = await OkAsync(Root, "mainline", "show");
		Assert.Equal(first["change_id"], head["change_id"]);
		Assert.Equal("replaced", (await OkAsync(Root, "workspace", "show", "first"))["status"]);
	}

	[Fact]
	public async Task Leases_GuardLandings_WhereThePolicyRequiresThem()
	{
		await InitAsync();
		await OkAsync(Root, "workspace", "create", "policy");
		await File.WriteAllTextAsync(Path.Combine(Workspace("policy"), ".bassia.toml"), "[leases]\nrequired = [\"billing\"]\n");
		await OkAsync(Root, "workspace", "land", "policy");
		await OkAsync(Root, "workspace", "create", "bill");
		Directory.CreateDirectory(Path.Combine(Workspace("bill"), "billing"));
		await File.WriteAllTextAsync(Path.Combine(Workspace("bill"), "billing", "a.cs"), "a\n");
		Assert.Equal("rejected", (await FailAsync(Root, "workspace", "land", "bill"))["outcome"]);

		var lease = await OkAsync(Root, "lease", "acquire", "billing", "-workspace", "bill");
		await FailAsync(Root, "lease", "acquire", "billing", "-workspace", "policy");
		Assert.Single((TomlTableArray)(await OkAsync(Root, "lease", "list"))["lease"]);
		await OkAsync(Root, "workspace", "land", "bill", "-lease-token", $"{lease["id"]}:{lease["token"]}");
	}

	[Fact]
	public async Task MainlineRestore_PutsAnEarlierTreeBack_AsANewSeq()
	{
		await InitAsync();
		await OkAsync(Root, "agent", "start", "-name", "one", "-land", "-run", WriteFileCommand("a.txt", "one"));
		await OkAsync(Root, "agent", "start", "-name", "two", "-land", "-run", WriteFileCommand("b.txt", "two"));
		var restored = await OkAsync(Root, "mainline", "restore", "1");
		Assert.Equal(3L, restored["seq"]);
		var ls = await OkAsync(Root, "mainline", "ls");
		Assert.Equal(new[] { "a.txt" }, ((TomlTableArray)ls["entry"]).Select(entry => (string)entry["name"]));
		var changed = await OkAsync(Root, "mainline", "changed", "-since", "2");
		Assert.Equal("b.txt", ((TomlTableArray)changed["path"])[0]["path"]);
	}

	[Fact]
	public async Task GcAndScrub_RunOnALocalMonorepo()
	{
		await InitAsync();
		await OkAsync(Root, "agent", "start", "-name", "one", "-land", "-run", WriteFileCommand("a.txt", "one"));
		var gc = await OkAsync(Root, "gc", "-dry-run");
		Assert.Equal(true, gc["dry_run"]);
		var scrub = await OkAsync(Root, "scrub");
		Assert.True((long)scrub["checked"] > 0);
	}

	[Fact]
	public async Task Status_Help_AndReplacedCommands()
	{
		var outside = await OkAsync(temp.Path, "status");
		Assert.Equal(false, outside["in_monorepo"]);
		await InitAsync();
		var status = await OkAsync(Root, "status");
		Assert.Equal("local", status["kind"]);

		var help = await OkAsync(temp.Path, "help");
		Assert.Contains("workspace land", ((TomlTableArray)help["commands"]).Select(command => (string)command["name"]));
		Assert.Contains("workspace", ((TomlTableArray)help["concept"]).Select(concept => (string)concept["name"]));
		await OkAsync(temp.Path, "help", "-all");
		await OkAsync(temp.Path, "help", "workspace", "land");

		var (exit, _, error) = await RunInDirectoryAsync(Root, "component", "list");
		Assert.Equal(2, exit);
		Assert.Contains("bassia import", error);
		var (unknown, _, _) = await RunInDirectoryAsync(Root, "workspace", "frobnicate");
		Assert.Equal(2, unknown);
	}

	[Fact]
	public async Task Config_SetGetUnset()
	{
		await InitAsync();
		await OkAsync(Root, "config", "set", "-key", "agent.model", "-value", "opus");
		Assert.Equal("opus", (await OkAsync(Root, "config", "get", "agent.model"))["value"]);
		await OkAsync(Root, "config", "unset", "agent.model");
		Assert.Equal("default", (await OkAsync(Root, "config", "get", "agent.model"))["source"]);
		var (exit, _, _) = await RunInDirectoryAsync(Root, "config", "get", "no.such");
		Assert.Equal(2, exit);
	}
}
