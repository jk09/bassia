namespace Bassia.Scenarios.Harness;

using System.Diagnostics;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

/// <summary>One <c>bassia</c> invocation: its command line, exit code, output and parsed TOML result.</summary>
public sealed record CliResult(IReadOnlyList<string> Args, string Directory, int ExitCode, string Stdout, string Stderr)
{
	/// <summary>The marker every result starts with (docs/reference.md, "Results").</summary>
	public const string Marker = "# bassia result";

	/// <summary>The TOML result: from stdout on success, from stderr on failure; empty when there is none (exit code 2 prints usage).</summary>
	public TomlTable Result => result ??= Parse(ExitCode == 0 ? Stdout : Stderr) ?? Parse(Stdout) ?? Parse(Stderr) ?? new TomlTable();

	private TomlTable? result;

	public bool HasResult => Parse(Stdout) is not null || Parse(Stderr) is not null;

	public string CommandLine => "bassia " + string.Join(' ', Args.Select(arg => arg.Length == 0 || arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg));

	private static TomlTable? Parse(string text)
	{
		var start = text.LastIndexOf(Marker, StringComparison.Ordinal);
		return start < 0 ? null : TomlSerializer.Deserialize<TomlTable>(text[start..]);
	}
}

/// <summary>
/// Runs <c>bassia</c>: in-process by default (the same entry point the executable calls, so breakpoints in Bassia's
/// code are hit), or the installed executable named by <c>BASSIA_SCENARIO_CLI</c>. Only arguments, environment,
/// working directory, exit code and output cross this boundary.
/// </summary>
public static class Cli
{
	// The in-process CLI reads the current directory and the environment, and writes to Console: all process-global.
	private static readonly SemaphoreSlim Gate = new(1, 1);

	public static async Task<CliResult> RunAsync(string directory, IReadOnlyDictionary<string, string?> environment, IReadOnlyList<string> args, TimeSpan? timeout = null)
	{
		if (ScenarioEnvironment.ExternalCli is { } executable)
		{
			return await RunProcessAsync(executable, directory, environment, args, timeout ?? TimeSpan.FromMinutes(10));
		}

		await Gate.WaitAsync();
		var originalDirectory = Environment.CurrentDirectory;
		var originalOut = Console.Out;
		var originalError = Console.Error;
		var saved = environment.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
		var stdout = new StringWriter(new StringBuilder());
		var stderr = new StringWriter(new StringBuilder());
		try
		{
			foreach (var (key, value) in environment)
			{
				Environment.SetEnvironmentVariable(key, value);
			}

			Directory.SetCurrentDirectory(directory);
			Console.SetOut(stdout);
			Console.SetError(stderr);
			var exitCode = await ProgramCli.RunAsync([.. args]);
			return new CliResult(args, directory, exitCode, stdout.ToString(), stderr.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
			Directory.SetCurrentDirectory(originalDirectory);
			foreach (var (key, value) in saved)
			{
				Environment.SetEnvironmentVariable(key, value);
			}

			Gate.Release();
		}
	}

	private static async Task<CliResult> RunProcessAsync(string executable, string directory, IReadOnlyDictionary<string, string?> environment, IReadOnlyList<string> args, TimeSpan timeout)
	{
		var start = new ProcessStartInfo(executable)
		{
			WorkingDirectory = directory,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var arg in args)
		{
			start.ArgumentList.Add(arg);
		}

		foreach (var (key, value) in environment)
		{
			start.Environment[key] = value;
		}

		using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
		process.StandardInput.Close();
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		using var cancel = new CancellationTokenSource(timeout);
		try
		{
			await process.WaitForExitAsync(cancel.Token);
		}
		catch (OperationCanceledException)
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException($"bassia {string.Join(' ', args)} did not finish within {timeout}.");
		}

		return new CliResult(args, directory, process.ExitCode, await stdout, await stderr);
	}
}
