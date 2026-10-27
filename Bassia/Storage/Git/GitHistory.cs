namespace Bassia.Storage.Git;

using System.Globalization;
using Bassia.Git;

/// <summary>
/// History as people read it, from a git repository: <c>git log --date-order</c> with the decorations and, when asked,
/// the changed paths (<see cref="IComponentStore.LogAsync"/>), and line counts per file (<c>git diff --numstat</c>).
/// Shared by component stores, working copies and the meta-repo.
/// </summary>
internal static class GitHistory
{
	private const char Start = '\u001e';
	private const char Separator = '\u001f';

	public static async Task<IReadOnlyList<LogEntry>> LogAsync(string repoDir, LogQuery query)
	{
		if (query.Tips?.FirstOrDefault(tip => tip.StartsWith('-')) is { } option)
		{
			throw new StorageException($"'{option}' is not a branch or tag.");
		}

		List<string> arguments = ["log", "--date-order", "--decorate=full", $"--format={Start}%H{Separator}%P{Separator}%ct{Separator}%an <%ae>{Separator}%D{Separator}%B{Separator}"];
		if (query.Paths)
		{
			arguments.AddRange(["--name-only", "--no-renames"]);
		}

		if (query.Skip > 0)
		{
			arguments.Add($"--skip={query.Skip}");
		}

		if (query.Limit is { } limit)
		{
			arguments.Add($"--max-count={limit}");
		}

		if (query.Grep is { Count: > 0 } grep)
		{
			arguments.Add("--fixed-strings");
			arguments.AddRange(grep.Select(text => $"--grep={text}"));
		}

		arguments.AddRange(query.Tips is null ? ["--branches", "--tags"] : query.Tips);
		arguments.Add("--");
		var log = await GitClient.In(repoDir).RunAsync(arguments);
		if (log.ExitCode != 0)
		{
			// An empty repository has no commits to show; anything else is worth surfacing.
			if (log.Error.Contains("does not have any commits", StringComparison.Ordinal))
			{
				return [];
			}

			throw new GitException($"git log failed in '{repoDir}': {log.Error.Trim()}");
		}

		var entries = new List<LogEntry>();
		foreach (var record in log.Output.Replace("\r", "").Split(Start, StringSplitOptions.RemoveEmptyEntries))
		{
			var fields = record.Split(Separator);
			if (fields.Length < 7 || !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
			{
				continue;
			}

			var (branches, tags) = Decorations(fields[4]);
			var commit = new StoredCommit(fields[0], fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), fields[5].TrimEnd('\n'), fields[3]);
			var paths = query.Paths
				? fields[6].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
				: null;
			entries.Add(new LogEntry(commit, DateTimeOffset.FromUnixTimeSeconds(seconds), branches, tags, paths));
		}

		return entries;
	}

	/// <summary><c>HEAD -&gt; refs/heads/main, tag: refs/tags/v0</c> as branches and tags; refs of remotes and HEAD itself are left out.</summary>
	private static (IReadOnlyList<string> Branches, IReadOnlyList<string> Tags) Decorations(string decoration)
	{
		var branches = new List<string>();
		var tags = new List<string>();
		foreach (var entry in decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var name = entry.StartsWith("HEAD -> ", StringComparison.Ordinal) ? entry["HEAD -> ".Length..] : entry;
			name = name.StartsWith("tag: ", StringComparison.Ordinal) ? name["tag: ".Length..] : name;
			if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
			{
				branches.Add(name["refs/heads/".Length..]);
			}
			else if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
			{
				tags.Add(name["refs/tags/".Length..]);
			}
		}

		return (branches, tags);
	}

	/// <summary><c>git diff --numstat</c>; from nothing is from the empty tree.</summary>
	public static async Task<IReadOnlyList<FileStat>> DiffStatAsync(string repoDir, string? from, string to)
	{
		var git = GitClient.In(repoDir);
		from ??= (await git.RunOrThrowAsync(["mktree"], "")).Trim();
		var numstat = await git.RunOrThrowAsync(["diff", "--numstat", from, to, "--"]);
		return numstat.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(line => line.Split('\t'))
			.Where(fields => fields.Length >= 3)
			.Select(fields => new FileStat(fields[2], Count(fields[0]), Count(fields[1])))
			.ToList();

		static int Count(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : -1;
	}
}
