namespace Bassia.Core.Workspaces;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Tomlyn;
using Tomlyn.Model;

/// <summary>A workspace's directory on this machine and its tree state.</summary>
public sealed class LocalWorkspace(string id, string root, string stateFile, IWorkspaceProvider provider)
{
	public string Id { get; } = id;

	public string Root { get; } = root;

	public string StateFile { get; } = stateFile;

	public IWorkspaceProvider Provider { get; } = provider;

	public TreeState State { get; set; } = TreeState.Load(stateFile);

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
public sealed class WorkspaceClient(Repository repo, ILanding landing, string stateDir, string agent, Func<string, string, IWorkspaceProvider>? providers = null,
	string? monorepoRoot = null)
{
	private readonly Func<string, string, IWorkspaceProvider> providers = providers ?? ((root, _) => new CheckoutProvider(root, repo));

	public Repository Repository => repo;

	public WorkspaceService Service { get; } = new(repo);

	public ILanding Landing => landing;

	public string Agent => agent;

	public static string Host => Environment.MachineName;

	private string StateFile(string id) => Path.Combine(stateDir, id + ".json");

	/// <summary>Creates a workspace (on mainline, or stacked on <paramref name="on"/>) and checks it out into <paramref name="directory"/>.</summary>
	public async Task<(WorkspaceState State, LocalWorkspace Local)> CreateAsync(string name, string directory, string? on = null, string intent = "",
		ReplayRecipe? recipe = null, ChangeId? changeId = null, string? replayOf = null, CancellationToken ct = default)
	{
		directory = Path.GetFullPath(directory);
		CheckDirectory(directory);
		var state = await Service.CreateAsync(name, agent, on, intent, recipe, changeId, directory, Host, replayOf, ct);
		var local = await CheckOutAsync(state, directory, ct);
		return (state, local);
	}

	public async Task<(WorkspaceState State, LocalWorkspace Local)> CloneAsync(string source, string name, string directory, CancellationToken ct = default)
	{
		directory = Path.GetFullPath(directory);
		CheckDirectory(directory);
		var state = await Service.CloneAsync(source, name, agent, directory, Host, ct);
		return (state, await CheckOutAsync(state, directory, ct));
	}

	/// <summary>Writes a workspace's working tree into <paramref name="directory"/> and makes it the workspace's local directory.</summary>
	public async Task<LocalWorkspace> CheckOutAsync(WorkspaceState state, string directory, CancellationToken ct = default)
	{
		directory = Path.GetFullPath(directory);
		Directory.CreateDirectory(directory);
		if (File.Exists(StateFile(state.Id)))
		{
			File.Delete(StateFile(state.Id));
		}

		var local = new LocalWorkspace(state.Id, directory, StateFile(state.Id), providers(directory, state.Id));
		var working = await repo.Objects.ReadCommitAsync(state.Record.Working, ct);
		await local.Provider.UpdateAsync(local.State, working.Tree, ct);
		local.Save();
		WriteMarker(directory, state.Id, monorepoRoot);
		if (state.Record.Path != directory || state.Record.Host != Host)
		{
			await Service.SetPathAsync(state.Id, directory, Host, ct);
		}

		return local;
	}

	/// <summary>The workspace's directory on this machine, if it has one here.</summary>
	public LocalWorkspace? Local(WorkspaceState state)
	{
		var path = state.Record.Path;
		if (path is null || state.Record.Host != Host || !File.Exists(StateFile(state.Id)) || ReadMarker(path) is not { } marker || marker.Workspace != state.Id)
		{
			return null;
		}

		return new LocalWorkspace(state.Id, path, StateFile(state.Id), providers(path, state.Id));
	}

	/// <summary>
	/// Snapshots the workspace's directory, if it has one here: hashes only the paths whose size or time changed, uploads
	/// missing chunks, rebuilds the trees along changed paths and amends the working commit on the workspace's own head.
	/// </summary>
	public async Task<SnapshotResult> SnapshotAsync(WorkspaceState state, string? run = null, CancellationToken ct = default)
	{
		if (Local(state) is not { } local || !state.IsOpen)
		{
			return new SnapshotResult(state, [], 0, false);
		}

		var policy = await PolicyAsync(local, ct);
		var scan = await local.Provider.ScanAsync(local.State, policy, true, ct);
		var diskTree = scan.Changes.Count == 0 ? local.State.Tree : await repo.Trees.EditAsync(local.State.Tree, scan.Changes, ct);
		var current = state;
		var working = await repo.Objects.ReadCommitAsync(current.Record.Working, ct);
		if (diskTree == working.Tree)
		{
			local.State.Tree = diskTree;
			local.Save();
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

		local.Save();
		return new SnapshotResult(updated, scan.Changes, scan.Hashed, true);
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
		}

		return result;
	}

	/// <summary>Makes the directory show the workspace's working tree (after an operation changed it).</summary>
	public async Task UpdateDirectoryAsync(WorkspaceState state, CancellationToken ct = default)
	{
		if (Local(state) is not { } local)
		{
			return;
		}

		var tree = (await repo.Objects.ReadCommitAsync(state.Record.Working, ct)).Tree;
		if (tree != local.State.Tree)
		{
			await local.Provider.UpdateAsync(local.State, tree, ct);
			local.Save();
		}
	}

	/// <summary>Removes the workspace's directory and its tree state from this machine.</summary>
	public void RemoveDirectory(WorkspaceState state)
	{
		if (Local(state) is { } local)
		{
			if (Directory.Exists(local.Root))
			{
				Directory.Delete(local.Root, true);
			}

			File.Delete(local.StateFile);
		}
	}

	private async Task<RepoPolicy> PolicyAsync(LocalWorkspace local, CancellationToken ct)
	{
		var file = Path.Combine(local.Root, RepoPolicy.FileName);
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
