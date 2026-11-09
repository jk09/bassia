namespace Bassia.Core.Workspaces;

/// <summary>
/// The virtual file system that projects a lazily materialized workspace folder: its files are placeholders in a
/// backing directory, hydrated on first use (<see cref="Placeholders"/>). FUSE on Linux and macOS, ProjFS on Windows;
/// implemented outside the core, which holds no platform code.
/// </summary>
public interface ILazyFolders
{
	/// <summary>Why lazy folders cannot be used on this machine (a driver or library missing), or null when they can.</summary>
	string? Unavailable { get; }

	/// <summary>Projects the workspace's backing directory at its folder, if it is not already; returns once it is.</summary>
	Task MountAsync(LocalWorkspace local, CancellationToken ct = default);

	/// <summary>Stops projecting the folder (the backing directory stays).</summary>
	Task UnmountAsync(LocalWorkspace local, CancellationToken ct = default);

	/// <summary>Whether the folder is projected now.</summary>
	bool IsMounted(LocalWorkspace local);
}

/// <summary>How a workspace folder gets its files: written in full, or as placeholders hydrated on first use.</summary>
public static class Materialize
{
	public const string Full = "full";
	public const string Lazy = "lazy";

	public static bool IsLazy(string? mode) => mode switch
	{
		null or "" or Full => false,
		Lazy => true,
		_ => throw new Storage.VersionException($"Unknown materialization '{mode}': use {Full} (write every file in context) or {Lazy} (placeholders hydrated on first use).")
	};
}
