namespace Bassia.Locks;

using Bassia.Git;

/// <summary>A released hold whose run's result changed what a run is about to change, and that the run's checkout lacks.</summary>
internal sealed record LockBehind(LockEntry Entry, LockResult Result, IReadOnlyList<string> Holds);

/// <summary>What bringing one result into a checkout did: the files it changed, or why it could not.</summary>
internal sealed record LockCatchUpOutcome(LockBehind Behind, IReadOnlyList<string> Files, string? Error);

/// <summary>
/// The git side of locking, in a run's component checkouts: which files the agent changed, whether a finished run's
/// result is missing from the checkout, and bringing that result in.
/// </summary>
internal static class LockCheckouts
{
	/// <summary>
	/// The files changed in a checkout's working tree (modified, added, deleted, untracked), relative to its root. The
	/// agent never commits - Bassia does once it exits - so the working tree is all it changed. A missing or broken
	/// checkout changed nothing.
	/// </summary>
	public static async Task<IReadOnlyList<string>> ChangedFilesAsync(string checkout)
	{
		if (!Directory.Exists(checkout))
		{
			return [];
		}

		var status = await GitClient.In(checkout).RunAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames"]);
		if (status.ExitCode != 0)
		{
			return [];
		}

		// Each entry is "XY <path>", NUL-terminated.
		return status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
			.Where(entry => entry.Length > 3)
			.Select(entry => entry[3..])
			.Distinct(StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>Whether <paramref name="commit"/> is in the checkout's history: the checkout has it and HEAD contains it.</summary>
	public static async Task<bool> ContainsAsync(string checkout, string commit)
	{
		var git = GitClient.In(checkout);
		if ((await git.RunAsync(["cat-file", "-e", $"{commit}^{{commit}}"])).ExitCode != 0)
		{
			return false;
		}

		return (await git.RunAsync(["merge-base", "--is-ancestor", commit, "HEAD"])).ExitCode == 0;
	}

	/// <summary>
	/// The released holds of other runs that overlap <paramref name="keys"/> in <paramref name="component"/>, whose
	/// result there the checkout does not contain yet, oldest release first.
	/// </summary>
	public static async Task<IReadOnlyList<LockBehind>> BehindAsync(LockTable table, string runId, string component, string checkout, IReadOnlyList<LockKey> keys)
	{
		var behind = new List<LockBehind>();
		var released = table.ReadAll()
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
				if (!await ContainsAsync(checkout, result.Commit))
				{
					behind.Add(new LockBehind(entry, result, holds));
				}
			}
		}

		return behind;
	}

	/// <summary>
	/// Merges another run's result into the checkout: fetches its result tag from the component's source-of-truth repo
	/// and merges it (a fast-forward while the run's branch is still at its base). The agent's own uncommitted changes
	/// stay as they are; a merge that would overwrite them, or that conflicts, is undone and reported.
	/// </summary>
	public static async Task<LockCatchUpOutcome> BringInAsync(Monorepo monorepo, string checkout, LockBehind behind)
	{
		var git = GitClient.In(checkout);
		var result = behind.Result;
		var fetch = await git.RunAsync(["fetch", "--quiet", "--no-tags", monorepo.SourceRepoDir(result.Component), $"+refs/tags/{result.Tag}:refs/tags/{result.Tag}"]);
		if (fetch.ExitCode != 0)
		{
			return new LockCatchUpOutcome(behind, [], $"could not fetch {result.Tag}: {fetch.Error.Trim()}");
		}

		var before = await git.RunOrThrowAsync(["rev-parse", "HEAD"]);
		var merge = await git.RunAsync(["merge", "--no-edit", "--quiet", "-m",
			$"bassia: bring in {result.Tag} (run {RunMetadata.Key(behind.Entry.RunId)}) before changing {string.Join(", ", behind.Holds)}", result.Commit]);
		if (merge.ExitCode != 0)
		{
			if ((await git.RunAsync(["rev-parse", "--quiet", "--verify", "MERGE_HEAD"])).ExitCode == 0)
			{
				await git.RunAsync(["merge", "--abort"]);
			}

			var reason = (merge.Error + merge.Output).Trim().Split('\n').FirstOrDefault(line => line.Trim().Length > 0)?.Trim() ?? $"git merge exited with {merge.ExitCode}";
			return new LockCatchUpOutcome(behind, [], $"could not merge {result.Tag}: {reason}");
		}

		var after = await git.RunOrThrowAsync(["rev-parse", "HEAD"]);
		var files = after == before ? "" : await git.RunOrThrowAsync(["diff", "--name-only", before, after]);
		return new LockCatchUpOutcome(behind, files.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), null);
	}

	/// <summary>
	/// Forgets released results that landed on their component's default branch: from there every run starting on the
	/// branch has them. An entry with nothing left to remember goes.
	/// </summary>
	public static async Task ForgetLandedAsync(Monorepo monorepo, LockTable table)
	{
		var landed = new HashSet<(string RunId, string Component)>();
		foreach (var entry in table.ReadAll().Where(entry => table.StateOf(entry) == LockState.Released))
		{
			foreach (var result in entry.Results)
			{
				var source = monorepo.SourceRepoDir(result.Component);
				if (monorepo.FindComponent(result.Component) is null || !Directory.Exists(source))
				{
					landed.Add((entry.RunId, result.Component));
					continue;
				}

				var git = GitClient.In(source);
				var head = await git.RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"]);
				if (head.ExitCode == 0 && (await git.RunAsync(["merge-base", "--is-ancestor", result.Commit, $"refs/heads/{head.Output.Trim()}"])).ExitCode == 0)
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
