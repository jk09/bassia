namespace Bassia.Core.Workspaces;

using System.Text;
using System.Text.RegularExpressions;
using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>
/// A workspace's context-spec - a filter in the spirit of a ClearCase config spec: the paths of the monorepo the
/// workspace contains. An agentic run in the workspace sees, reads and changes only those; a snapshot refuses a change
/// to any other path, and sync and landing carry the rest of the tree along unchanged. Rules are paths from the root of
/// the tree, tried in order, the last that matches deciding:
/// <list type="bullet">
/// <item><c>api</c>, <c>api/</c>, <c>libs/shared</c>: that file, or that directory with everything below it.</item>
/// <item><c>*</c>, <c>*.md</c>, <c>docs/*.md</c>: files matching the glob at that depth (<c>*</c> stays within one
/// name, <c>**</c> crosses folders: <c>**/*.proto</c>); <c>src/*/</c> (trailing slash): matching directories, whole.</item>
/// <item><c>!pattern</c>: exclude what it matches (<c>!api/testdata/</c>).</item>
/// </list>
/// No rules is the whole monorepo. <c>.bassia.toml</c> is always in context (it holds the ignore rules a snapshot
/// needs). Directories outside the context still appear, empty, at its edge, so a folder shows the monorepo's shape.
/// The spec belongs to the workspace: it is stored on its record (<see cref="Model.WorkspaceRecord.Context"/>).
/// </summary>
public sealed class ContextSpec : IEquatable<ContextSpec>
{
	public static readonly ContextSpec All = new([]);

	private readonly List<Rule> rules;

	public ContextSpec(IEnumerable<string> patterns)
	{
		rules = patterns.Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0).Select(Rule.Parse).ToList();
		Patterns = rules.Select(rule => rule.Text).ToList();
	}

	/// <summary>The rules as given (normalized); empty: the whole monorepo.</summary>
	public IReadOnlyList<string> Patterns { get; }

	public bool IsAll => rules.Count == 0;

	/// <summary>Parses comma- or newline-separated rules; "all", or no rules, is the whole monorepo.</summary>
	public static ContextSpec Parse(IEnumerable<string> patterns)
	{
		var list = Split(patterns);
		return list.Count == 1 && list[0].Equals("all", StringComparison.OrdinalIgnoreCase) ? All : new ContextSpec(list);
	}

	/// <summary>
	/// Parses rules, expanding each <c>@name</c> to that profile of <paramref name="policy"/>'s <c>[context.profiles]</c>,
	/// so a workspace records the rules themselves.
	/// </summary>
	public static ContextSpec Resolve(IEnumerable<string> rules, Version.RepoPolicy policy)
	{
		var expanded = new List<string>();
		foreach (var rule in Split(rules))
		{
			if (!rule.StartsWith('@'))
			{
				expanded.Add(rule);
				continue;
			}

			if (!policy.ContextProfiles.TryGetValue(rule[1..], out var profile))
			{
				throw new VersionException($"No context profile '{rule[1..]}' in {Version.RepoPolicy.FileName} [context.profiles]" +
					(policy.ContextProfiles.Count == 0 ? " (it defines none)." : $"; it defines: {string.Join(", ", policy.ContextProfiles.Keys)}."));
			}

			expanded.AddRange(profile);
		}

		return Parse(expanded);
	}

	/// <summary>Whether any rule names a profile (and needs <see cref="Resolve"/>).</summary>
	public static bool NamesProfile(IEnumerable<string> rules) => Split(rules).Any(rule => rule.StartsWith('@'));

	/// <summary>The spec a record stores (null or empty: the whole monorepo).</summary>
	public static ContextSpec Of(IReadOnlyList<string>? stored) => stored is { Count: > 0 } ? new ContextSpec(stored) : All;

	/// <summary>What a record stores: null for the whole monorepo.</summary>
	public IReadOnlyList<string>? ToStored() => IsAll ? null : Patterns;

	/// <summary>This spec with <paramref name="more"/> appended: the last match decides, so they take precedence.</summary>
	public ContextSpec Add(IEnumerable<string> more)
	{
		var added = Split(more);
		return IsAll ? this : new ContextSpec(Patterns.Concat(added));
	}

	/// <summary>This spec without the rules equal to <paramref name="these"/>.</summary>
	public ContextSpec Remove(IEnumerable<string> these)
	{
		var drop = new HashSet<string>(new ContextSpec(Split(these)).Patterns, StringComparer.Ordinal);
		var missing = drop.Where(rule => !Patterns.Contains(rule, StringComparer.Ordinal)).ToList();
		if (missing.Count > 0)
		{
			throw new VersionException($"The context-spec has no rule {string.Join(", ", missing.Select(rule => $"'{rule}'"))}; its rules are: {this}.");
		}

		return new ContextSpec(Patterns.Where(rule => !drop.Contains(rule)));
	}

	private static List<string> Split(IEnumerable<string> patterns) =>
		patterns.SelectMany(pattern => pattern.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries)).Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0).ToList();

	/// <summary>Whether the file (or symlink) at <paramref name="path"/> is in context.</summary>
	public bool Contains(string path)
	{
		if (rules.Count == 0 || path == Version.RepoPolicy.FileName)
		{
			return true;
		}

		var inside = false;
		foreach (var rule in rules)
		{
			if (rule.MatchesFile(path))
			{
				inside = !rule.Exclude;
			}
		}

		return inside;
	}

	/// <summary>Whether anything under the directory <paramref name="dir"/> may be in context: if not, its subtree is never read.</summary>
	public bool MayContainUnder(string dir) => rules.Count == 0 || dir.Length == 0 || rules.Any(rule => !rule.Exclude && rule.MayMatchUnder(dir));

	public bool Equals(ContextSpec? other) => other is not null && Patterns.SequenceEqual(other.Patterns, StringComparer.Ordinal);

	public override bool Equals(object? obj) => Equals(obj as ContextSpec);

	public override int GetHashCode() => string.Join(",", Patterns).GetHashCode(StringComparison.Ordinal);

	public override string ToString() => rules.Count == 0 ? "all" : string.Join(",", Patterns);

	private sealed class Rule
	{
		public required string Text { get; init; }

		public required bool Exclude { get; init; }

		/// <summary>Per segment: a regex for one name, or null for "**".</summary>
		public required Regex?[] Segments { get; init; }

		/// <summary>The rule names a directory with everything below it (a literal path, or a trailing slash).</summary>
		public required bool Subtree { get; init; }

		/// <summary>A trailing slash: directories only, never a file of that name.</summary>
		public required bool DirectoryOnly { get; init; }

		public static Rule Parse(string text)
		{
			var exclude = text.StartsWith('!');
			var body = (exclude ? text[1..] : text).Replace('\\', '/').Trim();
			var directory = body.EndsWith('/');
			body = body.Trim('/');
			if (body.Length == 0 || body.Split('/').Any(part => part is "." or ".."))
			{
				throw new VersionException($"Invalid context rule '{text}': a path from the root of the tree, such as 'api/', 'docs/*.md' or '!api/testdata/'.");
			}

			var literal = !body.Contains('*') && !body.Contains('?');
			var parts = body.Split('/');
			return new Rule
			{
				Text = (exclude ? "!" : "") + body + (directory ? "/" : ""),
				Exclude = exclude,
				Segments = parts.Select(part => part == "**" ? null : SegmentRegex(part)).ToArray(),
				// A literal path is a file of that name, or a directory with its subtree.
				Subtree = directory || literal,
				DirectoryOnly = directory
			};
		}

		private static Regex SegmentRegex(string part)
		{
			var pattern = new StringBuilder("^");
			foreach (var c in part)
			{
				pattern.Append(c switch { '*' => "[^/]*", '?' => "[^/]", _ => Regex.Escape(c.ToString()) });
			}

			return new Regex(pattern.Append('$').ToString(), RegexOptions.CultureInvariant);
		}

		public bool MatchesFile(string path)
		{
			var names = PathNames.Split(path);
			if (!DirectoryOnly && Match(names, 0, 0, names.Length))
			{
				return true;
			}

			// A directory rule matches every file below a directory it matches.
			if (Subtree)
			{
				for (var depth = names.Length - 1; depth >= 1; depth--)
				{
					if (Match(names, 0, 0, depth))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>Whether the first <paramref name="count"/> names match the segments from <paramref name="segment"/> on, exactly.</summary>
		private bool Match(string[] names, int name, int segment, int count)
		{
			if (segment == Segments.Length)
			{
				return name == count;
			}

			if (Segments[segment] is null)
			{
				// "**": any number of names, zero included.
				for (var skip = name; skip <= count; skip++)
				{
					if (Match(names, skip, segment + 1, count))
					{
						return true;
					}
				}

				return false;
			}

			return name < count && Segments[segment]!.IsMatch(names[name]) && Match(names, name + 1, segment + 1, count);
		}

		/// <summary>Whether a file below <paramref name="dir"/> could match: its names fit a prefix of the segments.</summary>
		public bool MayMatchUnder(string dir)
		{
			var names = PathNames.Split(dir);
			return Fits(names, 0, 0);

			bool Fits(string[] all, int name, int segment)
			{
				if (name == all.Length)
				{
					// The directory is consumed; something below can still match unless the rule is used up.
					return segment < Segments.Length || Subtree;
				}

				if (segment == Segments.Length)
				{
					// The rule named an ancestor directory: its whole subtree.
					return Subtree;
				}

				if (Segments[segment] is null)
				{
					return true;
				}

				return Segments[segment]!.IsMatch(all[name]) && Fits(all, name + 1, segment + 1);
			}
		}
	}
}

/// <summary>A change, or a context-spec, that would cross a workspace's context boundary; <see cref="Paths"/> are the paths outside.</summary>
public sealed class ContextBoundaryException(IReadOnlyList<string> paths, string message) : VersionException(message)
{
	public IReadOnlyList<string> Paths { get; } = paths;

	/// <summary>The first few paths, for a message.</summary>
	public static string List(IReadOnlyList<string> paths) =>
		string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? $" and {paths.Count - 5} more" : "");
}
