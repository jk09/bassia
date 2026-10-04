namespace Bassia.Prompt;

/// <summary>
/// A skill: reusable instructions for a kind of ask, stored in the meta-repo as <c>.bassia/skills/&lt;name&gt;/SKILL.md</c>.
/// <see cref="Body"/> is the Markdown after the front matter.
/// </summary>
internal sealed record Skill(string Name, string Description, string Body, string Path);

/// <summary>
/// The skills of a monorepo, in the shape Claude Code uses: a folder per skill holding <c>SKILL.md</c>, whose optional
/// front matter (between <c>---</c> lines) gives <c>name</c> and <c>description</c>. The LLM is shown every skill's
/// name and description and asks for the bodies it needs, so many skills cost little prompt.
/// </summary>
internal static class SkillStore
{
	public const string FolderName = "skills";
	public const string FileName = "SKILL.md";

	public static string DirectoryOf(string root) => System.IO.Path.Combine(root, Monorepo.MetaRepoFolderName, FolderName);

	/// <summary>The skills under <paramref name="root"/>'s meta-repo by name; none outside a monorepo.</summary>
	public static IReadOnlyList<Skill> Load(string? root)
	{
		if (root is null || !Directory.Exists(DirectoryOf(root)))
		{
			return [];
		}

		return Directory.EnumerateDirectories(DirectoryOf(root))
			.Select(folder => System.IO.Path.Combine(folder, FileName))
			.Where(File.Exists)
			.Select(path => Parse(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)!), File.ReadAllText(path), path))
			.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public static Skill? Find(IReadOnlyList<Skill> skills, string name) =>
		skills.FirstOrDefault(skill => string.Equals(skill.Name, name.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

	/// <summary>A <c>SKILL.md</c>: front matter <c>name:</c>/<c>description:</c> lines when present, the rest is the body.</summary>
	internal static Skill Parse(string folderName, string text, string path)
	{
		var lines = text.Replace("\r\n", "\n").Split('\n');
		var name = folderName;
		var description = "";
		var bodyStart = 0;
		if (lines.Length > 0 && lines[0].Trim() == "---")
		{
			var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
			if (end > 0)
			{
				foreach (var line in lines[1..end])
				{
					var colon = line.IndexOf(':');
					if (colon <= 0)
					{
						continue;
					}

					var key = line[..colon].Trim();
					var value = line[(colon + 1)..].Trim().Trim('"', '\'');
					if (key.Equals("name", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
					{
						name = value;
					}
					else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
					{
						description = value;
					}
				}

				bodyStart = end + 1;
			}
		}

		var body = string.Join('\n', lines[bodyStart..]).Trim();
		if (description.Length == 0)
		{
			// Without front matter, the first line of prose describes the skill.
			description = body.Split('\n').Select(line => line.Trim().TrimStart('#').Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
		}

		return new Skill(name, description, body, path);
	}
}
