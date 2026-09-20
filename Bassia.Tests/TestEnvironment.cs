using System.Diagnostics;
using System.Text;

// ProgramCli reads Environment.CurrentDirectory and writes to Console, both process-global.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Bassia.Tests;

internal sealed class TempDirectory : IDisposable
{
	public string Path { get; }

	public TempDirectory()
	{
		Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bassia-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path);
	}

	public void Dispose()
	{
		try
		{
			// Git marks pack files read-only, which Directory.Delete refuses to remove on Windows.
			foreach (var file in Directory.EnumerateFiles(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
			{
				File.SetAttributes(file, FileAttributes.Normal);
			}

			Directory.Delete(Path, recursive: true);
		}
		catch (IOException)
		{
			// Best-effort cleanup; leftover temp folders don't affect other tests.
		}
		catch (UnauthorizedAccessException)
		{
			// Same as above.
		}
	}
}

internal static class TestEnvironment
{
	static TestEnvironment()
	{
		// Commits and annotated tags need an identity even on machines without a global git config.
		foreach (var variable in new[] { "GIT_AUTHOR_NAME", "GIT_COMMITTER_NAME" })
		{
			Environment.SetEnvironmentVariable(variable, Environment.GetEnvironmentVariable(variable) ?? "Bassia Tests");
		}

		foreach (var variable in new[] { "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_EMAIL" })
		{
			Environment.SetEnvironmentVariable(variable, Environment.GetEnvironmentVariable(variable) ?? "tests@bassia.invalid");
		}
	}

	public static Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args) =>
		RunInDirectoryAsync(Environment.CurrentDirectory, args);

	public static async Task<(int ExitCode, string Output, string Error)> RunInDirectoryAsync(string directory, params string[] args)
	{
		var originalDirectory = Environment.CurrentDirectory;
		var originalOut = Console.Out;
		var originalError = Console.Error;

		var outWriter = new StringWriter(new StringBuilder());
		var errorWriter = new StringWriter(new StringBuilder());

		Directory.SetCurrentDirectory(directory);
		Console.SetOut(outWriter);
		Console.SetError(errorWriter);

		try
		{
			var exitCode = await ProgramCli.RunAsync(args);
			return (exitCode, outWriter.ToString(), errorWriter.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
			Directory.SetCurrentDirectory(originalDirectory);
		}
	}

	/// <summary>Runs git in <paramref name="directory"/> and returns trimmed stdout; fails the test on a non-zero exit code.</summary>
	public static async Task<string> GitAsync(string directory, params string[] args)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = directory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (var arg in args)
		{
			startInfo.ArgumentList.Add(arg);
		}

		using var process = Process.Start(startInfo)!;
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed in '{directory}': {await error}");
		return (await output).Trim();
	}

	/// <summary>Shell command (for <c>bassia agent -run</c>) that writes <paramref name="content"/> to a file relative to the run folder.</summary>
	public static string WriteFileCommand(string relativePath, string content) =>
		OperatingSystem.IsWindows()
			? $"echo {content}> {relativePath.Replace('/', '\\')}"
			: $"echo '{content}' > {relativePath}";

	public static string FailingCommand => OperatingSystem.IsWindows() ? "exit /b 3" : "exit 3";
}

/// <summary>
/// A throw-away Bassia monorepo with source repositories for components created on demand. Each component's
/// upstream lives in <c>&lt;temp&gt;/upstream/&lt;name&gt;</c>; <c>add-component</c> clones it as the bare
/// source-of-truth repo at <c>&lt;temp&gt;/root/&lt;name&gt;/.git</c>, exactly like a GitHub-hosted component.
/// </summary>
internal sealed class MonorepoFixture : IAsyncDisposable
{
	private readonly TempDirectory temp = new();

	public string Root => Path.Combine(temp.Path, "root");
	public string Workspace => Path.Combine(Root, ".workspace");
	public string Cache => Path.Combine(Root, ".cache");
	public string RunsRepo => Path.Combine(Workspace, ".agentic-runs", ".git");
	public string SourceRepo(string component) => Path.Combine(Root, component);
	public string RunDir(string runId) => Path.Combine(Workspace, runId);

	public static async Task<MonorepoFixture> CreateAsync()
	{
		var fixture = new MonorepoFixture();
		Directory.CreateDirectory(fixture.Root);
		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(fixture.Root, "init");
		Assert.True(exitCode == 0, error);
		return fixture;
	}

	/// <summary>Creates an upstream repo with one commit tagged <paramref name="tag"/> and registers it as a component.</summary>
	public async Task AddComponentAsync(string name, string tag = "v0")
	{
		var upstream = Path.Combine(temp.Path, "upstream", name);
		Directory.CreateDirectory(upstream);
		await TestEnvironment.GitAsync(upstream, "init", "--quiet", "--initial-branch=main");
		await File.WriteAllTextAsync(Path.Combine(upstream, "README.md"), $"# {name}\n");
		await TestEnvironment.GitAsync(upstream, "add", "--all");
		await TestEnvironment.GitAsync(upstream, "commit", "--quiet", "-m", "Initial commit");

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(Root, "add-component", upstream);
		Assert.True(exitCode == 0, error);
		await TestEnvironment.GitAsync(SourceRepo(name), "tag", "-a", tag, "-m", "baseline");
	}

	/// <summary>Adds <c>references = [...]</c> to a registered component by rewriting components.toml.</summary>
	public async Task SetReferencesAsync(string component, params string[] references)
	{
		var path = Path.Combine(Root, ".bassia", "components.toml");
		var lines = (await File.ReadAllLinesAsync(path)).ToList();
		var nameLine = lines.IndexOf($"name = \"{component}\"");
		Assert.True(nameLine >= 0, $"component '{component}' not found in components.toml");
		lines.Insert(nameLine + 1, $"references = [{string.Join(", ", references.Select(reference => $"\"{reference}\""))}]");
		await File.WriteAllLinesAsync(path, lines);
	}

	public Task<(int ExitCode, string Output, string Error)> AgentAsync(params string[] args) =>
		TestEnvironment.RunInDirectoryAsync(Root, ["agent", .. args]);

	public ValueTask DisposeAsync()
	{
		temp.Dispose();
		return ValueTask.CompletedTask;
	}
}
