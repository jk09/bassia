namespace Bassia.Tests;

public class ProgramCliTests
{
	[Fact]
	public async Task RunAsync_NoArguments_PrintsHelpAndReturnsSuccess()
	{
		var (exitCode, output, error) = await TestEnvironment.RunAsync();

		Assert.Equal(0, exitCode);
		Assert.StartsWith(TomlResult.Marker, output);
		Assert.Contains("command = \"help\"", output);
		Assert.Contains("bassia - a Git-based monorepo", output);
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
		Assert.Contains("usage = \"bassia [-C <path>] <command> [<subcommand>]", output);
	}

	[Fact]
	public async Task RunAsync_UnknownCommand_ReturnsExitCodeTwoAndWritesError()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("frobnicate");

		Assert.Equal(2, exitCode);
		Assert.Contains("Unknown command 'frobnicate'", error);
	}

	[Theory]
	[InlineData("commit", "'bassia commit' was removed")]
	[InlineData("branch", "'bassia component show -name <component>'")]
	[InlineData("agent", "'bassia run start -select <component@tag,...> -run <command>'")]
	[InlineData("add-component", "'bassia component add -url <url> [-name <name>]'")]
	[InlineData("integrate", "'bassia integration plan|start|advance'")]
	[InlineData("ui", "'bassia web'")]
	public async Task RunAsync_ReplacedCommand_FailsNamingItsSuccessor(string command, string successor)
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync(command, "-m", "x");

		Assert.Equal(2, exitCode);
		Assert.Contains(successor, error);
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
		Assert.Contains("ok = true", output);
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
		Assert.Contains("ok = true", output);
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
		Assert.Contains("ok = true", output);
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
		Assert.Contains("-C requires a path", error);
	}

	[Fact]
	public async Task RunAsync_AddComponentWithoutArguments_ReturnsUsageError()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("component", "add");

		Assert.Equal(2, exitCode);
		Assert.Contains("Usage: bassia component add -url <url>", error);
	}

	[Fact]
	public async Task RunAsync_AddComponentWithoutInit_Fails()
	{
		using var workspace = new TempDirectory();

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(
			workspace.Path, "component", "add", "-url", "https://example.invalid/component_1.git");

		Assert.Equal(1, exitCode);
		Assert.Contains("is not inside a Bassia monorepo", error);
	}

	[Fact]
	public async Task RunAsync_AddComponentSuccess_CommitsComponentsTomlInMetaRepo()
	{
		using var workspace = new TempDirectory();
		var (initExitCode, _, initError) = await TestEnvironment.RunInDirectoryAsync(workspace.Path, "init");
		Assert.True(initExitCode == 0, initError);

		var upstream = Path.Combine(workspace.Path, "upstream");
		Directory.CreateDirectory(upstream);
		await TestEnvironment.GitAsync(upstream, "init", "--quiet", "--initial-branch=main");
		await File.WriteAllTextAsync(Path.Combine(upstream, "README.md"), "# component_1\n");
		await TestEnvironment.GitAsync(upstream, "add", "--all");
		await TestEnvironment.GitAsync(upstream, "commit", "--quiet", "-m", "Initial commit");

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(workspace.Path, "component", "add", upstream, "-name", "component_1");
		Assert.True(exitCode == 0, error);

		var metaRepoDir = Path.Combine(workspace.Path, ".bassia");
		var status = await TestEnvironment.GitAsync(metaRepoDir, "status", "--porcelain");
		Assert.Empty(status);

		var lastCommitMessage = await TestEnvironment.GitAsync(metaRepoDir, "log", "-1", "--format=%s");
		Assert.Equal("Add component 'component_1' from '" + upstream + "'", lastCommitMessage);

		var committedFiles = await TestEnvironment.GitAsync(metaRepoDir, "show", "--name-only", "--format=", "HEAD");
		Assert.Contains("components.toml", committedFiles.Split('\n'));
	}

	[Fact]
	public async Task RunAsync_Init_CommitsMetaRepoFiles()
	{
		using var workspace = new TempDirectory();
		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(workspace.Path, "init");
		Assert.True(exitCode == 0, error);

		var metaRepoDir = Path.Combine(workspace.Path, ".bassia");
		var status = await TestEnvironment.GitAsync(metaRepoDir, "status", "--porcelain");
		Assert.Empty(status);

		var committedFiles = await TestEnvironment.GitAsync(metaRepoDir, "show", "--name-only", "--format=", "HEAD");
		Assert.Contains("config.toml", committedFiles.Split('\n'));
		Assert.Contains("components.toml", committedFiles.Split('\n'));
	}
}
