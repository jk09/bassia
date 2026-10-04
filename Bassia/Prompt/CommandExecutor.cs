namespace Bassia.Prompt;

using System.Diagnostics;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

/// <summary>What one <c>bassia</c> command printed and returned.</summary>
internal sealed record CommandOutcome(int ExitCode, string Output, string Error)
{
	/// <summary>
	/// The command's TOML result: from the last result marker on stdout (where a successful command prints it,
	/// possibly after streamed output) or else on stderr; null when there is none that parses.
	/// </summary>
	public TomlTable? Result
	{
		get
		{
			foreach (var text in new[] { Output, Error })
			{
				var start = text.LastIndexOf(TomlResult.Marker, StringComparison.Ordinal);
				if (start < 0)
				{
					continue;
				}

				try
				{
					return TomlSerializer.Deserialize<TomlTable>(text[start..]);
				}
				catch (TomlException)
				{
					// Something else printed after the marker; no usable result there.
				}
			}

			return null;
		}
	}

	/// <summary>The result as text: the TOML document, or the raw output when there is none.</summary>
	public string ResultText
	{
		get
		{
			foreach (var text in new[] { Output, Error })
			{
				var start = text.LastIndexOf(TomlResult.Marker, StringComparison.Ordinal);
				if (start >= 0)
				{
					return text[start..].Trim();
				}
			}

			return (Output + "\n" + Error).Trim();
		}
	}
}

/// <summary>Runs one <c>bassia</c> command line (arguments without <c>bassia</c>).</summary>
internal interface ICommandExecutor
{
	Task<CommandOutcome> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellation);
}

/// <summary>
/// Runs a command by starting this same <c>bassia</c> again with an argument list - no shell, so no quoting rules to
/// get wrong and nothing outside the command table can run. Its output is shown as it comes and captured for the LLM.
/// A child process rather than an in-process call: commands change the current directory (<c>-C</c>), stream
/// agents' output and may run for a long time, none of which should leak into the prompt's own process.
/// </summary>
internal sealed class SelfExecutor(string workingDirectory) : ICommandExecutor
{
	public async Task<CommandOutcome> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellation)
	{
		var startInfo = new ProcessStartInfo
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true
		};

		var (fileName, prefix) = SelfCommand();
		startInfo.FileName = fileName;
		foreach (var arg in prefix.Concat(args))
		{
			startInfo.ArgumentList.Add(arg);
		}

		using var process = Process.Start(startInfo) ?? throw new PromptException($"Could not start '{fileName}'.");
		process.StandardInput.Close();

		var output = new StringBuilder();
		var error = new StringBuilder();
		process.OutputDataReceived += (_, e) => Relay(e.Data, output, Console.Out);
		process.ErrorDataReceived += (_, e) => Relay(e.Data, error, Console.Error);
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		try
		{
			await process.WaitForExitAsync(cancellation);
		}
		catch (OperationCanceledException)
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
			{
				// It already exited.
			}

			throw;
		}

		// WaitForExit without a timeout also drains the redirected streams.
		process.WaitForExit();
		return new CommandOutcome(process.ExitCode, output.ToString(), error.ToString());
	}

	private static void Relay(string? line, StringBuilder capture, TextWriter console)
	{
		if (line is null)
		{
			return;
		}

		lock (capture)
		{
			capture.Append(line).Append('\n');
		}

		console.WriteLine(line);
	}

	/// <summary>
	/// How to start this bassia again: its own executable, or <c>dotnet &lt;Bassia.dll&gt;</c> when it was started that way.
	/// </summary>
	internal static (string FileName, IReadOnlyList<string> Prefix) SelfCommand()
	{
		var processPath = Environment.ProcessPath ?? throw new PromptException("Cannot tell which executable this bassia is.");
		var name = Path.GetFileNameWithoutExtension(processPath);
		if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
		{
			var assembly = typeof(SelfExecutor).Assembly.Location;
			return (processPath, [assembly]);
		}

		return (processPath, []);
	}
}
