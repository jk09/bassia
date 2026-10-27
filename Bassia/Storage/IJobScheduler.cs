namespace Bassia.Storage;

/// <summary>
/// Where the monorepo keeps its live jobs - runs and integrations being executed right now - so any process (another
/// agent, the dashboard) sees what is live, follows its output and stops it. The durable records say what happened; a
/// job says who is doing it. <see cref="LocalJobScheduler"/> keeps them in <c>&lt;workspace&gt;/.jobs</c> and starts
/// detached jobs as background processes of this machine; a server keeps them for every machine.
/// </summary>
/// <remarks>
/// Whether a job is alive and how it is stopped are the scheduler's business: a caller never checks or kills a process
/// itself, since the job may run elsewhere.
/// </remarks>
internal interface IJobScheduler
{
	/// <summary>Where the jobs are kept, as the backend names it; shown to users, never parsed.</summary>
	string Location { get; }

	/// <summary>
	/// Registers the calling process as the one executing <paramref name="id"/>. The handle cancels when a stop is
	/// requested and, when disposed, ends the registration - a detached job's stays, finished, so its log can be read.
	/// </summary>
	IJobHandle Attach(string id, string kind, string? log = null, bool detached = false, bool hosted = false);

	Task<JobInfo?> FindAsync(string id);

	/// <summary>Every registered job of the given kind.</summary>
	Task<IReadOnlyList<JobInfo>> ListAsync(string kind);

	/// <summary>Whether the job is still being executed.</summary>
	bool IsAlive(JobInfo? job);

	/// <summary>
	/// Starts a detached job: a background bassia given the arguments <paramref name="arguments"/> builds from the job's log
	/// location, and waits until it has registered (or failed to). Returns the job and where its output goes.
	/// </summary>
	Task<DetachedJob> StartDetachedAsync(string id, string workingDirectory, Func<string, IReadOnlyList<string>> arguments);

	/// <summary>The output of a detached job from <paramref name="offset"/> on, and the offset to read on from; null when it has none.</summary>
	Task<JobLog?> ReadLogAsync(JobInfo job, long offset = 0);

	/// <summary>Where a detached job records its result.</summary>
	string ResultLocation(string id);

	/// <summary>Asks the job to stop; it notices within a fraction of a second.</summary>
	Task RequestStopAsync(string id);

	/// <summary>Withdraws a stop request, once the job is over.</summary>
	Task ClearStopAsync(string id);

	/// <summary>Ends the job by force when it does not stop on request; false when the scheduler may not (a job hosted by the dashboard).</summary>
	Task<bool> KillAsync(JobInfo job);
}

/// <summary>A registered job: id, kind, the process and machine executing it, when it started, its log, and how it runs.</summary>
internal sealed record JobInfo(string Id, string Kind, int Pid, string ProcessStart, string Started, string? Log, bool Detached, bool Hosted, string? Finished,
	string? Host = null);

/// <summary>The current process's registration as a job: its stop signal, and its end when disposed.</summary>
internal interface IJobHandle : IDisposable
{
	CancellationToken Cancellation { get; }

	/// <summary>Stops the job from within, e.g. on Ctrl-C.</summary>
	void Cancel();
}

/// <summary>A detached job as started: its registration (null when its process never registered), log and result locations, and the log so far when it failed.</summary>
internal sealed record DetachedJob(JobInfo? Job, string Log, string ResultFile, string Failure);

/// <summary>Part of a job's output: the text and the offset after it.</summary>
internal sealed record JobLog(string Text, long Next)
{
	public IReadOnlyList<string> Lines => Text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
