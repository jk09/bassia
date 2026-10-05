namespace Bassia.Cli;

using System.Globalization;
using System.Text;

/// <summary>
/// A result for a person instead of a parser (<c>-human</c>): the same payload <see cref="TomlResult"/> serializes,
/// printed as plain text - the message (or <c>error: ...</c>), then <c>key: value</c> lines, bullets for string lists,
/// indented sections for nested tables, and an ASCII table (or one block per entry when the cells are too long for a
/// table) for lists of entries. Multi-line text is printed as drawn. The TOML output stays the complete form.
/// </summary>
internal static class HumanOutput
{
	/// <summary>Whether this run prints human-readable results. Set by <see cref="ProgramCli.RunAsync"/> for each command line.</summary>
	public static bool Enabled { get; set; }

	/// <summary>The widest cell an entry list may have and still be drawn as a table.</summary>
	private const int TableCell = 48;

	private const string Indent = "  ";

	public static string Render(IReadOnlyDictionary<string, object?> payload)
	{
		var builder = new StringBuilder();
		var failed = payload.TryGetValue("error", out var error) && error is not null;
		var headline = failed ? payload["error"] : payload.GetValueOrDefault("message");
		if (headline is not null)
		{
			builder.Append(failed ? "error: " : "").Append(Scalar(headline)).Append('\n');
		}

		var data = payload.Where(entry => entry.Key is not ("ok" or "command" or "message" or "error")).ToList();
		var hasTable = data.Any(entry => entry is { Key: "table", Value: TomlText });
		WriteEntries(builder, data, 0, hasTable);
		return builder.ToString().TrimEnd('\n');
	}

	private static void WriteEntries(StringBuilder builder, IEnumerable<KeyValuePair<string, object?>> entries, int depth, bool skipEntryLists)
	{
		var pad = string.Concat(Enumerable.Repeat(Indent, depth));
		foreach (var (key, value) in entries)
		{
			switch (value)
			{
				case null:
					break;
				case TomlText text:
					builder.Append('\n');
					AppendLines(builder, text.Text, pad);
					break;
				case string or bool or int or long or double:
					WriteScalar(builder, pad, key, Scalar(value));
					break;
				case IReadOnlyDictionary<string, object?> nested:
					builder.Append(pad).Append(key).Append(":\n");
					WriteEntries(builder, nested, depth + 1, skipEntryLists: false);
					break;
				case IEnumerable<IReadOnlyDictionary<string, object?>> tables:
					if (!skipEntryLists)
					{
						builder.Append(pad).Append(key).Append(":\n");
						WriteEntryList(builder, tables.ToList(), depth + 1);
					}

					break;
				case IEnumerable<string> strings:
					builder.Append(pad).Append(key).Append(":\n");
					foreach (var item in strings)
					{
						builder.Append(pad).Append(Indent).Append("- ").Append(item).Append('\n');
					}

					break;
				default:
					WriteScalar(builder, pad, key, Scalar(value));
					break;
			}
		}
	}

	private static void WriteScalar(StringBuilder builder, string pad, string key, string text)
	{
		if (!text.Contains('\n'))
		{
			builder.Append(pad).Append(key).Append(": ").Append(text).Append('\n');
			return;
		}

		builder.Append(pad).Append(key).Append(":\n");
		AppendLines(builder, text, pad + Indent);
	}

	private static void WriteEntryList(StringBuilder builder, IReadOnlyList<IReadOnlyDictionary<string, object?>> entries, int depth)
	{
		var pad = string.Concat(Enumerable.Repeat(Indent, depth));
		var columns = entries.SelectMany(entry => entry.Keys).Distinct().ToList();
		var tabular = entries.All(entry => entry.Values.All(value => value is null or bool or int or long or double
			|| (value is string text && text.Length <= TableCell && !text.Contains('\n'))));
		if (tabular && columns.Count > 0)
		{
			var rows = entries.Select(entry => (IReadOnlyList<string>)columns.Select(column => entry.TryGetValue(column, out var cell) && cell is not null ? Scalar(cell) : "").ToList());
			AppendLines(builder, AsciiTable.Render(columns, rows, TableCell), pad);
			return;
		}

		foreach (var entry in entries)
		{
			var first = true;
			var block = new StringBuilder();
			WriteEntries(block, entry, 1, skipEntryLists: false);
			foreach (var line in block.ToString().TrimEnd('\n').Split('\n'))
			{
				// The entry's first line carries the dash: "  - name: x", the rest lines up under it.
				builder.Append(pad).Append(first ? "- " : Indent).Append(line.StartsWith(Indent, StringComparison.Ordinal) ? line[Indent.Length..] : line).Append('\n');
				first = false;
			}
		}
	}

	private static void AppendLines(StringBuilder builder, string text, string pad)
	{
		foreach (var line in text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
		{
			builder.Append(line.Length == 0 ? "" : pad).Append(line).Append('\n');
		}
	}

	private static string Scalar(object? value) => value switch
	{
		bool flag => flag ? "true" : "false",
		IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
		_ => value?.ToString() ?? ""
	};
}
