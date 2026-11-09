namespace Bassia.Tests;

using Bassia.Core.Model;
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
	public async Task ContextSpec_FromAProfile_ShapesTheFolder_GuardsItsBoundary_AndWorkspaceContextChangesIt()
	{
		await InitAsync();
		await OkAsync(Root, "workspace", "create", "base");
		Directory.CreateDirectory(Path.Combine(Workspace("base"), "api"));
		Directory.CreateDirectory(Path.Combine(Workspace("base"), "web"));
		await File.WriteAllTextAsync(Path.Combine(Workspace("base"), "api", "a.cs"), "a\n");
		await File.WriteAllTextAsync(Path.Combine(Workspace("base"), "web", "w.js"), "w\n");
		await File.WriteAllTextAsync(Path.Combine(Workspace("base"), ".bassia.toml"), "[context.profiles]\napi = [\"*\", \"api/\"]\n");
		await OkAsync(Root, "workspace", "land", "base");

		var created = await OkAsync(Root, "workspace", "create", "api-fix", "-context", "@api");
		Assert.Equal(new[] { "*", "api/" }, ((TomlArray)created["context"]).Cast<string>());
		Assert.Equal(2L, created["files_in_folder"]);
		Assert.True(File.Exists(Path.Combine(Workspace("api-fix"), "api", "a.cs")));
		Assert.False(File.Exists(Path.Combine(Workspace("api-fix"), "web", "w.js")));
		Assert.True(Directory.Exists(Path.Combine(Workspace("api-fix"), "web")));

		var shown = await OkAsync(Root, "workspace", "context", "api-fix");
		Assert.Equal(2L, shown["paths_in_context"]);

		// A write outside the context is refused, naming the path, by every command that snapshots.
		await File.WriteAllTextAsync(Path.Combine(Workspace("api-fix"), "web", "x.js"), "x\n");
		var refused = await FailAsync(Root, "workspace", "status", "api-fix");
		Assert.Contains("web/x.js", (string)refused["error"]);
		Assert.Contains("web/x.js", (string)(await FailAsync(Root, "workspace", "land", "api-fix"))["error"]);
		File.Delete(Path.Combine(Workspace("api-fix"), "web", "x.js"));

		await File.WriteAllTextAsync(Path.Combine(Workspace("api-fix"), "api", "a.cs"), "a2\n");
		var landed = await OkAsync(Root, "workspace", "land", "api-fix", "-keep");
		Assert.Equal("landed", landed["outcome"]);
		Assert.Equal("w\n", (string)(await OkAsync(Root, "mainline", "cat", "web/w.js"))["content"]);

		var widened = await OkAsync(Root, "workspace", "context", "api-fix", "-add", "web/");
		Assert.Equal(new[] { "*", "api/", "web/" }, ((TomlArray)widened["context"]).Cast<string>());
		Assert.Equal(1L, widened["files_added"]);
		Assert.Equal("w\n", await File.ReadAllTextAsync(Path.Combine(Workspace("api-fix"), "web", "w.js")));
		await OkAsync(Root, "workspace", "undo", "api-fix");
		Assert.False(File.Exists(Path.Combine(Workspace("api-fix"), "web", "w.js")));
		var all = await OkAsync(Root, "workspace", "context", "api-fix", "-set", "all");
		Assert.Equal(new[] { "all" }, ((TomlArray)all["context"]).Cast<string>());

		var missing = await FailAsync(Root, "workspace", "create", "nope", "-context", "@nope");
		Assert.Contains("defines: api", (string)missing["error"]);
	}

	[Fact]
	public async Task LoadRules_AreGone_AndSayWhatReplacedThem()
	{
		await InitAsync();
		var (code, _, stderr) = await RunInDirectoryAsync(Root, "workspace", "create", "x", "-load", "api/");
		Assert.Equal(2, code);
		Assert.Contains("-context", stderr);
		(code, _, stderr) = await RunInDirectoryAsync(Root, "workspace", "load", "x");
		Assert.Equal(2, code);
		Assert.Contains("workspace context", stderr);
		(code, _, stderr) = await RunInDirectoryAsync(Root, "config", "set", "-key", "workspace.load", "-value", "api/");
		Assert.Equal(2, code);
		Assert.Contains("context-spec", stderr);

		// A [load] table left on mainline is not read; a new workspace says to rename it.
		await OkAsync(Root, "workspace", "create", "base");
		await File.WriteAllTextAsync(Path.Combine(Workspace("base"), ".bassia.toml"), "[load]\ndefault = [\"*\"]\n");
		await OkAsync(Root, "workspace", "land", "base");
		Assert.Contains("[context]", (string)(await FailAsync(Root, "workspace", "create", "next"))["error"]);
		await OkAsync(Root, "workspace", "create", "next", "-context", "all");
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
	public async Task AgentStart_WritingOutsideItsContext_FailsTheRun_AndLandsNothing()
	{
		await InitAsync();
		var inside = await OkAsync(Root, "agent", "start", "-name", "inside", "-land", "-context", "docs/", "-run",
			(OperatingSystem.IsWindows() ? "mkdir docs && " : "mkdir -p docs && ") + WriteFileCommand("docs/notes.txt", "inside"));
		Assert.Equal("landed", ((TomlTable)inside["land"])["outcome"]);
		Assert.Equal(new[] { "docs/" }, ((TomlArray)inside["context"]).Cast<string>());

		var outside = await FailAsync(Root, "agent", "start", "-name", "outside", "-land", "-context", "docs/", "-run", WriteFileCommand("notes.txt", "outside"));
		Assert.Equal("failed", ((TomlTable)outside["run"])["status"]);
		Assert.Contains("notes.txt", (string)((TomlTable)outside["run"])["last_line"]);
		Assert.False(outside.ContainsKey("land"));
		var (_, _, error) = await RunInDirectoryAsync(Root, "mainline", "cat", "notes.txt");
		Assert.Contains("notes.txt", error);
	}

	[Fact]
	public async Task AgentStart_SnapshotsTheWorkspaceWhileTheAgentWorks()
	{
		await InitAsync();
		await OkAsync(Root, "config", "set", "-key", "agent.snapshot_quiet", "-value", "1");
		var command = OperatingSystem.IsWindows()
			? "echo one> a.txt & ping -n 4 127.0.0.1 > nul & echo two> b.txt"
			: "echo one > a.txt; sleep 3; echo two > b.txt";
		var run = await OkAsync(Root, "agent", "start", "-name", "settle", "-run", command);
		var runId = (string)run["run_id"];

		// The workspace's operation log holds a snapshot of a.txt alone, taken while the agent was still working.
		using var monorepo = Monorepo.Open(Root);
		var workspace = await monorepo.Repo.ResolveWorkspaceAsync("settle");
		var snapshots = new List<string>();
		for (ObjectId? op = workspace.Head; op is { } id;)
		{
			var operation = await monorepo.Repo.Objects.ReadOperationAsync(id);
			if (operation.Run == runId && operation.Description.StartsWith("snapshot:", StringComparison.Ordinal))
			{
				snapshots.Add(operation.Description);
			}

			op = operation.Parents.Count > 0 ? operation.Parents[0] : null;
		}

		Assert.Contains("snapshot: a.txt", snapshots);
		Assert.Contains(snapshots, snapshot => snapshot.Contains("b.txt"));
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
	public void AgentWait_WaitsForAFinishedRunsProcess_WhichMayStillBeLanding()
	{
		// A run is recorded as done before it lands; while its process lives on this machine it is still finishing.
		using var self = System.Diagnostics.Process.GetCurrentProcess();
		var run = new RunRecord("run-test", "true", null, "agent", null, RunStatus.Succeeded, DateTimeOffset.UtcNow,
			Host: Environment.MachineName, Pid: Environment.ProcessId);
		Assert.False(Agents.AgentRunner.IsAlive(run));
		Assert.True(Agents.AgentRunner.IsFinishing(run));

		// Not when the process is gone, runs elsewhere, started after the run (a reused id), or the run is still running.
		using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
		exited.WaitForExit();
		Assert.False(Agents.AgentRunner.IsFinishing(run with { Pid = exited.Id }));
		Assert.False(Agents.AgentRunner.IsFinishing(run with { Host = "elsewhere" }));
		Assert.False(Agents.AgentRunner.IsFinishing(run with { Started = self.StartTime.ToUniversalTime().AddHours(-1) }));
		Assert.False(Agents.AgentRunner.IsFinishing(run with { Status = RunStatus.Running }));
	}

	[Fact]
	public void RunWords_AreQuotedBackIntoOneShellLine_KeepingShellSyntax()
	{
		Assert.Equal("echo hi > a.txt", Bassia.Cli.Invocation.JoinRest(["echo hi > a.txt"]));
		if (OperatingSystem.IsWindows())
		{
			Assert.Equal("echo \"a b\" && dir", Bassia.Cli.Invocation.JoinRest(["echo", "a b", "&&", "dir"]));
			return;
		}

		Assert.Equal("npm test && echo $HOME > *.log", Bassia.Cli.Invocation.JoinRest(["npm", "test", "&&", "echo", "$HOME", ">", "*.log"]));
		Assert.Equal("printf '%s\\n' 'it'\\''s \"$USER\"' '' '`x`'", Bassia.Cli.Invocation.JoinRest(["printf", "%s\\n", "it's \"$USER\"", "", "`x`"]));
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

		// bassia-04acae8b: the dashboard is gone; Studio is the web UI.
		var (web, _, webError) = await RunInDirectoryAsync(Root, "web");
		Assert.Equal(2, web);
		Assert.Contains("Bassia Studio", webError);
		Assert.DoesNotContain("web", ((TomlTableArray)help["commands"]).Select(command => (string)command["name"]));
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
