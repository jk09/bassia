namespace Bassia.Unwind;

/// <summary>
/// The identity of a repository: its remote address with everything that does not tell repositories apart removed.
/// A submodule is the same component wherever it is found when its identity is the same:
/// <c>https://github.com/Owner/Repo.git</c>, <c>git@github.com:owner/repo</c> and <c>ssh://git@github.com/owner/repo/</c>
/// are all <c>github.com/owner/repo</c>. A local path (or <c>file://</c> URL) is identified by its full path.
/// </summary>
internal static class RepoIdentity
{
	/// <summary>
	/// The URL of a submodule whose <c>.gitmodules</c> URL is <paramref name="url"/>, inside a repository at
	/// <paramref name="superprojectUrl"/>. Like git, a URL starting with <c>./</c> or <c>../</c> is relative to the
	/// superproject's URL taken as a folder: <c>../lib.git</c> in <c>https://host/owner/app.git</c> is
	/// <c>https://host/owner/lib.git</c>. Any other URL is returned unchanged.
	/// </summary>
	public static string Resolve(string url, string superprojectUrl)
	{
		url = url.Trim();
		if (!url.StartsWith("./", StringComparison.Ordinal) && !url.StartsWith("../", StringComparison.Ordinal))
		{
			return url;
		}

		var basePart = superprojectUrl.Trim().TrimEnd('/', '\\');
		var rest = url;
		while (true)
		{
			if (rest.StartsWith("./", StringComparison.Ordinal))
			{
				rest = rest[2..];
			}
			else if (rest.StartsWith("../", StringComparison.Ordinal))
			{
				rest = rest[3..];
				var schemeEnd = basePart.IndexOf("://", StringComparison.Ordinal);
				var cut = basePart.LastIndexOfAny(['/', '\\', ':']);
				if (cut < 0 || (schemeEnd >= 0 && cut <= schemeEnd + 2))
				{
					throw new MonorepoException($"Cannot resolve the submodule URL '{url}' against '{superprojectUrl}': it climbs above the host.");
				}

				var separator = basePart[cut];
				basePart = basePart[..cut];
				if (separator == ':')
				{
					// git@host:repo.git + ../lib.git -> git@host:lib.git
					return $"{basePart}:{rest}";
				}
			}
			else
			{
				break;
			}
		}

		return rest.Length == 0 ? basePart : $"{basePart}/{rest}";
	}

	/// <summary>The identity of the repository at <paramref name="url"/> (a URL, an scp-like address or a local path).</summary>
	public static string Of(string url)
	{
		var trimmed = url.Trim();
		if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
		{
			return LocalIdentity(Uri.TryCreate(trimmed, UriKind.Absolute, out var fileUri) ? fileUri.LocalPath : trimmed["file://".Length..]);
		}

		var scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
		if (scheme > 0)
		{
			var afterScheme = trimmed[(scheme + 3)..];
			var slash = afterScheme.IndexOf('/');
			var authority = slash < 0 ? afterScheme : afterScheme[..slash];
			var path = slash < 0 ? "" : afterScheme[slash..];
			return RemoteIdentity(Host(authority), path);
		}

		if (IsScpLike(trimmed, out var host, out var scpPath))
		{
			return RemoteIdentity(host, scpPath);
		}

		return LocalIdentity(trimmed);
	}

	/// <summary><c>[user@]host:path</c>, git's scp-like syntax: a colon before the first slash, and not a drive letter.</summary>
	private static bool IsScpLike(string url, out string host, out string path)
	{
		host = path = "";
		var colon = url.IndexOf(':');
		var slash = url.IndexOfAny(['/', '\\']);
		if (colon <= 0 || (slash >= 0 && slash < colon))
		{
			return false;
		}

		var authority = url[..colon];
		if (authority.Length == 1 && char.IsLetter(authority[0]))
		{
			return false; // C:\path or C:/path
		}

		host = Host(authority);
		path = url[(colon + 1)..];
		return true;
	}

	/// <summary>The host of <c>[user[:password]@]host[:port]</c>, lowercased.</summary>
	private static string Host(string authority)
	{
		var at = authority.LastIndexOf('@');
		var host = at < 0 ? authority : authority[(at + 1)..];
		var port = host.LastIndexOf(':');
		if (port > 0 && !host.EndsWith(']') && host[(port + 1)..].All(char.IsDigit))
		{
			host = host[..port];
		}

		return host.ToLowerInvariant();
	}

	// Hosted repository names (GitHub, GitLab, Bitbucket, Azure DevOps) are case-insensitive.
	private static string RemoteIdentity(string host, string path) =>
		$"{host}/{StripGitSuffix(path.Replace('\\', '/').Trim('/'))}".TrimEnd('/').ToLowerInvariant();

	private static string LocalIdentity(string path)
	{
		string full;
		try
		{
			full = Path.GetFullPath(path);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			full = path;
		}

		var normalized = StripGitSuffix(full.Replace('\\', '/').TrimEnd('/'));
		return "file:" + (OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized);
	}

	private static string StripGitSuffix(string path)
	{
		path = path.TrimEnd('/');
		if (path.EndsWith("/.git", StringComparison.OrdinalIgnoreCase))
		{
			path = path[..^5];
		}
		else if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			path = path[..^4];
		}

		return path.TrimEnd('/');
	}
}
