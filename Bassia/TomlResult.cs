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
	/// Comment line opening every printed result. <c>bassia agent</c> lets the agent command inherit stdout, so a
	/// result can be preceded by arbitrary output; the marker is where a caller starts parsing. It is a TOML
	/// comment, so the marked text stays a valid document on its own.
	/// </summary>
	public const string Marker = "# bassia result";

	public static string Serialize(IReadOnlyDictionary<string, object?> payload) =>
		Marker + "\n" + TomlSerializer.Serialize(ToTable(payload)).TrimEnd('\n');

	private static TomlTable ToTable(IReadOnlyDictionary<string, object?> values)
	{
		var table = new TomlTable();

		// A TOML table's own key/value pairs must all precede its first sub-table, otherwise they would be read as
		// belonging to that sub-table. OrderBy is stable, so each group keeps the order the caller wrote it in.
		var entries = values
			.Select(entry => (entry.Key, Value: Convert(entry.Value)))
			.Where(entry => entry.Value is not null)
			.OrderBy(entry => entry.Value is TomlTable or TomlTableArray ? 1 : 0);

		foreach (var (key, value) in entries)
		{
			table[key] = value!;
		}

		return table;
	}

	/// <summary>Maps a payload value to its Tomlyn representation, or null for a value TOML cannot express.</summary>
	private static object? Convert(object? value) => value switch
	{
		// TOML has no null: an absent value is an absent key, as in the run records.
		null => null,
		string or bool or long or double => value,
		int number => (long)number,
		IReadOnlyDictionary<string, object?> nested => ToTable(nested),
		IEnumerable<IReadOnlyDictionary<string, object?>> tables => ToTableArray(tables),
		IEnumerable<string> strings => ToArray(strings),
		_ => value.ToString()
	};

	private static TomlTableArray ToTableArray(IEnumerable<IReadOnlyDictionary<string, object?>> tables)
	{
		var array = new TomlTableArray();
		foreach (var table in tables)
		{
			array.Add(ToTable(table));
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
