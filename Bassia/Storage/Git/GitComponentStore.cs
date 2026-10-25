namespace Bassia.Storage.Git;

using Bassia.Git;
using Bassia.Unwind;

/// <summary>
/// <see cref="IComponentStore"/> on a git repository - the bare repository (or clone) next to the meta-repo that is a
/// component's source of truth. Everything goes through plumbing commands, so a bare repository will do.
/// </summary>
internal sealed class GitComponentStore(string name, string repoDir) : IComponentStore
{
	private readonly GitClient git = GitClient.In(repoDir);

	public string Name { get; } = name;

	public string Location { get; } = repoDir;

	public GitClient Git => git;

	// ----- branches -----

	public async Task<string?> DefaultBranchAsync()
	{
		var head = await git.RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"]);
		return head.ExitCode == 0 && head.Output.Trim().Length > 0 ? head.Output.Trim() : null;
	}

	public async Task<IReadOnlyList<StoredBranch>> ListBranchesAsync() =>
		(await GitRef.ListAsync(git)).Where(item => item.Kind == GitRefKind.Branch).Select(item => new StoredBranch(item.Name, item.Commit, item.Subject)).ToList();

	public async Task<string?> BranchTipAsync(string branch)
	{
		var tip = await git.RunAsync(["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}"]);
		return tip.ExitCode == 0 ? tip.Output.Trim() : null;
	}

	public Task SetBranchAsync(string branch, string commit, string? expected = null) =>
		git.RunOrThrowAsync(expected is null
			? ["update-ref", "-m", "bassia", $"refs/heads/{branch}", commit]
			: ["update-ref", "-m", "bassia", $"refs/heads/{branch}", commit, expected]);

	// ----- tags -----

	public async Task<IReadOnlyList<StoredTag>> ListTagsAsync(string? prefix = null) =>
		(await ReadTagsAsync("refs/tags")).Where(tag => prefix is null || tag.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();

	public async Task<StoredTag?> GetTagAsync(string name) => (await ReadTagsAsync($"refs/tags/{name}")).FirstOrDefault(tag => tag.Name == name);

	public async Task CreateTagAsync(string name, string commit, string message)
	{
		var result = await git.RunAsync(["tag", "-a", name, "-m", message, commit]);
		if (result.ExitCode != 0)
		{
			throw new GitException($"git tag failed in component '{Name}': {result.Error.Trim()}");
		}
	}

	public Task DeleteTagAsync(string name) => git.RunOrThrowAsync(["tag", "-d", name]);

	private async Task<IReadOnlyList<StoredTag>> ReadTagsAsync(string pattern)
	{
		const string format = "%(refname:strip=2)%09%(objecttype)%09%(objectname)%09%(*objecttype)%09%(*objectname)%09%(creatordate:iso-strict)%09%(contents:subject)";
		var output = await git.RunOrThrowAsync(["for-each-ref", "--sort=refname", $"--format={format}", pattern]);
		var tags = new List<StoredTag>();
		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var fields = line.TrimEnd('\r').Split('\t');
			if (fields.Length < 7)
			{
				continue;
			}

			// A tag of something other than a commit (a tree, a blob) is no version of the component.
			var annotated = fields[1] == "tag";
			if (annotated ? fields[3] != "commit" : fields[1] != "commit")
			{
				continue;
			}

			tags.Add(new StoredTag(fields[0], annotated ? fields[4] : fields[2], annotated, fields[6], fields[5]));
		}

		return tags;
	}

	// ----- the commit DAG -----

	public async Task<StoredCommit?> GetCommitAsync(string id)
	{
		var type = await git.RunAsync(["cat-file", "-t", id]);
		if (type.ExitCode != 0 || type.Output.Trim() != "commit")
		{
			return null;
		}

		return ParseCommits(await git.RunOrThrowAsync(["log", "-1", "--format=%H%x00%P%x00%B%x01", id])).FirstOrDefault();
	}

	public async Task<IReadOnlyList<StoredObject>> FindObjectsAsync(string prefix)
	{
		var candidates = (await git.RunOrThrowAsync(["rev-parse", $"--disambiguate={prefix}"]))
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var objects = new List<StoredObject>();
		foreach (var candidate in candidates)
		{
			objects.Add(new StoredObject(candidate, (await git.RunOrThrowAsync(["cat-file", "-t", candidate])).Trim()));
		}

		return objects;
	}

	public async Task<ResolvedRef?> ResolveRefAsync(string name)
	{
		if (name.StartsWith('-'))
		{
			return null;
		}

		var symbolic = await git.RunAsync(["rev-parse", "--symbolic-full-name", name]);
		var fullName = symbolic.ExitCode == 0 ? symbolic.Output.Trim() : "";
		if (!fullName.StartsWith("refs/heads/", StringComparison.Ordinal) && !fullName.StartsWith("refs/tags/", StringComparison.Ordinal))
		{
			return null;
		}

		var commit = await git.RunAsync(["rev-parse", "--verify", "--quiet", $"{fullName}^{{commit}}"]);
		if (commit.ExitCode != 0)
		{
			return null;
		}

		var kind = fullName.StartsWith("refs/heads/", StringComparison.Ordinal)
			? RefKind.Branch
			: (await git.RunOrThrowAsync(["cat-file", "-t", fullName])).Trim() == "tag" ? RefKind.AnnotatedTag : RefKind.LightweightTag;
		return new ResolvedRef(fullName, kind, commit.Output.Trim());
	}

	public async Task<bool> IsAncestorAsync(string ancestor, string descendant) =>
		(await git.RunAsync(["merge-base", "--is-ancestor", ancestor, descendant])).ExitCode == 0;

	public async Task<IReadOnlyList<StoredCommit>> HistoryAsync(string tip) =>
		ParseCommits(await git.RunOrThrowAsync(["log", "--topo-order", "--format=%H%x00%P%x00%B%x01", tip]));

	internal static List<StoredCommit> ParseCommits(string output) =>
		output.Split('\x01', StringSplitOptions.RemoveEmptyEntries)
			.Select(entry => entry.TrimStart('\n', '\r').Split('\0'))
			.Where(fields => fields.Length >= 3 && fields[0].Length > 0)
			.Select(fields => new StoredCommit(fields[0], fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), fields[2].TrimEnd('\n')))
			.ToList();

	// ----- content -----

	public async Task<IReadOnlyList<StoredFile>> ListFilesAsync(string commit)
	{
		var files = new List<StoredFile>();
		foreach (var entry in Split.GitBinary.SplitNul(await Split.GitBinary.RunAsync(Location, ["ls-tree", "-r", "-z", "-l", "--full-tree", commit])))
		{
			// <mode> SP <type> SP <object> SP+ <size> TAB <path>
			var tab = entry.IndexOf('\t');
			var fields = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (fields[1] == "blob")
			{
				files.Add(new StoredFile(entry[(tab + 1)..], fields[0], fields[2], long.TryParse(fields[3], out var size) ? size : 0));
			}
		}

		return files;
	}

	public async Task<IReadOnlyList<NestedUnit>> ListNestedAsync(string commit) =>
		(await Submodules.ReadAsync(git, commit)).Select(link => new NestedUnit(link.Path, link.Commit, link.Name, link.Url)).ToList();

	/// <summary>
	/// A commit on top of <paramref name="commit"/> with the same tree minus the gitlinks at <paramref name="paths"/> and
	/// their <c>.gitmodules</c> sections (the file goes when nothing is left in it). Built in a scratch index, so a bare
	/// repository will do.
	/// </summary>
	public async Task<string> CommitWithoutNestedAsync(string commit, IReadOnlyList<string> paths, string message)
	{
		var names = (await ListNestedAsync(commit)).Where(unit => paths.Contains(unit.Path)).Select(unit => unit.Name).OfType<string>().Distinct().ToList();
		var scratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"bassia-{Guid.NewGuid():N}")).FullName;
		var index = Path.Combine(scratch, "index");

		// update-index wants a work tree even for index-only changes; a bare repository has none, so it gets an empty one.
		var workTree = Directory.CreateDirectory(Path.Combine(scratch, "empty-work-tree")).FullName;
		var gitDir = (await git.RunOrThrowAsync(["rev-parse", "--absolute-git-dir"])).Trim();
		var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index, ["GIT_DIR"] = gitDir, ["GIT_WORK_TREE"] = workTree };
		try
		{
			await git.RunOrThrowAsync(["read-tree", commit], environment: environment);
			await git.RunOrThrowAsync(["update-index", "--force-remove", "--", .. paths], environment: environment);

			var gitmodules = await git.RunAsync(["cat-file", "-p", $"{commit}:{Submodules.GitmodulesFile}"]);
			if (gitmodules.ExitCode == 0)
			{
				var file = Path.Combine(scratch, "gitmodules");
				await File.WriteAllTextAsync(file, gitmodules.Output);
				foreach (var name in names)
				{
					await git.RunAsync(["config", "--file", file, "--remove-section", $"submodule.{name}"]);
				}

				var remaining = await File.ReadAllTextAsync(file);
				if (remaining.Split('\n').Any(line => line.TrimStart().StartsWith('[')))
				{
					var blob = await git.RunOrThrowAsync(["hash-object", "-w", file]);
					await git.RunOrThrowAsync(["update-index", "--add", "--cacheinfo", $"100644,{blob},{Submodules.GitmodulesFile}"], environment: environment);
				}
				else
				{
					await git.RunOrThrowAsync(["update-index", "--force-remove", "--", Submodules.GitmodulesFile], environment: environment);
				}
			}

			var tree = await git.RunOrThrowAsync(["write-tree"], environment: environment);
			return await git.RunOrThrowAsync(["commit-tree", tree, "-p", commit, "-F", "-"], message);
		}
		finally
		{
			Directory.Delete(scratch, recursive: true);
		}
	}

	public async Task<bool> EnsureCommitAsync(string commit, string url)
	{
		if (await HasCommitAsync(commit))
		{
			return true;
		}

		await git.RunAsync(["fetch", "--quiet", "--no-tags", url, commit]);
		return await HasCommitAsync(commit);
	}

	private async Task<bool> HasCommitAsync(string commit) => (await git.RunAsync(["cat-file", "-e", $"{commit}^{{commit}}"])).ExitCode == 0;

	public async Task<string?> UpstreamAsync()
	{
		if (!Directory.Exists(Location))
		{
			return null;
		}

		var result = await git.RunAsync(["config", "--get", "remote.origin.url"]);
		return result.ExitCode == 0 && result.Output.Trim().Length > 0 ? result.Output.Trim() : null;
	}

	// ----- merging and comparing -----

	/// <summary>
	/// <c>git merge-tree --write-tree</c> (git 2.38+): the real three-way merge in the object database. Exit code 0 is a
	/// clean merge and prints the tree, 1 is a conflict and lists the conflicted paths after the tree. A file merge driver
	/// is given to it as configuration, so it is probed without touching the repository.
	/// </summary>
	public async Task<MergeResult> MergeAsync(string ours, string theirs, FileMergeDriver? driver = null)
	{
		var merge = await git.RunAsync([.. GitMergeDriver.Options(driver), "merge-tree", "--write-tree", "--name-only", "--no-messages", ours, theirs]);
		var lines = merge.Output.Replace("\r", "").Split('\n');
		var warnings = driver?.Warnings(merge.Error) ?? [];
		return merge.ExitCode switch
		{
			0 => new MergeResult(true, lines[0].Trim(), [], warnings),
			1 => new MergeResult(false, lines[0].Trim(), lines.Skip(1).TakeWhile(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToList(), warnings),
			_ => throw new GitException($"git merge-tree failed in '{Location}' (merging needs git 2.38 or later): {merge.Error.Trim()}")
		};
	}

	/// <summary>An unreferenced commit of the tree; git's garbage collection removes it in due course.</summary>
	public Task<string> CommitSnapshotAsync(string snapshot, IReadOnlyList<string> parents, string message) =>
		git.RunOrThrowAsync(["-c", "user.name=Bassia", "-c", "user.email=bassia@bassia.invalid",
			"commit-tree", snapshot, .. parents.SelectMany(parent => new[] { "-p", parent }), "-m", message]);

	public async Task<string?> MergeBaseAsync(string first, string second)
	{
		var result = await git.RunAsync(["merge-base", first, second]);
		return result.ExitCode == 0 && result.Output.Trim().Length > 0 ? result.Output.Trim() : null;
	}

	public async Task<IReadOnlyList<string>> ChangedPathsAsync(string from, string to) =>
		Lines(await git.RunOrThrowAsync(["diff", "--name-only", "--no-renames", from, to]));

	public async Task<string> DiffAsync(string from, string to) => (await git.RunAsync(["diff", "--no-color", from, to])).Output;

	internal static List<string> Lines(string text) =>
		text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

	// ----- working copies -----

	public async Task<IComponentWorkspace> CheckoutAsync(string commit, string directory, string branch)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(directory))!);
		await GitClient.In(Path.GetDirectoryName(Path.GetFullPath(directory))!).RunOrThrowAsync(["clone", "--quiet", "--no-checkout", Location, directory]);
		await GitClient.In(directory).RunOrThrowAsync(["checkout", "--quiet", "-b", branch, commit]);
		return new GitWorkspace(directory, Location);
	}
}

/// <summary>
/// <see cref="IComponentWorkspace"/> as a git clone of the component's repository: commits and tags are made in the
/// clone and published by pushing them back.
/// </summary>
internal sealed class GitWorkspace(string directory, string sourceRepoDir) : IComponentWorkspace
{
	private readonly GitClient checkout = GitClient.In(directory);

	public string Directory { get; } = directory;

	/// <summary>
	/// Hides the path from the clone's index without touching its tracked <c>.gitignore</c>. No trailing slash: git sees a
	/// symlink as a file, and a <c>/path/</c> pattern matches directories only, so the owner would commit the link itself.
	/// </summary>
	public async Task HideAsync(string path)
	{
		var excludePath = Path.Combine(Directory, ".git", "info", "exclude");
		System.IO.Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
		await File.AppendAllTextAsync(excludePath, $"/{path.Replace('\\', '/')}\n");
	}

	public async Task<string?> CommitAsync(string message)
	{
		await checkout.RunOrThrowAsync(["add", "--all"]);
		if (await MergeInProgressAsync())
		{
			await checkout.RunOrThrowAsync(["commit", "--quiet", "--no-edit", "-m", message]);
			return await HeadAsync();
		}

		var status = await checkout.RunOrThrowAsync(["status", "--porcelain"]);
		if (status.Length == 0)
		{
			return null;
		}

		await checkout.RunOrThrowAsync(["commit", "--quiet", "-m", message]);
		return await HeadAsync();
	}

	public async Task<IReadOnlyList<string>> ListTagsAsync(string prefix) =>
		(await checkout.RunOrThrowAsync(["tag", "--list"])).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(tag => tag.StartsWith(prefix, StringComparison.Ordinal)).ToList();

	public async Task<string?> TagCommitAsync(string name)
	{
		var existing = await checkout.RunAsync(["rev-parse", "--quiet", "--verify", $"refs/tags/{name}^{{commit}}"]);
		return existing.ExitCode == 0 ? existing.Output.Trim() : null;
	}

	public Task TagAsync(string name, string commit, string message) => checkout.RunOrThrowAsync(["tag", "-a", name, "-m", message, commit]);

	public Task PublishAsync(string branch, IReadOnlyList<string> tags) =>
		checkout.RunOrThrowAsync(["push", "--quiet", sourceRepoDir, $"refs/heads/{branch}:refs/heads/{branch}", .. tags.Select(tag => $"refs/tags/{tag}:refs/tags/{tag}")]);

	public Task<string> HeadAsync() => checkout.RunOrThrowAsync(["rev-parse", "HEAD"]);

	/// <summary>
	/// <c>git status</c> of the working tree, untracked files included. The agent never commits - Bassia does once it
	/// exits - so the working tree is all it changed. A missing or broken checkout changed nothing.
	/// </summary>
	public async Task<IReadOnlyList<string>> ChangedPathsAsync()
	{
		if (!System.IO.Directory.Exists(Directory))
		{
			return [];
		}

		var status = await checkout.RunAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames"]);
		if (status.ExitCode != 0)
		{
			return [];
		}

		// Each entry is "XY <path>", NUL-terminated.
		return status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
			.Where(entry => entry.Length > 3)
			.Select(entry => entry[3..])
			.Distinct(StringComparer.Ordinal)
			.ToList();
	}

	public async Task<bool> ContainsAsync(string commit) =>
		(await checkout.RunAsync(["cat-file", "-e", $"{commit}^{{commit}}"])).ExitCode == 0
		&& (await checkout.RunAsync(["merge-base", "--is-ancestor", commit, "HEAD"])).ExitCode == 0;

	public async Task<bool> FetchTagAsync(string tag) =>
		(await checkout.RunAsync(["fetch", "--quiet", "--no-tags", sourceRepoDir, $"+refs/tags/{tag}:refs/tags/{tag}"])).ExitCode == 0;

	public async Task<MergeOutcome> MergeAsync(string commit, WorkspaceMerge options)
	{
		List<string> arguments = [.. GitMergeDriver.Options(options.Driver)];
		if (options.Diff3)
		{
			arguments.AddRange(["-c", "merge.conflictStyle=diff3"]);
		}

		arguments.AddRange(["merge", "--quiet"]);
		if (!options.FastForward)
		{
			arguments.Add("--no-ff");
		}

		arguments.AddRange(options.Commit ? ["--no-edit", "-m", options.Message] : ["--no-commit"]);
		var merge = await checkout.RunAsync([.. arguments, commit]);
		var warnings = options.Driver?.Warnings(merge.Error) ?? [];
		if (merge.ExitCode == 0)
		{
			return new MergeOutcome(MergeStatus.Merged, [], warnings);
		}

		// Only a conflict leaves the merge open. Anything else (no git identity, local changes the merge would overwrite)
		// stops git before it starts, and fails with git's own message.
		var error = (merge.Error + merge.Output).Trim();
		error = error.Length == 0 ? $"git merge exited with {merge.ExitCode}" : error;
		return await MergeInProgressAsync()
			? new MergeOutcome(MergeStatus.Conflicted, await ConflictedPathsAsync(), warnings, error)
			: new MergeOutcome(MergeStatus.Failed, [], warnings, error);
	}

	public async Task<bool> MergeInProgressAsync() =>
		(await checkout.RunAsync(["rev-parse", "--quiet", "--verify", "MERGE_HEAD"])).ExitCode == 0;

	public async Task<IReadOnlyList<string>> ConflictedPathsAsync() =>
		GitComponentStore.Lines((await checkout.RunAsync(["diff", "--name-only", "--diff-filter=U"])).Output);

	/// <summary>
	/// <c>git merge --abort</c> alone keeps files a failed resolver created, and the next <c>git add --all</c> would then
	/// commit them; so the reset is hard and untracked files go too.
	/// </summary>
	public async Task ResetAsync(string commit)
	{
		if (await MergeInProgressAsync())
		{
			await checkout.RunAsync(["merge", "--abort"]);
		}

		await checkout.RunOrThrowAsync(["reset", "--quiet", "--hard", commit]);
		await checkout.RunOrThrowAsync(["clean", "--quiet", "-d", "--force", "-x"]);
	}

	public async Task AbortMergeAsync()
	{
		if (await MergeInProgressAsync())
		{
			await checkout.RunOrThrowAsync(["merge", "--abort"]);
		}
	}

	public async Task<IReadOnlyList<string>> ChangedPathsAsync(string from, string to) =>
		from == to ? [] : GitComponentStore.Lines(await checkout.RunOrThrowAsync(["diff", "--name-only", from, to]));

	public async Task<string?> MergeBaseAsync(string first, string second)
	{
		var result = await checkout.RunAsync(["merge-base", first, second]);
		return result.ExitCode == 0 && result.Output.Trim().Length > 0 ? result.Output.Trim() : null;
	}

	public async Task<IReadOnlyList<StoredCommit>> LogAsync(string from, string to) =>
		GitComponentStore.ParseCommits((await checkout.RunAsync(["log", "--format=%H%x00%P%x00%B%x01", $"{from}..{to}"])).Output);

	public async Task<string> DiffAsync(string from, string to) => (await checkout.RunAsync(["diff", "--no-color", from, to])).Output;
}

/// <summary>A <see cref="FileMergeDriver"/> as git configuration: the driver's definition and an attributes file assigning it its files.</summary>
internal static class GitMergeDriver
{
	public static IReadOnlyList<string> Options(FileMergeDriver? driver)
	{
		if (driver is null)
		{
			return [];
		}

		var attributes = string.Concat(driver.Extensions.Select(extension => $"*.{extension} merge={driver.Name}\n"));
		var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(attributes)))[..16];
		var attributesFile = Path.Combine(Path.GetTempPath(), $"bassia-merge-{driver.Name}-{hash}.gitattributes");
		if (!File.Exists(attributesFile) || File.ReadAllText(attributesFile) != attributes)
		{
			var temporary = attributesFile + $".{Guid.NewGuid():N}";
			File.WriteAllText(temporary, attributes);
			File.Move(temporary, attributesFile, overwrite: true);
		}

		return
		[
			"-c", $"merge.{driver.Name}.name={driver.Name} merge driver",
			"-c", $"merge.{driver.Name}.driver={driver.Command}",
			"-c", $"core.attributesFile={attributesFile}"
		];
	}
}
