namespace Bassia;

using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// Where a setting comes from, lowest first: Bassia's built-in default, the monorepo's <c>.bassia/config.toml</c>
/// (committed to the meta-repo, shared by everyone), and the user's <c>.bassia/config.user.toml</c> (never committed).
/// </summary>
internal enum ConfigLayer { Default, Monorepo, User }

/// <summary>
/// A setting <c>bassia config</c> reads and writes: its dotted key, the TOML table it lives in (<paramref name="Section"/>,
/// one entry per table name) and its name there. <paramref name="Choices"/> limits the values; a
/// <paramref name="IsList"/> setting is a TOML array, written as a comma-separated value; an <paramref name="IsInteger"/>
/// one takes a positive whole number. A merge or lock setting can be overridden per component
/// (<c>merge.component.&lt;name&gt;.&lt;setting&gt;</c>, <c>lock.component.&lt;name&gt;.&lt;setting&gt;</c>); such a key names
/// its <paramref name="Component"/> and falls back to the global one.
/// </summary>
internal sealed record ConfigKey(
	string Key,
	IReadOnlyList<string> Section,
	string Name,
	string Default,
	string Description,
	IReadOnlyList<string>? Choices = null,
	bool IsList = false,
	bool PerComponent = false,
	string? Component = null,
	bool IsInteger = false)
{
	/// <summary>The <c>[table]</c> header text, with names quoted where TOML needs it.</summary>
	public string SectionText => string.Join('.', Section.Select(ConfigFile.TomlKey));

	/// <summary>The global key a per-component key falls back to; the key itself otherwise.</summary>
	public string GlobalKey => Component is null ? Key : $"{Section[0]}.{Name}";

	public string Allowed => Choices is not null ? string.Join(" | ", Choices)
		: IsList ? "a comma-separated list" : IsInteger ? "a whole number" : "any text";
}

/// <summary>A setting's effective value, the layer it comes from, and the key that was set (a component key or its global key).</summary>
internal sealed record ConfigValue(ConfigKey Key, string Value, ConfigLayer Layer, string SetAs)
{
	public string Source => Layer switch
	{
		ConfigLayer.Default => "default",
		ConfigLayer.Monorepo => SetAs == Key.Key ? "monorepo" : $"monorepo ({SetAs})",
		_ => SetAs == Key.Key ? "user" : $"user ({SetAs})"
	};
}

/// <summary>
/// Both configuration files as read at one moment. Every consumer reads settings through it, so the layering is
/// decided in one place: a value in the user layer wins over the monorepo's, which wins over the default; a
/// per-component merge or lock setting is looked up first for the component, then globally.
/// </summary>
internal sealed class ConfigSnapshot
{
	private readonly TomlTable monorepo;
	private readonly TomlTable user;

	private ConfigSnapshot(TomlTable monorepo, TomlTable user)
	{
		this.monorepo = monorepo;
		this.user = user;
	}

	public static ConfigSnapshot Empty { get; } = new(new TomlTable(), new TomlTable());

	public static ConfigSnapshot Load(string root) =>
		new(ConfigFile.Read(ConfigFile.PathOf(root, ConfigLayer.Monorepo)), ConfigFile.Read(ConfigFile.PathOf(root, ConfigLayer.User)));

	/// <summary>The value <paramref name="key"/> has in one layer, or null when that layer does not set it.</summary>
	public string? Raw(ConfigLayer layer, ConfigKey key)
	{
		object current = layer switch { ConfigLayer.Monorepo => monorepo, ConfigLayer.User => user, _ => new TomlTable() };
		foreach (var part in key.Section.Append(key.Name))
		{
			if (current is not TomlTable table || !table.TryGetValue(part, out var next))
			{
				return null;
			}

			current = next;
		}

		var text = current switch
		{
			string value => value,
			TomlArray array => string.Join(",", array.Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture))),
			bool flag => flag ? "true" : "false",
			null => null,
			_ => Convert.ToString(current, System.Globalization.CultureInfo.InvariantCulture)
		};
		return string.IsNullOrWhiteSpace(text) && !key.IsList ? null : text;
	}

	public ConfigValue Resolve(ConfigKey key)
	{
		var candidates = key.Component is null ? [key] : new[] { key, ConfigFile.Find(key.GlobalKey) };
		foreach (var candidate in candidates)
		{
			foreach (var layer in new[] { ConfigLayer.User, ConfigLayer.Monorepo })
			{
				if (Raw(layer, candidate) is { } value)
				{
					return new ConfigValue(key, value, layer, candidate.Key);
				}
			}
		}

		return new ConfigValue(key, key.Default, ConfigLayer.Default, key.Key);
	}

	public string Value(string key) => Resolve(ConfigFile.Find(key)).Value;

	/// <summary>The per-component merge and lock keys set in either layer, in name order.</summary>
	public IReadOnlyList<ConfigKey> ComponentKeys()
	{
		var keys = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var table in new[] { monorepo, user })
		{
			foreach (var (group, known) in ConfigFile.ComponentSettings)
			{
				if (table.TryGetValue(group, out var groupValue) && groupValue is TomlTable groupTable
					&& groupTable.TryGetValue("component", out var components) && components is TomlTable componentTables)
				{
					foreach (var (component, settings) in componentTables)
					{
						if (settings is TomlTable settingTable)
						{
							foreach (var name in settingTable.Keys.Where(known.Contains))
							{
								keys.Add(ConfigFile.ComponentKey(group, component, name).Key);
							}
						}
					}
				}
			}
		}

		return keys.Select(ConfigFile.Find).ToList();
	}

	/// <summary>Every value set outside its key's choices (or not a whole number where one is needed), as one message per value.</summary>
	public IReadOnlyList<string> Problems()
	{
		var problems = new List<string>();
		foreach (var key in ConfigFile.Keys.Concat(ComponentKeys()).Where(key => key.Choices is not null || key.IsInteger))
		{
			foreach (var layer in new[] { ConfigLayer.Monorepo, ConfigLayer.User })
			{
				if (Raw(layer, key) is not { } value)
				{
					continue;
				}

				if (key.Choices is { } choices && !choices.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
				{
					problems.Add($"{key.Key} = '{value}' in {ConfigFile.FileName(layer)} is not one of {key.Allowed}");
				}
				else if (key.IsInteger && !ConfigFile.IsPositiveInteger(value))
				{
					problems.Add($"{key.Key} = '{value}' in {ConfigFile.FileName(layer)} is not a positive whole number");
				}
			}
		}

		return problems;
	}
}

/// <summary>
/// The configuration files as <c>bassia config</c> edits them. Only the keys Bassia reads can be set, and a value is
/// written by replacing (or adding) its one line in its table, so the comments <c>init</c> wrote - which explain
/// every setting - survive an edit. The monorepo layer is <c>.bassia/config.toml</c>; the user layer is
/// <c>.bassia/config.user.toml</c>, kept out of the meta-repo's commits.
/// </summary>
internal static class ConfigFile
{
	public const string UserFileName = "config.user.toml";

	/// <summary>The merge settings a component can override.</summary>
	public static readonly IReadOnlySet<string> MergeSettings = new HashSet<string>(StringComparer.Ordinal) { "semantic", "warnings", "manual_paths", "advance", "boundaries", "verify", "owner_review" };

	/// <summary>The lock settings a component can override.</summary>
	public static readonly IReadOnlySet<string> LockSettings = new HashSet<string>(StringComparer.Ordinal) { "files", "folders", "conflict" };

	/// <summary>The groups with per-component settings (<c>&lt;group&gt;.component.&lt;name&gt;.&lt;setting&gt;</c>) and their settings.</summary>
	public static readonly IReadOnlyList<(string Group, IReadOnlySet<string> Settings)> ComponentSettings = [("merge", MergeSettings), ("lock", LockSettings)];

	public static readonly IReadOnlyList<ConfigKey> Keys =
	[
		new("workspace.path", ["workspace"], "path", Monorepo.DefaultWorkspaceFolderName,
			"Folder of the run and integration checkouts, absolute or relative to the monorepo root."),
		new("agent.command", ["agent"], "command", Monorepo.DefaultAgentCommand,
			"Agent command a run composed from -prompt starts with (the prompt is appended as one quoted argument)."),
		new("agent.commit.subject", ["agent", "commit"], "subject", Monorepo.DefaultCommitSubject,
			"Subject line of a run's result commits. Placeholders: {run_id}, {short_id}, {summary}, {component}."),
		new("integration.resolver", ["integration"], "resolver", Monorepo.DefaultResolver,
			"Command that resolves a semantic merge; it gets the merge brief on stdin."),
		new("integration.weave", ["integration"], "weave", Monorepo.DefaultStructuralDriver,
			"Structural merge driver tried where git's merge conflicts (weave's weave-driver), used when installed; off disables it."),
		new("merge.semantic", ["merge"], "semantic", "resolver",
			"What happens to a result neither git nor weave can merge: the resolver merges it, or it is left for a human (manual).",
			["resolver", "manual"], PerComponent: true),
		new("merge.warnings", ["merge"], "warnings", "resolver",
			"What happens to a result weave merged with warnings: the resolver reviews it, it is left for a human (manual), or weave's merge is accepted.",
			["resolver", "manual", "accept"], PerComponent: true),
		new("merge.boundaries", ["merge"], "boundaries", "warn",
			"What happens to a result that adds code boundary violations (non-exported paths, indirect use, paths outside the component): they are recorded (warn), or the result is left for a human (manual).",
			["warn", "manual"], PerComponent: true),
		new("merge.verify", ["merge"], "verify", "",
			"Tasks (from each component's bassia.toml, e.g. build,test) the integration runs on every component it affects - the ones it changed and the ones referencing them - before it publishes anything; a failure publishes nothing.",
			IsList: true, PerComponent: true),
		new("merge.owner_review", ["merge"], "owner_review", "off",
			"Whether a result in a component with owners is merged like any other (off) or left for a human - its owners - to review (manual).",
			["off", "manual"], PerComponent: true),
		new("merge.manual_paths", ["merge"], "manual_paths", "",
			"Path patterns (as in split plans, e.g. **/*.csproj,db/migrations) whose conflicts always need a human.",
			IsList: true, PerComponent: true),
		new("merge.advance", ["merge"], "advance", "manual",
			"Whether a completed integration fast-forwards the base branches itself (auto) or waits for 'bassia integration advance' (manual).",
			["manual", "auto"], PerComponent: true),
		new("lock.files", ["lock"], "files", "",
			"Path patterns (as in split plans, e.g. **/*.cs) of files a run holds one by one while its agent changes them; other runs see the hold and can wait for it.",
			IsList: true, PerComponent: true),
		new("lock.folders", ["lock"], "folders", "",
			"Path patterns of folders a run holds as a whole while its agent changes any file in them: src/* is each folder directly under src, ** the folder of every file.",
			IsList: true, PerComponent: true),
		new("lock.conflict", ["lock"], "conflict", "resolver",
			"What the integration does with a result that changed a path another run held: the resolver reviews it even if git merges it (resolver), it is left for a human (manual), or it merges like any other (merge).",
			["resolver", "manual", "merge"], PerComponent: true),
		new("lock.ttl", ["lock"], "ttl", "120",
			"Seconds a hold lasts without its run renewing it: the holds of a crashed or suspended run expire after this.",
			IsInteger: true)
	];

	private static readonly Regex ComponentKeyPattern = new(@"^(?<group>merge|lock)\.component\.(?<component>.+)\.(?<setting>[a-z_]+)$", RegexOptions.CultureInvariant);

	public static string PathOf(string root, ConfigLayer layer = ConfigLayer.Monorepo) =>
		Path.Combine(root, Monorepo.MetaRepoFolderName, FileName(layer));

	public static string FileName(ConfigLayer layer) => layer == ConfigLayer.User ? UserFileName : "config.toml";

	public static ConfigKey Find(string key)
	{
		if (Keys.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase)) is { } known)
		{
			return known;
		}

		var match = ComponentKeyPattern.Match(key);
		if (match.Success && ComponentSettings.Any(entry => entry.Group == match.Groups["group"].Value && entry.Settings.Contains(match.Groups["setting"].Value)))
		{
			return ComponentKey(match.Groups["group"].Value, match.Groups["component"].Value, match.Groups["setting"].Value);
		}

		throw new MonorepoException($"Unknown configuration key '{key}'. Known keys: {string.Join(", ", Keys.Select(candidate => candidate.Key))}, " +
			$"and {string.Join(" and ", ComponentSettings.Select(entry => $"{entry.Group}.component.<component>.<{string.Join("|", entry.Settings)}>"))} for one component.");
	}

	/// <summary>A merge setting for one component: <c>merge.component.&lt;component&gt;.&lt;setting&gt;</c>.</summary>
	public static ConfigKey ComponentKey(string component, string setting) => ComponentKey("merge", component, setting);

	/// <summary>A merge or lock setting for one component: <c>&lt;group&gt;.component.&lt;component&gt;.&lt;setting&gt;</c>.</summary>
	public static ConfigKey ComponentKey(string group, string component, string setting)
	{
		var global = Keys.First(key => key.Key == $"{group}.{setting}");
		return global with
		{
			Key = $"{group}.component.{component}.{setting}",
			Section = [group, "component", component],
			Component = component,
			PerComponent = false,
			Description = $"{global.Description} For component '{component}' only; falls back to {global.Key}."
		};
	}

	public static bool IsPositiveInteger(string value) =>
		int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0;

	/// <summary>The value set in one layer, or null when that layer does not set it.</summary>
	public static string? Get(string root, ConfigKey key, ConfigLayer layer = ConfigLayer.Monorepo) => ConfigSnapshot.Load(root).Raw(layer, key);

	/// <summary>Checks <paramref name="value"/> against the key and returns it as it is stored (a choice in lower case).</summary>
	public static string Normalize(ConfigKey key, string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new MonorepoException($"An empty value is not allowed for {key.Key}; 'bassia config unset -key {key.Key}' goes back to the next layer.");
		}

		if (key.Choices is { } choices)
		{
			return choices.FirstOrDefault(choice => string.Equals(choice, value.Trim(), StringComparison.OrdinalIgnoreCase))
				?? throw new MonorepoException($"'{value}' is not a valid value for {key.Key}; use {key.Allowed}.");
		}

		if (key.IsInteger)
		{
			return IsPositiveInteger(value) ? value.Trim() : throw new MonorepoException($"'{value}' is not a valid value for {key.Key}; use a positive whole number.");
		}

		return value;
	}

	/// <summary>
	/// Writes <paramref name="value"/> for <paramref name="key"/> in <paramref name="layer"/>: replaces the key's line in
	/// its <c>[table]</c>, adds the line at the end of the table, or appends the table. The edited configuration must
	/// still load, or the file is left as it was.
	/// </summary>
	public static void Set(string root, ConfigKey key, string value, ConfigLayer layer = ConfigLayer.Monorepo)
	{
		value = Normalize(key, value);
		var line = $"{TomlKey(key.Name)} = {(key.IsList ? List(value) : Quote(value))}";
		Edit(root, layer, lines =>
		{
			var header = lines.FindIndex(candidate => IsHeader(candidate, key.Section));
			if (header < 0)
			{
				if (lines.Count > 0 && lines[^1].Trim().Length > 0)
				{
					lines.Add("");
				}

				lines.Add($"[{key.SectionText}]");
				lines.Add(line);
				return true;
			}

			var end = SectionEnd(lines, header);
			var existing = lines.FindIndex(header + 1, end - header - 1, candidate => IsAssignment(candidate, key.Name));
			if (existing >= 0)
			{
				lines[existing] = line;
				return true;
			}

			// After the section's last non-blank line, so the blank line before the next section stays.
			var insert = end;
			while (insert > header + 1 && lines[insert - 1].Trim().Length == 0)
			{
				insert--;
			}

			lines.Insert(insert, line);
			return true;
		});
	}

	/// <summary>Removes <paramref name="key"/>'s line from <paramref name="layer"/>; false when that layer did not set it.</summary>
	public static bool Unset(string root, ConfigKey key, ConfigLayer layer = ConfigLayer.Monorepo) => Edit(root, layer, lines =>
	{
		var header = lines.FindIndex(candidate => IsHeader(candidate, key.Section));
		if (header < 0)
		{
			return false;
		}

		var end = SectionEnd(lines, header);
		var existing = lines.FindIndex(header + 1, end - header - 1, candidate => IsAssignment(candidate, key.Name));
		if (existing < 0)
		{
			return false;
		}

		lines.RemoveAt(existing);
		end--;

		// A per-component table holds nothing else; once empty it goes, with the blank line before it.
		if (key.Component is not null && lines.Skip(header + 1).Take(end - header - 1).All(candidate => candidate.Trim().Length == 0))
		{
			lines.RemoveRange(header, end - header);
			if (header > 0 && header < lines.Count && lines[header - 1].Trim().Length > 0)
			{
				lines.Insert(header, "");
			}

			while (header > 0 && header == lines.Count && lines[header - 1].Trim().Length == 0)
			{
				lines.RemoveAt(--header);
			}
		}

		return true;
	});

	/// <summary>
	/// Keeps the user layer out of the meta-repo's commits. A meta-repo made by this version ignores it in its
	/// <c>.gitignore</c>; an older one gets the entry in its local <c>info/exclude</c>, which needs no commit.
	/// </summary>
	public static void EnsureUserLayerIgnored(string root)
	{
		var metaRepo = Path.Combine(root, Monorepo.MetaRepoFolderName);
		var gitignore = Path.Combine(metaRepo, ".gitignore");
		if (File.Exists(gitignore) && File.ReadAllLines(gitignore).Any(line => line.Trim().TrimStart('/') == UserFileName))
		{
			return;
		}

		var gitDir = Path.Combine(metaRepo, ".git");
		if (!Directory.Exists(gitDir))
		{
			return;
		}

		var exclude = Path.Combine(gitDir, "info", "exclude");
		if (File.Exists(exclude) && File.ReadAllLines(exclude).Any(line => line.Trim().TrimStart('/') == UserFileName))
		{
			return;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
		var existing = File.Exists(exclude) ? File.ReadAllText(exclude) : "";
		File.AppendAllText(exclude, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") + $"# Bassia's per-user configuration layer\n/{UserFileName}\n");
	}

	/// <summary>Applies <paramref name="change"/> to the file's lines; when it changed them, writes and checks the result.</summary>
	private static bool Edit(string root, ConfigLayer layer, Func<List<string>, bool> change)
	{
		if (layer == ConfigLayer.Default)
		{
			throw new MonorepoException("The default layer is built into Bassia and cannot be changed.");
		}

		var path = PathOf(root, layer);
		var existed = File.Exists(path);
		var original = existed ? File.ReadAllText(path) : "";
		var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
		if (lines.Count > 0 && lines[^1] == "")
		{
			lines.RemoveAt(lines.Count - 1);
		}

		if (!existed && layer == ConfigLayer.User)
		{
			lines.Add("# Your own Bassia settings for this monorepo; they override config.toml and are never committed.");
			lines.Add("# Read and change them with 'bassia config list|get|set|unset -user'.");
		}

		if (!change(lines))
		{
			return false;
		}

		if (layer == ConfigLayer.User)
		{
			EnsureUserLayerIgnored(root);
		}

		File.WriteAllText(path, string.Join("\n", lines) + "\n");
		try
		{
			Monorepo.Load(root);
		}
		catch (MonorepoException)
		{
			if (existed)
			{
				File.WriteAllText(path, original);
			}
			else
			{
				File.Delete(path);
			}

			throw;
		}

		return true;
	}

	internal static TomlTable Read(string path)
	{
		if (!File.Exists(path))
		{
			return new TomlTable();
		}

		try
		{
			return TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? new TomlTable();
		}
		catch (TomlException ex)
		{
			throw new MonorepoException($"Could not parse '{path}': {ex.Message}");
		}
	}

	private static int SectionEnd(List<string> lines, int header)
	{
		var end = lines.FindIndex(header + 1, candidate => candidate.TrimStart().StartsWith('['));
		return end < 0 ? lines.Count : end;
	}

	private static bool IsHeader(string line, IReadOnlyList<string> section) =>
		HeaderParts(line) is { } parts && parts.SequenceEqual(section, StringComparer.Ordinal);

	/// <summary>The table names of a <c>[a.b."c d"]</c> header line, or null when the line is not one.</summary>
	internal static IReadOnlyList<string>? HeaderParts(string line)
	{
		var text = line.Trim();
		if (!text.StartsWith('[') || text.StartsWith("[[", StringComparison.Ordinal))
		{
			return null;
		}

		var parts = new List<string>();
		var current = new StringBuilder();
		var i = 1;
		while (i < text.Length)
		{
			var c = text[i];
			if (c == '"' || c == '\'')
			{
				var close = text.IndexOf(c, i + 1);
				if (close < 0)
				{
					return null;
				}

				current.Append(c == '"' ? text[(i + 1)..close].Replace("\\\"", "\"").Replace("\\\\", "\\") : text[(i + 1)..close]);
				i = close + 1;
				continue;
			}

			if (c == '.' || c == ']')
			{
				parts.Add(current.ToString().Trim());
				current.Clear();
				if (c == ']')
				{
					return parts;
				}
			}
			else if (!char.IsWhiteSpace(c))
			{
				current.Append(c);
			}

			i++;
		}

		return null;
	}

	private static bool IsAssignment(string line, string name)
	{
		var trimmed = line.TrimStart();
		var key = TomlKey(name);
		return trimmed.StartsWith(key, StringComparison.Ordinal) && trimmed[key.Length..].TrimStart().StartsWith('=');
	}

	/// <summary>A TOML key: bare when it can be, quoted otherwise.</summary>
	public static string TomlKey(string name) =>
		name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? name : Quote(name);

	private static string List(string value) =>
		"[" + string.Join(", ", value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Quote)) + "]";

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
