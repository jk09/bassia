namespace Bassia.Platform;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>What a folder's volume offers: its file system, whether it is a Dev Drive (and trusted), block cloning.</summary>
internal sealed record VolumeInfo(
	string Path,
	string? Volume,
	string? FileSystem,
	bool BlockCloning,
	bool? DevDrive,
	bool? Trusted,
	long? ClusterSize,
	string? Error);

/// <summary>
/// Copy-on-write file clones. On Windows a ReFS volume that reports <c>FILE_SUPPORTS_BLOCK_REFCOUNTING</c> - a Dev Drive
/// on Windows 11 24H2 / Windows Server 2025 and later - clones a file by remapping its clusters
/// (<c>FSCTL_DUPLICATE_EXTENTS_TO_FILE</c>): no data is read or written, and a later write to either file reallocates
/// only the clusters it touches.
/// </summary>
internal interface IFileCloner
{
	/// <summary>Whether files in <paramref name="directory"/> can be cloned at all.</summary>
	bool CanClone(string directory);

	/// <summary>Whether two existing paths are on the same volume (clones never cross volumes).</summary>
	bool SameVolume(string a, string b);

	/// <summary>Creates <paramref name="destination"/> as a clone of <paramref name="source"/>; throws <see cref="IOException"/> when it cannot.</summary>
	void Clone(string source, string destination);

	VolumeInfo Describe(string path);
}

internal static class FileCloners
{
	/// <summary>The cloner of this platform: block cloning on Windows, none elsewhere.</summary>
	public static IFileCloner Platform() => OperatingSystem.IsWindows() ? new WindowsBlockCloner() : new NoCloner();
}

/// <summary>No cloning: every file is written.</summary>
internal sealed class NoCloner : IFileCloner
{
	public bool CanClone(string directory) => false;

	public bool SameVolume(string a, string b) => false;

	public void Clone(string source, string destination) => throw new IOException("File cloning is not available on this platform.");

	public VolumeInfo Describe(string path) =>
		new(path, null, null, false, null, null, null, "Block cloning (Dev Drive, ReFS) is used on Windows only; workspaces here are written file by file.");
}

/// <summary>Block cloning on Windows ReFS volumes (Dev Drive).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBlockCloner : IFileCloner
{
	private const uint FileSupportsBlockRefcounting = 0x08000000;
	private const uint FsctlDuplicateExtentsToFile = 0x00098344;
	private const uint FsctlQueryPersistentVolumeState = 0x0009023C;
	private const uint PersistentVolumeStateDevVolume = 0x00002000;
	private const uint PersistentVolumeStateTrustedVolume = 0x00004000;

	/// <summary>The largest range one clone call covers (a multiple of every cluster size).</summary>
	private const long MaxRange = 1L << 30;

	private readonly Dictionary<string, VolumeInfo> volumes = new(StringComparer.OrdinalIgnoreCase);

	public bool CanClone(string directory) => Volume(directory) is { BlockCloning: true };

	public bool SameVolume(string a, string b) =>
		VolumeRoot(a) is { } first && VolumeRoot(b) is { } second && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

	public VolumeInfo Describe(string path) => Query(path);

	private VolumeInfo? Volume(string path)
	{
		var root = VolumeRoot(path);
		if (root is null)
		{
			return null;
		}

		lock (volumes)
		{
			if (!volumes.TryGetValue(root, out var info))
			{
				volumes[root] = info = Query(path);
			}

			return info;
		}
	}

	public void Clone(string source, string destination)
	{
		var cluster = Volume(source)?.ClusterSize ?? throw new IOException($"Cannot read the cluster size of the volume of '{source}'.");
		using var from = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
		var size = RandomAccess.GetLength(from);
		using var to = File.OpenHandle(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
		try
		{
			// The destination needs its final size before clusters are mapped into it; ranges are cluster aligned, and the
			// last one may run past the end of the file up to its cluster boundary.
			RandomAccess.SetLength(to, size);
			for (long offset = 0; offset < size; offset += MaxRange)
			{
				var remaining = size - offset;
				var count = Math.Min(MaxRange, (remaining + cluster - 1) / cluster * cluster);
				var data = new DuplicateExtentsData
				{
					FileHandle = from.DangerousGetHandle(),
					SourceFileOffset = offset,
					TargetFileOffset = offset,
					ByteCount = count
				};
				if (!DeviceIoControl(to, FsctlDuplicateExtentsToFile, ref data, Marshal.SizeOf<DuplicateExtentsData>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
				{
					throw new IOException($"Block cloning '{source}' failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
				}
			}
		}
		catch
		{
			to.Dispose();
			File.Delete(destination);
			throw;
		}
	}

	private static string? VolumeRoot(string path)
	{
		var buffer = new char[1024];
		return GetVolumePathNameW(Path.GetFullPath(path), buffer, (uint)buffer.Length) ? new string(buffer).TrimEnd('\0') : null;
	}

	private static VolumeInfo Query(string path)
	{
		var root = VolumeRoot(path);
		if (root is null)
		{
			return new VolumeInfo(path, null, null, false, null, null, null, $"Cannot find the volume of '{path}': {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
		}

		var fileSystem = new StringBuilder(64);
		if (!GetVolumeInformationW(root, null, 0, out _, out _, out var flags, fileSystem, fileSystem.Capacity))
		{
			return new VolumeInfo(path, root, null, false, null, null, null, $"Cannot read the volume '{root}': {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
		}

		long? cluster = GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _) ? (long)sectorsPerCluster * bytesPerSector : null;
		var (devDrive, trusted) = PersistentState(root);
		return new VolumeInfo(path, root, fileSystem.ToString(), (flags & FileSupportsBlockRefcounting) != 0 && cluster is not null, devDrive, trusted, cluster, null);
	}

	/// <summary>Whether the volume was formatted as a Dev Drive, and trusted on this machine; null when Windows will not say.</summary>
	private static (bool? DevDrive, bool? Trusted) PersistentState(string root)
	{
		var name = new char[64];
		if (!GetVolumeNameForVolumeMountPointW(root, name, (uint)name.Length))
		{
			return (null, null);
		}

		// \\?\Volume{guid}\ names the volume's root directory; without the trailing slash it names the volume itself.
		var device = new string(name).TrimEnd('\0').TrimEnd('\\');
		using var handle = CreateFileW(device, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			return (null, null);
		}

		var query = new PersistentVolumeInformation
		{
			FlagMask = PersistentVolumeStateDevVolume | PersistentVolumeStateTrustedVolume,
			Version = 1
		};
		var size = Marshal.SizeOf<PersistentVolumeInformation>();
		if (!DeviceIoControl(handle, FsctlQueryPersistentVolumeState, ref query, size, out var result, size, out _, IntPtr.Zero))
		{
			// Volumes formatted before Dev Drive existed, or other file systems, do not answer: not a Dev Drive.
			return (false, null);
		}

		return ((result.VolumeFlags & PersistentVolumeStateDevVolume) != 0, (result.VolumeFlags & PersistentVolumeStateTrustedVolume) != 0);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DuplicateExtentsData
	{
		public IntPtr FileHandle;
		public long SourceFileOffset;
		public long TargetFileOffset;
		public long ByteCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PersistentVolumeInformation
	{
		public uint VolumeFlags;
		public uint FlagMask;
		public uint Version;
		public uint Reserved;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetVolumePathNameW(string fileName, [Out] char[] volumePathName, uint bufferLength);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint, [Out] char[] volumeName, uint bufferLength);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetVolumeInformationW(string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber,
		out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int fileSystemNameSize);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetDiskFreeSpaceW(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector, out uint numberOfFreeClusters,
		out uint totalNumberOfClusters);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition,
		uint flagsAndAttributes, IntPtr templateFile);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref DuplicateExtentsData inBuffer, int inBufferSize, IntPtr outBuffer,
		int outBufferSize, out int bytesReturned, IntPtr overlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref PersistentVolumeInformation inBuffer, int inBufferSize,
		out PersistentVolumeInformation outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
