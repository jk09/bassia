namespace Bassia.Storage.Memory;

/// <summary>
/// A working copy of a <see cref="MemoryComponentStore"/>: the commit's files written to a real folder, so an agent
/// changes them like any other files. Committing reads the folder back; commits and tags stay with the working copy until
/// they are published to the store.
/// </summary>
internal sealed class MemoryWorkspace : IComponentWorkspace
{
	private readonly MemoryComponentStore store;
	private readonly HashSet<string> hidden = new(StringComparer.Ordinal);
	private readonly Dictionary<string, MemoryCommit> local = new(StringComparer.Ordinal);
	private readonly Dictionary<string, (StoredTag Tag, string Message)> localTags = new(StringComparer.Ordinal);
	private string head;

	private MemoryWorkspace(MemoryComponentStore store, string directory, string branch, string head)
	{
		this.store = store;
		Directory = directory;
		Branch = branch;
		this.head = head;
	}

	public string Directory { get; }

	public string Branch { get; }

	internal static MemoryWorkspace Create(MemoryComponentStore store, string commit, string directory, string branch)
	{
		var snapshot = store.Get(commit);
		if (System.IO.Directory.Exists(directory) && System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
		{
			throw new StorageException($"'{directory}' is not empty; a working copy needs an empty folder.");
		}

		System.IO.Directory.CreateDirectory(directory);
		foreach (var (path, file) in snapshot.Files)
		{
			var target = System.IO.Path.Combine(directory, path);
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
			File.WriteAllBytes(target, file.Content);
		}

		return new MemoryWorkspace(store, directory, branch, commit);
	}

	public Task HideAsync(string path)
	{
		hidden.Add(path.Replace('\\', '/').Trim('/'));
		return Task.CompletedTask;
	}

	public Task<string?> CommitAsync(string message)
	{
		var parent = Find(head);
		var files = new SortedDictionary<string, MemoryFile>(StringComparer.Ordinal);
		foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
		{
			var path = System.IO.Path.GetRelativePath(Directory, file).Replace('\\', '/');
			if (!IsHidden(path))
			{
				files[path] = new MemoryFile("100644", File.ReadAllBytes(file));
			}
		}

		if (files.Count == parent.Files.Count && files.All(file => parent.Files.TryGetValue(file.Key, out var old) && old.Object == file.Value.Object))
		{
			return Task.FromResult<string?>(null);
		}

		// The commit is made in the store's object space but no branch or tag of the store names it until it is published.
		var id = store.Add([head], message, files, parent.Nested);
		local[id] = store.Get(id);
		head = id;
		return Task.FromResult<string?>(id);
	}

	public async Task<IReadOnlyList<string>> ListTagsAsync(string prefix) =>
		(await store.ListTagsAsync(prefix)).Select(tag => tag.Name)
			.Concat(localTags.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
			.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

	public async Task<string?> TagCommitAsync(string name) =>
		localTags.TryGetValue(name, out var entry) ? entry.Tag.Commit : (await store.GetTagAsync(name))?.Commit;

	public Task TagAsync(string name, string commit, string message)
	{
		if (localTags.ContainsKey(name))
		{
			throw new StorageException($"Tag '{name}' already exists in '{Directory}'.");
		}

		localTags[name] = (new StoredTag(name, commit, true, message.Split('\n')[0].Trim(), MemoryComponentStore.Now()), message);
		return Task.CompletedTask;
	}

	public async Task PublishAsync(string branch, IReadOnlyList<string> tags)
	{
		foreach (var tag in tags)
		{
			if (!localTags.ContainsKey(tag) && await store.GetTagAsync(tag) is null)
			{
				throw new StorageException($"Tag '{tag}' does not exist in '{Directory}'.");
			}

			if (localTags.TryGetValue(tag, out var entry) && await store.GetTagAsync(tag) is { } existing && existing.Commit != entry.Tag.Commit)
			{
				throw new StorageException($"Tag '{tag}' already exists in component '{store.Name}' at another commit.");
			}
		}

		var tip = await store.BranchTipAsync(branch);
		if (tip is not null && !await store.IsAncestorAsync(tip, head))
		{
			throw new StorageException($"Publishing '{branch}' to component '{store.Name}' is not a fast-forward.");
		}

		await store.SetBranchAsync(branch, head);
		foreach (var tag in tags.Where(localTags.ContainsKey))
		{
			if (await store.GetTagAsync(tag) is null)
			{
				store.SetTag(localTags[tag].Tag, localTags[tag].Message);
			}
		}
	}

	private bool IsHidden(string path) => hidden.Any(prefix => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));

	private MemoryCommit Find(string id) => local.GetValueOrDefault(id) ?? store.Get(id);
}
