using System.Diagnostics;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

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

	public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

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
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}

internal static class TestEnvironment
{
	[System.Runtime.CompilerServices.ModuleInitializer]
	internal static void Initialize()
	{
		EnsureGitIdentity();

		// A test runner may hand bassia a terminal; a bare 'bassia' must still print the plain help.
		ProgramCli.Interactive = () => false;
		Environment.SetEnvironmentVariable("BASSIA_USER", "tester");
	}

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

	/// <summary>Runs bassia in <paramref name="directory"/>, asserts success, and returns the parsed TOML result.</summary>
	public static async Task<TomlTable> OkAsync(string directory, params string[] args)
	{
		var (exit, output, error) = await RunInDirectoryAsync(directory, args);
		Assert.True(exit == 0, $"bassia {string.Join(' ', args)} failed ({exit}): {error}{output}");
		return Parse(output);
	}

	/// <summary>Runs bassia expecting a failure (exit code 1) and returns the parsed TOML error result.</summary>
	public static async Task<TomlTable> FailAsync(string directory, params string[] args)
	{
		var (exit, output, error) = await RunInDirectoryAsync(directory, args);
		Assert.True(exit == 1, $"bassia {string.Join(' ', args)} was expected to fail but exited {exit}: {output}{error}");
		return Parse(error);
	}

	public static TomlTable Parse(string text)
	{
		var start = text.LastIndexOf(TomlResult.Marker, StringComparison.Ordinal);
		Assert.True(start >= 0, $"No result marker in: {text}");
		return TomlSerializer.Deserialize<TomlTable>(text[start..])!;
	}

	public static async Task<string> GitAsync(string directory, params string[] args)
	{
		var startInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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

	/// <summary>A shell command (for <c>bassia agent start -run</c>) that writes <paramref name="content"/> to a file.</summary>
	public static string WriteFileCommand(string relativePath, string content) =>
		OperatingSystem.IsWindows() ? $"echo {content}> {relativePath.Replace('/', '\\')}" : $"echo '{content}' > {relativePath}";
}
