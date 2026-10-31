namespace Bassia.Platform;

using Bassia.Core.Model;
using Bassia.Core.Version;
using Bassia.Core.Workspaces;

/// <summary>Whether a workspace folder gets its files cloned, and why (not).</summary>
internal sealed record CloneDecision(bool Cloned, string Reason);

/// <summary>
/// One copy of each file's content (by content id) in <c>.bassia/clones</c>, read-only, from which workspaces on the
/// same block-cloning volume (a Windows Dev Drive) get their files as copy-on-write clones instead of byte-by-byte
/// writes. An entry is written from the object store the first time a workspace needs it; every later workspace that
/// holds the same content costs only a metadata update. Editing a cloned file in a workspace reallocates the clusters
/// written and leaves the cache and other workspaces untouched.
/// </summary>
internal sealed class CloneCache(string directory, Repository repo, IFileCloner cloner)
{
	/// <summary>Smaller files are written directly: cloning one is no cheaper than writing a cluster or two.</summary>
	public const long MinimumSize = 8 * 1024;

	private int cloned;
	private int copied;
	private int filled;

	public string Directory { get; } = Path.GetFullPath(directory);

	public IFileCloner Cloner => cloner;

	public int Cloned => cloned;

	public int Copied => copied;

	public int Filled => filled;

	public string EntryPath(ObjectId id) => Path.Combine(Directory, id.Hex[..2], id.Hex[2..]);

	/// <summary>Whether workspaces in <paramref name="workspaceRoot"/> get cloned files, given the setting (auto or off).</summary>
	public CloneDecision Decide(string workspaceRoot, string setting)
	{
		if (setting.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			return new(false, "workspace.clone is off");
		}

		// The cache's parent (the monorepo's .bassia folder) exists; the cache itself is made on first use.
		var parent = Path.GetDirectoryName(Directory)!;
		if (!cloner.CanClone(parent))
		{
			var volume = cloner.Describe(parent);
			return new(false, volume.Error ?? $"the monorepo folder's volume ({volume.Volume ?? "?"}, {volume.FileSystem ?? "unknown file system"}) does not support block cloning; " +
				"put the monorepo folder on a Dev Drive (Windows 11 24H2 or later) or another ReFS volume");
		}

		if (!cloner.SameVolume(parent, workspaceRoot))
		{
			return new(false, "the workspace folder is on another volume than the monorepo folder; clones never cross volumes");
		}

		return new(true, "the workspace folder and the clone cache share a block-cloning volume");
	}

	/// <summary>The materializer for a workspace folder, or null when its files are to be written.</summary>
	public IFileMaterializer? For(string workspaceRoot, string setting) =>
		Decide(workspaceRoot, setting).Cloned ? new Materializer(this) : null;

	private sealed class Materializer(CloneCache cache) : IFileMaterializer
	{
		public Task<bool> TryWriteAsync(Leaf leaf, string destination, CancellationToken ct = default) => cache.TryWriteAsync(leaf, destination, ct);
	}

	public async Task<bool> TryWriteAsync(Leaf leaf, string destination, CancellationToken ct = default)
	{
		if (!leaf.IsFile || leaf.Size < MinimumSize)
		{
			return false;
		}

		var entry = await EnsureAsync(leaf, ct);
		try
		{
			cloner.Clone(entry, destination);
			Interlocked.Increment(ref cloned);
		}
		catch (IOException)
		{
			// Too many clones of a cluster, or a volume that refuses: a plain copy (which Windows 11 24H2 itself clones on a
			// Dev Drive when it can) still spares fetching the content from the store again.
			if (File.Exists(destination))
			{
				File.Delete(destination);
			}

			File.Copy(entry, destination);
			File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
			Interlocked.Increment(ref copied);
		}

		return true;
	}

	/// <summary>The cache entry holding the leaf's content, written from the store if it is not there yet.</summary>
	public async Task<string> EnsureAsync(Leaf leaf, CancellationToken ct = default)
	{
		var entry = EntryPath(leaf.Id);
		if (File.Exists(entry))
		{
			return entry;
		}

		System.IO.Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
		var temp = entry + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
		await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
		{
			await repo.Objects.CopyContentAsync(leaf, stream, ct);
		}

		File.SetAttributes(temp, FileAttributes.ReadOnly);
		try
		{
			File.Move(temp, entry, overwrite: false);
			Interlocked.Increment(ref filled);
		}
		catch (IOException) when (File.Exists(entry))
		{
			// Another process filled the same content first.
			Delete(temp);
		}

		return entry;
	}

	/// <summary>How many entries the cache holds and their size.</summary>
	public (int Files, long Bytes) Size()
	{
		if (!System.IO.Directory.Exists(Directory))
		{
			return (0, 0);
		}

		var files = new DirectoryInfo(Directory).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
		return (files.Count, files.Sum(file => file.Length));
	}

	/// <summary>Deletes every entry whose content id is not in <paramref name="keep"/> (and leftover temporary files).</summary>
	public (int Files, long Bytes) Prune(IReadOnlySet<ObjectId> keep)
	{
		if (!System.IO.Directory.Exists(Directory))
		{
			return (0, 0);
		}

		var removed = 0;
		long bytes = 0;
		foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("*", SearchOption.AllDirectories).ToList())
		{
			var prefix = file.Directory!.Name;
			var keepIt = ObjectId.TryParse(prefix + file.Name, out var id) && keep.Contains(id);
			if (!keepIt)
			{
				bytes += file.Length;
				Delete(file.FullName);
				removed++;
			}
		}

		return (removed, bytes);
	}

	private static void Delete(string path)
	{
		File.SetAttributes(path, FileAttributes.Normal);
		File.Delete(path);
	}
}
