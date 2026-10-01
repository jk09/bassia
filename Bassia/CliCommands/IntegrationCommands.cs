namespace Bassia;

using System.Diagnostics;
using System.Text;
using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Integration;

/// <summary>
/// <c>bassia integration ...</c>: merging the results of agentic runs - the triage, the integration itself (in the
/// foreground or detached), watching and stopping it, and advancing the base branches to its result.
/// </summary>
internal static class IntegrationCommands
{
	public const string JobKind = "integration";

	private sealed record Choices(IReadOnlyList<RunMetadata> Runs, IReadOnlyList<ComponentIntegration> Plan, RunMetadataStore Store, StructuralMerge? Structural, string StructuralStatus);

	/// <summary>The runs <c>-runs</c> names and the triage of them under <c>-onto</c>, <c>-semantic</c>, <c>-skip</c> and <c>-weave</c>.</summary>
	private static async Task<Choices> PlanAsync(Invocation invocation, Monorepo monorepo)
	{
		var store = new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir);
		var runs = await IntegrationSupport.SelectRunsAsync(store, string.Join(",", invocation.List("runs")));

		var onto = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var selection in ComponentSelection.ParseList("-onto", string.Join(",", invocation.List("onto"))))
		{
			onto[selection.Component] = selection.CommitIsh!;
		}

		var strategies = new Dictionary<(string, string), MergeStrategy>();
		foreach (var runId in invocation.List("semantic"))
		{
			strategies[("*", IntegrationSupport.RequireSelected(runs, runId))] = MergeStrategy.Semantic;
		}

		foreach (var runId in invocation.List("skip"))
		{
			strategies[("*", IntegrationSupport.RequireSelected(runs, runId))] = MergeStrategy.Skip;
		}

		var (structural, structuralStatus) = StructuralMerge.Resolve(monorepo, invocation.Get("weave"));
		var plan = await IntegrationPlanner.PlanAsync(monorepo, runs, new IntegrationChoices { Onto = onto, Strategies = strategies, Structural = structural });
		return new Choices(runs, plan, store, structural, structuralStatus);
	}

	// ----- bassia integration plan -----

	public static async Task<int> PlanCommandAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var (runs, plan, _, structural, structuralStatus) = await PlanAsync(invocation, monorepo);
		var steps = plan.SelectMany(component => component.Steps).ToList();
		var weave = structural is null ? $" Structural merge: {structuralStatus}." : "";
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Triage of {runs.Count} run(s): {steps.Count(step => step.Strategy == MergeStrategy.Syntactic)} step(s) for git, " +
			$"{steps.Count(step => step.Strategy == MergeStrategy.Structural)} for the structural merge, " +
			$"{steps.Count(step => step.Strategy == MergeStrategy.Semantic)} for the resolver, {steps.Count(step => step.Strategy == MergeStrategy.Skip)} skipped.{weave} Nothing was changed.",
			new Dictionary<string, object?>
			{
				["plan"] = true,
				["structural_merge"] = structuralStatus,
				["runs"] = runs.OrderBy(run => run.Created, StringComparer.Ordinal).Select(run => run.RunId).ToList(),
				["steps"] = new TomlText(Steps(plan)),
				["component"] = plan.Select(IntegrationSupport.ComponentData).ToList()
			});
	}

	// ----- bassia integration start -----

	public static async Task<int> StartAsync(Invocation invocation)
	{
		var log = invocation.Get("log");
		JobOutput? output = null;
		try
		{
			output = log is null ? null : JobOutput.Redirect(log);
			var monorepo = AgentCommand.LoadMonorepo();
			var (runs, plan, store, structural, _) = await PlanAsync(invocation, monorepo);
			if (plan.Count == 0)
			{
				throw new IntegrationException("The plan is empty; there is nothing to integrate.");
			}

			var resolver = invocation.Get("resolve") ?? monorepo.Resolver;
			if (invocation.Has("detach") && log is null)
			{
				return await DetachAsync(invocation, monorepo, runs, resolver, structural);
			}

			var id = invocation.Get("id") is { } given ? IntegrationRecord.NormalizeId(given) : IntegrationRecord.NewId();
			var jobs = new JobRegistry(monorepo);
			using var job = jobs.Attach(id, JobKind, log, detached: log is not null);
			ConsoleCancelEventHandler stop = (_, args) =>
			{
				args.Cancel = true;
				job.Cancel();
			};

			if (output is null)
			{
				Console.CancelKeyPress += stop;
			}

			try
			{
				var context = output is null
					? new IntegrationContext { Cancellation = job.Cancellation }
					: new IntegrationContext
					{
						Cancellation = job.Cancellation,
						OnProgress = progress => output.Line($"bassia: {progress.Message}"),
						OnOutput = output.Line
					};

				IntegrationOutcome outcome;
				try
				{
					outcome = await IntegrationRunner.RunAsync(new GitClient(monorepo.Root), monorepo, runs, plan, resolver, context, id, structural);
				}
				catch (OperationCanceledException)
				{
					var message = $"Integration '{id}' was stopped before it started; nothing was recorded.";
					var data = new Dictionary<string, object?> { ["integration_id"] = id, ["status"] = "cancelled" };
					return RunCommands.Finish(output, false, invocation.Command, message, data);
				}

				var result = IntegrationSupport.RecordData(outcome, store.RepoDir);
				result["steps"] = new TomlText(Steps(outcome.Record.Components));
				return RunCommands.Finish(output, outcome.Ok, invocation.Command, outcome.Message, result);
			}
			finally
			{
				Console.CancelKeyPress -= stop;
			}
		}
		catch (Exception ex) when (output is not null && ProgramCli.IsReportable(ex))
		{
			return output.WriteResult(false, invocation.Command, ex.Message);
		}
		finally
		{
			output?.Dispose();
		}
	}

	private static async Task<int> DetachAsync(Invocation invocation, Monorepo monorepo, IReadOnlyList<RunMetadata> runs, string resolver, StructuralMerge? structural)
	{
		var id = IntegrationRecord.NewId();
		var jobs = new JobRegistry(monorepo);
		Directory.CreateDirectory(jobs.Dir);
		var log = jobs.LogPath(id);
		await File.WriteAllTextAsync(log, "");

		// The background process plans again from the same choices; the runs are passed by their full ids.
		var arguments = new List<string> { "integration", "start", "-id", id, "-log", log, "-runs", string.Join(",", runs.Select(run => run.RunId)) };
		foreach (var name in new[] { "onto", "semantic", "skip" }.Where(invocation.Has))
		{
			arguments.AddRange([$"-{name}", string.Join(",", invocation.List(name))]);
		}

		// The structural merge as this process found it, so the background process cannot come to another conclusion.
		arguments.AddRange(["-weave", structural?.Command ?? StructuralMerge.Off]);
		arguments.AddRange(["-resolve", resolver]);
		using var process = DetachedProcess.Start(monorepo.Root, arguments);
		var job = await RunCommands.WaitForRegistrationAsync(jobs, id, process);
		if (job is null)
		{
			return ProgramCli.WriteResult(false, invocation.Command,
				$"The background process for integration '{id}' did not start. {RunCommands.Tail(log, 20)}".Trim(),
				new Dictionary<string, object?> { ["integration_id"] = id, ["log"] = log });
		}

		var shortId = IntegrationRecord.Key(id);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Integration '{id}' started in the background. Follow it with 'bassia integration show {shortId}', 'bassia integration logs {shortId}' " +
			$"or 'bassia integration wait {shortId}'; stop it with 'bassia integration stop {shortId}'.",
			new Dictionary<string, object?>
			{
				["integration_id"] = id,
				["short_id"] = shortId,
				["status"] = "started",
				["detached"] = true,
				["pid"] = job.Pid,
				["log"] = log,
				["result_file"] = jobs.ResultPath(id),
				["runs"] = runs.Select(run => run.RunId).ToList(),
				["resolver"] = resolver,
				["structural_driver"] = structural?.Command
			});
	}

	// ----- finding an integration -----

	private sealed record Found(Monorepo Monorepo, IntegrationStore Store, JobRegistry Jobs, string Id, IntegrationRecord? Record, JobInfo? Job)
	{
		public bool Alive => JobRegistry.IsAlive(Job);
		public bool Live => Alive && (Record is null || Record.Status == "started");
		public bool Stale => !Alive && Record?.Status == "started";
		public string Status => Record?.Status ?? (Alive ? "preparing" : "unknown");
	}

	private static async Task<Found> FindAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var runs = new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir);
		var store = new IntegrationStore(runs);
		var jobs = new JobRegistry(monorepo);
		var name = IntegrationRecord.NormalizeId(invocation.Require("id"));
		var key = IntegrationRecord.Key(name);
		var candidates = (await runs.ListKeysAsync(IntegrationRecord.RefPrefix)).Select(IntegrationRecord.NormalizeId)
			.Concat(jobs.List(JobKind).Select(job => job.Id))
			.Distinct(StringComparer.Ordinal)
			.Where(id => key.Length >= 4 && IntegrationRecord.Key(id).StartsWith(key, StringComparison.OrdinalIgnoreCase))
			.ToList();

		var id = candidates.Count switch
		{
			1 => candidates[0],
			0 => throw new IntegrationException($"Unknown integration '{invocation.Get("id")}'. List them with 'bassia integration list'."),
			_ => throw new IntegrationException($"'{invocation.Get("id")}' names more than one integration; give more of its id.")
		};

		return new Found(monorepo, store, jobs, id, await store.LoadLatestAsync(id), jobs.Find(id));
	}

	private static async Task<Found> ReloadAsync(Found found) =>
		found with { Record = await found.Store.LoadLatestAsync(found.Id), Job = found.Jobs.Find(found.Id) };

	// ----- bassia integration list -----

	public static async Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var store = new IntegrationStore(new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir));
		var jobs = new JobRegistry(monorepo);
		var limit = invocation.Int("limit", 20, 1);
		var records = await store.ListLatestAsync();
		var rows = records.Select(record => new Found(monorepo, store, jobs, record.IntegrationId, record, jobs.Find(record.IntegrationId))).ToList();
		var shown = rows.Take(limit).ToList();

		return ProgramCli.WriteResult(true, invocation.Command,
			$"{shown.Count} of {rows.Count} integration(s).",
			new Dictionary<string, object?>
			{
				["total"] = rows.Count,
				["table"] = new TomlText(AsciiTable.Render(["ID", "STATUS", "LIVE", "CREATED", "RUNS", "RESULT TAGS"],
					shown.Select(found => (IReadOnlyList<string>)
					[
						IntegrationRecord.Key(found.Id), found.Status + (found.Stale ? "!" : ""), found.Live ? "yes" : "",
						RunCommands.Time(found.Record!.Created), string.Join(",", found.Record.Runs.Select(RunMetadata.Key)),
						string.Join(",", found.Record.Components.Where(component => component.ResultTag is not null).Select(component => component.Name))
					]))),
				["integration"] = shown.Select(found => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["integration_id"] = found.Id,
					["short_id"] = IntegrationRecord.Key(found.Id),
					["status"] = found.Status,
					["live"] = found.Live,
					["stale"] = found.Stale,
					["created"] = found.Record!.Created,
					["finished"] = found.Record.Finished,
					["runs"] = found.Record.Runs.ToList(),
					["result_tags"] = found.Record.Components.Where(component => component.ResultTag is not null)
						.Select(component => $"{component.Name}@{component.ResultTag}").ToList(),
					["advanced"] = found.Record.Components.Count > 0 && found.Record.Components.All(component => component.Advanced)
				}).ToList()
			});
	}

	// ----- bassia integration show -----

	public static async Task<int> ShowAsync(Invocation invocation)
	{
		var found = await FindAsync(invocation);
		return ProgramCli.WriteResult(true, invocation.Command, Describe(found), Details(found));
	}

	private static string Describe(Found found) =>
		found.Stale ? $"Integration '{found.Id}' is recorded as started, but no process is executing it any more; 'bassia integration stop {IntegrationRecord.Key(found.Id)}' records it as cancelled."
		: found.Live ? $"Integration '{found.Id}' is live ({found.Status})."
		: found.Record is null ? $"Integration '{found.Id}' never got past preparing; it has no record."
		: $"Integration '{found.Id}' is {found.Status}.";

	private static Dictionary<string, object?> Details(Found found)
	{
		var data = new Dictionary<string, object?>
		{
			["integration_id"] = found.Id,
			["short_id"] = IntegrationRecord.Key(found.Id),
			["status"] = found.Status,
			["live"] = found.Live,
			["stale"] = found.Stale,
			["pid"] = found.Alive ? found.Job?.Pid : null,
			["log"] = found.Job?.Log,
			["last_output"] = found.Live && found.Job?.Log is { } log ? RunCommands.LastLine(log) : null
		};

		if (found.Record is { } record)
		{
			data["created"] = record.Created;
			data["finished"] = record.Finished;
			data["resolver"] = record.Resolver;
			data["structural_driver"] = record.StructuralDriver;
			data["workspace"] = record.WorkspacePath;
			data["runs"] = record.Runs.ToList();
			data["steps"] = new TomlText(Steps(record.Components));
			data["component"] = record.Components.Select(IntegrationSupport.ComponentData).ToList();
		}

		return data;
	}

	// ----- bassia integration logs / wait / stop -----

	public static async Task<int> LogsAsync(Invocation invocation)
	{
		var found = await FindAsync(invocation);
		var tail = invocation.Int("tail", 50, 0);
		if (found.Job?.Log is not { } log || !File.Exists(log))
		{
			throw new IntegrationException($"Integration '{found.Id}' was not started with -detach, so its output went to the terminal or frontend that started it.");
		}

		var lines = RunCommands.ReadLines(log);
		var shown = tail == 0 ? lines : lines.TakeLast(tail).ToList();
		return ProgramCli.WriteResult(true, invocation.Command,
			$"{shown.Count} of {lines.Count} line(s) of the output of integration '{found.Id}' ({(found.Live ? "live" : found.Status)}).",
			new Dictionary<string, object?>
			{
				["integration_id"] = found.Id,
				["status"] = found.Status,
				["live"] = found.Live,
				["log"] = log,
				["lines"] = lines.Count,
				["output"] = new TomlText(shown.Count == 0 ? "(no output yet)" : string.Join("\n", shown))
			});
	}

	public static async Task<int> WaitAsync(Invocation invocation)
	{
		var found = await FindAsync(invocation);
		var timeout = invocation.Int("timeout", 0, 0);
		var clock = Stopwatch.StartNew();
		while (found.Live)
		{
			if (timeout > 0 && clock.Elapsed.TotalSeconds >= timeout)
			{
				var data = Details(found);
				data["timed_out"] = true;
				return ProgramCli.WriteResult(false, invocation.Command, $"Integration '{found.Id}' is still running after {timeout}s.", data);
			}

			await Task.Delay(500);
			found = await ReloadAsync(found);
		}

		found = await ReloadAsync(found);
		return ProgramCli.WriteResult(found.Record?.Status == "completed", invocation.Command, Describe(found), Details(found));
	}

	public static async Task<int> StopAsync(Invocation invocation)
	{
		var found = await FindAsync(invocation);
		var timeout = invocation.Int("timeout", 30, 1);
		if (!found.Live)
		{
			if (!found.Stale)
			{
				throw new IntegrationException($"Integration '{found.Id}' is not live (status '{found.Status}'); there is nothing to stop.");
			}

			await RecordCancelledAsync(found);
			found = await ReloadAsync(found);
			return ProgramCli.WriteResult(true, invocation.Command,
				$"Integration '{found.Id}' was no longer running (its process is gone); it is now recorded as cancelled.", Details(found));
		}

		found.Jobs.RequestStop(found.Id);
		var clock = Stopwatch.StartNew();
		while (found.Live && clock.Elapsed.TotalSeconds < timeout)
		{
			await Task.Delay(200);
			found = await ReloadAsync(found);
		}

		var killed = false;
		if (found.Live && found.Job is { Hosted: false } job)
		{
			JobRegistry.Kill(job);
			killed = true;
			found = await ReloadAsync(found);
		}

		if (found.Live)
		{
			return ProgramCli.WriteResult(false, invocation.Command, $"Integration '{found.Id}' did not stop within {timeout}s.", Details(found));
		}

		if (found.Record?.Status == "started")
		{
			await RecordCancelledAsync(found);
			found = await ReloadAsync(found);
		}

		found.Jobs.ClearStop(found.Id);
		var data = Details(found);
		data["killed"] = killed;
		return ProgramCli.WriteResult(true, invocation.Command,
			found.Record is null ? $"Integration '{found.Id}' was stopped before it started; nothing was recorded." : $"Integration '{found.Id}' stopped; it is recorded as {found.Record.Status}.",
			data);
	}

	private static async Task RecordCancelledAsync(Found found)
	{
		var record = found.Record!;
		record.Status = "cancelled";
		record.Finished ??= RunMetadata.Timestamp();
		await found.Store.CommitAsync(record);
	}

	// ----- bassia integration advance -----

	public static async Task<int> AdvanceAsync(Invocation invocation)
	{
		var found = await FindAsync(invocation);
		var record = found.Record ?? throw new IntegrationException($"Integration '{found.Id}' has no record yet.");
		if (found.Live)
		{
			throw new IntegrationException($"Integration '{found.Id}' is still running; wait for it with 'bassia integration wait {IntegrationRecord.Key(found.Id)}'.");
		}

		var outcome = await IntegrationRunner.AdvanceAsync(new GitClient(found.Monorepo.Root), found.Monorepo, record);
		return ProgramCli.WriteResult(outcome.Ok, invocation.Command, outcome.Message, IntegrationSupport.RecordData(outcome, found.Store.RepoDir));
	}

	// ----- the steps picture -----

	/// <summary>
	/// Per component its base and its steps in execution order, as a tree:
	/// <code>
	/// app  onto main @ 1a2b3c4  -> integration/steady-heron-5e11aa/0  completed
	/// |-- brave-otter-3f2a91  fast_forward  SYNTAX     merged
	/// |-- calm-lynx-7d0e12    conflict      STRUCT     merged     [src/api.py]  weave: clean
	/// `-- quiet-fern-91ab22   conflict      SEMANTIC*  resolved   [README.md]  weave: conflict [README.md]  conflicts with brave-otter-3f2a91
	/// </code>
	/// A star marks a strategy that was chosen rather than decided by the triage. <c>weave:</c> is the structural
	/// merge's verdict where git conflicts, with the files still conflicted after it.
	/// </summary>
	internal static string Steps(IReadOnlyList<ComponentIntegration> components)
	{
		if (components.Count == 0)
		{
			return "(nothing to integrate)";
		}

		var builder = new StringBuilder();
		foreach (var component in components)
		{
			builder.Append(component.Name).Append("  onto ").Append(component.BaseRef).Append(" @ ").Append(component.BaseCommit[..Math.Min(7, component.BaseCommit.Length)]);
			if (component.ResultTag is not null)
			{
				builder.Append("  -> ").Append(component.ResultTag);
			}

			if (component.ResultStatus != ResultStatus.Pending)
			{
				builder.Append("  ").Append(component.ResultStatus.ToString().ToLowerInvariant());
			}

			if (component.Advanced)
			{
				builder.Append("  (advanced)");
			}

			builder.Append('\n');
			// Run keys differ in length; padding them to the longest keeps the columns aligned.
			var keyWidth = component.Steps.Select(step => RunMetadata.Key(step.RunId).Length).DefaultIfEmpty(0).Max();
			for (var i = 0; i < component.Steps.Count; i++)
			{
				var step = component.Steps[i];
				var strategy = step.Strategy switch
				{
					MergeStrategy.Syntactic => "SYNTAX",
					MergeStrategy.Structural => "STRUCT",
					MergeStrategy.Semantic => "SEMANTIC",
					_ => "SKIP"
				} + (step.Overridden ? "*" : "");
				builder.Append(i == component.Steps.Count - 1 ? "`-- " : "|-- ")
					.Append(RunMetadata.Key(step.RunId).PadRight(keyWidth)).Append("  ")
					.Append(IntegrationRecord.Snake(step.Triage).PadRight(12)).Append("  ")
					.Append(strategy.PadRight(9));
				if (step.Outcome != StepOutcome.Pending)
				{
					builder.Append("  ").Append(IntegrationRecord.Snake(step.Outcome).PadRight(9));
				}

				if (step.Conflicts.Count > 0)
				{
					builder.Append("  [").Append(string.Join(", ", step.Conflicts)).Append(']');
				}

				if (step.Structural is { } structural)
				{
					builder.Append("  weave: ").Append(IntegrationRecord.Snake(structural));
					if (step.StructuralConflicts.Count > 0)
					{
						builder.Append(" [").Append(string.Join(", ", step.StructuralConflicts)).Append(']');
					}
				}

				if (step.ConflictsWith.Count > 0)
				{
					builder.Append("  conflicts with ").Append(string.Join(", ", step.ConflictsWith.Select(RunMetadata.Key)));
				}

				builder.Append('\n');
			}
		}

		return string.Join("\n", builder.ToString().TrimEnd('\n').Split('\n').Select(line => AsciiTable.Ascii(line).TrimEnd()));
	}
}
