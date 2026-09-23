namespace Bassia;

using System.Diagnostics;

/// <summary>
/// Runs a user-supplied command line through the platform shell (<c>cmd.exe</c> / <c>sh</c>): the agent command of a
/// run and the resolver command of a semantic merge. Both are one shell line, so they may use pipes, <c>&amp;&amp;</c>
/// and quoting exactly as the user typed them.
/// </summary>
internal static class ShellCommand
{
	/// <summary>
	/// Runs <paramref name="command"/> in <paramref name="workingDirectory"/> and returns its exit code, or <c>null</c>
	/// when <paramref name="cancellation"/> fired and the process tree was killed. Without <paramref name="onOutput"/>
	/// the process inherits this console; with one, stdout and stderr are redirected and delivered line by line.
	/// <paramref name="standardInput"/>, when given, is written to the process's stdin, which is then closed.
	/// </summary>
	public static async Task<int?> RunAsync(
		string command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> environment,
		string? standardInput,
		Action<string>? onOutput,
		CancellationToken cancellation)
	{
		var startInfo = new ProcessStartInfo
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false
		};

		if (OperatingSystem.IsWindows())
		{
			startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
			// /s keeps the quoted command intact; cmd strips only the outer quotes.
			startInfo.Arguments = $"/d /s /c \"{command}\"";
		}
		else
		{
			startInfo.FileName = "/bin/sh";
			startInfo.ArgumentList.Add("-c");
			startInfo.ArgumentList.Add(command);
		}

		foreach (var (name, value) in environment)
		{
			startInfo.Environment[name] = value;
		}

		if (onOutput is not null)
		{
			startInfo.RedirectStandardOutput = true;
			startInfo.RedirectStandardError = true;
		}

		startInfo.RedirectStandardInput = standardInput is not null;

		using var process = Process.Start(startInfo)
			?? throw new IOException($"Could not start the command '{command}'.");

		if (onOutput is not null)
		{
			process.OutputDataReceived += (_, args) => Deliver(args.Data);
			process.ErrorDataReceived += (_, args) => Deliver(args.Data);
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
		}

		// Written alongside the wait rather than before it: a command that never reads its stdin would otherwise
		// block the writer on a full pipe forever.
		var writing = standardInput is null ? Task.CompletedTask : WriteInputAsync(process, standardInput);

		try
		{
			await process.WaitForExitAsync(cancellation);
			await writing;
		}
		catch (OperationCanceledException)
		{
			// The command runs under a shell, so the real worker is a grandchild: only killing the whole tree
			// actually stops it. Then wait unconditionally, so the working directory is quiescent before the caller
			// inspects or discards it.
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
			{
				// It exited between the cancellation and the kill; nothing left to stop.
			}

			await process.WaitForExitAsync(CancellationToken.None);
			await writing;
			return null;
		}

		return process.ExitCode;

		void Deliver(string? line)
		{
			if (line is not null)
			{
				onOutput(line);
			}
		}
	}

	private static async Task WriteInputAsync(Process process, string input)
	{
		try
		{
			await process.StandardInput.WriteAsync(input);
			process.StandardInput.Close();
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
		{
			// The command exited (or closed its stdin) without reading all of it; its exit code tells the rest.
		}
	}
}
