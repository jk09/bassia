namespace Bassia.Web;

using Bassia.Storage;

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

		var storage = monorepo.Backend.Components;
		var entries = new List<TimelineEntry>();
		foreach (var component in unit)
		{
			if (!storage.Exists(component))
			{
				continue;
			}

			var store = storage.Open(component);
			var scope = await ScopeAsync(store, $"Component '{component}'", branch);
			entries.AddRange(await LogAsync(store, component, new LogQuery(scope, Limit: limit)));
		}

		var ordered = Order(entries);
		return new TimelinePage(unit, ordered.Skip(skip).Take(pageSize).ToList(), page, pageSize, ordered.Count > skip + pageSize);
	}

	/// <summary>
	/// Newest first, ties broken by component. Within a component the order its history came in is kept (the sort is
	/// stable): commit dates have one-second resolution, and that order puts a merge above what it merges.
	/// </summary>
	public static List<TimelineEntry> Order(IEnumerable<TimelineEntry> entries) => entries
		.OrderByDescending(entry => entry.Date)
		.ThenBy(entry => entry.Component, StringComparer.Ordinal)
		.ToList();

	/// <summary>What a component's history covers: every branch and tag (null), or the one branch asked for.</summary>
	public static async Task<IReadOnlyList<string>?> ScopeAsync(IComponentStore store, string label, string? branch)
	{
		if (branch is null)
		{
			return null;
		}

		return await store.BranchTipAsync(branch) is not null
			? [$"refs/heads/{branch}"]
			: throw new MonorepoException($"{label} has no branch '{branch}'.");
	}

	/// <summary>A store's history (<paramref name="query"/>: scope, limits, filters), each commit with the provenance its message records.</summary>
	public static async Task<IReadOnlyList<TimelineEntry>> LogAsync(IComponentStore store, string component, LogQuery query) =>
		Entries(await store.LogAsync(query), component);

	/// <summary>History as timeline entries: the refs are the branches and tags, as the decorations name them.</summary>
	public static IReadOnlyList<TimelineEntry> Entries(IEnumerable<LogEntry> log, string component) => log
		.Select(entry => new TimelineEntry(component, entry.Commit.Id, entry.Date, Author(entry.Commit.Author), entry.Commit.Subject,
			[.. entry.Branches, .. entry.Tags], CommitProvenance.Parse(Body(entry.Commit.Message))))
		.ToList();

	/// <summary>The author's name, without the email.</summary>
	private static string Author(string? author) =>
		author is null ? "" : author.IndexOf(" <", StringComparison.Ordinal) is var at and >= 0 ? author[..at] : author;

	/// <summary>A message without its subject line: what a run's or integration's commit records its provenance in.</summary>
	private static string Body(string message) =>
		message.IndexOf('\n') is var newline and >= 0 ? message[(newline + 1)..].TrimStart('\n') : "";
}
