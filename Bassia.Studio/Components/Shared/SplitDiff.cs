namespace Bassia.Studio.Components.Shared;

using Bassia.Core.Text;

/// <summary>One row of a side-by-side diff: a line on either side (or both), or a fold of unchanged lines.</summary>
public sealed record SplitRow(string Kind, int? OldNumber, string? OldText, int? NewNumber, string? NewText, int Folded = 0);

/// <summary>Builds side-by-side diff rows from two texts, folding unchanged runs longer than the context.</summary>
public static class SplitDiff
{
	public const string Equal = "equal";
	public const string Delete = "delete";
	public const string Insert = "insert";
	public const string Replace = "replace";
	public const string Fold = "fold";

	public static IReadOnlyList<SplitRow> Build(string oldText, string newText, int context = 3)
	{
		var a = LineDiff.Lines(oldText);
		var b = LineDiff.Lines(newText);
		var rows = new List<SplitRow>();
		var ops = LineDiff.Ops(a, b);
		for (var index = 0; index < ops.Count; index++)
		{
			var op = ops[index];
			if (op.Kind == DiffOpKind.Equal)
			{
				var head = index == 0 ? 0 : context;
				var tail = index == ops.Count - 1 ? 0 : context;
				if (op.OldCount > head + tail + 1)
				{
					for (var i = 0; i < head; i++)
					{
						rows.Add(new SplitRow(Equal, op.OldStart + i + 1, a[op.OldStart + i], op.NewStart + i + 1, b[op.NewStart + i]));
					}

					rows.Add(new SplitRow(Fold, null, null, null, null, op.OldCount - head - tail));
					for (var i = op.OldCount - tail; i < op.OldCount; i++)
					{
						rows.Add(new SplitRow(Equal, op.OldStart + i + 1, a[op.OldStart + i], op.NewStart + i + 1, b[op.NewStart + i]));
					}
				}
				else
				{
					for (var i = 0; i < op.OldCount; i++)
					{
						rows.Add(new SplitRow(Equal, op.OldStart + i + 1, a[op.OldStart + i], op.NewStart + i + 1, b[op.NewStart + i]));
					}
				}

				continue;
			}

			var kind = op.Kind switch { DiffOpKind.Delete => Delete, DiffOpKind.Insert => Insert, _ => Replace };
			for (var i = 0; i < Math.Max(op.OldCount, op.NewCount); i++)
			{
				rows.Add(new SplitRow(kind,
					i < op.OldCount ? op.OldStart + i + 1 : null, i < op.OldCount ? a[op.OldStart + i] : null,
					i < op.NewCount ? op.NewStart + i + 1 : null, i < op.NewCount ? b[op.NewStart + i] : null));
			}
		}

		return rows;
	}
}
