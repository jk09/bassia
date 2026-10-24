namespace Bassia.Tests;

/// <summary><c>bassia component create</c> on the git storage: a new component a run can select straight away.</summary>
public class ComponentCreateTests
{
	[Fact]
	public async Task Create_ThenRunOnIt_CommitsTheFirstFilesOnTopOfTheEmptyCommit()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "create", "docs");

		Assert.True(exitCode == 0, error);
		Assert.Contains("name = \"docs\"", output);
		Assert.Contains("name = \"docs\"", await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml")));
		var first = await TestEnvironment.GitAsync(monorepo.SourceRepo("docs"), "rev-parse", "refs/heads/main");
		Assert.Equal("", await TestEnvironment.GitAsync(monorepo.SourceRepo("docs"), "ls-tree", "-r", "--name-only", first));

		var (runExitCode, runOutput, runError) = await monorepo.RunStartAsync("-select", "docs", "-run", TestEnvironment.WriteFileCommand("docs/index.md", "# Docs"));

		Assert.True(runExitCode == 0, runError);
		Assert.Contains("result_status = \"pushed\"", runOutput);
		var tag = RunMetadata.TagName(TestEnvironment.RunIdOf(runOutput), 0);
		Assert.Equal(first, await TestEnvironment.GitAsync(monorepo.SourceRepo("docs"), "rev-parse", $"{tag}^{{commit}}^"));
		Assert.Equal("index.md", await TestEnvironment.GitAsync(monorepo.SourceRepo("docs"), "ls-tree", "-r", "--name-only", tag));
	}

	[Fact]
	public async Task Create_TakenOrInvalidName_FailsAndLeavesNothing()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");

		var (takenExitCode, _, takenError) = await monorepo.BassiaAsync("component", "create", "example");
		var (invalidExitCode, _, invalidError) = await monorepo.BassiaAsync("component", "create", ".hidden");
		var (danglingExitCode, _, danglingError) = await monorepo.BassiaAsync("component", "create", "app", "-references", "missing");

		Assert.Equal((1, 1, 1), (takenExitCode, invalidExitCode, danglingExitCode));
		Assert.Contains("already registered", takenError);
		Assert.Contains("not a valid component name", invalidError);
		Assert.Contains("'missing', which is not registered", danglingError);
		Assert.False(Directory.Exists(monorepo.SourceRepo("app")));
		Assert.False(Directory.Exists(monorepo.SourceRepo(".hidden")));
	}
}
