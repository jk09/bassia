namespace Bassia.Git;

using System.Diagnostics;

/// <summary>
/// Runs the <c>git</c command in the <paramref name="workingDirectory"/>.
/// </summary>
/// <param name="workingDirectory">The directory in which the <c>git</c> command is run.</param>
internal sealed class GitClient(string workingDirectory)
{
	private readonly string workingDirectory = workingDirectory;

    /// <summary>Returns a client bound to another working directory (e.g. a component checkout).</summary>
    public static GitClient In(string directory) => new(directory);

	public Task<GitResult> RunAsync(IReadOnlyList<string> arguments) => RunAsync(arguments, standardInput: null);

	public async Task<GitResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "git",
				WorkingDirectory = workingDirectory,
				RedirectStandardInput = standardInput is not null,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};

		foreach (var argument in arguments)
		{
			process.StartInfo.ArgumentList.Add(argument);
		}

		process.Start();
		var outputTask = process.StandardOutput.ReadToEndAsync();
		var errorTask = process.StandardError.ReadToEndAsync();
		if (standardInput is not null)
		{
			await process.StandardInput.WriteAsync(standardInput);
			process.StandardInput.Close();
		}

		await process.WaitForExitAsync();

		return new GitResult(process.ExitCode, await outputTask, await errorTask);
	}

	/// <summary>Runs git and throws <see cref="GitException"/> on a non-zero exit code; returns trimmed stdout.</summary>
	public async Task<string> RunOrThrowAsync(IReadOnlyList<string> arguments, string? standardInput = null)
	{
		var result = await RunAsync(arguments, standardInput);
		if (result.ExitCode != 0)
		{
			throw new GitException($"git {string.Join(' ', arguments)} failed in '{workingDirectory}': {result.Error.Trim()}");
		}

		return result.Output.Trim();
	}
}
