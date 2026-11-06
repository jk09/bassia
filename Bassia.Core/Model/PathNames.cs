namespace Bassia.Core.Model;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Repository paths: forward-slash separated, relative to the root, without a leading or trailing slash. The root is
/// the empty string. Names are case-sensitive bytes; two paths that differ only by case are refused at snapshot because
/// Windows directories cannot hold both.
/// </summary>
public static class PathNames
{
	public static bool IsValidName(string name) =>
		name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\', '\0']) < 0;

	/// <summary>A user-supplied path made canonical: backslashes become slashes, surrounding slashes and "./" go.</summary>
	public static string Normalize(string path)
	{
		var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToList();
		foreach (var part in parts)
		{
			if (!IsValidName(part))
			{
				throw new ArgumentException($"'{path}' is not a valid repository path.");
			}
		}

		return string.Join('/', parts);
	}

	public static string[] Split(string path) => path.Length == 0 ? [] : path.Split('/');

	public static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

	public static string Parent(string path)
	{
		var slash = path.LastIndexOf('/');
		return slash < 0 ? "" : path[..slash];
	}

	public static string Name(string path)
	{
		var slash = path.LastIndexOf('/');
		return slash < 0 ? path : path[(slash + 1)..];
	}

	/// <summary>Whether <paramref name="path"/> is <paramref name="prefix"/> itself or inside it. The root contains everything.</summary>
	public static bool IsUnder(string path, string prefix)
	{
		prefix = prefix.TrimEnd('/');
		return prefix.Length == 0 || path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
	}

	/// <summary>Whether two prefixes overlap: one contains the other.</summary>
	public static bool Overlap(string a, string b) => IsUnder(a, b) || IsUnder(b, a);

	/// <summary>A prefix in its canonical form for leases and policies: normalized, ending with "/" unless it is the root.</summary>
	public static string Prefix(string prefix)
	{
		var normalized = Normalize(prefix);
		return normalized.Length == 0 ? "" : normalized + "/";
	}

	/// <summary>The ancestors of a prefix, root first, excluding the prefix itself ("a/b/" gives "", "a/").</summary>
	public static IEnumerable<string> Ancestors(string prefix)
	{
		var parts = Split(prefix.TrimEnd('/'));
		var current = "";
		yield return "";
		for (var index = 0; index < parts.Length - 1; index++)
		{
			current += parts[index] + "/";
			yield return current;
		}
	}
}

/// <summary>
/// A gitignore-like glob over repository paths: <c>*</c> matches within a name, <c>**</c> across names, <c>?</c> one
/// character. A pattern without a slash matches the name of any path (<c>*.md</c>, <c>bin</c>); a trailing slash
/// matches a directory and everything in it (<c>node_modules/</c>); a pattern with a slash is anchored at the root
/// (<c>docs/**</c>, <c>src/*/Generated/</c>).
/// </summary>
public sealed class PathGlob
{
	private readonly Regex regex;

	public PathGlob(string pattern)
	{
		Pattern = pattern;
		var text = pattern.Replace('\\', '/').Trim();
		var directory = text.EndsWith('/');
		text = text.Trim('/');
		var anchored = text.Contains('/');
		var body = new StringBuilder();
		for (var index = 0; index < text.Length; index++)
		{
			var c = text[index];
			if (c == '*' && index + 1 < text.Length && text[index + 1] == '*')
			{
				index++;
				if (index + 1 < text.Length && text[index + 1] == '/')
				{
					index++;
					body.Append("(?:.*/)?");
				}
				else
				{
					body.Append(".*");
				}
			}
			else if (c == '*')
			{
				body.Append("[^/]*");
			}
			else if (c == '?')
			{
				body.Append("[^/]");
			}
			else
			{
				body.Append(Regex.Escape(c.ToString()));
			}
		}

		var start = anchored ? "^" : "^(?:.*/)?";
		var end = directory ? "/.*$" : "(?:/.*)?$";
		regex = new Regex(start + body + end, RegexOptions.CultureInvariant);
	}

	public string Pattern { get; }

	/// <summary>Whether <paramref name="path"/> (a file path, or a directory path ending in "/") is matched.</summary>
	public bool IsMatch(string path) => regex.IsMatch(path);

	public override string ToString() => Pattern;
}
