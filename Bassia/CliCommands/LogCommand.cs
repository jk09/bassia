namespace Bassia;

using System.Globalization;
using System.Text;
using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Web;

/// <summary>
/// <c>bassia log</c>: history with an ASCII graph. Without <c>-component</c> it is the meta-repo's history (the
/// monorepo's registrations and configuration); with it, the combined timeline of the chosen components and the
/// components they depend on - the unit a run over them selects, as on the web dashboard's timeline. One component
/// gets git's own commit graph; several get one lane per component.
/// </summary>
internal static class LogCommand
{
	private const char Separator = '\u001f';

	public static async Task<int> RunAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var limit = invocation.Int("limit", 20, 1, Timeline.MaxPageSize);
		var page = invocation.Int("page", 1, 1);
		var chosen = invocation.List("component");

		if (chosen.Count == 0)
		{
			return await SingleRepoAsync(invocation, monorepo.MetaRepoDir, "meta-repo", null, limit, page);
		}

		var unit = invocation.Has("only")
			? chosen.Select(name => ComponentCommands.RequireComponent(monorepo, name).Name).Distinct(StringComparer.Ordinal).ToList()
			: Timeline.Unit(monorepo, chosen);
		if (unit.Count == 1)
		{
			return await SingleRepoAsync(invocation, ComponentCommands.RequireRepo(monorepo, unit[0]), unit[0], unit, limit, page);
		}

		var timeline = await Timeline.ReadAsync(monorepo, unit, page, limit);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"{timeline.Entries.Count} commit(s) of {string.Join(", ", unit)}, page {page}{(timeline.HasMore ? $"; more with -page {page + 1}" : "")}.",
			new Dictionary<string, object?>
			{
				["components"] = unit.ToList(),
				["page"] = page,
				["limit"] = limit,
				["has_more"] = timeline.HasMore,
				["graph"] = new TomlText(Lanes(unit, timeline.Entries)),
				["commit"] = timeline.Entries.Select(entry => Commit(entry.Component, entry.Hash, entry.Date, entry.Author, entry.Subject, entry.Refs)).ToList()
			});
	}

	/// <summary>One repository's history: git's <c>--graph</c> drawing plus the commits as tables.</summary>
	private static async Task<int> SingleRepoAsync(Invocation invocation, string repoDir, string label, IReadOnlyList<string>? unit, int limit, int page)
	{
		var git = GitClient.In(repoDir);
		string[] scope = unit is null ? ["HEAD"] : ["--branches", "--tags"];
		var skip = (page - 1) * limit;
		var drawing = await git.RunAsync(["log", "--graph", "--oneline", "--decorate", "--date-order", $"-n{limit}", $"--skip={skip}", .. scope]);
		var records = await git.RunAsync(["log", "--date-order", $"-n{limit + 1}", $"--skip={skip}",
			$"--format=%H{Separator}%ct{Separator}%an{Separator}%s{Separator}%D", .. scope]);
		if (records.ExitCode != 0 && !records.Error.Contains("does not have any commits", StringComparison.Ordinal))
		{
			throw new GitException($"git log failed in '{repoDir}': {records.Error.Trim()}");
		}

		var commits = new List<IReadOnlyDictionary<string, object?>>();
		foreach (var line in records.Output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var fields = line.Split(Separator);
			if (fields.Length >= 5 && long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
			{
				commits.Add(Commit(unit is null ? null : label, fields[0], DateTimeOffset.FromUnixTimeSeconds(seconds), fields[2], fields[3], Refs(fields[4])));
			}
		}

		var hasMore = commits.Count > limit;
		commits = commits.Take(limit).ToList();
		return ProgramCli.WriteResult(true, invocation.Command,
			$"{commits.Count} commit(s) of {label}, page {page}{(hasMore ? $"; more with -page {page + 1}" : "")}.",
			new Dictionary<string, object?>
			{
				["repository"] = repoDir,
				["components"] = unit?.ToList(),
				["page"] = page,
				["limit"] = limit,
				["has_more"] = hasMore,
				["graph"] = new TomlText(drawing.ExitCode == 0 && drawing.Output.Trim().Length > 0 ? AsciiLines(drawing.Output) : "(no commits)"),
				["commit"] = commits
			});
	}

	private static Dictionary<string, object?> Commit(string? component, string hash, DateTimeOffset date, string author, string subject, IReadOnlyList<string> refs) => new()
	{
		["component"] = component,
		["hash"] = hash,
		["date"] = date.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
		["author"] = author,
		["subject"] = subject,
		["refs"] = refs.ToList()
	};

	/// <summary>
	/// One column per component, a <c>*</c> in the column of the component a commit belongs to and a <c>|</c> in
	/// every column whose component still has commits further down the page, then the commit itself:
	/// <code>
	/// app lib
	///  *   |   2026-09-21 14:02  1a2b3c4d5e  add the changelog  (agent/run-3f2a91c4/0)
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

	/// <summary><c>HEAD -> main, tag: v0, agent/run-x</c> -> <c>main</c>, <c>v0</c>, <c>agent/run-x</c>.</summary>
	private static IReadOnlyList<string> Refs(string decoration) =>
		decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(entry => entry.StartsWith("HEAD -> ", StringComparison.Ordinal) ? entry["HEAD -> ".Length..] : entry)
			.Select(entry => entry.StartsWith("tag: ", StringComparison.Ordinal) ? entry["tag: ".Length..] : entry)
			.Where(entry => entry != "HEAD")
			.ToList();
}
