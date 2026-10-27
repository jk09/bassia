namespace Bassia;

using System.Globalization;
using System.Text;
using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Core;
using Bassia.Storage;
using Bassia.Web;

/// <summary>
/// <c>bassia log</c>: history with an ASCII graph, every commit attributed to the run or integration its message
/// records. Without <c>-component</c> it is the meta-repo's history (the monorepo's registrations and
/// configuration); with it, the combined timeline of the chosen components and the components they depend on - the
/// unit a run over them selects, as on the web dashboard's timeline. One component gets git's own commit graph;
/// several get one lane per component. <c>-branch</c> narrows each component to one branch, and <c>-run</c> reports
/// where runs' work went (see <see cref="RunsAsync"/>).
/// </summary>
internal static class LogCommand
{
	public static async Task<int> RunAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var limit = invocation.Int("limit", 20, 1, Timeline.MaxPageSize);
		var page = invocation.Int("page", 1, 1);
		var chosen = invocation.List("component");
		var branch = invocation.Get("branch");

		if (invocation.Has("run"))
		{
			if (branch is not null)
			{
				throw invocation.Usage("-branch cannot be combined with -run, which reports on each component's default branch itself.");
			}

			return await RunsAsync(invocation, monorepo, chosen, limit, page);
		}

		if (chosen.Count == 0)
		{
			return await MetaRepoAsync(invocation, monorepo, branch, limit, page);
		}

		var unit = invocation.Has("only")
			? chosen.Select(name => ComponentCommands.RequireComponent(monorepo, name).Name).Distinct(StringComparer.Ordinal).ToList()
			: Timeline.Unit(monorepo, chosen);
		if (unit.Count == 1)
		{
			ComponentCommands.RequireRepo(monorepo, unit[0]);
			return await SingleComponentAsync(invocation, monorepo.Backend.Components.Open(unit[0]), unit[0], branch, limit, page);
		}

		var timeline = await Timeline.ReadAsync(monorepo, unit, page, limit, branch);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"{timeline.Entries.Count} commit(s) of {string.Join(", ", unit)}{(branch is null ? "" : $" on {branch}")}, page {page}{(timeline.HasMore ? $"; more with -page {page + 1}" : "")}.",
			new Dictionary<string, object?>
			{
				["components"] = unit.ToList(),
				["branch"] = branch,
				["page"] = page,
				["limit"] = limit,
				["has_more"] = timeline.HasMore,
				["graph"] = new TomlText(Lanes(unit, timeline.Entries)),
				["commit"] = timeline.Entries.Select(entry => Commit(entry, entry.Component)).ToList()
			});
	}

	/// <summary>The meta-repo's history: its revisions drawn as a graph plus the commits as tables.</summary>
	private static async Task<int> MetaRepoAsync(Invocation invocation, Monorepo monorepo, string? branch, int limit, int page)
	{
		IReadOnlyList<LogEntry> log;
		try
		{
			log = await monorepo.Backend.Meta.LogAsync(new LogQuery(branch is null ? null : [$"refs/heads/{branch}"], (page - 1) * limit, limit + 1));
		}
		catch (StorageException) when (branch is not null)
		{
			throw new MonorepoException($"The meta-repo has no branch '{branch}'.");
		}

		return History(invocation, monorepo.Backend.Meta.Location, "meta-repo", null, branch, log, branch ?? log.FirstOrDefault()?.Branches.FirstOrDefault(), limit, page);
	}

	/// <summary>One component's history: its commit graph plus the commits as tables.</summary>
	private static async Task<int> SingleComponentAsync(Invocation invocation, IComponentStore store, string component, string? branch, int limit, int page)
	{
		var scope = await Timeline.ScopeAsync(store, $"Component '{component}'", branch);
		var log = await store.LogAsync(new LogQuery(scope, (page - 1) * limit, limit + 1));
		return History(invocation, store.Location, component, [component], branch, log, await store.DefaultBranchAsync(), limit, page);
	}

	private static int History(Invocation invocation, string location, string label, IReadOnlyList<string>? unit, string? branch, IReadOnlyList<LogEntry> log,
		string? defaultBranch, int limit, int page)
	{
		var shown = log.Take(limit).ToList();
		var hasMore = log.Count > limit;
		var commits = Timeline.Entries(shown, label).Select(entry => Commit(entry, unit is null ? null : label)).ToList();
		return ProgramCli.WriteResult(true, invocation.Command,
			$"{commits.Count} commit(s) of {label}{(branch is null ? "" : $" on {branch}")}, page {page}{(hasMore ? $"; more with -page {page + 1}" : "")}.",
			new Dictionary<string, object?>
			{
				["repository"] = location,
				["components"] = unit?.ToList(),
				["branch"] = branch,
				["page"] = page,
				["limit"] = limit,
				["has_more"] = hasMore,
				["graph"] = new TomlText(shown.Count > 0 ? AsciiLines(Graph.CommitGraph.Render(shown, defaultBranch)) : "(no commits)"),
				["commit"] = commits
			});
	}

	// ----- bassia log -run -----

	/// <summary>
	/// Where runs' work went: in every component the runs touched (or the ones named), the runs' result commits and
	/// the integration merges that brought them in, each marked with whether it is on the component's default branch,
	/// and per run and component whether the result landed there and through which integration.
	/// </summary>
	private static async Task<int> RunsAsync(Invocation invocation, Monorepo monorepo, IReadOnlyList<string> chosen, int limit, int page)
	{
		var records = monorepo.RunRecords();
		var runs = (await IntegrationSupport.SelectRunsAsync(records, string.Join(",", invocation.List("run"))))
			.DistinctBy(run => run.RunId).OrderBy(run => run.Created, StringComparer.Ordinal).ToList();
		var runIds = runs.Select(run => run.RunId).ToHashSet(StringComparer.Ordinal);

		var names = chosen.Count > 0
			? chosen.Select(name => ComponentCommands.RequireComponent(monorepo, name).Name).ToHashSet(StringComparer.Ordinal)
			: runs.SelectMany(run => run.Components.Select(component => component.Name)).ToHashSet(StringComparer.Ordinal);
		var components = monorepo.Components.Select(component => component.Name).Where(names.Contains)
			.Concat(names.Where(name => monorepo.FindComponent(name) is null).Order(StringComparer.Ordinal))
			.ToList();

		var found = new List<(TimelineEntry Entry, bool OnDefault)>();
		var defaults = new Dictionary<string, string?>(StringComparer.Ordinal);
		var storage = monorepo.Backend.Components;
		foreach (var component in components)
		{
			if (!storage.Exists(component))
			{
				defaults[component] = null;
				continue;
			}

			var store = storage.Open(component);
			var defaultBranch = defaults[component] = await store.DefaultBranchAsync();
			var tip = defaultBranch is null ? null : await store.BranchTipAsync(defaultBranch);
			foreach (var entry in await Timeline.LogAsync(store, component, new LogQuery(Grep: runs.Select(run => run.RunId).ToList())))
			{
				if (entry.Provenance is { } provenance && runIds.Contains(provenance.RunId))
				{
					found.Add((entry, tip is not null && await store.IsAncestorAsync(entry.Hash, tip)));
				}
			}
		}

		var ordered = Timeline.Order(found.Select(item => item.Entry));
		var onDefault = found.ToDictionary(item => (item.Entry.Component, item.Entry.Hash), item => item.OnDefault);
		var shown = ordered.Skip((page - 1) * limit).Take(limit).ToList();
		var hasMore = ordered.Count > page * limit;

		var summaries = new List<IReadOnlyDictionary<string, object?>>();
		var results = 0;
		var landed = 0;
		foreach (var run in runs)
		{
			var entries = new List<IReadOnlyDictionary<string, object?>>();
			foreach (var component in run.Components.Where(component => names.Contains(component.Name)))
			{
				var defaultBranch = defaults.GetValueOrDefault(component.Name);
				var isResult = component.ResultStatus == ResultStatus.Pushed && component.ResultCommit is not null;
				var hasLanded = isResult && defaultBranch is not null && storage.Exists(component.Name)
					&& await storage.Open(component.Name).BranchTipAsync(defaultBranch) is { } tip
					&& await storage.Open(component.Name).IsAncestorAsync(component.ResultCommit!, tip);
				results += isResult ? 1 : 0;
				landed += hasLanded ? 1 : 0;
				entries.Add(new Dictionary<string, object?>
				{
					["name"] = component.Name,
					["result_status"] = component.ResultStatus.ToString().ToLowerInvariant(),
					["result_tag"] = component.ResultTag,
					["result_commit"] = component.ResultCommit,
					["default_branch"] = defaultBranch,
					["landed"] = hasLanded,
					["merged_by"] = found
						.Where(item => item.OnDefault && item.Entry.Component == component.Name && item.Entry.Provenance!.RunId == run.RunId
							&& item.Entry.Provenance.Kind == CommitProvenance.Integration)
						.Select(item => item.Entry.Provenance!.IntegrationId!)
						.Distinct(StringComparer.Ordinal)
						.ToList()
				});
			}

			summaries.Add(new Dictionary<string, object?>
			{
				["run_id"] = run.RunId,
				["short_id"] = RunMetadata.Key(run.RunId),
				["status"] = run.Status,
				["summary"] = AgentCommand.SummarizeCommand(run.Command),
				["component"] = entries
			});
		}

		return ProgramCli.WriteResult(true, invocation.Command,
			$"{ordered.Count} commit(s) of {runs.Count} run(s) in {(components.Count == 0 ? "no component" : string.Join(", ", components))}; " +
			$"{landed} of {results} result(s) landed on their component's default branch{(hasMore ? $"; more commits with -page {page + 1}" : "")}.",
			new Dictionary<string, object?>
			{
				["runs"] = runs.Select(run => run.RunId).ToList(),
				["components"] = components,
				["page"] = page,
				["limit"] = limit,
				["has_more"] = hasMore,
				["results"] = results,
				["landed"] = landed,
				["graph"] = new TomlText(Lanes(components, shown)),
				["commit"] = shown.Select(entry => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(Commit(entry, entry.Component))
				{
					["default_branch"] = defaults.GetValueOrDefault(entry.Component),
					["on_default_branch"] = onDefault[(entry.Component, entry.Hash)]
				}).ToList(),
				["run"] = summaries
			});
	}

	/// <summary>A commit as the log reports it, with the run (and integration) its message records.</summary>
	private static Dictionary<string, object?> Commit(TimelineEntry entry, string? component) => new()
	{
		["component"] = component,
		["hash"] = entry.Hash,
		["date"] = entry.Date.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
		["author"] = entry.Author,
		["subject"] = entry.Subject,
		["refs"] = entry.Refs.ToList(),
		["kind"] = entry.Provenance?.Kind,
		["run_id"] = entry.Provenance?.RunId,
		["integration_id"] = entry.Provenance?.IntegrationId
	};

	/// <summary>
	/// One column per component, a <c>*</c> in the column of the component a commit belongs to and a <c>|</c> in
	/// every column whose component still has commits further down the page, then the commit itself:
	/// <code>
	/// app lib
	///  *   |   2026-09-21 14:02  1a2b3c4d5e  add the changelog  (agent-run/brave-otter-3f2a91/0)
	///  |   *   2026-09-21 13:40  9f8e7d6c5b  fix the parser
	/// </code>
	/// </summary>
	internal static string Lanes(IReadOnlyList<string> unit, IReadOnlyList<TimelineEntry> entries)
	{
		var widths = unit.Select(name => Math.Max(3, name.Length + 1)).ToList();
		var builder = new StringBuilder();
		builder.Append(string.Concat(unit.Select((name, lane) => AsciiTable.Ascii(name).PadRight(widths[lane]))).TrimEnd()).Append('\n');
		if (entries.Count == 0)
		{
			return builder.Append("(no commits)").ToString();
		}

		var last = unit.ToDictionary(name => name, name => entries.ToList().FindLastIndex(entry => entry.Component == name), StringComparer.Ordinal);
		var first = unit.ToDictionary(name => name, name => entries.ToList().FindIndex(entry => entry.Component == name), StringComparer.Ordinal);
		for (var row = 0; row < entries.Count; row++)
		{
			var entry = entries[row];
			for (var lane = 0; lane < unit.Count; lane++)
			{
				var name = unit[lane];
				var mark = entry.Component == name ? '*' : first[name] >= 0 && first[name] < row && row < last[name] ? '|' : ' ';
				builder.Append(' ').Append(mark).Append(new string(' ', widths[lane] - 2));
			}

			builder.Append(' ').Append(entry.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
				.Append("  ").Append(entry.ShortHash)
				.Append("  ").Append(AsciiTable.Ascii(entry.Subject));
			if (entry.Refs.Count > 0)
			{
				builder.Append("  (").Append(AsciiTable.Ascii(string.Join(", ", entry.Refs))).Append(')');
			}

			builder.Append('\n');
		}

		return builder.ToString().TrimEnd('\n');
	}

	private static string AsciiLines(string text) =>
		string.Join("\n", text.Replace("\r", "").TrimEnd('\n').Split('\n').Select(line => AsciiTable.Ascii(line).TrimEnd()));
}
