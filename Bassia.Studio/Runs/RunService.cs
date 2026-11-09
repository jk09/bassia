namespace Bassia.Studio.Runs;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

/// <summary>A run to start: a task (prompt or command) in a new workspace, or a replay of a workspace's recorded task.</summary>
public sealed record RunSpec(
	string? Prompt = null,
	string? Command = null,
	string? On = null,
	string? Workspace = null,
	string? Name = null,
	string? Model = null,
	bool Land = false,
	IReadOnlyList<string>? Leases = null,
	IReadOnlyList<string>? Context = null,
	string? ReplayOf = null);

/// <summary>The states of a run Studio started, before and after the run records itself on its workspace.</summary>
public static class StudioRunStatus
{
	public const string Starting = "starting";
	public const string LaunchFailed = "launch-failed";
	public const string JobFailed = "job-failed";

	public static bool IsLive(string status) => status is Starting or RunStatus.Running;
}

/// <summary>A run Studio started: what it asked for, its job, and the output reported so far.</summary>
public sealed class StudioRun(string runId, RunSpec spec, string user, string token)
{
	public const int MaxLines = 5000;

	public string RunId => runId;

	public RunSpec Spec => spec;

	public string User => user;

	internal string Token => token;

	public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

	public DateTimeOffset? Finished { get; internal set; }

	public string Status { get; internal set; } = StudioRunStatus.Starting;

	public string? Execution { get; internal set; }

	public string? WorkspaceId { get; internal set; }

	public string? Message { get; internal set; }

	public bool StopRequested { get; internal set; }

	internal List<string> Lines { get; } = [];

	/// <summary>The output so far (the last <see cref="MaxLines"/> lines).</summary>
	public IReadOnlyList<string> Output
	{
		get
		{
			lock (Lines)
			{
				return Lines.ToList();
			}
		}
	}
}

/// <summary>A run as the runs page lists it: one Studio started, or one recorded on a workspace (started anywhere).</summary>
public sealed record RunView(string RunId, string Status, string? WorkspaceId, string? WorkspaceName, string Agent, string? Task, DateTimeOffset Started,
	DateTimeOffset? Finished, StudioRun? Studio, RunRecord? Record);

/// <summary>
/// Starts agentic runs on a run host and follows them: each run is a <c>bassia agent job</c> that clones the server and
/// runs <c>agent start</c> (or <c>agent replay</c>) as the CLI does - the same workspace, recipe, run record and
/// landing - and posts its output back here, where open pages receive it live. Stopping asks the run to stop (it then
/// snapshots what the agent changed); a job that does not stop within the grace period is killed.
/// </summary>
public sealed class RunService(MonorepoClient client, StudioOptions options, IRunHost? host)
{
	public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(30);

	private readonly ConcurrentDictionary<string, StudioRun> runs = new();

	/// <summary>Raised when a run Studio started changes (new output, status); pages refresh on it.</summary>
	public event Action<StudioRun>? Changed;

	public bool Enabled => host is not null;

	public string? HostName => host?.Name;

	public StudioRun? Find(string runId) => runs.GetValueOrDefault(runId);

	/// <summary>Where a job posts its reports (the address a job can reach Studio at).</summary>
	public Func<string> ReportBase { get; set; } = () => "http://127.0.0.1";

	public static string NewRunId() => "run-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));

	/// <summary>The <c>bassia</c> arguments of a run: <c>agent start ...</c> or <c>agent replay ...</c>.</summary>
	public static List<string> Arguments(RunSpec spec, string runId)
	{
		var args = new List<string>();
		if (spec.ReplayOf is { } original)
		{
			args.AddRange(["agent", "replay", original, "-run-id", runId]);
		}
		else
		{
			args.AddRange(["agent", "start", "-run-id", runId]);
			void Add(string name, string? value)
			{
				if (!string.IsNullOrWhiteSpace(value))
				{
					args.AddRange(["-" + name, value.Trim()]);
				}
			}

			Add("workspace", spec.Workspace);
			Add("on", spec.On);
			Add("name", spec.Name);
			Add("model", spec.Model);
			if (spec.Context is { Count: > 0 } context)
			{
				args.AddRange(["-context", string.Join(',', context)]);
			}
		}

		if (spec.Land)
		{
			args.Add("-land");
		}

		if (spec.Leases is { Count: > 0 } leases)
		{
			args.AddRange(["-lease", string.Join(',', leases)]);
		}

		if (spec.ReplayOf is null)
		{
			// -run takes the rest of the line, so it goes last.
			args.AddRange(spec.Prompt is { } prompt ? ["-prompt", prompt] : ["-run", spec.Command!]);
		}

		return args;
	}

	/// <summary>Starts a run for <paramref name="user"/> on the run host.</summary>
	public async Task<StudioRun> StartAsync(RunSpec spec, StudioUser user, CancellationToken ct = default)
	{
		if (host is null)
		{
			throw new VersionException("Runs are not configured: start Studio with -run-host local or -run-host aca.");
		}

		if (!user.CanChange)
		{
			throw new StudioForbiddenException($"'{user.Name}' is a reader: starting a run needs the contributor role.");
		}

		var repo = await client.OpenAsync(ct);
		if (spec.ReplayOf is { } original)
		{
			var state = await repo.ResolveWorkspaceAsync(original, ct);
			if (!user.IsAdmin && state.Record.Owner != user.Name)
			{
				throw new StudioForbiddenException($"Workspace '{state.Name}' belongs to '{state.Record.Owner}'; only its owner or an admin may replay it.");
			}

			var working = await repo.Objects.ReadCommitAsync(state.Record.Working, ct);
			if (working.Recipe is not { } recipe || recipe.Prompt is null && recipe.Command is null)
			{
				throw new VersionException($"Workspace '{state.Name}' has no replay recipe (it was not made by a run); sync it and resolve by hand.");
			}

			spec = spec with { ReplayOf = state.Id };
		}
		else
		{
			if (string.IsNullOrWhiteSpace(spec.Prompt) == string.IsNullOrWhiteSpace(spec.Command))
			{
				throw new VersionException("Give the run exactly one of a prompt and a command.");
			}

			spec = spec with { Prompt = string.IsNullOrWhiteSpace(spec.Prompt) ? null : spec.Prompt, Command = string.IsNullOrWhiteSpace(spec.Command) ? null : spec.Command };
			if (spec.Workspace is { Length: > 0 } existing)
			{
				var state = await repo.ResolveWorkspaceAsync(existing, ct);
				if (!user.IsAdmin && state.Record.Owner != user.Name)
				{
					throw new StudioForbiddenException($"Workspace '{state.Name}' belongs to '{state.Record.Owner}'; only its owner or an admin may run in it.");
				}
			}
		}

		var runId = NewRunId();
		var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
		var run = new StudioRun(runId, spec, user.Name, token);
		runs[runId] = run;
		var environment = new Dictionary<string, string>
		{
			[JobVariables.Server] = options.JobServer ?? options.Server,
			[JobVariables.Args] = System.Text.Json.JsonSerializer.Serialize(Arguments(spec, runId)),
			[JobVariables.ReportUrl] = ReportBase().TrimEnd('/') + "/runs/report/" + runId,
			[JobVariables.ReportToken] = token,
			[JobVariables.User] = user.Name
		};
		if (host is LocalProcessRunHost && options.Token is { } serverToken)
		{
			// A cloud job reads the server token from the job's secret instead.
			environment[JobVariables.Token] = serverToken;
		}

		try
		{
			run.Execution = await host.LaunchAsync(new RunLaunch(runId, environment), ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Finish(run, StudioRunStatus.LaunchFailed, $"The run host could not start the job: {ex.Message}");
			throw new VersionException(run.Message!);
		}

		Changed?.Invoke(run);
		return run;
	}

	/// <summary>Takes a report from a job; the answer tells it whether to stop. False when the token is wrong.</summary>
	public bool Report(string runId, string? token, RunReport report, out bool stop)
	{
		stop = false;
		if (!runs.TryGetValue(runId, out var run) || token is null
			|| !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(run.Token)))
		{
			return false;
		}

		lock (run.Lines)
		{
			run.Lines.AddRange(report.Lines ?? []);
			if (run.Lines.Count > StudioRun.MaxLines)
			{
				run.Lines.RemoveRange(0, run.Lines.Count - StudioRun.MaxLines);
			}
		}

		run.WorkspaceId = report.Workspace ?? run.WorkspaceId;
		if (report.Done)
		{
			// A job's own failure report may follow the run's final one; keep the run's status then.
			if (run.Finished is null || run.Status is RunStatus.Running or StudioRunStatus.Starting)
			{
				Finish(run, report.Status ?? RunStatus.Failed, report.Message);
			}
			else if (report.Message is { } message)
			{
				run.Message ??= message;
			}
		}
		else if (run.Status == StudioRunStatus.Starting)
		{
			run.Status = RunStatus.Running;
		}

		stop = run.StopRequested;
		Changed?.Invoke(run);
		return true;
	}

	private void Finish(StudioRun run, string status, string? message)
	{
		run.Status = status;
		run.Message = message ?? run.Message;
		run.Finished = DateTimeOffset.UtcNow;
		Changed?.Invoke(run);
	}

	/// <summary>Asks a run to stop; kills its job if it has not stopped after the grace period.</summary>
	public async Task StopAsync(string runId, StudioUser user, CancellationToken ct = default)
	{
		var run = runs.GetValueOrDefault(runId) ?? throw new VersionException($"Run '{runId}' was not started by this Studio; stop it where it runs ('bassia agent stop {runId}').");
		if (!user.CanChange || !user.IsAdmin && run.User != user.Name)
		{
			throw new StudioForbiddenException($"Run '{runId}' belongs to '{run.User}'; only its owner or an admin may stop it.");
		}

		if (run.Finished is not null)
		{
			return;
		}

		run.StopRequested = true;
		Changed?.Invoke(run);
		_ = KillLaterAsync(run);
		await Task.CompletedTask;
	}

	private async Task KillLaterAsync(StudioRun run)
	{
		await Task.Delay(StopGrace);
		if (run.Finished is null && run.Execution is { } execution && host is not null)
		{
			try
			{
				await host.KillAsync(execution);
			}
			finally
			{
				Finish(run, RunStatus.Stopped, "The job did not stop on request and was killed; its last changes may not be snapshotted.");
			}
		}
	}

	/// <summary>Every run: those Studio started (newest first), then the runs recorded on workspaces.</summary>
	public async Task<IReadOnlyList<RunView>> ListAsync(CancellationToken ct = default)
	{
		var repo = await client.OpenAsync(ct);
		var workspaces = await repo.ListWorkspacesAsync(true, ct);
		var byRun = workspaces.Where(state => state.Record.Run is not null).ToDictionary(state => state.Record.Run!.RunId);
		var names = workspaces.ToDictionary(state => state.Id, state => state.Name);
		var views = new List<RunView>();
		foreach (var run in runs.Values)
		{
			var recorded = byRun.GetValueOrDefault(run.RunId);
			var workspace = run.WorkspaceId ?? recorded?.Id;
			views.Add(new RunView(run.RunId, run.Status, workspace, workspace is null ? null : names.GetValueOrDefault(workspace), run.User,
				TaskOf(run.Spec), run.Started, run.Finished, run, recorded?.Record.Run));
		}

		foreach (var (runId, state) in byRun)
		{
			if (!runs.ContainsKey(runId))
			{
				var record = state.Record.Run!;
				views.Add(new RunView(runId, record.Status, state.Id, state.Name, record.Agent, record.Prompt ?? record.Command, record.Started, record.Finished, null, record));
			}
		}

		return views.OrderByDescending(view => view.Started).ToList();
	}

	public static string? TaskOf(RunSpec spec) => spec.ReplayOf is { } original ? $"replay of {original}" : spec.Prompt ?? spec.Command;
}

/// <summary>The environment of a run's job.</summary>
public static class JobVariables
{
	public const string Server = "BASSIA_JOB_SERVER";
	public const string Token = "BASSIA_JOB_TOKEN";
	public const string Args = "BASSIA_JOB_ARGS";
	public const string ReportUrl = "BASSIA_REPORT_URL";
	public const string ReportToken = "BASSIA_REPORT_TOKEN";
	public const string User = "BASSIA_USER";
}

/// <summary>A job's report (what <c>bassia</c>'s run reporter posts).</summary>
public sealed record RunReport(string? RunId, string? Workspace, IReadOnlyList<string>? Lines, string? Status = null, bool Done = false, string? Message = null);
