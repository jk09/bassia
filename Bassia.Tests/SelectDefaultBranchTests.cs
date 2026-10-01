namespace Bassia.Tests;

/// <summary><c>-select component</c>: a bare component name starts the run from the tip of its default branch.</summary>
public class SelectDefaultBranchTests
{
	[Fact]
	public async Task Select_BareNames_StartTheRunFromEachDefaultBranchTip()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");
		var appTip = await AdvanceAsync(monorepo.SourceRepo("app"), "main");
		var libTip = await AdvanceAsync(monorepo.SourceRepo("lib"), "main");

		var (exitCode, output, error) = await monorepo.RunStartAsync(
			"-select", "app,lib", "-run", TestEnvironment.WriteFileCommand("app/hello.cs", "class Hello {}"));

		Assert.True(exitCode == 0, error);
		Assert.Contains("commitish = \"main\"", output);
		Assert.Contains($"commit = \"{appTip}\"", output);
		Assert.Contains($"commit = \"{libTip}\"", output);
		var runId = TestEnvironment.RunIdOf(output);

		// The record keeps the selection as given, and the branch tips it resolved to as provenance.
		var record = await TestEnvironment.GitAsync(monorepo.RunsRepo, "show", $"{RunMetadata.TagName(runId, 0)}:run.toml");
		Assert.Contains("select = \"app,lib\"", record);
		Assert.Contains("commitish = \"main\"", record);
		Assert.Contains($"commit = \"{appTip}\"", record);
		var result = await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "rev-parse", $"{RunMetadata.TagName(runId, 0)}^{{commit}}");
		Assert.Equal(appTip, await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "rev-parse", $"{result}^"));

		// The branch itself is left where it was; the run's result lives on the run branch and tag.
		Assert.Equal(appTip, await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "rev-parse", "main"));
	}

	[Fact]
	public async Task Select_BareAndTaggedEntries_CanBeMixed()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");
		var appTip = await AdvanceAsync(monorepo.SourceRepo("app"), "main");
		await AdvanceAsync(monorepo.SourceRepo("lib"), "main");
		var libBaseline = await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "rev-parse", "v0^{commit}");

		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "app,lib@v0", "-run", "echo");

		Assert.True(exitCode == 0, error);
		Assert.Contains("commitish = \"main\"", output);
		Assert.Contains($"commit = \"{appTip}\"", output);
		Assert.Contains("commitish = \"v0\"", output);
		Assert.Contains($"commit = \"{libBaseline}\"", output);
	}

	[Fact]
	public async Task Select_BareName_FollowsTheSourceRepoHeadBranch()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var source = monorepo.SourceRepo("example");
		var tip = await AdvanceAsync(source, "trunk");
		await TestEnvironment.GitAsync(source, "symbolic-ref", "HEAD", "refs/heads/trunk");

		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "example", "-run", "echo");

		Assert.True(exitCode == 0, error);
		Assert.Contains("commitish = \"trunk\"", output);
		Assert.Contains($"commit = \"{tip}\"", output);
	}

	[Fact]
	public async Task Select_BareName_WithUnbornDefaultBranch_Fails()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "symbolic-ref", "HEAD", "refs/heads/missing");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", "example", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("has no commit on its default branch 'missing'", error);
		Assert.Contains("example@<tag|hash>", error);
		Assert.False(monorepo.HasRunDirs);
	}

	[Fact]
	public async Task Select_BareName_WithDetachedHead_Fails()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var source = monorepo.SourceRepo("example");
		await TestEnvironment.GitAsync(source, "update-ref", "--no-deref", "HEAD", await TestEnvironment.GitAsync(source, "rev-parse", "main"));

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", "example", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("has no default branch: its HEAD is detached", error);
		Assert.False(monorepo.HasRunDirs);
	}

	[Fact]
	public async Task Select_BareName_StillRequiresTheFullClosure()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", "app", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("'lib' (referenced by 'app')", error);
		Assert.Contains("-select app,lib.", error);
		Assert.False(monorepo.HasRunDirs);
	}

	[Fact]
	public async Task Select_BareName_Detached_ReportsAMissingBranchStraightAway()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "symbolic-ref", "HEAD", "refs/heads/missing");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", "example", "-detach", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains("has no commit on its default branch 'missing'", error);
		Assert.False(monorepo.HasRunDirs);
	}

	/// <summary>Moves (or creates) <paramref name="branch"/> to an untagged commit on top of v0 and returns it.</summary>
	private static async Task<string> AdvanceAsync(string repo, string branch)
	{
		var commit = await TestEnvironment.GitAsync(repo, "commit-tree", "v0^{tree}", "-p", "v0^{commit}", "-m", $"untagged on {branch}");
		await TestEnvironment.GitAsync(repo, "update-ref", $"refs/heads/{branch}", commit);
		return commit;
	}
}
