namespace Bassia.Lazy;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Windows.ProjFS;

/// <summary>
/// A ProjFS provider (Windows) that projects a lazy workspace's backing directory into its folder, the virtualization
/// root. Listings and placeholder information come from the backing directory (sizes and times as they are there, so a
/// snapshot of the folder sees an untouched placeholder as unchanged); a file's content is hydrated in the backing
/// directory and streamed to ProjFS when the file is first read. Changes stay in the folder, as ProjFS keeps them:
/// snapshots read the folder. When a command changes the backing directory under the folder (a sync, an undo, a
/// context change), it drops a request naming the paths into <see cref="ControlDirectory"/>, and the server deletes
/// their on-disk state so the folder shows the backing directory again.
/// </summary>
internal sealed class ProjFsServer : IRequiredCallbacks
{
	private readonly string backing;
	private readonly string root;
	private readonly string control;
	private readonly Func<string, bool> hydrate;
	private readonly Action<string>? log;
	private readonly ConcurrentDictionary<Guid, Enumeration> enumerations = new();
	private VirtualizationInstance? instance;

	public ProjFsServer(string backing, string root, string control, Func<string, bool> hydrate, Action<string>? log = null)
	{
		this.backing = Path.GetFullPath(backing);
		this.root = Path.GetFullPath(root);
		this.control = control;
		this.hydrate = hydrate;
		this.log = log;
	}

	/// <summary>How many placeholders this server hydrated.</summary>
	public int Hydrated { get; private set; }

	/// <summary>Where a folder's server and the commands meet: its lock (held while it serves), requests and stop file.</summary>
	public static string ControlDirectory(string stateFile) => Path.ChangeExtension(stateFile, ".projfs");

	public static string LockFile(string control) => Path.Combine(control, "serving.lock");

	public static string StopFile(string control) => Path.Combine(control, "stop");

	public static string Requests(string control) => Path.Combine(control, "requests");

	/// <summary>Why ProjFS cannot be used here, or null when it can.</summary>
	public static string? Unavailable()
	{
		if (!OperatingSystem.IsWindows())
		{
			return "ProjFS serves lazy folders on Windows only";
		}

		return NativeLibrary.TryLoad("ProjectedFSLib.dll", out _)
			? null
			: "the Windows Projected File System is not enabled (Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart, as an administrator)";
	}

	/// <summary>Serves until <see cref="StopFile"/> appears or <paramref name="ct"/> is cancelled.</summary>
	public void Run(CancellationToken ct = default)
	{
		Directory.CreateDirectory(Requests(control));
		File.Delete(StopFile(control));
		Directory.CreateDirectory(root);
		var idFile = Path.Combine(control, "instance.id");
		if (!File.Exists(idFile))
		{
			var id = Guid.NewGuid();
			Check(VirtualizationInstance.MarkDirectoryAsVirtualizationRoot(root, id), "mark the folder as a virtualization root");
			File.WriteAllText(idFile, id.ToString());
		}

		instance = new VirtualizationInstance(root, 0, 0, false, []);
		Check(instance.StartVirtualizing(this), "start virtualizing");
		FileStream? serving = null;
		try
		{
			// Held only once the folder is virtualized: holding it is what tells commands the folder is served.
			serving = new FileStream(LockFile(control), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			log?.Invoke($"serving '{backing}' at '{root}'");
			while (!ct.IsCancellationRequested && !File.Exists(StopFile(control)))
			{
				ProcessRequests();
				ct.WaitHandle.WaitOne(100);
			}

			ProcessRequests();
		}
		finally
		{
			instance.StopVirtualizing();
			instance.Dispose();
			instance = null;
			File.Delete(StopFile(control));
			serving?.Dispose();
			log?.Invoke($"stopped serving '{root}' ({Hydrated} placeholder(s) hydrated)");
		}
	}

	private static void Check(HResult result, string what)
	{
		if (result != HResult.Ok)
		{
			throw new IOException($"ProjFS could not {what} ({result}, 0x{(int)result:X8}).");
		}
	}

	/// <summary>Deletes the on-disk state of the paths each request names (deepest first), so the folder shows the backing directory.</summary>
	private void ProcessRequests()
	{
		foreach (var request in Directory.EnumerateFiles(Requests(control), "*.req").Order(StringComparer.Ordinal).ToList())
		{
			var paths = File.ReadAllLines(request).Where(line => line.Length > 0)
				.OrderByDescending(path => path.Count(c => c == '/')).ThenByDescending(path => path.Length);
			foreach (var path in paths)
			{
				var relative = path.Replace('/', '\\');
				var result = instance!.DeleteFile(relative, UpdateType.AllowDirtyMetadata | UpdateType.AllowDirtyData | UpdateType.AllowTombstone | UpdateType.AllowReadOnly,
					out var cause);
				if (result is not (HResult.Ok or HResult.FileNotFound or HResult.PathNotFound))
				{
					log?.Invoke($"refresh {path}: {result} ({cause})");
				}
			}

			instance!.ClearNegativePathCache(out _);
			File.Delete(request);
		}
	}

	private string Backing(string relative) => relative.Length == 0 ? backing : Path.Combine(backing, relative);

	private static bool Hidden(string name) => name.EndsWith(Core.Workspaces.CheckoutProvider.HydratingSuffix, StringComparison.Ordinal);

	// ----- callbacks -----

	public HResult StartDirectoryEnumerationCallback(int commandId, Guid enumerationId, string relativePath, uint triggeringProcessId, string triggeringProcessImageFileName)
	{
		try
		{
			var directory = new DirectoryInfo(Backing(relativePath));
			if (!directory.Exists)
			{
				return HResult.PathNotFound;
			}

			var entries = directory.EnumerateFileSystemInfos().Where(info => !Hidden(info.Name)).ToList();
			entries.Sort((a, b) => Utils.FileNameCompare(a.Name, b.Name));
			enumerations[enumerationId] = new Enumeration(entries);
			return HResult.Ok;
		}
		catch (Exception ex)
		{
			log?.Invoke($"enumerate {relativePath}: {ex.Message}");
			return HResult.InternalError;
		}
	}

	public HResult GetDirectoryEnumerationCallback(int commandId, Guid enumerationId, string filterFileName, bool restartScan, IDirectoryEnumerationResults result)
	{
		if (!enumerations.TryGetValue(enumerationId, out var enumeration))
		{
			return HResult.InternalError;
		}

		if (restartScan || enumeration.Filter is null)
		{
			enumeration.Index = 0;
			enumeration.Filter = string.IsNullOrEmpty(filterFileName) ? "*" : filterFileName;
		}

		while (enumeration.Index < enumeration.Entries.Count)
		{
			var entry = enumeration.Entries[enumeration.Index];
			if (Utils.IsFileNameMatch(entry.Name, enumeration.Filter))
			{
				var isDirectory = entry is DirectoryInfo;
				var added = result.Add(entry.Name, isDirectory ? 0 : ((FileInfo)entry).Length, isDirectory, entry.Attributes,
					entry.CreationTimeUtc, entry.LastAccessTimeUtc, entry.LastWriteTimeUtc, entry.LastWriteTimeUtc);
				if (!added)
				{
					// The buffer is full: this entry comes first next time.
					break;
				}
			}

			enumeration.Index++;
		}

		return HResult.Ok;
	}

	public HResult EndDirectoryEnumerationCallback(Guid enumerationId)
	{
		enumerations.TryRemove(enumerationId, out _);
		return HResult.Ok;
	}

	public HResult GetPlaceholderInfoCallback(int commandId, string relativePath, uint triggeringProcessId, string triggeringProcessImageFileName)
	{
		try
		{
			var full = Backing(relativePath);
			FileSystemInfo? info = File.Exists(full) ? new FileInfo(full) : Directory.Exists(full) ? new DirectoryInfo(full) : null;
			if (info is null || Hidden(info.Name))
			{
				return HResult.FileNotFound;
			}

			// The name as the backing directory spells it (the request may differ in case).
			var parent = Path.GetDirectoryName(relativePath) ?? "";
			var canonical = new DirectoryInfo(Path.GetDirectoryName(full)!).EnumerateFileSystemInfos(info.Name).FirstOrDefault()?.Name ?? info.Name;
			var name = parent.Length == 0 ? canonical : Path.Combine(parent, canonical);
			var isDirectory = info is DirectoryInfo;
			var id = new byte[VirtualizationInstance.PlaceholderIdLength];
			return instance!.WritePlaceholderInfo(name, info.CreationTimeUtc, info.LastAccessTimeUtc, info.LastWriteTimeUtc, info.LastWriteTimeUtc,
				info.Attributes, isDirectory ? 0 : ((FileInfo)info).Length, isDirectory, id, id);
		}
		catch (Exception ex)
		{
			log?.Invoke($"placeholder {relativePath}: {ex.Message}");
			return HResult.InternalError;
		}
	}

	public HResult GetFileDataCallback(int commandId, string relativePath, ulong byteOffset, uint length, Guid dataStreamId, byte[] contentId, byte[] providerId,
		uint triggeringProcessId, string triggeringProcessImageFileName)
	{
		try
		{
			var path = relativePath.Replace('\\', '/');
			if (hydrate(path))
			{
				Hydrated++;
			}

			using var source = new FileStream(Backing(relativePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			source.Position = (long)byteOffset;
			const uint chunk = 1 << 20;
			for (uint written = 0; written < length;)
			{
				var size = Math.Min(chunk, length - written);
				using var buffer = instance!.CreateWriteBuffer(size);
				var bytes = new byte[size];
				var read = 0;
				while (read < size)
				{
					var count = source.Read(bytes, read, (int)size - read);
					if (count == 0)
					{
						break;
					}

					read += count;
				}

				buffer.Stream.Write(bytes, 0, read);
				var result = instance.WriteFileData(dataStreamId, buffer, byteOffset + written, (uint)read);
				if (result != HResult.Ok)
				{
					return result;
				}

				if (read < size)
				{
					break;
				}

				written += (uint)read;
			}

			return HResult.Ok;
		}
		catch (Exception ex)
		{
			log?.Invoke($"data {relativePath}: {ex.Message}");
			return HResult.InternalError;
		}
	}

	private sealed class Enumeration(List<FileSystemInfo> entries)
	{
		public List<FileSystemInfo> Entries { get; } = entries;

		public int Index { get; set; }

		public string? Filter { get; set; }
	}
}

/// <summary>The commands' side of a ProjFS server's control directory: whether it serves, refresh requests, stop.</summary>
internal static class ProjFsControl
{
	/// <summary>Whether a server holds the lock of <paramref name="control"/> now.</summary>
	public static bool IsServing(string control)
	{
		var lockFile = ProjFsServer.LockFile(control);
		if (!File.Exists(lockFile))
		{
			return false;
		}

		try
		{
			using var probe = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			return false;
		}
		catch (IOException)
		{
			return true;
		}
	}

	/// <summary>Asks the server to show these paths of the backing directory again; waits while it serves, else it takes them when it starts.</summary>
	public static async Task RefreshAsync(string control, IReadOnlyList<string> paths, CancellationToken ct = default)
	{
		var requests = ProjFsServer.Requests(control);
		Directory.CreateDirectory(requests);
		var request = Path.Combine(requests, $"{DateTime.UtcNow.Ticks:D20}-{Guid.NewGuid():N}.req");
		await File.WriteAllLinesAsync(request + ".tmp", paths, ct);
		File.Move(request + ".tmp", request);
		for (var waited = 0; File.Exists(request) && IsServing(control); waited++)
		{
			if (waited > 600)
			{
				throw new IOException($"The ProjFS server did not take the changes in 30 s ({control}).");
			}

			await Task.Delay(50, ct);
		}
	}

	/// <summary>Stops the server and waits until it has.</summary>
	public static async Task StopAsync(string control, CancellationToken ct = default)
	{
		if (!IsServing(control))
		{
			return;
		}

		await File.WriteAllTextAsync(ProjFsServer.StopFile(control), "", ct);
		for (var waited = 0; IsServing(control); waited++)
		{
			if (waited > 600)
			{
				throw new IOException($"The ProjFS server did not stop in 30 s ({control}).");
			}

			await Task.Delay(50, ct);
		}
	}
}
