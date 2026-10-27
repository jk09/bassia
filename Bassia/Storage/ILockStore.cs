namespace Bassia.Storage;

using System.Globalization;
using Bassia.Locks;

/// <summary>
/// Where the lock table of a monorepo lives: one entry per run (its holds, waits, conflicts, catch-ups and released
/// results), shared by every process and machine working on the monorepo. A lock decision reads the entries, decides
/// and writes what it changed as one atomic step (<see cref="UpdateAsync{T}"/>); <see cref="Locks.FileLockStore"/> does
/// it under a file mutex in <c>&lt;root&gt;/.locks</c>, a server in a transaction.
/// </summary>
/// <remarks>
/// An entry is live while its run renews it: its heartbeat is younger than <see cref="Ttl"/> (a lease). That is all the
/// contract promises, since a process id means nothing on another machine; a backend may also know an entry's process
/// is gone and treat it as expired sooner, as the file store does for processes on its own machine.
/// </remarks>
internal interface ILockStore
{
	/// <summary>Where the table lives, as the backend names it; shown to users, never parsed.</summary>
	string Location { get; }

	/// <summary>How long an entry stays live without its run renewing it.</summary>
	TimeSpan Ttl { get; }

	/// <summary>Whether any run has an entry; when none has, a run that holds nothing has nothing to check.</summary>
	Task<bool> HasEntriesAsync();

	/// <summary>Every run's entry as it is now, oldest run first.</summary>
	Task<IReadOnlyList<LockEntry>> ReadAllAsync();

	/// <summary>The run's entry, or null when it has none.</summary>
	Task<LockEntry?> FindAsync(string runId);

	/// <summary>Where an entry stands: released once its run finished, expired once its lease ran out, live otherwise.</summary>
	LockState StateOf(LockEntry entry, DateTimeOffset? now = null);

	/// <summary>Whether a run still waits for a path it said it waits for.</summary>
	bool IsWaiting(LockEntry entry, LockWait wait);

	/// <summary>
	/// One lock decision: reads every entry, lets <paramref name="change"/> decide on them (see <see cref="LockSession"/>),
	/// and writes and deletes what it changed, atomically with respect to every other decision.
	/// </summary>
	Task<T> UpdateAsync<T>(Func<LockSession, T> change);
}

internal static class LockStores
{
	public static Task UpdateAsync(this ILockStore store, Action<LockSession> change) => store.UpdateAsync(session =>
	{
		change(session);
		return true;
	});

	public static async Task<bool> HasAsync(this ILockStore store, string runId) => await store.FindAsync(runId) is not null;

	/// <summary>The contract's liveness: released, or expired once the heartbeat is older than <paramref name="ttl"/>.</summary>
	public static LockState ByLease(LockEntry entry, TimeSpan ttl, DateTimeOffset? now = null) =>
		entry.State == LockEntry.ReleasedState ? LockState.Released
		: Age(entry, now) > ttl ? LockState.Expired
		: LockState.Live;

	/// <summary>How long ago the entry was last renewed.</summary>
	public static TimeSpan Age(LockEntry entry, DateTimeOffset? now = null) =>
		DateTimeOffset.TryParse(entry.Heartbeat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var heartbeat)
			? (now ?? DateTimeOffset.UtcNow) - heartbeat
			: TimeSpan.MaxValue;
}
