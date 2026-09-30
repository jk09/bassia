namespace Bassia.Split;

using System.Diagnostics;
using Bassia.Git;

/// <summary>
/// Runs git with raw bytes on stdin and stdout. <see cref="GitClient"/> decodes text, which is right for porcelain
/// output but not for <c>fast-export</c>/<c>fast-import</c> streams and <c>-z</c> listings: their byte counts and
/// paths must survive exactly as git wrote them.
/// </summary>
internal static class GitBinary
{
	public static async Task<byte[]> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, byte[]? standardInput = null)
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
		using var output = new MemoryStream();
		var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
		var errorTask = process.StandardError.ReadToEndAsync();
		if (standardInput is not null)
		{
			try
			{
				await process.StandardInput.BaseStream.WriteAsync(standardInput);
				process.StandardInput.Close();
			}
			catch (IOException)
			{
				// git stopped reading (it failed); its exit code and stderr say why.
			}
		}

		await process.WaitForExitAsync();
		await outputTask;
		var error = await errorTask;
		if (process.ExitCode != 0)
		{
			throw new GitException($"git {string.Join(' ', arguments.Take(3))} failed in '{workingDirectory}': {error.Trim()}");
		}

		return output.ToArray();
	}

	/// <summary>Splits NUL-terminated output (<c>-z</c>) into its fields, decoded as UTF-8.</summary>
	public static List<string> SplitNul(byte[] output)
	{
		var fields = new List<string>();
		var start = 0;
		for (var i = 0; i < output.Length; i++)
		{
			if (output[i] == 0)
			{
				fields.Add(System.Text.Encoding.UTF8.GetString(output, start, i - start));
				start = i + 1;
			}
		}

		if (start < output.Length)
		{
			fields.Add(System.Text.Encoding.UTF8.GetString(output, start, output.Length - start));
		}

		return fields;
	}
}
