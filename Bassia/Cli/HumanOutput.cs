namespace Bassia.Cli;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// A result for a person instead of a parser (<c>-human</c>): the same payload <see cref="TomlResult"/> serializes,
/// printed as Markdown - the message (or <c>**error:** ...</c>), then <c>- **key:** value</c> bullets, one heading per
/// list, nested table or multi-line text, and a Markdown table (or one bullet per entry when the cells are too long for
/// a table) for lists of entries. Drawn text (graphs, trees, tables) goes into fenced code blocks, so it keeps its
/// layout. The output reads fine as plain text and renders in any Markdown reader. The TOML output stays the complete form.
/// </summary>
internal static partial class HumanOutput
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
			var lines = Scalar(headline).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(Escape);
			builder.Append(failed ? "**error:** " : "").Append(string.Join("\\\n", lines)).Append("\n\n");
		}

		var data = payload.Where(entry => entry.Key is not ("ok" or "command" or "message" or "error")).ToList();
		var hasTable = data.Any(entry => entry is { Key: "table", Value: TomlText });

		// The plain values first, as one list under the message; everything bigger gets a section of its own.
		var scalars = data.Where(entry => IsInline(entry.Value)).ToList();
		WriteBullets(builder, scalars, 0);
		if (scalars.Count > 0)
		{
			builder.Append('\n');
		}

		foreach (var (key, value) in data.Where(entry => !IsInline(entry.Value)))
		{
			WriteSection(builder, key, value, hasTable);
		}

		return builder.ToString().TrimEnd('\n');
	}

	/// <summary>A single-line plain value, which fits on a bullet.</summary>
	private static bool IsInline(object? value) =>
		value is string or bool or int or long or double && !Scalar(value).Contains('\n');

	private static void WriteSection(StringBuilder builder, string key, object? value, bool skipEntryLists)
	{
		switch (value)
		{
			case null:
				break;
			case TomlText text:
				AppendFenced(builder, text.Text, "");
				builder.Append('\n');
				break;
			case IReadOnlyDictionary<string, object?> nested:
				AppendHeading(builder, key);
				WriteBullets(builder, nested, 0);
				builder.Append('\n');
				break;
			case IEnumerable<IReadOnlyDictionary<string, object?>> tables:
				if (!skipEntryLists)
				{
					AppendHeading(builder, key);
					WriteEntryList(builder, tables.ToList());
					builder.Append('\n');
				}

				break;
			case IEnumerable<string> strings:
				AppendHeading(builder, key);
				foreach (var item in strings)
				{
					builder.Append("- ").Append(Escape(item)).Append('\n');
				}

				builder.Append('\n');
				break;
			default:
				AppendHeading(builder, key);
				AppendFenced(builder, Scalar(value), "");
				builder.Append('\n');
				break;
		}
	}

	private static void AppendHeading(StringBuilder builder, string key) =>
		builder.Append("## ").Append(Escape(key)).Append("\n\n");

	/// <summary>The entries as a bullet list: <c>- **key:** value</c>, with lists, nested tables and drawn text indented under their key.</summary>
	private static void WriteBullets(StringBuilder builder, IEnumerable<KeyValuePair<string, object?>> entries, int depth)
	{
		var pad = string.Concat(Enumerable.Repeat(Indent, depth));
		foreach (var (key, value) in entries)
		{
			switch (value)
			{
				case null:
					break;
				case TomlText text:
					builder.Append(pad).Append("- ").Append(Key(key)).Append('\n');
					AppendFenced(builder, text.Text, pad + Indent);
					break;
				case string or bool or int or long or double:
					WriteScalar(builder, pad, key, Scalar(value));
					break;
				case IReadOnlyDictionary<string, object?> nested:
					builder.Append(pad).Append("- ").Append(Key(key)).Append('\n');
					WriteBullets(builder, nested, depth + 1);
					break;
				case IEnumerable<IReadOnlyDictionary<string, object?>> tables:
					builder.Append(pad).Append("- ").Append(Key(key)).Append('\n');
					WriteEntries(builder, tables.ToList(), depth + 1);
					break;
				case IEnumerable<string> strings:
					builder.Append(pad).Append("- ").Append(Key(key)).Append('\n');
					foreach (var item in strings)
					{
						builder.Append(pad).Append(Indent).Append("- ").Append(Escape(item)).Append('\n');
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
			builder.Append(pad).Append("- ").Append(Key(key)).Append(' ').Append(Escape(text)).Append('\n');
			return;
		}

		builder.Append(pad).Append("- ").Append(Key(key)).Append('\n');
		AppendFenced(builder, text, pad + Indent);
	}

	/// <summary>A list of entries under a section heading: a table of their plain columns, or bullets when a cell is too long for one.</summary>
	private static void WriteEntryList(StringBuilder builder, IReadOnlyList<IReadOnlyDictionary<string, object?>> entries)
	{
		var columns = entries.SelectMany(entry => entry.Keys).Distinct().ToList();
		var tabular = entries.All(entry => entry.Values.All(value => value is null or bool or int or long or double
			|| (value is string text && text.Length <= TableCell && !text.Contains('\n'))));
		if (!tabular || columns.Count == 0)
		{
			WriteEntries(builder, entries, 0);
			return;
		}

		builder.Append("| ").Append(string.Join(" | ", columns.Select(Escape))).Append(" |\n");
		builder.Append("| ").Append(string.Join(" | ", columns.Select(_ => "---"))).Append(" |\n");
		foreach (var entry in entries)
		{
			var cells = columns.Select(column => entry.TryGetValue(column, out var cell) && cell is not null ? Escape(Scalar(cell)) : "");
			builder.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
		}
	}

	/// <summary>One bullet per entry: its first field on the bullet, the other fields nested under it.</summary>
	private static void WriteEntries(StringBuilder builder, IReadOnlyList<IReadOnlyDictionary<string, object?>> entries, int depth)
	{
		var pad = string.Concat(Enumerable.Repeat(Indent, depth));
		foreach (var entry in entries)
		{
			var fields = entry.Where(field => field.Value is not null).ToList();
			if (fields.Count == 0)
			{
				continue;
			}

			var (firstKey, firstValue) = fields[0];
			if (IsInline(firstValue))
			{
				WriteScalar(builder, pad, firstKey, Scalar(firstValue));
				WriteBullets(builder, fields.Skip(1), depth + 1);
			}
			else
			{
				WriteBullets(builder, fields, depth);
			}
		}
	}

	private static void AppendFenced(StringBuilder builder, string text, string pad)
	{
		var body = text.Replace("\r\n", "\n").TrimEnd('\n');
		var longestTicks = TickRun().Matches(body).Select(match => match.Length).DefaultIfEmpty(0).Max();
		var fence = new string('`', Math.Max(3, longestTicks + 1));
		builder.Append(pad).Append(fence).Append('\n');
		foreach (var line in body.Split('\n'))
		{
			builder.Append(line.Length == 0 ? "" : pad).Append(line).Append('\n');
		}

		builder.Append(pad).Append(fence).Append('\n');
	}

	private static string Key(string key) => $"**{Escape(key)}:**";

	/// <summary>
	/// Backslash-escapes what a Markdown reader would take for markup (<c>&lt;path&gt;</c> would vanish as an HTML tag),
	/// but leaves an underscore inside a word alone, so <c>rest_of_line</c> stays readable.
	/// </summary>
	private static string Escape(string text) => EmphasisUnderscore().Replace(MarkupCharacter().Replace(text, "\\$0"), "\\_");

	private static string Scalar(object? value) => value switch
	{
		bool flag => flag ? "true" : "false",
		IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
		_ => value?.ToString() ?? ""
	};

	[GeneratedRegex(@"[\\`*<>\[\]|]")]
	private static partial Regex MarkupCharacter();

	[GeneratedRegex(@"(?<![A-Za-z0-9])_|_(?![A-Za-z0-9])")]
	private static partial Regex EmphasisUnderscore();

	[GeneratedRegex("`+")]
	private static partial Regex TickRun();
}
