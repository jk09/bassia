namespace Bassia.Core.Text;

using System.Text;

/// <summary>A run of lines the two sides share (<see cref="Equal"/>) or that only one side has.</summary>
public sealed record DiffOp(DiffOpKind Kind, int OldStart, int OldCount, int NewStart, int NewCount);

public enum DiffOpKind
{
	Equal,
	Delete,
	Insert,
	Replace
}

/// <summary>
/// Line-level diffs (Myers' O(ND) algorithm with common prefix and suffix trimmed), unified hunks, and a similarity
/// ratio. Hunks are produced lazily: only when a review asks for them, never to decide what changed.
/// </summary>
public static class LineDiff
{
	public static string[] Lines(string text)
	{
		if (text.Length == 0)
		{
			return [];
		}

		var lines = text.Split('\n');
		if (lines[^1].Length == 0)
		{
			lines = lines[..^1];
		}

		return lines.Select(line => line.TrimEnd('\r')).ToArray();
	}

	public static string[] Lines(ReadOnlySpan<byte> bytes) => Lines(Encoding.UTF8.GetString(bytes));

	/// <summary>Whether bytes look like text: no NUL in the first 8 KiB, and valid UTF-8.</summary>
	public static bool IsText(ReadOnlySpan<byte> bytes)
	{
		if (bytes[..Math.Min(bytes.Length, 8192)].Contains((byte)0))
		{
			return false;
		}

		try
		{
			new UTF8Encoding(false, true).GetString(bytes);
			return true;
		}
		catch (DecoderFallbackException)
		{
			return false;
		}
	}

	/// <summary>The matching line pairs (old index, new index) of a longest common subsequence.</summary>
	public static IReadOnlyList<(int Old, int New)> Matches(IReadOnlyList<string> a, IReadOnlyList<string> b)
	{
		var prefix = 0;
		while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
		{
			prefix++;
		}

		var suffix = 0;
		while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
		{
			suffix++;
		}

		var result = new List<(int, int)>();
		for (var index = 0; index < prefix; index++)
		{
			result.Add((index, index));
		}

		var middle = Myers(a, prefix, a.Count - suffix, b, prefix, b.Count - suffix);
		result.AddRange(middle);
		for (var index = suffix; index > 0; index--)
		{
			result.Add((a.Count - index, b.Count - index));
		}

		return result;
	}

	private static List<(int, int)> Myers(IReadOnlyList<string> a, int aStart, int aEnd, IReadOnlyList<string> b, int bStart, int bEnd)
	{
		var n = aEnd - aStart;
		var m = bEnd - bStart;
		var result = new List<(int, int)>();
		if (n == 0 || m == 0)
		{
			return result;
		}

		var max = n + m;
		var offset = max;
		var v = new int[2 * max + 2];
		var trace = new List<int[]>();
		var found = false;
		for (var d = 0; d <= max && !found; d++)
		{
			trace.Add((int[])v.Clone());
			for (var k = -d; k <= d; k += 2)
			{
				int x;
				if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
				{
					x = v[offset + k + 1];
				}
				else
				{
					x = v[offset + k - 1] + 1;
				}

				var y = x - k;
				while (x < n && y < m && a[aStart + x] == b[bStart + y])
				{
					x++;
					y++;
				}

				v[offset + k] = x;
				if (x >= n && y >= m)
				{
					found = true;
					break;
				}
			}
		}

		// Walk the trace backwards to recover the snakes (diagonal runs are matches).
		var px = n;
		var py = m;
		for (var d = trace.Count - 1; d >= 0 && (px > 0 || py > 0); d--)
		{
			var vd = trace[d];
			var k = px - py;
			int prevK;
			if (k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1]))
			{
				prevK = k + 1;
			}
			else
			{
				prevK = k - 1;
			}

			var prevX = vd[offset + prevK];
			var prevY = prevX - prevK;
			while (px > prevX && py > prevY)
			{
				px--;
				py--;
				result.Add((aStart + px, bStart + py));
			}

			if (d > 0)
			{
				px = prevX;
				py = prevY;
			}
		}

		result.Reverse();
		return result;
	}

	/// <summary>The diff as runs of equal, deleted, inserted and replaced lines.</summary>
	public static IReadOnlyList<DiffOp> Ops(IReadOnlyList<string> a, IReadOnlyList<string> b)
	{
		var ops = new List<DiffOp>();
		int ai = 0, bi = 0;
		foreach (var (oldIndex, newIndex) in Matches(a, b).Append((a.Count, b.Count)))
		{
			if (oldIndex > ai || newIndex > bi)
			{
				var kind = oldIndex > ai && newIndex > bi ? DiffOpKind.Replace : oldIndex > ai ? DiffOpKind.Delete : DiffOpKind.Insert;
				ops.Add(new DiffOp(kind, ai, oldIndex - ai, bi, newIndex - bi));
			}

			if (oldIndex < a.Count)
			{
				if (ops.Count > 0 && ops[^1].Kind == DiffOpKind.Equal && ops[^1].OldStart + ops[^1].OldCount == oldIndex)
				{
					var last = ops[^1];
					ops[^1] = last with { OldCount = last.OldCount + 1, NewCount = last.NewCount + 1 };
				}
				else
				{
					ops.Add(new DiffOp(DiffOpKind.Equal, oldIndex, 1, newIndex, 1));
				}
			}

			ai = oldIndex + 1;
			bi = newIndex + 1;
		}

		return ops;
	}

	/// <summary>2 * matching lines / all lines: 1 for equal texts, 0 for texts with nothing in common.</summary>
	public static double Similarity(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
		a.Count + b.Count == 0 ? 1 : 2.0 * Matches(a, b).Count / (a.Count + b.Count);

	/// <summary>Added and deleted line counts.</summary>
	public static (int Added, int Deleted) Stat(IReadOnlyList<string> a, IReadOnlyList<string> b)
	{
		var matched = Matches(a, b).Count;
		return (b.Count - matched, a.Count - matched);
	}

	/// <summary>A unified diff with <paramref name="context"/> lines around each change.</summary>
	public static string Unified(string oldPath, string newPath, IReadOnlyList<string> a, IReadOnlyList<string> b, int context = 3)
	{
		var ops = Ops(a, b);
		if (ops.All(op => op.Kind == DiffOpKind.Equal))
		{
			return "";
		}

		var text = new StringBuilder();
		text.Append("--- ").Append(oldPath).Append('\n').Append("+++ ").Append(newPath).Append('\n');
		var changes = ops.Where(op => op.Kind != DiffOpKind.Equal).ToList();
		var index = 0;
		while (index < changes.Count)
		{
			// Group changes whose context overlaps into one hunk.
			var first = changes[index];
			var last = first;
			while (index + 1 < changes.Count && changes[index + 1].OldStart - (last.OldStart + last.OldCount) <= 2 * context)
			{
				last = changes[++index];
			}

			index++;
			var oldFrom = Math.Max(0, first.OldStart - context);
			var oldTo = Math.Min(a.Count, last.OldStart + last.OldCount + context);
			var newFrom = Math.Max(0, first.NewStart - context);
			var newTo = Math.Min(b.Count, last.NewStart + last.NewCount + context);
			text.Append($"@@ -{oldFrom + 1},{oldTo - oldFrom} +{newFrom + 1},{newTo - newFrom} @@\n");
			var oi = oldFrom;
			var ni = newFrom;
			foreach (var op in ops.Where(op => op.OldStart + op.OldCount > oldFrom && op.OldStart < oldTo || op.Kind == DiffOpKind.Insert && op.OldStart >= oldFrom && op.OldStart <= oldTo))
			{
				if (op.Kind == DiffOpKind.Equal)
				{
					var from = Math.Max(op.OldStart, oi);
					var to = Math.Min(op.OldStart + op.OldCount, oldTo);
					for (var line = from; line < to; line++)
					{
						text.Append(' ').Append(a[line]).Append('\n');
					}

					oi = Math.Max(oi, to);
					ni = Math.Max(ni, op.NewStart + (to - op.OldStart));
				}
				else
				{
					for (var line = op.OldStart; line < op.OldStart + op.OldCount; line++)
					{
						text.Append('-').Append(a[line]).Append('\n');
					}

					for (var line = op.NewStart; line < op.NewStart + op.NewCount; line++)
					{
						text.Append('+').Append(b[line]).Append('\n');
					}

					oi = op.OldStart + op.OldCount;
					ni = op.NewStart + op.NewCount;
				}
			}
		}

		return text.ToString();
	}
}

/// <summary>The result of a three-way text merge: the merged lines and how many regions conflicted.</summary>
public sealed record TextMergeResult(IReadOnlyList<string> Lines, int Conflicts, int ChangedLines)
{
	public bool Clean => Conflicts == 0;

	public string Text => Lines.Count == 0 ? "" : string.Join('\n', Lines) + "\n";
}

/// <summary>
/// diff3: a line-based three-way merge. Regions only one side changed take that side; regions both changed the same
/// way are taken once; regions both changed differently conflict and are written with Jujutsu/git-style markers.
/// </summary>
public static class TextMerge
{
	public static TextMergeResult Merge(IReadOnlyList<string> baseLines, IReadOnlyList<string> ours, IReadOnlyList<string> theirs,
		string oursLabel = "workspace", string theirsLabel = "mainline")
	{
		var mapA = MatchMap(baseLines, ours);
		var mapB = MatchMap(baseLines, theirs);
		var result = new List<string>();
		var conflicts = 0;
		var changed = 0;
		int o = 0, a = 0, b = 0;
		while (true)
		{
			// Find the next base line from o on that is stable: matched in both sides at the expected offsets.
			var next = -1;
			for (var line = o; line < baseLines.Count; line++)
			{
				if (mapA[line] >= a && mapB[line] >= b)
				{
					next = line;
					break;
				}
			}

			var oEnd = next < 0 ? baseLines.Count : next;
			var aEnd = next < 0 ? ours.Count : mapA[next];
			var bEnd = next < 0 ? theirs.Count : mapB[next];
			var baseChunk = Slice(baseLines, o, oEnd);
			var aChunk = Slice(ours, a, aEnd);
			var bChunk = Slice(theirs, b, bEnd);
			if (baseChunk.Count > 0 || aChunk.Count > 0 || bChunk.Count > 0)
			{
				var aChanged = !aChunk.SequenceEqual(baseChunk);
				var bChanged = !bChunk.SequenceEqual(baseChunk);
				if (!aChanged)
				{
					result.AddRange(bChunk);
					changed += bChanged ? Math.Max(bChunk.Count, baseChunk.Count) : 0;
				}
				else if (!bChanged || aChunk.SequenceEqual(bChunk))
				{
					result.AddRange(aChunk);
					changed += Math.Max(aChunk.Count, baseChunk.Count);
				}
				else
				{
					conflicts++;
					changed += Math.Max(aChunk.Count, bChunk.Count);
					result.Add("<<<<<<< " + oursLabel);
					result.AddRange(aChunk);
					result.Add("||||||| base");
					result.AddRange(baseChunk);
					result.Add("=======");
					result.AddRange(bChunk);
					result.Add(">>>>>>> " + theirsLabel);
				}
			}

			if (next < 0)
			{
				break;
			}

			result.Add(baseLines[next]);
			o = next + 1;
			a = mapA[next] + 1;
			b = mapB[next] + 1;
		}

		return new TextMergeResult(result, conflicts, changed);
	}

	private static int[] MatchMap(IReadOnlyList<string> baseLines, IReadOnlyList<string> side)
	{
		var map = Enumerable.Repeat(-1, baseLines.Count).ToArray();
		foreach (var (oldIndex, newIndex) in LineDiff.Matches(baseLines, side))
		{
			map[oldIndex] = newIndex;
		}

		return map;
	}

	private static List<string> Slice(IReadOnlyList<string> lines, int from, int to) =>
		lines.Skip(from).Take(Math.Max(0, to - from)).ToList();

	/// <summary>The conflict markers <see cref="Merge"/> writes, for materializing a conflict in a workspace.</summary>
	public static string Materialize(string? baseText, IReadOnlyList<(string Label, string? Text)> sides)
	{
		var text = new StringBuilder();
		text.Append("<<<<<<< ").Append(sides[0].Label).Append('\n').Append(Body(sides[0].Text));
		text.Append("||||||| base\n").Append(Body(baseText));
		for (var index = 1; index < sides.Count; index++)
		{
			text.Append("=======\n").Append(Body(sides[index].Text));
		}

		text.Append(">>>>>>> ").Append(sides[^1].Label).Append('\n');
		return text.ToString();
	}

	private static string Body(string? text) =>
		text is null ? "" : text.EndsWith('\n') || text.Length == 0 ? text : text + "\n";
}
