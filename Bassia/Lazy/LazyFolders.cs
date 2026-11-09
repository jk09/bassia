namespace Bassia.Lazy;

using System.Diagnostics;
using Bassia.Agents;
using Bassia.Core.Workspaces;

/// <summary>This machine's virtual file system for lazy workspace folders.</summary>
internal static class LazyFolders
{
	/// <summary>FUSE on Linux and macOS, ProjFS on Windows.</summary>
	public static ILazyFolders Platform(Monorepo monorepo) =>
		OperatingSystem.IsWindows() ? new ProjFsFolders(monorepo) : new FuseFolders(monorepo);

	/// <summary>The log of the process that serves a workspace's folder.</summary>
	public static string LogFile(Monorepo monorepo, string workspace) => Path.Combine(monorepo.BassiaDir, "workspaces", workspace + ".mount.log");

	/// <summary>Starts <c>bassia workspace serve</c> in the background and waits until <paramref name="mounted"/> says the folder is projected.</summary>
	public static async Task StartServerAsync(Monorepo monorepo, LocalWorkspace local, Func<bool> mounted, CancellationToken ct)
	{
		var log = LogFile(monorepo, local.Id);
		Directory.CreateDirectory(Path.GetDirectoryName(log)!);
		using var process = DetachedProcess.Start(monorepo.Root, ["-C", monorepo.Root, "workspace", "serve", local.Id]);
		var clock = Stopwatch.StartNew();
		while (!mounted())
		{
			if (process.HasExited || clock.Elapsed > TimeSpan.FromSeconds(30))
			{
				var tail = File.Exists(log) ? string.Join(" | ", File.ReadLines(log).TakeLast(5)) : "";
				var output = process.HasExited ? (await process.StandardError.ReadToEndAsync(ct)).Trim() : "";
				throw new MonorepoException($"The lazy folder '{local.Root}' could not be served{(process.HasExited ? $" (the server exited with {process.ExitCode})" : " in 30 s")}: " +
					$"{(tail.Length > 0 ? tail : output.Length > 0 ? output : "no output")}.");
			}

			await Task.Delay(50, ct);
		}
	}
}

/// <summary>Lazy folders on Linux and macOS: a FUSE server per folder, in a background <c>bassia workspace serve</c> process.</summary>
internal sealed class FuseFolders(Monorepo monorepo) : ILazyFolders
{
	private string? unavailable;
	private bool checkedAvailability;

	public string? Unavailable
	{
		get
		{
			if (!checkedAvailability)
			{
				unavailable = FuseServer.Unavailable();
				checkedAvailability = true;
			}

			return unavailable;
		}
	}

	public async Task MountAsync(LocalWorkspace local, CancellationToken ct = default)
	{
		if (!IsMounted(local))
		{
			Directory.CreateDirectory(local.Root);
			await LazyFolders.StartServerAsync(monorepo, local, () => IsMounted(local), ct);
		}
	}

	public async Task UnmountAsync(LocalWorkspace local, CancellationToken ct = default)
	{
		if (!IsMounted(local))
		{
			return;
		}

		var (tool, args) = OperatingSystem.IsLinux() ? ("fusermount3", new[] { "-u", "-z", local.Root }) : ("umount", new[] { local.Root });
		var start = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
		foreach (var arg in args)
		{
			start.ArgumentList.Add(arg);
		}

		using (var process = Process.Start(start)!)
		{
			await process.WaitForExitAsync(ct);
		}

		for (var waited = 0; IsMounted(local) && waited < 100; waited++)
		{
			await Task.Delay(50, ct);
		}
	}

	public bool IsMounted(LocalWorkspace local) => MountPoints().Contains(Path.GetFullPath(local.Root).TrimEnd('/'));

	public bool ScansFolder => false;

	public Task RefreshAsync(LocalWorkspace local, IReadOnlyList<string> paths, CancellationToken ct = default) => Task.CompletedTask;

	public async Task RemoveFolderAsync(LocalWorkspace local, CancellationToken ct = default)
	{
		await UnmountAsync(local, ct);
		if (Directory.Exists(local.Root))
		{
			Directory.Delete(local.Root, true);
		}
	}

	/// <summary>The mount points of this machine: /proc/self/mountinfo on Linux, mount(8) on macOS.</summary>
	private static HashSet<string> MountPoints()
	{
		var points = new HashSet<string>(StringComparer.Ordinal);
		if (OperatingSystem.IsLinux())
		{
			foreach (var line in File.ReadLines("/proc/self/mountinfo"))
			{
				var fields = line.Split(' ');
				if (fields.Length > 4)
				{
					points.Add(Unescape(fields[4]));
				}
			}
		}
		else
		{
			var start = new ProcessStartInfo("mount") { RedirectStandardOutput = true, UseShellExecute = false };
			using var process = Process.Start(start)!;
			foreach (var line in process.StandardOutput.ReadToEnd().Split('\n'))
			{
				// "<device> on <mount point> (<options>)"
				var on = line.IndexOf(" on ", StringComparison.Ordinal);
				var options = line.LastIndexOf(" (", StringComparison.Ordinal);
				if (on > 0 && options > on)
				{
					points.Add(line[(on + 4)..options]);
				}
			}
		}

		return points;
	}

	/// <summary>mountinfo escapes space, tab, newline and backslash as octal (\040).</summary>
	private static string Unescape(string field)
	{
		var builder = new System.Text.StringBuilder();
		for (var index = 0; index < field.Length; index++)
		{
			if (field[index] == '\\' && index + 3 < field.Length)
			{
				var octal = field.Substring(index + 1, 3);
				if (octal.All(c => c is >= '0' and <= '7'))
				{
					builder.Append((char)Convert.ToInt32(octal, 8));
					index += 3;
					continue;
				}
			}

			builder.Append(field[index]);
		}

		return builder.ToString();
	}
}

/// <summary>
/// Lazy folders on Windows: a ProjFS provider per folder, in a background <c>bassia workspace serve</c> process that
/// holds a lock file while it serves, and takes refresh requests and a stop file from its control directory.
/// </summary>
internal sealed class ProjFsFolders(Monorepo monorepo) : ILazyFolders
{
	private string? unavailable;
	private bool checkedAvailability;

	public string? Unavailable
	{
		get
		{
			if (!checkedAvailability)
			{
				unavailable = ProjFsServer.Unavailable();
				checkedAvailability = true;
			}

			return unavailable;
		}
	}

	public bool ScansFolder => true;

	private static string Control(LocalWorkspace local) => ProjFsServer.ControlDirectory(local.StateFile);

	public async Task MountAsync(LocalWorkspace local, CancellationToken ct = default)
	{
		if (!IsMounted(local))
		{
			await LazyFolders.StartServerAsync(monorepo, local, () => IsMounted(local), ct);
		}
	}

	public Task UnmountAsync(LocalWorkspace local, CancellationToken ct = default) => ProjFsControl.StopAsync(Control(local), ct);

	/// <summary>Served while the server holds its lock file.</summary>
	public bool IsMounted(LocalWorkspace local) => ProjFsControl.IsServing(Control(local));

	public Task RefreshAsync(LocalWorkspace local, IReadOnlyList<string> paths, CancellationToken ct = default) =>
		ProjFsControl.RefreshAsync(Control(local), paths, ct);

	public async Task RemoveFolderAsync(LocalWorkspace local, CancellationToken ct = default)
	{
		// A virtualization root is emptied while it is served (its placeholders need the provider), then removed.
		if (Directory.Exists(local.Root))
		{
			await MountAsync(local, ct);
			foreach (var entry in new DirectoryInfo(local.Root).EnumerateFileSystemInfos())
			{
				if (entry is DirectoryInfo directory)
				{
					directory.Delete(true);
				}
				else
				{
					entry.Delete();
				}
			}
		}

		await UnmountAsync(local, ct);
		if (Directory.Exists(local.Root))
		{
			Directory.Delete(local.Root, true);
		}

		if (Directory.Exists(Control(local)))
		{
			Directory.Delete(Control(local), true);
		}
	}
}
