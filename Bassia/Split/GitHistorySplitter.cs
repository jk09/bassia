namespace Bassia.Split;

using Bassia.Git;
using Bassia.Storage;
using Bassia.Storage.Git;

/// <summary>
/// The git half of a split (<see cref="GitComponentStorage.SplitAsync"/>): the source branch's history is read with
/// <c>fast-export</c>, rewritten per part by <see cref="HistoryRewriter"/> and imported with <c>fast-import</c> into a new
/// bare repository per part, whose tip is then verified against the allocation. The source repository is never written.
/// </summary>
internal static class GitHistorySplitter
{
	public static async Task<IReadOnlyList<SplitPartResult>> SplitAsync(string root, GitComponentStore source, SplitHistoryRequest request)
	{
		var allocation = request.Allocation;
		var plan = allocation.Plan;
		var sourceDir = source.Location;
		var git = source.Git;

		var tags = await ListTagsAsync(sourceDir, allocation.Branch);
		var export = await FastExport.ReadAsync(sourceDir, allocation.Branch, tags.Where(item => item.Annotated).Select(item => item.Name).ToList());
		var renames = plan.FollowRenames ? await ListRenamesAsync(sourceDir, allocation.Branch, export) : [];
		var ownership = new Ownership(plan, allocation.TipOwner, export.Commits, renames);
		var rewriter = new HistoryRewriter(export, ownership, (parent, commit) => DiffAsync(sourceDir, export.Commits[parent].OriginalId, export.Commits[commit].OriginalId));
		var lightweight = tags.Where(item => !item.Annotated && export.IndexOf.ContainsKey(item.Commit))
			.Select(item => new LightweightTag(item.Name, export.IndexOf[item.Commit])).ToList();
		var ident = (await git.RunOrThrowAsync(["var", "GIT_COMMITTER_IDENT"])).Trim();
		var objects = Path.Combine(await git.RunOrThrowAsync(["rev-parse", "--absolute-git-dir"]), "objects");

		var parts = new List<SplitPartResult>();
		for (var index = 0; index < plan.Parts.Count; index++)
		{
			var part = plan.Parts[index];
			var history = await rewriter.RewriteAsync(index, allocation.Branch, $"{ComponentSplitter.Trailer}: {allocation.Source.Name}@",
				export.IndexOf[allocation.TipCommit], lightweight, ident, request.RecordMessage(part), request.Tag);

			var partDir = Path.Combine(root, part.Name);
			var gitDir = Path.Combine(partDir, ".git");
			await GitClient.In(root).RunOrThrowAsync(["init", "--bare", "--quiet", gitDir]);
			var target = GitClient.In(gitDir);

			// Borrow the source's objects while importing, then copy what the part reaches and stop borrowing.
			var alternates = Path.Combine(gitDir, "objects", "info", "alternates");
			await File.WriteAllTextAsync(alternates, objects.Replace('\\', '/') + "\n");
			await GitBinary.RunAsync(gitDir, ["fast-import", "--quiet"], history.Stream);
			await target.RunOrThrowAsync(["symbolic-ref", "HEAD", $"refs/heads/{allocation.Branch}"]);
			await target.RunOrThrowAsync(["repack", "-a", "-d", "-q"]);
			File.Delete(alternates);
			await target.RunOrThrowAsync(["fsck", "--connectivity-only", "--no-dangling", "--no-progress"]);
			if (part.Url is not null)
			{
				await target.RunOrThrowAsync(["remote", "add", "origin", part.Url]);
			}

			await VerifyTipAsync(gitDir, allocation.Branch, part.Name, allocation.Files[index]);
			parts.Add(new SplitPartResult(part.Name, partDir, allocation.Files[index].Count, allocation.Files[index].Sum(file => file.Size),
				history.Commits, await target.RunOrThrowAsync(["rev-parse", $"refs/heads/{allocation.Branch}"]),
				history.Tags, history.DroppedTags, ownership.ClaimedPaths(index)));
		}

		return parts;
	}

	/// <summary>The files at the tip of <paramref name="branch"/>, with mode, object and size (gitlinks included).</summary>
	internal static async Task<List<TipFile>> ListTipAsync(string repository, string branch)
	{
		var files = new List<TipFile>();
		foreach (var entry in GitBinary.SplitNul(await GitBinary.RunAsync(repository, ["ls-tree", "-r", "-z", "-l", "--full-tree", $"refs/heads/{branch}"])))
		{
			// <mode> SP <type> SP <object> SP+ <size> TAB <path>
			var tab = entry.IndexOf('\t');
			var fields = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			files.Add(new TipFile(entry[(tab + 1)..], fields[0], fields[2], long.TryParse(fields[3], out var size) ? size : 0));
		}

		return files;
	}

	/// <summary>The tip of the part must hold exactly its allocated files, each with the source tip's mode and content.</summary>
	private static async Task VerifyTipAsync(string gitDir, string branch, string part, IReadOnlyList<TipFile> expected)
	{
		var actual = (await ListTipAsync(gitDir, branch)).ToDictionary(file => file.Path, StringComparer.Ordinal);
		var wanted = expected.ToDictionary(file => file.Path, StringComparer.Ordinal);
		var missing = wanted.Keys.Where(path => !actual.ContainsKey(path)).ToList();
		var extra = actual.Keys.Where(path => !wanted.ContainsKey(path)).ToList();
		var differing = wanted.Values.Where(file => actual.TryGetValue(file.Path, out var got) && (got.Object != file.Object || got.Mode != file.Mode)).Select(file => file.Path).ToList();
		if (missing.Count + extra.Count + differing.Count > 0)
		{
			throw new GitException($"The rewritten history of '{part}' does not end in its allocated files " +
				$"(missing: {Sample(missing)}; unexpected: {Sample(extra)}; different: {Sample(differing)}); nothing was changed.");
		}
	}

	private static string Sample(List<string> paths) => paths.Count == 0 ? "none" : string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? $" and {paths.Count - 5} more" : "");

	private sealed record SourceTag(string Name, bool Annotated, string Commit);

	/// <summary>The tags reachable from the branch, each with the commit it (finally) points to.</summary>
	private static async Task<List<SourceTag>> ListTagsAsync(string repository, string branch)
	{
		var output = await GitClient.In(repository).RunOrThrowAsync(["for-each-ref", "--merged", $"refs/heads/{branch}",
			"--format=%(refname:strip=2)%09%(objecttype)%09%(objectname)%09%(*objectname)", "refs/tags/"]);
		return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(line => line.Split('\t'))
			.Where(fields => fields.Length >= 3 && (fields[1] == "commit" || (fields[1] == "tag" && fields.Length == 4)))
			.Select(fields => new SourceTag(fields[0], fields[1] == "tag", fields[1] == "tag" ? fields[3] : fields[2]))
			.ToList();
	}

	/// <summary>Every rename on the branch (git's rename detection), as stream indices.</summary>
	private static async Task<List<Rename>> ListRenamesAsync(string repository, string branch, FastExport export)
	{
		var fields = GitBinary.SplitNul(await GitBinary.RunAsync(repository,
			["log", "-M", "--diff-filter=R", "--name-status", "-z", "--format=%x01%H", $"refs/heads/{branch}"]));
		var renames = new List<Rename>();
		var commit = -1;
		for (var i = 0; i < fields.Count; i++)
		{
			var field = fields[i].TrimStart('\n');
			if (field.StartsWith('\x01'))
			{
				commit = export.IndexOf.TryGetValue(field[1..], out var index) ? index : -1;
			}
			else if (field.StartsWith('R') && i + 2 < fields.Count)
			{
				if (commit >= 0)
				{
					renames.Add(new Rename(commit, fields[i + 1], fields[i + 2]));
				}

				i += 2;
			}
		}

		return renames;
	}

	/// <summary>The changes of <paramref name="commit"/> relative to <paramref name="parent"/> (full ids).</summary>
	private static async Task<List<FileChange>> DiffAsync(string repository, string parent, string commit)
	{
		var fields = GitBinary.SplitNul(await GitBinary.RunAsync(repository, ["diff-tree", "-r", "-z", "--no-renames", "--no-commit-id", parent, commit]));
		var changes = new List<FileChange>();
		for (var i = 0; i + 1 < fields.Count; i += 2)
		{
			// :<old mode> <new mode> <old object> <new object> <status>
			var header = fields[i].TrimStart(':').Split(' ');
			var path = fields[i + 1];
			changes.Add(header[4].StartsWith('D') ? new FileChange('D', "", "", path) : new FileChange('M', header[1], header[3], path));
		}

		return changes;
	}
}
