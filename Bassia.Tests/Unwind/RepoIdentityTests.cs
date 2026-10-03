using Bassia.Unwind;

namespace Bassia.Tests.Unwind;

public class RepoIdentityTests
{
	[Theory]
	[InlineData("https://github.com/Owner/Repo.git")]
	[InlineData("https://github.com/owner/repo")]
	[InlineData("https://github.com/owner/repo/")]
	[InlineData("http://user@GitHub.com/owner/repo.git")]
	[InlineData("ssh://git@github.com/owner/repo.git")]
	[InlineData("ssh://git@github.com:22/owner/repo")]
	[InlineData("git@github.com:owner/repo.git")]
	[InlineData("git://github.com/owner/repo")]
	public void Of_RemoteAddressesOfOneRepository_AreTheSameIdentity(string url) =>
		Assert.Equal("github.com/owner/repo", RepoIdentity.Of(url));

	[Fact]
	public void Of_DifferentRepositories_Differ()
	{
		Assert.NotEqual(RepoIdentity.Of("https://github.com/owner/repo"), RepoIdentity.Of("https://github.com/other/repo"));
		Assert.NotEqual(RepoIdentity.Of("https://github.com/owner/repo"), RepoIdentity.Of("https://gitlab.com/owner/repo"));
	}

	[Fact]
	public void Of_LocalPath_IsItsFullPathWithoutGitSuffix()
	{
		var path = Path.Combine(Path.GetTempPath(), "upstream", "lib");
		Assert.Equal(RepoIdentity.Of(path), RepoIdentity.Of(path + ".git"));
		Assert.Equal(RepoIdentity.Of(path), RepoIdentity.Of(Path.Combine(path, ".git")));
		Assert.Equal(RepoIdentity.Of(path), RepoIdentity.Of(path + Path.DirectorySeparatorChar));
		Assert.StartsWith("file:", RepoIdentity.Of(path));
	}

	[Theory]
	[InlineData("../lib.git", "https://github.com/owner/app.git", "https://github.com/owner/lib.git")]
	[InlineData("../../other/lib", "https://github.com/owner/app", "https://github.com/other/lib")]
	[InlineData("./sub", "https://github.com/owner/app", "https://github.com/owner/app/sub")]
	[InlineData("../lib.git", "git@github.com:owner/app.git", "git@github.com:owner/lib.git")]
	[InlineData("../lib.git", "git@github.com:app.git", "git@github.com:lib.git")]
	[InlineData("https://example.com/x.git", "https://github.com/owner/app", "https://example.com/x.git")]
	[InlineData("../lib", "/srv/upstream/app", "/srv/upstream/lib")]
	public void Resolve_RelativeUrls_AreRelativeToTheSuperprojectUrl(string url, string superproject, string expected) =>
		Assert.Equal(expected, RepoIdentity.Resolve(url, superproject));

	[Fact]
	public void Resolve_ClimbingAboveTheHost_Fails() =>
		Assert.Throws<MonorepoException>(() => RepoIdentity.Resolve("../../../x", "https://github.com/owner"));
}
