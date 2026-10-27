namespace Bassia.Locks;

using Bassia.Core;
using Bassia.Storage;

/// <summary>A released hold whose run's result changed what a run is about to change, and that the run's checkout lacks.</summary>
internal sealed record LockBehind(LockEntry Entry, LockResult Result, IReadOnlyList<string> Holds);

/// <summary>What bringing one result into a checkout did: the files it changed, or why it could not.</summary>
internal sealed record LockCatchUpOutcome(LockBehind Behind, IReadOnlyList<string> Files, string? Error);

/// <summary>
/// The storage side of locking, in a run's component working copies: which files the agent changed, whether a finished
/// run's result is missing from the working copy, bringing that result in, and forgetting results that landed. Everything
/// goes through the storage contract, so locking works on any backend.
/// </summary>
internal static class LockCheckouts
{
	/// <summary>
	/// The files changed in a checkout's working tree (modified, added, deleted, untracked), relative to its root. The
	/// agent never commits - Bassia does once it exits - so the working tree is all it changed. A missing or broken
	/// checkout changed nothing.
	/// </summary>
	public static async Task<IReadOnlyList<string>> ChangedFilesAsync(IComponentStorage storage, string component, string checkout)
	{
		if (!Directory.Exists(checkout))
		{
			return [];
		}

		try
		{
			return await storage.OpenWorkspace(component, checkout).ChangedPathsAsync();
		}
		catch (StorageException)
		{
			return [];
		}
	}

	/// <summary>Whether <paramref name="commit"/> is in the checkout's history: the checkout has it and its head contains it.</summary>
	public static async Task<bool> ContainsAsync(IComponentStorage storage, string component, string checkout, string commit)
	{
		try
		{
			return await storage.OpenWorkspace(component, checkout).ContainsAsync(commit);
		}
		catch (StorageException)
		{
			return false;
		}
	}

	/// <summary>
	/// The released holds of other runs that overlap <paramref name="keys"/> in <paramref name="component"/>, whose
	/// result there the checkout does not contain yet, oldest release first.
	/// </summary>
	public static async Task<IReadOnlyList<LockBehind>> BehindAsync(IComponentStorage storage, Storage.ILockStore table, string runId, string component, string checkout, IReadOnlyList<LockKey> keys)
	{
		var behind = new List<LockBehind>();
		var released = (await table.ReadAllAsync())
			.Where(entry => entry.RunId != runId && table.StateOf(entry) == LockState.Released)
			.OrderBy(entry => entry.Released, StringComparer.Ordinal);
		foreach (var entry in released)
		{
			var holds = entry.Holds.Where(hold => keys.Any(key => key.Overlaps(hold.Key))).Select(hold => hold.Key.Display).ToList();
			if (holds.Count == 0)
			{
				continue;
			}

			foreach (var result in entry.Results.Where(result => result.Component == component))
			{
				if (!await ContainsAsync(storage, component, checkout, result.Commit))
				{
					behind.Add(new LockBehind(entry, result, holds));
				}
			}
		}

		return behind;
	}

	/// <summary>
	/// Merges another run's result into the checkout: fetches its result tag from the component's store and merges it (a
	/// fast-forward while the run's branch is still at its base). The agent's own uncommitted changes stay as they are; a
	/// merge that would overwrite them, or that conflicts, is undone and reported.
	/// </summary>
	public static async Task<LockCatchUpOutcome> BringInAsync(IComponentStorage storage, string checkout, LockBehind behind)
	{
		var result = behind.Result;
		var workspace = storage.OpenWorkspace(result.Component, checkout);
		if (!await workspace.FetchTagAsync(result.Tag))
		{
			return new LockCatchUpOutcome(behind, [], $"could not fetch {result.Tag}: component '{result.Component}' has no such tag");
		}

		var before = await workspace.HeadAsync();
		var merge = await workspace.MergeAsync(result.Commit, new WorkspaceMerge(
			$"bassia: bring in {result.Tag} (run {RunMetadata.Key(behind.Entry.RunId)}) before changing {string.Join(", ", behind.Holds)}", FastForward: true));
		if (merge.Status != MergeStatus.Merged)
		{
			await workspace.AbortMergeAsync();
			var reason = (merge.Error ?? "").Split('\n').FirstOrDefault(line => line.Trim().Length > 0)?.Trim() ?? "the merge did not complete";
			return new LockCatchUpOutcome(behind, [], $"could not merge {result.Tag}: {reason}");
		}

		var after = await workspace.HeadAsync();
		return new LockCatchUpOutcome(behind, await workspace.ChangedPathsAsync(before, after), null);
	}

	/// <summary>
	/// Forgets released results that landed on their component's default branch: from there every run starting on the
	/// branch has them. An entry with nothing left to remember goes.
	/// </summary>
	public static async Task ForgetLandedAsync(MonorepoGraph graph, Storage.ILockStore table)
	{
		var landed = new HashSet<(string RunId, string Component)>();
		foreach (var entry in (await table.ReadAllAsync()).Where(entry => table.StateOf(entry) == LockState.Released))
		{
			foreach (var result in entry.Results)
			{
				if (!graph.Registry.Contains(result.Component) || !graph.Storage.Exists(result.Component))
				{
					landed.Add((entry.RunId, result.Component));
					continue;
				}

				var store = graph.Storage.Open(result.Component);
				if (await store.DefaultBranchAsync() is { } branch && await store.BranchTipAsync(branch) is { } tip && await store.IsAncestorAsync(result.Commit, tip))
				{
					landed.Add((entry.RunId, result.Component));
				}
			}
		}

		if (landed.Count == 0)
		{
			return;
		}

		await table.UpdateAsync(session =>
		{
			foreach (var entry in session.Entries.Where(entry => entry.State == LockEntry.ReleasedState).ToList())
			{
				if (entry.Results.RemoveAll(result => landed.Contains((entry.RunId, result.Component))) == 0)
				{
					continue;
				}

				entry.Holds.RemoveAll(hold => entry.Results.All(result => result.Component != hold.Key.Component));
				if (entry.Results.Count == 0)
				{
					session.Delete(entry.RunId);
				}
				else
				{
					session.Save(entry);
				}
			}
		});
	}
}
