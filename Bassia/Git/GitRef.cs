namespace Bassia.Git;

internal enum GitRefKind { Branch, AnnotatedTag, LightweightTag }

/// <summary>A branch or tag of a repository, with the full hash of the commit it (ultimately) points at.</summary>
internal sealed record GitRef(string Name, GitRefKind Kind, string Commit, string Subject)
{
	public bool IsTag => Kind is GitRefKind.AnnotatedTag or GitRefKind.LightweightTag;

	/// <summary>Lists local branches followed by tags. A tag is annotated when the ref points at a tag object.</summary>
	public static async Task<IReadOnlyList<GitRef>> ListAsync(GitClient git)
	{
		const string format = "%(refname)%09%(objecttype)%09%(objectname)%09%(*objectname)%09%(contents:subject)";
		var output = await git.RunOrThrowAsync(["for-each-ref", $"--format={format}", "refs/heads", "refs/tags"]);

		var refs = new List<GitRef>();
		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var fields = line.TrimEnd('\r').Split('\t');
			if (fields.Length < 5)
			{
				continue;
			}

			var (refName, objectType, objectName, peeledName, subject) = (fields[0], fields[1], fields[2], fields[3], fields[4]);
			if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
			{
				refs.Add(new GitRef(refName["refs/heads/".Length..], GitRefKind.Branch, objectName, subject));
			}
			else if (refName.StartsWith("refs/tags/", StringComparison.Ordinal))
			{
				var annotated = objectType == "tag";
				refs.Add(new GitRef(refName["refs/tags/".Length..], annotated ? GitRefKind.AnnotatedTag : GitRefKind.LightweightTag,
					annotated ? peeledName : objectName, subject));
			}
		}

		return refs;
	}
}
