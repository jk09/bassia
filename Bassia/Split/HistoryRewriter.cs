namespace Bassia.Split;

using System.Text;

/// <summary>A lightweight tag of the split branch: its name and the stream index of its commit.</summary>
internal sealed record LightweightTag(string Name, int Target);

/// <summary>What <see cref="HistoryRewriter"/> wrote for one part.</summary>
internal sealed record RewrittenHistory(byte[] Stream, int Commits, IReadOnlyList<string> Tags, IReadOnlyList<string> DroppedTags);

/// <summary>
/// Rewrites the source's history for one part, as a <c>git fast-import</c> stream: every commit that changes one of
/// the part's files, reduced to those files, with the original author, committer and message (plus a
/// <c>Split-from</c> trailer naming the original commit). Commits that change none of its files are left out, and a
/// merge whose parents collapsed into one line of history becomes an ordinary commit, as <c>git filter-repo</c> does.
/// The stream ends with the tags that still have a commit, and the split record commit and tag.
/// </summary>
internal sealed class HistoryRewriter(FastExport export, Ownership ownership, Func<int, int, Task<List<FileChange>>> diff)
{
	/// <summary>
	/// Builds the stream for <paramref name="part"/>. <paramref name="diff"/> returns a commit's changes relative to
	/// another of its parents (stream indices), for merges whose first parent was left out.
	/// </summary>
	public async Task<RewrittenHistory> RewriteAsync(
		int part,
		string branch,
		string trailerPrefix,
		int tip,
		IReadOnlyList<LightweightTag> lightweightTags,
		string ident,
		string recordMessage,
		string recordTag)
	{
		var branchRef = $"refs/heads/{branch}";
		var stream = new MemoryStream();
		var map = new int[export.Commits.Count];
		var parentsOf = new Dictionary<int, int[]>();
		var generation = new Dictionary<int, int>();
		var nextMark = 1;
		var written = 0;

		foreach (var commit in export.Commits)
		{
			var candidates = commit.Parents
				.Select(parent => (Original: parent, Mark: map[parent]))
				.Where(parent => parent.Mark != 0)
				.DistinctBy(parent => parent.Mark)
				.ToList();
			var kept = candidates
				.Where(parent => !candidates.Any(other => other.Mark != parent.Mark && IsAncestor(parent.Mark, other.Mark, parentsOf, generation)))
				.ToList();

			// Changes are relative to the first parent. When the part's first parent comes from another parent of the
			// original (the first one had nothing of the part, or collapsed into the other), they are recomputed.
			var changes = kept.Count == 0 || kept[0].Original == commit.Parents[0]
				? commit.Changes
				: await diff(kept[0].Original, commit.Index);
			var mine = changes.Where(change => ownership.Belongs(part, change.Path, commit.Index)).ToList();

			if (mine.Count == 0 && kept.Count < 2)
			{
				map[commit.Index] = kept.Count == 0 ? 0 : kept[0].Mark;
				continue;
			}

			var mark = nextMark++;
			map[commit.Index] = mark;
			parentsOf[mark] = kept.Select(parent => parent.Mark).ToArray();
			generation[mark] = 1 + kept.Select(parent => generation[parent.Mark]).DefaultIfEmpty(0).Max();
			written++;

			if (kept.Count == 0)
			{
				Write(stream, $"reset {branchRef}\n");
			}

			Write(stream, $"commit {branchRef}\nmark :{mark}\n");
			Write(stream, "author ");
			stream.Write(commit.Author);
			Write(stream, "\ncommitter ");
			stream.Write(commit.Committer);
			Write(stream, "\n");
			WriteData(stream, WithTrailer(commit.Message, $"{trailerPrefix}{commit.OriginalId}"));
			for (var i = 0; i < kept.Count; i++)
			{
				Write(stream, $"{(i == 0 ? "from" : "merge")} :{kept[i].Mark}\n");
			}

			foreach (var change in mine)
			{
				Write(stream, change.Kind == 'M'
					? $"M {change.Mode} {change.Object} {PathQuoting.Quote(change.Path)}\n"
					: $"D {PathQuoting.Quote(change.Path)}\n");
			}

			Write(stream, "\n");
		}

		var tipMark = map[tip];
		if (tipMark == 0)
		{
			throw new InvalidOperationException("The part has no commit at the tip of the split branch.");
		}

		var tags = new List<string>();
		var dropped = new List<string>();
		foreach (var tag in export.Tags)
		{
			if (map[tag.Target] == 0)
			{
				dropped.Add(tag.Name);
				continue;
			}

			tags.Add(tag.Name);
			Write(stream, $"tag {tag.Name}\nfrom :{map[tag.Target]}\ntagger ");
			stream.Write(tag.Tagger.Length > 0 ? tag.Tagger : Encoding.UTF8.GetBytes(ident));
			Write(stream, "\n");
			WriteData(stream, tag.Message);
		}

		foreach (var tag in lightweightTags)
		{
			if (map[tag.Target] == 0)
			{
				dropped.Add(tag.Name);
				continue;
			}

			tags.Add(tag.Name);
			Write(stream, $"reset refs/tags/{tag.Name}\nfrom :{map[tag.Target]}\n\n");
		}

		// The split record: a commit without changes on top of the part's history, and the split tag on it.
		var recordMark = nextMark;
		Write(stream, $"commit {branchRef}\nmark :{recordMark}\nauthor {ident}\ncommitter {ident}\n");
		WriteData(stream, Encoding.UTF8.GetBytes(recordMessage));
		Write(stream, $"from :{tipMark}\n\n");
		Write(stream, $"tag {recordTag}\nfrom :{recordMark}\ntagger {ident}\n");
		WriteData(stream, Encoding.UTF8.GetBytes(recordMessage));

		return new RewrittenHistory(stream.ToArray(), written, tags, dropped);
	}

	/// <summary>Whether <paramref name="ancestor"/> is reachable from <paramref name="descendant"/> in the rewritten history.</summary>
	private static bool IsAncestor(int ancestor, int descendant, Dictionary<int, int[]> parentsOf, Dictionary<int, int> generation)
	{
		if (generation[ancestor] >= generation[descendant])
		{
			return false;
		}

		var seen = new HashSet<int>();
		var pending = new Stack<int>([descendant]);
		while (pending.Count > 0)
		{
			foreach (var parent in parentsOf[pending.Pop()])
			{
				if (parent == ancestor)
				{
					return true;
				}

				// Generations fall along every parent edge: below the ancestor's generation it cannot be reached.
				if (generation[parent] > generation[ancestor] && seen.Add(parent))
				{
					pending.Push(parent);
				}
			}
		}

		return false;
	}

	/// <summary>
	/// Appends <paramref name="trailer"/> to a commit message: into its trailer block when the last paragraph is one
	/// (e.g. <c>Co-Authored-By:</c> lines), otherwise as a new paragraph.
	/// </summary>
	internal static byte[] WithTrailer(byte[] message, string trailer)
	{
		var text = Encoding.UTF8.GetString(message).TrimEnd('\n', '\r', ' ');
		if (text.Length == 0)
		{
			return Encoding.UTF8.GetBytes(trailer + "\n");
		}

		var paragraphs = text.Replace("\r\n", "\n").Split("\n\n");
		var last = paragraphs[^1].Split('\n');
		var inTrailerBlock = paragraphs.Length > 1 && last.All(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^[A-Za-z0-9][A-Za-z0-9-]*: "));
		return Encoding.UTF8.GetBytes(text + (inTrailerBlock ? "\n" : "\n\n") + trailer + "\n");
	}

	private static void Write(Stream stream, string text) => stream.Write(Encoding.UTF8.GetBytes(text));

	/// <summary>A counted <c>data</c> block and the optional line feed after it, which also ends a tag.</summary>
	private static void WriteData(Stream stream, byte[] data)
	{
		Write(stream, $"data {data.Length}\n");
		stream.Write(data);
		Write(stream, "\n");
	}
}
