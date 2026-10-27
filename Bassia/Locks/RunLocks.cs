namespace Bassia.Locks;

using Bassia.Storage;

/// <summary>
/// The locking side of one run while its agent works, kept by the bassia process that executes the run. Every few
/// seconds it looks at what the agent changed in each component checkout: a change the run's filter covers is held
/// (and a reverted one let go), a change to what another live run holds is a conflict, and the run's entry is renewed
/// so its holds stay live. A run that comes back after its holds expired (its process was suspended) loses what others
/// took meanwhile. When the agent is done, <see cref="FinishAsync"/> records it all in the run record and releases.
/// Nothing is polled while no run of the monorepo locks anything.
/// </summary>
internal sealed class RunLocks : Core.IAgentRunLocks
{
	/// <summary>How often the checkouts are looked at and the holds renewed.</summary>
	internal static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

	private readonly Monorepo monorepo;
	private readonly Storage.IComponentStorage storage;
	private readonly RunMetadata metadata;
	private readonly IReadOnlyDictionary<string, LockFilter> filters;
	private readonly Action<string> report;
	private readonly Storage.ILockStore table;
	private readonly SemaphoreSlim gate = new(1, 1);
	private readonly CancellationTokenSource stop = new();
	private readonly List<LockConflict> stale = [];
	private Dictionary<string, IReadOnlyList<string>> changed = new(StringComparer.Ordinal);
	private Task? loop;
	private bool finished;

	private RunLocks(Monorepo monorepo, Storage.IComponentStorage storage, RunMetadata metadata, IReadOnlyDictionary<string, LockFilter> filters, Action<string> report)
	{
		this.monorepo = monorepo;
		this.storage = storage;
		this.metadata = metadata;
		this.filters = filters;
		this.report = report;
		table = monorepo.Locks;
	}

	/// <summary>Whether the run itself holds anything it changes: its filter covers some path of some component.</summary>
	public bool Locking => filters.Values.Any(filter => !filter.IsEmpty);

	public Storage.ILockStore Table => table;

	/// <summary>The filter of every component of the run: the configured one plus the run's own patterns.</summary>
	public static IReadOnlyDictionary<string, LockFilter> Filters(Monorepo monorepo, RunMetadata metadata)
	{
		var components = metadata.Components.Select(component => component.Name).ToList();
		var own = RunLockPatterns.Parse(metadata.LockFiles, metadata.LockFolders, components);
		return components.ToDictionary(name => name, name => LockFilter.For(monorepo, name, own), StringComparer.Ordinal);
	}

	/// <summary>Starts watching the run's checkouts; <paramref name="report"/> receives a line for every new hold or conflict.</summary>
	public static RunLocks Start(Monorepo monorepo, RunMetadata metadata, Action<string> report) =>
		Start(monorepo, monorepo.Graph().Storage, metadata, report);

	/// <summary>Starts watching the run's working copies, which <paramref name="storage"/> made.</summary>
	public static RunLocks Start(Monorepo monorepo, Storage.IComponentStorage storage, RunMetadata metadata, Action<string> report)
	{
		var locks = new RunLocks(monorepo, storage, metadata, Filters(monorepo, metadata), report);
		locks.loop = locks.LoopAsync();
		return locks;
	}

	private async Task LoopAsync()
	{
		while (!stop.IsCancellationRequested)
		{
			try
			{
				await TickAsync();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Git.GitException)
			{
				// The next round tries again; a lock table that stays unreadable shows in 'bassia lock list'.
			}

			try
			{
				await Task.Delay(Interval, stop.Token);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}

	/// <summary>Stops watching once the agent has exited, after one last look at what it left behind.</summary>
	public async Task StopAsync()
	{
		await stop.CancelAsync();
		if (loop is not null)
		{
			await loop;
		}

		await TickAsync();
	}

	/// <summary>One round now, outside the schedule (tests).</summary>
	internal Task PollAsync() => TickAsync();

	/// <summary>One round: what changed, what is held, what collides, and the heartbeat.</summary>
	private async Task TickAsync()
	{
		await gate.WaitAsync();
		try
		{
			if (finished || (!Locking && !await table.HasEntriesAsync()))
			{
				return;
			}

			var now = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
			foreach (var component in metadata.Components)
			{
				now[component.Name] = await LockCheckouts.ChangedFilesAsync(storage, component.Name, component.Path);
			}

			changed = now;
			var lines = await table.UpdateAsync(session => Apply(session, now));
			foreach (var line in lines)
			{
				report($"{metadata.RunId}: {line}");
			}
		}
		finally
		{
			gate.Release();
		}
	}

	private List<string> Apply(LockSession session, IReadOnlyDictionary<string, IReadOnlyList<string>> files)
	{
		var lines = new List<string>();
		var stamp = RunMetadata.Timestamp();
		var mine = session.Find(metadata.RunId);
		var created = mine is null;
		mine ??= NewEntry(metadata.RunId, metadata.Created, Environment.ProcessId, LocalJobScheduler.CurrentProcessStart(), stamp);

		// Back after the holds expired (the process was suspended, the machine slept): what another run took meanwhile
		// is that run's now, and whatever this run changed there collides with it.
		if (!created && table.StateOf(mine) == LockState.Expired)
		{
			foreach (var hold in mine.Holds.ToList())
			{
				var blockers = session.Blockers(metadata.RunId, hold.Key);
				if (blockers.Count == 0)
				{
					continue;
				}

				mine.Holds.Remove(hold);
				foreach (var (entry, taken) in blockers)
				{
					if (AddConflict(mine, new LockConflict(hold.Key.Component, hold.Key.Path, LockConflict.Held, entry.RunId, taken.Key.Display, stamp)))
					{
						lines.Add($"lost {hold.Key.Display} while suspended: run {RunMetadata.Key(entry.RunId)} holds {taken.Key.Display} now.");
					}
				}
			}
		}

		var wanted = new HashSet<LockKey>();
		var held = mine.Holds.Select(hold => hold.Key).ToHashSet();
		foreach (var (component, paths) in files)
		{
			var filter = filters[component];
			foreach (var path in paths)
			{
				var key = filter.KeyFor(component, path);
				var blockers = session.Blockers(metadata.RunId, key ?? new LockKey(component, path, LockKind.File));
				if (blockers.Count > 0)
				{
					foreach (var (entry, hold) in blockers)
					{
						if (AddConflict(mine, new LockConflict(component, path, LockConflict.Held, entry.RunId, hold.Key.Display, stamp)))
						{
							lines.Add($"changed {component}/{path} while run {RunMetadata.Key(entry.RunId)} holds {hold.Key.Display}; " +
								$"'bassia lock wait -path {component}/{path}' waits for it.");
						}
					}

					continue;
				}

				if (key is null)
				{
					continue;
				}

				wanted.Add(key);
				if (held.Add(key))
				{
					mine.Holds.Add(new LockHold(key, LockHold.Changed, stamp));
					lines.Add($"holds {key.Display}.");
				}
			}
		}

		// A change the agent reverted is let go; what the run claimed stays until it is released.
		mine.Holds.RemoveAll(hold => hold.How == LockHold.Changed && !wanted.Contains(hold.Key));
		mine.Heartbeat = stamp;
		if (!created || mine.Holds.Count > 0 || mine.Conflicts.Count > 0)
		{
			session.Save(mine);
		}

		return lines;
	}

	private static bool AddConflict(LockEntry entry, LockConflict conflict)
	{
		if (entry.Conflicts.Any(existing => existing.Component == conflict.Component && existing.Path == conflict.Path && existing.With == conflict.With))
		{
			return false;
		}

		entry.Conflicts.Add(conflict);
		return true;
	}

	/// <summary>
	/// The paths the agent changed although a finished run's result changed them too, which this run's checkout does not
	/// have: a <see cref="LockConflict.Stale"/> conflict each, as the run built on a version older than that result.
	/// </summary>
	public async Task CheckStaleAsync()
	{
		var released = (await table.ReadAllAsync()).Where(entry => entry.RunId != metadata.RunId && table.StateOf(entry) == LockState.Released).ToList();
		if (released.Count == 0)
		{
			return;
		}

		var stamp = RunMetadata.Timestamp();
		foreach (var component in metadata.Components)
		{
			var paths = changed.GetValueOrDefault(component.Name) ?? [];
			foreach (var entry in released)
			{
				foreach (var result in entry.Results.Where(result => result.Component == component.Name))
				{
					var hits = paths.SelectMany(path =>
					{
						var key = filters[component.Name].KeyFor(component.Name, path) ?? new LockKey(component.Name, path, LockKind.File);
						return entry.Holds.Where(hold => hold.Key.Overlaps(key)).Select(hold => (Path: path, Hold: hold));
					}).ToList();
					if (hits.Count == 0 || await LockCheckouts.ContainsAsync(storage, component.Name, component.Path, result.Commit))
					{
						continue;
					}

					foreach (var (path, hold) in hits)
					{
						stale.Add(new LockConflict(component.Name, path, LockConflict.Stale, entry.RunId, hold.Key.Display, stamp));
						report($"{metadata.RunId}: changed {component.Name}/{path} without the result {result.Tag} of run {RunMetadata.Key(entry.RunId)}, which held {hold.Key.Display}.");
					}
				}
			}
		}
	}

	/// <summary>
	/// Copies what the run held, collided with and caught up with into its record, then releases its holds: remembered
	/// with the results it pushed, so later runs are told to catch up, or gone when it pushed nothing it held.
	/// </summary>
	public async Task FinishAsync()
	{
		await gate.WaitAsync();
		try
		{
			if (finished)
			{
				return;
			}

			finished = true;
			var entry = await table.FindAsync(metadata.RunId);
			foreach (var component in metadata.Components)
			{
				var paths = changed.GetValueOrDefault(component.Name) ?? [];
				component.Held.Clear();
				component.Held.AddRange(entry?.Holds.Where(hold => hold.Key.Component == component.Name).Select(hold => hold.Key) ?? []);

				// What collided counts only where the result still changes it: a reverted change merges nothing.
				component.LockConflicts.Clear();
				component.LockConflicts.AddRange((entry?.Conflicts ?? []).Concat(stale)
					.Where(conflict => conflict.Component == component.Name && paths.Any(path => path == conflict.Path || path.StartsWith(conflict.Path + "/", StringComparison.Ordinal)))
					.DistinctBy(conflict => (conflict.Path, conflict.Kind, conflict.With)));
				component.CaughtUp.Clear();
				component.CaughtUp.AddRange(entry?.CatchUps.Where(catchUp => catchUp.Component == component.Name) ?? []);
			}

			await ReleaseAsync(table, metadata);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Releases a run's holds: an entry that held something in a component the run pushed a result to is kept as
	/// released, with those results; anything else is removed.
	/// </summary>
	public static async Task ReleaseAsync(Storage.ILockStore table, RunMetadata metadata)
	{
		if (!await table.HasAsync(metadata.RunId))
		{
			return;
		}

		await table.UpdateAsync(session =>
		{
			if (session.Find(metadata.RunId) is not { } mine)
			{
				return;
			}

			var results = metadata.Components
				.Where(component => component.ResultStatus == ResultStatus.Pushed && component.ResultTag is not null && component.ResultCommit is not null)
				.Where(component => mine.Holds.Any(hold => hold.Key.Component == component.Name))
				.Select(component => new LockResult(component.Name, component.ResultTag!, component.ResultCommit!))
				.ToList();
			if (results.Count == 0)
			{
				session.Delete(metadata.RunId);
				return;
			}

			mine.State = LockEntry.ReleasedState;
			mine.Released = RunMetadata.Timestamp();
			mine.Holds.RemoveAll(hold => results.All(result => result.Component != hold.Key.Component));
			mine.Waits.Clear();
			mine.Results.Clear();
			mine.Results.AddRange(results);
			session.Save(mine);
		});
	}

	/// <summary>Removes everything a run holds or is remembered for (a stopped or abandoned run).</summary>
	public static async Task ForgetAsync(Monorepo monorepo, string runId)
	{
		var table = monorepo.Locks;
		if (await table.HasAsync(runId))
		{
			await table.UpdateAsync(session => session.Delete(runId));
		}
	}

	/// <summary>A new, live entry for a run executed by the given process on this machine.</summary>
	public static LockEntry NewEntry(string runId, string started, int pid, string processStart, string heartbeat) => new()
	{
		RunId = runId,
		Host = FileLockStore.ThisHost,
		Pid = pid,
		ProcessStart = processStart,
		Started = started,
		Heartbeat = heartbeat,
		User = Core.Actor.Current?.User.Id
	};

	public async ValueTask DisposeAsync()
	{
		if (!stop.IsCancellationRequested)
		{
			await stop.CancelAsync();
		}

		if (loop is not null)
		{
			try
			{
				await loop;
			}
			catch (OperationCanceledException)
			{
			}
		}

		if (!finished)
		{
			// The run ended some other way (an error): it holds nothing any more.
			finished = true;
			try
			{
				await ForgetAsync(monorepo, metadata.RunId);
			}
			catch (IOException)
			{
				// Its entry expires with its process anyway.
			}
		}

		stop.Dispose();
		gate.Dispose();
	}
}
