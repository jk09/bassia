namespace Bassia.Storage.Cloud;

using Bassia.Git;
using Bassia.Storage.Git;

/// <summary>
/// <see cref="IComponentStore"/> of the cloud storage: reads, merges and object-only writes (commits referenced by
/// nothing yet) run on the node's cache, brought up to the table once per store; every ref change is published
/// (<see cref="CloudComponentStorage.PublishAsync"/>) under a compare-and-swap.
/// </summary>
internal sealed class CloudComponentStore : IComponentStore
{
	private readonly CloudComponentStorage storage;
	private readonly Lazy<Task> synced;

	public CloudComponentStore(CloudComponentStorage storage, string name)
	{
		this.storage = storage;
		Name = name;
		Cache = new GitComponentStore(name, storage.LocationOf(name));
		synced = new Lazy<Task>(() => storage.SyncAsync(name));
	}

	public string Name { get; }

	/// <summary>The cache folder (see <see cref="CloudComponentStorage.LocationOf"/>).</summary>
	public string Location => Cache.Location;

	/// <summary>The node's cache of the component: a bare repository real git reads.</summary>
	internal GitComponentStore Cache { get; }

	/// <summary>The cache, brought up to the table the first time a store is used.</summary>
	internal async Task<GitComponentStore> SyncedAsync()
	{
		await synced.Value;
		return Cache;
	}

	private async Task<T> ReadAsync<T>(Func<GitComponentStore, Task<T>> read) => await read(await SyncedAsync());

	private Task PublishAsync(params RefUpdate[] updates) => storage.PublishAsync(Name, updates);

	// ----- branches -----

	public Task<string?> DefaultBranchAsync() => ReadAsync(cache => cache.DefaultBranchAsync());

	public Task<IReadOnlyList<StoredBranch>> ListBranchesAsync() => ReadAsync(cache => cache.ListBranchesAsync());

	public Task<string?> BranchTipAsync(string branch) => ReadAsync(cache => cache.BranchTipAsync(branch));

	public async Task SetBranchAsync(string branch, string commit, string? expected = null)
	{
		await SyncedAsync();
		await PublishAsync(new RefUpdate($"refs/heads/{branch}", commit, expected is null ? RefExpect.Any : RefExpect.Value, expected));
	}

	// ----- tags -----

	public Task<IReadOnlyList<StoredTag>> ListTagsAsync(string? prefix = null) => ReadAsync(cache => cache.ListTagsAsync(prefix));

	public Task<StoredTag?> GetTagAsync(string name) => ReadAsync(cache => cache.GetTagAsync(name));

	public Task<string?> TagMessageAsync(string name) => ReadAsync(cache => cache.TagMessageAsync(name));

	/// <summary>The tag object is made in the cache and published only while no such tag exists; otherwise it is dropped again.</summary>
	public async Task CreateTagAsync(string name, string commit, string message)
	{
		var cache = await SyncedAsync();
		await cache.CreateTagAsync(name, commit, message);
		var tag = await cache.Git.RunOrThrowAsync(["rev-parse", $"refs/tags/{name}"]);
		try
		{
			await PublishAsync(new RefUpdate($"refs/tags/{name}", tag, RefExpect.Absent));
		}
		catch (StorageException)
		{
			await cache.Git.RunAsync(["update-ref", "-d", $"refs/tags/{name}", tag]);
			throw;
		}
	}

	public async Task DeleteTagAsync(string name)
	{
		await SyncedAsync();
		await PublishAsync(new RefUpdate($"refs/tags/{name}", null, RefExpect.Any));
	}

	// ----- the commit DAG -----

	public Task<StoredCommit?> GetCommitAsync(string id) => ReadAsync(cache => cache.GetCommitAsync(id));

	public Task<IReadOnlyList<StoredObject>> FindObjectsAsync(string prefix) => ReadAsync(cache => cache.FindObjectsAsync(prefix));

	public Task<ResolvedRef?> ResolveRefAsync(string name) => ReadAsync(cache => cache.ResolveRefAsync(name));

	public Task<bool> IsAncestorAsync(string ancestor, string descendant) => ReadAsync(cache => cache.IsAncestorAsync(ancestor, descendant));

	public Task<IReadOnlyList<StoredCommit>> HistoryAsync(string tip) => ReadAsync(cache => cache.HistoryAsync(tip));

	// ----- content -----

	public Task<IReadOnlyList<StoredFile>> ListFilesAsync(string commit) => ReadAsync(cache => cache.ListFilesAsync(commit));

	public Task<byte[]?> ReadFileAsync(string commit, string path) => ReadAsync(cache => cache.ReadFileAsync(commit, path));

	public Task<IReadOnlyList<NestedUnit>> ListNestedAsync(string commit) => ReadAsync(cache => cache.ListNestedAsync(commit));

	public Task<string> CommitWithoutNestedAsync(string commit, IReadOnlyList<string> paths, string message) =>
		ReadAsync(cache => cache.CommitWithoutNestedAsync(commit, paths, message));

	/// <summary>Fetched into the cache only: the objects are published with the first ref that reaches them.</summary>
	public Task<bool> EnsureCommitAsync(string commit, string url) => ReadAsync(cache => cache.EnsureCommitAsync(commit, url));

	public Task<string?> UpstreamAsync() => ReadAsync(cache => cache.UpstreamAsync());

	/// <summary>None: the cloud holds the content of every component.</summary>
	public Task<IReadOnlyList<string>> MissingContentAsync(string commit, IReadOnlyList<string>? paths = null) => Task.FromResult<IReadOnlyList<string>>([]);

	public Task HydrateAsync(string commit, IReadOnlyList<string> paths) => Task.CompletedTask;

	/// <summary>Fetches the upstream's branches and tags aside in the cache, then publishes them over the store's (the upstream wins).</summary>
	public async Task FetchUpstreamAsync()
	{
		var cache = await SyncedAsync();
		const string Aside = "refs/bassia-upstream/";
		var fetch = await cache.Git.RunAsync(["fetch", "--quiet", "--no-tags", "origin", $"+refs/heads/*:{Aside}heads/*", $"+refs/tags/*:{Aside}tags/*"]);
		try
		{
			if (fetch.ExitCode != 0)
			{
				throw new GitException($"Could not fetch component '{Name}' from its upstream: {fetch.Error.Trim()}");
			}

			var fetched = await CloudComponentStorage.LocalRefsAsync(cache.Git, Aside);
			await storage.PublishAsync(Name, fetched.Select(pair => new RefUpdate("refs/" + pair.Key[Aside.Length..], pair.Value, RefExpect.Any)).ToList());
		}
		finally
		{
			var leftovers = await CloudComponentStorage.LocalRefsAsync(cache.Git, Aside);
			if (leftovers.Count > 0)
			{
				await cache.Git.RunOrThrowAsync(["update-ref", "--stdin"], string.Concat(leftovers.Keys.Select(name => $"delete {name}\n")));
			}
		}
	}

	public async Task PushUpstreamAsync(string branch, IReadOnlyList<string> tags) => await (await SyncedAsync()).PushUpstreamAsync(branch, tags);

	// ----- merging and comparing -----

	public Task<MergeResult> MergeAsync(string ours, string theirs, FileMergeDriver? driver = null) => ReadAsync(cache => cache.MergeAsync(ours, theirs, driver));

	public Task<string> CommitSnapshotAsync(string snapshot, IReadOnlyList<string> parents, string message) =>
		ReadAsync(cache => cache.CommitSnapshotAsync(snapshot, parents, message));

	public Task<string?> MergeBaseAsync(string first, string second) => ReadAsync(cache => cache.MergeBaseAsync(first, second));

	public Task<IReadOnlyList<string>> ChangedPathsAsync(string from, string to) => ReadAsync(cache => cache.ChangedPathsAsync(from, to));

	public Task<string> DiffAsync(string from, string to) => ReadAsync(cache => cache.DiffAsync(from, to));

	public Task<IReadOnlyList<FileStat>> DiffStatAsync(string? from, string to) => ReadAsync(cache => cache.DiffStatAsync(from, to));

	public Task<IReadOnlyList<LogEntry>> LogAsync(LogQuery query) => ReadAsync(cache => cache.LogAsync(query));

	// ----- working copies -----

	public async Task<IComponentWorkspace> CheckoutAsync(string commit, string directory, string branch, IReadOnlyList<string>? load = null)
	{
		var cache = await SyncedAsync();
		await cache.CheckoutAsync(commit, directory, branch, load);
		return storage.OpenWorkspace(Name, directory);
	}
}

/// <summary>
/// <see cref="IComponentWorkspace"/> of the cloud storage: a git working copy cloned from the node's cache. Publishing
/// pushes into the cache aside (nothing a reader sees), then publishes the refs - the branch only as a fast-forward, a
/// tag only when new or unchanged, as a push to git would.
/// </summary>
internal sealed class CloudWorkspace(CloudComponentStorage storage, string name, GitWorkspace workspace) : IComponentWorkspace
{
	public string Directory => workspace.Directory;

	public async Task PublishAsync(string branch, IReadOnlyList<string> tags)
	{
		await storage.SyncAsync(name);
		var aside = $"refs/bassia-incoming/{Guid.NewGuid():N}/";
		var cache = GitClient.In(storage.LocationOf(name));
		await GitClient.In(Directory).RunOrThrowAsync(["push", "--quiet", storage.LocationOf(name),
			$"refs/heads/{branch}:{aside}heads/{branch}", .. tags.Select(tag => $"refs/tags/{tag}:{aside}tags/{tag}")]);
		try
		{
			var pushed = await CloudComponentStorage.LocalRefsAsync(cache, aside);
			await storage.PublishAsync(name, pushed.Select(pair =>
			{
				var target = "refs/" + pair.Key[aside.Length..];
				return new RefUpdate(target, pair.Value, target.StartsWith("refs/heads/", StringComparison.Ordinal) ? RefExpect.FastForward : RefExpect.AbsentOrSame);
			}).ToList());
		}
		finally
		{
			var leftovers = await CloudComponentStorage.LocalRefsAsync(cache, aside);
			await cache.RunOrThrowAsync(["update-ref", "--stdin"], string.Concat(leftovers.Keys.Select(item => $"delete {item}\n")));
		}
	}

	public async Task<IReadOnlyList<string>> LoadAsync(IReadOnlyList<string> patterns)
	{
		await storage.SyncAsync(name);
		return await workspace.LoadAsync(patterns);
	}

	public async Task<bool> FetchTagAsync(string tag)
	{
		await storage.SyncAsync(name);
		return await workspace.FetchTagAsync(tag);
	}

	public Task HideAsync(string path) => workspace.HideAsync(path);

	public Task<string?> CommitAsync(string message) => workspace.CommitAsync(message);

	public Task<IReadOnlyList<string>> ListTagsAsync(string prefix) => workspace.ListTagsAsync(prefix);

	public Task<string?> TagCommitAsync(string name) => workspace.TagCommitAsync(name);

	public Task TagAsync(string name, string commit, string message) => workspace.TagAsync(name, commit, message);

	public Task<string> HeadAsync() => workspace.HeadAsync();

	public Task<IReadOnlyList<string>> ChangedPathsAsync() => workspace.ChangedPathsAsync();

	public Task<bool> ContainsAsync(string commit) => workspace.ContainsAsync(commit);

	public Task<MergeOutcome> MergeAsync(string commit, WorkspaceMerge options) => workspace.MergeAsync(commit, options);

	public Task<bool> MergeInProgressAsync() => workspace.MergeInProgressAsync();

	public Task<IReadOnlyList<string>> ConflictedPathsAsync() => workspace.ConflictedPathsAsync();

	public Task ResetAsync(string commit) => workspace.ResetAsync(commit);

	public Task AbortMergeAsync() => workspace.AbortMergeAsync();

	public Task<IReadOnlyList<string>> ChangedPathsAsync(string from, string to) => workspace.ChangedPathsAsync(from, to);

	public Task<string?> MergeBaseAsync(string first, string second) => workspace.MergeBaseAsync(first, second);

	public Task<IReadOnlyList<StoredCommit>> LogAsync(string from, string to) => workspace.LogAsync(from, to);

	public Task<string> DiffAsync(string from, string to) => workspace.DiffAsync(from, to);

	public Task<IReadOnlyList<FileStat>> DiffStatAsync(string from, string to) => workspace.DiffStatAsync(from, to);
}
