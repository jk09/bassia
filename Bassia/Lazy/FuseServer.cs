namespace Bassia.Lazy;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// A FUSE file system (libfuse 3, high-level API) that projects a lazy workspace's backing directory at its folder: a
/// passthrough to the backing directory, except that a placeholder is hydrated - its content fetched and written - just
/// before it is first opened or changed (opened, truncated, renamed, linked, its mode or times set). Listings and
/// attributes come from the backing directory itself (<c>lstat</c> straight into FUSE's buffer), so nothing is read to
/// list a folder. One server per process: the callbacks are static and reach it through <see cref="current"/>.
/// </summary>
internal sealed unsafe class FuseServer
{
	private static FuseServer? current;

	private readonly string backing;
	private readonly Func<string, bool> hydrate;
	private readonly Action<string>? log;
	private IntPtr fuse;

	/// <param name="backing">The backing directory.</param>
	/// <param name="hydrate">Hydrates the placeholder at a path relative to the backing directory, if it is one; true when it wrote it.</param>
	public FuseServer(string backing, Func<string, bool> hydrate, Action<string>? log = null)
	{
		this.backing = Path.GetFullPath(backing).TrimEnd('/');
		this.hydrate = hydrate;
		this.log = log;
	}

	/// <summary>How many placeholders this server hydrated.</summary>
	public int Hydrated { get; private set; }

	/// <summary>Why FUSE cannot be used here, or null when it can.</summary>
	public static string? Unavailable()
	{
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
		{
			return "FUSE serves lazy folders on Linux and macOS only";
		}

		if (OperatingSystem.IsLinux() && !File.Exists("/dev/fuse"))
		{
			return "/dev/fuse is missing (load the fuse kernel module, or give the container the device)";
		}

		if (!Native.TryLoad())
		{
			return OperatingSystem.IsLinux() ? "libfuse 3 is not installed (apt install fuse3, dnf install fuse3)" : "macFUSE (with libfuse 3) is not installed";
		}

		return null;
	}

	/// <summary>Mounts at <paramref name="mountpoint"/> and serves until it is unmounted (fusermount3 -u) or the process is signalled.</summary>
	public int Run(string mountpoint)
	{
		if (Interlocked.CompareExchange(ref current, this, null) is not null)
		{
			throw new InvalidOperationException("A FUSE server already runs in this process.");
		}

		try
		{
			var ops = Operations();
			string[] argv = ["bassia", "-o", "default_permissions,attr_timeout=0,entry_timeout=0,negative_timeout=0,fsname=bassia,subtype=bassia"];
			var pointers = argv.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
			try
			{
				fixed (IntPtr* argvPtr = pointers)
				{
					var args = new Native.FuseArgs { Argc = argv.Length, Argv = argvPtr, Allocated = 0 };
					fuse = Native.fuse_new(&args, &ops, (nuint)sizeof(Native.FuseOperations), IntPtr.Zero);
				}
			}
			finally
			{
				foreach (var pointer in pointers)
				{
					Marshal.FreeCoTaskMem(pointer);
				}
			}

			if (fuse == IntPtr.Zero)
			{
				throw new IOException("fuse_new failed.");
			}

			if (Native.fuse_mount(fuse, mountpoint) != 0)
			{
				Native.fuse_destroy(fuse);
				throw new IOException($"Could not mount FUSE at '{mountpoint}' (is it in use, or is fusermount3 missing?).");
			}

			var session = Native.fuse_get_session(fuse);
			Native.fuse_set_signal_handlers(session);
			log?.Invoke($"serving '{backing}' at '{mountpoint}'");
			var result = Native.fuse_loop(fuse);
			Native.fuse_remove_signal_handlers(session);
			Native.fuse_unmount(fuse);
			Native.fuse_destroy(fuse);
			log?.Invoke($"unmounted '{mountpoint}' ({Hydrated} placeholder(s) hydrated)");
			return result;
		}
		finally
		{
			current = null;
		}
	}

	private static Native.FuseOperations Operations() => new()
	{
		Getattr = (IntPtr)(delegate* unmanaged<byte*, IntPtr, IntPtr, int>)&Getattr,
		Readlink = (IntPtr)(delegate* unmanaged<byte*, byte*, nuint, int>)&Readlink,
		Mknod = (IntPtr)(delegate* unmanaged<byte*, uint, ulong, int>)&Mknod,
		Mkdir = (IntPtr)(delegate* unmanaged<byte*, uint, int>)&Mkdir,
		Unlink = (IntPtr)(delegate* unmanaged<byte*, int>)&Unlink,
		Rmdir = (IntPtr)(delegate* unmanaged<byte*, int>)&Rmdir,
		Symlink = (IntPtr)(delegate* unmanaged<byte*, byte*, int>)&Symlink,
		Rename = (IntPtr)(delegate* unmanaged<byte*, byte*, uint, int>)&Rename,
		Link = (IntPtr)(delegate* unmanaged<byte*, byte*, int>)&Link,
		Chmod = (IntPtr)(delegate* unmanaged<byte*, uint, IntPtr, int>)&Chmod,
		Chown = (IntPtr)(delegate* unmanaged<byte*, uint, uint, IntPtr, int>)&Chown,
		Truncate = (IntPtr)(delegate* unmanaged<byte*, long, IntPtr, int>)&Truncate,
		Open = (IntPtr)(delegate* unmanaged<byte*, IntPtr, int>)&Open,
		Read = (IntPtr)(delegate* unmanaged<byte*, byte*, nuint, long, IntPtr, int>)&Read,
		Write = (IntPtr)(delegate* unmanaged<byte*, byte*, nuint, long, IntPtr, int>)&Write,
		Statfs = (IntPtr)(delegate* unmanaged<byte*, IntPtr, int>)&Statfs,
		Flush = (IntPtr)(delegate* unmanaged<byte*, IntPtr, int>)&Flush,
		Release = (IntPtr)(delegate* unmanaged<byte*, IntPtr, int>)&Release,
		Fsync = (IntPtr)(delegate* unmanaged<byte*, int, IntPtr, int>)&Fsync,
		Readdir = (IntPtr)(delegate* unmanaged<byte*, IntPtr, IntPtr, long, IntPtr, int, int>)&Readdir,
		Access = (IntPtr)(delegate* unmanaged<byte*, int, int>)&Access,
		Create = (IntPtr)(delegate* unmanaged<byte*, uint, IntPtr, int>)&Create,
		Utimens = (IntPtr)(delegate* unmanaged<byte*, IntPtr, IntPtr, int>)&Utimens
	};

	// ----- callbacks: each returns 0 or -errno, and never lets an exception reach native code -----

	private const int EIO = 5;
	private const int EINVAL = 22;
	private const int EEXIST = 17;

	private static string Relative(byte* path) => (Marshal.PtrToStringUTF8((IntPtr)path) ?? "/").TrimStart('/');

	private string Full(string relative) => relative.Length == 0 ? backing : backing + "/" + relative;

	private static int Guard(Func<FuseServer, int> body)
	{
		try
		{
			return current is { } server ? body(server) : -EIO;
		}
		catch (Exception ex)
		{
			current?.log?.Invoke($"error: {ex.GetType().Name}: {ex.Message}");
			return -EIO;
		}
	}

	private static int Result(int value) => value < 0 ? -Marshal.GetLastPInvokeError() : value;

	/// <summary>Hydrates a placeholder before it is opened or changed; a failure to fetch is an I/O error.</summary>
	private void Hydrate(string relative)
	{
		if (relative.Length > 0 && hydrate(relative))
		{
			Hydrated++;
		}
	}

	[UnmanagedCallersOnly]
	private static int Getattr(byte* path, IntPtr stat, IntPtr fi) => Guard(server => Result(Libc.lstat(server.Full(Relative(path)), stat)));

	[UnmanagedCallersOnly]
	private static int Readlink(byte* path, byte* buffer, nuint size) => Guard(server =>
	{
		var length = Libc.readlink(server.Full(Relative(path)), buffer, size - 1);
		if (length < 0)
		{
			return -Marshal.GetLastPInvokeError();
		}

		buffer[length] = 0;
		return 0;
	});

	[UnmanagedCallersOnly]
	private static int Mknod(byte* path, uint mode, ulong device) => Guard(server => Result(Libc.mknod(server.Full(Relative(path)), mode, device)));

	[UnmanagedCallersOnly]
	private static int Mkdir(byte* path, uint mode) => Guard(server => Result(Libc.mkdir(server.Full(Relative(path)), mode)));

	[UnmanagedCallersOnly]
	private static int Unlink(byte* path) => Guard(server => Result(Libc.unlink(server.Full(Relative(path)))));

	[UnmanagedCallersOnly]
	private static int Rmdir(byte* path) => Guard(server => Result(Libc.rmdir(server.Full(Relative(path)))));

	[UnmanagedCallersOnly]
	private static int Symlink(byte* target, byte* path) => Guard(server =>
		Result(Libc.symlink(Marshal.PtrToStringUTF8((IntPtr)target)!, server.Full(Relative(path)))));

	[UnmanagedCallersOnly]
	private static int Rename(byte* from, byte* to, uint flags) => Guard(server =>
	{
		const uint noReplace = 1;
		var source = Relative(from);
		var target = server.Full(Relative(to));
		if ((flags & ~noReplace) != 0)
		{
			return -EINVAL;
		}

		if ((flags & noReplace) != 0 && Path.Exists(target))
		{
			return -EEXIST;
		}

		// A placeholder keeps its content when it moves: hydrate it first (a directory's placeholders hydrate when used).
		server.Hydrate(source);
		return Result(Libc.rename(server.Full(source), target));
	});

	[UnmanagedCallersOnly]
	private static int Link(byte* from, byte* to) => Guard(server =>
	{
		var source = Relative(from);
		server.Hydrate(source);
		return Result(Libc.link(server.Full(source), server.Full(Relative(to))));
	});

	[UnmanagedCallersOnly]
	private static int Chmod(byte* path, uint mode, IntPtr fi) => Guard(server =>
	{
		var relative = Relative(path);
		server.Hydrate(relative);
		return Result(Libc.chmod(server.Full(relative), mode));
	});

	[UnmanagedCallersOnly]
	private static int Chown(byte* path, uint uid, uint gid, IntPtr fi) => Guard(server => Result(Libc.lchown(server.Full(Relative(path)), uid, gid)));

	[UnmanagedCallersOnly]
	private static int Truncate(byte* path, long size, IntPtr fi) => Guard(server =>
	{
		var relative = Relative(path);
		server.Hydrate(relative);
		return Result(Libc.truncate(server.Full(relative), size));
	});

	[UnmanagedCallersOnly]
	private static int Open(byte* path, IntPtr fi) => Guard(server =>
	{
		var relative = Relative(path);
		server.Hydrate(relative);
		var fd = Libc.open(server.Full(relative), Native.Flags(fi), 0);
		if (fd < 0)
		{
			return -Marshal.GetLastPInvokeError();
		}

		Native.SetHandle(fi, (ulong)fd);
		return 0;
	});

	[UnmanagedCallersOnly]
	private static int Create(byte* path, uint mode, IntPtr fi) => Guard(server =>
	{
		var fd = Libc.open(server.Full(Relative(path)), Native.Flags(fi) | Libc.OCreat, mode);
		if (fd < 0)
		{
			return -Marshal.GetLastPInvokeError();
		}

		Native.SetHandle(fi, (ulong)fd);
		return 0;
	});

	[UnmanagedCallersOnly]
	private static int Read(byte* path, byte* buffer, nuint size, long offset, IntPtr fi) => Guard(_ =>
		(int)Math.Clamp(ResultLong(Libc.pread((int)Native.Handle(fi), buffer, size, offset)), int.MinValue, int.MaxValue));

	[UnmanagedCallersOnly]
	private static int Write(byte* path, byte* buffer, nuint size, long offset, IntPtr fi) => Guard(_ =>
		(int)Math.Clamp(ResultLong(Libc.pwrite((int)Native.Handle(fi), buffer, size, offset)), int.MinValue, int.MaxValue));

	private static long ResultLong(nint value) => value < 0 ? -Marshal.GetLastPInvokeError() : value;

	[UnmanagedCallersOnly]
	private static int Statfs(byte* path, IntPtr buffer) => Guard(server => Result(Libc.statvfs(server.backing, buffer)));

	[UnmanagedCallersOnly]
	private static int Flush(byte* path, IntPtr fi) => 0;

	[UnmanagedCallersOnly]
	private static int Release(byte* path, IntPtr fi) => Guard(_ => Result(Libc.close((int)Native.Handle(fi))));

	[UnmanagedCallersOnly]
	private static int Fsync(byte* path, int dataOnly, IntPtr fi) => Guard(_ => Result(Libc.fsync((int)Native.Handle(fi))));

	[UnmanagedCallersOnly]
	private static int Readdir(byte* path, IntPtr buffer, IntPtr filler, long offset, IntPtr fi, int flags) => Guard(server =>
	{
		var full = server.Full(Relative(path));
		if (!Directory.Exists(full))
		{
			return -2;
		}

		var fill = (delegate* unmanaged<IntPtr, byte*, IntPtr, long, int, int>)filler;
		foreach (var name in new[] { ".", ".." }.Concat(Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName)))
		{
			if (name is null || name.EndsWith(Core.Workspaces.CheckoutProvider.HydratingSuffix, StringComparison.Ordinal))
			{
				continue;
			}

			var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
			fixed (byte* text = bytes)
			{
				if (fill(buffer, text, IntPtr.Zero, 0, 0) != 0)
				{
					break;
				}
			}
		}

		return 0;
	});

	[UnmanagedCallersOnly]
	private static int Access(byte* path, int mask) => Guard(server => Result(Libc.access(server.Full(Relative(path)), mask)));

	[UnmanagedCallersOnly]
	private static int Utimens(byte* path, IntPtr times, IntPtr fi) => Guard(server =>
	{
		var relative = Relative(path);
		server.Hydrate(relative);
		return Result(Libc.utimensat(Libc.AtFdcwd, server.Full(relative), times, Libc.AtSymlinkNofollow));
	});

	/// <summary>libfuse 3's entry points and structures.</summary>
	private static class Native
	{
		private const string Library = "fuse3";

		static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);

		private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
		{
			if (name != Library)
			{
				return IntPtr.Zero;
			}

			foreach (var candidate in Candidates)
			{
				if (NativeLibrary.TryLoad(candidate, out var handle))
				{
					return handle;
				}
			}

			return IntPtr.Zero;
		}

		private static readonly string[] Candidates = OperatingSystem.IsMacOS()
			? ["/usr/local/lib/libfuse3.dylib", "/usr/local/lib/libfuse3.4.dylib", "libfuse3.dylib"]
			: ["libfuse3.so.3", "libfuse3.so"];

		public static bool TryLoad() => Candidates.Any(candidate => NativeLibrary.TryLoad(candidate, out _));

		[StructLayout(LayoutKind.Sequential)]
		public struct FuseArgs
		{
			public int Argc;
			public IntPtr* Argv;
			public int Allocated;
		}

		/// <summary><c>struct fuse_operations</c> of libfuse 3, in order, up to <c>lseek</c>.</summary>
		[StructLayout(LayoutKind.Sequential)]
		public struct FuseOperations
		{
			public IntPtr Getattr, Readlink, Mknod, Mkdir, Unlink, Rmdir, Symlink, Rename, Link, Chmod, Chown, Truncate, Open, Read, Write,
				Statfs, Flush, Release, Fsync, Setxattr, Getxattr, Listxattr, Removexattr, Opendir, Readdir, Releasedir, Fsyncdir, Init,
				Destroy, Access, Create, Lock, Utimens, Bmap, Ioctl, Poll, WriteBuf, ReadBuf, Flock, Fallocate, CopyFileRange, Lseek;
		}

		/// <summary><c>fuse_file_info.flags</c>: the first field.</summary>
		public static int Flags(IntPtr fi) => *(int*)fi;

		/// <summary><c>fuse_file_info.fh</c>: after flags, the bit fields and padding, at offset 16.</summary>
		public static ulong Handle(IntPtr fi) => *(ulong*)((byte*)fi + 16);

		public static void SetHandle(IntPtr fi, ulong handle) => *(ulong*)((byte*)fi + 16) = handle;

		[DllImport(Library)]
		public static extern IntPtr fuse_new(FuseArgs* args, FuseOperations* ops, nuint size, IntPtr userData);

		[DllImport(Library)]
		public static extern int fuse_mount(IntPtr fuse, [MarshalAs(UnmanagedType.LPUTF8Str)] string mountpoint);

		[DllImport(Library)]
		public static extern int fuse_loop(IntPtr fuse);

		[DllImport(Library)]
		public static extern void fuse_unmount(IntPtr fuse);

		[DllImport(Library)]
		public static extern void fuse_destroy(IntPtr fuse);

		[DllImport(Library)]
		public static extern IntPtr fuse_get_session(IntPtr fuse);

		[DllImport(Library)]
		public static extern int fuse_set_signal_handlers(IntPtr session);

		[DllImport(Library)]
		public static extern void fuse_remove_signal_handlers(IntPtr session);
	}

	/// <summary>The C library calls the passthrough makes, on paths in the backing directory.</summary>
	private static class Libc
	{
		private const string C = "libc";

		public static int OCreat => OperatingSystem.IsMacOS() ? 0x200 : 0x40;

		public static int AtFdcwd => OperatingSystem.IsMacOS() ? -2 : -100;

		public static int AtSymlinkNofollow => OperatingSystem.IsMacOS() ? 0x20 : 0x100;

		[DllImport(C, SetLastError = true)] public static extern int lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr stat);
		[DllImport(C, SetLastError = true)] public static extern nint readlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer, nuint size);
		[DllImport(C, SetLastError = true)] public static extern int mknod([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode, ulong device);
		[DllImport(C, SetLastError = true)] public static extern int mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
		[DllImport(C, SetLastError = true)] public static extern int unlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
		[DllImport(C, SetLastError = true)] public static extern int rmdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
		[DllImport(C, SetLastError = true)] public static extern int symlink([MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
		[DllImport(C, SetLastError = true)] public static extern int rename([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);
		[DllImport(C, SetLastError = true)] public static extern int link([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);
		[DllImport(C, SetLastError = true)] public static extern int chmod([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
		[DllImport(C, SetLastError = true)] public static extern int lchown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);
		[DllImport(C, SetLastError = true)] public static extern int truncate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, long size);
		[DllImport(C, SetLastError = true)] public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
		[DllImport(C, SetLastError = true)] public static extern int close(int fd);
		[DllImport(C, SetLastError = true)] public static extern nint pread(int fd, byte* buffer, nuint size, long offset);
		[DllImport(C, SetLastError = true)] public static extern nint pwrite(int fd, byte* buffer, nuint size, long offset);
		[DllImport(C, SetLastError = true)] public static extern int fsync(int fd);
		[DllImport(C, SetLastError = true)] public static extern int statvfs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
		[DllImport(C, SetLastError = true)] public static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mask);
		[DllImport(C, SetLastError = true)] public static extern int utimensat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr times, int flags);
	}
}
