namespace Bassia;

using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// Serializes a command result as the TOML document <c>bassia</c> prints for machine consumption. TOML (rather than
/// JSON) keeps results in the same notation as everything else Bassia stores - <c>config.toml</c>,
/// <c>components.toml</c>, <c>run.toml</c> - so a caller reading a result and a caller reading a run record use one
/// parser and one vocabulary of snake_case keys.
/// </summary>
internal static class TomlResult
{
	/// <summary>
	/// Comment line opening every printed result. <c>bassia run start</c> lets the agent command inherit stdout, so a
	/// result can be preceded by arbitrary output; the marker is where a caller starts parsing. It is a TOML
	/// comment, so the marked text stays a valid document on its own.
	/// </summary>
	public const string Marker = "# bassia result";

	public static string Serialize(IReadOnlyDictionary<string, object?> payload)
	{
		var texts = new List<string>();
		var toml = TomlSerializer.Serialize(ToTable(payload, texts)).TrimEnd('\n');

		// Multi-line text (ASCII graphics) goes out as a literal string, which shows it exactly as drawn: Tomlyn
		// would write a basic string with every newline escaped. It is serialized as a placeholder first, so the
		// document's structure stays Tomlyn's, and the placeholder is then swapped for the literal.
		for (var index = 0; index < texts.Count; index++)
		{
			toml = toml.Replace($"\"{Placeholder(index)}\"", "'''\n" + texts[index] + "'''");
		}

		return Marker + "\n" + toml;
	}

	private static string Placeholder(int index) => $"__bassia_text_{index}__";

	private static TomlTable ToTable(IReadOnlyDictionary<string, object?> values, List<string> texts)
	{
		var table = new TomlTable();

		// A TOML table's own key/value pairs must all precede its first sub-table, otherwise they would be read as
		// belonging to that sub-table. OrderBy is stable, so each group keeps the order the caller wrote it in.
		var entries = values
			.Select(entry => (entry.Key, Value: Convert(entry.Value, texts)))
			.Where(entry => entry.Value is not null)
			.OrderBy(entry => entry.Value is TomlTable or TomlTableArray ? 1 : 0);

		foreach (var (key, value) in entries)
		{
			table[key] = value!;
		}

		return table;
	}

	/// <summary>Maps a payload value to its Tomlyn representation, or null for a value TOML cannot express.</summary>
	private static object? Convert(object? value, List<string> texts) => value switch
	{
		// TOML has no null: an absent value is an absent key, as in the run records.
		null => null,
		TomlText text => TextValue(text, texts),
		string or bool or long or double => value,
		int number => (long)number,
		IReadOnlyDictionary<string, object?> nested => ToTable(nested, texts),
		IEnumerable<IReadOnlyDictionary<string, object?>> tables => ToTableArray(tables, texts),
		IEnumerable<string> strings => ToArray(strings),
		_ => value.ToString()
	};

	/// <summary>
	/// A placeholder for <paramref name="text"/>, or the text itself as a plain string when a literal string cannot
	/// hold it (it contains three apostrophes in a row, or a control character other than tab and newline).
	/// </summary>
	private static string TextValue(TomlText text, List<string> texts)
	{
		var normalized = text.Text.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
		if (normalized.Contains(TripleApostrophe, StringComparison.Ordinal) || normalized.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
		{
			return text.Text;
		}

		texts.Add(normalized);
		return Placeholder(texts.Count - 1);
	}

	private const string TripleApostrophe = "'''";

	private static TomlTableArray ToTableArray(IEnumerable<IReadOnlyDictionary<string, object?>> tables, List<string> texts)
	{
		var array = new TomlTableArray();
		foreach (var table in tables)
		{
			array.Add(ToTable(table, texts));
		}

		return array;
	}

	private static TomlArray ToArray(IEnumerable<string> values)
	{
		var array = new TomlArray();
		foreach (var value in values)
		{
			array.Add(value);
		}

		return array;
	}
}

/// <summary>
/// Multi-line text in a result - an ASCII graph, a tree, a table - printed as a TOML multi-line literal string so
/// it reads as drawn while the result stays one valid TOML document.
/// </summary>
internal sealed record TomlText(string Text);
