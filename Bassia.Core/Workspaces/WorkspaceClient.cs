namespace Bassia.Core.Workspaces;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// A workspace's directory on this machine and its tree state. A lazy one keeps its files in a backing directory
/// (<see cref="TreeState.Backing"/>) that a virtual file system projects at <see cref="Root"/>.
/// </summary>
public sealed class LocalWorkspace(string id, string root, string stateFile, IWorkspaceProvider provider, TreeState? state = null)
{
	public string Id { get; } = id;

	/// <summary>The workspace's folder: where agents and tools work.</summary>
	public string Root { get; } = root;

	public string StateFile { get; } = stateFile;

	public IWorkspaceProvider Provider { get; } = provider;

	public TreeState State { get; set; } = state ?? TreeState.Load(stateFile);

	/// <summary>Whether the folder is lazy: placeholders in a backing directory, projected at the folder.</summary>
	public bool IsLazy => State.Backing is not null;

	/// <summary>Where the files really are: the backing directory of a lazy folder, else the folder.</summary>
	public string Files => State.Backing ?? Root;

	/// <summary>The provider that reads the folder for snapshots, when it is not <see cref="Provider"/> (a ProjFS folder keeps changes itself).</summary>
	public IWorkspaceProvider? Scanner { get; init; }

	public void Save() => State.Save(StateFile);
}

/// <summary>What a snapshot did.</summary>
public sealed record SnapshotResult(WorkspaceState Workspace, IReadOnlyList<PathChange> Changes, int Hashed, bool Local)
{
	public bool Changed => Changes.Count > 0;
}

/// <summary>
/// Workspaces on this machine: creates their directories, snapshots them implicitly before anything reads them, and
/// keeps the directory in step when sync, undo or restore change the working commit. Commits are never made by hand:
/// every command that reads a workspace snapshots it first.
/// </summary>
public sealed class WorkspaceClient(Repository repo, ILanding landing, string stateDir, string agent, Func<string, string, bool, IWorkspaceProvider>? providers = null,
	string? monorepoRoot = null, ILazyFolders? lazyFolders = null)
{
	/// <summary>The provider for a directory of files (a workspace's id; whether it holds placeholders).</summary>
	private readonly Func<string, string, bool, IWorkspaceProvider> providers = providers ?? ((root, _, lazy) => new CheckoutProvider(root, repo) { Lazy = lazy });

	/// <summary>The virtual file system of lazy folders here, if any.</summary>
	public ILazyFolders? LazyFolders => lazyFolders;

	public Repository Repository => repo;

	public WorkspaceService Service { get; } = new(repo);

	public ILanding Landing => landing;

	public string Agent => agent;

	public static string Host => Environment.MachineName;

	private string StateFile(string id) => Path.Combine(stateDir, id + ".json");

	/// <summary>Creates a workspace (on mainline, or stacked on <paramref name="on"/>) and checks it out into <paramref name="directory"/>.</summary>
	public async Task<(WorkspaceState State, LocalWorkspace Local)> CreateAsync(string name, string directory, string? on = null, string intent = "",
		ReplayRecipe? recipe = null, ChangeId? changeId = null, string? replayOf = null, ContextSpec? context = null, bool lazy = false, CancellationToken ct = default)
	{
		directory = Path.GetFullPath(directory);
		CheckDirectory(directory);
		CheckLazy(lazy);
		var state = await Service.CreateAsync(name, agent, on, intent, recipe, changeId, directory, Host, replayOf, context, ct);
		var local = await CheckOutAsync(state, directory, lazy, ct);
		return (state, local);
	}

	public async Task<(WorkspaceState State, LocalWorkspace Local)> CloneAsync(string source, string name, string directory, ContextSpec? context = null,
		bool lazy = false, CancellationToken ct = default)
	{
		directory = Path.GetFullPath(directory);
		CheckDirectory(directory);
		CheckLazy(lazy);
		var state = await Service.CloneAsync(source, name, agent, directory, Host, context, ct);
		return (state, await CheckOutAsync(state, directory, lazy, ct));
	}

	/// <summary>
	/// Writes a workspace's working tree into <paramref name="directory"/> and makes it the workspace's local directory:
	/// the paths its context-spec contains, with only their content fetched and cached; directories outside the context
	/// appear empty at its edge. A <paramref name="lazy"/> folder holds placeholders, hydrated on first use by this
	/// machine's virtual file system (<see cref="ILazyFolders"/>): nothing is fetched until a file is read.
	/// </summary>
	public async Task<LocalWorkspace> CheckOutAsync(WorkspaceState state, string directory, bool lazy = false, CancellationToken ct = default)
	{
		directory = Path.GetFullPath(directory);
		CheckLazy(lazy);
		if (Local(state) is { IsLazy: true } previous && lazyFolders is not null)
		{
			await lazyFolders.UnmountAsync(previous, ct);
		}

		Directory.CreateDirectory(directory);
		if (File.Exists(StateFile(state.Id)))
		{
			File.Delete(StateFile(state.Id));
		}

		var backing = lazy ? BackingDirectory(state.Id) : null;
		if (backing is not null && Directory.Exists(backing))
		{
			Directory.Delete(backing, true);
		}

		var files = backing ?? directory;
		Directory.CreateDirectory(files);
		var tree = new TreeState { Context = ContextSpec.Of(state.Record.Context), Backing = backing };
		var local = new LocalWorkspace(state.Id, directory, StateFile(state.Id), providers(files, state.Id, lazy), tree);
		var working = await repo.Objects.ReadCommitAsync(state.Record.Working, ct);
		await local.Provider.UpdateAsync(local.State, working.Tree, ct);
		local.Save();
		WriteMarker(files, state.Id, monorepoRoot);
		if (lazy)
		{
			await lazyFolders!.MountAsync(local, ct);
		}

		if (state.Record.Path != directory || state.Record.Host != Host)
		{
			await Service.SetPathAsync(state.Id, directory, Host, ct);
		}

		return local;
	}

	/// <summary>The workspace's directory on this machine, if it has one here (a lazy one whether projected now or not).</summary>
	public LocalWorkspace? Local(WorkspaceState state)
	{
		var path = state.Record.Path;
		if (path is null || state.Record.Host != Host || !File.Exists(StateFile(state.Id)))
		{
			return null;
		}

		var tree = TreeState.Load(StateFile(state.Id));
		var files = tree.Backing ?? path;
		if (ReadMarker(files) is not { } marker || marker.Workspace != state.Id)
		{
			return null;
		}

		return new LocalWorkspace(state.Id, path, StateFile(state.Id), providers(files, state.Id, tree.Backing is not null), tree)
		{
			Scanner = tree.Backing is not null && lazyFolders is { ScansFolder: true } ? providers(path, state.Id, false) : null
		};
	}

	/// <summary>Where a lazy folder of workspace <paramref name="id"/> keeps its files and placeholders.</summary>
	public string BackingDirectory(string id) => Path.Combine(stateDir, id + ".files");

	private void CheckLazy(bool lazy)
	{
		if (lazy && (lazyFolders is null ? "this client has no virtual file system for lazy folders" : lazyFolders.Unavailable) is { } why)
		{
			throw new VersionException($"A lazy workspace folder cannot be made here: {why}. Use full materialization instead.");
		}
	}

	/// <summary>
	/// Snapshots the workspace's directory, if it has one here: hashes only the paths whose size or time changed, uploads
	/// missing chunks, rebuilds the trees along changed paths and amends the working commit on the workspace's own head.
	/// </summary>
	public Task<SnapshotResult> SnapshotAsync(WorkspaceState state, string? run = null, CancellationToken ct = default) =>
		// Changed files' chunks, the rebuilt trees, the commit, the view and the operation go up together.
		repo.Objects.BatchAsync(() => SnapshotOnceAsync(state, run, ct));

	private async Task<SnapshotResult> SnapshotOnceAsync(WorkspaceState state, string? run, CancellationToken ct)
	{
		if (Local(state) is not { } local || !state.IsOpen)
		{
			return new SnapshotResult(state, [], 0, false);
		}

		var policy = await PolicyAsync(local, ct);
		// The record's context-spec is the boundary, even when another client changed it and this folder is not in step yet.
		if (local.Scanner is not null)
		{
			// The folder holds the changes, and only a served folder lists its placeholders.
			await lazyFolders!.MountAsync(local, ct);
		}

		var scan = await (local.Scanner ?? local.Provider).ScanAsync(local.State, policy, true, ct, ContextSpec.Of(state.Record.Context));
		var diskTree = scan.Changes.Count == 0 ? local.State.Tree : await repo.Trees.EditAsync(local.State.Tree, scan.Changes, ct);
		var current = state;
		var working = await repo.Objects.ReadCommitAsync(current.Record.Working, ct);
		if (diskTree == working.Tree)
		{
			local.State.Tree = diskTree;
			await ApplyContextAsync(local, current, ct);
			local.Save();
			await RefreshAsync(local, ct);
			return new SnapshotResult(current, scan.Changes, scan.Hashed, true);
		}

		// Apply what changed on disk onto the working commit as it is now (another process may have amended it).
		var target = working.Tree == local.State.Tree ? diskTree : await repo.Trees.EditAsync(working.Tree, scan.Changes, ct);
		var updated = await Service.SetTreeAsync(state.Id, target, $"snapshot: {Summary(scan.Changes)}", agent, run, ct);
		local.State.Tree = diskTree;
		var final = (await repo.Objects.ReadCommitAsync(updated.Record.Working, ct)).Tree;
		if (final != diskTree)
		{
			await local.Provider.UpdateAsync(local.State, final, ct);
		}

		await ApplyContextAsync(local, updated, ct);
		local.Save();
		await RefreshAsync(local, ct);
		return new SnapshotResult(updated, scan.Changes, scan.Hashed, true);
	}

	/// <summary>
	/// Changes the workspace's context-spec: snapshots its folder here first (a change outside the current context is
	/// refused as always), records the new spec as an operation (refused if it leaves out a path the workspace changed),
	/// then writes what the new spec adds to the folder and removes what it drops. Paths outside the context stay in the
	/// workspace's tree, unchanged.
	/// </summary>
	public async Task<(WorkspaceState State, ContextResult? Result)> SetContextAsync(WorkspaceState state, ContextSpec context, CancellationToken ct = default)
	{
		state = (await SnapshotAsync(state, null, ct)).Workspace;
		state = await Service.SetContextAsync(state.Id, context, agent, ct);
		return (state, await UpdateDirectoryAsync(state, ct));
	}

	/// <summary>Brings the folder's context in step with the record's, if it differs (after a change here or elsewhere, or an undo).</summary>
	private static async Task<ContextResult?> ApplyContextAsync(LocalWorkspace local, WorkspaceState state, CancellationToken ct)
	{
		var context = ContextSpec.Of(state.Record.Context);
		return local.State.Context.Equals(context) ? null : await local.Provider.ApplyContextAsync(local.State, context, ct);
	}

	/// <summary>Snapshots, then rebases onto the newest base and updates the directory.</summary>
	public async Task<SyncResult> SyncAsync(WorkspaceState state, IMergeDriver? semantic = null, CancellationToken ct = default)
	{
		state = (await SnapshotAsync(state, null, ct)).Workspace;
		ConflictResolver? resolver = null;
		if (semantic is not null)
		{
			var main = await repo.GetMainAsync(ct);
			resolver = new ConflictResolver(repo.Objects, await RepoPolicy.LoadAsync(repo.Trees, main.Tree, ct), semantic);
		}

		var result = await Service.SyncAsync(state.Id, resolver, agent, ct);
		await UpdateDirectoryAsync(result.Workspace, ct);
		return result;
	}

	public async Task<(WorkspaceState State, ObjectId OldTree, ObjectId NewTree)> UndoAsync(WorkspaceState state, CancellationToken ct = default)
	{
		state = (await SnapshotAsync(state, null, ct)).Workspace;
		var result = await Service.UndoAsync(state.Id, agent, ct);
		await UpdateDirectoryAsync(result.State, ct);
		return result;
	}

	public async Task<(WorkspaceState State, ObjectId OldTree, ObjectId NewTree)> RestoreAsync(WorkspaceState state, ObjectId op, CancellationToken ct = default)
	{
		state = (await SnapshotAsync(state, null, ct)).Workspace;
		var result = await Service.RestoreAsync(state.Id, op, agent, null, ct);
		await UpdateDirectoryAsync(result.State, ct);
		return result;
	}

	/// <summary>Snapshots, then lands (or with <see cref="LandRequest.DryRun"/> checks whether it can land).</summary>
	public async Task<LandResult> LandAsync(WorkspaceState state, LandRequest request, CancellationToken ct = default)
	{
		await SnapshotAsync(state, null, ct);
		var result = await landing.LandAsync(request with { Workspace = state.Id, Agent = request.Agent ?? agent }, ct);
		if (!request.DryRun && result.Outcome == LandOutcome.Landed && await repo.FindWorkspaceAsync(state.Id, ct) is { } after)
		{
			await UpdateDirectoryAsync(after, ct);
			if (!after.IsOpen)
			{
				// A landed workspace's lazy folder is no longer served; 'workspace mount' serves it again.
				await UnmountAsync(after, ct);
			}
		}

		return result;
	}

	/// <summary>
	/// Makes the directory show the workspace's working tree and context-spec (after an operation changed either); what
	/// bringing the context in step did, if it had to.
	/// </summary>
	public async Task<ContextResult?> UpdateDirectoryAsync(WorkspaceState state, CancellationToken ct = default)
	{
		if (Local(state) is not { } local)
		{
			return null;
		}

		var tree = (await repo.Objects.ReadCommitAsync(state.Record.Working, ct)).Tree;
		if (tree != local.State.Tree)
		{
			await local.Provider.UpdateAsync(local.State, tree, ct);
		}

		var result = await ApplyContextAsync(local, state, ct);
		local.Save();
		await RefreshAsync(local, ct);
		return result;
	}

	/// <summary>Has a lazy folder's server show again what the provider just changed in the backing directory.</summary>
	private async Task RefreshAsync(LocalWorkspace local, CancellationToken ct)
	{
		if (local.IsLazy && local.Provider is CheckoutProvider provider && provider.TakeChanged() is { Count: > 0 } paths && lazyFolders is not null)
		{
			await lazyFolders.RefreshAsync(local, paths, ct);
		}
	}

	/// <summary>Removes the workspace's directory (and a lazy one's backing directory) and its tree state from this machine.</summary>
	public async Task RemoveDirectoryAsync(WorkspaceState state, CancellationToken ct = default)
	{
		if (Local(state) is { } local)
		{
			if (local.IsLazy)
			{
				if (lazyFolders is not null)
				{
					await lazyFolders.RemoveFolderAsync(local, ct);
				}

				if (Directory.Exists(local.Files))
				{
					Directory.Delete(local.Files, true);
				}
			}

			if (Directory.Exists(local.Root))
			{
				Directory.Delete(local.Root, true);
			}

			File.Delete(local.StateFile);
		}
	}

	/// <summary>Stops projecting a lazy folder (after its workspace closed); its files stay in the backing directory.</summary>
	public async Task UnmountAsync(WorkspaceState state, CancellationToken ct = default)
	{
		if (Local(state) is { IsLazy: true } local && lazyFolders is not null)
		{
			await lazyFolders.UnmountAsync(local, ct);
		}
	}

	/// <summary>Projects a lazy folder again (after a restart), if it is lazy and not projected now.</summary>
	public async Task MountAsync(WorkspaceState state, CancellationToken ct = default)
	{
		if (Local(state) is { IsLazy: true } local)
		{
			CheckLazy(true);
			await lazyFolders!.MountAsync(local, ct);
		}
	}

	private async Task<RepoPolicy> PolicyAsync(LocalWorkspace local, CancellationToken ct)
	{
		var file = Path.Combine(local.Files, RepoPolicy.FileName);
		if (File.Exists(file))
		{
			try
			{
				return RepoPolicy.Parse(await File.ReadAllTextAsync(file, ct));
			}
			catch (VersionException)
			{
				// A policy being edited may not parse yet; the defaults still keep build output out.
				return RepoPolicy.Default;
			}
		}

		return RepoPolicy.Default;
	}

	private static string Summary(IReadOnlyList<PathChange> changes)
	{
		var text = string.Join(", ", changes.Take(3).Select(change => (change.Leaf is null ? "-" : "") + change.Path));
		return changes.Count > 3 ? $"{text} and {changes.Count - 3} more" : text;
	}

	private static void CheckDirectory(string directory)
	{
		if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
		{
			throw new VersionException($"'{directory}' is not empty; a workspace needs a new or empty directory.");
		}
	}

	// ----- the marker in a workspace's root -----

	/// <summary>What the marker file in a workspace's root records.</summary>
	public sealed record Marker(string Workspace, string? Monorepo);

	public static void WriteMarker(string directory, string workspace, string? monorepo = null)
	{
		var table = new TomlTable { ["workspace"] = workspace };
		var existing = ReadMarker(directory);
		if ((monorepo ?? existing?.Monorepo) is { } root)
		{
			table["monorepo"] = root;
		}

		File.WriteAllText(Path.Combine(directory, CheckoutProvider.MarkerName),
			"# A Bassia workspace: its id and the monorepo it belongs to. Never snapshotted.\n" + TomlSerializer.Serialize(table), Encoding.UTF8);
	}

	public static Marker? ReadMarker(string directory)
	{
		var file = Path.Combine(directory, CheckoutProvider.MarkerName);
		if (!File.Exists(file))
		{
			return null;
		}

		try
		{
			var table = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file));
			return table is not null && table.TryGetValue("workspace", out var id) && id is string workspace
				? new Marker(workspace, table.TryGetValue("monorepo", out var root) ? root as string : null)
				: null;
		}
		catch (TomlException)
		{
			return null;
		}
	}

	/// <summary>The workspace directory containing <paramref name="path"/>, found by its marker, or null.</summary>
	public static (string Directory, Marker Marker)? FindEnclosing(string path)
	{
		for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir is not null; dir = dir.Parent)
		{
			if (ReadMarker(dir.FullName) is { } marker)
			{
				return (dir.FullName, marker);
			}
		}

		return null;
	}
}
