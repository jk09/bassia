namespace Bassia.Storage.Memory;

using Bassia.Split;

/// <summary>
/// The reference implementation of <see cref="IComponentStorage"/>: component stores kept in memory, imported from
/// <see cref="MemoryUpstreams"/> instead of URLs. Working copies are real folders (see <see cref="MemoryWorkspace"/>).
/// </summary>
internal sealed class MemoryComponentStorage(MemoryUpstreams? upstreams = null) : IComponentStorage
{
	private readonly Dictionary<string, MemoryComponentStore> stores = new(StringComparer.Ordinal);
	private readonly Dictionary<string, MemoryWorkspace> workspaces = new(StringComparer.Ordinal);

	public MemoryUpstreams Upstreams { get; } = upstreams ?? new MemoryUpstreams();

	/// <summary>
	/// File merge drivers run in-process instead of their commands, by <see cref="FileMergeDriver.Name"/>: how tests give
	/// the memory backend a structural merge without installing one. A driver not listed runs its command.
	/// </summary>
	public Dictionary<string, InProcessMergeDriver> Drivers { get; } = new(StringComparer.Ordinal);

	/// <summary>The store of a component, for tests to seed and inspect.</summary>
	public MemoryComponentStore this[string name] => (MemoryComponentStore)Open(name);

	public IReadOnlyCollection<string> Names
	{
		get
		{
			lock (stores)
			{
				return stores.Keys.Order(StringComparer.Ordinal).ToList();
			}
		}
	}

	public bool Exists(string name)
	{
		lock (stores)
		{
			return stores.ContainsKey(name);
		}
	}

	public string LocationOf(string name) => $"memory:{name}";

	public IComponentStore Open(string name)
	{
		lock (stores)
		{
			return stores.TryGetValue(name, out var store) ? store : throw new StorageException($"Component '{name}' has no store at '{LocationOf(name)}'.");
		}
	}

	public Task<IComponentStore> CreateAsync(string name, string defaultBranch, string url, string message)
	{
		var store = new MemoryComponentStore(name, LocationOf(name)) { DefaultBranch = defaultBranch, Upstream = url.Length > 0 ? url : null };
		store.Commit(defaultBranch, message);
		return Task.FromResult<IComponentStore>(Put(store));
	}

	public Task<IComponentStore> ImportAsync(string name, string url) => Task.FromResult<IComponentStore>(Put(Clone(name, url)));

	public Task<IComponentStore> ImportHistoryAsync(string name, string url)
	{
		var store = Clone(name, url);
		store.DropContent();
		return Task.FromResult<IComponentStore>(Put(store));
	}

	public Task<IComponentStore> ForkAsync(IComponentStore source, string name, string branch, string commit, IReadOnlyList<string> tags, string url)
	{
		var parent = (MemoryComponentStore)source;
		var fork = new MemoryComponentStore(name, LocationOf(name)) { DefaultBranch = branch, Upstream = url.Length > 0 ? url : null };
		foreach (var ancestor in parent.Ancestry(commit).Reverse())
		{
			fork.Adopt(ancestor);
		}

		fork.SetBranchAsync(branch, commit).GetAwaiter().GetResult();
		foreach (var tag in tags)
		{
			var stored = parent.GetTagAsync(tag).GetAwaiter().GetResult() ?? throw new StorageException($"Tag '{tag}' is not in component '{parent.Name}'.");
			fork.SetTag(stored, parent.TagMessage(tag) ?? stored.Subject);
		}

		return Task.FromResult<IComponentStore>(Put(fork));
	}

	/// <summary>
	/// Each part's history: every commit of the branch reduced to the part's files (by the tip's allocation, or by the
	/// plan's patterns for paths gone before the tip), commits that change none of them left out, and a record commit on
	/// top tagged with the split tag. Tags whose commit survives come along.
	/// </summary>
	public async Task<IReadOnlyList<SplitPartResult>> SplitAsync(IComponentStore source, SplitHistoryRequest request)
	{
		var origin = (MemoryComponentStore)source;
		var allocation = request.Allocation;
		var plan = allocation.Plan;
		var history = origin.Ancestry(allocation.TipCommit).Reverse().ToList();
		var sourceTags = (await origin.ListTagsAsync()).Where(tag => history.Any(commit => commit.Node.Id == tag.Commit)).ToList();
		var results = new List<SplitPartResult>();

		for (var index = 0; index < plan.Parts.Count; index++)
		{
			var part = plan.Parts[index];
			bool Belongs(string path) => allocation.TipOwner.TryGetValue(path, out var owner)
				? owner == index || owner == -1
				: !plan.Drop.Matches(path) && (part.Paths.Matches(path) || plan.Shared.Matches(path));

			var store = new MemoryComponentStore(part.Name, LocationOf(part.Name)) { DefaultBranch = allocation.Branch, Upstream = part.Url };
			var map = new Dictionary<string, string?>(StringComparer.Ordinal);
			var written = 0;
			foreach (var commit in history)
			{
				var parents = commit.Node.Parents.Select(parent => map[parent]).OfType<string>().Distinct().ToList();
				var files = commit.Files.Where(file => Belongs(file.Key)).ToDictionary();
				var same = parents.Count == 1 && SameFiles(store.Get(parents[0]).Files, files);
				if ((parents.Count == 0 && files.Count == 0) || same)
				{
					map[commit.Node.Id] = parents.FirstOrDefault();
					continue;
				}

				map[commit.Node.Id] = store.Add(parents, $"{commit.Node.Message.TrimEnd()}\n\n{SplitTrailer(allocation, commit.Node.Id)}", files);
				written++;
			}

			var head = map[allocation.TipCommit];
			var tip = store.Add(head is null ? [] : [head], request.RecordMessage(part), head is null ? new Dictionary<string, MemoryFile>() : store.Get(head).Files);
			await store.SetBranchAsync(allocation.Branch, tip);
			var kept = new List<string>();
			var dropped = new List<string>();
			foreach (var tag in sourceTags)
			{
				if (map[tag.Commit] is { } target)
				{
					store.SetTag(tag with { Commit = target }, origin.TagMessage(tag.Name) ?? tag.Subject);
					kept.Add(tag.Name);
				}
				else
				{
					dropped.Add(tag.Name);
				}
			}

			await store.CreateTagAsync(request.Tag, tip, request.RecordMessage(part));
			Put(store);

			var tipFiles = store.Get(tip).Files.Keys.ToHashSet(StringComparer.Ordinal);
			if (!tipFiles.SetEquals(allocation.Files[index].Select(file => file.Path)))
			{
				throw new StorageException($"The rewritten history of '{part.Name}' does not end in its allocated files; nothing was changed.");
			}

			results.Add(new SplitPartResult(part.Name, store.Location, allocation.Files[index].Count, allocation.Files[index].Sum(file => file.Size),
				written + 1, tip, kept, dropped, 0));
		}

		return results;
	}

	public Task DeleteAsync(string name)
	{
		lock (stores)
		{
			stores.Remove(name);
		}

		return Task.CompletedTask;
	}

	public IStagingArea BeginStaging() => new Staging(this);

	public IComponentWorkspace OpenWorkspace(string name, string directory)
	{
		lock (workspaces)
		{
			return workspaces.TryGetValue(Path.GetFullPath(directory), out var workspace)
				? workspace
				: throw new StorageException($"'{directory}' is not a working copy of component '{name}'.");
		}
	}

	public Task MoveBranchesAsync(IReadOnlyList<BranchMove> moves) => BranchMoves.InSequenceAsync(this, moves);

	/// <summary>Remembers working copies by folder, so a retry can reopen them like git reopens a checkout.</summary>
	internal void Track(MemoryWorkspace workspace)
	{
		lock (workspaces)
		{
			workspaces[Path.GetFullPath(workspace.Directory)] = workspace;
		}
	}

	private MemoryComponentStore Put(MemoryComponentStore store)
	{
		lock (stores)
		{
			if (stores.ContainsKey(store.Name))
			{
				throw new StorageException($"'{LocationOf(store.Name)}' already exists.");
			}

			store.Upstreams = Upstreams;
			store.Storage = this;
			store.Location = LocationOf(store.Name);
			stores[store.Name] = store;
			return store;
		}
	}

	private MemoryComponentStore Clone(string name, string url)
	{
		var remote = Upstreams.Find(url) ?? throw new StorageException($"Could not import '{url}': no such repository.");
		var copy = remote.Copy(name, LocationOf(name));
		copy.Upstream = url;
		copy.Upstreams = Upstreams;
		return copy;
	}

	private static bool SameFiles(IReadOnlyDictionary<string, MemoryFile> left, IReadOnlyDictionary<string, MemoryFile> right) =>
		left.Count == right.Count && left.All(file => right.TryGetValue(file.Key, out var other) && other.Object == file.Value.Object);

	private static string SplitTrailer(SplitAllocation allocation, string commit) => $"{ComponentSplitter.Trailer}: {allocation.Source.Name}@{commit}";

	private sealed class Staging(MemoryComponentStorage storage) : IStagingArea
	{
		private readonly List<MemoryComponentStore> staged = [];

		public Task<IComponentStore> ImportAsync(string name, string url)
		{
			var store = storage.Clone(name, url);
			store.Location = $"memory-staging:{name}";
			store.Storage = storage;
			staged.Add(store);
			return Task.FromResult<IComponentStore>(store);
		}

		public Task<IComponentStore> PromoteAsync(IComponentStore store)
		{
			var memory = (MemoryComponentStore)store;
			staged.Remove(memory);
			return Task.FromResult<IComponentStore>(storage.Put(memory));
		}

		public void Dispose() => staged.Clear();
	}
}
