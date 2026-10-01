using Bassia.Git;

namespace Bassia.Tests.Git;

public class GitRefTests
{
	[Fact]
	public async Task ListAsync_ReturnsBranchesThenTagsAndTellsAnnotatedFromLightweight()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var source = monorepo.SourceRepo("example");
		await TestEnvironment.GitAsync(source, "tag", "light", "main");
		var commit = await TestEnvironment.GitAsync(source, "rev-parse", "main");

		var refs = await GitRef.ListAsync(GitClient.In(source));

		Assert.Equal(["main", "light", "v0"], refs.Select(reference => reference.Name).ToArray());
		Assert.Equal(GitRefKind.Branch, refs[0].Kind);
		Assert.Equal(GitRefKind.LightweightTag, refs[1].Kind);
		Assert.Equal(GitRefKind.AnnotatedTag, refs[2].Kind);
		Assert.All(refs, reference => Assert.Equal(commit, reference.Commit));
		Assert.Equal("Initial commit", refs[0].Subject);
		Assert.Equal("baseline", refs[2].Subject);
	}
}
