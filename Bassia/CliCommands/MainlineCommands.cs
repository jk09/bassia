namespace Bassia.CliCommands;

using System.Text;
using Bassia.Cli;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using static Bassia.CliCommands.Results;

/// <summary><c>bassia mainline ...</c>: the trunk every workspace lands on, read by seq.</summary>
internal static class MainlineCommands
{
	private static async Task<MainState> AtAsync(Monorepo monorepo, Invocation invocation)
	{
		var main = await monorepo.Repo.GetMainAsync();
		return invocation.Get("seq") is null ? main : await monorepo.Repo.MainAtSeqAsync(invocation.Int("seq", (int)main.Seq, 0));
	}

	public static async Task<int> LogAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var limit = invocation.Int("limit", 30, 1);
		var path = invocation.Get("path") is { } given ? PathNames.Normalize(given) : null;
		var entries = new List<(MainState State, int Touched)>();
		var state = await monorepo.Repo.GetMainAsync();
		while (entries.Count < limit)
		{
			MainState? parent = state.Operation.Parents.Count > 0 ? await monorepo.Repo.MainAtAsync(state.Operation.Parents[0]) : null;
			var changes = await monorepo.Repo.Trees.DiffAsync(parent?.Tree, state.Tree);
			if (path is null || changes.Any(change => PathNames.IsUnder(change.Path, path)))
			{
				entries.Add((state, changes.Count));
			}

			if (parent is null)
			{
				break;
			}

			state = parent;
		}

		return Ok(invocation, $"{entries.Count} landing(s){(path is null ? "" : $" touching '{path}'")}, newest first.",
			new Dictionary<string, object?>
			{
				["table"] = Table(["SEQ", "COMMIT", "CHANGE", "TIME", "AUTHOR", "PATHS", "INTENT"], entries.Select(entry => (IReadOnlyList<string>)
				[
					entry.State.Seq.ToString(), entry.State.CommitId.Short, entry.State.Commit.ChangeId.Short, Time(entry.State.Commit.Time),
					entry.State.Commit.Author.ToString(), entry.Touched.ToString(), Short(entry.State.Commit.Intent, 50)
				])),
				["landing"] = Tables(entries, entry => Landing(entry.State, entry.Touched))
			});
	}

	private static Dictionary<string, object?> Landing(MainState state, int touched) => new()
	{
		["seq"] = state.Seq,
		["commit"] = state.CommitId.Hex,
		["change_id"] = state.Commit.ChangeId.Hex,
		["time"] = Time(state.Commit.Time),
		["author"] = state.Commit.Author.Agent,
		["run"] = state.Commit.Author.Run,
		["workspace"] = state.Operation.Workspace,
		["intent"] = state.Commit.Intent,
		["machine_merged"] = state.Commit.MachineMerged,
		["paths"] = touched
	};

	public static async Task<int> ShowAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await AtAsync(monorepo, invocation);
		MainState? parent = state.Operation.Parents.Count > 0 ? await monorepo.Repo.MainAtAsync(state.Operation.Parents[0]) : null;
		var records = await monorepo.Repo.Diffs.DiffAsync(parent?.Tree, state.Tree);
		var data = Landing(state, records.Count);
		data["recipe"] = state.Commit.Recipe is { } recipe
			? new Dictionary<string, object?> { ["prompt"] = recipe.Prompt, ["command"] = recipe.Command, ["agent"] = recipe.Agent, ["model"] = recipe.Model }
			: null;
		data["change"] = Tables(records, Diff);
		if (invocation.Has("patch"))
		{
			var text = new StringBuilder();
			foreach (var record in records)
			{
				var patch = await monorepo.Repo.Diffs.PatchAsync(record);
				text.Append(patch.Hunks ?? $"--- {record.OldPath ?? record.Path}\n+++ {record.Path}\n# {patch.Summary}\n");
			}

			data["patch"] = new TomlText(text.Length == 0 ? "(no changes)" : text.ToString());
		}

		return Ok(invocation, $"Seq {state.Seq}: {Short(state.Commit.Intent)} ({Summary(records)}).", data);
	}

	public static async Task<int> LsAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await AtAsync(monorepo, invocation);
		var path = PathNames.Normalize(invocation.Get("path") ?? "");
		var leaf = await monorepo.Repo.Trees.GetAsync(state.Tree, path) ?? throw new MonorepoException($"'{path}' does not exist at seq {state.Seq}.");
		var entries = leaf.Kind == EntryKind.Tree ? (await monorepo.Repo.Objects.ReadTreeAsync(leaf.Id)).Entries : [TreeEntry.Of(PathNames.Name(path), leaf)];
		return Ok(invocation, $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} in '{(path.Length == 0 ? "/" : path)}' at seq {state.Seq} (listed from tree metadata, no file content read).",
			new Dictionary<string, object?>
			{
				["seq"] = state.Seq,
				["path"] = path,
				["table"] = Table(["KIND", "SIZE", "NAME"], entries.Select(entry => (IReadOnlyList<string>)
					[entry.Kind.ToString().ToLowerInvariant(), Size(entry.Size), entry.Kind == EntryKind.Tree ? entry.Name + "/" : entry.Name])),
				["entry"] = Tables(entries, entry => new Dictionary<string, object?>
				{
					["name"] = entry.Name,
					["kind"] = entry.Kind.ToString().ToLowerInvariant(),
					["size"] = entry.Size,
					["id"] = entry.Id.Hex,
					["executable"] = entry.Executable
				})
			});
	}

	public static async Task<int> CatAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await AtAsync(monorepo, invocation);
		var path = PathNames.Normalize(invocation.Require("path"));
		var leaf = await monorepo.Repo.Trees.GetAsync(state.Tree, path);
		if (leaf is null || !(leaf.IsFile || leaf.Kind == EntryKind.Symlink))
		{
			return Fail(invocation, $"'{path}' is not a file at seq {state.Seq}.");
		}

		var bytes = await monorepo.Repo.Objects.ReadContentAsync(leaf);
		var text = Core.Text.LineDiff.IsText(bytes) ? Encoding.UTF8.GetString(bytes) : null;
		return Ok(invocation, $"'{path}' at seq {state.Seq}: {Size(bytes.Length)}{(text is null ? ", binary (not shown)" : "")}.",
			new Dictionary<string, object?> { ["path"] = path, ["seq"] = state.Seq, ["size"] = bytes.LongLength, ["id"] = leaf.Id.Hex, ["content"] = text is null ? null : new TomlText(text) });
	}

	public static async Task<int> ChangedAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var since = invocation.Int("since", 0, 0);
		var prefix = PathNames.Normalize(invocation.Get("prefix") ?? "");
		var main = await monorepo.Repo.GetMainAsync();
		if (monorepo.LocalLanding is not null)
		{
			await monorepo.Repo.EnsureIndexedAsync(main);
		}

		var versions = await monorepo.Repo.Paths.ChangedSinceAsync(prefix, since).ToListAsync();
		return Ok(invocation, $"{versions.Count} path(s) under '{(prefix.Length == 0 ? "/" : prefix)}' changed after seq {since} (mainline is at seq {main.Seq}).",
			new Dictionary<string, object?>
			{
				["since"] = since,
				["seq"] = main.Seq,
				["table"] = Table(["SEQ", "STATE", "PATH"], versions.Select(version => (IReadOnlyList<string>)
					[version.Seq.ToString(), version.Leaf is null ? "deleted" : version.Leaf.Kind.ToString().ToLowerInvariant(), version.Path])),
				["path"] = Tables(versions, version => new Dictionary<string, object?>
				{
					["path"] = version.Path,
					["seq"] = version.Seq,
					["deleted"] = version.Leaf is null,
					["id"] = version.Leaf?.Id.Hex,
					["size"] = version.Leaf?.Size
				})
			});
	}

	public static async Task<int> RestoreAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var seq = invocation.Int("seq", 0, 0);
		var target = await monorepo.Repo.MainAtSeqAsync(seq);
		var main = await monorepo.Repo.GetMainAsync();
		var intent = invocation.Get("intent") ?? $"restore mainline to seq {seq}";
		var workspace = await monorepo.Workspaces.Service.CreateAsync($"restore-{seq}", monorepo.User, intent: intent);
		await monorepo.Workspaces.Service.AmendWorkingAsync(workspace.Id, commit => commit with { Tree = target.Tree }, "restore", monorepo.User);
		var result = await monorepo.Landing.LandAsync(new LandRequest(workspace.Id, Agent: monorepo.User));
		var data = Land(result);
		return result.Outcome == LandOutcome.Landed
			? Ok(invocation, $"Mainline holds the tree of seq {seq} again, as the new seq {result.Seq} (history stays linear; seq {main.Seq} and before are kept).", data)
			: Fail(invocation, result.Message, data);
	}
}

/// <summary><c>bassia lease ...</c>: advisory leases on path prefixes, with fencing tokens.</summary>
internal static class LeaseCommands
{
	public static async Task<int> ListAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var leases = await monorepo.Repo.Leases.ListAsync(invocation.Get("prefix") is { } prefix ? PathNames.Normalize(prefix) : null);
		var names = (await monorepo.Repo.ListWorkspacesAsync(true)).ToDictionary(state => state.Id, state => state.Name);
		return Ok(invocation, $"{leases.Count} live lease(s).", new Dictionary<string, object?>
		{
			["table"] = Table(["PREFIX", "MODE", "WORKSPACE", "TOKEN", "EXPIRES", "ID"], leases.Select(lease => (IReadOnlyList<string>)
				[lease.Prefix.Length == 0 ? "/" : lease.Prefix, lease.Mode.ToString().ToLowerInvariant(), names.GetValueOrDefault(lease.Owner, lease.Owner),
					lease.Token.ToString(), Time(lease.Expires), lease.Id])),
			["lease"] = Tables(leases, Lease)
		});
	}

	public static async Task<int> AcquireAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var workspace = await monorepo.WorkspaceAsync(invocation.Get("workspace"));
		var mode = invocation.Get("mode")?.ToLowerInvariant() switch
		{
			null or "exclusive" => LeaseMode.Exclusive,
			"intent" => LeaseMode.Intent,
			var other => throw invocation.Usage($"-mode is exclusive or intent, not '{other}'.")
		};
		var ttl = TimeSpan.FromSeconds(invocation.Int("ttl", monorepo.Config.GetInt("lease.ttl"), 1));
		var wait = TimeSpan.FromSeconds(invocation.Int("wait", 0, 0));
		var result = await monorepo.Repo.Leases.AcquireAsync(new LeaseRequest(invocation.Require("prefix"), mode, workspace.Id, ttl, wait,
			Environment.GetEnvironmentVariable("BASSIA_RUN_ID")));
		if (result.Lease is not { } lease)
		{
			return Fail(invocation, $"'{invocation.Require("prefix")}' is leased by others: {string.Join(", ", result.Blocking.Select(other => $"{other.Prefix} ({other.Mode.ToString().ToLowerInvariant()}, workspace {other.Owner})"))}.",
				new Dictionary<string, object?> { ["blocking"] = Tables(result.Blocking, Results.Lease) });
		}

		var data = Results.Lease(lease);
		data["land_with"] = $"-lease-token {lease.Id}:{lease.Token}";
		return Ok(invocation, $"Leased '{lease.Prefix}' ({mode.ToString().ToLowerInvariant()}) for workspace '{workspace.Name}' until {Time(lease.Expires)}; fencing token {lease.Token}.", data);
	}

	public static async Task<int> RenewAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var ttl = TimeSpan.FromSeconds(invocation.Int("ttl", monorepo.Config.GetInt("lease.ttl"), 1));
		var lease = await monorepo.Repo.Leases.RenewAsync(invocation.Require("id"), long.Parse(invocation.Require("token")), ttl);
		return lease is null
			? Fail(invocation, "The lease expired or was granted to someone else; acquire it again (a new fencing token).")
			: Ok(invocation, $"Renewed '{lease.Prefix}' until {Time(lease.Expires)}.", Results.Lease(lease));
	}

	public static async Task<int> ReleaseAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		await monorepo.Repo.Leases.ReleaseAsync(invocation.Require("id"), long.Parse(invocation.Require("token")));
		return Ok(invocation, "Released the lease.");
	}
}
