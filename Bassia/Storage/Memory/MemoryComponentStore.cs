namespace Bassia.Storage.Memory;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>A file of an in-memory commit.</summary>
internal sealed record MemoryFile(string Mode, byte[] Content)
{
	public string Object { get; } = Convert.ToHexStringLower(SHA1.HashData(Content));

	public static MemoryFile Text(string text) => new("100644", Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// An in-memory commit: its DAG node, its complete content and the units nested in it, when it was made and its place
/// in the order commits are made in (a commit is always made after its parents).
/// </summary>
internal sealed record MemoryCommit(
	StoredCommit Node,
	IReadOnlyDictionary<string, MemoryFile> Files,
	IReadOnlyDictionary<string, NestedUnit> Nested,
	DateTimeOffset Date = default,
	long Sequence = 0);

/// <summary>
/// The reference implementation of <see cref="IComponentStore"/>: a component history kept in memory. It implements the
/// contract as literally as possible and is what the component graph's unit tests run on; a new backend can be checked
/// against it.
/// </summary>
internal sealed class MemoryComponentStore(string name, string location) : IComponentStore
{
	private static long sequence;

	private readonly object gate = new();
	private readonly Dictionary<string, MemoryCommit> commits = new(StringComparer.Ordinal);
	private readonly SortedDictionary<string, string> branches = new(StringComparer.Ordinal);
	private readonly SortedDictionary<string, (StoredTag Tag, string Message)> tags = new(StringComparer.Ordinal);

	public string Name { get; } = name;

	public string Location { get; set; } = location;

	/// <summary>The default branch (git's <c>HEAD</c>); null for a detached head.</summary>
	public string? DefaultBranch { get; set; } = "main";

	public string? Upstream { get; set; }

	/// <summary>
	/// The content objects a store imported with its history only holds; null when it holds all of them. Its commits keep
	/// their files' content anyway (memory is cheap): this set is what the store may read without its upstream.
	/// </summary>
	private HashSet<string>? held;

	/// <summary>How many content objects the store fetched from its upstream: what tests check laziness by.</summary>
	public int HydratedObjects { get; private set; }

	/// <summary>Whether the store holds the content of <paramref name="objectId"/>.</summary>
	public bool Holds(string objectId)
	{
		lock (gate)
		{
			return held is null || held.Contains(objectId);
		}
	}

	/// <summary>Makes the store one that holds no content until it hydrates it (<see cref="IComponentStorage.ImportHistoryAsync"/>).</summary>
	internal void DropContent()
	{
		lock (gate)
		{
			held = new HashSet<string>(StringComparer.Ordinal);
		}
	}

	/// <summary>
	/// Fetches the content of <paramref name="files"/> the store does not hold from its upstream, which must be reachable
	/// and hold it.
	/// </summary>
	internal void Hydrate(IEnumerable<MemoryFile> files)
	{
		lock (gate)
		{
			if (held is null)
			{
				return;
			}

			var missing = files.Select(file => file.Object).Where(id => !held.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
			if (missing.Count == 0)
			{
				return;
			}

			var remote = Upstream is null ? null : Upstreams?.Find(Upstream);
			if (remote is null)
			{
				throw new StorageException($"Could not fetch the content of component '{Name}': its upstream '{Upstream}' cannot be reached.");
			}

			foreach (var id in missing)
			{
				if (!remote.Holds(id) || !remote.Contains(id))
				{
					throw new StorageException($"Could not fetch object {id} of component '{Name}' from its upstream '{Upstream}'.");
				}

				held.Add(id);
				HydratedObjects++;
			}
		}
	}

	/// <summary>Whether any commit of the store has a file with the content <paramref name="objectId"/>.</summary>
	private bool Contains(string objectId)
	{
		lock (gate)
		{
			return commits.Values.Any(commit => commit.Files.Values.Any(file => file.Object == objectId));
		}
	}

	// ----- seeding (tests, imports) -----

	/// <summary>
	/// Commits on <paramref name="branch"/> (creating it, or a root commit when it has no tip): the branch tip's content with
	/// <paramref name="changes"/> applied (null content deletes a file) and the units nested in <paramref name="nested"/>
	/// added or (with a null commit) removed. Returns the new commit's id.
	/// </summary>
	public string Commit(string branch, string message, IReadOnlyDictionary<string, string?>? changes = null, IReadOnlyList<NestedUnit>? nested = null)
	{
		lock (gate)
		{
			var tip = branches.GetValueOrDefault(branch);
			var files = tip is null ? new SortedDictionary<string, MemoryFile>(StringComparer.Ordinal) : new SortedDictionary<string, MemoryFile>(commits[tip].Files.ToDictionary(), StringComparer.Ordinal);
			var units = tip is null ? new SortedDictionary<string, NestedUnit>(StringComparer.Ordinal) : new SortedDictionary<string, NestedUnit>(commits[tip].Nested.ToDictionary(), StringComparer.Ordinal);
			foreach (var (path, content) in changes ?? new Dictionary<string, string?>())
			{
				if (content is null)
				{
					files.Remove(path);
				}
				else
				{
					files[path] = MemoryFile.Text(content);
				}
			}

			foreach (var unit in nested ?? [])
			{
				units[unit.Path] = unit;
			}

			var id = Add(tip is null ? [] : [tip], message, files, units);
			branches[branch] = id;
			return id;
		}
	}

	/// <summary>Adds a commit with exactly this content and these parents (which must exist); returns its id.</summary>
	public string Add(IReadOnlyList<string> parents, string message, IReadOnlyDictionary<string, MemoryFile> files, IReadOnlyDictionary<string, NestedUnit>? nested = null)
	{
		lock (gate)
		{
			foreach (var parent in parents.Where(parent => !commits.ContainsKey(parent)))
			{
				throw new StorageException($"Commit {parent} is not in component '{Name}'.");
			}

			var number = Interlocked.Increment(ref sequence);
			var id = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(
				$"{number}\n{string.Join(' ', parents)}\n{message}\n{string.Join('\n', files.Select(file => $"{file.Key} {file.Value.Object}"))}")));
			// Content made here is held here.
			held?.UnionWith(files.Values.Select(file => file.Object));
			commits[id] = new MemoryCommit(new StoredCommit(id, parents.ToList(), message, Core.Actor.Current is { } actor ? $"{actor.Author.Name} <{actor.Author.Email}>" : null),
				new SortedDictionary<string, MemoryFile>(files.ToDictionary(), StringComparer.Ordinal),
				new SortedDictionary<string, NestedUnit>((nested ?? new Dictionary<string, NestedUnit>()).ToDictionary(), StringComparer.Ordinal),
				DateTimeOffset.UtcNow, number);
			return id;
		}
	}

	/// <summary>Takes over a commit of another store (its ancestors must be here already).</summary>
	internal void Adopt(MemoryCommit commit)
	{
		lock (gate)
		{
			commits.TryAdd(commit.Node.Id, commit);
		}
	}

	internal MemoryCommit? Find(string id)
	{
		lock (gate)
		{
			return commits.GetValueOrDefault(id);
		}
	}

	internal MemoryCommit Get(string id) => Find(id) ?? throw new StorageException($"Commit {id} is not in component '{Name}'.");

	/// <summary><paramref name="tip"/> and its ancestors, every commit before its parents.</summary>
	internal IReadOnlyList<MemoryCommit> Ancestry(string tip)
	{
		lock (gate)
		{
			var ordered = new List<MemoryCommit>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			void Visit(string id)
			{
				if (!seen.Add(id))
				{
					return;
				}

				var commit = Get(id);
				foreach (var parent in commit.Node.Parents)
				{
					Visit(parent);
				}

				ordered.Add(commit);
			}

			Visit(tip);
			ordered.Reverse();
			return ordered;
		}
	}

	/// <summary>An independent copy of this store (every commit, branch and tag) for another component name.</summary>
	internal MemoryComponentStore Copy(string newName, string newLocation)
	{
		lock (gate)
		{
			var copy = new MemoryComponentStore(newName, newLocation) { DefaultBranch = DefaultBranch, Upstream = Upstream };
			copy.held = held is null ? null : new HashSet<string>(held, StringComparer.Ordinal);
			foreach (var commit in commits.Values)
			{
				copy.commits[commit.Node.Id] = commit;
			}

			foreach (var (branch, commit) in branches)
			{
				copy.branches[branch] = commit;
			}

			foreach (var (tag, entry) in tags)
			{
				copy.tags[tag] = entry;
			}

			return copy;
		}
	}

	internal void SetTag(StoredTag tag, string message)
	{
		lock (gate)
		{
			tags[tag.Name] = (tag, message);
		}
	}

	internal string? TagMessage(string tag)
	{
		lock (gate)
		{
			return tags.TryGetValue(tag, out var entry) ? entry.Message : null;
		}
	}

	internal static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

	// ----- IComponentStore -----

	public Task<string?> DefaultBranchAsync() => Task.FromResult(DefaultBranch);

	public Task<IReadOnlyList<StoredBranch>> ListBranchesAsync()
	{
		lock (gate)
		{
			return Task.FromResult<IReadOnlyList<StoredBranch>>(branches.Select(branch => new StoredBranch(branch.Key, branch.Value, commits[branch.Value].Node.Subject)).ToList());
		}
	}

	public Task<string?> BranchTipAsync(string branch)
	{
		lock (gate)
		{
			return Task.FromResult(branches.GetValueOrDefault(branch));
		}
	}

	public Task SetBranchAsync(string branch, string commit, string? expected = null)
	{
		lock (gate)
		{
			Get(commit);
			if (expected is not null && branches.GetValueOrDefault(branch) != expected)
			{
				throw new StorageException($"Branch '{branch}' of component '{Name}' moved: it no longer points at {expected}.");
			}

			branches[branch] = commit;
			return Task.CompletedTask;
		}
	}

	public Task<IReadOnlyList<StoredTag>> ListTagsAsync(string? prefix = null)
	{
		lock (gate)
		{
			return Task.FromResult<IReadOnlyList<StoredTag>>(tags.Values.Select(entry => entry.Tag)
				.Where(tag => prefix is null || tag.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList());
		}
	}

	public Task<StoredTag?> GetTagAsync(string name)
	{
		lock (gate)
		{
			return Task.FromResult(tags.TryGetValue(name, out var entry) ? entry.Tag : null);
		}
	}

	public Task<string?> TagMessageAsync(string name)
	{
		lock (gate)
		{
			return Task.FromResult(tags.TryGetValue(name, out var entry) && entry.Tag.Annotated ? entry.Message : null);
		}
	}

	public Task CreateTagAsync(string name, string commit, string message)
	{
		lock (gate)
		{
			Get(commit);
			if (tags.ContainsKey(name))
			{
				throw new StorageException($"Tag '{name}' already exists in component '{Name}'.");
			}

			tags[name] = (new StoredTag(name, commit, true, message.Split('\n')[0].Trim(), Now(), Tagger()), message);
			return Task.CompletedTask;
		}
	}

	public Task DeleteTagAsync(string name)
	{
		lock (gate)
		{
			tags.Remove(name);
			return Task.CompletedTask;
		}
	}

	public Task<StoredCommit?> GetCommitAsync(string id) => Task.FromResult(Find(id)?.Node);

	public Task<IReadOnlyList<StoredObject>> FindObjectsAsync(string prefix)
	{
		lock (gate)
		{
			return Task.FromResult<IReadOnlyList<StoredObject>>(commits.Keys.Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
				.Order(StringComparer.Ordinal).Select(id => new StoredObject(id, "commit")).ToList());
		}
	}

	public Task<ResolvedRef?> ResolveRefAsync(string name)
	{
		lock (gate)
		{
			// As git: HEAD is the default branch, a full ref name is taken as it is, else a tag wins over a branch of the same name.
			if (name == "HEAD")
			{
				name = DefaultBranch is null ? name : $"refs/heads/{DefaultBranch}";
			}

			var tag = name.StartsWith("refs/tags/", StringComparison.Ordinal) ? name["refs/tags/".Length..] : name.StartsWith("refs/", StringComparison.Ordinal) ? null : name;
			if (tag is not null && tags.TryGetValue(tag, out var entry))
			{
				return Task.FromResult<ResolvedRef?>(new ResolvedRef($"refs/tags/{tag}", entry.Tag.Annotated ? RefKind.AnnotatedTag : RefKind.LightweightTag, entry.Tag.Commit));
			}

			var branch = name.StartsWith("refs/heads/", StringComparison.Ordinal) ? name["refs/heads/".Length..] : name.StartsWith("refs/", StringComparison.Ordinal) ? null : name;
			return Task.FromResult(branch is not null && branches.TryGetValue(branch, out var commit)
				? new ResolvedRef($"refs/heads/{branch}", RefKind.Branch, commit)
				: null);
		}
	}

	public Task<bool> IsAncestorAsync(string ancestor, string descendant) =>
		Task.FromResult(Find(descendant) is not null && Ancestry(descendant).Any(commit => commit.Node.Id == ancestor));

	public Task<IReadOnlyList<StoredCommit>> HistoryAsync(string tip) =>
		Task.FromResult<IReadOnlyList<StoredCommit>>(Ancestry(tip).Select(commit => commit.Node).ToList());

	public Task<IReadOnlyList<StoredFile>> ListFilesAsync(string commit) =>
		Task.FromResult<IReadOnlyList<StoredFile>>(Get(commit).Files
			.Select(file => new StoredFile(file.Key, file.Value.Mode, file.Value.Object, Holds(file.Value.Object) ? file.Value.Content.LongLength : -1)).ToList());

	public Task<byte[]?> ReadFileAsync(string commit, string path)
	{
		if (!Get(commit).Files.TryGetValue(path, out var file))
		{
			return Task.FromResult<byte[]?>(null);
		}

		Hydrate([file]);
		return Task.FromResult<byte[]?>(file.Content);
	}

	public Task<IReadOnlyList<NestedUnit>> ListNestedAsync(string commit) =>
		Task.FromResult<IReadOnlyList<NestedUnit>>(Get(commit).Nested.Values.ToList());

	public Task<string> CommitWithoutNestedAsync(string commit, IReadOnlyList<string> paths, string message)
	{
		var source = Get(commit);
		var nested = source.Nested.Where(unit => !paths.Contains(unit.Key)).ToDictionary();
		return Task.FromResult(Add([commit], message, source.Files, nested));
	}

	/// <summary>Who makes a tag now: the committer of <see cref="Core.Actor.Current"/>, as git records a tagger.</summary>
	internal static string? Tagger() => Core.Actor.Current is { } actor ? $"{actor.Committer.Name} <{actor.Committer.Email}>" : null;

	/// <summary>The upstreams <see cref="EnsureCommitAsync"/> fetches from; set by the storage that made the store.</summary>
	internal MemoryUpstreams? Upstreams { get; set; }

	public Task<bool> EnsureCommitAsync(string commit, string url)
	{
		if (Find(commit) is not null)
		{
			return Task.FromResult(true);
		}

		if (Upstreams?.Find(url) is not { } remote || remote.Find(commit) is null)
		{
			return Task.FromResult(false);
		}

		foreach (var ancestor in remote.Ancestry(commit).Reverse())
		{
			Adopt(ancestor);
		}

		return Task.FromResult(true);
	}

	public Task<string?> UpstreamAsync() => Task.FromResult(Upstream);

	// ----- content held locally -----

	public Task<IReadOnlyList<string>> MissingContentAsync(string commit, IReadOnlyList<string>? paths = null)
	{
		var wanted = paths?.ToHashSet(StringComparer.Ordinal);
		return Task.FromResult<IReadOnlyList<string>>(Get(commit).Files
			.Where(file => (wanted is null || wanted.Contains(file.Key)) && !Holds(file.Value.Object)).Select(file => file.Key).ToList());
	}

	public Task HydrateAsync(string commit, IReadOnlyList<string> paths)
	{
		var files = Get(commit).Files;
		Hydrate(paths.Where(files.ContainsKey).Select(path => files[path]));
		return Task.CompletedTask;
	}

	private MemoryComponentStore RequireUpstream() =>
		(Upstream is null ? null : Upstreams?.Find(Upstream))
			?? throw new StorageException($"Component '{Name}' cannot reach its upstream '{Upstream}'.");

	public Task FetchUpstreamAsync()
	{
		var remote = RequireUpstream();
		var remoteBranches = remote.ListBranchesAsync().GetAwaiter().GetResult();
		var remoteTags = remote.ListTagsAsync().GetAwaiter().GetResult();
		foreach (var tip in remoteBranches.Select(branch => branch.Commit).Concat(remoteTags.Select(tag => tag.Commit)).Distinct(StringComparer.Ordinal))
		{
			foreach (var ancestor in remote.Ancestry(tip).Reverse())
			{
				Adopt(ancestor);
			}
		}

		lock (gate)
		{
			foreach (var branch in remoteBranches)
			{
				branches[branch.Name] = branch.Commit;
			}

			foreach (var tag in remoteTags)
			{
				tags[tag.Name] = (tag, remote.TagMessage(tag.Name) ?? tag.Subject);
			}
		}

		return Task.CompletedTask;
	}

	public async Task PushUpstreamAsync(string branch, IReadOnlyList<string> tagNames)
	{
		var remote = RequireUpstream();
		var tip = await BranchTipAsync(branch) ?? throw new StorageException($"Component '{Name}' has no branch '{branch}'.");
		if (await remote.BranchTipAsync(branch) is { } remoteTip && !await IsAncestorAsync(remoteTip, tip))
		{
			throw new StorageException($"Pushing '{branch}' of component '{Name}' to '{Upstream}' is not a fast-forward.");
		}

		foreach (var ancestor in Ancestry(tip).Reverse())
		{
			remote.Adopt(ancestor);
		}

		await remote.SetBranchAsync(branch, tip);
		foreach (var name in tagNames)
		{
			var tag = await GetTagAsync(name) ?? throw new StorageException($"Tag '{name}' is not in component '{Name}'.");
			foreach (var ancestor in Ancestry(tag.Commit).Reverse())
			{
				remote.Adopt(ancestor);
			}

			if (await remote.GetTagAsync(name) is { } existing && existing.Commit != tag.Commit)
			{
				throw new StorageException($"Tag '{name}' already exists in '{Upstream}' at another commit.");
			}

			remote.SetTag(tag, TagMessage(name) ?? tag.Subject);
		}
	}

	// ----- merging and comparing -----

	private readonly Dictionary<string, IReadOnlyDictionary<string, MemoryFile>> snapshots = new(StringComparer.Ordinal);

	/// <summary>Remembers merged content under an id derived from it, for <see cref="CommitSnapshotAsync"/>.</summary>
	internal string Snapshot(IReadOnlyDictionary<string, MemoryFile> files)
	{
		var id = "snapshot-" + Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(
			string.Join('\n', files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => $"{file.Key} {file.Value.Object}")))));
		lock (gate)
		{
			snapshots[id] = files;
		}

		return id;
	}

	/// <summary>The in-process stand-ins for file merge drivers, by driver name (see <see cref="MemoryComponentStorage.Drivers"/>).</summary>
	internal IReadOnlyDictionary<string, InProcessMergeDriver>? Drivers => Storage?.Drivers;

	public async Task<MergeResult> MergeAsync(string ours, string theirs, FileMergeDriver? driver = null)
	{
		var mergeBase = await MergeBaseAsync(ours, theirs);
		Hydrate(new[] { ours, theirs }.Concat(mergeBase is null ? [] : [mergeBase]).SelectMany(commit => Get(commit).Files.Values));
		var merged = await MemoryMerge.MergeAsync(mergeBase is null ? null : Get(mergeBase).Files, Get(ours).Files, Get(theirs).Files, driver, Drivers);
		return new MergeResult(merged.Clean, Snapshot(merged.Files), merged.Conflicts, merged.Warnings);
	}

	public Task<string> CommitSnapshotAsync(string snapshot, IReadOnlyList<string> parents, string message)
	{
		IReadOnlyDictionary<string, MemoryFile> files;
		lock (gate)
		{
			files = snapshots.TryGetValue(snapshot, out var found) ? found : throw new StorageException($"Snapshot {snapshot} is not in component '{Name}'.");
		}

		return Task.FromResult(Add(parents, message, files, parents.Count > 0 ? Get(parents[0]).Nested : null));
	}

	/// <summary>The newest commit of <paramref name="second"/>'s ancestry that is also an ancestor of <paramref name="first"/>.</summary>
	public Task<string?> MergeBaseAsync(string first, string second)
	{
		var ancestors = Ancestry(first).Select(commit => commit.Node.Id).ToHashSet(StringComparer.Ordinal);
		return Task.FromResult(Ancestry(second).Select(commit => commit.Node.Id).FirstOrDefault(ancestors.Contains));
	}

	public Task<IReadOnlyList<string>> ChangedPathsAsync(string from, string to) => Task.FromResult(MemoryMerge.Changed(Get(from).Files, Get(to).Files));

	public Task<string> DiffAsync(string from, string to) => Task.FromResult(MemoryMerge.Diff(Get(from).Files, Get(to).Files));

	public Task<IReadOnlyList<FileStat>> DiffStatAsync(string? from, string to) =>
		Task.FromResult(MemoryMerge.Stat(from is null ? new Dictionary<string, MemoryFile>() : Get(from).Files, Get(to).Files));

	public async Task<IReadOnlyList<LogEntry>> LogAsync(LogQuery query)
	{
		var tips = new List<string>();
		if (query.Tips is null)
		{
			tips.AddRange((await ListBranchesAsync()).Select(branch => branch.Commit));
			tips.AddRange((await ListTagsAsync()).Select(tag => tag.Commit));
		}
		else
		{
			foreach (var tip in query.Tips)
			{
				tips.Add((await ResolveRefAsync(tip))?.Commit ?? Find(tip)?.Node.Id ?? throw new StorageException($"'{tip}' is no branch, tag or commit of component '{Name}'."));
			}
		}

		var branchesAt = (await ListBranchesAsync()).ToLookup(branch => branch.Commit, branch => branch.Name);
		var tagsAt = (await ListTagsAsync()).ToLookup(tag => tag.Commit, tag => tag.Name);
		var commits = tips.Distinct(StringComparer.Ordinal).SelectMany(Ancestry).DistinctBy(commit => commit.Node.Id)
			.Where(commit => query.Grep is not { Count: > 0 } grep || grep.Any(text => commit.Node.Message.Contains(text, StringComparison.Ordinal)))
			.OrderByDescending(commit => commit.Date).ThenByDescending(commit => commit.Sequence)
			.Skip(query.Skip)
			.Take(query.Limit ?? int.MaxValue);
		return commits.Select(commit => new LogEntry(commit.Node, commit.Date, branchesAt[commit.Node.Id].ToList(), tagsAt[commit.Node.Id].ToList(),
			query.Paths ? MemoryMerge.Changed(commit.Node.Parents.Count == 0 ? new Dictionary<string, MemoryFile>() : Get(commit.Node.Parents[0]).Files, commit.Files) : null)).ToList();
	}

	/// <summary>The storage holding this store, which remembers its working copies; null for an upstream.</summary>
	internal MemoryComponentStorage? Storage { get; set; }

	public Task<IComponentWorkspace> CheckoutAsync(string commit, string directory, string branch, IReadOnlyList<string>? load = null)
	{
		var workspace = MemoryWorkspace.Create(this, commit, directory, branch, load);
		Storage?.Track(workspace);
		return Task.FromResult<IComponentWorkspace>(workspace);
	}
}

/// <summary>Repositories outside the monorepo, by URL: what a memory storage imports from and fetches pinned commits from.</summary>
internal sealed class MemoryUpstreams
{
	private readonly Dictionary<string, MemoryComponentStore> byUrl = new(StringComparer.Ordinal);

	/// <summary>An upstream repository at <paramref name="url"/>, created empty on first use; seed it with <see cref="MemoryComponentStore.Commit"/>.</summary>
	public MemoryComponentStore At(string url)
	{
		lock (byUrl)
		{
			return byUrl.TryGetValue(url, out var store) ? store : byUrl[url] = new MemoryComponentStore(url, url);
		}
	}

	public MemoryComponentStore? Find(string url)
	{
		lock (byUrl)
		{
			return byUrl.GetValueOrDefault(url);
		}
	}
}
