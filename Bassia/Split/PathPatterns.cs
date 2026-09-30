namespace Bassia.Split;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// A list of path patterns of a split plan, matched against paths relative to the component's root. <c>**</c> matches
/// any number of folders, <c>*</c> anything within one path segment, <c>?</c> one character; a pattern also matches
/// everything below what it matches, so <c>src/core</c> (or <c>src/core/</c>) is the whole folder. Patterns are
/// anchored at the root: <c>*.md</c> is the Markdown files at the top, <c>**/*.md</c> all of them. A pattern starting
/// with <c>!</c> excludes what it matches from the list's earlier and later positive patterns alike.
/// </summary>
internal sealed class PathPatterns
{
	private readonly List<Regex> include = [];
	private readonly List<Regex> exclude = [];

	public PathPatterns(IReadOnlyList<string> patterns)
	{
		Texts = patterns;
		foreach (var pattern in patterns)
		{
			var negated = pattern.StartsWith('!');
			var body = Normalize(negated ? pattern[1..] : pattern);
			if (body.Length == 0)
			{
				throw new ArgumentException($"'{pattern}' is not a path pattern.");
			}

			(negated ? exclude : include).Add(new Regex(ToRegex(body), RegexOptions.CultureInvariant | RegexOptions.Compiled));
		}
	}

	public static PathPatterns Empty { get; } = new([]);

	public IReadOnlyList<string> Texts { get; }

	public bool IsEmpty => include.Count == 0;

	public bool Matches(string path) =>
		include.Any(regex => regex.IsMatch(path)) && !exclude.Any(regex => regex.IsMatch(path));

	private static string Normalize(string pattern) =>
		pattern.Trim().Replace('\\', '/').TrimStart('/').TrimEnd('/');

	private static string ToRegex(string glob)
	{
		var builder = new StringBuilder("^");
		for (var i = 0; i < glob.Length; i++)
		{
			var c = glob[i];
			if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
			{
				var slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
				var atSegmentStart = i == 0 || glob[i - 1] == '/';
				if (atSegmentStart && slashAfter)
				{
					builder.Append("(?:.*/)?");        // "**/": zero or more folders
					i += 2;
				}
				else
				{
					builder.Append(".*");
					i += 1;
				}
			}
			else if (c == '*')
			{
				builder.Append("[^/]*");
			}
			else if (c == '?')
			{
				builder.Append("[^/]");
			}
			else
			{
				builder.Append(Regex.Escape(c.ToString()));
			}
		}

		// Whatever a pattern matches, it matches with everything below it.
		return builder.Append("(?:/.*)?$").ToString();
	}
}
