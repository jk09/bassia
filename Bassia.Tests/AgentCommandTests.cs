namespace Bassia.Tests;

public class AgentCommandTests
{
	[Fact]
	public async Task Agent_WithoutArguments_ReturnsUsage()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("agent");

		Assert.Equal(1, exitCode);
		Assert.Contains("Usage: bassia agent -select", error);
	}

	[Fact]
	public async Task Agent_OutsideMonorepo_Fails()
	{
		using var directory = new TempDirectory();

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(directory.Path, "agent", "-select", "example@v0", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("is not inside a Bassia monorepo", error);
	}

	[Fact]
	public async Task Agent_UnregisteredComponent_Fails()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, _, error) = await monorepo.AgentAsync("-select", "missing@v0", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("Component 'missing' is not registered", error);
		Assert.False(Directory.Exists(monorepo.RunDir("agentic-run-1")));
	}

	[Theory]
	[InlineData("main", "is a commit, not an annotated tag")]
	[InlineData("HEAD", "is a commit, not an annotated tag")]
	[InlineData("v9", "does not exist")]
	public async Task Agent_SelectWithoutAnnotatedTag_Fails(string commitIsh, string expectedError)
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, _, error) = await monorepo.AgentAsync("-select", $"example@{commitIsh}", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, error);
		Assert.False(Directory.Exists(monorepo.RunDir("agentic-run-1")));
	}

	[Fact]
	public async Task Agent_SingleComponent_CommitsTagsAndPushesResultWithoutTouchingBaseline()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var baseline = await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "rev-parse", "v0^{commit}");

		var (exitCode, output, error) = await monorepo.AgentAsync(
			"-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/hello.cs", "class Hello {}"));

		Assert.True(exitCode == 0, error);
		Assert.Contains("\"status\": \"completed\"", output);
		Assert.Contains("\"resultStatus\": \"pushed\"", output);

		// A direct checkout in the workspace, not a junction.
		var checkout = Path.Combine(monorepo.RunDir("agentic-run-1"), "example");
		Assert.True(Directory.Exists(checkout));
		Assert.False(new DirectoryInfo(checkout).Attributes.HasFlag(FileAttributes.ReparsePoint));
		Assert.False(Directory.Exists(Path.Combine(monorepo.Cache, "agentic-run-1")));

		// The source of truth received a tagged commit on top of v0 with the script; v0 itself is unchanged.
		var source = monorepo.SourceRepo("example");
		var resultCommit = await TestEnvironment.GitAsync(source, "rev-parse", "agent/agentic-run-1/0^{commit}");
		Assert.Equal("tag", await TestEnvironment.GitAsync(source, "cat-file", "-t", "agent/agentic-run-1/0"));
		Assert.Equal(baseline, await TestEnvironment.GitAsync(source, "rev-parse", $"{resultCommit}^"));
		Assert.Equal(baseline, await TestEnvironment.GitAsync(source, "rev-parse", "v0^{commit}"));
		Assert.Contains("class Hello", await TestEnvironment.GitAsync(source, "show", "agent/agentic-run-1/0:hello.cs"));
		Assert.Equal(resultCommit, await TestEnvironment.GitAsync(source, "rev-parse", "agent/agentic-run-1"));
		Assert.Equal(baseline, await TestEnvironment.GitAsync(source, "rev-parse", "main"));

		var message = await TestEnvironment.GitAsync(source, "log", "-1", "--format=%B", resultCommit);
		Assert.StartsWith("agent(agentic-run-1):", message);
		Assert.Contains("agent/agentic-run-1/0", message);
	}

	[Fact]
	public async Task Agent_RecordsMetadataAsTaggedPlumbingCommitsWithoutCheckout()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, _, error) = await monorepo.AgentAsync(
			"-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/hello.cs", "x"));
		Assert.True(exitCode == 0, error);

		var runs = monorepo.RunsRepo;
		Assert.Equal("true", await TestEnvironment.GitAsync(runs, "rev-parse", "--is-bare-repository"));
		Assert.Equal("agent/agentic-run-1/0\nagent/agentic-run-1/1", (await TestEnvironment.GitAsync(runs, "tag", "--list")).Replace("\r", ""));
		Assert.Equal("", await TestEnvironment.GitAsync(runs, "for-each-ref", "refs/heads"));

		var started = await TestEnvironment.GitAsync(runs, "show", "agent/agentic-run-1/0:run.toml");
		var completed = await TestEnvironment.GitAsync(runs, "show", "agent/agentic-run-1/1:run.toml");
		Assert.Contains("status = \"started\"", started);
		Assert.Contains("select = \"example@v0\"", started);
		Assert.Contains("commitish = \"v0\"", started);
		Assert.Contains("status = \"completed\"", completed);
		Assert.Contains("result_tag = \"agent/agentic-run-1/0\"", completed);

		// Lineage 1 is a child of lineage 0.
		var parent = await TestEnvironment.GitAsync(runs, "rev-parse", "agent/agentic-run-1/1^{commit}^");
		Assert.Equal(await TestEnvironment.GitAsync(runs, "rev-parse", "agent/agentic-run-1/0^{commit}"), parent);
	}

	[Fact]
	public async Task Agent_NestedComponent_IsCachedAndJunctionedAndCommittedSeparately()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");

		var command = TestEnvironment.WriteFileCommand("app/a.txt", "a") + " && " + TestEnvironment.WriteFileCommand("app/lib/l.txt", "l");
		var (exitCode, output, error) = await monorepo.AgentAsync("-select", "app@v0", "-pin", "lib@v0", "-run", command);

		Assert.True(exitCode == 0, error);
		Assert.Contains("\"materialization\": \"cache\"", output);

		var appCheckout = Path.Combine(monorepo.RunDir("agentic-run-1"), "app");
		var libCache = Path.Combine(monorepo.Cache, "agentic-run-1", "lib");
		var junction = Path.Combine(appCheckout, "lib");
		Assert.True(Directory.Exists(libCache));
		Assert.False(Directory.Exists(Path.Combine(monorepo.RunDir("agentic-run-1"), "lib")));
		Assert.True(new DirectoryInfo(junction).Attributes.HasFlag(FileAttributes.ReparsePoint));
		Assert.True(File.Exists(Path.Combine(libCache, "l.txt")));

		// Each repo received only its own file.
		var appFiles = await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "ls-tree", "--name-only", "-r", "agent/agentic-run-1/0");
		var libFiles = await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "ls-tree", "--name-only", "-r", "agent/agentic-run-1/0");
		Assert.Contains("a.txt", appFiles);
		Assert.DoesNotContain("lib", appFiles);
		Assert.Contains("l.txt", libFiles);
	}

	[Fact]
	public async Task Agent_SelectedComponentThatIsAlsoNested_IsJunctionedToItsDirectCheckout()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");

		var (exitCode, output, error) = await monorepo.AgentAsync(
			"-select", "app@v0,lib@v0", "-run", TestEnvironment.WriteFileCommand("app/lib/l.txt", "l"));

		Assert.True(exitCode == 0, error);
		Assert.DoesNotContain("\"materialization\": \"cache\"", output);
		var libCheckout = Path.Combine(monorepo.RunDir("agentic-run-1"), "lib");
		Assert.False(new DirectoryInfo(libCheckout).Attributes.HasFlag(FileAttributes.ReparsePoint));
		Assert.True(new DirectoryInfo(Path.Combine(monorepo.RunDir("agentic-run-1"), "app", "lib")).Attributes.HasFlag(FileAttributes.ReparsePoint));
		Assert.False(Directory.Exists(Path.Combine(monorepo.Cache, "agentic-run-1")));
		Assert.True(File.Exists(Path.Combine(libCheckout, "l.txt")));
		Assert.Contains("l.txt", await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "ls-tree", "--name-only", "-r", "agent/agentic-run-1/0"));
	}

	[Fact]
	public async Task Agent_CyclicReferences_AreRejectedBeforeMaterializing()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");
		await monorepo.SetReferencesAsync("lib", "app");

		var (exitCode, _, error) = await monorepo.AgentAsync("-select", "app@v0", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("cyclic component reference: app -> lib -> app", error);
		Assert.False(Directory.Exists(monorepo.RunDir("agentic-run-1")));
	}

	[Fact]
	public async Task Agent_FailingCommand_RecordsFailureAndCommitsNothing()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (exitCode, _, error) = await monorepo.AgentAsync("-select", "example@v0", "-run", TestEnvironment.FailingCommand);

		Assert.Equal(1, exitCode);
		Assert.Contains("exited with code 3", error);
		Assert.True(Directory.Exists(monorepo.RunDir("agentic-run-1")));
		Assert.Equal("v0", await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "tag", "--list"));
		Assert.Contains("status = \"failed\"", await TestEnvironment.GitAsync(monorepo.RunsRepo, "show", "agent/agentic-run-1/1:run.toml"));
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
		var (exitCode, _, error) = await monorepo.AgentAsync("-select", "app@v0", "-pin", "lib@v0", "-run", command);

		Assert.Equal(1, exitCode);
		Assert.Contains("\"status\": \"partial\"", error);
		Assert.Contains("\"resultStatus\": \"failed\"", error);
		Assert.Contains("lib is locked", error);
		Assert.Contains("agent/agentic-run-1/0", await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "tag", "--list"));
		Assert.Equal("v0", await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "tag", "--list"));

		File.Delete(hook);
		var (retryExitCode, retryOutput, retryError) = await monorepo.AgentAsync("retry", "agentic-run-1");

		Assert.True(retryExitCode == 0, retryError);
		Assert.Contains("\"status\": \"completed\"", retryOutput);
		Assert.Contains("agent/agentic-run-1/0", await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "tag", "--list"));
		Assert.Contains("l.txt", await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "ls-tree", "--name-only", "-r", "agent/agentic-run-1/0"));
		Assert.Contains("status = \"completed\"", await TestEnvironment.GitAsync(monorepo.RunsRepo, "show", "agent/agentic-run-1/2:run.toml"));
	}

	[Fact]
	public async Task Agent_Abandon_DiscardsWorkspaceAndCacheButKeepsPushedResults()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");
		var command = TestEnvironment.WriteFileCommand("app/a.txt", "a") + " && " + TestEnvironment.WriteFileCommand("app/lib/l.txt", "l");
		var (exitCode, _, error) = await monorepo.AgentAsync("-select", "app@v0", "-pin", "lib@v0", "-run", command);
		Assert.True(exitCode == 0, error);

		var (abandonExitCode, abandonOutput, abandonError) = await monorepo.AgentAsync("abandon", "agentic-run-1");

		Assert.True(abandonExitCode == 0, abandonError);
		Assert.Contains("abandoned", abandonOutput);
		Assert.False(Directory.Exists(monorepo.RunDir("agentic-run-1")));
		Assert.False(Directory.Exists(Path.Combine(monorepo.Cache, "agentic-run-1")));
		Assert.Contains("agent/agentic-run-1/0", await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "tag", "--list"));
		Assert.Contains("status = \"abandoned\"", await TestEnvironment.GitAsync(monorepo.RunsRepo, "show", "agent/agentic-run-1/2:run.toml"));

		var (again, _, againError) = await monorepo.AgentAsync("abandon", "agentic-run-1");
		Assert.Equal(1, again);
		Assert.Contains("already abandoned", againError);
	}

	[Fact]
	public async Task Agent_RunIds_SkipIdsAlreadyUsedInSourceRepos()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "tag", "-a", "agent/agentic-run-1/0", "-m", "left over from another workspace", "v0^{}");

		var (exitCode, output, error) = await monorepo.AgentAsync(
			"-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/hello.cs", "x"));

		Assert.True(exitCode == 0, error);
		Assert.Contains("\"runId\": \"agentic-run-2\"", output);
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
