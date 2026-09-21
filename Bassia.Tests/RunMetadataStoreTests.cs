using Bassia.Git;

namespace Bassia.Tests;

public class RunMetadataStoreTests
{
	[Fact]
	public async Task ListLatestAsync_WithoutAnyRun_IsEmptyEvenBeforeTheStoreExists()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		var store = new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepo);

		Assert.Empty(await store.ListLatestAsync());
	}

	[Fact]
	public async Task ListLatestAsync_ReturnsEveryRunAtItsLatestLineageNewestFirst()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var (_, first, error) = await monorepo.AgentAsync("-select", "example@v0", "-run", TestEnvironment.WriteFileCommand("example/a.txt", "a"));
		Assert.True(first.Length > 0, error);
		var (_, _, failedError) = await monorepo.AgentAsync("-select", "example@v0", "-run", TestEnvironment.FailingCommand);
		var store = new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepo);

		var runs = await store.ListLatestAsync();

		Assert.Equal(2, runs.Count);
		Assert.Equal(TestEnvironment.RunIdOf(failedError), runs[0].RunId);
		Assert.Equal("failed", runs[0].Status);
		Assert.Equal(TestEnvironment.RunIdOf(first), runs[1].RunId);
		Assert.Equal("completed", runs[1].Status);
		Assert.Equal(1, runs[1].Lineage);
		Assert.Equal(ResultStatus.Pushed, runs[1].Components.Single().ResultStatus);
	}
}
