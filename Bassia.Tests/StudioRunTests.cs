namespace Bassia.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Server;
using Bassia.Studio;
using Bassia.Studio.Runs;

/// <summary>
/// Agentic runs started from Studio on the local run host: each run is a real <c>bassia agent job</c> process that
/// clones the in-process server, runs the task as <c>bassia agent start</c> does and reports its output to Studio.
/// Tasks are shell commands, so no agent or network is needed.
/// </summary>
public sealed class StudioRunTests : IAsyncLifetime
{
	private const string Token = "run-token";
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(120);
	private readonly TempDirectory temp = new();
	private ServerApp server = null!;
	private StudioApp studio = null!;
	private MonorepoClient client = null!;

	private RunService Runs => studio.Runs;

	public async Task InitializeAsync()
	{
		server = new ServerApp(new ServerOptions { Data = temp.Combine("server"), Postgres = Backends.RequiredPostgres(), Url = "http://127.0.0.1:0", Token = Token, Chunks = new ChunkParameters(1024, 4096, 16384) });
		await server.StartAsync();
		var options = new StudioOptions
		{
			Server = server.Address!,
			Token = Token,
			Url = "http://127.0.0.1:0",
			Cache = temp.Combine("cache"),
			RunHost = "local",
			Bassia = $"\"{Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet"}\" \"{typeof(ProgramCli).Assembly.Location}\"",
			Runs = temp.Combine("runs")
		};
		studio = new StudioApp(options);
		await studio.StartAsync();
		client = new MonorepoClient(options with { Cache = temp.Combine("cache-test") });
		var actions = new StudioActions(client, new StudioUser("seed", StudioRole.Admin));
		var seed = await actions.CreateAsync("seed", "Seed");
		await actions.SaveTextAsync(seed.Id, "src/hello.txt", "hello\n", null);
		Assert.Equal(LandOutcome.Landed, (await actions.LandAsync(seed.Id)).Outcome);
	}

	public async Task DisposeAsync()
	{
		client.Dispose();
		await studio.DisposeAsync();
		await server.DisposeAsync();
		temp.Dispose();
	}

	private static StudioUser Ann => new("ann", StudioRole.Contributor);

	private static string LongCommand => OperatingSystem.IsWindows() ? "ping -n 120 127.0.0.1" : "sleep 120";

	private static async Task<StudioRun> UntilAsync(StudioRun run, Func<StudioRun, bool> condition)
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		while (!condition(run))
		{
			Assert.True(clock.Elapsed < Patience, $"Run {run.RunId} is {run.Status}: {run.Message}\n{string.Join('\n', run.Output)}");
			await Task.Delay(100);
		}

		return run;
	}

	private static Task<StudioRun> FinishedAsync(StudioRun run) => UntilAsync(run, r => r.Finished is not null);

	private async Task<string?> MainlineTextAsync(string path)
	{
		var main = await server.Repo.GetMainAsync();
		return await server.Repo.Trees.GetAsync(main.Tree, path) is { } leaf ? Encoding.UTF8.GetString(await server.Repo.Objects.ReadContentAsync(leaf)).TrimEnd() : null;
	}

	[PostgresFact]
	public async Task Run_ExecutesAsAJob_StreamsOutput_AndLands()
	{
		var run = await Runs.StartAsync(new RunSpec(Command: "echo made by the job> made.txt && echo line one && echo line two", Name: "job-run", Land: true), Ann);
		await FinishedAsync(run);
		Assert.True(run.Status == RunStatus.Succeeded, $"{run.Status}: {run.Message}\n{string.Join('\n', run.Output)}");
		Assert.Contains("line one", run.Output);
		Assert.Contains("line two", run.Output);
		Assert.Equal("made by the job", await MainlineTextAsync("made.txt"));

		// The run is recorded on its workspace as the CLI records it, owned by the Studio user.
		var workspace = await server.Repo.ResolveWorkspaceAsync(run.WorkspaceId!);
		Assert.Equal("job-run", workspace.Name);
		Assert.Equal("ann", workspace.Record.Owner);
		Assert.Equal(run.RunId, workspace.Record.Run!.RunId);
		Assert.Equal(WorkspaceStatus.Landed, workspace.Record.Status);
		var view = Assert.Single(await Runs.ListAsync(), item => item.RunId == run.RunId);
		Assert.Equal("job-run", view.WorkspaceName);
	}

	[PostgresFact]
	public async Task Stop_AsksTheJobToStop_AndKeepsWhatItChanged()
	{
		var command = (OperatingSystem.IsWindows() ? "echo partial> partial.txt && " : "echo partial> partial.txt && ") + LongCommand;
		var run = await Runs.StartAsync(new RunSpec(Command: command, Name: "long"), Ann);
		await UntilAsync(run, r => r.Status == RunStatus.Running);
		await Assert.ThrowsAsync<StudioForbiddenException>(() => Runs.StopAsync(run.RunId, new StudioUser("bob", StudioRole.Contributor)));
		await Runs.StopAsync(run.RunId, Ann);
		await FinishedAsync(run);
		Assert.Equal(RunStatus.Stopped, run.Status);
		var workspace = await server.Repo.ResolveWorkspaceAsync(run.WorkspaceId!);
		Assert.Equal(RunStatus.Stopped, workspace.Record.Run!.Status);
		var tree = (await server.Repo.Objects.ReadCommitAsync(workspace.Record.Working)).Tree;
		Assert.NotNull(await server.Repo.Trees.GetAsync(tree, "partial.txt"));
	}

	[PostgresFact]
	public async Task Replay_RerunsTheRecordedTaskOnTheNewMainline_KeepingTheChange()
	{
		var first = await Runs.StartAsync(new RunSpec(Command: "echo from the task> src/hello.txt", Name: "task"), Ann);
		await FinishedAsync(first);
		Assert.Equal(RunStatus.Succeeded, first.Status);

		// Meanwhile someone else lands a change to the same file: the run's workspace needs a replay.
		var actions = new StudioActions(client, new StudioUser("cat", StudioRole.Contributor));
		var other = await actions.CreateAsync("other");
		var leaf = await server.Repo.Trees.GetAsync((await server.Repo.Objects.ReadCommitAsync(other.Record.Working)).Tree, "src/hello.txt");
		await actions.SaveTextAsync(other.Id, "src/hello.txt", "hello from cat\n", leaf!.Id);
		Assert.Equal(LandOutcome.Landed, (await actions.LandAsync(other.Id)).Outcome);
		var annActions = new StudioActions(client, Ann);
		Assert.Equal(LandOutcome.NeedsReplay, (await annActions.CheckAsync(first.WorkspaceId!)).Outcome);
		var change = (await server.Repo.Objects.ReadCommitAsync((await server.Repo.ResolveWorkspaceAsync(first.WorkspaceId!)).Record.Working)).ChangeId;

		var replay = await Runs.StartAsync(new RunSpec(ReplayOf: first.WorkspaceId, Land: true), Ann);
		await FinishedAsync(replay);
		Assert.True(replay.Status == RunStatus.Succeeded, $"{replay.Status}: {replay.Message}\n{string.Join('\n', replay.Output)}");
		Assert.Equal("from the task", await MainlineTextAsync("src/hello.txt"));
		var main = await server.Repo.GetMainAsync();
		Assert.Equal(change, main.Commit.ChangeId);
		Assert.Equal(WorkspaceStatus.Replaced, (await server.Repo.ResolveWorkspaceAsync(first.WorkspaceId!)).Record.Status);
	}

	[PostgresFact]
	public async Task FailedTask_IsReportedAsFailed()
	{
		var run = await Runs.StartAsync(new RunSpec(Command: "echo trying && exit 3", Name: "fails"), Ann);
		await FinishedAsync(run);
		Assert.Equal(RunStatus.Failed, run.Status);
		Assert.Contains("trying", run.Output);
	}

	[PostgresFact]
	public async Task Reports_NeedTheRunsToken()
	{
		var run = await Runs.StartAsync(new RunSpec(Command: LongCommand, Name: "guarded"), Ann);
		using var http = new HttpClient { BaseAddress = new Uri(studio.Address!) };
		using var forged = await http.PostAsJsonAsync($"/runs/report/{run.RunId}", new RunReport(run.RunId, null, ["forged"], RunStatus.Succeeded, true));
		Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
		Assert.DoesNotContain("forged", run.Output);
		await Runs.StopAsync(run.RunId, Ann);
		await FinishedAsync(run);
	}

	[PostgresFact]
	public async Task Starting_NeedsAContributorAndAValidTask()
	{
		await Assert.ThrowsAsync<StudioForbiddenException>(() => Runs.StartAsync(new RunSpec(Command: "echo x"), new StudioUser("rita", StudioRole.Reader)));
		await Assert.ThrowsAsync<VersionException>(() => Runs.StartAsync(new RunSpec(), Ann));
		await Assert.ThrowsAsync<VersionException>(() => Runs.StartAsync(new RunSpec(Prompt: "a", Command: "b"), Ann));
		var seedWorkspace = (await server.Repo.ListWorkspacesAsync(true)).Single(state => state.Name == "seed");
		await Assert.ThrowsAsync<StudioForbiddenException>(() => Runs.StartAsync(new RunSpec(ReplayOf: seedWorkspace.Id), Ann));

		var disabled = new RunService(client, new StudioOptions { Server = server.Address! }, null);
		await Assert.ThrowsAsync<VersionException>(() => disabled.StartAsync(new RunSpec(Command: "echo x"), Ann));
	}

	[PostgresFact]
	public void Arguments_PutTheCommandLast()
	{
		var start = RunService.Arguments(new RunSpec(Command: "make test -j 4", On: "base", Name: "n", Land: true, Leases: ["src", "docs"], Load: ["src/"]), "run-1");
		Assert.Equal(["agent", "start", "-run-id", "run-1", "-on", "base", "-name", "n", "-load", "src/", "-land", "-lease", "src,docs", "-run", "make test -j 4"], start);
		var replay = RunService.Arguments(new RunSpec(ReplayOf: "w123", Land: true), "run-2");
		Assert.Equal(["agent", "replay", "w123", "-run-id", "run-2", "-land"], replay);
	}

	[PostgresFact]
	public void ContainerAppsJob_TemplateCarriesTheRunAndTheJobsSecrets()
	{
		var options = new ContainerAppsJobOptions("/subscriptions/s/resourceGroups/g/providers/Microsoft.App/jobs/agents", "registry.example/bassia-worker:1",
			SecretEnvironment: new Dictionary<string, string> { ["BASSIA_JOB_TOKEN"] = "server-token", ["ANTHROPIC_API_KEY"] = "anthropic-api-key" });
		var launch = new RunLaunch("run-x", new Dictionary<string, string> { [JobVariables.Server] = "https://server", [JobVariables.ReportToken] = "t" });
		var container = Assert.Single(ContainerAppsJobRunHost.Template(options, launch).Containers);
		Assert.Equal("registry.example/bassia-worker:1", container.Image);
		Assert.Equal(["bassia"], container.Command);
		Assert.Equal(["agent", "job"], container.Args);
		Assert.Contains(container.Env, env => env.Name == JobVariables.Server && env.Value == "https://server");
		Assert.Contains(container.Env, env => env.Name == "BASSIA_JOB_TOKEN" && env.SecretRef == "server-token" && env.Value is null);
		Assert.Contains(container.Env, env => env.Name == "ANTHROPIC_API_KEY" && env.SecretRef == "anthropic-api-key");
	}

	[PostgresFact]
	public void Options_ParseRunHostSwitches()
	{
		var options = StudioOptions.Parse(["-server", "http://s", "-run-host", "aca", "-aca-job", "/jobs/j", "-aca-image", "img", "-aca-secrets", "A=a, B=b"]);
		Assert.Equal("aca", options.RunHost);
		Assert.Equal(new Dictionary<string, string> { ["A"] = "a", ["B"] = "b" }, options.AcaSecretEnvironment());
		Assert.IsType<ContainerAppsJobRunHost>(StudioApp.CreateRunHost(options));
		Assert.Equal(["dotnet", "/opt/my tools/Bassia.dll"], StudioOptions.SplitCommand("dotnet \"/opt/my tools/Bassia.dll\""));
		Assert.Throws<ArgumentException>(() => StudioApp.CreateRunHost(options with { AcaImage = null }));
	}
}
