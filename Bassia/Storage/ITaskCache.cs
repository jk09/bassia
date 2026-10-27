namespace Bassia.Storage;

using Bassia.Core;

/// <summary>
/// Where task results are cached: a passed task's log and declared outputs under its key (see <see cref="TaskCache.Key"/>),
/// so an unchanged task is restored instead of run. <see cref="TaskCache"/> keeps them in <c>&lt;workspace&gt;/.task-cache</c>;
/// a server keeps one cache for everybody.
/// </summary>
internal interface ITaskCache
{
	/// <summary>Where the cache lives, as the backend names it; shown to users, never parsed.</summary>
	string Location { get; }

	/// <summary>
	/// The cached result under <paramref name="key"/>, with its outputs restored into <paramref name="componentFolder"/> and
	/// its log copied to <paramref name="logPath"/>; null when there is none.
	/// </summary>
	Task<TaskResult?> TryRestoreAsync(string key, PlannedTask task, string componentFolder, string logPath);

	/// <summary>
	/// Stores a passed <paramref name="result"/> under <paramref name="key"/>: its log and the files of
	/// <paramref name="componentFolder"/> its task's outputs match, leaving out <paramref name="nested"/> (the reference paths).
	/// </summary>
	Task StoreAsync(string key, PlannedTask task, TaskResult result, string componentFolder, IReadOnlyList<string> nested);

	/// <summary>How many entries the cache holds and their size in bytes.</summary>
	Task<(int Entries, long Bytes)> SizeAsync();

	/// <summary>Removes every entry.</summary>
	Task ClearAsync();
}
