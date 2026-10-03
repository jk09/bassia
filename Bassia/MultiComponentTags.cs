namespace Bassia;

using Bassia.Git;
using Bassia.Integration;
using Bassia.Unwind;

/// <summary>A tag in one component: the commit it names, whether it is annotated, its date and subject.</summary>
internal sealed record ComponentTag(string Component, string Commit, bool Annotated, string Date, string Subject);

/// <summary>
/// A tag name across the monorepo with every component that has it. Identically named tags link commits of several
/// components into one selectable combination: an unwound submodule tree, an integration's result, or a baseline
/// tagged with <c>bassia tag create</c> for a run that spans components.
/// </summary>
internal sealed record MultiComponentTag(string Name, IReadOnlyList<ComponentTag> Components)
{
	public string Kind => KindOf(Name);

	/// <summary>The <c>-select</c> value that starts a run from this tag in every component that has it.</summary>
	public string Select => string.Join(",", Components.Select(component => $"{component.Component}@{Name}"));

	public static string KindOf(string name) =>
		name.StartsWith(SubmoduleUnwinder.TagPrefix, StringComparison.Ordinal) ? "unwind"
		: name.StartsWith(IntegrationRecord.RefPrefix, StringComparison.Ordinal) ? "integration"
		: name.StartsWith(RunMetadata.RefPrefix, StringComparison.Ordinal) ? "run"
		: name.StartsWith("split/", StringComparison.Ordinal) ? "split"
		: "other";

	/// <summary>
	/// Every tag of every component repository, grouped by name and sorted by it. The repositories are the only source:
	/// nothing else records which components a tag spans, so nothing can drift from them.
	/// </summary>
	public static async Task<IReadOnlyList<MultiComponentTag>> ReadAllAsync(Monorepo monorepo)
	{
		const string format = "%(refname:strip=2)%09%(objecttype)%09%(objectname)%09%(*objectname)%09%(creatordate:iso-strict)%09%(contents:subject)";
		var byName = new SortedDictionary<string, List<ComponentTag>>(StringComparer.Ordinal);
		foreach (var component in monorepo.Components)
		{
			var sourceDir = monorepo.SourceRepoDir(component.Name);
			if (!Directory.Exists(sourceDir))
			{
				continue;
			}

			var output = await GitClient.In(sourceDir).RunOrThrowAsync(["for-each-ref", $"--format={format}", "refs/tags"]);
			foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				var fields = line.TrimEnd('\r').Split('\t');
				if (fields.Length < 6)
				{
					continue;
				}

				var annotated = fields[1] == "tag";
				if (!byName.TryGetValue(fields[0], out var list))
				{
					byName[fields[0]] = list = [];
				}

				list.Add(new ComponentTag(component.Name, annotated ? fields[3] : fields[2], annotated, fields[4], fields[5]));
			}
		}

		return byName.Select(entry => new MultiComponentTag(entry.Key, entry.Value)).ToList();
	}
}
