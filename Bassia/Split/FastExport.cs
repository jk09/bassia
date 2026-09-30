namespace Bassia.Split;

using System.Text;
using Bassia.Git;

/// <summary>A file change of a commit relative to its first parent: <c>M</c> (add or modify) or <c>D</c> (delete).</summary>
internal readonly record struct FileChange(char Kind, string Mode, string Object, string Path);

/// <summary>
/// One commit of a <c>git fast-export --no-data</c> stream. <see cref="Index"/> is its position in the stream, which
/// is topological: every parent comes before its children. The author, committer and message are kept as the exact
/// bytes git wrote, so a rewritten commit carries them unchanged.
/// </summary>
internal sealed class ExportedCommit
{
	public int Index { get; init; }
	public required string OriginalId { get; init; }
	public required byte[] Author { get; init; }
	public required byte[] Committer { get; init; }
	public required byte[] Message { get; init; }

	/// <summary>Parents as stream indices, first parent first.</summary>
	public List<int> Parents { get; } = [];

	/// <summary>Changes relative to the first parent (to the empty tree for a root commit).</summary>
	public List<FileChange> Changes { get; } = [];
}

/// <summary>An annotated tag of the stream: its target commit (a stream index), tagger line and message bytes.</summary>
internal sealed record ExportedTag(string Name, int Target, byte[] Tagger, byte[] Message);

/// <summary>
/// The history of one branch (and its annotated tags) as read from <c>git fast-export</c> in a single pass:
/// commits with parents and file changes, no file contents.
/// </summary>
internal sealed class FastExport
{
	public List<ExportedCommit> Commits { get; } = [];
	public List<ExportedTag> Tags { get; } = [];
	public Dictionary<string, int> IndexOf { get; } = new(StringComparer.Ordinal);

	public static async Task<FastExport> ReadAsync(string repository, string branch, IReadOnlyList<string> annotatedTags)
	{
		var output = await GitBinary.RunAsync(repository,
		[
			"fast-export", "--no-data", "--show-original-ids", "--signed-tags=strip", "--reencode=yes", "--use-done-feature",
			$"refs/heads/{branch}", .. annotatedTags.Select(tag => $"refs/tags/{tag}")
		]);
		return Parse(output);
	}

	public static FastExport Parse(byte[] stream)
	{
		var export = new FastExport();
		var reader = new StreamReader(stream);
		var marks = new Dictionary<int, int>();

		while (reader.ReadLine() is { } line)
		{
			if (line.Length == 0 || line.StartsWith("feature ") || line.StartsWith("progress ") || line is "checkpoint" or "done")
			{
				continue;
			}

			if (line.StartsWith("reset "))
			{
				// A ref position ("reset <ref>" and an optional "from"): the caller maps refs itself.
				if (reader.PeekLine() is { } next && next.StartsWith("from "))
				{
					reader.ReadLine();
				}

				continue;
			}

			if (line.StartsWith("commit "))
			{
				export.ReadCommit(reader, marks);
			}
			else if (line.StartsWith("tag "))
			{
				export.ReadTag(line[4..], reader, marks);
			}
			else
			{
				throw new GitException($"Unexpected line in the fast-export stream: '{line}'.");
			}
		}

		return export;
	}

	private void ReadCommit(StreamReader reader, Dictionary<int, int> marks)
	{
		int? mark = null;
		string? originalId = null;
		byte[]? author = null, committer = null, message = null;
		var parents = new List<int>();
		var changes = new List<FileChange>();

		while (reader.ReadLine() is { } line && line.Length > 0)
		{
			if (line.StartsWith("mark :"))
			{
				mark = int.Parse(line[6..]);
			}
			else if (line.StartsWith("original-oid "))
			{
				originalId = line[13..];
			}
			else if (line.StartsWith("author "))
			{
				author = reader.LastLineBytes[7..];
			}
			else if (line.StartsWith("committer "))
			{
				committer = reader.LastLineBytes[10..];
			}
			else if (line.StartsWith("encoding "))
			{
				// --reencode=yes already converted the message to UTF-8.
			}
			else if (line.StartsWith("data "))
			{
				message = reader.ReadData(line);
			}
			else if (line.StartsWith("from ") || line.StartsWith("merge "))
			{
				parents.Add(ResolveMark(line[(line.IndexOf(' ') + 1)..], marks));
			}
			else if (line.StartsWith("M "))
			{
				// M <mode> <object> <path>
				var bytes = reader.LastLineBytes;
				var first = Array.IndexOf(bytes, (byte)' ', 2);
				var second = Array.IndexOf(bytes, (byte)' ', first + 1);
				changes.Add(new FileChange('M', line[2..first], Encoding.ASCII.GetString(bytes, first + 1, second - first - 1), PathQuoting.Unquote(bytes[(second + 1)..])));
			}
			else if (line.StartsWith("D "))
			{
				changes.Add(new FileChange('D', "", "", PathQuoting.Unquote(reader.LastLineBytes[2..])));
			}
			else if (line == "deleteall" || line.StartsWith("R ") || line.StartsWith("C ") || line.StartsWith("N "))
			{
				throw new GitException($"Unsupported fast-export command '{line.Split(' ')[0]}'.");
			}
			else
			{
				throw new GitException($"Unexpected line in a fast-export commit: '{line}'.");
			}
		}

		if (mark is null || originalId is null || committer is null || message is null)
		{
			throw new GitException("fast-export produced an incomplete commit.");
		}

		var commit = new ExportedCommit
		{
			Index = Commits.Count,
			OriginalId = originalId,
			Author = author ?? committer,
			Committer = committer,
			Message = message
		};
		commit.Parents.AddRange(parents);
		commit.Changes.AddRange(changes);
		marks[mark.Value] = commit.Index;
		IndexOf[originalId] = commit.Index;
		Commits.Add(commit);
	}

	private void ReadTag(string name, StreamReader reader, Dictionary<int, int> marks)
	{
		int? target = null;
		byte[]? tagger = null, message = null;
		while (reader.ReadLine() is { } line && line.Length > 0)
		{
			if (line.StartsWith("from "))
			{
				target = marks.TryGetValue(line.StartsWith("from :") && int.TryParse(line[6..], out var mark) ? mark : -1, out var index) ? index : null;
			}
			else if (line.StartsWith("tagger "))
			{
				tagger = reader.LastLineBytes[7..];
			}
			else if (line.StartsWith("data "))
			{
				message = reader.ReadData(line);
				// The message may be followed by the command's closing blank line; a tag has nothing after its data.
				break;
			}
		}

		// A tag of a tag, or of something that is not a commit of this history, has no commit target: it is left out.
		if (target is not null && message is not null)
		{
			Tags.Add(new ExportedTag(name, target.Value, tagger ?? [], message));
		}
	}

	private static int ResolveMark(string reference, Dictionary<int, int> marks) =>
		reference.StartsWith(':') && int.TryParse(reference[1..], out var mark) && marks.TryGetValue(mark, out var index)
			? index
			: throw new GitException($"fast-export referenced a parent outside the exported history: '{reference}'.");

	/// <summary>A line reader over the raw stream that can also hand out the exact bytes of a line and of a <c>data</c> block.</summary>
	private sealed class StreamReader(byte[] stream)
	{
		private int position;

		public byte[] LastLineBytes { get; private set; } = [];

		public string? ReadLine()
		{
			if (position >= stream.Length)
			{
				return null;
			}

			var end = Array.IndexOf(stream, (byte)'\n', position);
			end = end < 0 ? stream.Length : end;
			LastLineBytes = stream[position..end];
			position = Math.Min(end + 1, stream.Length);
			return Encoding.UTF8.GetString(LastLineBytes);
		}

		public string? PeekLine()
		{
			var saved = position;
			var savedBytes = LastLineBytes;
			var line = ReadLine();
			position = saved;
			LastLineBytes = savedBytes;
			return line;
		}

		/// <summary>The <c>data &lt;count&gt;</c> block that follows <paramref name="header"/>: exactly count bytes.</summary>
		public byte[] ReadData(string header)
		{
			if (!int.TryParse(header[5..], out var count) || count < 0 || position + count > stream.Length)
			{
				throw new GitException($"Unsupported fast-export data header '{header}'.");
			}

			var data = stream[position..(position + count)];
			position += count;
			return data;
		}
	}
}

/// <summary>Git's C-style path quoting, as <c>fast-export</c> writes it and <c>fast-import</c> reads it.</summary>
internal static class PathQuoting
{
	public static string Unquote(byte[] raw)
	{
		if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
		{
			return Encoding.UTF8.GetString(raw);
		}

		var bytes = new List<byte>(raw.Length);
		for (var i = 1; i < raw.Length - 1; i++)
		{
			if (raw[i] != '\\')
			{
				bytes.Add(raw[i]);
				continue;
			}

			var c = raw[++i];
			if (c is >= (byte)'0' and <= (byte)'7')
			{
				bytes.Add((byte)((c - '0') * 64 + (raw[i + 1] - '0') * 8 + (raw[i + 2] - '0')));
				i += 2;
				continue;
			}

			bytes.Add(c switch
			{
				(byte)'a' => 7,
				(byte)'b' => 8,
				(byte)'t' => 9,
				(byte)'n' => 10,
				(byte)'v' => 11,
				(byte)'f' => 12,
				(byte)'r' => 13,
				_ => c
			});
		}

		return Encoding.UTF8.GetString(bytes.ToArray());
	}

	/// <summary>Always quotes: accepted by <c>fast-import</c> for every path, and required for some.</summary>
	public static string Quote(string path)
	{
		var builder = new StringBuilder("\"");
		foreach (var c in path)
		{
			builder.Append(c switch
			{
				'"' => "\\\"",
				'\\' => "\\\\",
				'\n' => "\\n",
				'\t' => "\\t",
				'\r' => "\\r",
				_ when c < ' ' => $"\\{Convert.ToString(c, 8).PadLeft(3, '0')}",
				_ => c.ToString()
			});
		}

		return builder.Append('"').ToString();
	}
}
