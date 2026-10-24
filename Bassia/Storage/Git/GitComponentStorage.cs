namespace Bassia.Storage.Git;

using Bassia.Git;
using Bassia.Split;

/// <summary>
/// <see cref="IComponentStorage"/> on git: each component is a bare repository (or a regular clone) in the folder of its
/// name next to the meta-repo, <c>&lt;root&gt;/&lt;name&gt;</c>.
/// </summary>
internal sealed class GitComponentStorage(string root) : IComponentStorage
{
	public string Root { get; } = root;

	public bool Exists(string name) => Directory.Exists(LocationOf(name)) || File.Exists(LocationOf(name));

	public string LocationOf(string name) => Path.Combine(Root, name);

	public IComponentStore Open(string name) =>
		Directory.Exists(LocationOf(name))
			? new GitComponentStore(name, LocationOf(name))
			: throw new StorageException($"Component '{name}' has no repository at '{LocationOf(name)}'.");

	public async Task<IComponentStore> CreateAsync(string name, string defaultBranch, string url, string message)
	{
		var componentDir = LocationOf(name);
		try
		{
			await GitClient.In(Root).RunOrThrowAsync(["init", "--bare", "--quiet", $"--initial-branch={defaultBranch}", Path.Combine(componentDir, ".git")]);
			var git = GitClient.In(componentDir);
			var tree = await git.RunOrThrowAsync(["mktree"], "");
			var commit = await git.RunOrThrowAsync(["commit-tree", tree, "-m", message]);
			await git.RunOrThrowAsync(["update-ref", $"refs/heads/{defaultBranch}", commit]);
			if (url.Length > 0)
			{
				await GitClient.In(componentDir).RunOrThrowAsync(["remote", "add", "origin", url]);
			}
		}
		catch
		{
			DeleteDirectory(componentDir);
			throw;
		}

		return new GitComponentStore(name, componentDir);
	}

	public async Task<IComponentStore> ImportAsync(string name, string url)
	{
		var componentDir = LocationOf(name);
		var clone = await GitClient.In(Root).RunAsync(["clone", "--bare", url, Path.Combine(componentDir, ".git")]);
		if (clone.ExitCode != 0)
		{
			DeleteDirectory(componentDir);
			throw new GitException($"git clone failed: {clone.Error.Trim()}");
		}

		return new GitComponentStore(name, componentDir);
	}

	/// <summary>
	/// A single-branch bare clone of the parent without tags, given the wanted tags, cut back to the fork point (the later
	/// commits only the clone brought along are pruned) and detached from the parent: no <c>origin</c> points at it.
	/// </summary>
	public async Task<IComponentStore> ForkAsync(IComponentStore source, string name, string branch, string commit, IReadOnlyList<string> tags, string url)
	{
		var forkDir = LocationOf(name);
		try
		{
			var clone = await GitClient.In(Root).RunAsync(
				["clone", "--bare", "--single-branch", "--no-tags", "--branch", branch, source.Location, Path.Combine(forkDir, ".git")]);
			if (clone.ExitCode != 0)
			{
				throw new GitException($"git clone failed: {clone.Error.Trim()}");
			}

			var fork = GitClient.In(forkDir);
			if (tags.Count > 0)
			{
				await fork.RunOrThrowAsync(["fetch", "--quiet", "--no-tags", "origin", .. tags.Select(tag => $"+refs/tags/{tag}:refs/tags/{tag}")]);
			}

			await fork.RunOrThrowAsync(["remote", "remove", "origin"]);
			if (url.Length > 0)
			{
				await fork.RunOrThrowAsync(["remote", "add", "origin", url]);
			}

			var tip = await fork.RunOrThrowAsync(["rev-parse", $"refs/heads/{branch}"]);
			if (tip != commit)
			{
				await fork.RunOrThrowAsync(["update-ref", $"refs/heads/{branch}", commit]);
				await fork.RunOrThrowAsync(["reflog", "expire", "--expire=now", "--all"]);
				await fork.RunOrThrowAsync(["gc", "--quiet", "--prune=now"]);
			}
		}
		catch
		{
			DeleteDirectory(forkDir);
			throw;
		}

		return new GitComponentStore(name, forkDir);
	}

	public Task<IReadOnlyList<SplitPartResult>> SplitAsync(IComponentStore source, SplitHistoryRequest request) =>
		GitHistorySplitter.SplitAsync(Root, (GitComponentStore)source, request);

	public Task DeleteAsync(string name)
	{
		DeleteDirectory(LocationOf(name));
		return Task.CompletedTask;
	}

	public IStagingArea BeginStaging() => new Staging(this);

	public IComponentWorkspace OpenWorkspace(string name, string directory) => new GitWorkspace(directory, LocationOf(name));

	/// <summary>Deletes a folder tree, including the read-only pack files git leaves behind.</summary>
	internal static void DeleteDirectory(string directory)
	{
		if (!Directory.Exists(directory))
		{
			return;
		}

		foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(directory, recursive: true);
	}

	/// <summary>Bare clones in a hidden staging folder at the monorepo root, moved next to the meta-repo when promoted.</summary>
	private sealed class Staging(GitComponentStorage storage) : IStagingArea
	{
		private readonly string stagingDir = Path.Combine(storage.Root, $".unwind-staging-{Guid.NewGuid():N}"[..23]);

		public async Task<IComponentStore> ImportAsync(string name, string url)
		{
			var repoDir = Path.Combine(stagingDir, name);
			Directory.CreateDirectory(stagingDir);
			var clone = await GitClient.In(storage.Root).RunAsync(["clone", "--bare", "--quiet", url, Path.Combine(repoDir, ".git")]);
			if (clone.ExitCode != 0)
			{
				throw new GitException(clone.Error.Trim());
			}

			return new GitComponentStore(name, repoDir);
		}

		public Task<IComponentStore> PromoteAsync(IComponentStore staged)
		{
			var finalDir = storage.LocationOf(staged.Name);
			Directory.Move(staged.Location, finalDir);
			return Task.FromResult<IComponentStore>(new GitComponentStore(staged.Name, finalDir));
		}

		public void Dispose() => DeleteDirectory(stagingDir);
	}
}
