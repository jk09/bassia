namespace Bassia.Tests.Ui;

public class UiCommandTests
{
	[Fact]
	public async Task Ui_OutsideMonorepo_FailsLikeTheOtherCommands()
	{
		using var directory = new TempDirectory();

		var (exitCode, output, error) = await TestEnvironment.RunInDirectoryAsync(directory.Path, "ui");

		Assert.Equal(1, exitCode);
		Assert.Empty(output);
		Assert.Contains("is not inside a Bassia monorepo", error);
	}

	[Fact]
	public async Task Ui_WithoutInteractiveTerminal_FailsInsteadOfOpeningTheFrontend()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();

		var (exitCode, output, error) = await TestEnvironment.RunInDirectoryAsync(monorepo.Root, "ui");

		Assert.Equal(1, exitCode);
		Assert.Empty(output);
		Assert.Contains("interactive terminal", error);
	}

	[Fact]
	public async Task Help_MentionsTheUiCommand()
	{
		var (_, output, _) = await TestEnvironment.RunAsync("help");

		Assert.Contains("name = \"ui\"", output);
	}
}
