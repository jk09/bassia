namespace Bassia.Scenarios.Harness;

using Tomlyn.Model;

/// <summary>Reading a TOML result by the keys docs/reference.md names; a missing key fails the test with the result shown.</summary>
public static class Toml
{
	public static string Str(this TomlTable table, string key) => table.TryGetValue(key, out var value) && value is string text
		? text
		: throw Missing(table, key, "a string");

	public static string? StrOrNull(this TomlTable table, string key) => table.TryGetValue(key, out var value) ? value as string : null;

	public static long Long(this TomlTable table, string key) => table.TryGetValue(key, out var value) && value is long number
		? number
		: throw Missing(table, key, "an integer");

	public static long? LongOrNull(this TomlTable table, string key) => table.TryGetValue(key, out var value) && value is long number ? number : null;

	public static bool Bool(this TomlTable table, string key) => table.TryGetValue(key, out var value) && value is bool flag
		? flag
		: throw Missing(table, key, "a boolean");

	public static TomlTable Table(this TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlTable inner
		? inner
		: throw Missing(table, key, "a table");

	/// <summary>An array of tables (<c>[[key]]</c>); empty when the result has none.</summary>
	public static IReadOnlyList<TomlTable> Tables(this TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlTableArray array
		? array.ToList()
		: [];

	/// <summary>An array of strings; empty when absent.</summary>
	public static IReadOnlyList<string> Strings(this TomlTable table, string key) => table.TryGetValue(key, out var value) && value is TomlArray array
		? array.Select(item => item?.ToString() ?? "").ToList()
		: [];

	public static string Dump(this TomlTable table) => Tomlyn.TomlSerializer.Serialize(table);

	private static Xunit.Sdk.XunitException Missing(TomlTable table, string key, string kind) =>
		new($"The result has no '{key}' ({kind}):\n{table.Dump()}");
}
