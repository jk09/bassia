using Bassia.CliCommands.Agent;
using Tomlyn;
using Tomlyn.Model;

namespace Bassia.Tests;

public class AgentCommandTests
{
	[Fact]
	public async Task Agent_WithoutArguments_ReturnsUsage()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("run", "start");

		Assert.Equal(2, exitCode);
		Assert.Contains("-select is required", error);
		Assert.Contains("Usage: bassia run start -select", error);
	}

	[Fact]
	public async Task Agent_OutsideMonorepo_Fails()
	{
		using var directory = new TempDirectory();

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(directory.Path, "run", "start", "-select", "example@v0", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("is not inside a Bassia monorepo", error);
	}

	[Fact]
	public async Task Agent_UnregisteredComponent_Fails()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", "missing@v0", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("Component 'missing' is not registered", error);
		Assert.False(monorepo.HasRunDirs);
	}

	[Theory]
	[InlineData("main", "is a commit, not an annotated tag")]
	[InlineData("HEAD", "is a commit, not an annotated tag")]
	[InlineData("v9", "does not exist")]
	public async Task Agent_SelectWithoutAnnotatedTag_Fails(string commitIsh, string expectedError)
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", $"example@{commitIsh}", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, error);
		Assert.False(monorepo.HasRunDirs);
	}

	[Fact]
	public async Task Agent_SingleComponent_CommitsTagsAndPushesResultWithoutTouchingBaseline()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var baseline = await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "rev-parse", "v0^{commit}");

		var (exitCode, output, error) = await monorepo.RunStartAsync(
			"-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/hello.cs", "class Hello {}"));

		Assert.True(exitCode == 0, error);
		Assert.Contains("status = \"completed\"", output);
		Assert.Contains("result_status = \"pushed\"", output);
		var runId = TestEnvironment.RunIdOf(output);
		var branch = RunMetadata.RefBase(runId);
		var tag = RunMetadata.TagName(runId, 0);

		// A direct checkout in the run folder, not a junction.
		var checkout = monorepo.Checkout(runId, "example");
		Assert.True(Directory.Exists(checkout));
		Assert.False(new DirectoryInfo(checkout).Attributes.HasFlag(FileAttributes.ReparsePoint));

		// The source of truth received a tagged commit on top of v0 with the script; v0 itself is unchanged.
		var source = monorepo.SourceRepo("example");
		var resultCommit = await TestEnvironment.GitAsync(source, "rev-parse", $"{tag}^{{commit}}");
		Assert.Equal("tag", await TestEnvironment.GitAsync(source, "cat-file", "-t", tag));
		Assert.Equal(baseline, await TestEnvironment.GitAsync(source, "rev-parse", $"{resultCommit}^"));
		Assert.Equal(baseline, await TestEnvironment.GitAsync(source, "rev-parse", "v0^{commit}"));
		Assert.Contains("class Hello", await TestEnvironment.GitAsync(source, "show", $"{tag}:hello.cs"));
		Assert.Equal(resultCommit, await TestEnvironment.GitAsync(source, "rev-parse", branch));
		Assert.Equal(baseline, await TestEnvironment.GitAsync(source, "rev-parse", "main"));

		// Subject from the default template, then a TOML body naming the run, its command and the tag on this commit.
		var message = await TestEnvironment.GitAsync(source, "log", "-1", "--format=%B", resultCommit);
		var lines = message.Replace("\r", "").Split('\n');
		Assert.Equal($"agent({RunMetadata.Key(runId)}): {AgentCommand.SummarizeCommand(TestEnvironment.WriteFileCommand("example/hello.cs", "class Hello {}"))}", lines[0]);
		Assert.Equal("", lines[1]);
		var body = (TomlTable)TomlSerializer.Deserialize<TomlTable>(string.Join('\n', lines[2..]))!["agentic_run"];
		Assert.Equal(runId, body["id"]);
		Assert.Equal("example@v0", body["select"]);
		Assert.Equal(TestEnvironment.WriteFileCommand("example/hello.cs", "class Hello {}"), body["command"]);
		Assert.Equal(RunMetadata.TagName(runId, 0), body["record_tag"]);
		var component = (TomlTable)body["component"];
		Assert.Equal("example", component["name"]);
		Assert.Equal("v0", component["commitish"]);
		Assert.Equal(baseline, component["base_commit"]);
		Assert.Equal(branch, component["branch"]);
		Assert.Equal(tag, component["tag"]);
	}

	[Fact]
	public async Task Agent_CommitSubject_IsConfigurable()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await File.AppendAllTextAsync(Path.Combine(monorepo.MetaRepo, "config.toml"), "\n[agent.commit]\nsubject = \"[{component}] {summary} ({run_id})\"\n");

		var (exitCode, output, error) = await monorepo.RunStartAsync(
			"-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/hello.cs", "x"));

		Assert.True(exitCode == 0, error);
		var runId = TestEnvironment.RunIdOf(output);
		var subject = await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "log", "-1", "--format=%s", RunMetadata.RefBase(runId));
		Assert.Equal($"[example] {AgentCommand.SummarizeCommand(TestEnvironment.WriteFileCommand("example/hello.cs", "x"))} ({runId})", subject);
	}

	[Fact]
	public async Task Agent_RunIds_AreRandomAndUnique()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (firstExit, first, firstError) = await monorepo.RunStartAsync("-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/one.txt", "1"));
		var (secondExit, second, secondError) = await monorepo.RunStartAsync("-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/two.txt", "2"));

		Assert.True(firstExit == 0, firstError);
		Assert.True(secondExit == 0, secondError);
		var firstId = TestEnvironment.RunIdOf(first);
		var secondId = TestEnvironment.RunIdOf(second);
		Assert.NotEqual(firstId, secondId);
		Assert.True(Directory.Exists(monorepo.RunDir(firstId)));
		Assert.True(Directory.Exists(monorepo.RunDir(secondId)));

		// Both runs branched off v0 independently, each with its own branch and tag in the source of truth.
		var source = monorepo.SourceRepo("example");
		var refs = (await TestEnvironment.GitAsync(source, "for-each-ref", "--format=%(refname)", "refs/heads/agent-run/", "refs/tags/agent-run/")).Replace("\r", "").Split('\n');
		Assert.Equal(
			new[] { $"refs/heads/{RunMetadata.RefBase(firstId)}", $"refs/heads/{RunMetadata.RefBase(secondId)}", $"refs/tags/{RunMetadata.TagName(firstId, 0)}", $"refs/tags/{RunMetadata.TagName(secondId, 0)}" }.Order(),
			refs.Order());
	}

	[Fact]
	public async Task Agent_CommandThatChangesNothing_CompletesButSaysNothingWasCommitted()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "example@v0", "-run", "echo hello");

		Assert.True(exitCode == 0, error);
		Assert.Contains("status = \"completed\"", output);
		Assert.Contains("result_status = \"unchanged\"", output);
		Assert.Contains("changed no component; nothing was committed", output);

		var refs = await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "for-each-ref", "--format=%(refname)", "refs/heads/agent-run/", "refs/tags/agent-run/");
		Assert.Equal("", refs);
	}

	[Fact]
	public async Task Agent_RecordsMetadataAsTaggedPlumbingCommitsWithoutCheckout()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, output, error) = await monorepo.RunStartAsync(
			"-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/hello.cs", "x"));
		Assert.True(exitCode == 0, error);
		var runId = TestEnvironment.RunIdOf(output);
		var lineage0 = RunMetadata.TagName(runId, 0);
		var lineage1 = RunMetadata.TagName(runId, 1);

		// The record lives at the monorepo root, alongside (not inside) the meta-repo, and outlives the run folder.
		var runs = monorepo.RunsRepo;
		Assert.Equal("true", await TestEnvironment.GitAsync(runs, "rev-parse", "--is-bare-repository"));
		Assert.Equal("", await TestEnvironment.GitAsync(monorepo.MetaRepo, "status", "--porcelain"));
		Assert.Equal($"{lineage0}\n{lineage1}", (await TestEnvironment.GitAsync(runs, "tag", "--list")).Replace("\r", ""));
		Assert.Equal("", await TestEnvironment.GitAsync(runs, "for-each-ref", "refs/heads"));

		var started = await TestEnvironment.GitAsync(runs, "show", $"{lineage0}:run.toml");
		var completed = await TestEnvironment.GitAsync(runs, "show", $"{lineage1}:run.toml");
		Assert.Contains("status = \"started\"", started);
		Assert.Contains("select = \"example@v0\"", started);
		Assert.Contains("commitish = \"v0\"", started);
		Assert.Contains("status = \"completed\"", completed);
		Assert.Contains($"result_tag = \"{RunMetadata.TagName(runId, 0)}\"", completed);

		// Lineage 1 is a child of lineage 0.
		var parent = await TestEnvironment.GitAsync(runs, "rev-parse", $"{lineage1}^{{commit}}^");
		Assert.Equal(await TestEnvironment.GitAsync(runs, "rev-parse", $"{lineage0}^{{commit}}"), parent);
	}

	[Fact]
	public async Task Agent_ComponentChain_IsCheckedOutSideBySideAndJunctionedIntoItsReferrers()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("a");
		await monorepo.AddComponentAsync("b");
		await monorepo.AddComponentAsync("c");
		await monorepo.SetReferencesAsync("a", ("b", "b_lib"));
		await monorepo.SetReferencesAsync("b", ("c", "c_lib"));

		// Every component is edited through the path its referrer sees it at.
		var command = string.Join(" && ",
			TestEnvironment.WriteFileCommand("a/script1.cs", "1"),
			TestEnvironment.WriteFileCommand("a/b_lib/script2.cs", "2"),
			TestEnvironment.WriteFileCommand("a/b_lib/c_lib/script3.cs", "3"));
		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "a@v0,b@v0,c@v0", "-run", command);

		Assert.True(exitCode == 0, error);
		var runId = TestEnvironment.RunIdOf(output);

		// All three are real checkouts side by side; the nested positions are links to those siblings.
		foreach (var component in new[] { "a", "b", "c" })
		{
			Assert.True(Directory.Exists(monorepo.Checkout(runId, component)));
			Assert.False(new DirectoryInfo(monorepo.Checkout(runId, component)).Attributes.HasFlag(FileAttributes.ReparsePoint));
		}

		foreach (var junction in new[] { Path.Combine("a", "b_lib"), Path.Combine("b", "c_lib") })
		{
			Assert.True(new DirectoryInfo(Path.Combine(monorepo.RunDir(runId), junction)).Attributes.HasFlag(FileAttributes.ReparsePoint));
		}

		Assert.True(File.Exists(Path.Combine(monorepo.Checkout(runId, "b"), "script2.cs")));
		Assert.True(File.Exists(Path.Combine(monorepo.Checkout(runId, "c"), "script3.cs")));

		// Each source of truth received only its own file, on identically named branch and tag.
		foreach (var (component, file) in new[] { ("a", "script1.cs"), ("b", "script2.cs"), ("c", "script3.cs") })
		{
			var source = monorepo.SourceRepo(component);
			var files = await TestEnvironment.GitAsync(source, "ls-tree", "--name-only", "-r", RunMetadata.TagName(runId, 0));
			Assert.Equal($"README.md\n{file}", files.Replace("\r", ""));
			Assert.Equal(
				await TestEnvironment.GitAsync(source, "rev-parse", $"{RunMetadata.TagName(runId, 0)}^{{commit}}"),
				await TestEnvironment.GitAsync(source, "rev-parse", RunMetadata.RefBase(runId)));
		}
	}

	[Fact]
	public async Task Agent_SelectionMissingAReferencedComponent_IsRejectedBeforeMaterializing()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", "app@v0", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("-select must cover the full component closure", error);
		Assert.Contains("'lib' (referenced by 'app')", error);
		Assert.False(monorepo.HasRunDirs);
	}

	[Fact]
	public async Task Agent_CyclicReferences_AreRejectedBeforeMaterializing()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");
		await monorepo.SetReferencesAsync("lib", "app");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", "app@v0", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("cyclic component reference: app -> lib -> app", error);
		Assert.False(monorepo.HasRunDirs);
	}

	[Fact]
	public async Task Agent_FailingCommand_RecordsFailureAndCommitsNothing()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", "example@v0", "-run", TestEnvironment.FailingCommand);

		Assert.Equal(1, exitCode);
		Assert.Contains("exited with code 3", error);
		var runId = TestEnvironment.RunIdOf(error);
		Assert.True(Directory.Exists(monorepo.RunDir(runId)));
		Assert.Equal("v0", await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "tag", "--list"));
		Assert.Contains("status = \"failed\"", await TestEnvironment.GitAsync(monorepo.RunsRepo, "show", $"{RunMetadata.TagName(runId, 1)}:run.toml"));
	}

	[Fact]
	public async Task Agent_Cancelled_KillsTheAgentProcessTreeAndRecordsTheRunAsCancelled()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		using var cancellation = new CancellationTokenSource();
		var steps = new List<AgentRunStep>();
		var git = new Bassia.Git.GitClient(monorepo.Root);

		// The agent blocks; the run is cancelled as soon as its process is the thing being waited on.
		var context = new AgentRunContext
		{
			Cancellation = cancellation.Token,
			OnStep = step =>
			{
				lock (steps)
				{
					steps.Add(step);
				}

				if (step.Phase == AgentRunPhase.Agent)
				{
					cancellation.Cancel();
				}
			}
		};

		var outcome = await AgentCommand.StartRunAsync(git, Monorepo.Load(monorepo.Root), "example@v0", TestEnvironment.SleepCommand(60), context);

		Assert.False(outcome.Ok);
		Assert.Contains("was cancelled", outcome.Message);
		Assert.Equal("cancelled", outcome.Metadata.Status);
		Assert.Null(outcome.Metadata.AgentExitCode);
		// Nothing was committed into the component, and the run folder is kept for inspection.
		Assert.Equal("v0", await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "tag", "--list"));
		Assert.True(Directory.Exists(monorepo.RunDir(outcome.Metadata.RunId)));
		Assert.Contains("status = \"cancelled\"", await TestEnvironment.GitAsync(monorepo.RunsRepo, "show", $"{RunMetadata.TagName(outcome.Metadata.RunId, 1)}:run.toml"));

		// The steps went to the context instead of stderr, which is what keeps a live frontend readable.
		Assert.Contains(steps, step => step.Phase == AgentRunPhase.Preparing);
		Assert.Contains(steps, step => step.Phase == AgentRunPhase.Cancelled);

		var (retryExitCode, _, retryError) = await monorepo.BassiaAsync("run", "retry", outcome.Metadata.RunId);
		Assert.Equal(1, retryExitCode);
		Assert.Contains("has status 'cancelled'", retryError);
	}

	[Fact]
	public async Task Agent_WithAnOutputCallback_CapturesTheAgentsOutputInsteadOfLettingItReachTheConsole()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var lines = new List<string>();
		var context = new AgentRunContext
		{
			OnOutput = line =>
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		};

		var outcome = await AgentCommand.StartRunAsync(new Bassia.Git.GitClient(monorepo.Root), Monorepo.Load(monorepo.Root),
			"example@v0", "echo hello-from-the-agent", context);

		Assert.True(outcome.Ok);
		Assert.Contains(lines, line => line.Contains("hello-from-the-agent"));
	}

	[Fact]
	public async Task Agent_PushFailure_IsPartialAndCanBeRetried()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");
		var hook = Path.Combine(monorepo.SourceRepo("lib"), ".git", "hooks", "pre-receive");
		await File.WriteAllTextAsync(hook, "#!/bin/sh\necho 'lib is locked' >&2\nexit 1\n");
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		var command = TestEnvironment.WriteFileCommand("app/a.txt", "a") + " && " + TestEnvironment.WriteFileCommand("app/lib/l.txt", "l");
		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", "app@v0,lib@v0", "-run", command);

		Assert.Equal(1, exitCode);
		Assert.Contains("status = \"partial\"", error);
		Assert.Contains("result_status = \"failed\"", error);
		Assert.Contains("lib is locked", error);
		var runId = TestEnvironment.RunIdOf(error);
		var resultTag = RunMetadata.TagName(runId, 0);
		Assert.Contains(resultTag, await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "tag", "--list"));
		Assert.Equal("v0", await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "tag", "--list"));

		// retry accepts the bare <id> as well as the full run id.
		File.Delete(hook);
		var (retryExitCode, retryOutput, retryError) = await monorepo.BassiaAsync("run", "retry", RunMetadata.Key(runId));

		Assert.True(retryExitCode == 0, retryError);
		Assert.Contains("status = \"completed\"", retryOutput);
		Assert.Contains(resultTag, await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "tag", "--list"));
		Assert.Contains("l.txt", await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "ls-tree", "--name-only", "-r", resultTag));
		Assert.Contains("status = \"completed\"", await TestEnvironment.GitAsync(monorepo.RunsRepo, "show", $"{RunMetadata.TagName(runId, 2)}:run.toml"));
	}

	[Fact]
	public async Task Agent_Abandon_DiscardsTheRunFolderButKeepsPushedResults()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");
		var command = TestEnvironment.WriteFileCommand("app/a.txt", "a") + " && " + TestEnvironment.WriteFileCommand("app/lib/l.txt", "l");
		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "app@v0,lib@v0", "-run", command);
		Assert.True(exitCode == 0, error);
		var runId = TestEnvironment.RunIdOf(output);

		var (abandonExitCode, abandonOutput, abandonError) = await monorepo.BassiaAsync("run", "abandon", runId);

		Assert.True(abandonExitCode == 0, abandonError);
		Assert.Contains("abandoned", abandonOutput);
		Assert.False(Directory.Exists(monorepo.RunDir(runId)));
		Assert.Contains(RunMetadata.TagName(runId, 0), await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "tag", "--list"));
		Assert.Contains("status = \"abandoned\"", await TestEnvironment.GitAsync(monorepo.RunsRepo, "show", $"{RunMetadata.TagName(runId, 2)}:run.toml"));

		var (again, _, againError) = await monorepo.BassiaAsync("run", "abandon", runId);
		Assert.Equal(1, again);
		Assert.Contains("already abandoned", againError);
	}

	[Fact]
	public async Task Agent_ResultTagCounter_FollowsTheTagsAlreadyInTheCheckout()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/one.txt", "1"));
		Assert.True(exitCode == 0, error);
		var runId = TestEnvironment.RunIdOf(output);
		var checkout = Bassia.Git.GitClient.In(monorepo.Checkout(runId, "example"));

		// The first result took /0; another result on the same run branch takes /1, and tags of other runs don't count.
		Assert.Equal(RunMetadata.TagName(runId, 1), await AgentCommand.NextResultTagAsync(checkout, runId));
		await TestEnvironment.GitAsync(monorepo.Checkout(runId, "example"), "tag", "-a", RunMetadata.TagName(runId, 1), "-m", "next", "HEAD");
		await TestEnvironment.GitAsync(monorepo.Checkout(runId, "example"), "tag", "-a", RunMetadata.TagName(RunMetadata.NewRunId(), 7), "-m", "other run", "HEAD");
		Assert.Equal(RunMetadata.TagName(runId, 2), await AgentCommand.NextResultTagAsync(checkout, runId));
	}

	[Fact]
	public void SelectionParsing_RejectsMalformedAndDuplicateEntries()
	{
		var parsed = ComponentSelection.ParseList("-select", "app@v0, lib@release/1.0");
		Assert.Equal([new("app", "v0"), new("lib", "release/1.0")], parsed);

		Assert.Contains("expected <component>@<commit-ish>", Assert.Throws<AgentException>(() => ComponentSelection.ParseList("-select", "app")).Message);
		Assert.Contains("expected <component>@<commit-ish>", Assert.Throws<AgentException>(() => ComponentSelection.ParseList("-select", "app@")).Message);
		Assert.Contains("more than once", Assert.Throws<AgentException>(() => ComponentSelection.ParseList("-select", "app@v0,app@v1")).Message);
	}

	[Fact]
	public void SummarizeCommand_PrefersTheQuotedPrompt()
	{
		Assert.Equal("add a hello world script", AgentCommand.SummarizeCommand("\"C:\\tools\\claude.exe\" -p \"add a hello world script\" --model sonnet"));
		Assert.Equal("echo hi> file.txt", AgentCommand.SummarizeCommand("echo hi> file.txt"));
		Assert.EndsWith("...", AgentCommand.SummarizeCommand(new string('x', 100)));
	}
}
