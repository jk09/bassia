namespace Bassia.Storage.Memory;

using Bassia.Locks;

/// <summary>
/// The reference implementation of <see cref="ILockStore"/>: entries in memory, one decision at a time, liveness by
/// lease alone - exactly what the contract promises and nothing a file or a process adds. Entries are copied in and
/// out through their TOML form, so a decision never changes an entry another one is looking at.
/// </summary>
internal sealed class MemoryLockStore(TimeSpan ttl) : ILockStore
{
	private readonly SemaphoreSlim gate = new(1, 1);
	private readonly Dictionary<string, string> entries = new(StringComparer.Ordinal);

	public string Location => "memory:locks";

	public TimeSpan Ttl { get; } = ttl;

	public Task<bool> HasEntriesAsync()
	{
		lock (entries)
		{
			return Task.FromResult(entries.Count > 0);
		}
	}

	public Task<IReadOnlyList<LockEntry>> ReadAllAsync() => Task.FromResult<IReadOnlyList<LockEntry>>(Snapshot());

	public Task<LockEntry?> FindAsync(string runId)
	{
		lock (entries)
		{
			return Task.FromResult(entries.TryGetValue(runId, out var text) ? LockEntry.FromToml(text) : null);
		}
	}

	public LockState StateOf(LockEntry entry, DateTimeOffset? now = null) => LockStores.ByLease(entry, Ttl, now);

	/// <summary>A wait is waited while its run's entry is live: the contract knows no processes.</summary>
	public bool IsWaiting(LockEntry entry, LockWait wait) => StateOf(entry) == LockState.Live;

	public async Task<T> UpdateAsync<T>(Func<LockSession, T> change)
	{
		await gate.WaitAsync();
		try
		{
			var session = new LockSession(this, Snapshot());
			var result = change(session);
			lock (entries)
			{
				foreach (var runId in session.Deleted)
				{
					entries.Remove(runId);
				}

				foreach (var entry in session.Saved)
				{
					entries[entry.RunId] = entry.ToToml();
				}
			}

			return result;
		}
		finally
		{
			gate.Release();
		}
	}

	private List<LockEntry> Snapshot()
	{
		lock (entries)
		{
			return entries.Values.Select(LockEntry.FromToml).OrderBy(entry => entry.Started, StringComparer.Ordinal).ToList();
		}
	}
}
