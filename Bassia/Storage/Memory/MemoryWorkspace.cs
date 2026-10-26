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

	/// <summary>
	/// The merge left open by <see cref="MergeAsync"/>: the commit being merged, the paths it left conflicted, and the
	/// paths it wrote (which held no uncommitted change, so aborting puts the head's version back).
	/// </summary>
	private (string Commit, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Written)? merging;

	/// <summary>The files a sparse working copy materialized; null when it has them all.</summary>
	private readonly HashSet<string>? loaded;

	private MemoryWorkspace(MemoryComponentStore store, string directory, string branch, string head, HashSet<string>? loaded)
	{
		this.store = store;
		Directory = directory;
		Branch = branch;
		this.head = head;
		this.loaded = loaded;
	}

	public string Directory { get; }

	public string Branch { get; }

	internal static MemoryWorkspace Create(MemoryComponentStore store, string commit, string directory, string branch, IReadOnlyList<string>? load = null)
	{
		var snapshot = store.Get(commit);
		if (System.IO.Directory.Exists(directory) && System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
		{
			throw new StorageException($"'{directory}' is not empty; a working copy needs an empty folder.");
		}

		var patterns = load is null ? null : new Split.PathPatterns(load);
		var files = snapshot.Files.Where(file => patterns is null || patterns.Matches(file.Key)).ToList();
		store.Hydrate(files.Select(file => file.Value));
		System.IO.Directory.CreateDirectory(directory);
		foreach (var (path, file) in files)
		{
			var target = System.IO.Path.Combine(directory, path);
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
			File.WriteAllBytes(target, file.Content);
		}

		return new MemoryWorkspace(store, directory, branch, commit, patterns is null ? null : files.Select(file => file.Key).ToHashSet(StringComparer.Ordinal));
	}

	public Task HideAsync(string path)
	{
		hidden.Add(path.Replace('\\', '/').Trim('/'));
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<string>> LoadAsync(IReadOnlyList<string> patterns)
	{
		if (loaded is null)
		{
			return Task.FromResult<IReadOnlyList<string>>([]);
		}

		var matcher = new Split.PathPatterns(patterns);
		var files = Find(head).Files.Where(file => matcher.Matches(file.Key) && !loaded.Contains(file.Key) && !IsHidden(file.Key)).ToList();
		store.Hydrate(files.Select(file => file.Value));
		foreach (var (path, file) in files)
		{
			// A file the agent already wrote there is the agent's.
			var target = System.IO.Path.Combine(Directory, path);
			if (!File.Exists(target))
			{
				System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
				File.WriteAllBytes(target, file.Content);
			}

			loaded.Add(path);
		}

		return Task.FromResult<IReadOnlyList<string>>(files.Select(file => file.Key).ToList());
	}

	public Task<string?> CommitAsync(string message)
	{
		var parent = Find(head);
		var files = ReadFiles();
		if (merging is null && MemoryMerge.Changed(parent.Files, files).Count == 0)
		{
			return Task.FromResult<string?>(null);
		}

		// The commit is made in the store's object space but no branch or tag of the store names it until it is published.
		var id = store.Add(merging is null ? [head] : [head, merging.Value.Commit], message, files, parent.Nested);
		local[id] = store.Get(id);
		head = id;
		merging = null;
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

		localTags[name] = (new StoredTag(name, commit, true, message.Split('\n')[0].Trim(), MemoryComponentStore.Now(), MemoryComponentStore.Tagger()), message);
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

	public Task<string> HeadAsync() => Task.FromResult(head);

	public Task<IReadOnlyList<string>> ChangedPathsAsync() =>
		Task.FromResult(System.IO.Directory.Exists(Directory) ? MemoryMerge.Changed(Find(head).Files, ReadFiles()) : (IReadOnlyList<string>)[]);

	public Task<bool> ContainsAsync(string commit) =>
		Task.FromResult(store.Find(commit) is not null && store.Ancestry(head).Any(ancestor => ancestor.Node.Id == commit));

	/// <summary>The store's commits are this working copy's object space too, so fetching a tag is finding it.</summary>
	public async Task<bool> FetchTagAsync(string tag) => await store.GetTagAsync(tag) is not null;

	public async Task<MergeOutcome> MergeAsync(string commit, WorkspaceMerge options)
	{
		var theirs = store.Get(commit);
		if (merging is not null)
		{
			return new MergeOutcome(MergeStatus.Failed, [], [], "a merge is already in progress");
		}

		if (store.Ancestry(head).Any(ancestor => ancestor.Node.Id == commit))
		{
			return new MergeOutcome(MergeStatus.Merged, [], []);
		}

		var ours = Find(head);
		var dirty = MemoryMerge.Changed(ours.Files, ReadFiles());
		var fastForward = options.FastForward && store.Ancestry(commit).Any(ancestor => ancestor.Node.Id == head);
		var mergeBase = await store.MergeBaseAsync(head, commit);
		var merged = fastForward
			? new MemoryMerge.Result(new SortedDictionary<string, MemoryFile>(theirs.Files.ToDictionary(), StringComparer.Ordinal), [], [])
			: await MemoryMerge.MergeAsync(mergeBase is null ? null : store.Get(mergeBase).Files, ours.Files, theirs.Files, options.Driver, store.Drivers, options.Diff3);
		var changed = MemoryMerge.Changed(ours.Files, merged.Files);
		if (changed.Intersect(dirty, StringComparer.Ordinal).ToList() is { Count: > 0 } overwritten)
		{
			return new MergeOutcome(MergeStatus.Failed, [], merged.Warnings,
				$"Your local changes to the following files would be overwritten by merge: {string.Join(", ", overwritten)}");
		}

		WriteFiles(ours.Files, merged.Files);
		if (fastForward)
		{
			head = commit;
			return new MergeOutcome(MergeStatus.Merged, [], []);
		}

		if (merged.Clean && options.Commit)
		{
			head = store.Add([head, commit], options.Message, merged.Files, ours.Nested);
			return new MergeOutcome(MergeStatus.Merged, [], merged.Warnings);
		}

		merging = (commit, merged.Conflicts, changed);
		return merged.Clean
			? new MergeOutcome(MergeStatus.Merged, [], merged.Warnings)
			: new MergeOutcome(MergeStatus.Conflicted, merged.Conflicts, merged.Warnings, $"conflicts in {string.Join(", ", merged.Conflicts)}");
	}

	public Task<bool> MergeInProgressAsync() => Task.FromResult(merging is not null);

	public Task AbortMergeAsync()
	{
		if (merging is { } open)
		{
			var ours = Find(head).Files;
			var files = ReadFiles();
			WriteFiles(files.Where(file => open.Written.Contains(file.Key)).ToDictionary(),
				ours.Where(file => open.Written.Contains(file.Key)).ToDictionary());
			merging = null;
		}

		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<string>> ConflictedPathsAsync() => Task.FromResult(merging?.Conflicts ?? []);

	public Task ResetAsync(string commit)
	{
		WriteFiles(ReadFiles(), Find(commit).Files);
		head = commit;
		merging = null;
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<string>> ChangedPathsAsync(string from, string to) => store.ChangedPathsAsync(from, to);

	public Task<string?> MergeBaseAsync(string first, string second) => store.MergeBaseAsync(first, second);

	public Task<IReadOnlyList<StoredCommit>> LogAsync(string from, string to)
	{
		var excluded = store.Ancestry(from).Select(commit => commit.Node.Id).ToHashSet(StringComparer.Ordinal);
		return Task.FromResult<IReadOnlyList<StoredCommit>>(store.Ancestry(to).Where(commit => !excluded.Contains(commit.Node.Id)).Select(commit => commit.Node).ToList());
	}

	public Task<string> DiffAsync(string from, string to) => store.DiffAsync(from, to);

	/// <summary>
	/// The working copy's files, hidden paths (nested components) left out. In a sparse working copy, a file of the head it
	/// never materialized is as the head has it unless something wrote it.
	/// </summary>
	private SortedDictionary<string, MemoryFile> ReadFiles()
	{
		var files = new SortedDictionary<string, MemoryFile>(StringComparer.Ordinal);
		foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
		{
			var path = System.IO.Path.GetRelativePath(Directory, file).Replace('\\', '/');
			if (!IsHidden(path))
			{
				files[path] = new MemoryFile("100644", File.ReadAllBytes(file));
			}
		}

		if (loaded is not null)
		{
			foreach (var (path, file) in Find(head).Files.Where(file => !loaded.Contains(file.Key) && !IsHidden(file.Key)))
			{
				files.TryAdd(path, file);
			}
		}

		return files;
	}

	/// <summary>Turns the files the working copy has (<paramref name="from"/>) into <paramref name="to"/>, touching only what differs.</summary>
	private void WriteFiles(IReadOnlyDictionary<string, MemoryFile> from, IReadOnlyDictionary<string, MemoryFile> to)
	{
		foreach (var path in MemoryMerge.Changed(from, to).Where(path => !IsHidden(path)))
		{
			var target = System.IO.Path.Combine(Directory, path);
			if (to.TryGetValue(path, out var file))
			{
				System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
				File.WriteAllBytes(target, file.Content);
			}
			else if (File.Exists(target))
			{
				File.Delete(target);
			}
		}
	}

	private bool IsHidden(string path) => hidden.Any(prefix => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));

	private MemoryCommit Find(string id) => local.GetValueOrDefault(id) ?? store.Get(id);
}
