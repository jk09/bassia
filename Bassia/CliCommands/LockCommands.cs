namespace Bassia;

using System.Diagnostics;
using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Core;
using Bassia.Git;
using Bassia.Locks;

/// <summary>
/// <c>bassia lock ...</c>: the shared view of which files and folders parallel runs hold. An agent inside a run (which
/// has <c>BASSIA_RUN_ID</c>) checks a path before it rewrites it, claims it, or waits until the run holding it is done
/// and continues from that run's result; a person sees who holds what and breaks the holds of a stuck run.
/// </summary>
internal static class LockCommands
{
	/// <summary>How often <c>lock wait</c> looks again.</summary>
	internal static TimeSpan WaitInterval { get; set; } = TimeSpan.FromMilliseconds(500);

	/// <summary>The run a lock command acts for: its id, folder, record and the process executing it.</summary>
	private sealed record RunContext(string RunId, string RunDir, RunMetadata? Record, JobInfo? Job)
	{
		public bool Live => JobRegistry.IsAlive(Job) && (Record is null || Record.Status == "started");

		public string Checkout(string component) =>
			Record?.Components.FirstOrDefault(entry => entry.Name == component)?.Path ?? Path.Combine(RunDir, component);

		public bool Has(string component) => Record is null || Record.Components.Any(entry => entry.Name == component);
	}

	/// <summary>A path as given and the key it is held as.</summary>
	private sealed record LockPath(string Given, LockKey Key);

	/// <summary>
	/// The monorepo of the working directory - inside a run folder that is the run's monorepo - or, when the run folder
	/// is elsewhere (<c>workspace.path</c>), the one <c>BASSIA_ROOT</c> names.
	/// </summary>
	private static Monorepo LoadMonorepo()
	{
		var root = Monorepo.FindRoot(Environment.CurrentDirectory)
			?? (Environment.GetEnvironmentVariable("BASSIA_ROOT") is { Length: > 0 } fromRun && Directory.Exists(Path.Combine(fromRun, Monorepo.MetaRepoFolderName)) ? fromRun : null)
			?? throw new AgentException($"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia init' first.");
		return Monorepo.Load(root);
	}

	// ----- bassia lock list -----

	public static async Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = LoadMonorepo();
		var table = new LockTable(monorepo);
		await LockCheckouts.ForgetLandedAsync(monorepo.Graph(), table);
		var component = invocation.Get("component");
		var run = invocation.Get("run") is { } name ? (await FindRunAsync(monorepo, name)).RunId : null;
		var entries = table.ReadAll();

		var holds = entries
			.Where(entry => run is null || entry.RunId == run)
			.SelectMany(entry => entry.Holds.Where(hold => component is null || hold.Key.Component == component).Select(hold => (Entry: entry, Hold: hold)))
			.ToList();
		var waits = entries
			.Where(entry => run is null || entry.RunId == run)
			.SelectMany(entry => entry.Waits.Where(wait => table.IsWaiting(entry, wait) && (component is null || wait.Key.Component == component)).Select(wait => (Entry: entry, Wait: wait)))
			.ToList();

		var states = holds.Select(pair => table.StateOf(pair.Entry)).ToList();
		var message = holds.Count == 0 && waits.Count == 0
			? "Nothing is held" + (component is null ? "" : $" in '{component}'") + (run is null ? "" : $" by run '{RunMetadata.Key(run)}'") + "."
			: $"{holds.Count} hold(s) of {holds.Select(pair => pair.Entry.RunId).Distinct().Count()} run(s): {states.Count(state => state == LockState.Live)} live, " +
				$"{states.Count(state => state == LockState.Released)} released with results not landed yet, {states.Count(state => state == LockState.Expired)} expired; " +
				$"{waits.Select(pair => pair.Entry.RunId).Distinct().Count()} run(s) waiting.";

		return ProgramCli.WriteResult(true, invocation.Command, message, new Dictionary<string, object?>
		{
			["lock_table"] = table.Dir,
			["ttl_seconds"] = (long)table.Ttl.TotalSeconds,
			["live"] = states.Count(state => state == LockState.Live),
			["released"] = states.Count(state => state == LockState.Released),
			["expired"] = states.Count(state => state == LockState.Expired),
			["table"] = new TomlText(AsciiTable.Render(["PATH", "KIND", "RUN", "STATE", "HOW", "SINCE", "WAITING"],
				holds.Select(pair => (IReadOnlyList<string>)
				[
					pair.Hold.Key.Display, LockKey.KindName(pair.Hold.Key.Kind), RunMetadata.Key(pair.Entry.RunId), StateName(table.StateOf(pair.Entry)),
					pair.Hold.How, RunCommands.Time(pair.Hold.Since), string.Join(",", Waiters(table, entries, pair.Entry, pair.Hold).Select(RunMetadata.Key))
				]))),
			["hold"] = holds.Select(pair => (IReadOnlyDictionary<string, object?>)HoldData(table, entries, pair.Entry, pair.Hold)).ToList(),
			["wait"] = waits.Select(pair => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["run_id"] = pair.Entry.RunId,
				["short_id"] = RunMetadata.Key(pair.Entry.RunId),
				["component"] = pair.Wait.Key.Component,
				["path"] = pair.Wait.Key.Display,
				["since"] = pair.Wait.Since,
				["held_by"] = entries.Where(other => other.RunId != pair.Entry.RunId && table.StateOf(other) == LockState.Live
					&& other.Holds.Any(hold => hold.Key.Overlaps(pair.Wait.Key))).Select(other => other.RunId).ToList()
			}).ToList()
		});
	}

	private static Dictionary<string, object?> HoldData(LockTable table, IReadOnlyList<LockEntry> entries, LockEntry entry, LockHold hold) => new()
	{
		["component"] = hold.Key.Component,
		["path"] = hold.Key.Path,
		["kind"] = LockKey.KindName(hold.Key.Kind),
		["display"] = hold.Key.Display,
		["run_id"] = entry.RunId,
		["short_id"] = RunMetadata.Key(entry.RunId),
		["state"] = StateName(table.StateOf(entry)),
		["how"] = hold.How,
		["since"] = hold.Since,
		["heartbeat"] = entry.Heartbeat,
		["host"] = entry.Host,
		["result_tag"] = entry.Results.FirstOrDefault(result => result.Component == hold.Key.Component)?.Tag,
		["waiters"] = Waiters(table, entries, entry, hold).ToList()
	};

	/// <summary>The runs waiting for something <paramref name="hold"/> covers.</summary>
	private static IEnumerable<string> Waiters(LockTable table, IReadOnlyList<LockEntry> entries, LockEntry holder, LockHold hold) => entries
		.Where(entry => entry.RunId != holder.RunId && entry.Waits.Any(wait => table.IsWaiting(entry, wait) && wait.Key.Overlaps(hold.Key)))
		.Select(entry => entry.RunId);

	internal static string StateName(LockState state) => state.ToString().ToLowerInvariant();

	// ----- bassia lock check -----

	public static async Task<int> CheckAsync(Invocation invocation)
	{
		var monorepo = LoadMonorepo();
		var table = new LockTable(monorepo);
		await LockCheckouts.ForgetLandedAsync(monorepo.Graph(), table);
		var run = await RunOfAsync(invocation, monorepo, required: false);
		var paths = Resolve(invocation, monorepo, run);
		var entries = table.ReadAll();
		var results = new List<IReadOnlyDictionary<string, object?>>();
		var notes = new List<string>();
		var free = true;
		var anyBehind = false;
		foreach (var path in paths)
		{
			var others = entries.Where(entry => entry.RunId != run?.RunId).ToList();
			var heldBy = others.Where(entry => table.StateOf(entry) == LockState.Live)
				.SelectMany(entry => entry.Holds.Where(hold => hold.Key.Overlaps(path.Key)).Select(hold => (Entry: entry, Hold: hold))).ToList();
			var expired = others.Where(entry => table.StateOf(entry) == LockState.Expired && entry.Holds.Any(hold => hold.Key.Overlaps(path.Key))).Select(entry => entry.RunId).ToList();
			var yours = run is not null && entries.FirstOrDefault(entry => entry.RunId == run.RunId) is { } mine && mine.Holds.Any(hold => hold.Key.Overlaps(path.Key));

			// A released result the run's checkout lacks; without a run, every released result that changed the path.
			var behind = run is not null && run.Has(path.Key.Component) && Directory.Exists(run.Checkout(path.Key.Component))
				? (await LockCheckouts.BehindAsync(monorepo.Graph().Storage, table, run.RunId, path.Key.Component, run.Checkout(path.Key.Component), [path.Key])).Select(item => (item.Entry.RunId, item.Result.Tag)).ToList()
				: run is null
					? others.Where(entry => table.StateOf(entry) == LockState.Released && entry.Holds.Any(hold => hold.Key.Overlaps(path.Key)))
						.SelectMany(entry => entry.Results.Where(result => result.Component == path.Key.Component).Select(result => (entry.RunId, result.Tag))).ToList()
					: [];

			var state = heldBy.Count > 0 ? "held" : behind.Count > 0 ? "behind" : yours ? "yours" : "free";
			free &= heldBy.Count == 0;
			anyBehind |= behind.Count > 0;
			if (heldBy.Count > 0)
			{
				notes.Add($"{path.Key.Display} is held by {string.Join(", ", heldBy.Select(pair => $"run {RunMetadata.Key(pair.Entry.RunId)} ({pair.Hold.Key.Display})"))}");
			}
			else if (behind.Count > 0)
			{
				notes.Add(run is null
					? $"{path.Key.Display} was changed by {string.Join(", ", behind.Select(item => item.Tag))}, not on its default branch yet"
					: $"the checkout lacks the change {string.Join(", ", behind.Select(item => item.Tag))} made to {path.Key.Display}");
			}

			results.Add(new Dictionary<string, object?>
			{
				["given"] = path.Given,
				["component"] = path.Key.Component,
				["path"] = path.Key.Path,
				["kind"] = LockKey.KindName(path.Key.Kind),
				["display"] = path.Key.Display,
				["state"] = state,
				["held_by"] = heldBy.Select(pair => pair.Entry.RunId).Distinct().ToList(),
				["holder_paths"] = heldBy.Select(pair => pair.Hold.Key.Display).Distinct().ToList(),
				["behind"] = behind.Select(item => item.Tag).ToList(),
				["behind_runs"] = behind.Select(item => item.RunId).Distinct().ToList(),
				["expired_holders"] = expired
			});
		}

		var wait = $"'bassia lock wait -path {string.Join(",", paths.Select(path => path.Given))}'";
		var message = notes.Count == 0
			? paths.Count == 1 ? $"{paths[0].Key.Display} is free to change." : $"All {paths.Count} paths are free to change."
			: string.Join("; ", notes) + (run is null ? "." : !free ? $". {wait} waits until the holder is done and brings its change in." : $". {wait} brings it in.");
		return ProgramCli.WriteResult(true, invocation.Command, message, new Dictionary<string, object?>
		{
			["run_id"] = run?.RunId,
			["free"] = free,
			["behind"] = anyBehind,
			["path"] = results
		});
	}

	// ----- bassia lock claim -----

	public static async Task<int> ClaimAsync(Invocation invocation)
	{
		var monorepo = LoadMonorepo();
		var table = new LockTable(monorepo);
		await LockCheckouts.ForgetLandedAsync(monorepo.Graph(), table);
		var run = await LiveRunAsync(invocation, monorepo);
		var paths = Resolve(invocation, monorepo, run);
		var keys = paths.Select(path => path.Key).ToList();
		var blockers = await table.UpdateAsync(session =>
		{
			var blocking = keys.SelectMany(key => session.Blockers(run.RunId, key)).ToList();
			if (blocking.Count == 0)
			{
				Hold(session, run, keys);
			}

			return blocking;
		});

		if (blockers.Count > 0)
		{
			return ProgramCli.WriteResult(false, invocation.Command,
				$"Nothing was claimed: {string.Join(", ", blockers.Select(pair => $"{pair.Hold.Key.Display} is held by run {RunMetadata.Key(pair.Entry.RunId)}").Distinct())}. " +
				$"'bassia lock wait -path {string.Join(",", paths.Select(path => path.Given))}' waits for it.",
				new Dictionary<string, object?>
				{
					["run_id"] = run.RunId,
					["claimed"] = new List<string>(),
					["held_by"] = blockers.Select(pair => pair.Entry.RunId).Distinct().ToList(),
					["holder_paths"] = blockers.Select(pair => pair.Hold.Key.Display).Distinct().ToList()
				});
		}

		var behind = new List<LockBehind>();
		foreach (var group in keys.GroupBy(key => key.Component))
		{
			if (Directory.Exists(run.Checkout(group.Key)))
			{
				behind.AddRange(await LockCheckouts.BehindAsync(monorepo.Graph().Storage, table, run.RunId, group.Key, run.Checkout(group.Key), group.ToList()));
			}
		}

		return ProgramCli.WriteResult(true, invocation.Command,
			$"Run '{RunMetadata.Key(run.RunId)}' holds {string.Join(", ", keys.Select(key => key.Display))}." +
			(behind.Count == 0 ? "" : $" Its checkout lacks {string.Join(", ", behind.Select(item => item.Result.Tag))}, which changed them: 'bassia lock wait -path {string.Join(",", paths.Select(path => path.Given))}' brings it in."),
			new Dictionary<string, object?>
			{
				["run_id"] = run.RunId,
				["claimed"] = keys.Select(key => key.Display).ToList(),
				["behind"] = behind.Select(item => item.Result.Tag).ToList()
			});
	}

	/// <summary>Adds claimed holds for <paramref name="keys"/> the run does not hold yet, creating its entry if need be.</summary>
	private static LockEntry Hold(LockSession session, RunContext run, IReadOnlyList<LockKey> keys)
	{
		var stamp = RunMetadata.Timestamp();
		var mine = session.Find(run.RunId) ?? RunLocks.NewEntry(run.RunId, run.Record?.Created ?? run.Job!.Started, run.Job!.Pid, run.Job.ProcessStart, stamp);
		foreach (var key in keys.Where(key => mine.Holds.All(hold => hold.Key != key)))
		{
			mine.Holds.Add(new LockHold(key, LockHold.Claimed, stamp));
		}

		mine.Heartbeat = stamp;
		session.Save(mine);
		return mine;
	}

	// ----- bassia lock wait -----

	public static async Task<int> WaitAsync(Invocation invocation)
	{
		var monorepo = LoadMonorepo();
		var table = new LockTable(monorepo);
		await LockCheckouts.ForgetLandedAsync(monorepo.Graph(), table);
		var run = await LiveRunAsync(invocation, monorepo);
		var paths = Resolve(invocation, monorepo, run);
		var keys = paths.Select(path => path.Key).ToList();
		var timeout = invocation.Int("timeout", 0, 0);
		var pid = Environment.ProcessId;
		var processStart = JobRegistry.CurrentProcessStart();
		var waitedFor = new List<string>();
		var clock = Stopwatch.StartNew();

		while (true)
		{
			var (blockers, deadlock) = await table.UpdateAsync(session =>
			{
				var blocking = keys.SelectMany(key => session.Blockers(run.RunId, key)).ToList();
				var mine = session.Find(run.RunId);
				if (blocking.Count == 0)
				{
					mine = Hold(session, run, keys);
					mine.Waits.RemoveAll(wait => wait.Pid == pid);
					return (blocking, (IReadOnlyList<string>?)null);
				}

				var cycle = session.DeadlockPath(run.RunId, blocking.Select(pair => pair.Entry.RunId));
				mine ??= RunLocks.NewEntry(run.RunId, run.Record?.Created ?? run.Job!.Started, run.Job!.Pid, run.Job.ProcessStart, RunMetadata.Timestamp());
				mine.Waits.RemoveAll(wait => wait.Pid == pid);
				if (cycle is null)
				{
					var stamp = RunMetadata.Timestamp();
					mine.Waits.AddRange(keys.Where(key => blocking.Any(pair => pair.Hold.Key.Overlaps(key))).Select(key => new LockWait(key, stamp, pid, processStart)));
				}

				session.Save(mine);
				return (blocking, cycle);
			});

			waitedFor.AddRange(blockers.Select(pair => pair.Entry.RunId).Where(id => !waitedFor.Contains(id)).Distinct());
			if (blockers.Count == 0)
			{
				break;
			}

			if (deadlock is not null)
			{
				return ProgramCli.WriteResult(false, invocation.Command,
					$"Waiting would deadlock: {string.Join(" -> ", deadlock.Select(RunMetadata.Key))} each wait for what the next one holds. " +
					"Release what you hold ('bassia lock release -path ...') or go on without these paths.",
					new Dictionary<string, object?> { ["run_id"] = run.RunId, ["deadlock"] = deadlock.ToList(), ["held_by"] = waitedFor });
			}

			if (timeout > 0 && clock.Elapsed.TotalSeconds >= timeout)
			{
				await table.UpdateAsync(session =>
				{
					if (session.Find(run.RunId) is { } mine && mine.Waits.RemoveAll(wait => wait.Pid == pid) > 0)
					{
						session.Save(mine);
					}
				});
				return ProgramCli.WriteResult(false, invocation.Command,
					$"{string.Join(", ", blockers.Select(pair => pair.Hold.Key.Display).Distinct())} still held by {string.Join(", ", blockers.Select(pair => RunMetadata.Key(pair.Entry.RunId)).Distinct())} after {timeout}s; wait again, or go on without them.",
					new Dictionary<string, object?> { ["run_id"] = run.RunId, ["timed_out"] = true, ["held_by"] = blockers.Select(pair => pair.Entry.RunId).Distinct().ToList() });
			}

			await Task.Delay(WaitInterval);
		}

		// Held now. What the runs that changed these paths left behind comes into the checkout, so the agent goes on
		// from their version instead of overwriting it.
		var outcomes = new List<LockCatchUpOutcome>();
		var behind = new List<LockBehind>();
		foreach (var group in keys.GroupBy(key => key.Component))
		{
			var checkout = run.Checkout(group.Key);
			if (!Directory.Exists(checkout))
			{
				continue;
			}

			var pending = await LockCheckouts.BehindAsync(monorepo.Graph().Storage, table, run.RunId, group.Key, checkout, group.ToList());
			behind.AddRange(pending);
			if (invocation.Has("no-refresh"))
			{
				continue;
			}

			foreach (var item in pending)
			{
				outcomes.Add(await LockCheckouts.BringInAsync(monorepo.Graph().Storage, checkout, item));
			}
		}

		var brought = outcomes.Where(outcome => outcome.Error is null).ToList();
		if (brought.Count > 0)
		{
			await table.UpdateAsync(session =>
			{
				if (session.Find(run.RunId) is { } mine)
				{
					var stamp = RunMetadata.Timestamp();
					mine.CatchUps.AddRange(brought.Select(outcome => new LockCatchUp(outcome.Behind.Result.Component, outcome.Behind.Entry.RunId,
						outcome.Behind.Result.Tag, outcome.Behind.Result.Commit, stamp, outcome.Files)));
					session.Save(mine);
				}
			});
		}

		var changed = brought.SelectMany(outcome => outcome.Files.Where(file => keys.Any(key => key.Component == outcome.Behind.Result.Component && key.Covers(file)))
			.Select(file => $"{outcome.Behind.Result.Component}/{file}")).Distinct().ToList();
		var failed = outcomes.Where(outcome => outcome.Error is not null).ToList();
		var held = string.Join(", ", keys.Select(key => key.Display));
		var message = failed.Count > 0
			? $"Run '{RunMetadata.Key(run.RunId)}' holds {held}, but its checkout could not be brought up to date: {string.Join("; ", failed.Select(outcome => outcome.Error))}. " +
				"Its own changes to these files collide with another run's; settle that before going on."
			: invocation.Has("no-refresh") && behind.Count > 0
				? $"Run '{RunMetadata.Key(run.RunId)}' holds {held}. Its checkout lacks {string.Join(", ", behind.Select(item => item.Result.Tag))}, which changed them; without -no-refresh this brings it in."
				: changed.Count > 0
					? $"Run '{RunMetadata.Key(run.RunId)}' holds {held}. Another run changed {string.Join(", ", changed)} meanwhile; its change is now in your checkout: re-read these files before you edit them."
					: $"Run '{RunMetadata.Key(run.RunId)}' holds {held}{(waitedFor.Count > 0 ? " after waiting" : "")}; nothing changed there meanwhile.";
		return ProgramCli.WriteResult(failed.Count == 0, invocation.Command, message, new Dictionary<string, object?>
		{
			["run_id"] = run.RunId,
			["held"] = keys.Select(key => key.Display).ToList(),
			["waited"] = waitedFor.Count > 0,
			["waited_for"] = waitedFor,
			["waited_seconds"] = (long)clock.Elapsed.TotalSeconds,
			["changed"] = changed,
			["behind"] = behind.Select(item => item.Result.Tag).ToList(),
			["brought_in"] = brought.Select(outcome => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["component"] = outcome.Behind.Result.Component,
				["run_id"] = outcome.Behind.Entry.RunId,
				["tag"] = outcome.Behind.Result.Tag,
				["commit"] = outcome.Behind.Result.Commit,
				["files"] = outcome.Files
			}).ToList(),
			["failed"] = failed.Select(outcome => outcome.Error!).ToList()
		});
	}

	// ----- bassia lock release -----

	public static async Task<int> ReleaseAsync(Invocation invocation)
	{
		var monorepo = LoadMonorepo();
		var table = new LockTable(monorepo);
		var name = invocation.Get("run") ?? Environment.GetEnvironmentVariable("BASSIA_RUN_ID")
			?? throw invocation.Usage("-run is required outside a run (inside one, BASSIA_RUN_ID names it).");
		if (!invocation.Has("all") && !invocation.Has("path"))
		{
			throw invocation.Usage("Give -path <path,...> to let go of some paths, or -all.");
		}

		var run = await FindRunAsync(monorepo, name);
		var entry = table.Find(run.RunId);
		if (entry is null)
		{
			return ProgramCli.WriteResult(true, invocation.Command, $"Run '{RunMetadata.Key(run.RunId)}' holds nothing.",
				new Dictionary<string, object?> { ["run_id"] = run.RunId, ["released"] = new List<string>() });
		}

		var self = Environment.GetEnvironmentVariable("BASSIA_RUN_ID") is { } current && RunMetadata.NormalizeRunId(current) == run.RunId;
		if (!self && table.StateOf(entry) == LockState.Live && !invocation.Has("force"))
		{
			throw new AgentException($"Run '{RunMetadata.Key(run.RunId)}' is live and releases its holds when it ends; -force breaks them now " +
				"(its agent may still be changing them).");
		}

		var keys = invocation.Has("all") ? null : Resolve(invocation, monorepo, run).Select(path => path.Key).ToList();
		var released = await table.UpdateAsync(session =>
		{
			if (session.Find(run.RunId) is not { } mine)
			{
				return new List<string>();
			}

			if (keys is null)
			{
				session.Delete(run.RunId);
				return mine.Holds.Select(hold => hold.Key.Display).ToList();
			}

			var gone = mine.Holds.Where(hold => keys.Any(key => key == hold.Key || (key.Component == hold.Key.Component && key.Covers(hold.Key.Path)))).ToList();
			mine.Holds.RemoveAll(gone.Contains);
			session.Save(mine);
			return gone.Select(hold => hold.Key.Display).ToList();
		});

		return ProgramCli.WriteResult(true, invocation.Command,
			released.Count == 0
				? $"Run '{RunMetadata.Key(run.RunId)}' held none of these paths."
				: $"Run '{RunMetadata.Key(run.RunId)}' let go of {string.Join(", ", released)}." +
					(table.StateOf(entry) == LockState.Live ? " A file its agent still has changed is held again at the next look." : ""),
			new Dictionary<string, object?> { ["run_id"] = run.RunId, ["released"] = released });
	}

	// ----- runs and paths -----

	/// <summary>The run <c>-run</c> or <c>BASSIA_RUN_ID</c> names; null (or a usage error) when neither does.</summary>
	private static async Task<RunContext?> RunOfAsync(Invocation invocation, Monorepo monorepo, bool required)
	{
		var name = invocation.Get("run") ?? Environment.GetEnvironmentVariable("BASSIA_RUN_ID");
		if (string.IsNullOrWhiteSpace(name))
		{
			return required ? throw invocation.Usage("-run is required outside a run (inside one, BASSIA_RUN_ID names it).") : null;
		}

		return await FindRunAsync(monorepo, name);
	}

	private static async Task<RunContext> LiveRunAsync(Invocation invocation, Monorepo monorepo)
	{
		var run = (await RunOfAsync(invocation, monorepo, required: true))!;
		return run.Live
			? run
			: throw new AgentException($"Agentic run '{RunMetadata.Key(run.RunId)}' is not live; only a run whose agent is working can hold paths.");
	}

	private static async Task<RunContext> FindRunAsync(Monorepo monorepo, string name)
	{
		var store = new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir);
		var jobs = new JobRegistry(monorepo);
		var candidates = (await store.ListKeysAsync(RunMetadata.RefPrefix)).Select(RunMetadata.NormalizeRunId)
			.Concat(jobs.List(RunCommands.JobKind).Select(job => job.Id))
			.Concat(new LockTable(monorepo).ReadAll().Select(entry => entry.RunId))
			.Distinct(StringComparer.Ordinal)
			.Where(runId => IntegrationSupport.MatchesRun(runId, name))
			.ToList();
		var runId = candidates.Count switch
		{
			1 => candidates[0],
			0 => throw new AgentException($"Unknown agentic run '{name}'. List the runs with 'bassia agent list'."),
			_ => throw new AgentException($"'{name}' names more than one agentic run ({string.Join(", ", candidates.Select(RunMetadata.Key))}); give more of its id.")
		};

		var record = await store.LoadLatestAsync(runId);
		var fromEnvironment = Environment.GetEnvironmentVariable("BASSIA_RUN_ID") is { } current && RunMetadata.NormalizeRunId(current) == runId
			? Environment.GetEnvironmentVariable("BASSIA_RUN_DIR")
			: null;
		return new RunContext(runId, record?.WorkspacePath ?? fromEnvironment ?? Path.Combine(monorepo.WorkspaceDir, runId), record, jobs.Find(runId));
	}

	private static IReadOnlyList<LockPath> Resolve(Invocation invocation, Monorepo monorepo, RunContext? run)
	{
		var given = invocation.List("path");
		if (given.Count == 0)
		{
			throw invocation.Usage("-path names no path.");
		}

		return given.Select(path => new LockPath(path, KeyOf(monorepo, run, invocation.Get("component"), path))).ToList();
	}

	/// <summary>
	/// The key a path is held as. A path relative to the working directory inside the run folder (or absolute in it)
	/// starts with its component; one outside it is read as <c>&lt;component&gt;/&lt;path&gt;</c>, and with
	/// <paramref name="component"/> it is relative to that component. A path through a nested component's folder
	/// belongs to the nested component. A folder (a trailing slash, or a folder in the run's checkout) is held as a
	/// whole; a file as the run's filter would hold it.
	/// </summary>
	internal static LockKey KeyOf(Monorepo monorepo, string? runDir, IReadOnlyDictionary<string, string>? checkouts, string? component, string text, Func<string, LockFilter> filterOf)
	{
		var raw = text.Trim();
		var folder = raw.EndsWith('/') || raw.EndsWith('\\');
		string name;
		string rest;
		if (component is not null)
		{
			name = component;
			rest = Normalize(raw, text);
		}
		else
		{
			string? relative = null;
			if (runDir is not null)
			{
				var full = Path.GetFullPath(raw, Environment.CurrentDirectory);
				var candidate = Path.GetRelativePath(runDir, full);
				if (candidate != "." && !candidate.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(candidate))
				{
					relative = candidate;
				}
			}

			if (relative is null && Path.IsPathRooted(raw))
			{
				throw new MonorepoException($"'{text}' is not inside the run folder{(runDir is null ? "" : $" '{runDir}'")}; give it as <component>/<path>.");
			}

			var normalized = Normalize(relative ?? raw, text);
			var slash = normalized.IndexOf('/');
			(name, rest) = slash < 0 ? (normalized, "") : (normalized[..slash], normalized[(slash + 1)..]);
		}

		if (monorepo.FindComponent(name) is null)
		{
			throw new MonorepoException($"'{text}' does not start with a registered component (it names '{name}'); give it as <component>/<path>.");
		}

		// Through a junction into the component nested there.
		for (var definition = monorepo.FindComponent(name)!; ;)
		{
			var nested = definition.References
				.Where(reference => rest == reference.Path || rest.StartsWith(reference.Path + "/", StringComparison.Ordinal))
				.OrderByDescending(reference => reference.Path.Length)
				.FirstOrDefault();
			if (nested is null || monorepo.FindComponent(nested.Name) is not { } inner)
			{
				break;
			}

			name = nested.Name;
			rest = rest == nested.Path ? "" : rest[(nested.Path.Length + 1)..];
			definition = inner;
		}

		if (rest.Length == 0 || folder)
		{
			return new LockKey(name, rest, LockKind.Folder);
		}

		if (checkouts?.GetValueOrDefault(name) is { } checkout && Directory.Exists(Path.Combine(checkout, rest)))
		{
			return new LockKey(name, rest, LockKind.Folder);
		}

		return filterOf(name).KeyFor(name, rest) ?? new LockKey(name, rest, LockKind.File);
	}

	private static LockKey KeyOf(Monorepo monorepo, RunContext? run, string? component, string text)
	{
		if (component is not null && monorepo.FindComponent(component) is null)
		{
			throw new MonorepoException($"'{component}' is not a registered component.");
		}

		var checkouts = run?.Record?.Components.ToDictionary(entry => entry.Name, entry => entry.Path, StringComparer.Ordinal);

		// A file is held as the run's filter holds it: the configured patterns plus the run's own.
		var own = run?.Record is { } record
			? RunLockPatterns.Parse(record.LockFiles, record.LockFolders, record.Components.Select(entry => entry.Name).ToList())
			: null;
		return KeyOf(monorepo, run?.RunDir, checkouts, component, text,
			name => LockFilter.For(monorepo, name, own is not null && run!.Has(name) ? own : null));
	}

	/// <summary><c>/</c>-separated, without <c>./</c>, leading or trailing slashes; <c>..</c> is refused.</summary>
	private static string Normalize(string path, string given)
	{
		var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != ".").ToList();
		return segments.Contains("..")
			? throw new MonorepoException($"'{given}' leaves the component; give a path inside it.")
			: string.Join('/', segments);
	}
}
