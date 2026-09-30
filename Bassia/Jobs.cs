namespace Bassia;

using System.Diagnostics;
using System.Globalization;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// A run or integration a <c>bassia</c> process is executing right now, as registered in the job folder. The record
/// is what lets another <c>bassia</c> process - usually another agent - see that the work is live, read its output
/// and stop it: the run records in <c>.agentic-runs</c> say what happened, a job says who is doing it.
/// <see cref="Hosted"/> jobs run inside <c>bassia web</c>, whose process must never be killed to stop one of them; <see cref="Detached"/> ones in a background process of their own, with a log.
/// </summary>
internal sealed record JobInfo(string Id, string Kind, int Pid, string ProcessStart, string Started, string? Log, bool Detached, bool Hosted, string? Finished);

/// <summary>
/// The job folder <c>&lt;workspace&gt;/.jobs</c>: per job a <c>&lt;id&gt;.toml</c> record, the <c>&lt;id&gt;.log</c> a
/// detached job writes its output to, the <c>&lt;id&gt;.result.toml</c> it writes its result to, and the
/// <c>&lt;id&gt;.stop</c> file another process creates to ask it to stop. It is local, disposable state, which is
/// why it sits in the workspace and not in the durable <c>.agentic-runs</c> record.
/// </summary>
internal sealed class JobRegistry(Monorepo monorepo)
{
	public const string FolderName = ".jobs";

	public string Dir { get; } = Path.Combine(monorepo.WorkspaceDir, FolderName);

	public string LogPath(string id) => Path.Combine(Dir, id + ".log");

	public string ResultPath(string id) => Path.Combine(Dir, id + ".result.toml");

	private string RecordPath(string id) => Path.Combine(Dir, id + ".toml");

	private string StopPath(string id) => Path.Combine(Dir, id + ".stop");

	/// <summary>
	/// Registers the current process as the one executing <paramref name="id"/>. The returned handle cancels when a
	/// stop is requested and, when disposed, removes the registration - or, for a detached job, marks it finished
	/// and keeps it, so its log can still be read.
	/// </summary>
	public JobHandle Attach(string id, string kind, string? log = null, bool detached = false, bool hosted = false)
	{
		Directory.CreateDirectory(Dir);
		File.Delete(StopPath(id));
		using var self = Process.GetCurrentProcess();
		var job = new JobInfo(id, kind, Environment.ProcessId, Stamp(self.StartTime), RunMetadata.Timestamp(), log, detached, hosted, null);
		Write(job);
		return new JobHandle(this, job, StopPath(id));
	}

	public JobInfo? Find(string id)
	{
		var path = RecordPath(id);
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			var table = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path));
			if (table is null)
			{
				return null;
			}

			return new JobInfo(
				(string)table["id"], (string)table["kind"], (int)(long)table["pid"], (string)table["process_start"], (string)table["started"],
				table.TryGetValue("log", out var log) ? log as string : null,
				table.TryGetValue("detached", out var detached) && detached is true,
				table.TryGetValue("hosted", out var hosted) && hosted is true,
				table.TryGetValue("finished", out var finished) ? finished as string : null);
		}
		catch (Exception ex) when (ex is IOException or TomlException or KeyNotFoundException or InvalidCastException)
		{
			// Being rewritten right now, or not a job record: either way nothing reliable to report.
			return null;
		}
	}

	/// <summary>Every registered job id of the given kind.</summary>
	public IReadOnlyList<JobInfo> List(string kind) =>
		!Directory.Exists(Dir)
			? []
			: Directory.EnumerateFiles(Dir, "*.toml")
				.Where(path => !path.EndsWith(".result.toml", StringComparison.Ordinal))
				.Select(path => Find(Path.GetFileNameWithoutExtension(path)))
				.OfType<JobInfo>()
				.Where(job => job.Kind == kind)
				.ToList();

	/// <summary>
	/// Whether the job's process is still running. The process's start time has to match the registration too, so a
	/// recycled process id never makes a long-finished job look live.
	/// </summary>
	public static bool IsAlive(JobInfo? job)
	{
		if (job is null || job.Finished is not null)
		{
			return false;
		}

		try
		{
			using var process = Process.GetProcessById(job.Pid);
			return !process.HasExited && Stamp(process.StartTime) == job.ProcessStart;
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
		{
			return false;
		}
	}

	/// <summary>Asks the job's process to stop; it notices within a fraction of a second.</summary>
	public void RequestStop(string id)
	{
		Directory.CreateDirectory(Dir);
		File.WriteAllText(StopPath(id), RunMetadata.Timestamp());
	}

	/// <summary>
	/// Kills the job's process tree: the last resort when it does not stop on request. A hosted job is never killed,
	/// since its process is the web dashboard serving everything else too.
	/// </summary>
	public static void Kill(JobInfo job)
	{
		if (job.Hosted)
		{
			return;
		}

		try
		{
			using var process = Process.GetProcessById(job.Pid);
			if (Stamp(process.StartTime) == job.ProcessStart)
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit(10_000);
			}
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
		{
			// Already gone.
		}
	}

	internal void Write(JobInfo job)
	{
		var table = new TomlTable
		{
			["id"] = job.Id,
			["kind"] = job.Kind,
			["pid"] = (long)job.Pid,
			["process_start"] = job.ProcessStart,
			["started"] = job.Started,
			["detached"] = job.Detached,
			["hosted"] = job.Hosted
		};
		if (job.Log is not null) table["log"] = job.Log;
		if (job.Finished is not null) table["finished"] = job.Finished;

		// Written aside and moved into place, so a reader never sees half a record.
		var path = RecordPath(job.Id);
		var temporary = path + $".{Environment.ProcessId}.tmp";
		File.WriteAllText(temporary, TomlSerializer.Serialize(table));
		File.Move(temporary, path, overwrite: true);
	}

	internal void Remove(string id)
	{
		File.Delete(RecordPath(id));
		File.Delete(StopPath(id));
	}

	internal void ClearStop(string id) => File.Delete(StopPath(id));

	/// <summary>A process start time, to the second, as recorded and compared.</summary>
	private static string Stamp(DateTime startTime) =>
		startTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

/// <summary>The current process's registration as a job: its stop signal, and its removal when disposed.</summary>
internal sealed class JobHandle : IDisposable
{
	private readonly JobRegistry registry;
	private readonly JobInfo job;
	private readonly string stopPath;
	private readonly CancellationTokenSource cancellation = new();
	private readonly Timer watcher;

	internal JobHandle(JobRegistry registry, JobInfo job, string stopPath)
	{
		this.registry = registry;
		this.job = job;
		this.stopPath = stopPath;
		watcher = new Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
	}

	public CancellationToken Cancellation => cancellation.Token;

	/// <summary>Stops the job from within, e.g. on Ctrl-C.</summary>
	public void Cancel()
	{
		try
		{
			cancellation.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// Already finished.
		}
	}

	private void Poll()
	{
		if (File.Exists(stopPath))
		{
			Cancel();
		}
	}

	public void Dispose()
	{
		watcher.Dispose();
		if (job.Detached)
		{
			registry.Write(job with { Finished = RunMetadata.Timestamp() });
			registry.ClearStop(job.Id);
		}
		else
		{
			registry.Remove(job.Id);
		}

		cancellation.Dispose();
	}
}

/// <summary>
/// Starts <c>bassia</c> again as a background process for <c>-detach</c>. None of the standard handles is
/// inherited - a caller capturing this process's output must not be held open by the background one - and on
/// Unix the process gets its own session, so it survives the shell or tool call that started it.
/// </summary>
internal static class DetachedProcess
{
	public static Process Start(string workingDirectory, IReadOnlyList<string> arguments)
	{
		var (executable, prefix) = Executable();
		var startInfo = new ProcessStartInfo
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		IEnumerable<string> command = prefix;
		if (!OperatingSystem.IsWindows() && File.Exists("/usr/bin/setsid"))
		{
			startInfo.FileName = "/usr/bin/setsid";
			startInfo.ArgumentList.Add(executable);
		}
		else
		{
			startInfo.FileName = executable;
		}

		foreach (var argument in command.Concat(arguments))
		{
			startInfo.ArgumentList.Add(argument);
		}

		var process = Process.Start(startInfo) ?? throw new IOException("Could not start the background bassia process.");
		process.StandardInput.Close();
		return process;
	}

	/// <summary>The bassia apphost next to this assembly, or <c>dotnet Bassia.dll</c> when there is none.</summary>
	private static (string Executable, IReadOnlyList<string> Prefix) Executable()
	{
		var assembly = typeof(DetachedProcess).Assembly.Location;
		var apphost = Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "Bassia.exe" : "Bassia");
		return File.Exists(apphost) ? (apphost, []) : ("dotnet", [assembly]);
	}
}
