namespace Bassia;

using Bassia.Storage;

using System.Diagnostics;
using System.Globalization;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// The jobs of this machine (<see cref="Storage.IJobScheduler"/>): the job folder <c>&lt;workspace&gt;/.jobs</c>, per
/// job a <c>&lt;id&gt;.toml</c> record, the <c>&lt;id&gt;.log</c> a detached job writes its output to, the
/// <c>&lt;id&gt;.result.toml</c> it writes its result to, and the <c>&lt;id&gt;.stop</c> file another process creates to
/// ask it to stop. A job is alive while its process is; detached jobs are background bassia processes. It is local,
/// disposable state, which is why it sits in the workspace and not in the durable <c>.agentic-runs</c> record.
/// </summary>
internal sealed class LocalJobScheduler(string workspaceDir) : Storage.IJobScheduler
{
	public const string FolderName = ".jobs";

	/// <summary>How long a detached job may take to register before it counts as failed to start.</summary>
	private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(30);

	public string Dir { get; } = Path.Combine(workspaceDir, FolderName);

	public string Location => Dir;

	public string LogPath(string id) => Path.Combine(Dir, id + ".log");

	public string ResultLocation(string id) => Path.Combine(Dir, id + ".result.toml");

	private string RecordPath(string id) => Path.Combine(Dir, id + ".toml");

	private string StopPath(string id) => Path.Combine(Dir, id + ".stop");

	/// <summary>
	/// Registers the current process as the one executing <paramref name="id"/>. The returned handle cancels when a
	/// stop is requested and, when disposed, removes the registration - or, for a detached job, marks it finished
	/// and keeps it, so its log can still be read.
	/// </summary>
	public Storage.IJobHandle Attach(string id, string kind, string? log = null, bool detached = false, bool hosted = false)
	{
		Directory.CreateDirectory(Dir);
		File.Delete(StopPath(id));
		var job = new JobInfo(id, kind, Environment.ProcessId, CurrentProcessStart(), RunMetadata.Timestamp(), log, detached, hosted, null, Environment.MachineName);
		Write(job);
		return new JobHandle(this, job, StopPath(id));
	}

	public Task<JobInfo?> FindAsync(string id) => Task.FromResult(Find(id));

	private JobInfo? Find(string id)
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
				table.TryGetValue("finished", out var finished) ? finished as string : null,
				table.TryGetValue("host", out var host) ? host as string : null);
		}
		catch (Exception ex) when (ex is IOException or TomlException or KeyNotFoundException or InvalidCastException)
		{
			// Being rewritten right now, or not a job record: either way nothing reliable to report.
			return null;
		}
	}

	/// <summary>Every registered job of the given kind.</summary>
	public Task<IReadOnlyList<JobInfo>> ListAsync(string kind) => Task.FromResult<IReadOnlyList<JobInfo>>(
		!Directory.Exists(Dir)
			? []
			: Directory.EnumerateFiles(Dir, "*.toml")
				.Where(path => !path.EndsWith(".result.toml", StringComparison.Ordinal))
				.Select(path => Find(Path.GetFileNameWithoutExtension(path)))
				.OfType<JobInfo>()
				.Where(job => job.Kind == kind)
				.ToList());

	/// <summary>
	/// Whether the job's process is still running. The process's start time has to match the registration too, so a
	/// recycled process id never makes a long-finished job look live.
	/// </summary>
	public bool IsAlive(JobInfo? job) => job is not null && job.Finished is null && IsProcessAlive(job.Pid, job.ProcessStart);

	/// <summary>
	/// Starts the background bassia, its log created first, and waits until it has registered its job or has exited
	/// without doing so; a process that failed leaves the end of its log for the caller to report.
	/// </summary>
	public async Task<Storage.DetachedJob> StartDetachedAsync(string id, string workingDirectory, Func<string, IReadOnlyList<string>> arguments)
	{
		Directory.CreateDirectory(Dir);
		var log = LogPath(id);
		await File.WriteAllTextAsync(log, "");
		using var process = DetachedProcess.Start(workingDirectory, arguments(log));
		JobInfo? job = null;
		var clock = Stopwatch.StartNew();
		while (clock.Elapsed < RegistrationTimeout)
		{
			if ((job = Find(id)) is not null)
			{
				break;
			}

			if (process.HasExited)
			{
				job = Find(id);
				break;
			}

			await Task.Delay(50);
		}

		var failure = job is null && await ReadLogAsync(log, 0) is { } text
			? string.Join(" | ", text.Lines.Where(line => line.Trim().Length > 0).TakeLast(20))
			: "";
		return new Storage.DetachedJob(job, log, ResultLocation(id), failure);
	}

	/// <summary>The log file from <paramref name="offset"/> on; a negative offset reads the last that many bytes.</summary>
	public Task<Storage.JobLog?> ReadLogAsync(JobInfo job, long offset = 0) =>
		job.Log is null ? Task.FromResult<Storage.JobLog?>(null) : ReadLogAsync(job.Log, offset);

	private static async Task<Storage.JobLog?> ReadLogAsync(string path, long offset)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var start = offset < 0 ? Math.Max(0, stream.Length + offset) : Math.Min(offset, stream.Length);
			stream.Seek(start, SeekOrigin.Begin);
			using var reader = new StreamReader(stream);
			var text = await reader.ReadToEndAsync();
			return new Storage.JobLog(text, stream.Position);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>Whether the process <paramref name="pid"/> runs and started at <paramref name="processStart"/> (as <see cref="CurrentProcessStart"/> records it).</summary>
	public static bool IsProcessAlive(int pid, string processStart)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			return !process.HasExited && SameStart(process.StartTime, processStart);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
		{
			return false;
		}
	}

	/// <summary>This process's start time, as recorded and compared to tell a live process from a recycled id.</summary>
	public static string CurrentProcessStart()
	{
		using var self = Process.GetCurrentProcess();
		return Stamp(self.StartTime);
	}

	/// <summary>Asks the job's process to stop; it notices within a fraction of a second.</summary>
	public Task RequestStopAsync(string id)
	{
		Directory.CreateDirectory(Dir);
		File.WriteAllText(StopPath(id), RunMetadata.Timestamp());
		return Task.CompletedTask;
	}

	public Task ClearStopAsync(string id)
	{
		ClearStop(id);
		return Task.CompletedTask;
	}

	/// <summary>
	/// Kills the job's process tree: the last resort when it does not stop on request. A hosted job is never killed,
	/// since its process is the web dashboard serving everything else too.
	/// </summary>
	public Task<bool> KillAsync(JobInfo job)
	{
		if (job.Hosted)
		{
			return Task.FromResult(false);
		}

		Kill(job);
		return Task.FromResult(true);
	}

	private static void Kill(JobInfo job)
	{
		try
		{
			using var process = Process.GetProcessById(job.Pid);
			if (SameStart(process.StartTime, job.ProcessStart))
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
		if (job.Host is not null) table["host"] = job.Host;

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

	/// <summary>How far two readings of one process's start time may differ and still name the same process.</summary>
	internal static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

	/// <summary>
	/// Whether a process that started at <paramref name="startTime"/> is the one recorded as <paramref name="processStart"/>.
	/// Not an equality of stamps: on Linux each process derives a start time from the clock and the uptime, so two
	/// processes read the same start a few milliseconds apart, and near a second boundary their stamps differ. A
	/// recycled process id that also started within the tolerance is not a realistic case.
	/// </summary>
	internal static bool SameStart(DateTime startTime, string processStart) =>
		DateTime.TryParseExact(processStart, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
			DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var recorded)
		&& (startTime.ToUniversalTime() - recorded).Duration() <= StartTolerance;
}

/// <summary>The current process's registration as a job: its stop signal, and its removal when disposed.</summary>
internal sealed class JobHandle : Storage.IJobHandle
{
	private readonly LocalJobScheduler registry;
	private readonly JobInfo job;
	private readonly string stopPath;
	private readonly CancellationTokenSource cancellation = new();
	private readonly Timer watcher;

	internal JobHandle(LocalJobScheduler registry, JobInfo job, string stopPath)
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
