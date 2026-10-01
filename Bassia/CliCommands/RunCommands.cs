namespace Bassia;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Git;

/// <summary>
/// <c>bassia run ...</c>: starting agentic runs (in the foreground or detached), watching and stopping them,
/// finishing or discarding them, and reviewing what they changed.
/// </summary>
internal static class RunCommands
{
	public const string JobKind = "run";

	/// <summary>How long <c>run start -detach</c> waits for the background process to register before giving up.</summary>
	private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(30);

	// ----- bassia run start -----

	public static async Task<int> StartAsync(Invocation invocation)
	{
		var log = invocation.Get("log");
		JobOutput? output = null;
		try
		{
			// A detached run's own process: from here on everything it prints belongs in its log.
			output = log is null ? null : JobOutput.Redirect(log);
			var monorepo = AgentCommand.LoadMonorepo();
			var select = string.Join(",", invocation.List("select"));
			var command = ComposeCommand(invocation, monorepo);
			if (invocation.Has("detach") && log is null)
			{
				return await DetachAsync(invocation, monorepo, select, command);
			}

			var runId = invocation.Get("id") is { } id ? RunMetadata.NormalizeRunId(id) : RunMetadata.NewRunId();
			var jobs = new JobRegistry(monorepo);
			using var job = jobs.Attach(runId, JobKind, log, detached: log is not null);
			ConsoleCancelEventHandler stop = (_, args) =>
			{
				args.Cancel = true;
				job.Cancel();
			};

			// Ctrl-C in the terminal of a foreground run stops it the way 'run stop' does, so it is recorded.
			if (output is null)
			{
				Console.CancelKeyPress += stop;
			}

			try
			{
				var context = output is null
					? new AgentRunContext { Cancellation = job.Cancellation }
					: new AgentRunContext
					{
						Cancellation = job.Cancellation,
						OnStep = step => output.Line($"bassia: {step.Message}"),
						OnOutput = output.Line
					};

				AgentRunOutcome outcome;
				try
				{
					outcome = await AgentCommand.StartRunAsync(new GitClient(monorepo.Root), monorepo, select, command, context, runId);
				}
				catch (OperationCanceledException)
				{
					return Finish(output, false, invocation.Command,
						$"Agentic run '{runId}' was stopped before its agent started; nothing was recorded.",
						new Dictionary<string, object?> { ["run_id"] = runId, ["status"] = "cancelled" });
				}

				return Finish(output, outcome.Ok, invocation.Command, outcome.Message,
					AgentCommand.ResultData(outcome.Metadata, outcome.Store, outcome.MetadataTags));
			}
			finally
			{
				Console.CancelKeyPress -= stop;
			}
		}
		catch (Exception ex) when (output is not null && ProgramCli.IsReportable(ex))
		{
			// The detached process's own failure: into its log, where 'run start -detach' and 'run logs' look.
			return output.WriteResult(false, invocation.Command, ex.Message);
		}
		finally
		{
			output?.Dispose();
		}
	}

	/// <summary>The result: printed, or for a detached run written to its result file.</summary>
	internal static int Finish(JobOutput? output, bool ok, string command, string message, IReadOnlyDictionary<string, object?> data) =>
		output is null
			? ProgramCli.WriteResult(ok, command, message, data)
			: output.WriteResult(ok, command, message, data);

	/// <summary>The agent command: <c>-run</c> as given, or composed from <c>-prompt</c> like the frontends compose it.</summary>
	private static string ComposeCommand(Invocation invocation, Monorepo monorepo)
	{
		var run = invocation.Get("run");
		var prompt = invocation.Get("prompt");
		if (run is not null && prompt is not null)
		{
			throw invocation.Usage("Give either -run or -prompt, not both.");
		}

		if (run is not null)
		{
			foreach (var name in new[] { "agent", "model", "effort", "context" }.Where(invocation.Has))
			{
				throw invocation.Usage($"-{name} composes the command from -prompt; it cannot be combined with -run.");
			}

			return run;
		}

		if (prompt is null)
		{
			throw invocation.Usage("Either -run <command> or -prompt <text> is required.");
		}

		return ComposeCommand(invocation.Get("agent") ?? monorepo.AgentCommand, invocation.Get("model"),
			invocation.Get("effort"), prompt, invocation.Get("context"));
	}

	/// <summary>
	/// The <c>-run</c> value: the agent command, optional model/effort flags, and the prompt as one quoted argument.
	/// The value is handed to the platform shell as a single line, so the context is joined with a space, not a newline.
	/// </summary>
	internal static string ComposeCommand(string agentCommand, string? model, string? effort, string prompt, string? context)
	{
		var fullPrompt = string.IsNullOrWhiteSpace(context) ? prompt.Trim() : $"{prompt.Trim()} Context: {context.Trim()}";
		var flags = (model is null ? "" : $" --model {model}") + (effort is null ? "" : $" --effort {effort}");
		return $"{agentCommand.Trim()}{flags} \"{fullPrompt.Replace("\"", "\\\"")}\"";
	}

	/// <summary>
	/// <c>-detach</c>: checks the selection here, so a mistake is reported straight away, then hands the run to a
	/// background bassia process under a pre-generated id and returns as soon as that process has registered.
	/// </summary>
	private static async Task<int> DetachAsync(Invocation invocation, Monorepo monorepo, string select, string command)
	{
		await AgentCommand.ResolveSelectionAsync(monorepo, select);
		var runId = RunMetadata.NewRunId();
		var jobs = new JobRegistry(monorepo);
		Directory.CreateDirectory(jobs.Dir);
		var log = jobs.LogPath(runId);
		await File.WriteAllTextAsync(log, "");

		using var process = DetachedProcess.Start(monorepo.Root, ["run", "start", "-id", runId, "-log", log, "-select", select, "-run", command]);
		var job = await WaitForRegistrationAsync(jobs, runId, process);
		if (job is null)
		{
			return ProgramCli.WriteResult(false, invocation.Command,
				$"The background process for run '{runId}' did not start. {Tail(log, 20)}".Trim(),
				new Dictionary<string, object?> { ["run_id"] = runId, ["log"] = log });
		}

		var shortId = RunMetadata.Key(runId);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Agentic run '{runId}' started in the background. Follow it with 'bassia run show {shortId}', 'bassia run logs {shortId}' " +
			$"or 'bassia run wait {shortId}'; stop it with 'bassia run stop {shortId}'.",
			new Dictionary<string, object?>
			{
				["run_id"] = runId,
				["short_id"] = shortId,
				["status"] = "started",
				["detached"] = true,
				["pid"] = job.Pid,
				["log"] = log,
				["result_file"] = jobs.ResultPath(runId),
				["select"] = select,
				["agent_command"] = command
			});
	}

	/// <summary>Waits until the background process has registered its job, or has exited without doing so.</summary>
	internal static async Task<JobInfo?> WaitForRegistrationAsync(JobRegistry jobs, string id, Process process)
	{
		var clock = Stopwatch.StartNew();
		while (clock.Elapsed < RegistrationTimeout)
		{
			if (jobs.Find(id) is { } job)
			{
				return job;
			}

			if (process.HasExited)
			{
				return jobs.Find(id);
			}

			await Task.Delay(50);
		}

		return null;
	}

	/// <summary>
	/// The start path of the web dashboard: the run is registered as a hosted job of the <c>web</c> process,
	/// so <c>bassia run show</c> sees it live and <c>bassia run stop</c> can stop it from another shell.
	/// </summary>
	internal static async Task<AgentRunOutcome> StartHostedAsync(GitClient git, Monorepo monorepo, string select, string command, AgentRunContext context)
	{
		var runId = RunMetadata.NewRunId();
		using var job = new JobRegistry(monorepo).Attach(runId, JobKind, hosted: true);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation, job.Cancellation);
		return await AgentCommand.StartRunAsync(git, monorepo, select, command, new AgentRunContext
		{
			Cancellation = linked.Token,
			OnStep = context.OnStep,
			OnOutput = context.OnOutput
		}, runId);
	}

	// ----- finding a run -----

	private sealed record FoundRun(Monorepo Monorepo, RunMetadataStore Store, JobRegistry Jobs, string RunId, RunMetadata? Record, JobInfo? Job)
	{
		public bool Alive => JobRegistry.IsAlive(Job);

		/// <summary>The process is live and the record (if any yet) has not reached a final status.</summary>
		public bool Live => Alive && (Record is null || Record.Status == "started");

		/// <summary>Recorded as started, but nothing is executing it any more (killed, crashed, machine restarted).</summary>
		public bool Stale => !Alive && Record?.Status == "started";

		public string Status => Record?.Status ?? (Alive ? "preparing" : "unknown");
	}

	/// <summary>
	/// The run <c>-id</c> names: its full id, its <c>&lt;key&gt;</c> part, or a prefix of at least four characters of the key -
	/// among recorded runs and runs still preparing.
	/// </summary>
	private static async Task<FoundRun> FindAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var store = new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir);
		var jobs = new JobRegistry(monorepo);
		var name = invocation.Require("id");
		var candidates = (await store.ListKeysAsync(RunMetadata.RefPrefix)).Select(RunMetadata.NormalizeRunId)
			.Concat(jobs.List(JobKind).Select(job => job.Id))
			.Distinct(StringComparer.Ordinal)
			.Where(runId => IntegrationSupport.MatchesRun(runId, name))
			.ToList();

		var runId = candidates.Count switch
		{
			1 => candidates[0],
			0 => throw new AgentException($"Unknown agentic run '{name}'. List the runs with 'bassia run list'."),
			_ => throw new AgentException($"'{name}' names more than one agentic run ({string.Join(", ", candidates.Select(RunMetadata.Key))}); give more of its id.")
		};

		return new FoundRun(monorepo, store, jobs, runId, await store.LoadLatestAsync(runId), jobs.Find(runId));
	}

	private static async Task<FoundRun> ReloadAsync(FoundRun run) =>
		run with { Record = await run.Store.LoadLatestAsync(run.RunId), Job = run.Jobs.Find(run.RunId) };

	// ----- bassia run list -----

	public static async Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var store = new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir);
		var jobs = new JobRegistry(monorepo);
		var status = invocation.Get("status")?.ToLowerInvariant();
		var component = invocation.Get("component");
		var limit = invocation.Int("limit", 20, 1);

		var recorded = await store.ListLatestAsync();
		var preparing = jobs.List(JobKind).Where(job => JobRegistry.IsAlive(job) && recorded.All(run => run.RunId != job.Id));
		var rows = preparing.Select(job => new FoundRun(monorepo, store, jobs, job.Id, null, job))
			.Concat(recorded.Select(run => new FoundRun(monorepo, store, jobs, run.RunId, run, jobs.Find(run.RunId))))
			.Where(run => status is null || run.Status == status || (status == "live" && run.Live) || (status == "stale" && run.Stale))
			.Where(run => component is null || run.Record?.Components.Any(entry => entry.Name == component) == true)
			.ToList();
		var shown = rows.Take(limit).ToList();

		return ProgramCli.WriteResult(true, invocation.Command,
			$"{shown.Count} of {rows.Count} run(s){(rows.Count > shown.Count ? $"; more with -limit {rows.Count}" : "")}.",
			new Dictionary<string, object?>
			{
				["total"] = rows.Count,
				["live"] = rows.Count(run => run.Live),
				["table"] = new TomlText(AsciiTable.Render(["ID", "STATUS", "LIVE", "CREATED", "TIME", "COMPONENTS", "SUMMARY"],
					shown.Select(run => (IReadOnlyList<string>)
					[
						RunMetadata.Key(run.RunId), run.Status + (run.Stale ? "!" : ""), run.Live ? "yes" : "",
						Time(run.Record?.Created ?? run.Job?.Started), Elapsed(run.Record?.Created ?? run.Job?.Started, run.Record?.Finished),
						string.Join(",", run.Record?.Components.Select(entry => entry.Name) ?? []),
						run.Record is null ? "" : AgentCommand.SummarizeCommand(run.Record.Command)
					]))),
				["run"] = shown.Select(run => (IReadOnlyDictionary<string, object?>)Summary(run)).ToList()
			});
	}

	private static Dictionary<string, object?> Summary(FoundRun run) => new()
	{
		["run_id"] = run.RunId,
		["short_id"] = RunMetadata.Key(run.RunId),
		["status"] = run.Status,
		["live"] = run.Live,
		["stale"] = run.Stale,
		["created"] = run.Record?.Created ?? run.Job?.Started,
		["finished"] = run.Record?.Finished,
		["components"] = run.Record?.Components.Select(entry => entry.Name).ToList() ?? [],
		["select"] = run.Record?.Select,
		["summary"] = run.Record is null ? null : AgentCommand.SummarizeCommand(run.Record.Command),
		["result_tags"] = run.Record?.Components.Where(entry => entry.ResultTag is not null && entry.ResultStatus == ResultStatus.Pushed)
			.Select(entry => $"{entry.Name}@{entry.ResultTag}").ToList() ?? []
	};

	// ----- bassia run show -----

	public static async Task<int> ShowAsync(Invocation invocation)
	{
		var run = await FindAsync(invocation);
		return ProgramCli.WriteResult(true, invocation.Command, Describe(run), Details(run));
	}

	private static string Describe(FoundRun run) =>
		run.Stale ? $"Agentic run '{run.RunId}' is recorded as started, but no process is executing it any more; 'bassia run stop {RunMetadata.Key(run.RunId)}' records it as cancelled."
		: run.Live ? $"Agentic run '{run.RunId}' is live ({run.Status})."
		: run.Record is null ? $"Agentic run '{run.RunId}' never got past preparing; it has no record."
		: $"Agentic run '{run.RunId}' is {run.Status}.";

	private static Dictionary<string, object?> Details(FoundRun run)
	{
		var data = run.Record is null
			? new Dictionary<string, object?> { ["run_id"] = run.RunId, ["status"] = run.Status }
			: AgentCommand.ResultData(run.Record, run.Store, []);
		data.Remove("metadata_tags");
		data["short_id"] = RunMetadata.Key(run.RunId);
		data["status"] = run.Status;
		data["live"] = run.Live;
		data["stale"] = run.Stale;
		data["pid"] = run.Alive ? run.Job?.Pid : null;
		data["hosted"] = run.Job?.Hosted;
		data["log"] = run.Job?.Log;
		data["result_file"] = run.Job?.Log is null ? null : run.Jobs.ResultPath(run.RunId);
		data["created"] = run.Record?.Created ?? run.Job?.Started;
		data["finished"] = run.Record?.Finished;
		data["select"] = run.Record?.Select;
		data["agent_command"] = run.Record?.Command;
		data["summary"] = run.Record is null ? null : AgentCommand.SummarizeCommand(run.Record.Command);
		data["last_output"] = run.Live && run.Job?.Log is { } log ? LastLine(log) : null;
		data["card"] = new TomlText(Card(run));
		return data;
	}

	/// <summary>The run as a card like the run board draws it, with a line per component result.</summary>
	private static string Card(FoundRun run)
	{
		const int width = 64;
		var lines = new List<string>
		{
			$"{(run.Stale ? "STALE" : run.Live ? "RUNNING" : run.Status.ToUpperInvariant())}".PadRight(width - 12) + Elapsed(run.Record?.Created ?? run.Job?.Started, run.Record?.Finished).PadLeft(8),
			run.Record?.Select ?? "(preparing)"
		};
		if (run.Record is not null)
		{
			lines.Add(AgentCommand.SummarizeCommand(run.Record.Command));
		}

		if (run.Live && run.Job?.Log is { } log && LastLine(log) is { } last)
		{
			lines.Add("> " + last);
		}

		var results = run.Record?.Components.Select(entry =>
			$"{entry.Name,-12} {entry.ResultStatus.ToString().ToLowerInvariant(),-10} {entry.ResultTag ?? entry.CommitIsh}").ToList() ?? [];

		var builder = new StringBuilder();
		var title = $"+-- {RunMetadata.Key(run.RunId)} ";
		builder.Append(title).Append(new string('-', width - title.Length - 1)).Append("+\n");
		foreach (var line in lines)
		{
			builder.Append("| ").Append(Fit(line, width - 4).PadRight(width - 4)).Append(" |\n");
		}

		if (results.Count > 0)
		{
			builder.Append('+').Append(new string('-', width - 2)).Append("+\n");
			foreach (var line in results)
			{
				builder.Append("| ").Append(Fit(line, width - 4).PadRight(width - 4)).Append(" |\n");
			}
		}

		builder.Append('+').Append(new string('-', width - 2)).Append('+');
		return builder.ToString();
	}

	// ----- bassia run logs -----

	public static async Task<int> LogsAsync(Invocation invocation)
	{
		var run = await FindAsync(invocation);
		var tail = invocation.Int("tail", 50, 0);
		if (run.Job?.Log is not { } log || !File.Exists(log))
		{
			throw new AgentException(
				$"Agentic run '{run.RunId}' was not started with -detach, so its output went to the terminal or frontend that started it; " +
				$"'bassia run show {RunMetadata.Key(run.RunId)}' shows its record.");
		}

		var lines = ReadLines(log);
		var shown = tail == 0 ? lines : lines.TakeLast(tail).ToList();
		return ProgramCli.WriteResult(true, invocation.Command,
			$"{shown.Count} of {lines.Count} line(s) of the output of run '{run.RunId}' ({(run.Live ? "live" : run.Status)}).",
			new Dictionary<string, object?>
			{
				["run_id"] = run.RunId,
				["status"] = run.Status,
				["live"] = run.Live,
				["log"] = log,
				["lines"] = lines.Count,
				["output"] = new TomlText(shown.Count == 0 ? "(no output yet)" : string.Join("\n", shown))
			});
	}

	// ----- bassia run wait -----

	public static async Task<int> WaitAsync(Invocation invocation)
	{
		var run = await FindAsync(invocation);
		var timeout = invocation.Int("timeout", 0, 0);
		var clock = Stopwatch.StartNew();
		while (run.Live)
		{
			if (timeout > 0 && clock.Elapsed.TotalSeconds >= timeout)
			{
				var data = Details(run);
				data["timed_out"] = true;
				return ProgramCli.WriteResult(false, invocation.Command,
					$"Agentic run '{run.RunId}' is still running after {timeout}s; wait again, or stop it with 'bassia run stop {RunMetadata.Key(run.RunId)}'.", data);
			}

			await Task.Delay(500);
			run = await ReloadAsync(run);
		}

		// A finished process writes its last record before it exits; read once more so it is never missed.
		run = await ReloadAsync(run);
		var ok = run.Record?.Status == "completed";
		return ProgramCli.WriteResult(ok, invocation.Command, Describe(run), Details(run));
	}

	// ----- bassia run stop -----

	public static async Task<int> StopAsync(Invocation invocation)
	{
		var run = await FindAsync(invocation);
		var timeout = invocation.Int("timeout", 30, 1);
		if (!run.Live)
		{
			if (!run.Stale)
			{
				throw new AgentException($"Agentic run '{run.RunId}' is not live (status '{run.Status}'); there is nothing to stop.");
			}

			await AgentCommand.RecordCancelledAsync(run.Store, run.Record!);
			run = await ReloadAsync(run);
			return ProgramCli.WriteResult(true, invocation.Command,
				$"Agentic run '{run.RunId}' was no longer running (its process is gone); it is now recorded as cancelled.", Details(run));
		}

		run.Jobs.RequestStop(run.RunId);
		var clock = Stopwatch.StartNew();
		while (run.Live && clock.Elapsed.TotalSeconds < timeout)
		{
			await Task.Delay(200);
			run = await ReloadAsync(run);
		}

		var killed = false;
		if (run.Live && run.Job is { Hosted: false } job)
		{
			JobRegistry.Kill(job);
			killed = true;
			run = await ReloadAsync(run);
		}

		if (run.Live)
		{
			return ProgramCli.WriteResult(false, invocation.Command,
				$"Agentic run '{run.RunId}' did not stop within {timeout}s; it is running inside a frontend, which finishes the commit/push step before it stops.",
				Details(run));
		}

		if (run.Record?.Status == "started")
		{
			await AgentCommand.RecordCancelledAsync(run.Store, run.Record);
			run = await ReloadAsync(run);
		}

		run.Jobs.ClearStop(run.RunId);
		var data = Details(run);
		data["killed"] = killed;
		return ProgramCli.WriteResult(true, invocation.Command,
			run.Record is null
				? $"Agentic run '{run.RunId}' was stopped before its agent started; nothing was recorded."
				: $"Agentic run '{run.RunId}' stopped; it is recorded as {run.Record.Status} and its run folder '{run.Record.WorkspacePath}' is kept for inspection.",
			data);
	}

	// ----- bassia run retry / abandon -----

	public static async Task<int> RetryAsync(Invocation invocation)
	{
		var run = await FindAsync(invocation);
		var record = run.Record ?? throw new AgentException($"Agentic run '{run.RunId}' has no record yet; nothing to retry.");
		if (run.Live)
		{
			throw new AgentException($"Agentic run '{run.RunId}' is still running.");
		}

		var outcome = await AgentCommand.RetryAsync(run.Monorepo, run.Store, record);
		return ProgramCli.WriteResult(outcome.Ok, invocation.Command, outcome.Message, AgentCommand.ResultData(outcome.Metadata, outcome.Store, outcome.MetadataTags));
	}

	public static async Task<int> AbandonAsync(Invocation invocation)
	{
		var run = await FindAsync(invocation);
		var record = run.Record ?? throw new AgentException($"Agentic run '{run.RunId}' has no record yet; nothing to abandon.");
		if (run.Live)
		{
			throw new AgentException($"Agentic run '{run.RunId}' is still running; stop it first with 'bassia run stop {RunMetadata.Key(run.RunId)}'.");
		}

		var outcome = await AgentCommand.AbandonAsync(run.Store, record);
		return ProgramCli.WriteResult(outcome.Ok, invocation.Command, outcome.Message, AgentCommand.ResultData(outcome.Metadata, outcome.Store, outcome.MetadataTags));
	}

	// ----- bassia run diff -----

	public static async Task<int> DiffAsync(Invocation invocation)
	{
		var run = await FindAsync(invocation);
		var record = run.Record ?? throw new AgentException($"Agentic run '{run.RunId}' has no record yet; nothing to compare.");
		var only = invocation.Get("component");
		if (only is not null && record.Components.All(entry => entry.Name != only))
		{
			throw new AgentException($"Agentic run '{run.RunId}' does not include component '{only}'.");
		}

		var components = new List<IReadOnlyDictionary<string, object?>>();
		var totalFiles = 0;
		foreach (var entry in record.Components.Where(entry => only is null || entry.Name == only))
		{
			var data = new Dictionary<string, object?>
			{
				["name"] = entry.Name,
				["base"] = entry.CommitIsh,
				["base_commit"] = entry.Commit,
				["result_status"] = entry.ResultStatus.ToString().ToLowerInvariant(),
				["result_commit"] = entry.ResultCommit,
				["result_tag"] = entry.ResultTag
			};

			// A pushed result is in the source of truth; a committed one only in the run's checkout.
			var repo = entry.ResultStatus == ResultStatus.Pushed ? run.Monorepo.SourceRepoDir(entry.Name) : entry.Path;
			if (entry.ResultCommit is null || !Directory.Exists(repo))
			{
				data["files_changed"] = 0;
				data["note"] = entry.ResultCommit is null ? "no result commit" : $"'{repo}' no longer exists";
				components.Add(data);
				continue;
			}

			var git = GitClient.In(repo);
			var numstat = await git.RunOrThrowAsync(["diff", "--numstat", entry.Commit, entry.ResultCommit]);
			var files = numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t')).Where(fields => fields.Length >= 3).ToList();
			data["files_changed"] = files.Count;
			data["insertions"] = files.Sum(fields => int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var added) ? added : 0);
			data["deletions"] = files.Sum(fields => int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var removed) ? removed : 0);
			data["files"] = files.Select(fields => fields[2]).ToList();
			data["stat"] = new TomlText(await git.RunOrThrowAsync(["diff", "--stat", entry.Commit, entry.ResultCommit]));
			if (invocation.Has("patch"))
			{
				data["patch"] = new TomlText(await git.RunOrThrowAsync(["diff", entry.Commit, entry.ResultCommit]));
			}

			totalFiles += files.Count;
			components.Add(data);
		}

		return ProgramCli.WriteResult(true, invocation.Command,
			$"Agentic run '{run.RunId}' changed {totalFiles} file(s) in {components.Count(component => component["files_changed"] is int count && count > 0)} component(s).",
			new Dictionary<string, object?>
			{
				["run_id"] = run.RunId,
				["status"] = run.Status,
				["component"] = components
			});
	}

	// ----- helpers -----

	/// <summary>A log's lines, with any <c># bassia result</c> marker neutralized so the log never looks like a result.</summary>
	internal static List<string> ReadLines(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream);
		var text = reader.ReadToEnd().Replace("\r", "").TrimEnd('\n');
		return text.Length == 0
			? []
			: text.Split('\n').Select(line => line.StartsWith(TomlResult.Marker, StringComparison.Ordinal) ? "#" + line : line).ToList();
	}

	internal static string? LastLine(string path) =>
		File.Exists(path) ? ReadLines(path).LastOrDefault(line => line.Trim().Length > 0) is { } line ? AsciiTable.Ascii(line) : null : null;

	internal static string Tail(string path, int count) =>
		File.Exists(path) ? string.Join(" | ", ReadLines(path).Where(line => line.Trim().Length > 0).TakeLast(count)) : "";

	private static string Fit(string text, int width)
	{
		text = AsciiTable.Ascii(text);
		return text.Length <= width ? text : text[..(width - 3)] + "...";
	}

	internal static string Time(string? timestamp) =>
		DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
			? time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
			: "";

	internal static string Elapsed(string? started, string? finished)
	{
		if (!DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var from))
		{
			return "";
		}

		var to = DateTimeOffset.TryParse(finished, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end) ? end : DateTimeOffset.UtcNow;
		return AsciiTable.Elapsed(to - from);
	}
}
