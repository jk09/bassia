namespace Bassia.Core.Workspaces;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Text;
using Bassia.Core.Version;

/// <summary>What the workspace last saw at a path: the tree's leaf, the file's size and modification time on disk, and
/// the id the disk bytes hash to (equal to the leaf's id, except for a conflict materialized with markers). A
/// <see cref="Placeholder"/> was written lazily: the file has the leaf's size and mode but no content until a virtual
/// file system hydrates it on first use (<see cref="Placeholders"/>), keeping its size and modification time.</summary>
public sealed record TreeStateEntry(Leaf Leaf, long Size, long Mtime, ObjectId DiskId, bool Placeholder = false);

/// <summary>
/// A workspace's tree state: for each tracked path the leaf last seen plus local size and modification time, and the
/// working tree they correspond to. It is what makes status cheap: only paths whose size or time differ are hashed.
/// </summary>
public sealed class TreeState
{
	public ObjectId Tree { get; set; }

	public Dictionary<string, TreeStateEntry> Entries { get; init; } = new(StringComparer.Ordinal);

	/// <summary>
	/// The context-spec the folder holds (<see cref="ContextSpec"/>): a copy of the workspace record's, as it was when the
	/// folder was last brought in step with it. Only in-context paths are tracked.
	/// </summary>
	public ContextSpec Context { get; set; } = ContextSpec.All;

	/// <summary>
	/// For a lazily materialized folder: the backing directory that holds its files and placeholders, which a virtual
	/// file system projects at the workspace's folder. Null: the folder holds the files itself.
	/// </summary>
	public string? Backing { get; set; }

	private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new IdConverter() } };

	public static TreeState Load(string file)
	{
		if (!File.Exists(file))
		{
			return new TreeState();
		}

		var data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file), Json) ?? throw new VersionException($"Unreadable tree state '{file}'.");
		return new TreeState
		{
			Tree = data.Tree,
			Context = ContextSpec.Of(data.Context),
			Backing = data.Backing,
			Entries = data.Entries.ToDictionary(entry => entry.Path, entry => new TreeStateEntry(
				new Leaf(entry.Kind, entry.Id, entry.LeafSize, entry.Executable), entry.Size, entry.Mtime, entry.DiskId, entry.Placeholder == true), StringComparer.Ordinal)
		};
	}

	public void Save(string file)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		var data = new Stored(Tree, Context.IsAll ? null : Context.Patterns.ToList(), Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new StoredEntry(pair.Key, pair.Value.Leaf.Kind,
			pair.Value.Leaf.Id, pair.Value.Leaf.Size, pair.Value.Leaf.Executable, pair.Value.Size, pair.Value.Mtime, pair.Value.DiskId, pair.Value.Placeholder ? true : null)).ToList(), Backing);
		var temp = file + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(data, Json));
		File.Move(temp, file, true);
	}

	private sealed record Stored(ObjectId Tree, List<string>? Context, List<StoredEntry> Entries, string? Backing = null);

	private sealed record StoredEntry(string Path, EntryKind Kind, ObjectId Id, long LeafSize, bool Executable, long Size, long Mtime, ObjectId DiskId,
		bool? Placeholder = null);

	private sealed class IdConverter : JsonConverter<ObjectId>
	{
		public override ObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ObjectId.Parse(reader.GetString()!);

		public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Hex);
	}
}

/// <summary>
/// Writes a file's content somewhere cheaper than streaming it from the object store - for example by block-cloning
/// it from a local cache on a copy-on-write volume (Windows Dev Drive). It may decline; the provider then writes the
/// file itself.
/// </summary>
public interface IFileMaterializer
{
	/// <summary>Creates <paramref name="destination"/> (which does not exist) with the leaf's content; false when it did not.</summary>
	Task<bool> TryWriteAsync(Leaf leaf, string destination, CancellationToken ct = default);
}

/// <summary>What a scan of the working directory found: the path changes to snapshot and the paths it hashed.</summary>
public sealed record ScanResult(IReadOnlyList<PathChange> Changes, int Hashed, int Unchanged);

/// <summary>
/// The provider behind a workspace directory: enumerate and materialize trees, read files, report dirty paths. A ProjFS
/// provider on Windows projects trees lazily and journals changes; this plain-checkout provider writes files out and
/// finds changes by comparing size and modification time with the tree state, for CI machines and other systems.
/// </summary>
public interface IWorkspaceProvider
{
	string Root { get; }

	/// <summary>Makes the directory hold <paramref name="to"/>, given it holds <paramref name="state"/>'s tree.</summary>
	Task UpdateAsync(TreeState state, ObjectId to, CancellationToken ct = default);

	/// <summary>
	/// Finds what changed on disk since <paramref name="state"/> and stores the new content (only missing chunks are
	/// uploaded). A change to a path outside <paramref name="context"/> (default: the state's) fails the whole scan with
	/// a <see cref="ContextBoundaryException"/> before anything is stored.
	/// </summary>
	Task<ScanResult> ScanAsync(TreeState state, RepoPolicy policy, bool store = true, CancellationToken ct = default, ContextSpec? context = null);

	/// <summary>
	/// Brings the directory in step with a new context-spec: writes the paths <paramref name="context"/> adds and removes
	/// the ones it drops, for the tree it holds. Snapshot first: a dropped path's local edits are not kept.
	/// </summary>
	Task<ContextResult> ApplyContextAsync(TreeState state, ContextSpec context, CancellationToken ct = default);
}

/// <summary>What bringing a folder in step with a new context-spec did: files written, and files removed.</summary>
public sealed record ContextResult(int Added, int Removed);

/// <summary>A plain directory checkout of a workspace's tree.</summary>
public sealed class CheckoutProvider(string root, Repository repo) : IWorkspaceProvider
{
	/// <summary>The file in a workspace's root that names its monorepo and workspace; never snapshotted.</summary>
	public const string MarkerName = ".bassia-workspace";

	/// <summary>The suffix of a file a placeholder is being hydrated into; never snapshotted.</summary>
	public const string HydratingSuffix = ".bassia-hydrate";

	public string Root { get; } = Path.GetFullPath(root);

	/// <summary>
	/// Whether the file system has an executable bit to read and write (not on Windows). Without one, a file keeps the
	/// mode its tree recorded: a snapshot must not read "not executable" from a file system that cannot say.
	/// </summary>
	public bool TracksExecutable { get; init; } = !OperatingSystem.IsWindows();

	/// <summary>Tried first for every file; null writes every file from the store.</summary>
	public IFileMaterializer? Materializer { get; init; }

	/// <summary>
	/// Writes regular files as placeholders - the right size and mode, no content, nothing fetched - for a virtual file
	/// system to hydrate on first use. Symlinks and conflicts are written in full.
	/// </summary>
	public bool Lazy { get; init; }

	/// <summary>Files written as placeholders by this provider.</summary>
	public int Placeholders { get; private set; }

	/// <summary>The paths (files and directories) this provider wrote or removed since <see cref="TakeChanged"/>.</summary>
	private readonly List<string> changed = [];

	/// <summary>The paths written or removed since the last call, for a virtual file system to show again.</summary>
	public IReadOnlyList<string> TakeChanged()
	{
		var paths = changed.Distinct(StringComparer.Ordinal).ToList();
		changed.Clear();
		return paths;
	}

	/// <summary>The modification time of placeholders: in the past, so a snapshot trusts it (no racy re-hash).</summary>
	private readonly DateTime placeholderTime = new(DateTime.UtcNow.AddMinutes(-1).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

	/// <summary>Files the materializer created, and files written from the store, by this provider.</summary>
	public int Materialized { get; private set; }

	public int Written { get; private set; }

	private string Full(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

	public async Task UpdateAsync(TreeState state, ObjectId to, CancellationToken ct = default)
	{
		Directory.CreateDirectory(Root);
		var context = state.Context;
		var changes = await repo.Trees.DiffAsync(state.Tree.IsEmpty ? null : state.Tree, to, "", context.IsAll ? null : context.MayContainUnder, ct);
		var emptied = new List<string>();
		foreach (var change in changes.Where(change => change.New is null || change.New.Kind == EntryKind.Tree))
		{
			if (change.Old?.Kind == EntryKind.Tree || change.New?.Kind == EntryKind.Tree)
			{
				// A directory nothing of which is in context: shown empty, so the folder keeps the monorepo's shape.
				if (change.New?.Kind == EntryKind.Tree)
				{
					Directory.CreateDirectory(Full(change.Path));
				}
				else
				{
					emptied.Add(change.Path);
				}

				changed.Add(change.Path);
				continue;
			}

			if (state.Entries.Remove(change.Path))
			{
				Delete(change.Path);
				changed.Add(change.Path);
				emptied.Add(PathNames.Parent(change.Path));
			}
		}

		var written = changes.Where(change => change.New is { } leaf && leaf.Kind != EntryKind.Tree && context.Contains(change.Path)).ToList();
		// The content of every new file in a few batched round trips, before any is written (a lazy folder fetches none).
		await repo.Objects.PrefetchContentAsync(written.Where(change => !IsPlaceholder(change.Path, change.New!)).Select(change => change.New!), ct);
		foreach (var change in written)
		{
			state.Entries[change.Path] = await WriteAsync(change.Path, change.New!, ct);
			changed.Add(change.Path);
		}

		RemoveEmptyDirectories(emptied);
		state.Tree = to;
	}

	public async Task<ContextResult> ApplyContextAsync(TreeState state, ContextSpec context, CancellationToken ct = default)
	{
		Directory.CreateDirectory(Root);
		var dropped = state.Entries.Keys.Where(path => !context.Contains(path)).ToList();
		foreach (var path in dropped)
		{
			Delete(path);
			state.Entries.Remove(path);
			changed.Add(path);
		}

		RemoveEmptyDirectories(dropped.Select(PathNames.Parent));
		state.Context = context;
		var added = 0;
		if (!state.Tree.IsEmpty)
		{
			var all = await repo.Trees.DiffAsync(null, state.Tree, "", context.IsAll ? null : context.MayContainUnder, ct);
			foreach (var change in all.Where(change => change.New?.Kind == EntryKind.Tree))
			{
				if (!Directory.Exists(Full(change.Path)))
				{
					Directory.CreateDirectory(Full(change.Path));
					changed.Add(change.Path);
				}
			}

			var adding = all.Where(change => change.New is { } leaf && leaf.Kind != EntryKind.Tree && context.Contains(change.Path) && !state.Entries.ContainsKey(change.Path)).ToList();
			await repo.Objects.PrefetchContentAsync(adding.Where(change => !IsPlaceholder(change.Path, change.New!)).Select(change => change.New!), ct);
			foreach (var change in adding)
			{
				state.Entries[change.Path] = await WriteAsync(change.Path, change.New!, ct);
				changed.Add(change.Path);
				added++;
			}
		}

		return new ContextResult(added, dropped.Count);
	}

	/// <summary>Whether a lazy folder writes this file as a placeholder: every regular file but <c>.bassia.toml</c>, which a snapshot reads.</summary>
	private bool IsPlaceholder(string path, Leaf leaf) => Lazy && leaf.Kind is EntryKind.File or EntryKind.Chunked && path != RepoPolicy.FileName;

	private async Task<TreeStateEntry> WriteAsync(string path, Leaf leaf, CancellationToken ct)
	{
		var full = Full(path);
		if (IsPlaceholder(path, leaf))
		{
			return WritePlaceholder(path, full, leaf);
		}

		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		if (Directory.Exists(full))
		{
			Directory.Delete(full, true);
		}
		else if (File.Exists(full) || new FileInfo(full).LinkTarget is not null)
		{
			File.Delete(full);
		}

		ObjectId diskId;
		if (leaf.Kind == EntryKind.Symlink)
		{
			var target = Encoding.UTF8.GetString(await repo.Objects.ReadContentAsync(leaf, ct));
			try
			{
				File.CreateSymbolicLink(full, target);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
			{
				// Without symlink rights (Windows), the link is written as a file holding its target.
				await File.WriteAllTextAsync(full, target, ct);
			}

			diskId = leaf.Id;
		}
		else if (leaf.Kind == EntryKind.Conflict)
		{
			var bytes = await MaterializeConflictAsync(path, leaf, ct);
			await File.WriteAllBytesAsync(full, bytes, ct);
			diskId = repo.Objects.Identify(bytes).Id;
		}
		else
		{
			if (Materializer is not null && await TryMaterializeAsync(leaf, full, ct))
			{
				Materialized++;
			}
			else
			{
				await using (var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					await repo.Objects.CopyContentAsync(leaf, stream, ct);
				}

				Written++;
			}

			diskId = leaf.Id;
		}

		if (TracksExecutable)
		{
			SetExecutable(full, leaf.Executable);
		}
		var info = new FileInfo(full);
		return new TreeStateEntry(leaf, LinkOrLength(info), Mtime(info), diskId);
	}

	private TreeStateEntry WritePlaceholder(string path, string full, Leaf leaf)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		Delete(path);
		using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
		{
			// Sparse: the size a listing shows, without the content.
			stream.SetLength(leaf.Size);
		}

		if (TracksExecutable)
		{
			SetExecutable(full, leaf.Executable);
		}

		File.SetLastWriteTimeUtc(full, placeholderTime);
		Placeholders++;
		var info = new FileInfo(full);
		return new TreeStateEntry(leaf, info.Length, Mtime(info), leaf.Id, Placeholder: true);
	}

	private async Task<bool> TryMaterializeAsync(Leaf leaf, string full, CancellationToken ct)
	{
		try
		{
			if (await Materializer!.TryWriteAsync(leaf, full, ct))
			{
				return true;
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// The cheap way failed; the store still has the content.
		}

		if (File.Exists(full))
		{
			File.Delete(full);
		}

		return false;
	}

	/// <summary>A conflict as text with markers: the workspace's side, the base, then mainline's side.</summary>
	private async Task<byte[]> MaterializeConflictAsync(string path, Leaf leaf, CancellationToken ct)
	{
		var conflict = await repo.Objects.ReadAsync<ConflictObject>(leaf.Id, ct);
		async Task<string?> Text(Leaf? side) => side is { IsFile: true } ? Encoding.UTF8.GetString(await repo.Objects.ReadContentAsync(side, ct)) : null;
		var labels = new[] { "workspace", "mainline" };
		var sides = new List<(string, string?)>();
		for (var index = 0; index < conflict.Sides.Count; index++)
		{
			sides.Add((index < labels.Length ? labels[index] : $"side {index + 1}", await Text(conflict.Sides[index])));
		}

		return Encoding.UTF8.GetBytes(TextMerge.Materialize(await Text(conflict.Bases.FirstOrDefault()), sides));
	}

	private void Delete(string path)
	{
		var full = Full(path);
		if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null)
		{
			Directory.Delete(full, true);
		}
		else if (File.Exists(full) || new FileInfo(full).LinkTarget is not null)
		{
			File.Delete(full);
		}
	}

	private void RemoveEmptyDirectories(IEnumerable<string> directories)
	{
		foreach (var directory in directories.Distinct().OrderByDescending(dir => dir.Length))
		{
			for (var dir = directory; dir.Length > 0; dir = PathNames.Parent(dir))
			{
				var full = Full(dir);
				if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any())
				{
					break;
				}

				Directory.Delete(full);
				changed.Add(dir);
			}
		}
	}

	public async Task<ScanResult> ScanAsync(TreeState state, RepoPolicy policy, bool store = true, CancellationToken ct = default, ContextSpec? context = null)
	{
		context ??= state.Context;
		var outside = new List<string>();
		var changes = new List<PathChange>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var hashed = 0;
		var unchanged = 0;
		var trackedDirs = new HashSet<string>(state.Entries.Keys.SelectMany(DirectoriesOf), StringComparer.Ordinal);
		var collisions = new List<string>();
		await ScanDirectoryAsync("");

		async Task ScanDirectoryAsync(string dir)
		{
			var full = dir.Length == 0 ? Root : Full(dir);
			if (!Directory.Exists(full))
			{
				return;
			}

			var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos().OrderBy(info => info.Name, StringComparer.Ordinal))
			{
				ct.ThrowIfCancellationRequested();
				var path = PathNames.Join(dir, entry.Name);
				if ((dir.Length == 0 && entry.Name == MarkerName) || entry.Name.EndsWith(HydratingSuffix, StringComparison.Ordinal))
				{
					continue;
				}

				var isDirectory = entry is DirectoryInfo && entry.LinkTarget is null;
				var tracked = isDirectory ? trackedDirs.Contains(path) : state.Entries.ContainsKey(path);
				if (!tracked && policy.IsIgnored(path, isDirectory))
				{
					continue;
				}

				if (!PathNames.IsValidName(entry.Name))
				{
					continue;
				}

				if (names.TryGetValue(entry.Name, out var other))
				{
					collisions.Add($"{PathNames.Join(dir, other)} and {path}");
				}

				names[entry.Name] = entry.Name;
				if (isDirectory)
				{
					await ScanDirectoryAsync(path);
					continue;
				}

				seen.Add(path);
				var info = (FileInfo)(entry is FileInfo file ? file : new FileInfo(entry.FullName));
				var size = LinkOrLength(info);
				var mtime = Mtime(info);
				state.Entries.TryGetValue(path, out var known);
				var executable = TracksExecutable ? IsExecutable(info) : known?.Leaf.Executable ?? false;
				if (known is not null && known.Mtime != 0 && known.Size == size && known.Mtime == mtime && known.Leaf.Executable == executable
					&& (known.Leaf.Kind == EntryKind.Symlink) == (info.LinkTarget is not null))
				{
					unchanged++;
					continue;
				}

				if (!context.Contains(path))
				{
					// A change outside the context: refused below, before anything is hashed or stored.
					outside.Add(path);
					continue;
				}

				hashed++;
				Leaf leaf;
				ObjectId diskId;
				if (info.LinkTarget is { } target)
				{
					var bytes = Encoding.UTF8.GetBytes(target);
					diskId = ObjectId.Of(bytes);
					leaf = store ? await repo.Objects.WriteSymlinkAsync(target, ct) : new Leaf(EntryKind.Symlink, diskId, bytes.Length);
				}
				else
				{
					var bytes = await File.ReadAllBytesAsync(info.FullName, ct);
					var identified = repo.Objects.Identify(bytes, executable);
					diskId = identified.Id;
					if (known is not null && known.DiskId == diskId && known.Leaf.Executable == executable)
					{
						// Touched but not changed (or a conflict left as materialized): only the stat is new.
						state.Entries[path] = known with { Size = size, Mtime = mtime, Placeholder = false };
						unchanged++;
						continue;
					}

					leaf = store ? await repo.Objects.WriteContentAsync(bytes, executable, ct) : identified;
				}

				if (known is null || !Trees.SameContent(known.Leaf, leaf))
				{
					changes.Add(new PathChange(path, leaf));
				}

				state.Entries[path] = new TreeStateEntry(leaf, size, mtime, diskId);
			}
		}

		if (collisions.Count > 0)
		{
			throw new VersionException($"Paths that differ only by case cannot be snapshotted (Windows cannot hold both): {string.Join("; ", collisions)}.");
		}

		outside.AddRange(state.Entries.Keys.Where(path => !seen.Contains(path) && !context.Contains(path)));
		if (outside.Count > 0)
		{
			outside.Sort(StringComparer.Ordinal);
			throw new ContextBoundaryException(outside, $"{outside.Count} path(s) outside the workspace's context-spec '{context}' were created, changed or deleted: " +
				$"{ContextBoundaryException.List(outside)}. Nothing was snapshotted. Widen the context ('bassia workspace context -add <rule>') or undo those changes.");
		}

		foreach (var path in state.Entries.Keys.Where(path => !seen.Contains(path)).ToList())
		{
			changes.Add(new PathChange(path, null));
			state.Entries.Remove(path);
		}

		return new ScanResult(changes, hashed, unchanged);
	}

	private static IEnumerable<string> DirectoriesOf(string path)
	{
		for (var dir = PathNames.Parent(path); dir.Length > 0; dir = PathNames.Parent(dir))
		{
			yield return dir;
		}
	}

	private static long LinkOrLength(FileInfo info) => info.LinkTarget is { } target ? Encoding.UTF8.GetByteCount(target) : info.Length;

	/// <summary>
	/// The modification time to remember, or 0 for a file written in the last two seconds: a later write within the
	/// clock's granularity could keep both size and time, so such a file is hashed again next time (git's racy check).
	/// </summary>
	private static long Mtime(FileInfo info)
	{
		if (info.LinkTarget is not null)
		{
			return 0;
		}

		var time = info.LastWriteTimeUtc;
		return DateTime.UtcNow - time < TimeSpan.FromSeconds(2) ? 0 : time.Ticks;
	}

	private static bool IsExecutable(FileInfo info) =>
		!OperatingSystem.IsWindows() && info.LinkTarget is null && (info.UnixFileMode & UnixFileMode.UserExecute) != 0;

	private static void SetExecutable(string full, bool executable)
	{
		if (OperatingSystem.IsWindows() || new FileInfo(full).LinkTarget is not null)
		{
			return;
		}

		var mode = File.GetUnixFileMode(full);
		var wanted = executable
			? mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
			: mode & ~(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
		if (wanted != mode)
		{
			File.SetUnixFileMode(full, wanted);
		}
	}
}

/// <summary>
/// Hydrates placeholders: what a virtual file system (FUSE, ProjFS) calls before a file is first read or changed.
/// A placeholder is hydrated only while it is still untouched - its size and modification time those its tree state
/// recorded - and keeps both, so a snapshot still sees it unchanged.
/// </summary>
public static class Placeholders
{
	/// <summary>
	/// Writes the content of the placeholder at <paramref name="path"/> (under <paramref name="root"/>) in place, if it
	/// is one and still untouched; true when it wrote it.
	/// </summary>
	public static async Task<bool> HydrateAsync(Repository repo, string root, string path, TreeStateEntry entry, CancellationToken ct = default)
	{
		if (!entry.Placeholder || entry.Leaf.Kind is not (EntryKind.File or EntryKind.Chunked))
		{
			return false;
		}

		var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
		var info = new FileInfo(full);
		if (!info.Exists || info.LinkTarget is not null || info.Length != entry.Size || info.LastWriteTimeUtc.Ticks != entry.Mtime)
		{
			return false;
		}

		var temp = full + CheckoutProvider.HydratingSuffix;
		await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			await repo.Objects.CopyContentAsync(entry.Leaf, stream, ct);
		}

		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(temp, File.GetUnixFileMode(full));
		}

		File.SetLastWriteTimeUtc(temp, new DateTime(entry.Mtime, DateTimeKind.Utc));
		File.Move(temp, full, true);
		return true;
	}
}
