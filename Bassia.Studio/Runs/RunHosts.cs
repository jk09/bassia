namespace Bassia.Studio.Runs;

using System.Diagnostics;

/// <summary>
/// What a run host starts: the <c>bassia agent job</c> command in an environment that names the server, the job's
/// arguments (<c>agent start|replay ...</c>, as JSON) and where to report the run's output.
/// </summary>
public sealed record RunLaunch(string RunId, IReadOnlyDictionary<string, string> Environment);

/// <summary>Where runs execute: a process on this machine, or an Azure Container Apps job execution per run.</summary>
public interface IRunHost
{
	string Name { get; }

	/// <summary>Starts the job; returns the host's id for it (to stop it).</summary>
	Task<string> LaunchAsync(RunLaunch launch, CancellationToken ct = default);

	/// <summary>Ends the job at once (when it did not stop on request).</summary>
	Task KillAsync(string execution, CancellationToken ct = default);
}

/// <summary>
/// Runs each job as a local <c>bassia agent job</c> process, in a folder of its own: for development, tests and a
/// single machine. <paramref name="bassia"/> is the command line that starts bassia (e.g. <c>bassia</c>, or
/// <c>dotnet /path/Bassia.dll</c>).
/// </summary>
public sealed class LocalProcessRunHost(IReadOnlyList<string> bassia, string root) : IRunHost
{
	private readonly Dictionary<string, Process> processes = [];
	private readonly Lock gate = new();

	public string Name => "local";

	public Task<string> LaunchAsync(RunLaunch launch, CancellationToken ct = default)
	{
		var folder = Path.Combine(root, launch.RunId);
		Directory.CreateDirectory(folder);
		var start = new ProcessStartInfo(bassia[0])
		{
			WorkingDirectory = folder,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true
		};
		foreach (var arg in bassia.Skip(1).Concat(["agent", "job", "-path", folder]))
		{
			start.ArgumentList.Add(arg);
		}

		foreach (var (name, value) in launch.Environment)
		{
			start.Environment[name] = value;
		}

		var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{bassia[0]}'.");
		process.StandardInput.Close();
		// The job's own console goes to a file next to the clone (the run's output also reaches Studio as reports).
		_ = CopyAsync(process.StandardOutput, Path.Combine(folder, "job.log"));
		_ = CopyAsync(process.StandardError, Path.Combine(folder, "job.err"));
		lock (gate)
		{
			processes[launch.RunId] = process;
		}

		return Task.FromResult(launch.RunId);
	}

	private static async Task CopyAsync(StreamReader reader, string file)
	{
		await using var writer = new StreamWriter(file, append: true) { AutoFlush = true };
		while (await reader.ReadLineAsync() is { } line)
		{
			await writer.WriteLineAsync(line);
		}
	}

	public Task KillAsync(string execution, CancellationToken ct = default)
	{
		lock (gate)
		{
			if (processes.Remove(execution, out var process) && !process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}

		return Task.CompletedTask;
	}
}
