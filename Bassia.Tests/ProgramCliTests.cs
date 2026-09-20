namespace Bassia.Tests;

public class ProgramCliTests
{
	[Fact]
	public async Task RunAsync_NoArguments_PrintsHelpAndReturnsSuccess()
	{
		var (exitCode, output, error) = await TestEnvironment.RunAsync();

		Assert.Equal(0, exitCode);
		Assert.Contains("bassia - a Git-based version control CLI", output);
		Assert.Empty(error);
	}

	[Theory]
	[InlineData("-h")]
	[InlineData("--help")]
	[InlineData("help")]
	public async Task RunAsync_HelpArgument_PrintsHelpAndReturnsSuccess(string helpArgument)
	{
		var (exitCode, output, _) = await TestEnvironment.RunAsync(helpArgument);

		Assert.Equal(0, exitCode);
		Assert.Contains("Usage: bassia [-C <path>] <command>", output);
	}

	[Fact]
	public async Task RunAsync_UnknownCommand_ReturnsExitCodeTwoAndWritesError()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("frobnicate");

		Assert.Equal(2, exitCode);
		Assert.Contains("Unknown command 'frobnicate'", error);
	}

	[Fact]
	public async Task RunAsync_CommitWithoutMessage_ReturnsUsageError()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("commit");

		Assert.Equal(2, exitCode);
		Assert.Contains("Usage: bassia commit -m", error);
	}

	[Fact]
	public async Task RunAsync_InitInEmptyDirectory_CreatesMetaRepoAndWorkspace()
	{
		using var workspace = new TempDirectory();

		var (exitCode, output, _) = await TestEnvironment.RunInDirectoryAsync(workspace.Path, "init");

		Assert.Equal(0, exitCode);
		Assert.True(Directory.Exists(Path.Combine(workspace.Path, ".bassia")));
		Assert.True(Directory.Exists(Path.Combine(workspace.Path, ".workspace")));
		Assert.True(File.Exists(Path.Combine(workspace.Path, ".bassia", "config.toml")));
		Assert.True(File.Exists(Path.Combine(workspace.Path, ".bassia", "components.toml")));
		Assert.Contains("\"ok\": true", output);
	}

	[Fact]
	public async Task RunAsync_InitTwice_SecondCallFails()
	{
		using var workspace = new TempDirectory();
		await TestEnvironment.RunInDirectoryAsync(workspace.Path, "init");

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(workspace.Path, "init");

		Assert.Equal(1, exitCode);
		Assert.Contains("already a Bassia monorepo", error);
	}

	[Fact]
	public async Task RunAsync_InitInNonEmptyDirectory_Fails()
	{
		using var workspace = new TempDirectory();
		await File.WriteAllTextAsync(Path.Combine(workspace.Path, "existing.txt"), "not empty");

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(workspace.Path, "init");

		Assert.Equal(1, exitCode);
		Assert.Contains("is not empty", error);
	}

	[Fact]
	public async Task RunAsync_InitWithDirectoryArgument_CreatesMonorepoInThatDirectory()
	{
		using var workspace = new TempDirectory();
		var target = Path.Combine(workspace.Path, "monorepo");

		var (exitCode, output, _) = await TestEnvironment.RunInDirectoryAsync(workspace.Path, "init", target);

		Assert.Equal(0, exitCode);
		Assert.True(Directory.Exists(Path.Combine(target, ".bassia")));
		Assert.True(Directory.Exists(Path.Combine(target, ".workspace")));
		Assert.Contains("\"ok\": true", output);
	}

	[Fact]
	public async Task RunAsync_WorkingDirectoryOption_RunsCommandInGivenDirectory()
	{
		using var workspace = new TempDirectory();
		var target = Path.Combine(workspace.Path, "monorepo");
		Directory.CreateDirectory(target);

		var (exitCode, output, _) = await TestEnvironment.RunInDirectoryAsync(workspace.Path, "-C", target, "init");

		Assert.Equal(0, exitCode);
		Assert.True(Directory.Exists(Path.Combine(target, ".bassia")));
		Assert.Contains("\"ok\": true", output);
	}

	[Fact]
	public async Task RunAsync_WorkingDirectoryOptionWithMissingDirectory_Fails()
	{
		using var workspace = new TempDirectory();
		var missing = Path.Combine(workspace.Path, "does-not-exist");

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(workspace.Path, "-C", missing, "init");

		Assert.Equal(2, exitCode);
		Assert.Contains("No such directory", error);
	}

	[Fact]
	public async Task RunAsync_WorkingDirectoryOptionWithoutPath_ReturnsUsageError()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("-C");

		Assert.Equal(2, exitCode);
		Assert.Contains("Usage: bassia -C <path>", error);
	}

	[Fact]
	public async Task RunAsync_AddComponentWithoutArguments_ReturnsUsageError()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("add-component");

		Assert.Equal(1, exitCode);
		Assert.Contains("Usage: bassia add-component", error);
	}

	[Fact]
	public async Task RunAsync_AddComponentWithoutInit_Fails()
	{
		using var workspace = new TempDirectory();

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(
			workspace.Path, "add-component", "https://example.invalid/component_1.git");

		Assert.Equal(1, exitCode);
		Assert.Contains("is not a Bassia monorepo", error);
	}
}
