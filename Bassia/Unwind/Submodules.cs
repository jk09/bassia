namespace Bassia.Unwind;

using Bassia.Git;

/// <summary>
/// A submodule of a commit: a gitlink (tree entry of mode <c>160000</c>) at <paramref name="Path"/> pinning
/// <paramref name="Commit"/>, with its <c>.gitmodules</c> section <paramref name="Name"/> and <paramref name="Url"/>
/// (null when <c>.gitmodules</c> does not describe the path).
/// </summary>
internal sealed record Gitlink(string Path, string Commit, string? Name, string? Url);

internal static class Submodules
{
	public const string GitmodulesFile = ".gitmodules";

	/// <summary>
	/// The gitlinks of <paramref name="commit"/> in <paramref name="repo"/> (a bare repo is fine), in path order. A
	/// <c>.gitmodules</c> section without a gitlink is not a submodule and is not returned, as git ignores it too.
	/// </summary>
	public static async Task<IReadOnlyList<Gitlink>> ReadAsync(GitClient repo, string commit)
	{
		var tree = await repo.RunOrThrowAsync(["ls-tree", "-r", "-z", "--full-tree", commit]);
		var gitlinks = new List<(string Path, string Commit)>();
		foreach (var entry in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			// <mode> SP <type> SP <object> TAB <path>
			var tab = entry.IndexOf('\t');
			var fields = entry[..tab].Split(' ');
			if (fields[0] == "160000")
			{
				gitlinks.Add((entry[(tab + 1)..], fields[2]));
			}
		}

		if (gitlinks.Count == 0)
		{
			return [];
		}

		var sections = await ReadGitmodulesAsync(repo, commit);
		return gitlinks.Select(link =>
		{
			var section = sections.FirstOrDefault(candidate => candidate.Value.Path == link.Path);
			return new Gitlink(link.Path, link.Commit, section.Key, section.Value.Url);
		}).ToList();
	}

	/// <summary>The <c>submodule.&lt;name&gt;</c> sections of a commit's <c>.gitmodules</c>: name -> (path, url).</summary>
	private static async Task<Dictionary<string, (string? Path, string? Url)>> ReadGitmodulesAsync(GitClient repo, string commit)
	{
		var sections = new Dictionary<string, (string? Path, string? Url)>(StringComparer.Ordinal);
		var result = await repo.RunAsync(["config", "--blob", $"{commit}:{GitmodulesFile}", "-z", "--get-regexp", @"^submodule\..*\.(path|url)$"]);
		if (result.ExitCode != 0)
		{
			return sections; // no .gitmodules, or no such keys
		}

		// -z: <key> LF <value> NUL
		foreach (var item in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			var newline = item.IndexOf('\n');
			if (newline < 0)
			{
				continue;
			}

			var key = item[..newline];
			var value = item[(newline + 1)..];
			var lastDot = key.LastIndexOf('.');
			var name = key["submodule.".Length..lastDot];
			var variable = key[(lastDot + 1)..];
			var current = sections.GetValueOrDefault(name);
			sections[name] = variable == "path" ? (value.Trim().TrimEnd('/'), current.Url) : (current.Path, value.Trim());
		}

		return sections;
	}
}
