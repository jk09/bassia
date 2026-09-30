namespace Bassia.Split;

using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

/// <summary>One new component of a split: its name, the patterns of the files it gets, and its registration.</summary>
internal sealed record SplitPart(string Name, PathPatterns Paths, IReadOnlyList<ComponentReference>? References, string? Url);

/// <summary>A component that referenced the source, and what it references instead.</summary>
internal sealed record SplitReferrer(string Name, IReadOnlyList<ComponentReference> References);

/// <summary>
/// A split plan: which component to break up and which of its files each new part gets. It is written - typically by
/// an LLM - as TOML:
/// <code>
/// source = "app"
/// branch = "main"                      # optional: the source's default branch
/// shared = ["LICENSE", ".editorconfig"] # optional: copied into every part, with history
/// drop = ["legacy/**"]                  # optional: left out of every part, with history
/// follow_renames = true                 # optional: a part's files keep their history under earlier paths
///
/// [[part]]
/// name = "app-core"
/// paths = ["src/core/**", "tests/core"]
/// references = ["lib"]                  # optional: default is the source's references
/// url = "https://example.com/app-core.git"  # optional: recorded in components.toml
///
/// [[referrer]]                          # optional: a component that referenced the source
/// name = "tool"
/// references = ["app-core", "lib"]
/// </code>
/// Parsing collects every problem instead of stopping at the first, so one answer tells the plan's author all
/// there is to fix.
/// </summary>
internal sealed class SplitPlan
{
	private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
	private static readonly HashSet<string> TopKeys = ["source", "branch", "shared", "drop", "follow_renames", "part", "referrer"];
	private static readonly HashSet<string> PartKeys = ["name", "paths", "references", "url"];
	private static readonly HashSet<string> ReferrerKeys = ["name", "references"];

	public string? Source { get; private set; }
	public string? Branch { get; private set; }
	public PathPatterns Shared { get; private set; } = PathPatterns.Empty;
	public PathPatterns Drop { get; private set; } = PathPatterns.Empty;
	public bool FollowRenames { get; private set; } = true;
	public List<SplitPart> Parts { get; } = [];
	public List<SplitReferrer> Referrers { get; } = [];

	/// <summary>False when the text is not TOML at all: there is nothing to check against the component.</summary>
	public bool IsReadable { get; private set; } = true;

	public static bool IsValidName(string name) => NamePattern.IsMatch(name) && !name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) && !name.Contains("..");

	/// <summary>Parses a plan; every problem found is added to <paramref name="problems"/>.</summary>
	public static SplitPlan Parse(string text, List<string> problems)
	{
		var plan = new SplitPlan();
		TomlTable table;
		try
		{
			table = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
		}
		catch (TomlException ex)
		{
			problems.Add($"The plan is not valid TOML: {ex.Message}");
			plan.IsReadable = false;
			return plan;
		}

		UnknownKeys(table, TopKeys, "the plan", problems);
		plan.Source = OptionalString(table, "source", "the plan", problems);
		plan.Branch = OptionalString(table, "branch", "the plan", problems);
		plan.Shared = Patterns(table, "shared", "the plan", problems, required: false);
		plan.Drop = Patterns(table, "drop", "the plan", problems, required: false);
		if (table.TryGetValue("follow_renames", out var follow))
		{
			if (follow is bool value)
			{
				plan.FollowRenames = value;
			}
			else
			{
				problems.Add("'follow_renames' must be true or false.");
			}
		}

		foreach (var (part, index) in Tables(table, "part", problems).Select((part, index) => (part, index)))
		{
			var where = $"[[part]] #{index + 1}";
			UnknownKeys(part, PartKeys, where, problems);
			var name = OptionalString(part, "name", where, problems);
			if (name is null)
			{
				problems.Add($"{where} has no 'name'.");
				continue;
			}

			where = $"Part '{name}'";
			if (!IsValidName(name))
			{
				problems.Add($"{where}: the name must start with a letter or digit and contain only letters, digits, '.', '_' and '-'.");
			}

			if (plan.Parts.Any(existing => existing.Name == name))
			{
				problems.Add($"{where} is named more than once.");
			}

			var paths = Patterns(part, "paths", where, problems, required: true);
			var references = part.ContainsKey("references") ? References(part, where, problems) : null;
			plan.Parts.Add(new SplitPart(name, paths, references, OptionalString(part, "url", where, problems)));
		}

		if (plan.Parts.Count < 2)
		{
			problems.Add($"A split needs at least two [[part]] entries; the plan has {plan.Parts.Count}.");
		}

		foreach (var (referrer, index) in Tables(table, "referrer", problems).Select((referrer, index) => (referrer, index)))
		{
			var where = $"[[referrer]] #{index + 1}";
			UnknownKeys(referrer, ReferrerKeys, where, problems);
			var name = OptionalString(referrer, "name", where, problems);
			if (name is null)
			{
				problems.Add($"{where} has no 'name'.");
				continue;
			}

			plan.Referrers.Add(new SplitReferrer(name, References(referrer, $"Referrer '{name}'", problems)));
		}

		return plan;
	}

	private static void UnknownKeys(TomlTable table, HashSet<string> known, string where, List<string> problems)
	{
		foreach (var key in table.Keys.Where(key => !known.Contains(key)))
		{
			problems.Add($"{Capitalize(where)} has the unknown key '{key}'; expected {string.Join(", ", known)}.");
		}
	}

	private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

	private static string? OptionalString(TomlTable table, string key, string where, List<string> problems)
	{
		if (!table.TryGetValue(key, out var value))
		{
			return null;
		}

		if (value is string text && !string.IsNullOrWhiteSpace(text))
		{
			return text.Trim();
		}

		problems.Add($"{Capitalize(where)}: '{key}' must be a non-empty string.");
		return null;
	}

	private static IEnumerable<TomlTable> Tables(TomlTable table, string key, List<string> problems)
	{
		if (!table.TryGetValue(key, out var value))
		{
			return [];
		}

		if (value is TomlTableArray array)
		{
			return array;
		}

		problems.Add($"'{key}' must be written as [[{key}]] tables.");
		return [];
	}

	private static PathPatterns Patterns(TomlTable table, string key, string where, List<string> problems, bool required)
	{
		if (!table.TryGetValue(key, out var value))
		{
			if (required)
			{
				problems.Add($"{Capitalize(where)} has no '{key}'.");
			}

			return PathPatterns.Empty;
		}

		var texts = value is TomlArray array && array.All(item => item is string) ? array.Cast<string>().ToList() : null;
		if (texts is null)
		{
			problems.Add($"{Capitalize(where)}: '{key}' must be an array of path patterns.");
			return PathPatterns.Empty;
		}

		try
		{
			var patterns = new PathPatterns(texts);
			if (required && patterns.IsEmpty)
			{
				problems.Add($"{Capitalize(where)}: '{key}' needs at least one pattern that is not an exclusion.");
			}

			return patterns;
		}
		catch (ArgumentException ex)
		{
			problems.Add($"{Capitalize(where)}: {ex.Message}");
			return PathPatterns.Empty;
		}
	}

	// references = ["lib", "ui:vendor/ui", { name = "other", path = "libs/other" }]
	private static List<ComponentReference> References(TomlTable table, string where, List<string> problems)
	{
		var references = new List<ComponentReference>();
		if (!table.TryGetValue("references", out var value) || value is not TomlArray array)
		{
			problems.Add($"{Capitalize(where)}: 'references' must be an array.");
			return references;
		}

		foreach (var item in array)
		{
			try
			{
				switch (item)
				{
					case string entry:
						references.AddRange(ComponentsFile.ParseReferences([entry]));
						break;
					case TomlTable reference when reference.TryGetValue("name", out var name) && name is string text:
						var path = reference.TryGetValue("path", out var pathValue) && pathValue is string customPath ? customPath : text;
						references.Add(new ComponentReference(text, path));
						break;
					default:
						problems.Add($"{Capitalize(where)}: each reference must be \"<component>\", \"<component>:<path>\" or {{ name = \"...\", path = \"...\" }}.");
						break;
				}
			}
			catch (MonorepoException ex)
			{
				problems.Add($"{Capitalize(where)}: {ex.Message}");
			}
		}

		return references;
	}
}
