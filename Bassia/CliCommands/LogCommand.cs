namespace Bassia;

using System.Globalization;
using System.Text;
using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Git;
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
			return await SingleRepoAsync(invocation, monorepo.MetaRepoDir, "meta-repo", null, branch, limit, page);
		}

		var unit = invocation.Has("only")
			? chosen.Select(name => ComponentCommands.RequireComponent(monorepo, name).Name).Distinct(StringComparer.Ordinal).ToList()
			: Timeline.Unit(monorepo, chosen);
		if (unit.Count == 1)
		{
			return await SingleRepoAsync(invocation, ComponentCommands.RequireRepo(monorepo, unit[0]), unit[0], unit, branch, limit, page);
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

	/// <summary>One repository's history: git's <c>--graph</c> drawing plus the commits as tables.</summary>
	private static async Task<int> SingleRepoAsync(Invocation invocation, string repoDir, string label, IReadOnlyList<string>? unit, string? branch, int limit, int page)
	{
		var git = GitClient.In(repoDir);
		IReadOnlyList<string> scope = branch is not null
			? await Timeline.ScopeAsync(git, unit is null ? "The meta-repo" : $"Component '{label}'", branch)
			: unit is null ? ["HEAD"] : ["--branches", "--tags"];
		var skip = (page - 1) * limit;
		var drawing = await git.RunAsync(["log", "--graph", "--oneline", "--decorate", "--date-order", $"-n{limit}", $"--skip={skip}", .. scope]);
		var entries = await Timeline.LogAsync(repoDir, label, ["--date-order", $"-n{limit + 1}", $"--skip={skip}", .. scope]);
		var hasMore = entries.Count > limit;
		var commits = entries.Take(limit).Select(entry => Commit(entry, unit is null ? null : label)).ToList();
		return ProgramCli.WriteResult(true, invocation.Command,
			$"{commits.Count} commit(s) of {label}{(branch is null ? "" : $" on {branch}")}, page {page}{(hasMore ? $"; more with -page {page + 1}" : "")}.",
			new Dictionary<string, object?>
			{
				["repository"] = repoDir,
				["components"] = unit?.ToList(),
				["branch"] = branch,
				["page"] = page,
				["limit"] = limit,
				["has_more"] = hasMore,
				["graph"] = new TomlText(drawing.ExitCode == 0 && drawing.Output.Trim().Length > 0 ? AsciiLines(drawing.Output) : "(no commits)"),
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
		var store = new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir);
		var runs = (await IntegrationSupport.SelectRunsAsync(store, string.Join(",", invocation.List("run"))))
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
		foreach (var component in components)
		{
			var repoDir = monorepo.SourceRepoDir(component);
			if (!Directory.Exists(repoDir))
			{
				defaults[component] = null;
				continue;
			}

			var git = GitClient.In(repoDir);
			var head = await git.RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"]);
			var defaultBranch = defaults[component] = head.ExitCode == 0 && head.Output.Trim().Length > 0 ? head.Output.Trim() : null;
			var grep = runs.Select(run => $"--grep={run.RunId}").ToList();
			foreach (var entry in await Timeline.LogAsync(repoDir, component, ["--branches", "--tags", "--date-order", "--fixed-strings", .. grep]))
			{
				if (entry.Provenance is { } provenance && runIds.Contains(provenance.RunId))
				{
					found.Add((entry, defaultBranch is not null && await IsAncestorAsync(git, entry.Hash, defaultBranch)));
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
				var repoDir = monorepo.SourceRepoDir(component.Name);
				var isResult = component.ResultStatus == ResultStatus.Pushed && component.ResultCommit is not null;
				var hasLanded = isResult && defaultBranch is not null && Directory.Exists(repoDir)
					&& await IsAncestorAsync(GitClient.In(repoDir), component.ResultCommit!, defaultBranch);
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

	private static async Task<bool> IsAncestorAsync(GitClient git, string ancestor, string descendant)
	{
		var result = await git.RunAsync(["merge-base", "--is-ancestor", ancestor, descendant]);
		return result.ExitCode switch
		{
			0 => true,
			1 => false,
			_ => throw new GitException($"git merge-base --is-ancestor failed in '{git.WorkingDirectory}': {result.Error.Trim()}")
		};
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
