namespace Bassia.Split;

using System.Text;

/// <summary>A folder of a component, to the survey's depth: its files and bytes at the tip, and the commits that touched it.</summary>
internal sealed record SurveyFolder(string Path, int Files, long Bytes, int Commits);

/// <summary>Two folders changed together by <c>Commits</c> commits.</summary>
internal sealed record SurveyPair(string First, string Second, int Commits);

internal sealed record SurveyResult(
	string Branch,
	int Files,
	long Bytes,
	int CommitsRead,
	IReadOnlyList<SurveyFolder> Folders,
	IReadOnlyList<TipFile> TopLevelFiles,
	IReadOnlyList<SurveyPair> Pairs,
	string PlanSkeleton);

/// <summary>
/// What an LLM needs to propose a split of a component: its folders with their size and how often they change, the
/// folders that change together (a split between them would turn one change into several), and a plan skeleton to
/// fill in. Read in two passes over git (<c>ls-tree</c> of the tip, <c>log --name-only</c> of the history).
/// </summary>
internal static class ComponentSurvey
{
	public static async Task<SurveyResult> RunAsync(string repository, string component, string branch, int depth, int commitLimit, int pairLimit)
	{
		var tip = await ComponentSplitter.ListTipAsync(repository, branch);
		var folders = new Dictionary<string, (int Files, long Bytes, int Commits)>(StringComparer.Ordinal);
		foreach (var file in tip.Where(file => file.Path.Contains('/')))
		{
			var key = FolderOf(file.Path, depth);
			var entry = folders.GetValueOrDefault(key);
			folders[key] = (entry.Files + 1, entry.Bytes + file.Size, entry.Commits);
		}

		var arguments = new List<string> { "log", "--no-renames", "--name-only", "-z", "--format=%x01%H", $"refs/heads/{branch}" };
		if (commitLimit > 0)
		{
			arguments.Insert(1, $"--max-count={commitLimit}");
		}

		var pairs = new Dictionary<(string, string), int>();
		var commits = 0;
		var touched = new HashSet<string>(StringComparer.Ordinal);
		void Flush()
		{
			var list = touched.Where(folders.ContainsKey).OrderBy(folder => folder, StringComparer.Ordinal).ToList();
			foreach (var folder in list)
			{
				var entry = folders[folder];
				folders[folder] = entry with { Commits = entry.Commits + 1 };
			}

			// Sweeping changes (a rename of everything, a reformat) say nothing about coupling.
			if (list.Count <= 20)
			{
				for (var i = 0; i < list.Count; i++)
				{
					for (var j = i + 1; j < list.Count; j++)
					{
						pairs[(list[i], list[j])] = pairs.GetValueOrDefault((list[i], list[j])) + 1;
					}
				}
			}

			touched.Clear();
		}

		foreach (var field in GitBinary.SplitNul(await GitBinary.RunAsync(repository, arguments)))
		{
			var value = field.TrimStart('\n');
			if (value.StartsWith('\x01'))
			{
				if (commits++ > 0)
				{
					Flush();
				}
			}
			else if (value.Contains('/'))
			{
				touched.Add(FolderOf(value, depth));
			}
		}

		if (commits > 0)
		{
			Flush();
		}

		var surveyed = folders
			.Select(entry => new SurveyFolder(entry.Key, entry.Value.Files, entry.Value.Bytes, entry.Value.Commits))
			.OrderBy(folder => folder.Path, StringComparer.Ordinal)
			.ToList();
		var coupled = pairs
			.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key.Item1, StringComparer.Ordinal).ThenBy(pair => pair.Key.Item2, StringComparer.Ordinal)
			.Take(pairLimit)
			.Select(pair => new SurveyPair(pair.Key.Item1, pair.Key.Item2, pair.Value))
			.ToList();
		var topLevel = tip.Where(file => !file.Path.Contains('/')).ToList();

		return new SurveyResult(branch, tip.Count, tip.Sum(file => file.Size), commits, surveyed, topLevel, coupled,
			Skeleton(component, branch, tip, topLevel));
	}

	/// <summary>The first <paramref name="depth"/> folders of a file's path.</summary>
	private static string FolderOf(string path, int depth)
	{
		var segments = path.Split('/');
		return string.Join('/', segments.Take(Math.Min(depth, segments.Length - 1)));
	}

	/// <summary>A plan to start from: one part per top-level folder, the top-level files shared.</summary>
	private static string Skeleton(string component, string branch, IReadOnlyList<TipFile> tip, IReadOnlyList<TipFile> topLevel)
	{
		var builder = new StringBuilder()
			.Append("# Split plan for '").Append(component).Append("'. Allocate every file at the tip to one part, or to 'shared' / 'drop'.\n")
			.Append("# Check it with: bassia component split -plan <this file> -dry-run\n")
			.Append("source = ").Append(ConfigFile.Quote(component)).Append('\n')
			.Append("branch = ").Append(ConfigFile.Quote(branch)).Append('\n')
			.Append("shared = [").Append(string.Join(", ", topLevel.Select(file => ConfigFile.Quote(file.Path)))).Append("]\n")
			.Append("drop = []\n");

		foreach (var folder in tip.Where(file => file.Path.Contains('/')).Select(file => file.Path.Split('/')[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
		{
			var name = new string(folder.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
			builder.Append("\n[[part]]\n")
				.Append("name = ").Append(ConfigFile.Quote($"{component}-{(name.Length == 0 ? "part" : name)}")).Append('\n')
				.Append("paths = [").Append(ConfigFile.Quote(folder + "/")).Append("]\n")
				.Append("# references = []  # default: the references of '").Append(component).Append("'\n");
		}

		return builder.ToString();
	}
}
