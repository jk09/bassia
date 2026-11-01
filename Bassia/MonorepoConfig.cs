namespace Bassia;

using Tomlyn;
using Tomlyn.Model;

/// <summary>A setting: its dotted key, default and meaning.</summary>
internal sealed record ConfigKey(string Key, string Default, string Description, string? Environment = null);

/// <summary>
/// The client's settings in <c>.bassia/config.toml</c> of a monorepo folder (not versioned: repository policy lives in
/// the tree's <c>.bassia.toml</c>). An environment variable, where a setting has one, wins over the file.
/// </summary>
internal sealed class MonorepoConfig(string file)
{
	public const string FileName = "config.toml";

	public static readonly IReadOnlyList<ConfigKey> Keys =
	[
		new("user.id", "", "Who acts: recorded as the agent of operations and as the owner of workspaces. Default: the login name.", "BASSIA_USER"),
		new("agent.command", "claude -p --permission-mode acceptEdits", "The agent an agentic run starts; the prompt is appended as one quoted argument."),
		new("agent.model", "", "The model passed to the agent as --model, when set."),
		new("server.url", "", "The bassia-server a clone talks to (set by 'bassia clone')."),
		new("server.token", "", "The server's shared token, when it requires one.", "BASSIA_SERVER_TOKEN"),
		new("merge.semantic_driver", "weave-driver", "The language-aware merge driver for the semantic conflict policy (git merge-driver style: %O %A %B); 'off' disables it."),
		new("land.validate", "", "A shell command that must pass on a machine-merged tree (semantic policy) before it lands; without one, semantic conflicts need a replay."),
		new("workspace.clone", "auto", "auto: on Windows, block-clone workspace files from .bassia/clones when the monorepo folder and the workspace are on a Dev Drive (or another block-cloning ReFS volume); off: always write them."),
		new("workspace.load", "", "The load rules a new workspace gets on this machine when -load is not given (comma-separated; @name for a profile of .bassia.toml's [load.profiles]); empty: .bassia.toml's [load] default, else everything."),
		new("workspace.dir", "workspaces", "Where 'workspace create' puts a workspace's folder when -path is not given, relative to the monorepo folder."),
		new("lease.ttl", "600", "Seconds a lease lives unless renewed."),
		new("gc.grace_hours", "24", "Blobs younger than this are never swept (uploads for landings in flight)."),
		new("gc.retention_days", "14", "How long a closed workspace's operations are kept before its unlanded snapshots become collectable.")
	];

	private TomlTable table = File.Exists(file) ? TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file)) ?? new TomlTable() : new TomlTable();

	public static ConfigKey Find(string key) =>
		Keys.FirstOrDefault(candidate => candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
		?? throw new Cli.CliUsageException("config", $"Unknown setting '{key}'. Run 'bassia config list' for every setting.");

	/// <summary>The effective value and where it comes from: environment, file or default.</summary>
	public (string Value, string Source) Resolve(string key)
	{
		var spec = Find(key);
		if (spec.Environment is { } variable && System.Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnvironment)
		{
			return (fromEnvironment, "environment");
		}

		if (Lookup(spec.Key) is { } value)
		{
			return (value, "monorepo");
		}

		return (spec.Default, "default");
	}

	public string Get(string key) => Resolve(key).Value;

	public string? GetOrNull(string key) => Get(key) is { Length: > 0 } value ? value : null;

	public int GetInt(string key) => int.TryParse(Get(key), out var value) ? value : int.Parse(Find(key).Default);

	private string? Lookup(string key)
	{
		var parts = key.Split('.');
		var section = table.TryGetValue(parts[0], out var s) ? s as TomlTable : null;
		return section is not null && section.TryGetValue(parts[1], out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;
	}

	public void Set(string key, string? value)
	{
		var spec = Find(key);
		var parts = spec.Key.Split('.');
		if (!table.TryGetValue(parts[0], out var s) || s is not TomlTable section)
		{
			table[parts[0]] = section = new TomlTable();
		}

		if (value is null)
		{
			section.Remove(parts[1]);
			if (section.Count == 0)
			{
				table.Remove(parts[0]);
			}
		}
		else
		{
			section[parts[1]] = value;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, TomlSerializer.Serialize(table));
		table = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file)) ?? new TomlTable();
	}

	/// <summary>Who acts: BASSIA_USER, user.id, else the login name.</summary>
	public string User => GetOrNull("user.id") ?? System.Environment.UserName;
}
