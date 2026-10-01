namespace Bassia.Web;

using System.Globalization;
using Bassia.Git;

/// <summary>One commit on the timeline, from the component it belongs to.</summary>
internal sealed record TimelineEntry(string Component, string Hash, DateTimeOffset Date, string Author, string Subject, IReadOnlyList<string> Refs,
	CommitProvenance? Provenance = null)
{
	public string ShortHash => Hash[..Math.Min(10, Hash.Length)];
}

internal sealed record TimelinePage(IReadOnlyList<string> Unit, IReadOnlyList<TimelineEntry> Entries, int Page, int PageSize, bool HasMore);

/// <summary>
/// The combined history of a unit of components: the ones chosen plus everything they depend on, the same closure a
/// run over them has to select. Like Fossil's timeline it is one chronological list, but it never covers the whole
/// monorepo: only the unit, and only as far as the page being shown. Each component's log is read with a limit of
/// <c>skip + page size</c>, which is all a merge by date can ever need from it.
/// </summary>
internal static class Timeline
{
	public const int DefaultPageSize = 50;
	public const int MaxPageSize = 500;

	private const char Separator = '\u001f';
	private const char RecordEnd = '\u001e';

	/// <summary>The unit of the chosen components: them plus their reference closure, in dependency order.</summary>
	public static IReadOnlyList<string> Unit(Monorepo monorepo, IEnumerable<string> chosen)
	{
		var names = chosen.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToList();
		foreach (var name in names.Where(name => monorepo.FindComponent(name) is null))
		{
			throw new MonorepoException($"Component '{name}' is not registered in components.toml.");
		}

		return monorepo.Closure(names).Select(component => component.Name).ToList();
	}

	/// <summary>
	/// One page of the unit's combined history: every branch and tag of each component, or only
	/// <paramref name="branch"/> when one is given (a component without that branch is an error).
	/// </summary>
	public static async Task<TimelinePage> ReadAsync(Monorepo monorepo, IReadOnlyList<string> unit, int page, int pageSize, string? branch = null)
	{
		page = Math.Max(1, page);
		pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
		var skip = (page - 1) * pageSize;
		var limit = skip + pageSize + 1; // one more than the page tells whether another page follows

		var entries = new List<TimelineEntry>();
		foreach (var component in unit)
		{
			var sourceDir = monorepo.SourceRepoDir(component);
			if (!Directory.Exists(sourceDir))
			{
				continue;
			}

			var scope = await ScopeAsync(GitClient.In(sourceDir), component, branch);
			entries.AddRange(await LogAsync(sourceDir, component, ["--date-order", $"-n{limit}", .. scope]));
		}

		var ordered = Order(entries);
		return new TimelinePage(unit, ordered.Skip(skip).Take(pageSize).ToList(), page, pageSize, ordered.Count > skip + pageSize);
	}

	/// <summary>
	/// Newest first, ties broken by component. Within a component the order git listed the commits in is kept (the
	/// sort is stable): commit dates have one-second resolution, and git's order puts a merge above what it merges.
	/// </summary>
	public static List<TimelineEntry> Order(IEnumerable<TimelineEntry> entries) => entries
		.OrderByDescending(entry => entry.Date)
		.ThenBy(entry => entry.Component, StringComparer.Ordinal)
		.ToList();

	/// <summary>What a component's log covers: <c>--branches --tags</c>, or the one branch asked for.</summary>
	public static async Task<IReadOnlyList<string>> ScopeAsync(GitClient git, string label, string? branch)
	{
		if (branch is null)
		{
			return ["--branches", "--tags"];
		}

		var exists = await git.RunAsync(["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"]);
		return exists.ExitCode == 0
			? [$"refs/heads/{branch}"]
			: throw new MonorepoException($"{label} has no branch '{branch}'.");
	}

	/// <summary>
	/// <c>git log</c> in <paramref name="repoDir"/> with <paramref name="arguments"/> (scope, limits, filters), each
	/// commit with the provenance its message records. An empty repository has no commits; any other failure throws.
	/// </summary>
	public static async Task<IReadOnlyList<TimelineEntry>> LogAsync(string repoDir, string component, IReadOnlyList<string> arguments)
	{
		var log = await GitClient.In(repoDir).RunAsync(["log", $"--format=%H{Separator}%ct{Separator}%an{Separator}%s{Separator}%D{Separator}%b{RecordEnd}", .. arguments]);
		if (log.ExitCode != 0)
		{
			// An empty repository has no commits to show; anything else is worth surfacing.
			if (log.Error.Contains("does not have any commits", StringComparison.Ordinal))
			{
				return [];
			}

			throw new GitException($"git log failed in '{repoDir}': {log.Error.Trim()}");
		}

		var entries = new List<TimelineEntry>();
		foreach (var record in log.Output.Replace("\r", "").Split(RecordEnd, StringSplitOptions.RemoveEmptyEntries))
		{
			var fields = record.TrimStart('\n').Split(Separator);
			if (fields.Length < 6 || !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
			{
				continue;
			}

			entries.Add(new TimelineEntry(component, fields[0], DateTimeOffset.FromUnixTimeSeconds(seconds), fields[2], fields[3], Refs(fields[4]),
				CommitProvenance.Parse(fields[5])));
		}

		return entries;
	}

	/// <summary><c>HEAD -> main, tag: v0, agent/run-x</c> -> <c>main</c>, <c>v0</c>, <c>agent/run-x</c>.</summary>
	internal static IReadOnlyList<string> Refs(string decoration) =>
		decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(entry => entry.StartsWith("HEAD -> ", StringComparison.Ordinal) ? entry["HEAD -> ".Length..] : entry)
			.Select(entry => entry.StartsWith("tag: ", StringComparison.Ordinal) ? entry["tag: ".Length..] : entry)
			.Where(entry => entry != "HEAD")
			.ToList();
}
