namespace Bassia.Agents;

using System.Diagnostics;
using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

/// <summary>What an agentic run should do.</summary>
internal sealed record AgentRunOptions(
	string? Prompt,
	string? Command,
	string? Workspace,
	string? Name,
	string? On,
	string? Model,
	bool Land,
	bool Keep,
	IReadOnlyList<string> Leases,
	TimeSpan LeaseWait,
	string? RunId = null,
	string? ReplayOf = null,
	ChangeId? ChangeId = null,
	IReadOnlyList<string>? Load = null);

/// <summary>How a run ended.</summary>
internal sealed record AgentRunResult(string RunId, WorkspaceState Workspace, RunRecord Run, IReadOnlyList<PathChange> Changes, LandResult? Land, string LogFile);

/// <summary>
/// Runs a coding agent in a workspace: one workspace per run (created for it, or one given), the task recorded on the
/// working commit as its replay recipe, the agent started in the workspace's folder, the result snapshotted and the run
/// recorded on the workspace - and, when asked, landed. Nothing here merges text: a landing that conflicts asks for a
/// replay of the same recipe on the new mainline, which keeps the change's ChangeId.
/// </summary>
internal sealed class AgentRunner(Monorepo monorepo)
{
	public const string RunPrefix = "run-";

	public static string NewRunId() => RunPrefix + RecordName.NewKey();

	public string LogFile(string runId) => Path.Combine(monorepo.RunsDir, runId + ".log");

	public string StopFile(string runId) => Path.Combine(monorepo.RunsDir, runId + ".stop");

	/// <summary>The agent and the full shell line of a run.</summary>
	public (string Agent, string Line) CommandLine(AgentRunOptions options)
	{
		if (options.Command is { } command)
		{
			return (FirstWord(command), command);
		}

		var configured = monorepo.Config.Get("agent.command");
		var model = options.Model ?? monorepo.Config.GetOrNull("agent.model");
		var line = configured + (model is null ? "" : $" --model {Quote(model)}") + " " + Quote(options.Prompt ?? "");
		return (FirstWord(configured), line);
	}

	/// <summary>The workspace a run works in: the one named, or a new one on mainline (or stacked with <c>On</c>).</summary>
	public async Task<WorkspaceState> PrepareWorkspaceAsync(AgentRunOptions options, string runId, CancellationToken ct = default)
	{
		if (options.Workspace is { } reference)
		{
			var existing = await monorepo.Repo.ResolveWorkspaceAsync(reference, ct);
			if (!existing.IsOpen)
			{
				throw new MonorepoException($"Workspace '{existing.Name}' is {existing.Record.Status}; a run needs an open workspace.");
			}

			if (monorepo.Workspaces.Local(existing) is null)
			{
				await monorepo.Workspaces.CheckOutAsync(existing, existing.Record.Path is { } path && !Directory.Exists(path) ? path : monorepo.DefaultWorkspacePath(existing.Name),
					await monorepo.ResolveLoadAsync(options.Load, ct), ct);
			}

			return (await monorepo.Repo.FindWorkspaceAsync(existing.Id, ct))!;
		}

		var name = options.Name ?? Slug(options.Prompt ?? options.Command ?? "run") + "-" + runId[RunPrefix.Length..].Split('-')[^1];
		var (agent, line) = CommandLine(options);
		var recipe = new ReplayRecipe(options.Prompt, options.Command, agent, options.Model ?? monorepo.Config.GetOrNull("agent.model"));
		var (state, _) = await monorepo.Workspaces.CreateAsync(name, monorepo.DefaultWorkspacePath(name), options.On, options.Prompt ?? line, recipe,
			options.ChangeId, options.ReplayOf, await monorepo.ResolveLoadAsync(options.Load, ct), ct);
		return state;
	}

	/// <summary>Runs the agent in the foreground (or as the background half of a detached run) and records the result.</summary>
	public async Task<AgentRunResult> RunAsync(AgentRunOptions options, Action<string>? output, CancellationToken ct = default)
	{
		var runId = options.RunId ?? NewRunId();
		var workspace = await PrepareWorkspaceAsync(options, runId, ct);
		var local = monorepo.Workspaces.Local(workspace) ?? throw new MonorepoException($"Workspace '{workspace.Name}' has no folder on this machine.");
		var (agent, line) = CommandLine(options);
		Directory.CreateDirectory(monorepo.RunsDir);
		var log = LogFile(runId);
		File.Delete(StopFile(runId));

		await monorepo.Workspaces.Service.SetRecipeAsync(workspace.Id,
			new ReplayRecipe(options.Prompt, options.Command, agent, options.Model ?? monorepo.Config.GetOrNull("agent.model")), new Author(agent, runId), ct);
		if (options.Prompt is { } prompt)
		{
			await monorepo.Workspaces.Service.AmendWorkingAsync(workspace.Id, commit => commit.Intent.Length == 0 ? commit with { Intent = prompt } : commit, "describe", agent, ct);
		}

		var leases = await AcquireLeasesAsync(workspace, options, runId, ct);
		var run = new RunRecord(runId, line, options.Prompt, agent, options.Model, RunStatus.Running, DateTimeOffset.UtcNow, Host: Environment.MachineName,
			Pid: Environment.ProcessId);
		await monorepo.Workspaces.Service.RecordRunAsync(workspace.Id, run, $"run {runId} started", ct);

		using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using var renew = new CancellationTokenSource();
		var renewing = RenewLeasesAsync(leases, renew.Token);
		var watching = WatchStopFileAsync(runId, stop, renew.Token);
		var lastLine = "";
		int? exit;
		await using (var writer = new StreamWriter(log, append: true, Encoding.UTF8) { AutoFlush = true })
		{
			var environment = new Dictionary<string, string>
			{
				["BASSIA_RUN_ID"] = runId,
				["BASSIA_WORKSPACE"] = workspace.Id,
				["BASSIA_MONOREPO"] = monorepo.Root
			};
			exit = await ShellCommand.RunAsync(line, local.Root, environment, null, text =>
			{
				lock (writer)
				{
					writer.WriteLine(text);
					if (text.Trim().Length > 0)
					{
						lastLine = text.Trim();
					}
				}

				output?.Invoke(text);
			}, stop.Token);
		}

		await renew.CancelAsync();
		await renewing;
		await watching;

		// The result: snapshot what the agent changed, keep the log with the run.
		var current = (await monorepo.Repo.FindWorkspaceAsync(workspace.Id, CancellationToken.None))!;
		var snapshot = await monorepo.Workspaces.SnapshotAsync(current, runId, CancellationToken.None);
		var logLeaf = await monorepo.Repo.Objects.WriteContentAsync(await File.ReadAllBytesAsync(log, CancellationToken.None), false, CancellationToken.None);
		var status = exit is null ? RunStatus.Stopped : exit == 0 ? RunStatus.Succeeded : RunStatus.Failed;
		run = run with { Status = status, Finished = DateTimeOffset.UtcNow, ExitCode = exit, Log = logLeaf.Kind == EntryKind.File ? logLeaf.Id : null, LastLine = Trim(lastLine) };
		var recorded = await monorepo.Workspaces.Service.RecordRunAsync(workspace.Id, run, $"run {runId} {status}", CancellationToken.None);

		LandResult? land = null;
		if (options.Land && status == RunStatus.Succeeded)
		{
			land = await monorepo.Workspaces.LandAsync(recorded, new LandRequest(workspace.Id, Keep: options.Keep, Agent: agent,
				Leases: leases.Select(lease => new LeaseToken(lease.Id, lease.Token)).ToList()), CancellationToken.None);
			recorded = (await monorepo.Repo.FindWorkspaceAsync(workspace.Id, CancellationToken.None))!;
		}

		foreach (var lease in leases)
		{
			await monorepo.Repo.Leases.ReleaseAsync(lease.Id, lease.Token, CancellationToken.None);
		}

		File.Delete(StopFile(runId));
		return new AgentRunResult(runId, recorded, run, snapshot.Changes, land, log);
	}

	private async Task<List<Lease>> AcquireLeasesAsync(WorkspaceState workspace, AgentRunOptions options, string runId, CancellationToken ct)
	{
		var ttl = TimeSpan.FromSeconds(monorepo.Config.GetInt("lease.ttl"));
		var leases = new List<Lease>();
		foreach (var prefix in options.Leases)
		{
			var result = await monorepo.Repo.Leases.AcquireAsync(new LeaseRequest(prefix, LeaseMode.Exclusive, workspace.Id, ttl, options.LeaseWait, runId), ct);
			if (result.Lease is null)
			{
				foreach (var held in leases)
				{
					await monorepo.Repo.Leases.ReleaseAsync(held.Id, held.Token, ct);
				}

				throw new MonorepoException($"Could not lease '{prefix}': held by {string.Join(", ", result.Blocking.Select(lease => $"workspace {lease.Owner} ({lease.Prefix})"))}.");
			}

			leases.Add(result.Lease);
		}

		return leases;
	}

	private async Task RenewLeasesAsync(List<Lease> leases, CancellationToken ct)
	{
		if (leases.Count == 0)
		{
			return;
		}

		var ttl = TimeSpan.FromSeconds(monorepo.Config.GetInt("lease.ttl"));
		try
		{
			while (true)
			{
				await Task.Delay(ttl / 3, ct);
				for (var index = 0; index < leases.Count; index++)
				{
					if (await monorepo.Repo.Leases.RenewAsync(leases[index].Id, leases[index].Token, ttl, ct) is { } renewed)
					{
						leases[index] = renewed;
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task WatchStopFileAsync(string runId, CancellationTokenSource stop, CancellationToken ct)
	{
		try
		{
			while (!ct.IsCancellationRequested)
			{
				if (File.Exists(StopFile(runId)))
				{
					await stop.CancelAsync();
					return;
				}

				await Task.Delay(300, ct);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	/// <summary>Starts the run as a background bassia process; returns once it has recorded the run on its workspace.</summary>
	public async Task<(string RunId, WorkspaceState Workspace)> StartDetachedAsync(AgentRunOptions options, IReadOnlyList<string> arguments, CancellationToken ct = default)
	{
		var runId = NewRunId();
		var workspace = await PrepareWorkspaceAsync(options, runId, ct);
		Directory.CreateDirectory(monorepo.RunsDir);
		await File.WriteAllTextAsync(LogFile(runId), "", ct);
		// -run takes the rest of the line, so the run's own switches go first.
		var args = new List<string> { "-workspace", workspace.Id, "-run-id", runId }.Concat(arguments).ToList();
		using var process = DetachedProcess.Start(monorepo.Root, ["-C", monorepo.Root, "agent", "start", .. args]);
		var clock = Stopwatch.StartNew();
		while (clock.Elapsed < TimeSpan.FromSeconds(60))
		{
			var state = await monorepo.Repo.FindWorkspaceAsync(workspace.Id, ct);
			if (state?.Record.Run?.RunId == runId)
			{
				return (runId, state);
			}

			if (process.HasExited)
			{
				var error = await process.StandardError.ReadToEndAsync(ct) + await process.StandardOutput.ReadToEndAsync(ct);
				if (options.Workspace is null)
				{
					await monorepo.Workspaces.Service.CloseAsync(workspace.Id, true, monorepo.User, CancellationToken.None);
				}

				throw new MonorepoException($"The background run exited before it started: {error.Trim()}");
			}

			await Task.Delay(100, ct);
		}

		throw new MonorepoException("The background run did not start within a minute.");
	}

	/// <summary>Every workspace that has a run, newest run first.</summary>
	public async Task<IReadOnlyList<WorkspaceState>> ListAsync(CancellationToken ct = default) =>
		(await monorepo.Repo.ListWorkspacesAsync(true, ct)).Where(state => state.Record.Run is not null)
			.OrderByDescending(state => state.Record.Run!.Started).ToList();

	/// <summary>The workspace whose run has this id (full, or a unique prefix of its key of at least 4 characters), or the workspace named.</summary>
	public async Task<WorkspaceState> FindAsync(string reference, CancellationToken ct = default)
	{
		var all = await ListAsync(ct);
		var key = reference.StartsWith(RunPrefix, StringComparison.Ordinal) ? reference[RunPrefix.Length..] : reference;
		var matches = all.Where(state => state.Record.Run!.RunId == reference || (key.Length >= 4 && state.Record.Run!.RunId[RunPrefix.Length..].StartsWith(key, StringComparison.Ordinal))).ToList();
		if (matches.Count == 1)
		{
			return matches[0];
		}

		if (matches.Count > 1)
		{
			throw new MonorepoException($"'{reference}' matches several runs: {string.Join(", ", matches.Select(state => state.Record.Run!.RunId))}.");
		}

		var workspace = await monorepo.Repo.ResolveWorkspaceAsync(reference, ct);
		return workspace.Record.Run is null ? throw new MonorepoException($"Workspace '{workspace.Name}' has no run.") : workspace;
	}

	/// <summary>Whether a run recorded as running is still alive (its process on this machine); a run elsewhere is taken at its word.</summary>
	public static bool IsAlive(RunRecord run)
	{
		if (run.Status != RunStatus.Running)
		{
			return false;
		}

		if (run.Host != Environment.MachineName || run.Pid is not { } pid)
		{
			return true;
		}

		try
		{
			using var process = Process.GetProcessById(pid);
			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	/// <summary>The run's output: the local log while it exists, else the log stored with the run.</summary>
	public async Task<string> ReadLogAsync(RunRecord run, CancellationToken ct = default)
	{
		var file = LogFile(run.RunId);
		if (File.Exists(file))
		{
			await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream);
			return await reader.ReadToEndAsync(ct);
		}

		return run.Log is { } id && await monorepo.Repo.Store.ReadAsync(id, null, ct) is { } bytes ? Encoding.UTF8.GetString(bytes.Span) : "";
	}

	public static string Slug(string text)
	{
		var words = new string(text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray())
			.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4);
		var slug = string.Join('-', words);
		return slug.Length == 0 ? "run" : slug[..Math.Min(slug.Length, 40)];
	}

	private static string FirstWord(string command) => Path.GetFileNameWithoutExtension(command.Trim().Split(' ')[0].Trim('"'));

	private static string Trim(string line) => line.Length > 200 ? line[..200] : line;

	public static string Quote(string text) => OperatingSystem.IsWindows()
		? "\"" + text.Replace("\"", "\\\"") + "\""
		: "'" + text.Replace("'", "'\\''") + "'";
}

/// <summary>
/// Starts <c>bassia</c> again as a background process for <c>-detach</c>. None of the standard handles is inherited,
/// and on Unix the process gets its own session, so it survives the shell or tool call that started it.
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

		if (!OperatingSystem.IsWindows() && File.Exists("/usr/bin/setsid"))
		{
			startInfo.FileName = "/usr/bin/setsid";
			startInfo.ArgumentList.Add(executable);
		}
		else
		{
			startInfo.FileName = executable;
		}

		foreach (var argument in prefix.Concat(arguments))
		{
			startInfo.ArgumentList.Add(argument);
		}

		var process = Process.Start(startInfo) ?? throw new IOException("Could not start the background bassia process.");
		process.StandardInput.Close();
		return process;
	}

	/// <summary>
	/// How this bassia was started, to start it again: the host running it (<c>dotnet Bassia.dll</c>), else the apphost
	/// next to this assembly.
	/// </summary>
	private static (string Executable, IReadOnlyList<string> Prefix) Executable()
	{
		var assembly = typeof(DetachedProcess).Assembly.Location;
		if (Environment.ProcessPath is { } host && Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
		{
			return (host, [assembly]);
		}

		var apphost = Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "Bassia.exe" : "Bassia");
		return File.Exists(apphost) ? (apphost, []) : ("dotnet", [assembly]);
	}
}
