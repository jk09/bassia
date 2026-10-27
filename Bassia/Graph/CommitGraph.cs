namespace Bassia.Graph;

using System.Text;
using Bassia.Storage;

/// <summary>
/// A history drawn as text, one line per commit, the way <c>git log --graph --oneline --decorate</c> draws it: a
/// <c>*</c> in the commit's column, a <c>|</c> in every other open line of descent, <c>\</c> where a merge opens one
/// and <c>/</c> where two meet again; then the short hash, the refs pointing at it and the subject.
/// </summary>
internal static class CommitGraph
{
	public static string Render(IReadOnlyList<LogEntry> entries, string? defaultBranch)
	{
		var builder = new StringBuilder();
		var columns = new List<string>();
		foreach (var entry in entries)
		{
			var id = entry.Commit.Id;
			var column = columns.IndexOf(id);
			if (column < 0)
			{
				columns.Add(id);
				column = columns.Count - 1;
			}

			builder.Append(Row(columns.Count, column, '*')).Append(' ').Append(Describe(entry, defaultBranch)).Append('\n');

			// The commit's line continues into its first parent; further parents open lines of their own.
			var parents = entry.Commit.Parents;
			if (parents.Count == 0)
			{
				columns.RemoveAt(column);
				continue;
			}

			columns[column] = parents[0];
			var opened = new List<int>();
			foreach (var parent in parents.Skip(1).Where(parent => !columns.Contains(parent)))
			{
				columns.Insert(column + 1 + opened.Count, parent);
				opened.Add(column + 1 + opened.Count);
			}

			if (opened.Count > 0)
			{
				builder.Append(Connector(columns.Count, opened, '\\')).Append('\n');
			}

			// Two lines waiting for the same commit meet: the right one bends into the left.
			for (var right = columns.Count - 1; right > 0; right--)
			{
				var left = columns.IndexOf(columns[right]);
				if (left < right)
				{
					builder.Append(Connector(columns.Count, [right], '/')).Append('\n');
					columns.RemoveAt(right);
				}
			}
		}

		return builder.ToString().TrimEnd('\n');
	}

	private static string Row(int count, int marked, char mark) =>
		string.Join(' ', Enumerable.Range(0, Math.Max(count, 1)).Select(column => column == marked ? mark : '|'));

	/// <summary>A row between two commits: a line in every column, except that the lines of <paramref name="bending"/> bend in the gap to their left.</summary>
	private static string Connector(int count, IReadOnlyList<int> bending, char mark)
	{
		var row = Enumerable.Repeat(' ', Math.Max(1, 2 * count - 1)).ToArray();
		for (var column = 0; column < count; column++)
		{
			if (bending.Contains(column))
			{
				row[Math.Max(0, 2 * column - 1)] = mark;
			}
			else
			{
				row[2 * column] = '|';
			}
		}

		return new string(row).TrimEnd();
	}

	/// <summary><c>1a2b3c4 (HEAD -&gt; main, tag: v1, topic) subject</c>.</summary>
	private static string Describe(LogEntry entry, string? defaultBranch)
	{
		var refs = new List<string>();
		if (defaultBranch is not null && entry.Branches.Contains(defaultBranch))
		{
			refs.Add($"HEAD -> {defaultBranch}");
		}

		refs.AddRange(entry.Tags.Select(tag => $"tag: {tag}"));
		refs.AddRange(entry.Branches.Where(branch => branch != defaultBranch));
		var hash = entry.Commit.Id[..Math.Min(7, entry.Commit.Id.Length)];
		return refs.Count == 0 ? $"{hash} {entry.Commit.Subject}" : $"{hash} ({string.Join(", ", refs)}) {entry.Commit.Subject}";
	}
}
