namespace Bassia.Core.Workspaces;

using System.Text;
using System.Text.RegularExpressions;
using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>
/// What a workspace loads - a configuration spec in the spirit of ClearCase's load rules: the paths a workspace's folder
/// holds and whose content the clone fetches and caches. Everything else stays on the server, untouched: a snapshot
/// never reads an unloaded path as deleted, and sync and landing carry it along unchanged. Rules are paths from the
/// root of the tree, tried in order, the last that matches deciding:
/// <list type="bullet">
/// <item><c>api</c>, <c>api/</c>, <c>libs/shared</c>: that file, or that directory with everything below it.</item>
/// <item><c>*</c>, <c>*.md</c>, <c>docs/*.md</c>: files matching the glob at that depth (<c>*</c> stays within one
/// name, <c>**</c> crosses folders: <c>**/*.proto</c>); <c>src/*/</c> (trailing slash): matching directories, whole.</item>
/// <item><c>!pattern</c>: exclude what it matches (<c>!api/testdata/</c>).</item>
/// </list>
/// No rules loads everything. <c>.bassia.toml</c> is always loaded (it holds the ignore rules a snapshot needs).
/// Directories that are not loaded still appear, empty, at the edge of what is, so the folder shows the monorepo's
/// shape.
/// </summary>
public sealed class LoadRules
{
	public static readonly LoadRules All = new([]);

	private readonly List<Rule> rules;

	public LoadRules(IEnumerable<string> patterns)
	{
		rules = patterns.Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0).Select(Rule.Parse).ToList();
		Patterns = rules.Select(rule => rule.Text).ToList();
	}

	/// <summary>The rules as given (normalized); empty: everything.</summary>
	public IReadOnlyList<string> Patterns { get; }

	public bool IsAll => rules.Count == 0;

	/// <summary>Parses comma- or newline-separated rules; "all", or no rules, loads everything.</summary>
	public static LoadRules Parse(IEnumerable<string> patterns)
	{
		var list = patterns.SelectMany(pattern => pattern.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries)).Select(pattern => pattern.Trim()).ToList();
		return list.Count == 1 && list[0].Equals("all", StringComparison.OrdinalIgnoreCase) ? All : new LoadRules(list);
	}

	/// <summary>Whether the file (or symlink) at <paramref name="path"/> is loaded.</summary>
	public bool IsLoaded(string path)
	{
		if (rules.Count == 0 || path == Version.RepoPolicy.FileName)
		{
			return true;
		}

		var loaded = false;
		foreach (var rule in rules)
		{
			if (rule.MatchesFile(path))
			{
				loaded = !rule.Exclude;
			}
		}

		return loaded;
	}

	/// <summary>Whether anything under the directory <paramref name="dir"/> may be loaded: if not, its subtree is never fetched.</summary>
	public bool MayLoadUnder(string dir) => rules.Count == 0 || dir.Length == 0 || rules.Any(rule => !rule.Exclude && rule.MayMatchUnder(dir));

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
				throw new VersionException($"Invalid load rule '{text}': a path from the root of the tree, such as 'api/', 'docs/*.md' or '!api/testdata/'.");
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
