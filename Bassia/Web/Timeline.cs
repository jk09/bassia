namespace Bassia.Web;

using System.Globalization;
using Bassia.Git;

/// <summary>One commit on the timeline, from the component it belongs to.</summary>
internal sealed record TimelineEntry(string Component, string Hash, DateTimeOffset Date, string Author, string Subject, IReadOnlyList<string> Refs)
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

	public static async Task<TimelinePage> ReadAsync(Monorepo monorepo, IReadOnlyList<string> unit, int page, int pageSize)
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

			var log = await GitClient.In(sourceDir).RunAsync(["log", "--branches", "--tags", "--date-order", $"-n{limit}",
				$"--format=%H{Separator}%ct{Separator}%an{Separator}%s{Separator}%D"]);
			if (log.ExitCode != 0)
			{
				// An empty repository has no commits to show; anything else is worth surfacing.
				if (log.Error.Contains("does not have any commits", StringComparison.Ordinal))
				{
					continue;
				}

				throw new GitException($"git log failed in '{sourceDir}': {log.Error.Trim()}");
			}

			foreach (var line in log.Output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				var fields = line.Split(Separator);
				if (fields.Length < 5 || !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
				{
					continue;
				}

				entries.Add(new TimelineEntry(component, fields[0], DateTimeOffset.FromUnixTimeSeconds(seconds), fields[2], fields[3], Refs(fields[4])));
			}
		}

		var ordered = entries
			.OrderByDescending(entry => entry.Date)
			.ThenBy(entry => entry.Component, StringComparer.Ordinal)
			.ThenBy(entry => entry.Hash, StringComparer.Ordinal)
			.ToList();
		return new TimelinePage(unit, ordered.Skip(skip).Take(pageSize).ToList(), page, pageSize, ordered.Count > skip + pageSize);
	}

	/// <summary><c>HEAD -> main, tag: v0, agent/run-x</c> -> <c>main</c>, <c>v0</c>, <c>agent/run-x</c>.</summary>
	private static IReadOnlyList<string> Refs(string decoration) =>
		decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(entry => entry.StartsWith("HEAD -> ", StringComparison.Ordinal) ? entry["HEAD -> ".Length..] : entry)
			.Select(entry => entry.StartsWith("tag: ", StringComparison.Ordinal) ? entry["tag: ".Length..] : entry)
			.Where(entry => entry != "HEAD")
			.ToList();
}
