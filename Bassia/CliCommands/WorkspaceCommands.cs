namespace Bassia.CliCommands;

using Bassia.Cli;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Interop;
using static Bassia.CliCommands.Results;

/// <summary>
/// <c>bassia workspace ...</c>: a workspace per agentic run over the monorepo, instead of branches. Commits are implicit:
/// every command that reads a workspace snapshots its folder first.
/// </summary>
internal static class WorkspaceCommands
{
	public static async Task<int> CreateAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var name = invocation.Require("name");
		var path = invocation.Get("path") is { } given ? Path.GetFullPath(given) : monorepo.DefaultWorkspacePath(name);
		if (invocation.Get("on") is { } on)
		{
			// A stacked workspace starts from its parent's work as it is in the parent's folder now.
			await monorepo.Workspaces.SnapshotAsync(await monorepo.Repo.ResolveWorkspaceAsync(on));
		}

		var (state, local) = await monorepo.Workspaces.CreateAsync(name, path, invocation.Get("on"), invocation.Get("intent") ?? "");
		var data = await WorkspaceAsync(monorepo.Repo, state);
		return Ok(invocation, state.Record.Parent is null
			? $"Created workspace '{name}' on mainline seq {state.Record.BaseSeq} in '{local.Root}'. Edit there; 'bassia workspace check' tells whether it can land."
			: $"Created workspace '{name}' stacked on '{invocation.Get("on")}' in '{local.Root}'. It lands after the workspace it is stacked on.", data);
	}

	public static async Task<int> CloneAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var source = await monorepo.WorkspaceAsync(invocation.Require("from"));
		await monorepo.Workspaces.SnapshotAsync(source);
		var name = invocation.Require("name");
		var path = invocation.Get("path") is { } given ? Path.GetFullPath(given) : monorepo.DefaultWorkspacePath(name);
		var (state, local) = await monorepo.Workspaces.CloneAsync(source.Id, name, path);
		return Ok(invocation, $"Cloned workspace '{source.Name}' as '{name}' in '{local.Root}': same base and work, a new change.",
			await WorkspaceAsync(monorepo.Repo, state));
	}

	public static async Task<int> CheckoutAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.Repo.ResolveWorkspaceAsync(invocation.Require("workspace"));
		var path = invocation.Get("path") is { } given ? Path.GetFullPath(given) : monorepo.DefaultWorkspacePath(state.Name);
		if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
		{
			return Fail(invocation, $"'{path}' is not empty; give an empty folder with -path.");
		}

		var local = await monorepo.Workspaces.CheckOutAsync(state, path);
		return Ok(invocation, $"Checked out workspace '{state.Name}' into '{local.Root}'.", await WorkspaceAsync(monorepo.Repo, (await monorepo.Repo.FindWorkspaceAsync(state.Id))!));
	}

	public static async Task<int> ListAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var all = invocation.Has("all");
		var workspaces = await monorepo.Repo.ListWorkspacesAsync(all);
		var entries = new List<IReadOnlyDictionary<string, object?>>();
		foreach (var state in workspaces)
		{
			entries.Add(await WorkspaceAsync(monorepo.Repo, state));
		}

		var data = new Dictionary<string, object?>
		{
			["table"] = Table(["NAME", "ID", "STATUS", "BASE", "ON", "RUN", "LAST LAND"], workspaces.Select(state => (IReadOnlyList<string>)
			[
				state.Name, state.Id, state.Record.Status, $"seq {state.Record.BaseSeq}",
				state.Record.Parent is { } parent ? workspaces.FirstOrDefault(other => other.Id == parent)?.Name ?? parent : "mainline",
				state.Record.Run is { } run ? (Agents.AgentRunner.IsAlive(run) ? "live" : run.Status) : "-",
				state.Record.LastLand?.Outcome ?? "-"
			])),
			["workspace"] = entries
		};
		return Ok(invocation, $"{workspaces.Count} {(all ? "" : "open ")}workspace(s).", data);
	}

	public static async Task<int> ShowAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		state = (await monorepo.Workspaces.SnapshotAsync(state)).Workspace;
		var data = await WorkspaceAsync(monorepo.Repo, state);
		var records = await DiffAsync(monorepo, state);
		data["changes"] = Summary(records);
		data["local"] = monorepo.Workspaces.Local(state) is not null;
		return Ok(invocation, $"Workspace '{state.Name}' ({state.Record.Status}) on {(state.Record.Parent is null ? "mainline" : "workspace " + state.Record.Parent)} " +
			$"seq {state.Record.BaseSeq}: {Summary(records)}.", data);
	}

	private static async Task<IReadOnlyList<DiffRecord>> DiffAsync(Monorepo monorepo, WorkspaceState state)
	{
		var baseTree = (await monorepo.Repo.Objects.ReadCommitAsync(state.Record.Base)).Tree;
		var workingTree = (await monorepo.Repo.Objects.ReadCommitAsync(state.Record.Working)).Tree;
		return await monorepo.Repo.Diffs.DiffAsync(baseTree, workingTree);
	}

	public static async Task<int> StatusAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var snapshot = await monorepo.Workspaces.SnapshotAsync(state);
		state = snapshot.Workspace;
		var records = await DiffAsync(monorepo, state);
		var conflicts = records.Where(record => record.New?.Kind == EntryKind.Conflict).Select(record => record.Path).ToList();
		var data = new Dictionary<string, object?>
		{
			["workspace"] = state.Name,
			["status"] = state.Record.Status,
			["base_seq"] = state.Record.BaseSeq,
			["snapshotted"] = snapshot.Changes.Count,
			["hashed"] = snapshot.Hashed,
			["conflicts"] = conflicts.Count == 0 ? null : conflicts,
			["table"] = Table(["STATUS", "PATH", "SIZE"], records.Select(record => (IReadOnlyList<string>)
			[
				record.New?.Kind == EntryKind.Conflict ? "conflict" : record.Status,
				record.OldPath is null ? record.Path : $"{record.OldPath} -> {record.Path}",
				record.New is null ? "-" : Size(record.New.Size)
			])),
			["change"] = Tables(records, Diff)
		};
		return Ok(invocation, $"Workspace '{state.Name}' against its base (seq {state.Record.BaseSeq}): {Summary(records)}" +
			$"{(conflicts.Count > 0 ? $"; {conflicts.Count} unresolved conflict(s)" : "")}.", data);
	}

	public static async Task<int> SnapshotAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var snapshot = await monorepo.Workspaces.SnapshotAsync(state);
		if (!snapshot.Local)
		{
			return Fail(invocation, $"Workspace '{state.Name}' has no folder on this machine; 'bassia workspace checkout {state.Name}' makes one.");
		}

		return Ok(invocation, snapshot.Changed
				? $"Snapshotted {snapshot.Changes.Count} changed path(s) of workspace '{state.Name}' ({snapshot.Hashed} hashed)."
				: $"Workspace '{state.Name}' has no changes since its last snapshot.",
			new Dictionary<string, object?>
			{
				["workspace"] = state.Name,
				["working"] = snapshot.Workspace.Record.Working.Hex,
				["hashed"] = snapshot.Hashed,
				["path"] = snapshot.Changes.Select(change => change.Path).ToList()
			});
	}

	public static async Task<int> DescribeAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var updated = await monorepo.Workspaces.Service.DescribeAsync(state.Id, invocation.Require("intent"), monorepo.User);
		return Ok(invocation, $"Workspace '{state.Name}' now says: {Short(invocation.Require("intent"))}", await WorkspaceAsync(monorepo.Repo, updated));
	}

	public static async Task<int> DiffAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		state = (await monorepo.Workspaces.SnapshotAsync(state)).Workspace;
		var records = (await DiffAsync(monorepo, state)).Where(record => invocation.Get("path") is not { } path || PathNames.IsUnder(record.Path, PathNames.Normalize(path))).ToList();
		var data = new Dictionary<string, object?> { ["workspace"] = state.Name, ["change"] = Tables(records, Diff) };
		if (invocation.Has("patch"))
		{
			var text = new System.Text.StringBuilder();
			var files = new List<IReadOnlyDictionary<string, object?>>();
			foreach (var record in records)
			{
				var patch = await monorepo.Repo.Diffs.PatchAsync(record);
				files.Add(new Dictionary<string, object?>
				{
					["path"] = record.Path,
					["added"] = patch.Added,
					["deleted"] = patch.Deleted,
					["rewritten"] = patch.Rewritten,
					["binary"] = patch.Binary,
					["summary"] = patch.Summary
				});
				text.Append(patch.Hunks ?? $"--- {record.OldPath ?? record.Path}\n+++ {record.Path}\n# {patch.Summary}\n");
			}

			data["file"] = files;
			data["patch"] = new TomlText(text.Length == 0 ? "(no changes)" : text.ToString());
		}
		else
		{
			data["stat"] = Table(["STATUS", "PATH", "OLD", "NEW"], records.Select(record => (IReadOnlyList<string>)
			[
				record.Status, record.OldPath is null ? record.Path : $"{record.OldPath} -> {record.Path}",
				record.Status == ChangeStatus.Mode ? (record.Old!.Executable ? "100755" : "100644") : record.Old is null ? "-" : Size(record.Old.Size),
				record.Status == ChangeStatus.Mode ? (record.New!.Executable ? "100755" : "100644") : record.New is null ? "-" : Size(record.New.Size)
			]));
		}

		return Ok(invocation, $"Workspace '{state.Name}' against its base: {Summary(records)}.", data);
	}

	private static LandRequest Request(Invocation invocation, WorkspaceState state, bool dryRun) =>
		new(state.Id, invocation.Has("strict"), invocation.List("reads"), LeaseTokens(invocation), invocation.Has("keep"), dryRun);

	private static IReadOnlyList<LeaseToken> LeaseTokens(Invocation invocation) => invocation.List("lease-token").Select(item =>
	{
		var parts = item.Split(':');
		return parts.Length == 2 && long.TryParse(parts[1], out var token) ? new LeaseToken(parts[0], token) : throw invocation.Usage($"'{item}' is not <lease-id>:<token>.");
	}).ToList();

	public static async Task<int> CheckAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var result = await monorepo.Workspaces.LandAsync(state, Request(invocation, state, dryRun: true));
		var data = Land(result);
		data["workspace"] = state.Name;
		data["can_land"] = result.Outcome is LandOutcome.WouldLand or LandOutcome.Nothing;
		data["next"] = result.Outcome switch
		{
			LandOutcome.WouldLand => $"bassia workspace land {state.Name}",
			LandOutcome.NeedsReplay => $"bassia agent replay {state.Name}   (or: bassia workspace sync {state.Name}, resolve, land)",
			LandOutcome.Conflict => $"resolve the conflicts in '{state.Record.Path}', then bassia workspace land {state.Name}",
			_ => null
		};
		return Ok(invocation, result.Message, data);
	}

	public static async Task<int> LandAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var result = await monorepo.Workspaces.LandAsync(state, Request(invocation, state, dryRun: false));
		var data = Land(result);
		data["workspace"] = state.Name;
		return LandOutcome.IsSuccess(result.Outcome) ? Ok(invocation, result.Message, data) : Fail(invocation, result.Message, data);
	}

	public static async Task<int> SyncAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var result = await monorepo.Workspaces.SyncAsync(state, MergeDrivers.Semantic(monorepo.Config.Get("merge.semantic_driver")));
		var data = new Dictionary<string, object?>
		{
			["workspace"] = state.Name,
			["moved"] = result.Moved,
			["from_seq"] = result.FromSeq,
			["to_seq"] = result.ToSeq,
			["conflicts"] = result.Conflicts.ToList(),
			["merged"] = result.Merged.ToList()
		};
		return Ok(invocation, !result.Moved
			? $"Workspace '{state.Name}' is already on its newest base (seq {result.ToSeq})."
			: result.Conflicts.Count == 0
				? $"Rebased workspace '{state.Name}' from seq {result.FromSeq} onto seq {result.ToSeq}{(result.Merged.Count > 0 ? $", {result.Merged.Count} path(s) merged" : "")}."
				: $"Rebased workspace '{state.Name}' onto seq {result.ToSeq} with {result.Conflicts.Count} conflict(s), written with markers: resolve them, then land.", data);
	}

	public static async Task<int> UndoAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var description = state.Operation.Description;
		var (after, oldTree, newTree) = await monorepo.Workspaces.UndoAsync(state);
		var changed = await monorepo.Repo.Trees.DiffAsync(oldTree, newTree);
		return Ok(invocation, $"Undid '{description}' in workspace '{state.Name}' ({changed.Count} path(s) changed back). Undo again to redo.",
			await WorkspaceAsync(monorepo.Repo, after));
	}

	public static async Task<int> RestoreAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var op = await ResolveOperationAsync(monorepo, state, invocation.Require("op"));
		var (after, _, _) = await monorepo.Workspaces.RestoreAsync(state, op);
		return Ok(invocation, $"Restored workspace '{state.Name}' to operation {op.Short}.", await WorkspaceAsync(monorepo.Repo, after));
	}

	/// <summary>An operation of the workspace's log by its seq, or by a prefix of its id of at least 4 characters.</summary>
	private static async Task<ObjectId> ResolveOperationAsync(Monorepo monorepo, WorkspaceState state, string reference)
	{
		var entries = await monorepo.Repo.LogAsync(HeadName.Workspace(state.Id)).ToListAsync();
		var bySeq = long.TryParse(reference, out var seq) ? entries.Where(entry => entry.Operation.Seq == seq).ToList() : [];
		var byId = reference.Length >= 4 ? entries.Where(entry => entry.Id.Hex.StartsWith(reference, StringComparison.OrdinalIgnoreCase)).ToList() : [];
		var matches = bySeq.Count > 0 ? bySeq : byId;
		return matches.Count switch
		{
			1 => matches[0].Id,
			0 => throw new MonorepoException($"No operation '{reference}' in workspace '{state.Name}' (give its seq, or 4+ characters of its id). Run 'bassia workspace log {state.Name}'."),
			_ => throw new MonorepoException($"'{reference}' matches several operations of workspace '{state.Name}'; give more of the id.")
		};
	}

	public static async Task<int> LogAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var limit = invocation.Int("limit", 50, 1);
		var entries = await monorepo.Repo.LogAsync(HeadName.Workspace(state.Id), limit).ToListAsync();
		return Ok(invocation, $"{entries.Count} operation(s) of workspace '{state.Name}', newest first. 'bassia workspace restore -op <id>' returns to one.",
			new Dictionary<string, object?>
			{
				["table"] = Table(["SEQ", "OPERATION", "TIME", "BY", "DESCRIPTION"], entries.Select(entry => (IReadOnlyList<string>)
					[entry.Operation.Seq.ToString(), entry.Id.Short, Time(entry.Operation.Time), entry.Operation.Agent ?? "-", entry.Operation.Description])),
				["operation"] = Tables(entries, Operation)
			});
	}

	public static async Task<int> CloseAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var closed = await monorepo.Workspaces.Service.CloseAsync(state.Id, invocation.Has("abandon"), monorepo.User);
		var removed = false;
		if (invocation.Has("remove"))
		{
			removed = monorepo.Workspaces.Local(state) is not null;
			monorepo.Workspaces.RemoveDirectory(state);
		}

		return Ok(invocation, $"Workspace '{state.Name}' is {closed.Record.Status}{(removed ? " and its folder is removed" : "")}. Its operations stay recoverable until retention ends.",
			await WorkspaceAsync(monorepo.Repo, closed));
	}
}
