namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: coding agents run in workspaces of their own. <c>agent start</c> records the task as the change's replay
/// recipe, runs the agent (<c>agent.command</c>, here a deterministic stand-in for Claude Code, given the prompt the
/// same way) or a shell command in the workspace folder, snapshots what it changed, and with <c>-land</c> lands it.
/// Runs can be detached and followed (<c>list</c>, <c>show</c>, <c>logs</c>, <c>wait</c>, <c>stop</c>), and a run that
/// lost its race is replayed on the new mainline with the same change id.
/// </summary>
[Trait("Scenario", "AgentRuns")]
public sealed class AgentRunScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	private async Task<ScenarioClient> ClientWithStandInAsync(string name = "alice")
	{
		var client = await CloneAsync(name);
		await client.OkAsync("config", "set", "-key", "agent.command", "-value", Agents.StandIn);
		// Snapshots while the agent works are not what these scenarios look at.
		await client.OkAsync("config", "set", "-key", "agent.snapshot_quiet", "-value", "0");
		return client;
	}

	[ScenarioFact]
	public async Task APromptedRun_ChangesItsWorkspace_AndLands()
	{
		var alice = await ClientWithStandInAsync();
		var run = await alice.OkAsync("agent", "start", "-name", "hello", "-land",
			"-prompt", "Create a file named greetings/hello.txt containing exactly the text: Hello, Bassia");

		Assert.Equal("succeeded", run.Table("run").Str("status"));
		Assert.Equal(["greetings/hello.txt"], run.Strings("changed"));
		Assert.Equal("landed", run.Table("land").Str("outcome"));
		Assert.Equal("Hello, Bassia\n", await alice.CatAsync("greetings/hello.txt"));

		// The prompt is the intent, and the replay recipe is recorded with the landing.
		var shown = await alice.OkAsync("mainline", "show", "-seq", run.Table("land").Long("seq").ToString());
		Assert.StartsWith("Create a file named greetings/hello.txt", shown.Str("intent"));
		Assert.Contains("greetings/hello.txt", shown.Table("recipe").Str("prompt"));
	}

	[ScenarioFact]
	public async Task ARunWithoutLand_LeavesItsWorkspaceOpen_ForReview()
	{
		var alice = await ClientWithStandInAsync();
		var run = await alice.OkAsync("agent", "start", "-name", "review-me", "-prompt", "Create a file named draft.md containing exactly the text: draft");
		Assert.Equal("open", run.Str("status"));
		Assert.Equal("draft\n", alice.Read("review-me", "draft.md"));
		Assert.Equal("would-land", (await alice.OkAsync("workspace", "check", "review-me")).Str("outcome"));
		Assert.Equal(0L, await alice.MainSeqAsync());
	}

	[ScenarioFact]
	public async Task AShellCommandRun_IsAnAgentRunToo()
	{
		var alice = await ClientWithStandInAsync();
		var command = OperatingSystem.IsWindows() ? "echo generated> out.txt" : "echo generated > out.txt";
		var run = await alice.OkAsync("agent", "start", "-name", "scripted", "-land", "-run", command);
		Assert.Equal("succeeded", run.Table("run").Str("status"));
		Assert.Equal("generated", (await alice.CatAsync("out.txt")).Trim());
	}

	[ScenarioFact]
	public async Task AFailingAgent_FailsTheRun_AndLandsNothing()
	{
		var alice = await ClientWithStandInAsync();
		var failed = await alice.FailAsync("agent", "start", "-name", "broken", "-land",
			"-prompt", "Create a file named half-done.txt containing exactly the text: half. Fail with exit code 3");
		Assert.Equal("failed", failed.Table("run").Str("status"));
		Assert.Equal(3L, failed.Table("run").Long("exit_code"));
		Assert.Equal(0L, await alice.MainSeqAsync());

		// What it changed is still snapshotted in its workspace, for a look.
		Assert.Contains("half-done.txt", (await alice.OkAsync("workspace", "status", "broken")).Tables("change").Select(change => change.Str("path")));
	}

	[ScenarioFact]
	public async Task TheAgentRuns_InItsWorkspaceFolder_WithTheRunsEnvironment()
	{
		var alice = await ClientWithStandInAsync();
		var command = OperatingSystem.IsWindows()
			? "echo %BASSIA_RUN_ID% %BASSIA_WORKSPACE%> env.txt"
			: "echo \"$BASSIA_RUN_ID $BASSIA_WORKSPACE\" > env.txt";
		var run = await alice.OkAsync("agent", "start", "-name", "env", "-run", command);
		var written = alice.Read("env", "env.txt");
		Assert.Contains(run.Str("run_id"), written);
		Assert.Contains(run.Str("id"), written);
	}

	[ScenarioFact]
	public async Task ADetachedRun_IsFollowedFromAnyShell_ListShowLogsWait()
	{
		var alice = await ClientWithStandInAsync();
		var started = await alice.OkAsync("agent", "start", "-name", "background", "-detach", "-land",
			"-prompt", "Sleep for 1 second. Create a file named later.txt containing exactly the text: done later");
		var runId = started.Str("run_id");

		Assert.Contains((await alice.OkAsync("agent", "list")).Tables("run"), run => run.Str("run_id") == runId);

		var waited = await alice.OkAsync("agent", "wait", runId, "-timeout", "120");
		Assert.Equal("done later\n", await alice.CatAsync("later.txt"));

		var shown = await alice.OkAsync("agent", "show", runId);
		Assert.Equal("succeeded", shown.Table("run").Str("status"));
		Assert.Contains("created later.txt", (await alice.OkAsync("agent", "logs", runId)).Str("output"));
		Assert.Contains((await alice.OkAsync("agent", "list", "-status", "succeeded")).Tables("run"), run => run.Str("run_id") == runId);
		Assert.True(waited.Bool("ok"));
	}

	[ScenarioFact]
	public async Task StoppingARun_EndsItAsStopped_AndKeepsWhatItChangedSoFar()
	{
		var alice = await ClientWithStandInAsync();
		var started = await alice.OkAsync("agent", "start", "-name", "endless", "-detach",
			"-prompt", "Create a file named progress.txt containing exactly the text: started. Sleep for 300 seconds");
		var runId = started.Str("run_id");
		await Eventually.AsyncTrue(() => Task.FromResult(alice.Exists("endless", "progress.txt")), TimeSpan.FromSeconds(60));

		await alice.OkAsync("agent", "stop", runId);
		await Eventually.AsyncTrue(async () => (await alice.OkAsync("agent", "show", runId)).Table("run").Str("status") == "stopped", TimeSpan.FromSeconds(60));
		Assert.Contains("progress.txt", (await alice.OkAsync("workspace", "status", "endless")).Tables("change").Select(change => change.Str("path")));
	}

	[ScenarioFact]
	public async Task ARunThatLostTheRace_IsReplayedOnTheNewMainline_WithTheSameChangeId()
	{
		var alice = await ClientWithStandInAsync();
		await alice.LandFilesAsync("seed", "seed", new Dictionary<string, string?> { ["settings.ini"] = "color=blue\nsize=10\n" });

		var slow = await alice.OkAsync("agent", "start", "-name", "add-mode",
			"-prompt", "Append the line mode=dark to the file settings.ini");
		var changeId = slow.Str("change_id");
		await alice.LandFilesAsync("faster", "a faster change", new Dictionary<string, string?> { ["settings.ini"] = "color=red\nsize=10\n" });
		Assert.Equal("needs-replay", (await alice.OkAsync("workspace", "check", "add-mode")).Str("outcome"));

		var replay = await alice.OkAsync("agent", "replay", "add-mode", "-land");
		Assert.Equal("add-mode", replay.Str("replay_of"));
		Assert.Equal(changeId, replay.Str("change_id"));
		Assert.Equal("landed", replay.Table("land").Str("outcome"));
		Assert.Equal("color=red\nsize=10\nmode=dark\n", await alice.CatAsync("settings.ini"));
		Assert.Equal("replaced", (await alice.OkAsync("workspace", "show", "add-mode")).Str("status"));

		var landing = (await alice.OkAsync("mainline", "log", "-limit", "1")).Tables("landing").Single();
		Assert.Equal(changeId, landing.Str("change_id"));
	}

	[ScenarioFact]
	public async Task ARun_CanWorkInAnExistingWorkspace_OrStackOnAnother()
	{
		var alice = await ClientWithStandInAsync();
		await alice.OkAsync("workspace", "create", "existing");
		alice.Write("existing", "base.txt", "base\n");
		await alice.OkAsync("agent", "start", "-workspace", "existing", "-prompt", "Append the line more to the file base.txt");
		Assert.Equal("base\nmore\n", alice.Read("existing", "base.txt"));

		var stacked = await alice.OkAsync("agent", "start", "-name", "on-top", "-on", "existing", "-prompt", "Append the line even more to the file base.txt");
		Assert.Equal("base\nmore\neven more\n", alice.Read("on-top", "base.txt"));
		Assert.Equal("rejected", (await alice.OkAsync("workspace", "check", "on-top")).Str("outcome"));
		Assert.True(stacked.Bool("ok"));
	}

	[ScenarioFact]
	public async Task TheModel_IsPassedToTheAgent()
	{
		var alice = await ClientWithStandInAsync();
		var run = await alice.OkAsync("agent", "start", "-name", "modelled", "-model", "some-model", "-prompt", "Create a file named m.txt containing exactly the text: m");
		Assert.Contains("model: some-model", (await alice.OkAsync("agent", "logs", run.Str("run_id"))).Str("output"));
	}

	[ScenarioFact]
	public async Task SeveralDetachedAgents_LandInParallel_OnDisjointPaths()
	{
		var alice = await ClientWithStandInAsync();
		var runs = new List<string>();
		for (var i = 1; i <= 4; i++)
		{
			runs.Add((await alice.OkAsync("agent", "start", "-name", $"parallel-{i}", "-detach", "-land",
				"-prompt", $"Create a file named area{i}/file.txt containing exactly the text: agent {i}")).Str("run_id"));
		}

		foreach (var run in runs)
		{
			await alice.OkAsync("agent", "wait", run, "-timeout", "180");
		}

		Assert.Equal(4L, await alice.MainSeqAsync());
		for (var i = 1; i <= 4; i++)
		{
			Assert.Equal($"agent {i}\n", await alice.CatAsync($"area{i}/file.txt"));
		}
	}

	[ScenarioFact]
	public async Task ARunWithLeases_HoldsThemWhileItRuns_AndLandsWithThem()
	{
		var alice = await ClientWithStandInAsync();
		await alice.LandFilesAsync("policy", "require leases", new Dictionary<string, string?>
		{
			[".bassia.toml"] = "[leases]\nrequired = [\"billing/\"]\n", ["billing/rates.txt"] = "1\n"
		});

		var run = await alice.OkAsync("agent", "start", "-name", "leased", "-land", "-lease", "billing/",
			"-prompt", "Replace 1 with 2 in the file billing/rates.txt");
		Assert.Equal("landed", run.Table("land").Str("outcome"));
		Assert.Equal("2\n", await alice.CatAsync("billing/rates.txt"));
	}
}

/// <summary>Real Claude Code (opt-in, <c>BASSIA_SCENARIO_CLAUDE=1</c>): the same scenario with the agent Bassia runs by default.</summary>
[Trait("Scenario", "AgentRuns")]
[Trait("Agent", "Claude")]
public sealed class ClaudeAgentScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	[ScenarioFact(Needs.Claude)]
	public async Task ClaudeCode_CarriesOutAPrompt_AndTheRunLands()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("config", "set", "-key", "agent.command", "-value", Agents.Claude);
		await alice.LandFilesAsync("seed", "seed", new Dictionary<string, string?> { ["README.md"] = "# Demo\n" });

		var run = await alice.OkAsync("agent", "start", "-name", "claude-hello", "-land",
			"-prompt", "Create a file named hello.txt containing exactly the text: Hello, Bassia. Do not change any other file.");
		Assert.Equal("succeeded", run.Table("run").Str("status"));
		Assert.Equal("landed", run.Table("land").Str("outcome"));
		Assert.Contains("Hello, Bassia", await alice.CatAsync("hello.txt"));
		Assert.Equal("# Demo\n", await alice.CatAsync("README.md"));
	}

	[ScenarioFact(Needs.Claude)]
	public async Task ClaudeCode_ReplaysItsTask_AfterLosingTheRace()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("config", "set", "-key", "agent.command", "-value", Agents.Claude);
		await alice.LandFilesAsync("seed", "seed", new Dictionary<string, string?> { ["names.txt"] = "alice\nbob\n" });

		var run = await alice.OkAsync("agent", "start", "-name", "claude-carol",
			"-prompt", "Append a line containing exactly carol to the end of names.txt. Do not change any other file.");
		await alice.LandFilesAsync("faster", "a faster change", new Dictionary<string, string?> { ["names.txt"] = "alice\nbob\ndave\n" });

		var replay = await alice.OkAsync("agent", "replay", "claude-carol", "-land");
		Assert.Equal(run.Str("change_id"), replay.Str("change_id"));
		var names = (await alice.CatAsync("names.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		Assert.Contains("carol", names);
		Assert.Contains("dave", names);
	}
}
