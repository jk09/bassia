namespace Bassia.Core.Version;

using Bassia.Core.Model;
using Tomlyn;
using Tomlyn.Model;

/// <summary>What happens when a workspace and mainline both rewrote a path.</summary>
public static class ConflictPolicy
{
	/// <summary>Reject the landing; the orchestrator re-runs the agent from the commit's recipe on the new base.</summary>
	public const string Replay = "replay";

	/// <summary>A language-aware merge driver merges edits to disjoint symbols; the result must pass validation.</summary>
	public const string Semantic = "semantic";

	/// <summary>Line-based three-way merge, only when both versions stay similar to the base.</summary>
	public const string TextMerge = "textmerge";

	/// <summary>Store the conflict in the workspace; mainline refuses it.</summary>
	public const string Conflict = "conflict";

	public static readonly IReadOnlyList<string> All = [Replay, Semantic, TextMerge, Conflict];
}

/// <summary>How a prefix treats leases at landing.</summary>
public static class LeaseEnforcement
{
	public const string Optimistic = "optimistic";
	public const string LeaseRequired = "lease_required";
}

/// <summary>
/// The repository's policy, versioned with the code in <c>.bassia.toml</c> at the root of the tree and read from
/// mainline when a workspace lands:
/// <code>
/// [conflicts]
/// default = "replay"
/// [[conflicts.rule]]
/// paths = ["docs/**", "*.md"]
/// policy = "textmerge"
///
/// [leases]
/// required = ["src/Billing/"]
///
/// [ignore]
/// paths = ["dist/", "*.log"]
///
/// [textmerge]
/// min_similarity = 0.5
///
/// [load]                                    # what a workspace loads (Workspaces.LoadRules); default: everything
/// default = ["*", "docs/"]
/// [load.profiles]                           # named rule sets: bassia workspace create -load @api
/// api = ["*", "api/", "libs/shared/", "!api/testdata/"]
/// </code>
/// </summary>
public sealed class RepoPolicy
{
	public const string FileName = ".bassia.toml";

	/// <summary>Never snapshotted: version control folders, build output, dependency folders and caches.</summary>
	public static readonly IReadOnlyList<string> DefaultIgnore =
		[".git/", ".bassia/", ".bassia-workspace", "bin/", "obj/", "node_modules/", ".vs/", "__pycache__/", ".pytest_cache/", ".gradle/", "target/debug/", "target/release/"];

	public static readonly RepoPolicy Default = new();

	public string DefaultConflictPolicy { get; init; } = ConflictPolicy.Replay;

	public IReadOnlyList<(PathGlob Glob, string Policy)> ConflictRules { get; init; } = [];

	public IReadOnlyList<string> LeaseRequired { get; init; } = [];

	public IReadOnlyList<PathGlob> Ignore { get; init; } = DefaultIgnore.Select(pattern => new PathGlob(pattern)).ToList();

	public double TextMergeMinSimilarity { get; init; } = 0.5;

	/// <summary>The load rules a workspace gets when none are given; empty: everything.</summary>
	public IReadOnlyList<string> LoadDefault { get; init; } = [];

	/// <summary>Named load rule sets (<c>-load @name</c>).</summary>
	public IReadOnlyDictionary<string, IReadOnlyList<string>> LoadProfiles { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

	public string PolicyFor(string path) =>
		ConflictRules.FirstOrDefault(rule => rule.Glob.IsMatch(path)).Policy ?? DefaultConflictPolicy;

	public string EnforcementFor(string path) =>
		LeaseRequired.Any(prefix => PathNames.IsUnder(path, prefix.TrimEnd('/'))) ? LeaseEnforcement.LeaseRequired : LeaseEnforcement.Optimistic;

	public bool IsIgnored(string path, bool directory = false) =>
		Ignore.Any(glob => glob.IsMatch(directory ? path + "/" : path));

	public static RepoPolicy Parse(string text)
	{
		TomlTable table;
		try
		{
			table = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
		}
		catch (TomlException ex)
		{
			throw new Storage.VersionException($"{FileName} is not valid TOML: {ex.Message}", ex);
		}

		var conflicts = table.TryGetValue("conflicts", out var c) ? c as TomlTable : null;
		var defaultPolicy = conflicts?.TryGetValue("default", out var d) == true ? Check((string)d) : ConflictPolicy.Replay;
		var rules = new List<(PathGlob, string)>();
		if (conflicts?.TryGetValue("rule", out var r) == true && r is TomlTableArray ruleTables)
		{
			foreach (var rule in ruleTables)
			{
				var policy = Check(rule.TryGetValue("policy", out var p) ? p as string ?? "" : "");
				foreach (var pattern in Strings(rule, "paths"))
				{
					rules.Add((new PathGlob(pattern), policy));
				}
			}
		}

		var leases = table.TryGetValue("leases", out var l) ? l as TomlTable : null;
		var ignore = table.TryGetValue("ignore", out var i) ? i as TomlTable : null;
		var textmerge = table.TryGetValue("textmerge", out var t) ? t as TomlTable : null;
		var load = table.TryGetValue("load", out var lo) ? lo as TomlTable : null;
		var profiles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
		if (load?.TryGetValue("profiles", out var pr) == true && pr is TomlTable profileTable)
		{
			foreach (var (name, _) in profileTable)
			{
				profiles[name] = Strings(profileTable, name).ToList();
			}
		}

		return new RepoPolicy
		{
			LoadDefault = load is null ? [] : Strings(load, "default").ToList(),
			LoadProfiles = profiles,
			DefaultConflictPolicy = defaultPolicy,
			ConflictRules = rules,
			LeaseRequired = leases is null ? [] : Strings(leases, "required").Select(PathNames.Prefix).ToList(),
			Ignore = DefaultIgnore.Concat(ignore is null ? [] : Strings(ignore, "paths")).Select(pattern => new PathGlob(pattern)).ToList(),
			TextMergeMinSimilarity = textmerge?.TryGetValue("min_similarity", out var s) == true ? Convert.ToDouble(s) : 0.5
		};

		static string Check(string policy) => ConflictPolicy.All.Contains(policy)
			? policy
			: throw new Storage.VersionException($"{FileName}: unknown conflict policy '{policy}' (use {string.Join(", ", ConflictPolicy.All)}).");

		static IEnumerable<string> Strings(TomlTable table, string key) =>
			table.TryGetValue(key, out var value) && value is TomlArray array ? array.OfType<string>() : [];
	}

	/// <summary>The policy in a tree's <c>.bassia.toml</c>, or the default when there is none.</summary>
	public static async Task<RepoPolicy> LoadAsync(Trees trees, ObjectId root, CancellationToken ct = default)
	{
		var leaf = await trees.GetAsync(root, FileName, ct);
		if (leaf is null || !leaf.IsFile)
		{
			return Default;
		}

		return Parse(System.Text.Encoding.UTF8.GetString(await trees.Objects.ReadContentAsync(leaf, ct)));
	}
}
