using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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
	static TestEnvironment() => EnsureGitIdentity();

	/// <summary>
	/// Commits and annotated tags need an identity even on machines without a global git config. Tests that drive
	/// bassia in-process get this from the static constructor; tests that start it as a child process (which
	/// inherits this environment) call it themselves, before the first commit their subprocess makes.
	/// </summary>
	public static void EnsureGitIdentity()
	{
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

	/// <summary>Shell command (for <c>bassia run start -run</c>) that writes <paramref name="content"/> to a file relative to the run folder.</summary>
	public static string WriteFileCommand(string relativePath, string content) =>
		OperatingSystem.IsWindows()
			? $"echo {content}> {relativePath.Replace('/', '\\')}"
			: $"echo '{content}' > {relativePath}";

	public static string FailingCommand => OperatingSystem.IsWindows() ? "exit /b 3" : "exit 3";

	/// <summary>Shell command that copies its stdin to <paramref name="path"/> - how a test sees what a resolver was handed.</summary>
	public static string CaptureStdinCommand(string path) =>
		OperatingSystem.IsWindows() ? $"more > \"{path}\"" : $"cat > '{path}'";

	/// <summary>
	/// Shell command that blocks for <paramref name="seconds"/>, standing in for an agent that is still working.
	/// On Windows it pings rather than calling <c>timeout</c>, which refuses to run with stdin redirected - which
	/// is exactly how the frontend runs an agent whose output it captures.
	/// </summary>
	public static string SleepCommand(int seconds) =>
		OperatingSystem.IsWindows() ? $"ping -n {seconds + 1} 127.0.0.1 > nul" : $"sleep {seconds}";

	/// <summary>The run id from an <c>agent</c> command's TOML result (stdout on success, stderr on failure).</summary>
	public static string RunIdOf(string toml)
	{
		var match = Regex.Match(toml, "run_id = \"(agent-run-[0-9a-f]{32})\"");
		Assert.True(match.Success, $"no run id in: {toml}");
		return match.Groups[1].Value;
	}
}

/// <summary>
/// A throw-away Bassia monorepo with source repositories for components created on demand. Each component's
/// upstream lives in <c>&lt;temp&gt;/upstream/&lt;name&gt;</c>; <c>component add</c> clones it as the bare
/// source-of-truth repo at <c>&lt;temp&gt;/root/&lt;name&gt;/.git</c>, exactly like a GitHub-hosted component.
/// </summary>
internal sealed class MonorepoFixture : IAsyncDisposable
{
	private readonly TempDirectory temp = new();

	public string Root => Path.Combine(temp.Path, "root");
	public string Workspace => Path.Combine(Root, ".workspace");
	public string MetaRepo => Path.Combine(Root, ".bassia");
	public string RunsRepo => Path.Combine(Root, ".agentic-runs", ".git");
	public string SourceRepo(string component) => Path.Combine(Root, component);
	public string RunDir(string runId) => Path.Combine(Workspace, runId);
	public string Checkout(string runId, string component) => Path.Combine(RunDir(runId), component);
	/// <summary>Run or integration folders in the workspace; the job folder <c>.jobs</c> does not count.</summary>
	public bool HasRunDirs => Directory.Exists(Workspace) && Directory.EnumerateDirectories(Workspace).Any(directory => !Path.GetFileName(directory).StartsWith('.'));

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

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(Root, "component", "add", "-url", upstream);
		Assert.True(exitCode == 0, error);
		await TestEnvironment.GitAsync(SourceRepo(name), "tag", "-a", tag, "-m", "baseline");
	}

	/// <summary>Adds <c>references = [...]</c> to a registered component by rewriting components.toml.</summary>
	public Task SetReferencesAsync(string component, params string[] references) =>
		WriteReferencesAsync(component, references.Select(reference => $"\"{reference}\""));

	/// <summary>Adds references that nest a component at a folder name other than the component's own name.</summary>
	public Task SetReferencesAsync(string component, params (string Name, string Path)[] references) =>
		WriteReferencesAsync(component, references.Select(reference => $"{{ name = \"{reference.Name}\", path = \"{reference.Path}\" }}"));

	private async Task WriteReferencesAsync(string component, IEnumerable<string> entries)
	{
		var path = Path.Combine(MetaRepo, "components.toml");
		var lines = (await File.ReadAllLinesAsync(path)).ToList();
		var nameLine = lines.IndexOf($"name = \"{component}\"");
		Assert.True(nameLine >= 0, $"component '{component}' not found in components.toml");
		lines.Insert(nameLine + 1, $"references = [{string.Join(", ", entries)}]");
		await File.WriteAllLinesAsync(path, lines);
	}

	/// <summary>Runs any bassia command line in the monorepo root.</summary>
	public Task<(int ExitCode, string Output, string Error)> BassiaAsync(params string[] args) =>
		TestEnvironment.RunInDirectoryAsync(Root, args);

	public Task<(int ExitCode, string Output, string Error)> RunStartAsync(params string[] args) =>
		TestEnvironment.RunInDirectoryAsync(Root, ["run", "start", .. args]);

	public Task<(int ExitCode, string Output, string Error)> IntegrationStartAsync(params string[] args) =>
		TestEnvironment.RunInDirectoryAsync(Root, ["integration", "start", .. args]);

	/// <summary>Runs an agent over <c>&lt;component&gt;@v0</c> that writes <paramref name="content"/> to a file, and returns the run id.</summary>
	public async Task<string> RunWritingAsync(string component, string file, string content)
	{
		var (exitCode, output, error) = await RunStartAsync("-select", $"{component}@v0", "-run", TestEnvironment.WriteFileCommand($"{component}/{file}", content));
		Assert.True(exitCode == 0, error);
		return TestEnvironment.RunIdOf(output);
	}

	public ValueTask DisposeAsync()
	{
		temp.Dispose();
		return ValueTask.CompletedTask;
	}
}
