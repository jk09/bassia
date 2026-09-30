using System.Diagnostics;
using Bassia.CliCommands.Agent;
using Tomlyn.Model;

namespace Bassia.Tests;

/// <summary>
/// <c>bassia run ...</c> and <c>bassia integration ...</c>: composing, finding, listing, reviewing and stopping runs.
/// The detached cases start the built bassia apphost as a real background process.
/// </summary>
public class RunCommandTests
{
	private static TomlTable Result(string text) => CliSurfaceTests.Result(text);

	private static IEnumerable<TomlTable> Tables(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) ? ((TomlTableArray)value).Cast<TomlTable>() : [];

	private static RunMetadataStore Store(MonorepoFixture monorepo) =>
		new(new Bassia.Git.GitClient(monorepo.Root), monorepo.RunsRepo);

	[Fact]
	public async Task Start_WithPrompt_ComposesTheCommandFromTheConfiguredAgent()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		Assert.Equal(0, (await monorepo.BassiaAsync("config", "set", "-key", "agent.command", "-value", "echo")).ExitCode);

		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "example@v0", "-prompt", "say hi", "-model", "opus");

		Assert.True(exitCode == 0, error);
		var record = await Store(monorepo).LoadLatestAsync(TestEnvironment.RunIdOf(output));
		Assert.Equal("echo --model opus \"say hi\"", record!.Command);
	}

	[Theory]
	[InlineData("-prompt", "x", "-run", "echo", "Give either -run or -prompt")]
	[InlineData("-model", "opus", "-run", "echo", "-model composes the command from -prompt")]
	public async Task Start_WithConflictingCommandSwitches_IsAUsageError(string first, string firstValue, string second, string secondValue, string expected)
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", "example@v0", first, firstValue, second, secondValue);

		Assert.Equal(2, exitCode);
		Assert.Contains(expected, error);
		Assert.False(monorepo.HasRunDirs);
	}

	[Fact]
	public async Task ListShowAndDiff_FindARunByItsShortId()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var runId = await monorepo.RunWritingAsync("example", "a.txt", "hello");
		var shortId = RunMetadata.ShortKey(runId);

		var (listExit, listOutput, _) = await monorepo.BassiaAsync("run", "list");
		Assert.Equal(0, listExit);
		var run = Assert.Single(Tables(Result(listOutput), "run"));
		Assert.Equal(runId, run["run_id"]);
		Assert.Equal("completed", run["status"]);
		Assert.Contains(shortId, (string)Result(listOutput)["table"]);

		var (showExit, showOutput, _) = await monorepo.BassiaAsync("run", "show", shortId);
		Assert.Equal(0, showExit);
		var show = Result(showOutput);
		Assert.Equal(runId, show["run_id"]);
		Assert.False((bool)show["live"]);
		Assert.Contains("COMPLETED", (string)show["card"]);

		var (diffExit, diffOutput, diffError) = await monorepo.BassiaAsync("run", "diff", "-id", shortId, "-patch");
		Assert.True(diffExit == 0, diffError);
		var component = Assert.Single(Tables(Result(diffOutput), "component"));
		Assert.Equal(1L, component["files_changed"]);
		Assert.Equal(["a.txt"], ((TomlArray)component["files"]).Cast<string>());
		Assert.Contains("+hello", (string)component["patch"]);
	}

	[Fact]
	public async Task List_FiltersByStatusAndComponent()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await monorepo.AddComponentAsync("other");
		await monorepo.RunWritingAsync("example", "a.txt", "a");
		await monorepo.RunStartAsync("-select", "other@v0", "-run", TestEnvironment.FailingCommand);

		var (_, failed, _) = await monorepo.BassiaAsync("run", "list", "-status", "failed");
		var (_, example, _) = await monorepo.BassiaAsync("run", "list", "-component", "example");

		Assert.Equal("failed", Assert.Single(Tables(Result(failed), "run"))["status"]);
		Assert.Equal("completed", Assert.Single(Tables(Result(example), "run"))["status"]);
	}

	[Fact]
	public async Task Show_UnknownOrAmbiguousRun_Fails()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await monorepo.RunWritingAsync("example", "a.txt", "a");

		var (exitCode, _, error) = await monorepo.BassiaAsync("run", "show", "0000ffff");

		Assert.Equal(1, exitCode);
		Assert.Contains("Unknown agentic run '0000ffff'", error);
	}

	[Fact]
	public async Task Logs_OfARunThatWasNotDetached_Fails()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var runId = await monorepo.RunWritingAsync("example", "a.txt", "a");

		var (exitCode, _, error) = await monorepo.BassiaAsync("run", "logs", runId);

		Assert.Equal(1, exitCode);
		Assert.Contains("was not started with -detach", error);
	}

	[Fact]
	public async Task Stop_OfAFinishedRun_Fails_AndOfAStaleRun_RecordsItCancelled()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var runId = await monorepo.RunWritingAsync("example", "a.txt", "a");

		var (finishedExit, _, finishedError) = await monorepo.BassiaAsync("run", "stop", runId);
		Assert.Equal(1, finishedExit);
		Assert.Contains("is not live", finishedError);

		// A run recorded as started with no process behind it: what a killed or crashed bassia leaves.
		var store = Store(monorepo);
		var record = (await store.LoadLatestAsync(runId))!;
		record.Status = "started";
		await store.CommitAsync(record);
		var (_, showOutput, _) = await monorepo.BassiaAsync("run", "show", runId);
		Assert.True((bool)Result(showOutput)["stale"]);

		var (exitCode, output, error) = await monorepo.BassiaAsync("run", "stop", runId);

		Assert.True(exitCode == 0, error);
		Assert.Equal("cancelled", Result(output)["status"]);
		Assert.Equal("cancelled", (await store.LoadLatestAsync(runId))!.Status);
	}

	[Fact]
	public async Task RetryAndAbandon_WorkUnderTheirNewNames()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var runId = await monorepo.RunWritingAsync("example", "a.txt", "a");

		// Retrying a completed run finds nothing left to do.
		var (retryExit, retryOutput, retryError) = await monorepo.BassiaAsync("run", "retry", runId);
		Assert.True(retryExit == 0, retryError);
		Assert.Equal("completed", Result(retryOutput)["status"]);

		var (exitCode, output, error) = await monorepo.BassiaAsync("run", "abandon", "-id", RunMetadata.ShortKey(runId));

		Assert.True(exitCode == 0, error);
		Assert.Equal("abandoned", Result(output)["status"]);
		Assert.False(Directory.Exists(monorepo.RunDir(runId)));
	}

	[Fact]
	public async Task Stop_OfARunHostedByAFrontend_CancelsItThroughItsJob()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var repo = Monorepo.Load(monorepo.Root);
		var output = new List<string>();

		// What 'bassia web' does: the run executes inside its process, registered as a hosted job.
		var run = RunCommands.StartHostedAsync(new Bassia.Git.GitClient(monorepo.Root), repo, "example@v0", TestEnvironment.SleepCommand(60),
			new AgentRunContext { OnStep = _ => { }, OnOutput = line => { lock (output) output.Add(line); } });
		var runId = await WaitForLiveRunAsync(monorepo);

		var (exitCode, stopOutput, error) = await monorepo.BassiaAsync("run", "stop", runId);

		Assert.True(exitCode == 0, error);
		Assert.Equal("cancelled", Result(stopOutput)["status"]);
		var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.Equal("cancelled", outcome.Metadata.Status);
	}

	[Fact]
	[Trait("Category", "Process")]
	public async Task Detached_RunReturnsAtOnce_IsLive_AndCanBeWaitedForAndRead()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var command = $"{TestEnvironment.SleepCommand(2)} && {TestEnvironment.WriteFileCommand("example/late.txt", "late")} && echo agent-finished";

		var clock = Stopwatch.StartNew();
		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "example@v0", "-detach", "-run", command);

		Assert.True(exitCode == 0, error);
		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "-detach waited for the run");
		var started = Result(output);
		Assert.True((bool)started["detached"]);
		var runId = (string)started["run_id"];

		var (waitExit, waitOutput, waitError) = await monorepo.BassiaAsync("run", "wait", runId, "-timeout", "120");
		Assert.True(waitExit == 0, waitError + waitOutput);
		Assert.Equal("completed", Result(waitOutput)["status"]);

		var (logsExit, logsOutput, _) = await monorepo.BassiaAsync("run", "logs", runId, "-tail", "0");
		Assert.Equal(0, logsExit);
		var log = (string)Result(logsOutput)["output"];
		Assert.Contains("agent-finished", log);
		Assert.Contains("bassia: done:", log);
		Assert.DoesNotContain(TomlResult.Marker + "\n", log);
		Assert.Contains(TomlResult.Marker, await File.ReadAllTextAsync((string)started["result_file"]));
		Assert.Equal("late", await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "show", $"{RunMetadata.TagName(runId, 0)}:late.txt"));
	}

	[Fact]
	[Trait("Category", "Process")]
	public async Task Detached_RunCanBeStoppedFromAnotherProcess()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "example@v0", "-detach", "-run", TestEnvironment.SleepCommand(120));
		Assert.True(exitCode == 0, error);
		var runId = (string)Result(output)["run_id"];
		await WaitForLiveRunAsync(monorepo);

		var (stopExit, stopOutput, stopError) = await monorepo.BassiaAsync("run", "stop", runId);

		Assert.True(stopExit == 0, stopError);
		Assert.Equal("cancelled", Result(stopOutput)["status"]);
		var (_, showOutput, _) = await monorepo.BassiaAsync("run", "show", runId);
		Assert.False((bool)Result(showOutput)["live"]);
		Assert.True(Directory.Exists(monorepo.RunDir(runId)), "a stopped run keeps its folder");
	}

	[Fact]
	[Trait("Category", "Process")]
	public async Task Detached_IntegrationCompletesAndIsListed()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await monorepo.RunWritingAsync("example", "a.txt", "a");
		await monorepo.RunWritingAsync("example", "b.txt", "b");

		var (planExit, planOutput, _) = await monorepo.BassiaAsync("integration", "plan", "-runs", "all");
		Assert.Equal(0, planExit);
		Assert.Contains("SYNTAX", (string)Result(planOutput)["steps"]);

		var (exitCode, output, error) = await monorepo.IntegrationStartAsync("-runs", "all", "-detach");
		Assert.True(exitCode == 0, error);
		var id = (string)Result(output)["short_id"];

		var (waitExit, waitOutput, waitError) = await monorepo.BassiaAsync("integration", "wait", id, "-timeout", "120");
		Assert.True(waitExit == 0, waitError + waitOutput);
		Assert.Contains("merged", (string)Result(waitOutput)["steps"]);

		var (listExit, listOutput, _) = await monorepo.BassiaAsync("integration", "list");
		Assert.Equal(0, listExit);
		Assert.Equal("completed", Assert.Single(Tables(Result(listOutput), "integration"))["status"]);

		var (advanceExit, _, advanceError) = await monorepo.BassiaAsync("integration", "advance", id);
		Assert.True(advanceExit == 0, advanceError);
	}

	/// <summary>Waits until a run is recorded as started with a live process, and returns its id.</summary>
	private static async Task<string> WaitForLiveRunAsync(MonorepoFixture monorepo)
	{
		var clock = Stopwatch.StartNew();
		while (clock.Elapsed < TimeSpan.FromSeconds(60))
		{
			var (_, output, _) = await monorepo.BassiaAsync("run", "list", "-status", "live");
			if (Tables(Result(output), "run").FirstOrDefault(run => (string)run["status"] == "started") is { } run)
			{
				return (string)run["run_id"];
			}

			await Task.Delay(100);
		}

		throw new TimeoutException("No run became live.");
	}
}
