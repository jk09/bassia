namespace Bassia;

using System.Text;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// <c>.bassia/components.toml</c> as the <c>component</c> commands edit it: one <c>[[component]]</c> block per
/// component, changed line by line so hand-written comments and layout elsewhere in the file survive. Every edit is
/// checked by loading the monorepo from the result (names, references, no cycles) and undone when that fails.
/// </summary>
internal static class ComponentsFile
{
	public static string PathOf(string root) => Path.Combine(root, Monorepo.MetaRepoFolderName, "components.toml");

	/// <summary>Appends a new <c>[[component]]</c> block.</summary>
	public static void Add(string root, string name, string url, IReadOnlyList<ComponentReference> references)
	{
		var builder = new StringBuilder($"\n[[component]]\nname = {ConfigFile.Quote(name)}\nurl = {ConfigFile.Quote(url)}\n");
		if (references.Count > 0)
		{
			builder.Append(ReferencesLine(references)).Append('\n');
		}

		Edit(root, text => text + builder);
	}

	/// <summary>Replaces the <c>references</c> of a component; an empty list removes the key.</summary>
	public static void SetReferences(string root, string name, IReadOnlyList<ComponentReference> references) =>
		Edit(root, text =>
		{
			var lines = Lines(text);
			var (start, end) = Block(lines, name);
			for (var i = start + 1; i < end; i++)
			{
				if (!IsKey(lines[i], "references"))
				{
					continue;
				}

				// A hand-written array may span several lines: remove up to its closing bracket.
				var last = i;
				var depth = Depth(lines[i]);
				while (depth > 0 && last + 1 < end)
				{
					depth += Depth(lines[++last]);
				}

				lines.RemoveRange(i, last - i + 1);
				end -= last - i + 1;
				break;
			}

			if (references.Count > 0)
			{
				var nameLine = Enumerable.Range(start + 1, end - start - 1).FirstOrDefault(i => IsKey(lines[i], "name"), end - 1);
				lines.Insert(nameLine + 1, ReferencesLine(references));
			}

			return string.Join("\n", lines);
		});

	/// <summary>Removes a component's block, with the blank line before it.</summary>
	public static void Remove(string root, string name) =>
		Edit(root, text =>
		{
			var lines = Lines(text);
			var (start, end) = Block(lines, name);
			if (start > 0 && lines[start - 1].Trim().Length == 0)
			{
				start--;
			}

			lines.RemoveRange(start, end - start);
			return string.Join("\n", lines);
		});

	/// <summary>
	/// Parses <c>lib</c> and <c>lib:path/inside</c> entries: the component name, and the subfolder of the referring
	/// component where it is nested (the name itself when no path is given).
	/// </summary>
	public static IReadOnlyList<ComponentReference> ParseReferences(IEnumerable<string> entries) =>
		entries.Select(entry =>
		{
			var separator = entry.IndexOf(':');
			var name = (separator < 0 ? entry : entry[..separator]).Trim();
			var path = separator < 0 ? name : entry[(separator + 1)..].Trim();
			if (name.Length == 0 || path.Length == 0)
			{
				throw new MonorepoException($"Invalid reference '{entry}': expected <component> or <component>:<path>.");
			}

			return new ComponentReference(name, path);
		}).ToList();

	private static string ReferencesLine(IReadOnlyList<ComponentReference> references) =>
		"references = [" + string.Join(", ", references.Select(reference => reference.Path == reference.Name
			? ConfigFile.Quote(reference.Name)
			: $"{{ name = {ConfigFile.Quote(reference.Name)}, path = {ConfigFile.Quote(reference.Path)} }}")) + "]";

	private static void Edit(string root, Func<string, string> change)
	{
		var path = PathOf(root);
		var original = File.Exists(path) ? File.ReadAllText(path) : "";
		var changed = change(original.Replace("\r\n", "\n")).TrimEnd('\n') + "\n";
		File.WriteAllText(path, changed);
		try
		{
			var monorepo = Monorepo.Load(root);
			monorepo.Closure(monorepo.Components.Select(component => component.Name));
		}
		catch (MonorepoException)
		{
			File.WriteAllText(path, original);
			throw;
		}
	}

	private static List<string> Lines(string text) => text.Split('\n').ToList();

	/// <summary>The line range <c>[start, end)</c> of the named component's block.</summary>
	private static (int Start, int End) Block(List<string> lines, string name)
	{
		for (var start = 0; start < lines.Count; start++)
		{
			if (lines[start].Trim() != "[[component]]")
			{
				continue;
			}

			var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
			end = end < 0 ? lines.Count : end;
			while (end > start + 1 && lines[end - 1].Trim().Length == 0)
			{
				end--;
			}

			if (NameOf(lines.GetRange(start, end - start)) == name)
			{
				return (start, end);
			}
		}

		throw new MonorepoException($"Component '{name}' is not registered in components.toml.");
	}

	private static string? NameOf(List<string> block)
	{
		try
		{
			var table = TomlSerializer.Deserialize<TomlTable>(string.Join("\n", block));
			return table is not null && table.TryGetValue("component", out var components) && components is TomlTableArray array && array.Count > 0
				&& array[0].TryGetValue("name", out var name) ? name as string : null;
		}
		catch (TomlException)
		{
			return null;
		}
	}

	private static bool IsKey(string line, string key)
	{
		var trimmed = line.TrimStart();
		return trimmed.StartsWith(key, StringComparison.Ordinal) && trimmed[key.Length..].TrimStart().StartsWith('=');
	}

	/// <summary>Net bracket depth of a line, ignoring brackets inside quoted strings.</summary>
	private static int Depth(string line)
	{
		var depth = 0;
		char? quote = null;
		foreach (var c in line)
		{
			if (quote is not null)
			{
				if (c == quote)
				{
					quote = null;
				}
			}
			else if (c is '"' or '\'')
			{
				quote = c;
			}
			else if (c == '#')
			{
				break;
			}
			else if (c == '[')
			{
				depth++;
			}
			else if (c == ']')
			{
				depth--;
			}
		}

		return depth;
	}
}
