namespace Bassia.Tests.Git;

using Bassia.Git;

public class GitClientTests
{
	[Fact]
	public async Task RunAsync_Version_ReturnsGitVersionOutput()
	{
		var client = GitClient.In(Environment.CurrentDirectory);

		var result = await client.RunAsync(["--version"]);

		Assert.Equal(0, result.ExitCode);
		Assert.StartsWith("git version", result.Output.Trim());
		Assert.Equal("", result.Error);
	}

	[Fact]
	public async Task RunOrThrowAsync_FailingCommand_ThrowsGitException()
	{
		var directory = Directory.CreateTempSubdirectory("bassia-git-client-tests-");
		try
		{
			var client = GitClient.In(directory.FullName);

			await Assert.ThrowsAsync<GitException>(() => client.RunOrThrowAsync(["log"]));
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}
}
