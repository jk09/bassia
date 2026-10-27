namespace Bassia.Storage.Cloud;

using System.Collections.Concurrent;
using System.Globalization;
using Bassia.Git;
using Bassia.Split;
using Bassia.Storage.Git;

/// <summary>
/// <see cref="IComponentStorage"/> in the cloud: a component's objects are packfiles in an <see cref="IObjectStore"/>
/// (S3) and its refs are records of an <see cref="IRecordTable"/> (DynamoDB) - together the durable truth. Each node
/// keeps a cache, a bare git repository per component under <c>cacheRoot</c> filled from the packs, and runs real git on
/// it; nothing is ever written to the cache that is not published, or about to be.
/// </summary>
/// <remarks>
/// <para>
/// The table holds, per component (partition <c>component#&lt;name&gt;</c>): <c>meta</c> (the default branch
/// <c>head</c>, the <c>upstream</c>, <c>created</c>), one <c>ref#&lt;full ref&gt;</c> per branch and tag (its
/// <c>object</c>), and one <c>pack#&lt;pack&gt;</c> per pack in the object store at
/// <c>components/&lt;name&gt;/packs/&lt;pack&gt;.pack|.idx</c>.
/// </para>
/// <para>
/// A write (<see cref="PublishAsync"/>) packs the objects the new ref values reach and the table's values do not, uploads
/// the pack, then moves the refs in one conditional transaction with the pack's record: either every ref moves and the
/// pack is listed, or nothing changes and the uploaded pack is garbage (see <see cref="CloudGarbageCollector"/>).
/// </para>
/// </remarks>
internal sealed class CloudComponentStorage(IObjectStore objects, IRecordTable table, string cacheRoot) : IComponentStorage
{
	internal const string MetaKey = "meta";
	internal const string RefPrefix = "ref#";
	internal const string PackPrefix = "pack#";
	internal const string LeaseKey = "gc-lease";

	private readonly GitComponentStorage cache = new(cacheRoot);
	private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

	public IObjectStore Objects { get; } = objects;

	public IRecordTable Table { get; } = table;

	public string CacheRoot { get; } = cacheRoot;

	internal static string Partition(string name) => $"component#{name}";

	internal static string PackObject(string name, string pack, string extension) => $"components/{name}/packs/{pack}.{extension}";

	private string GitDir(string name) => Path.Combine(LocationOf(name), ".git");

	private SemaphoreSlim Gate(string name) => gates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));

	public bool Exists(string name) => Table.GetAsync(Partition(name), MetaKey).GetAwaiter().GetResult() is not null;

	/// <summary>The component's folder in this node's cache: a bare repository real git reads (see the class remarks).</summary>
	public string LocationOf(string name) => Path.Combine(CacheRoot, name);

	public IComponentStore Open(string name) =>
		Exists(name) ? new CloudComponentStore(this, name) : throw new StorageException($"Component '{name}' is not in {Table.Location}.");

	// ----- the cache -----

	/// <summary>
	/// Brings the cache of <paramref name="name"/> up to the table: creates it when missing, downloads the packs it lacks,
	/// and points its branches, tags and HEAD where the table does. Returns the component's records.
	/// </summary>
	internal async Task<IReadOnlyList<CloudRecord>> SyncAsync(string name)
	{
		var gate = Gate(name);
		await gate.WaitAsync();
		try
		{
			return await SyncLockedAsync(name);
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task<IReadOnlyList<CloudRecord>> SyncLockedAsync(string name)
	{
		var records = await Table.QueryAsync(Partition(name));
		var meta = records.FirstOrDefault(record => record.Sort == MetaKey) ?? throw new StorageException($"Component '{name}' is not in {Table.Location}.");
		await EnsureCacheAsync(name);

		var packDir = Path.Combine(GitDir(name), "objects", "pack");
		foreach (var pack in records.Where(record => record.Sort.StartsWith(PackPrefix, StringComparison.Ordinal)).Select(record => record.Sort[PackPrefix.Length..]))
		{
			if (File.Exists(Path.Combine(packDir, pack + ".idx")))
			{
				continue;
			}

			// The index last: git uses a pack only once its index is there.
			foreach (var extension in new[] { "pack", "idx" })
			{
				var partial = Path.Combine(packDir, $"tmp-{Guid.NewGuid():N}.{extension}");
				await Objects.GetFileAsync(PackObject(name, pack, extension), partial);
				File.Move(partial, Path.Combine(packDir, $"{pack}.{extension}"), overwrite: true);
			}
		}

		var git = GitClient.In(LocationOf(name));
		await SetLocalRefsAsync(git, Refs(records));
		if (meta["head"] is { Length: > 0 } head)
		{
			await git.RunOrThrowAsync(["symbolic-ref", "HEAD", $"refs/heads/{head}"]);
		}

		var upstream = meta["upstream"] ?? "";
		var configured = (await git.RunAsync(["config", "--get", "remote.origin.url"])).Output.Trim();
		if (upstream != configured)
		{
			await git.RunOrThrowAsync(upstream.Length == 0 ? ["config", "--unset", "remote.origin.url"] : ["config", "remote.origin.url", upstream]);
		}

		return records;
	}

	/// <summary>A bare repository that serves partial clones and objects by id (what the gateway and sparse working copies ask for).</summary>
	private async Task EnsureCacheAsync(string name)
	{
		if (Directory.Exists(GitDir(name)))
		{
			return;
		}

		Directory.CreateDirectory(CacheRoot);
		await GitClient.In(CacheRoot).RunOrThrowAsync(["init", "--bare", "--quiet", GitDir(name)]);
		await ConfigureCacheAsync(name);
	}

	private async Task ConfigureCacheAsync(string name)
	{
		var git = GitClient.In(LocationOf(name));
		await git.RunOrThrowAsync(["config", "uploadpack.allowFilter", "true"]);
		await git.RunOrThrowAsync(["config", "uploadpack.allowAnySHA1InWant", "true"]);
		// Refs a publish or an upstream fetch keeps aside for a moment are nobody's to fetch.
		await git.RunOrThrowAsync(["config", "transfer.hideRefs", "refs/bassia-"]);
		// The cache never forgets an object on its own: packs are deleted by the collector, in the object store first.
		await git.RunOrThrowAsync(["config", "gc.auto", "0"]);
	}

	/// <summary>The branches and tags of the records, by full ref name.</summary>
	internal static Dictionary<string, string> Refs(IEnumerable<CloudRecord> records) =>
		records.Where(record => record.Sort.StartsWith(RefPrefix, StringComparison.Ordinal) && record["object"] is not null)
			.ToDictionary(record => record.Sort[RefPrefix.Length..], record => record["object"]!, StringComparer.Ordinal);

	/// <summary>Points the cache's branches and tags exactly where <paramref name="wanted"/> says, deleting the others.</summary>
	private static async Task SetLocalRefsAsync(GitClient git, IReadOnlyDictionary<string, string> wanted)
	{
		var current = await LocalRefsAsync(git);
		var script = new System.Text.StringBuilder();
		foreach (var (name, value) in wanted.Where(pair => current.GetValueOrDefault(pair.Key) != pair.Value))
		{
			script.Append(CultureInfo.InvariantCulture, $"update {name} {value}\n");
		}

		foreach (var name in current.Keys.Where(name => !wanted.ContainsKey(name)))
		{
			script.Append(CultureInfo.InvariantCulture, $"delete {name}\n");
		}

		if (script.Length > 0)
		{
			await git.RunOrThrowAsync(["update-ref", "--stdin"], script.ToString());
		}
	}

	internal static async Task<Dictionary<string, string>> LocalRefsAsync(GitClient git, params string[] patterns) =>
		GitComponentStore.Lines(await git.RunOrThrowAsync(["for-each-ref", "--format=%(refname) %(objectname)", .. patterns.Length == 0 ? ["refs/heads", "refs/tags"] : patterns]))
			.Select(line => line.Split(' ', 2))
			.ToDictionary(fields => fields[0], fields => fields[1], StringComparer.Ordinal);

	// ----- the write path -----

	/// <summary>
	/// Publishes ref changes of <paramref name="name"/> whose objects are in its cache: packs and uploads the objects the
	/// table does not reach yet, then moves every ref - with the <paramref name="extra"/> records - in one transaction
	/// under each update's expectation, and finally moves the cache's refs. Throws <see cref="CloudConflictException"/>
	/// when an expectation does not hold; nothing moved then.
	/// </summary>
	internal async Task PublishAsync(string name, IReadOnlyList<RefUpdate> updates, IReadOnlyList<CloudWrite>? extra = null)
	{
		var gate = Gate(name);
		await gate.WaitAsync();
		try
		{
			var git = GitClient.In(LocationOf(name));
			var records = await Table.QueryAsync(Partition(name));
			var published = records.Where(record => record.Sort.StartsWith(RefPrefix, StringComparison.Ordinal)).ToDictionary(record => record.Sort[RefPrefix.Length..], StringComparer.Ordinal);

			var writes = new List<CloudWrite>();
			foreach (var update in updates)
			{
				if (await RefWriteAsync(git, name, update, published.GetValueOrDefault(update.Ref)) is { } write)
				{
					writes.Add(write);
				}
			}

			var known = Refs(records).Values.Distinct(StringComparer.Ordinal).ToList();
			var tips = updates.Where(update => update.Object is not null).Select(update => update.Object!).Distinct(StringComparer.Ordinal).ToList();
			if (await UploadPackAsync(name, git, tips, known) is { } pack)
			{
				writes.Insert(0, pack);
			}

			writes.AddRange(extra ?? []);
			try
			{
				await WriteAsync(writes);
			}
			catch (CloudConflictException)
			{
				var refs = updates.Select(update => update.Ref.Replace("refs/heads/", "").Replace("refs/", ""));
				throw new CloudConflictException($"{string.Join(", ", refs)} of component '{name}' moved meanwhile; nothing was published.");
			}

			var local = await LocalRefsAsync(git);
			foreach (var update in updates)
			{
				if (update.Object is null)
				{
					local.Remove(update.Ref);
				}
				else
				{
					local[update.Ref] = update.Object;
				}
			}

			await SetLocalRefsAsync(git, local);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>The write of one update under its expectation, or null when there is nothing to write (already so).</summary>
	private static async Task<CloudWrite?> RefWriteAsync(GitClient git, string name, RefUpdate update, CloudRecord? current)
	{
		var value = current?["object"];
		var values = update.Object is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["object"] = update.Object };
		var expected = update.Expect switch
		{
			RefExpect.Any => (long?)null,
			RefExpect.Absent when current is null => 0,
			RefExpect.AbsentOrSame when current is null => 0,
			RefExpect.AbsentOrSame when value == update.Object => -1,
			RefExpect.Value when current is not null && value == update.ExpectedObject => current.Version,
			RefExpect.FastForward when current is null => 0,
			RefExpect.FastForward when value == update.Object => -1,
			RefExpect.FastForward when update.Object is not null && value is not null
				&& (await git.RunAsync(["merge-base", "--is-ancestor", value, update.Object])).ExitCode == 0 => current!.Version,
			_ => throw new CloudConflictException(Conflict(name, update, value))
		};

		return expected == -1 ? null : new CloudWrite(Partition(name), RefPrefix + update.Ref, values, expected);
	}

	internal static string Conflict(string name, RefUpdate update, string? value)
	{
		var what = update.Ref.StartsWith("refs/tags/", StringComparison.Ordinal) ? $"Tag '{update.Ref["refs/tags/".Length..]}'" : $"Branch '{update.Ref.Replace("refs/heads/", "")}'";
		return update.Expect switch
		{
			RefExpect.Absent or RefExpect.AbsentOrSame => $"{what} of component '{name}' already exists.",
			RefExpect.FastForward => $"{what} of component '{name}' moved to {Short(value)}, which {Short(update.Object)} does not contain: fetch and merge it first.",
			_ => $"{what} of component '{name}' moved meanwhile: it points at {Short(value)}, not {Short(update.ExpectedObject)}."
		};

		static string Short(string? id) => id is null ? "nothing" : id[..Math.Min(7, id.Length)];
	}

	/// <summary>
	/// Packs the objects <paramref name="tips"/> reach and <paramref name="known"/> does not into the cache, uploads the
	/// pack (data, then index), and returns its record; null when there is nothing new.
	/// </summary>
	private async Task<CloudWrite?> UploadPackAsync(string name, GitClient git, IReadOnlyList<string> tips, IReadOnlyList<string> known)
	{
		if (tips.Count == 0)
		{
			return null;
		}

		// Only the known values the cache has bound the walk: a pack too big is harmless, one too small is not.
		var bounds = new List<string>();
		foreach (var value in known)
		{
			if ((await git.RunAsync(["cat-file", "-e", value])).ExitCode == 0)
			{
				bounds.Add(value);
			}
		}

		var revisions = string.Concat(tips.Select(tip => tip + "\n")) + string.Concat(bounds.Select(value => $"^{value}\n"));
		var reachable = await git.RunOrThrowAsync(["rev-list", "--objects", "--stdin"], revisions);
		if (reachable.Length == 0)
		{
			return null;
		}

		var packDir = Path.Combine(GitDir(name), "objects", "pack");
		var pack = "pack-" + await git.RunOrThrowAsync(["pack-objects", "--revs", "--quiet", Path.Combine(packDir, "pack")], revisions);
		var size = new FileInfo(Path.Combine(packDir, pack + ".pack")).Length;
		await Objects.PutFileAsync(PackObject(name, pack, "pack"), Path.Combine(packDir, pack + ".pack"));
		await Objects.PutFileAsync(PackObject(name, pack, "idx"), Path.Combine(packDir, pack + ".idx"));
		return PackRecord(name, pack, size);
	}

	internal static CloudWrite PackRecord(string name, string pack, long size) =>
		new(Partition(name), PackPrefix + pack, new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
			["size"] = size.ToString(CultureInfo.InvariantCulture)
		});

	/// <summary>
	/// The writes in one transaction. More than one may hold (an import of many refs) is written pack records first, in
	/// several, which is safe only for writes no one else expects: a new component's.
	/// </summary>
	private async Task WriteAsync(List<CloudWrite> writes)
	{
		if (writes.Count <= IRecordTable.MaxWrites)
		{
			await Table.WriteAsync(writes);
			return;
		}

		if (writes.Any(write => write.ExpectedVersion is > 0))
		{
			throw new StorageException($"{writes.Count} ref changes are more than one transaction holds ({IRecordTable.MaxWrites}); nothing was written.");
		}

		// The meta record last: until it is written, the component does not exist.
		var ordered = writes.OrderBy(write => write.Sort == MetaKey).ThenBy(write => !write.Sort.StartsWith(PackPrefix, StringComparison.Ordinal)).ToList();
		foreach (var chunk in ordered.Chunk(IRecordTable.MaxWrites))
		{
			await Table.WriteAsync(chunk);
		}
	}

	internal static CloudWrite MetaRecord(string name, string? head, string upstream) =>
		new(Partition(name), MetaKey, new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["head"] = head ?? "",
			["upstream"] = upstream,
			["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
		}, ExpectedVersion: 0);

	/// <summary>Publishes a cache made by git (a clone, an import, a split part) as the new component <paramref name="name"/>: every branch and tag.</summary>
	private async Task<IComponentStore> PublishNewAsync(string name, string upstream)
	{
		var git = GitClient.In(LocationOf(name));
		if ((await git.RunAsync(["rev-parse", "--show-object-format"])).Output.Trim() is { Length: > 0 } format && format != "sha1")
		{
			throw new StorageException($"Component '{name}' uses {format} object ids; the cloud storage holds sha1 repositories only.");
		}

		await ConfigureCacheAsync(name);
		var head = await git.RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"]);
		var refs = await LocalRefsAsync(git);
		await PublishAsync(name, refs.Select(pair => new RefUpdate(pair.Key, pair.Value, RefExpect.Absent)).ToList(),
			[MetaRecord(name, head.ExitCode == 0 ? head.Output.Trim() : null, upstream)]);
		return new CloudComponentStore(this, name);
	}

	/// <summary>Runs <paramref name="build"/> to make the cache of a new component, publishes it, and leaves no cache behind on failure.</summary>
	private async Task<IComponentStore> CreateWithAsync(string name, Func<Task> build, Func<Task<string>> upstream)
	{
		if (Exists(name))
		{
			throw new StorageException($"Component '{name}' already exists in {Table.Location}.");
		}

		GitComponentStorage.DeleteDirectory(LocationOf(name));
		Directory.CreateDirectory(CacheRoot);
		try
		{
			await build();
			return await PublishNewAsync(name, await upstream());
		}
		catch
		{
			GitComponentStorage.DeleteDirectory(LocationOf(name));
			throw;
		}
	}

	// ----- the contract -----

	public Task<IComponentStore> CreateAsync(string name, string defaultBranch, string url, string message) =>
		CreateWithAsync(name, () => cache.CreateAsync(name, defaultBranch, url, message), () => Task.FromResult(url));

	public Task<IComponentStore> ImportAsync(string name, string url) =>
		CreateWithAsync(name, () => cache.ImportAsync(name, url), () => Task.FromResult(url));

	/// <summary>The same as <see cref="ImportAsync"/>: the cloud holds every component's content, so it never needs its upstream for it.</summary>
	public Task<IComponentStore> ImportHistoryAsync(string name, string url) => ImportAsync(name, url);

	/// <summary>
	/// Copies the parent's packs within the object store - nothing is downloaded - and writes the fork's records: its
	/// branch at <paramref name="commit"/> and the named tags as the parent has them. Objects only the parent's later
	/// history reaches come along unreferenced, until the collector drops them.
	/// </summary>
	public async Task<IComponentStore> ForkAsync(IComponentStore source, string name, string branch, string commit, IReadOnlyList<string> tags, string url)
	{
		if (Exists(name))
		{
			throw new StorageException($"Component '{name}' already exists in {Table.Location}.");
		}

		var parent = await Table.QueryAsync(Partition(source.Name));
		var parentRefs = Refs(parent);
		var writes = new List<CloudWrite> { MetaRecord(name, branch, url) };
		foreach (var record in parent.Where(record => record.Sort.StartsWith(PackPrefix, StringComparison.Ordinal)))
		{
			var pack = record.Sort[PackPrefix.Length..];
			await Objects.CopyAsync(PackObject(source.Name, pack, "pack"), PackObject(name, pack, "pack"));
			await Objects.CopyAsync(PackObject(source.Name, pack, "idx"), PackObject(name, pack, "idx"));
			writes.Add(new CloudWrite(Partition(name), record.Sort, record.Values));
		}

		writes.Add(new CloudWrite(Partition(name), $"{RefPrefix}refs/heads/{branch}", new Dictionary<string, string> { ["object"] = commit }));
		foreach (var tag in tags)
		{
			var value = parentRefs.GetValueOrDefault($"refs/tags/{tag}") ?? throw new StorageException($"Component '{source.Name}' has no tag '{tag}'.");
			writes.Add(new CloudWrite(Partition(name), $"{RefPrefix}refs/tags/{tag}", new Dictionary<string, string> { ["object"] = value }));
		}

		GitComponentStorage.DeleteDirectory(LocationOf(name));
		await WriteAsync(writes);
		return new CloudComponentStore(this, name);
	}

	/// <summary>Splits in the cache, as git storage does, then publishes each part as a new component.</summary>
	public async Task<IReadOnlyList<SplitPartResult>> SplitAsync(IComponentStore source, SplitHistoryRequest request)
	{
		var store = (CloudComponentStore)source;
		await store.SyncedAsync();
		foreach (var part in request.Allocation.Plan.Parts.Where(part => !Exists(part.Name)))
		{
			GitComponentStorage.DeleteDirectory(LocationOf(part.Name));
		}

		var parts = await GitHistorySplitter.SplitAsync(CacheRoot, store.Cache, request);
		foreach (var (part, result) in request.Allocation.Plan.Parts.Zip(parts))
		{
			await PublishNewAsync(result.Name, part.Url ?? "");
		}

		return parts;
	}

	/// <summary>The meta record first (from then on the component does not exist), then its other records, its packs and the cache.</summary>
	public async Task DeleteAsync(string name)
	{
		var records = await Table.QueryAsync(Partition(name));
		await Table.WriteAsync([new CloudWrite(Partition(name), MetaKey, null)]);
		foreach (var chunk in records.Where(record => record.Sort != MetaKey).Chunk(IRecordTable.MaxWrites))
		{
			await Table.WriteAsync(chunk.Select(record => new CloudWrite(record.Partition, record.Sort, null)).ToList());
		}

		foreach (var key in await Objects.ListAsync($"components/{name}/"))
		{
			await Objects.DeleteAsync(key);
		}

		GitComponentStorage.DeleteDirectory(LocationOf(name));
	}

	public IStagingArea BeginStaging() => new Staging(this);

	public IComponentWorkspace OpenWorkspace(string name, string directory) => new CloudWorkspace(this, name, new GitWorkspace(directory, LocationOf(name)));

	/// <summary>
	/// One transaction: every branch moves only while its record still holds the expected commit. The commits are
	/// published already (an integration publishes its branch before it lands), so no pack is written.
	/// </summary>
	public async Task MoveBranchesAsync(IReadOnlyList<BranchMove> moves)
	{
		if (moves.Count > IRecordTable.MaxWrites)
		{
			throw new StorageException($"{moves.Count} branches are more than one transaction moves ({IRecordTable.MaxWrites}); none was moved.");
		}

		var writes = new List<CloudWrite>();
		var moved = new List<BranchMove>();
		foreach (var move in moves)
		{
			var record = await Table.GetAsync(Partition(move.Component), $"{RefPrefix}refs/heads/{move.Branch}");
			if (record?["object"] != move.Expected)
			{
				moved.Add(move);
				continue;
			}

			writes.Add(new CloudWrite(record.Partition, record.Sort, new Dictionary<string, string> { ["object"] = move.Commit }, record.Version));
		}

		if (moved.Count > 0)
		{
			throw new BranchesMovedException(moved);
		}

		try
		{
			await Table.WriteAsync(writes);
		}
		catch (CloudConflictException)
		{
			var now = new List<BranchMove>();
			foreach (var move in moves)
			{
				if ((await Table.GetAsync(Partition(move.Component), $"{RefPrefix}refs/heads/{move.Branch}"))?["object"] != move.Expected)
				{
					now.Add(move);
				}
			}

			throw new BranchesMovedException(now.Count > 0 ? now : moves);
		}

		foreach (var component in moves.Select(move => move.Component).Distinct(StringComparer.Ordinal))
		{
			await SyncAsync(component);
		}
	}

	/// <summary>Bare clones in a staging folder of the cache, moved into it and published when promoted.</summary>
	private sealed class Staging(CloudComponentStorage storage) : IStagingArea
	{
		private readonly string stagingDir = Path.Combine(storage.CacheRoot, $".staging-{Guid.NewGuid():N}"[..18]);

		public async Task<IComponentStore> ImportAsync(string name, string url)
		{
			var repoDir = Path.Combine(stagingDir, name);
			Directory.CreateDirectory(stagingDir);
			var clone = await GitClient.In(stagingDir).RunAsync(["clone", "--bare", "--quiet", url, Path.Combine(repoDir, ".git")]);
			if (clone.ExitCode != 0)
			{
				throw new GitException(clone.Error.Trim());
			}

			return new GitComponentStore(name, repoDir);
		}

		public async Task<IComponentStore> PromoteAsync(IComponentStore staged) =>
			await storage.CreateWithAsync(staged.Name,
				() =>
				{
					Directory.Move(staged.Location, storage.LocationOf(staged.Name));
					return Task.CompletedTask;
				},
				async () => await new GitComponentStore(staged.Name, storage.LocationOf(staged.Name)).UpstreamAsync() ?? "");

		public void Dispose() => GitComponentStorage.DeleteDirectory(stagingDir);
	}
}

/// <summary>What a ref must be before <see cref="CloudComponentStorage.PublishAsync"/> moves it.</summary>
internal enum RefExpect
{
	/// <summary>Anything: the ref moves whatever it is.</summary>
	Any,

	/// <summary>At <see cref="RefUpdate.ExpectedObject"/> (compare-and-swap).</summary>
	Value,

	/// <summary>Not there yet.</summary>
	Absent,

	/// <summary>Not there, or at a commit the new one contains.</summary>
	FastForward,

	/// <summary>Not there, or already at the new value (a tag published again).</summary>
	AbsentOrSame
}

/// <summary>A ref to move: its full name, the object it is to point at (null deletes it), and what it must be now.</summary>
internal sealed record RefUpdate(string Ref, string? Object, RefExpect Expect, string? ExpectedObject = null);
