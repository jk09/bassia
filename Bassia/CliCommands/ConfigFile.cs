namespace Bassia;

using System.Text;
using Tomlyn;
using Tomlyn.Model;

/// <summary>A setting of <c>.bassia/config.toml</c> that <c>bassia config</c> reads and writes.</summary>
internal sealed record ConfigKey(string Key, string Section, string Name, string Default, string Description);

/// <summary>
/// <c>.bassia/config.toml</c> as <c>bassia config</c> edits it. Only the keys Bassia reads can be set, and a value is
/// written by replacing (or adding) its one line in its section, so the comments <c>init</c> wrote - which explain
/// every setting - survive an edit.
/// </summary>
internal static class ConfigFile
{
	public static readonly IReadOnlyList<ConfigKey> Keys =
	[
		new("workspace.path", "workspace", "path", Monorepo.DefaultWorkspaceFolderName,
			"Folder of the run and integration checkouts, absolute or relative to the monorepo root."),
		new("agent.command", "agent", "command", Monorepo.DefaultAgentCommand,
			"Agent command a run composed from -prompt starts with (the prompt is appended as one quoted argument)."),
		new("agent.commit.subject", "agent.commit", "subject", Monorepo.DefaultCommitSubject,
			"Subject line of a run's result commits. Placeholders: {run_id}, {short_id}, {summary}, {component}."),
		new("integration.resolver", "integration", "resolver", Monorepo.DefaultResolver,
			"Command that resolves a semantic merge; it gets the merge brief on stdin."),
		new("integration.weave", "integration", "weave", Monorepo.DefaultStructuralDriver,
			"Structural merge driver tried where git's merge conflicts (weave's weave-driver), used when installed; off disables it.")
	];

	public static string PathOf(string root) => Path.Combine(root, Monorepo.MetaRepoFolderName, "config.toml");

	public static ConfigKey Find(string key) =>
		Keys.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
			?? throw new MonorepoException($"Unknown configuration key '{key}'. Known keys: {string.Join(", ", Keys.Select(candidate => candidate.Key))}.");

	/// <summary>The value set in the file, or null when the key is not set (Bassia then uses its default).</summary>
	public static string? Get(string root, ConfigKey key)
	{
		var path = PathOf(root);
		if (!File.Exists(path))
		{
			return null;
		}

		TomlTable table;
		try
		{
			table = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? new TomlTable();
		}
		catch (TomlException ex)
		{
			throw new MonorepoException($"Could not parse '{path}': {ex.Message}");
		}

		object current = table;
		foreach (var part in key.Section.Split('.').Append(key.Name))
		{
			if (current is not TomlTable section || !section.TryGetValue(part, out var next))
			{
				return null;
			}

			current = next;
		}

		return current as string;
	}

	/// <summary>
	/// Writes <paramref name="value"/> for <paramref name="key"/>: replaces the key's line in its <c>[section]</c>,
	/// adds the line at the end of the section, or appends the section. The edited file must still load, or it is
	/// left as it was.
	/// </summary>
	public static void Set(string root, ConfigKey key, string value)
	{
		var path = PathOf(root);
		var original = File.Exists(path) ? File.ReadAllText(path) : "";
		var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
		if (lines.Count > 0 && lines[^1] == "")
		{
			lines.RemoveAt(lines.Count - 1);
		}

		var line = $"{key.Name} = {Quote(value)}";
		var header = lines.FindIndex(candidate => IsHeader(candidate, key.Section));
		if (header < 0)
		{
			if (lines.Count > 0 && lines[^1].Trim().Length > 0)
			{
				lines.Add("");
			}

			lines.Add($"[{key.Section}]");
			lines.Add(line);
		}
		else
		{
			var end = lines.FindIndex(header + 1, candidate => candidate.TrimStart().StartsWith('['));
			end = end < 0 ? lines.Count : end;
			var existing = lines.FindIndex(header + 1, end - header - 1, candidate => IsAssignment(candidate, key.Name));
			if (existing >= 0)
			{
				lines[existing] = line;
			}
			else
			{
				// After the section's last non-blank line, so the blank line before the next section stays.
				var insert = end;
				while (insert > header + 1 && lines[insert - 1].Trim().Length == 0)
				{
					insert--;
				}

				lines.Insert(insert, line);
			}
		}

		File.WriteAllText(path, string.Join("\n", lines) + "\n");
		try
		{
			Monorepo.Load(root);
		}
		catch (MonorepoException)
		{
			File.WriteAllText(path, original);
			throw;
		}
	}

	private static bool IsHeader(string line, string section)
	{
		var trimmed = line.Trim();
		return trimmed.StartsWith('[') && !trimmed.StartsWith("[[", StringComparison.Ordinal)
			&& trimmed.TrimStart('[').Split(']')[0].Trim() == section;
	}

	private static bool IsAssignment(string line, string name)
	{
		var trimmed = line.TrimStart();
		return trimmed.StartsWith(name, StringComparison.Ordinal) && trimmed[name.Length..].TrimStart().StartsWith('=');
	}

	/// <summary>A TOML basic string.</summary>
	public static string Quote(string value)
	{
		var builder = new StringBuilder("\"");
		foreach (var c in value)
		{
			builder.Append(c switch
			{
				'\\' => "\\\\",
				'"' => "\\\"",
				'\n' => "\\n",
				'\r' => "\\r",
				'\t' => "\\t",
				_ when char.IsControl(c) => $"\\u{(int)c:X4}",
				_ => c.ToString()
			});
		}

		return builder.Append('"').ToString();
	}
}
