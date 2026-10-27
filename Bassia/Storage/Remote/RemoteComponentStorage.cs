namespace Bassia.Storage.Remote;

using System.Net;
using System.Text.Json;
using Bassia.Split;
using Bassia.Storage.Git;

/// <summary>
/// The component storage of a clone of a server. Histories stay where a depot clone keeps them - git partial clones
/// of the server's gateway next to the meta-repo, read and changed with git - while what makes or removes a history,
/// and landing on default branches, is the server's: a component is created, imported or forked there and then
/// cloned (history only) from it; publishing a working copy writes through to the server; moving default branches is a
/// landing in the server's merge queue.
/// </summary>
internal sealed class RemoteComponentStorage(GitComponentStorage local, ServerClient client, Core.Depot depot) : IComponentStorage
{
	public bool Exists(string name) => local.Exists(name);

	public string LocationOf(string name) => local.LocationOf(name);

	public IComponentStore Open(string name) => new RemoteComponentStore(local.Open(name), this);

	/// <summary>The server's history of <paramref name="name"/>, cloned here without file content.</summary>
	private async Task<IComponentStore> CloneAsync(string name) => new RemoteComponentStore(await local.ImportHistoryAsync(name, depot.ComponentUrl(name)), this);

	public async Task<IComponentStore> CreateAsync(string name, string defaultBranch, string url, string message)
	{
		await client.PostAsync($"/components/{Uri.EscapeDataString(name)}", new ComponentRequest("create", defaultBranch, url, message));
		return await CloneAsync(name);
	}

	/// <summary>From the server itself, a clone; from anywhere else, the server imports it first.</summary>
	public async Task<IComponentStore> ImportAsync(string name, string url)
	{
		if (url != depot.ComponentUrl(name))
		{
			await client.PostAsync($"/components/{Uri.EscapeDataString(name)}", new ComponentRequest("import", Url: url));
		}

		return await CloneAsync(name);
	}

	public Task<IComponentStore> ImportHistoryAsync(string name, string url) => ImportAsync(name, url);

	public async Task<IComponentStore> ForkAsync(IComponentStore source, string name, string branch, string commit, IReadOnlyList<string> tags, string url)
	{
		await client.PostAsync($"/components/{Uri.EscapeDataString(name)}", new ComponentRequest("fork", Url: url, Source: source.Name, Branch: branch, Commit: commit, Tags: tags));
		return await CloneAsync(name);
	}

	public Task<IReadOnlyList<SplitPartResult>> SplitAsync(IComponentStore source, SplitHistoryRequest request) =>
		throw new MonorepoException("Splitting a component is not available in a clone of a server yet; split it in the server's monorepo.");

	/// <summary>Deletes the server's history, then this clone's.</summary>
	public async Task DeleteAsync(string name)
	{
		await client.DeleteAsync($"/components/{Uri.EscapeDataString(name)}");
		await local.DeleteAsync(name);
	}

	public IStagingArea BeginStaging() => new Staging(this, local.BeginStaging());

	public IComponentWorkspace OpenWorkspace(string name, string directory) => new WriteThroughWorkspace(local.OpenWorkspace(name, directory), (GitComponentStore)local.Open(name));

	/// <summary>A landing in the server's merge queue; then this clone's branches follow the server's.</summary>
	public async Task MoveBranchesAsync(IReadOnlyList<BranchMove> moves)
	{
		try
		{
			await client.PostAsync("/land", new LandRequest(moves));
		}
		catch (ServerException ex) when (ex.Status == HttpStatusCode.Conflict && ex.Body.Contains("\"moved\"", StringComparison.Ordinal))
		{
			throw new BranchesMovedException(JsonSerializer.Deserialize<LandRefused>(ex.Body, Api.Json)!.Moved);
		}

		foreach (var component in moves.Select(move => move.Component).Distinct(StringComparer.Ordinal))
		{
			await local.Open(component).FetchUpstreamAsync();
		}
	}

	/// <summary>Imports staged here; a promoted one is imported by the server (from the same URL) and cloned from it.</summary>
	private sealed class Staging(RemoteComponentStorage storage, IStagingArea inner) : IStagingArea
	{
		public Task<IComponentStore> ImportAsync(string name, string url) => inner.ImportAsync(name, url);

		public async Task<IComponentStore> PromoteAsync(IComponentStore staged) =>
			await storage.ImportAsync(staged.Name, await staged.UpstreamAsync() ?? throw new MonorepoException($"The staged '{staged.Name}' has no upstream to import from."));

		public void Dispose() => inner.Dispose();
	}
}

/// <summary>A working copy whose publishing also pushes what it published to the server: the clone's store's upstream.</summary>
internal sealed class WriteThroughWorkspace(IComponentWorkspace inner, GitComponentStore store) : IComponentWorkspace
{
	public string Directory => inner.Directory;

	public async Task PublishAsync(string branch, IReadOnlyList<string> tags)
	{
		await inner.PublishAsync(branch, tags);
		await store.PushUpstreamAsync(branch, tags);
	}

	public Task HideAsync(string path) => inner.HideAsync(path);

	public Task<IReadOnlyList<string>> LoadAsync(IReadOnlyList<string> patterns) => inner.LoadAsync(patterns);

	public Task<string?> CommitAsync(string message) => inner.CommitAsync(message);

	public Task<IReadOnlyList<string>> ListTagsAsync(string prefix) => inner.ListTagsAsync(prefix);

	public Task<string?> TagCommitAsync(string name) => inner.TagCommitAsync(name);

	public Task TagAsync(string name, string commit, string message) => inner.TagAsync(name, commit, message);

	public Task<string> HeadAsync() => inner.HeadAsync();

	public Task<IReadOnlyList<string>> ChangedPathsAsync() => inner.ChangedPathsAsync();

	public Task<bool> ContainsAsync(string commit) => inner.ContainsAsync(commit);

	public Task<bool> FetchTagAsync(string tag) => inner.FetchTagAsync(tag);

	public Task<MergeOutcome> MergeAsync(string commit, WorkspaceMerge options) => inner.MergeAsync(commit, options);

	public Task<bool> MergeInProgressAsync() => inner.MergeInProgressAsync();

	public Task<IReadOnlyList<string>> ConflictedPathsAsync() => inner.ConflictedPathsAsync();

	public Task ResetAsync(string commit) => inner.ResetAsync(commit);

	public Task AbortMergeAsync() => inner.AbortMergeAsync();

	public Task<IReadOnlyList<string>> ChangedPathsAsync(string from, string to) => inner.ChangedPathsAsync(from, to);

	public Task<string?> MergeBaseAsync(string first, string second) => inner.MergeBaseAsync(first, second);

	public Task<IReadOnlyList<StoredCommit>> LogAsync(string from, string to) => inner.LogAsync(from, to);

	public Task<string> DiffAsync(string from, string to) => inner.DiffAsync(from, to);

	public Task<IReadOnlyList<FileStat>> DiffStatAsync(string from, string to) => inner.DiffStatAsync(from, to);
}

/// <summary>
/// A clone's store of a server's component: git's, except that its working copies write through to the server, and a
/// branch or tag it does not have yet - another client pushed it since - is fetched from the server when asked for.
/// </summary>
internal sealed class RemoteComponentStore(IComponentStore inner, RemoteComponentStorage storage) : IComponentStore
{
	/// <summary>Fetches <paramref name="reference"/> (<c>refs/heads/x</c> or <c>refs/tags/x</c>) from the server; false when it has none.</summary>
	private async Task<bool> FetchAsync(string reference)
	{
		if (reference.Contains("..", StringComparison.Ordinal) || reference.StartsWith('-'))
		{
			return false;
		}

		var fetch = await Bassia.Git.GitClient.In(inner.Location).RunAsync(["fetch", "--quiet", "--no-tags", "origin", $"+{reference}:{reference}"]);
		return fetch.ExitCode == 0;
	}

	public string Name => inner.Name;

	public string Location => inner.Location;

	public Task<string?> DefaultBranchAsync() => inner.DefaultBranchAsync();

	public Task<IReadOnlyList<StoredBranch>> ListBranchesAsync() => inner.ListBranchesAsync();

	public async Task<string?> BranchTipAsync(string branch) =>
		await inner.BranchTipAsync(branch) ?? (await FetchAsync($"refs/heads/{branch}") ? await inner.BranchTipAsync(branch) : null);

	public Task SetBranchAsync(string branch, string commit, string? expected = null) => inner.SetBranchAsync(branch, commit, expected);

	public Task<IReadOnlyList<StoredTag>> ListTagsAsync(string? prefix = null) => inner.ListTagsAsync(prefix);

	public async Task<StoredTag?> GetTagAsync(string name) =>
		await inner.GetTagAsync(name) ?? (await FetchAsync($"refs/tags/{name}") ? await inner.GetTagAsync(name) : null);

	public Task<string?> TagMessageAsync(string name) => inner.TagMessageAsync(name);

	public Task CreateTagAsync(string name, string commit, string message) => inner.CreateTagAsync(name, commit, message);

	public Task DeleteTagAsync(string name) => inner.DeleteTagAsync(name);

	public Task<StoredCommit?> GetCommitAsync(string id) => inner.GetCommitAsync(id);

	public Task<IReadOnlyList<StoredObject>> FindObjectsAsync(string prefix) => inner.FindObjectsAsync(prefix);

	public async Task<ResolvedRef?> ResolveRefAsync(string name)
	{
		if (await inner.ResolveRefAsync(name) is { } found)
		{
			return found;
		}

		var candidates = name.StartsWith("refs/", StringComparison.Ordinal) ? [name] : new[] { $"refs/tags/{name}", $"refs/heads/{name}" };
		foreach (var candidate in candidates)
		{
			if (await FetchAsync(candidate))
			{
				return await inner.ResolveRefAsync(name);
			}
		}

		return null;
	}

	public Task<bool> IsAncestorAsync(string ancestor, string descendant) => inner.IsAncestorAsync(ancestor, descendant);

	public Task<IReadOnlyList<StoredCommit>> HistoryAsync(string tip) => inner.HistoryAsync(tip);

	public Task<IReadOnlyList<StoredFile>> ListFilesAsync(string commit) => inner.ListFilesAsync(commit);

	public Task<byte[]?> ReadFileAsync(string commit, string path) => inner.ReadFileAsync(commit, path);

	public Task<IReadOnlyList<NestedUnit>> ListNestedAsync(string commit) => inner.ListNestedAsync(commit);

	public Task<string> CommitWithoutNestedAsync(string commit, IReadOnlyList<string> paths, string message) => inner.CommitWithoutNestedAsync(commit, paths, message);

	public Task<bool> EnsureCommitAsync(string commit, string url) => inner.EnsureCommitAsync(commit, url);

	public Task<string?> UpstreamAsync() => inner.UpstreamAsync();

	public Task<IReadOnlyList<string>> MissingContentAsync(string commit, IReadOnlyList<string>? paths = null) => inner.MissingContentAsync(commit, paths);

	public Task HydrateAsync(string commit, IReadOnlyList<string> paths) => inner.HydrateAsync(commit, paths);

	public Task FetchUpstreamAsync() => inner.FetchUpstreamAsync();

	public Task PushUpstreamAsync(string branch, IReadOnlyList<string> tags) => inner.PushUpstreamAsync(branch, tags);

	public Task<MergeResult> MergeAsync(string ours, string theirs, FileMergeDriver? driver = null) => inner.MergeAsync(ours, theirs, driver);

	public Task<string> CommitSnapshotAsync(string snapshot, IReadOnlyList<string> parents, string message) => inner.CommitSnapshotAsync(snapshot, parents, message);

	public Task<string?> MergeBaseAsync(string first, string second) => inner.MergeBaseAsync(first, second);

	public Task<IReadOnlyList<string>> ChangedPathsAsync(string from, string to) => inner.ChangedPathsAsync(from, to);

	public Task<string> DiffAsync(string from, string to) => inner.DiffAsync(from, to);

	public Task<IReadOnlyList<FileStat>> DiffStatAsync(string? from, string to) => inner.DiffStatAsync(from, to);

	public Task<IReadOnlyList<LogEntry>> LogAsync(LogQuery query) => inner.LogAsync(query);

	public async Task<IComponentWorkspace> CheckoutAsync(string commit, string directory, string branch, IReadOnlyList<string>? load = null)
	{
		await inner.CheckoutAsync(commit, directory, branch, load);
		return storage.OpenWorkspace(Name, directory);
	}
}
