namespace Bassia.Locks;

using System.Diagnostics;
using System.Globalization;
using Tomlyn;
using Tomlyn.Model;

/// <summary>A path a run holds: <c>changed</c> when Bassia saw its agent change it, <c>claimed</c> when the run asked for it.</summary>
internal sealed record LockHold(LockKey Key, string How, string Since)
{
	public const string Changed = "changed";
	public const string Claimed = "claimed";
}

/// <summary>A path a run waits for (<c>bassia lock wait</c>), by the process that waits.</summary>
internal sealed record LockWait(LockKey Key, string Since, int Pid, string ProcessStart);

/// <summary>
/// A path a run changed although it was not the run's to change: another live run held it (<see cref="Held"/>), or a
/// finished run's result changed it and the run did not have that result (<see cref="Stale"/>).
/// </summary>
internal sealed record LockConflict(string Component, string Path, string Kind, string With, string WithPath, string At)
{
	public const string Held = "held";
	public const string Stale = "stale";

	public string Display => $"{Component}/{Path}";

	public string Describe() => Kind == Stale
		? $"{Display} was changed without the result of run {RunMetadata.Key(With)}, which held {WithPath}"
		: $"{Display} was changed while run {RunMetadata.Key(With)} held {WithPath}";
}

/// <summary>Another run's result brought into a run's checkout, for paths the run waited for or was behind on.</summary>
internal sealed record LockCatchUp(string Component, string Run, string Tag, string Commit, string At, IReadOnlyList<string> Files);

/// <summary>A result a finished run pushed, remembered with its released holds until it lands.</summary>
internal sealed record LockResult(string Component, string Tag, string Commit);

/// <summary>Where a run's holds stand.</summary>
internal enum LockState
{
	/// <summary>The run's bassia process renews its holds: nobody else may change them.</summary>
	Live,

	/// <summary>Not renewed within <c>lock.ttl</c>, or its process is gone: the holds block nobody. A suspended run that comes back loses what others took meanwhile.</summary>
	Expired,

	/// <summary>The run finished; its holds are remembered, with its results, until those land on their default branch.</summary>
	Released
}

/// <summary>
/// One run's advertisement in the lock table (<c>.locks/&lt;run-id&gt;.toml</c>): who executes the run (host, process),
/// its heartbeat, what it holds and waits for, the conflicts seen so far and what it caught up with; once released, the
/// results it pushed.
/// </summary>
internal sealed class LockEntry
{
	public const string LiveState = "live";
	public const string ReleasedState = "released";

	public required string RunId { get; init; }
	public string State { get; set; } = LiveState;
	public required string Host { get; init; }
	public required int Pid { get; init; }
	public required string ProcessStart { get; init; }
	public required string Started { get; init; }
	public required string Heartbeat { get; set; }
	public string? Released { get; set; }
	public List<LockHold> Holds { get; } = [];
	public List<LockWait> Waits { get; } = [];
	public List<LockConflict> Conflicts { get; } = [];
	public List<LockCatchUp> CatchUps { get; } = [];
	public List<LockResult> Results { get; } = [];

	public string ToToml()
	{
		var table = new TomlTable
		{
			["run_id"] = RunId,
			["state"] = State,
			["host"] = Host,
			["pid"] = (long)Pid,
			["process_start"] = ProcessStart,
			["started"] = Started,
			["heartbeat"] = Heartbeat
		};
		if (Released is not null) table["released"] = Released;

		table["hold"] = Array(Holds, hold => new TomlTable
		{
			["component"] = hold.Key.Component, ["path"] = hold.Key.Path, ["kind"] = LockKey.KindName(hold.Key.Kind), ["how"] = hold.How, ["since"] = hold.Since
		});
		table["wait"] = Array(Waits, wait => new TomlTable
		{
			["component"] = wait.Key.Component, ["path"] = wait.Key.Path, ["kind"] = LockKey.KindName(wait.Key.Kind), ["since"] = wait.Since,
			["pid"] = (long)wait.Pid, ["process_start"] = wait.ProcessStart
		});
		table["conflict"] = Array(Conflicts, conflict => new TomlTable
		{
			["component"] = conflict.Component, ["path"] = conflict.Path, ["kind"] = conflict.Kind, ["with"] = conflict.With, ["with_path"] = conflict.WithPath, ["at"] = conflict.At
		});
		table["catch_up"] = Array(CatchUps, catchUp => new TomlTable
		{
			["component"] = catchUp.Component, ["run"] = catchUp.Run, ["tag"] = catchUp.Tag, ["commit"] = catchUp.Commit, ["at"] = catchUp.At,
			["files"] = Strings(catchUp.Files)
		});
		table["result"] = Array(Results, result => new TomlTable { ["component"] = result.Component, ["tag"] = result.Tag, ["commit"] = result.Commit });
		return TomlSerializer.Serialize(table);
	}

	public static LockEntry FromToml(string text)
	{
		var table = TomlSerializer.Deserialize<TomlTable>(text) ?? throw new TomlException("empty lock record");
		var entry = new LockEntry
		{
			RunId = (string)table["run_id"],
			State = (string)table["state"],
			Host = (string)table["host"],
			Pid = (int)(long)table["pid"],
			ProcessStart = (string)table["process_start"],
			Started = (string)table["started"],
			Heartbeat = (string)table["heartbeat"],
			Released = table.TryGetValue("released", out var released) ? released as string : null
		};

		foreach (var hold in Tables(table, "hold"))
		{
			entry.Holds.Add(new LockHold(Key(hold), (string)hold["how"], (string)hold["since"]));
		}

		foreach (var wait in Tables(table, "wait"))
		{
			entry.Waits.Add(new LockWait(Key(wait), (string)wait["since"], (int)(long)wait["pid"], (string)wait["process_start"]));
		}

		foreach (var conflict in Tables(table, "conflict"))
		{
			entry.Conflicts.Add(new LockConflict((string)conflict["component"], (string)conflict["path"], (string)conflict["kind"],
				(string)conflict["with"], (string)conflict["with_path"], (string)conflict["at"]));
		}

		foreach (var catchUp in Tables(table, "catch_up"))
		{
			entry.CatchUps.Add(new LockCatchUp((string)catchUp["component"], (string)catchUp["run"], (string)catchUp["tag"], (string)catchUp["commit"],
				(string)catchUp["at"], catchUp.TryGetValue("files", out var files) && files is TomlArray array ? array.OfType<string>().ToList() : []));
		}

		foreach (var result in Tables(table, "result"))
		{
			entry.Results.Add(new LockResult((string)result["component"], (string)result["tag"], (string)result["commit"]));
		}

		return entry;

		static LockKey Key(TomlTable item) => new((string)item["component"], (string)item["path"], LockKey.ParseKind((string)item["kind"]));
	}

	private static TomlTableArray Array<T>(IEnumerable<T> items, Func<T, TomlTable> map)
	{
		var array = new TomlTableArray();
		foreach (var item in items)
		{
			array.Add(map(item));
		}

		return array;
	}

	internal static TomlArray Strings(IEnumerable<string> values)
	{
		var array = new TomlArray();
		foreach (var value in values)
		{
			array.Add(value);
		}

		return array;
	}

	private static IEnumerable<TomlTable> Tables(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) && value is TomlTableArray array ? array : [];
}

/// <summary>
/// The lock table: <c>&lt;root&gt;/.locks</c>, one <see cref="LockEntry"/> file per run, shared by every bassia process of
/// the monorepo whatever its workspace. Anyone reads it at any time (each file is replaced atomically); every decision
/// that depends on what others hold - taking a hold, waiting, releasing - is made while holding <c>.locks/.mutex</c>
/// exclusively, an OS file lock that a crashed process gives up by itself.
/// </summary>
internal sealed class LockTable
{
	public const string FolderName = ".locks";
	private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(60);

	public LockTable(Monorepo monorepo) : this(Path.Combine(monorepo.Root, FolderName), LockFilter.Ttl(monorepo.Config))
	{
	}

	internal LockTable(string dir, TimeSpan ttl)
	{
		Dir = dir;
		Ttl = ttl;
	}

	public string Dir { get; }

	public TimeSpan Ttl { get; }

	/// <summary>The machine name a lock entry records, which decides whether its process can be checked from here.</summary>
	public static string ThisHost => Environment.MachineName;

	/// <summary>Whether any run is in the table at all; when none is, a run that holds nothing has nothing to check.</summary>
	public bool HasEntries => Directory.Exists(Dir) && Directory.EnumerateFiles(Dir, "*.toml").Any();

	/// <summary>Every run's entry as it is right now; an entry being replaced or unreadable is left out.</summary>
	public IReadOnlyList<LockEntry> ReadAll() =>
		!Directory.Exists(Dir) ? [] : Directory.EnumerateFiles(Dir, "*.toml").Select(Read).OfType<LockEntry>().OrderBy(entry => entry.Started, StringComparer.Ordinal).ToList();

	public LockEntry? Find(string runId) => Read(EntryPath(runId));

	/// <summary>Whether the run has an entry at all; nothing to release or forget otherwise.</summary>
	public bool Has(string runId) => File.Exists(EntryPath(runId));

	/// <summary>
	/// Where an entry stands: released once its run finished; expired when its heartbeat is older than the TTL or, on
	/// this machine, its process is gone; live otherwise.
	/// </summary>
	public LockState StateOf(LockEntry entry, DateTimeOffset? now = null)
	{
		if (entry.State == LockEntry.ReleasedState)
		{
			return LockState.Released;
		}

		return IsDead(entry) || Age(entry, now) > Ttl ? LockState.Expired : LockState.Live;
	}

	/// <summary>The entry's process ran on this machine and is gone: it can never renew its holds again.</summary>
	public static bool IsDead(LockEntry entry) =>
		entry.State == LockEntry.LiveState && entry.Host == ThisHost && !JobRegistry.IsProcessAlive(entry.Pid, entry.ProcessStart);

	/// <summary>How long ago the entry was last renewed.</summary>
	public static TimeSpan Age(LockEntry entry, DateTimeOffset? now = null) =>
		DateTimeOffset.TryParse(entry.Heartbeat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var heartbeat)
			? (now ?? DateTimeOffset.UtcNow) - heartbeat
			: TimeSpan.MaxValue;

	/// <summary>Whether a wait is still being waited: its process lives (on this machine), or its run's entry is live (elsewhere).</summary>
	public bool IsWaiting(LockEntry entry, LockWait wait) =>
		entry.Host == ThisHost ? JobRegistry.IsProcessAlive(wait.Pid, wait.ProcessStart) : StateOf(entry) == LockState.Live;

	/// <summary>
	/// Reads every entry under the mutex, lets <paramref name="change"/> decide, and writes or deletes what it changed.
	/// Entries whose process on this machine is gone are dropped first: a crashed run holds nothing.
	/// </summary>
	public async Task<T> UpdateAsync<T>(Func<LockSession, T> change)
	{
		Directory.CreateDirectory(Dir);
		using var mutex = await AcquireMutexAsync();
		var session = new LockSession(this, ReadAll().ToList());
		foreach (var dead in session.Entries.Where(IsDead).ToList())
		{
			session.Delete(dead.RunId);
		}

		var result = change(session);
		foreach (var runId in session.Deleted)
		{
			File.Delete(EntryPath(runId));
		}

		foreach (var entry in session.Saved)
		{
			Write(entry);
		}

		return result;
	}

	public Task UpdateAsync(Action<LockSession> change) => UpdateAsync(session =>
	{
		change(session);
		return true;
	});

	private async Task<FileStream> AcquireMutexAsync()
	{
		var path = Path.Combine(Dir, ".mutex");
		var clock = Stopwatch.StartNew();
		while (true)
		{
			try
			{
				return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException) when (clock.Elapsed < MutexTimeout)
			{
				await Task.Delay(15);
			}
			catch (IOException ex)
			{
				throw new IOException($"The lock table '{Dir}' stayed busy for {MutexTimeout.TotalSeconds:0}s: {ex.Message}", ex);
			}
		}
	}

	private string EntryPath(string runId) => Path.Combine(Dir, runId + ".toml");

	private static LockEntry? Read(string path)
	{
		try
		{
			return File.Exists(path) ? LockEntry.FromToml(File.ReadAllText(path)) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlException or KeyNotFoundException or InvalidCastException)
		{
			return null;
		}
	}

	private void Write(LockEntry entry)
	{
		// Written aside and moved into place, so a reader never sees half an entry.
		var path = EntryPath(entry.RunId);
		var temporary = path + $".{Environment.ProcessId}.tmp";
		File.WriteAllText(temporary, entry.ToToml());
		File.Move(temporary, path, overwrite: true);
	}
}

/// <summary>The lock table as one decision sees it, under the mutex: every entry, and what the decision changes.</summary>
internal sealed class LockSession
{
	private readonly LockTable table;
	private readonly List<LockEntry> entries;
	private readonly HashSet<string> saved = new(StringComparer.Ordinal);
	private readonly HashSet<string> deleted = new(StringComparer.Ordinal);

	// Checking a process is not free, and a decision asks about the same entries again and again.
	private readonly Dictionary<string, LockState> states = new(StringComparer.Ordinal);

	internal LockSession(LockTable table, List<LockEntry> entries)
	{
		this.table = table;
		this.entries = entries;
	}

	public LockTable Table => table;

	public IReadOnlyList<LockEntry> Entries => entries;

	internal IEnumerable<LockEntry> Saved => entries.Where(entry => saved.Contains(entry.RunId));

	internal IEnumerable<string> Deleted => deleted;

	public LockEntry? Find(string runId) => entries.FirstOrDefault(entry => entry.RunId == runId);

	/// <summary>Where the entry stands, as of this decision.</summary>
	public LockState StateOf(LockEntry entry) =>
		states.TryGetValue(entry.RunId, out var state) ? state : states[entry.RunId] = table.StateOf(entry);

	/// <summary>Marks <paramref name="entry"/> (new or changed) to be written.</summary>
	public void Save(LockEntry entry)
	{
		states.Remove(entry.RunId);
		if (!entries.Contains(entry))
		{
			entries.RemoveAll(existing => existing.RunId == entry.RunId);
			entries.Add(entry);
		}

		deleted.Remove(entry.RunId);
		saved.Add(entry.RunId);
	}

	public void Delete(string runId)
	{
		entries.RemoveAll(entry => entry.RunId == runId);
		saved.Remove(runId);
		deleted.Add(runId);
	}

	/// <summary>The live holds of other runs that overlap <paramref name="key"/>: what keeps <paramref name="runId"/> from changing it.</summary>
	public IReadOnlyList<(LockEntry Entry, LockHold Hold)> Blockers(string runId, LockKey key) => entries
		.Where(entry => entry.RunId != runId && StateOf(entry) == LockState.Live)
		.SelectMany(entry => entry.Holds.Where(hold => hold.Key.Overlaps(key)).Select(hold => (entry, hold)))
		.ToList();

	/// <summary>
	/// Whether <paramref name="runId"/> waiting for <paramref name="blockers"/> closes a cycle: one of them waits, directly
	/// or through others, for something <paramref name="runId"/> holds.
	/// </summary>
	public IReadOnlyList<string>? DeadlockPath(string runId, IEnumerable<string> blockers)
	{
		var previous = new Dictionary<string, string>(StringComparer.Ordinal);
		var queue = new Queue<string>();
		foreach (var blocker in blockers.Distinct(StringComparer.Ordinal))
		{
			previous[blocker] = runId;
			queue.Enqueue(blocker);
		}

		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			if (Find(current) is not { } entry)
			{
				continue;
			}

			foreach (var wait in entry.Waits.Where(wait => table.IsWaiting(entry, wait)))
			{
				foreach (var (holder, _) in Blockers(current, wait.Key))
				{
					if (holder.RunId == runId)
					{
						var path = new List<string> { runId, current };
						for (var step = current; previous[step] != runId; step = previous[step])
						{
							path.Insert(1, previous[step]);
						}

						path.Add(runId);
						return path;
					}

					if (previous.TryAdd(holder.RunId, current))
					{
						queue.Enqueue(holder.RunId);
					}
				}
			}
		}

		return null;
	}
}
