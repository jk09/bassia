using System.Diagnostics;
using System.Globalization;
using System.Text;
using Bassia.CliCommands.Agent;
using Tomlyn;
using Tomlyn.Model;
using Xunit.Abstractions;

namespace Bassia.Tests;

/// <summary>
/// The end-to-end proof for a whole monorepo lifecycle: a fresh Bassia monorepo in the temp folder, two
/// logically distinct components cloned from a real remote, a baseline annotated tag in each, and two agentic
/// runs started <em>at the same time</em> — one per component — whose results have to land back in the
/// top-level component repos as tagged commits while both runs are recorded in <c>.agentic-runs</c>.
///
/// Unlike the rest of the suite this test drives the built <c>bassia</c> executable as a child process. That is
/// what makes the two runs genuinely concurrent (<see cref="TestEnvironment.RunInDirectoryAsync"/> drives
/// <see cref="ProgramCli"/> in-process, through the process-global current directory and console) and it is
/// also how the CLI is actually used.
///
/// The run command stands in for a coding agent: an agentic run is defined by what it does to the repositories,
/// so a deterministic shell command that writes the "hello world" file the task asks for keeps the proof about
/// Bassia rather than about a language model's output. Everything downstream — commit, tag, push, record — is
/// the real code path a <c>claude -p ...</c> run takes.
///
/// It needs network access (the clone) and is therefore tagged <c>Category=EndToEnd</c>:
/// <c>dotnet test --filter Category!=EndToEnd</c> skips it, <c>--filter Category=EndToEnd</c> runs only it.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class ParallelAgentRunEndToEndTests(ITestOutputHelper output)
{
	/// <summary>
	/// Cloned twice, under two logical names, so the monorepo holds two components that are separate
	/// source-of-truth repositories. Point <c>BASSIA_E2E_COMPONENT_URL</c> at a local mirror to run offline.
	/// </summary>
	private static string ComponentUrl =>
		Environment.GetEnvironmentVariable("BASSIA_E2E_COMPONENT_URL") ?? "https://github.com/jk09/example.git";

	/// <summary>The component's default branch, which no agentic run may move.</summary>
	private const string UpstreamBranch = "main";

	[Fact]
	public async Task TwoComponentsClonedFromRemote_AgenticRunsInParallel_PushTaggedResultsAndRecordBothRuns()
	{
		TestEnvironment.EnsureGitIdentity();

		// Not a `using`: with BASSIA_E2E_KEEP the monorepo stays behind so the report's commands can be re-run.
		var temp = new TempDirectory();
		try
		{
			await RunScenarioAsync(temp.Path);
		}
		finally
		{
			if (!KeepMonorepo)
			{
				temp.Dispose();
			}
		}
	}

	private async Task RunScenarioAsync(string root)
	{
		var proof = new ProofReport();
		proof.Heading("Bassia parallel agentic run — end-to-end proof");
		proof.Fact("Date (UTC)", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
		proof.Fact("Monorepo root", root);
		proof.Fact("Component source", ComponentUrl);
		proof.Fact("bassia", BassiaExecutable);

		// ----- 1. a Bassia monorepo in the temp folder -----

		AssertOk(await BassiaAsync(root, "init"), "init");
		Assert.True(Directory.Exists(Path.Combine(root, ".bassia")), "init did not create the meta-repo");
		Assert.True(Directory.Exists(Path.Combine(root, ".workspace")), "init did not create the workspace");
		proof.Fact("Meta-repo HEAD", await TestEnvironment.GitAsync(Path.Combine(root, ".bassia"), "rev-parse", "HEAD"));

		// ----- 2. two logically different components, each a clone of the same upstream -----
		// ----- 3. an annotated tag on each component's HEAD, the baseline the run branches off -----

		var plans = new List<RunPlan>();
		for (var index = 1; index <= 2; index++)
		{
			var name = $"hello{index}";
			var added = AssertOk(await BassiaAsync(root, "add-component", ComponentUrl, name), "add-component");
			Assert.Equal(name, added["name"]);

			var source = Path.Combine(root, name);
			Assert.Equal("true", await TestEnvironment.GitAsync(source, "rev-parse", "--is-bare-repository"));

			var baseTag = $"base-{index}";
			await TestEnvironment.GitAsync(source, "tag", "-a", baseTag, "-m", $"baseline for {name}");
			Assert.Equal("tag", await TestEnvironment.GitAsync(source, "cat-file", "-t", baseTag));

			var head = await TestEnvironment.GitAsync(source, "rev-parse", "HEAD");
			var baseCommit = await TestEnvironment.GitAsync(source, "rev-parse", $"{baseTag}^{{commit}}");
			Assert.Equal(head, baseCommit);

			// The dummy task: "add hello world <n>" to this component, and nothing else.
			var file = $"HelloWorld{index}.cs";
			var content = $"class HelloWorld{index} {{ static void Main() {{ System.Console.WriteLine(\"hello world {index}\"); }} }}";
			Assert.Equal("", await TestEnvironment.GitAsync(source, "ls-tree", "--name-only", baseCommit, file));

			plans.Add(new RunPlan(name, baseTag, baseCommit, source, file, content,
				TestEnvironment.WriteFileCommand($"{name}/{file}", content)));

			proof.Section($"Component `{name}`");
			proof.Fact("Source of truth", source);
			proof.Fact("Baseline tag", $"`{baseTag}` (annotated, object `{await TestEnvironment.GitAsync(source, "rev-parse", baseTag)}`)");
			proof.Fact("Baseline commit", $"`{baseCommit}`");
			proof.Fact($"`{UpstreamBranch}` before the run", $"`{await TestEnvironment.GitAsync(source, "rev-parse", UpstreamBranch)}`");
			proof.Fact("Task", $"add `{file}`");
		}

		Assert.Equal(2, plans.Select(plan => plan.Name).Distinct(StringComparer.Ordinal).Count());

		// ----- 4. one agentic run per component, started at the same time, each pinned to its baseline tag -----

		var started = plans
			.Select(plan => BassiaAsync(root, "agent", "-select", $"{plan.Name}@{plan.BaseTag}", "-run", plan.AgentCommand))
			.ToArray();
		var results = await Task.WhenAll(started);

		// ----- 5. what each run left behind, in its component and in the run store -----

		var verified = new List<VerifiedRun>();
		for (var index = 0; index < plans.Count; index++)
		{
			verified.Add(await VerifyComponentResultAsync(plans[index], results[index], proof));
		}

		Assert.Equal(2, verified.Select(run => run.RunId).Distinct(StringComparer.Ordinal).Count());
		Assert.Equal(2, verified.Select(run => run.Workspace).Distinct(StringComparer.Ordinal).Count());

		// Neither run may appear in the other's component: a run only ever touches what it selected.
		for (var index = 0; index < verified.Count; index++)
		{
			var other = verified[1 - index];
			var refs = await TestEnvironment.GitAsync(plans[index].SourceRepo, "for-each-ref", "--format=%(refname)",
				$"refs/heads/{RunMetadata.RefBase(other.RunId)}", $"refs/tags/{RunMetadata.RefBase(other.RunId)}");
			Assert.Equal("", refs);
		}

		await VerifyRunRecordsAsync(root, verified, proof);

		// ----- 6. the proof itself -----

		proof.Section("Reproduction");
		proof.Line(KeepMonorepo
			? "`BASSIA_E2E_KEEP` is set, so the monorepo above is left in place and every ref named in this report can still be inspected there."
			: "The monorepo is deleted when the test ends; set `BASSIA_E2E_KEEP=1` to leave it behind and inspect the refs named above.");

		var report = proof.ToString();
		output.WriteLine(report);
		var path = WriteProof(report);
		output.WriteLine($"Proof written to {path}");
	}

	// ----- verification -----

	/// <summary>
	/// Checks one run against its component's source of truth: the result is a new annotated tag on a commit that
	/// sits directly on the selected baseline, carries the expected file, records the run in its message, and
	/// leaves the baseline tag and the upstream branch exactly where they were.
	/// </summary>
	private static async Task<VerifiedRun> VerifyComponentResultAsync(RunPlan plan, CliResult result, ProofReport proof)
	{
		var toml = AssertOk(result, $"agent -select {plan.Name}@{plan.BaseTag}");
		Assert.Equal("completed", toml["status"]);
		Assert.Equal(0L, toml["agent_exit_code"]);

		var runId = (string)toml["run_id"];
		var branch = RunMetadata.RefBase(runId);
		var resultTag = RunMetadata.TagName(runId, 0);

		var component = Assert.Single((TomlTableArray)toml["component"]);
		Assert.Equal(plan.Name, component["name"]);
		Assert.Equal(plan.BaseTag, component["commitish"]);
		Assert.Equal(plan.BaseCommit, component["commit"]);
		Assert.Equal("pushed", component["result_status"]);
		Assert.Equal(branch, component["branch"]);
		Assert.Equal(resultTag, component["result_tag"]);
		var resultCommit = (string)component["result_commit"];

		var source = plan.SourceRepo;

		// The result is an annotated tag on a commit whose one and only parent is the baseline the run selected.
		Assert.Equal("tag", await TestEnvironment.GitAsync(source, "cat-file", "-t", resultTag));
		Assert.Equal(resultCommit, await TestEnvironment.GitAsync(source, "rev-parse", $"{resultTag}^{{commit}}"));
		Assert.Equal(plan.BaseCommit, await TestEnvironment.GitAsync(source, "log", "-1", "--format=%P", resultCommit));
		Assert.NotEqual(plan.BaseCommit, resultCommit);

		// The change the task asked for is in that commit, and in no earlier one.
		Assert.Equal(plan.FileContent, await TestEnvironment.GitAsync(source, "show", $"{resultTag}:{plan.FileName}"));
		Assert.Equal(plan.FileName, await TestEnvironment.GitAsync(source, "diff-tree", "--no-commit-id", "--name-only", "-r", resultCommit));

		// Nothing that existed before the run moved: the baseline tag, the upstream branch, the rest of the tree.
		Assert.Equal(plan.BaseCommit, await TestEnvironment.GitAsync(source, "rev-parse", $"{plan.BaseTag}^{{commit}}"));
		Assert.Equal(plan.BaseCommit, await TestEnvironment.GitAsync(source, "rev-parse", UpstreamBranch));
		Assert.Equal(resultCommit, await TestEnvironment.GitAsync(source, "rev-parse", branch));

		// The run branch and its result tag are the only refs the run added to the component.
		var agentRefs = (await TestEnvironment.GitAsync(source, "for-each-ref", "--format=%(refname)", "refs/heads/agent/", "refs/tags/agent/"))
			.Replace("\r", "");
		Assert.Equal($"refs/heads/{branch}\nrefs/tags/{resultTag}", agentRefs);

		// The commit message carries the run's provenance, so a component's history reads back to the run.
		var message = (await TestEnvironment.GitAsync(source, "log", "-1", "--format=%B", resultCommit)).Replace("\r", "");
		var lines = message.Split('\n');
		Assert.Equal($"agent({RunMetadata.ShortKey(runId)}): {AgentCommand.SummarizeCommand(plan.AgentCommand)}", lines[0]);
		Assert.Equal("", lines[1]);
		var record = (TomlTable)TomlSerializer.Deserialize<TomlTable>(string.Join('\n', lines[2..]))!["agentic_run"];
		Assert.Equal(runId, record["id"]);
		Assert.Equal($"{plan.Name}@{plan.BaseTag}", record["select"]);
		Assert.Equal(plan.AgentCommand, record["command"]);
		Assert.Equal(resultTag, record["record_tag"]);
		var recordComponent = (TomlTable)record["component"];
		Assert.Equal(plan.Name, recordComponent["name"]);
		Assert.Equal(plan.BaseTag, recordComponent["commitish"]);
		Assert.Equal(plan.BaseCommit, recordComponent["base_commit"]);
		Assert.Equal(branch, recordComponent["branch"]);
		Assert.Equal(resultTag, recordComponent["tag"]);

		proof.Section($"Agentic run on `{plan.Name}`");
		proof.Fact("Run id", $"`{runId}`");
		proof.Fact("Selection", $"`{plan.Name}@{plan.BaseTag}` -> `{plan.BaseCommit}`");
		proof.Fact("Command", $"`{plan.AgentCommand}`");
		proof.Fact("Workspace", (string)toml["workspace"]);
		proof.Fact("Result branch", $"`{branch}` -> `{resultCommit}`");
		proof.Fact("Result tag", $"`{resultTag}` (annotated, object `{await TestEnvironment.GitAsync(source, "rev-parse", resultTag)}`)");
		proof.Fact("Result commit parent", $"`{await TestEnvironment.GitAsync(source, "rev-parse", $"{resultCommit}^")}` (= the baseline)");
		proof.Fact("Files changed", $"`{plan.FileName}` (blob `{await TestEnvironment.GitAsync(source, "rev-parse", $"{resultTag}:{plan.FileName}")}`)");
		proof.Fact($"`{plan.BaseTag}` after the run", $"`{await TestEnvironment.GitAsync(source, "rev-parse", $"{plan.BaseTag}^{{commit}}")}` (unmoved)");
		proof.Fact($"`{UpstreamBranch}` after the run", $"`{await TestEnvironment.GitAsync(source, "rev-parse", UpstreamBranch)}` (unmoved)");
		proof.Block("Commit message", message);
		proof.Block($"{plan.FileName} at {resultTag}", plan.FileContent);

		return new VerifiedRun(runId, plan, resultCommit, resultTag, (string)toml["workspace"]);
	}

	/// <summary>
	/// Checks the shared <c>.agentic-runs</c> store: both runs recorded, each as a pair of annotated tags over a
	/// parent/child pair of plumbing commits, with the final record naming the tag the run pushed. Two runs
	/// writing here at once is the part parallelism stresses, so the records are also checked against each other.
	/// </summary>
	private static async Task VerifyRunRecordsAsync(string root, IReadOnlyList<VerifiedRun> runs, ProofReport proof)
	{
		var store = Path.Combine(root, ".agentic-runs", ".git");
		Assert.Equal("true", await TestEnvironment.GitAsync(store, "rev-parse", "--is-bare-repository"));
		Assert.Equal("", await TestEnvironment.GitAsync(store, "for-each-ref", "refs/heads"));
		Assert.Equal("", await TestEnvironment.GitAsync(Path.Combine(root, ".bassia"), "status", "--porcelain"));

		// Exactly two records per run and nothing else: neither run overwrote or absorbed the other's lineage.
		var expected = runs
			.SelectMany(run => new[] { RunMetadata.TagName(run.RunId, 0), RunMetadata.TagName(run.RunId, 1) })
			.OrderBy(tag => tag, StringComparer.Ordinal);
		var actual = (await TestEnvironment.GitAsync(store, "tag", "--list")).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(expected, actual);

		proof.Section("Run records in `.agentic-runs`");
		proof.Fact("Store", $"{store} (bare, no branches — records are reachable only through their tags)");

		var intervals = new List<(string RunId, DateTimeOffset Created, DateTimeOffset Finished)>();
		foreach (var run in runs)
		{
			var startTag = RunMetadata.TagName(run.RunId, 0);
			var finalTag = RunMetadata.TagName(run.RunId, 1);
			Assert.Equal("tag", await TestEnvironment.GitAsync(store, "cat-file", "-t", startTag));
			Assert.Equal("tag", await TestEnvironment.GitAsync(store, "cat-file", "-t", finalTag));

			var started = RunMetadata.FromToml(await TestEnvironment.GitAsync(store, "show", $"{startTag}:{RunMetadata.FileName}"));
			Assert.Equal(run.RunId, started.RunId);
			Assert.Equal("started", started.Status);
			Assert.Equal($"{run.Plan.Name}@{run.Plan.BaseTag}", started.Select);
			Assert.Equal(ResultStatus.Pending, started.Components.Single().ResultStatus);

			var finished = RunMetadata.FromToml(await TestEnvironment.GitAsync(store, "show", $"{finalTag}:{RunMetadata.FileName}"));
			Assert.Equal(run.RunId, finished.RunId);
			Assert.Equal("completed", finished.Status);
			Assert.Equal(run.Plan.AgentCommand, finished.Command);
			Assert.Equal(0, finished.AgentExitCode);
			var recorded = finished.Components.Single();
			Assert.Equal(run.Plan.Name, recorded.Name);
			Assert.Equal(run.Plan.BaseTag, recorded.CommitIsh);
			Assert.Equal(run.Plan.BaseCommit, recorded.Commit);
			Assert.Equal(ResultStatus.Pushed, recorded.ResultStatus);
			Assert.Equal(run.ResultCommit, recorded.ResultCommit);
			Assert.Equal(run.ResultTag, recorded.ResultTag);
			Assert.Null(recorded.ResultError);

			// Lineage 1 is a child of lineage 0: the record's own history, written without ever moving a branch.
			var startCommit = await TestEnvironment.GitAsync(store, "rev-parse", $"{startTag}^{{commit}}");
			var finalCommit = await TestEnvironment.GitAsync(store, "rev-parse", $"{finalTag}^{{commit}}");
			Assert.Equal(startCommit, await TestEnvironment.GitAsync(store, "rev-parse", $"{finalCommit}^"));

			var created = Timestamp(finished.Created);
			var completedAt = Timestamp(finished.Finished!);
			intervals.Add((run.RunId, created, completedAt));

			proof.Fact($"`{startTag}`", $"commit `{startCommit}`, status `{started.Status}`");
			proof.Fact($"`{finalTag}`", $"commit `{finalCommit}` (child of `{startCommit}`), status `{finished.Status}`, result `{recorded.ResultTag}` = `{recorded.ResultCommit}`");
			proof.Block($"{finalTag}:{RunMetadata.FileName}", await TestEnvironment.GitAsync(store, "show", $"{finalTag}:{RunMetadata.FileName}"));
		}

		// The recorded intervals overlap, which is what makes this a parallel run rather than two runs in a row.
		var (firstId, firstCreated, firstFinished) = intervals[0];
		var (secondId, secondCreated, secondFinished) = intervals[1];
		Assert.True(firstCreated < secondFinished && secondCreated < firstFinished,
			$"the runs did not overlap: {firstId} {firstCreated:O}..{firstFinished:O}, {secondId} {secondCreated:O}..{secondFinished:O}");

		var overlap = (firstFinished < secondFinished ? firstFinished : secondFinished)
			- (firstCreated > secondCreated ? firstCreated : secondCreated);
		proof.Section("Parallelism");
		foreach (var (runId, created, finishedAt) in intervals)
		{
			proof.Fact($"`{runId}`", $"{created:O} .. {finishedAt:O} ({(finishedAt - created).TotalSeconds:F3}s)");
		}

		proof.Fact("Overlap", $"{overlap.TotalSeconds:F3}s — both runs were in flight at the same time");
	}

	private static DateTimeOffset Timestamp(string value) =>
		DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

	// ----- driving the built executable -----

	/// <summary>The bassia apphost, copied next to the test assembly by the project reference.</summary>
	private static string BassiaExecutable =>
		Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Bassia.exe" : "Bassia");

	/// <summary>
	/// Starts bassia as a child process in <paramref name="workingDirectory"/>. The process is started before
	/// the first await, so several calls made back to back really do run at the same time.
	/// </summary>
	private static async Task<CliResult> BassiaAsync(string workingDirectory, params string[] args)
	{
		var exe = BassiaExecutable;
		Assert.True(File.Exists(exe), $"'{exe}' is missing; build the solution before running the end-to-end tests.");

		var startInfo = new ProcessStartInfo(exe)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (var arg in args)
		{
			startInfo.ArgumentList.Add(arg);
		}

		using var process = Process.Start(startInfo)!;
		var standardOutput = process.StandardOutput.ReadToEndAsync();
		var standardError = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		return new CliResult(process.ExitCode, await standardOutput, await standardError);
	}

	/// <summary>Asserts the command succeeded and returns its TOML result.</summary>
	private static TomlTable AssertOk(CliResult result, string what)
	{
		Assert.True(result.ExitCode == 0, $"bassia {what} failed ({result.ExitCode}):\n{result.StandardError}");
		var toml = ParseResult(result.StandardOutput, what);
		Assert.True((bool)toml["ok"], $"bassia {what} reported failure:\n{result.StandardOutput}");
		return toml;
	}

	/// <summary>
	/// An agent command's own output can precede the result, so the result is the block starting at the last
	/// marker line bassia opens it with.
	/// </summary>
	private static TomlTable ParseResult(string standardOutput, string what)
	{
		var text = standardOutput.Replace("\r\n", "\n");
		var start = text.LastIndexOf(TomlResult.Marker + "\n", StringComparison.Ordinal);
		Assert.True(start >= 0, $"bassia {what} printed no TOML result:\n{standardOutput}");
		return TomlSerializer.Deserialize<TomlTable>(text[start..])!;
	}

	// ----- the proof -----

	private static bool KeepMonorepo => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BASSIA_E2E_KEEP"));

	/// <summary>
	/// Writes the report where a human (or a build) can pick it up: <c>BASSIA_E2E_PROOF</c> when set, otherwise a
	/// timestamped file in the temp folder. The report is also written to the test output.
	/// </summary>
	private static string WriteProof(string report)
	{
		var path = Environment.GetEnvironmentVariable("BASSIA_E2E_PROOF");
		if (string.IsNullOrWhiteSpace(path))
		{
			path = Path.Combine(Path.GetTempPath(), $"bassia-e2e-proof-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md");
		}

		var directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(path, report);
		return Path.GetFullPath(path);
	}

	/// <summary>The Markdown proof the test emits: every fact it asserted, with the shas needed to re-check it.</summary>
	private sealed class ProofReport
	{
		private readonly StringBuilder text = new();

		public void Heading(string title) => text.Append("# ").Append(title).Append("\n\n");

		public void Section(string title) => text.Append("\n## ").Append(title).Append("\n\n");

		public void Fact(string name, string? value) => text.Append("- **").Append(name).Append("**: ").Append(value).Append('\n');

		public void Line(string line) => text.Append(line).Append('\n');

		public void Block(string caption, string content) =>
			text.Append('\n').Append(caption).Append(":\n\n```\n").Append(content.Replace("\r", "")).Append("\n```\n");

		public override string ToString() => text.ToString();
	}

	private sealed record RunPlan(
		string Name,
		string BaseTag,
		string BaseCommit,
		string SourceRepo,
		string FileName,
		string FileContent,
		string AgentCommand);

	private sealed record VerifiedRun(string RunId, RunPlan Plan, string ResultCommit, string ResultTag, string Workspace);

	private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
