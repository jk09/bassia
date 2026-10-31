namespace Bassia.CliCommands;

using System.Reflection;
using Bassia.Cli;
using Bassia.Core.Model;
using Bassia.Core.Version;
using Bassia.Interop;
using static Bassia.CliCommands.Results;

/// <summary>Commands over the monorepo as a whole: create or clone one, its status and settings, import and export, maintenance.</summary>
internal static class MonorepoCommands
{
	public static async Task<int> InitAsync(Invocation invocation)
	{
		var path = invocation.Get("path") ?? Environment.CurrentDirectory;
		using var monorepo = await Monorepo.InitAsync(path);
		var main = await monorepo.Repo.GetMainAsync();
		return Ok(invocation, $"Created a local monorepo in '{monorepo.Root}': blobs in .bassia/objects (local-disk stand-in for cloud storage), " +
			"heads, path index and leases in .bassia/state.db. Next: 'bassia import -git <url>' or 'bassia workspace create <name>'.",
			new Dictionary<string, object?>
			{
				["root"] = monorepo.Root,
				["kind"] = monorepo.Kind,
				["seq"] = main.Seq,
				["commit"] = main.CommitId.Hex
			});
	}

	public static async Task<int> CloneAsync(Invocation invocation)
	{
		var url = invocation.Require("from");
		var path = invocation.Get("path") ?? Path.Combine(Environment.CurrentDirectory, new Uri(url).Host.Split('.')[0]);
		using var monorepo = await Monorepo.CloneAsync(url, path, invocation.Get("token"));
		var main = await monorepo.Repo.GetMainAsync();
		return Ok(invocation, $"Cloned '{url}' into '{monorepo.Root}': nothing is downloaded until a workspace or a command reads it.",
			new Dictionary<string, object?>
			{
				["root"] = monorepo.Root,
				["kind"] = monorepo.Kind,
				["url"] = monorepo.Url,
				["seq"] = main.Seq,
				["commit"] = main.CommitId.Hex
			});
	}

	public static async Task<int> StatusAsync(Invocation invocation)
	{
		if (Monorepo.FindRoot(Environment.CurrentDirectory) is null)
		{
			return Ok(invocation, $"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia init' or 'bassia clone -from <url>'.",
				new Dictionary<string, object?> { ["in_monorepo"] = false });
		}

		using var monorepo = Monorepo.Current();
		var main = await monorepo.Repo.GetMainAsync();
		var workspaces = await monorepo.Repo.ListWorkspacesAsync();
		var current = await monorepo.CurrentWorkspaceAsync();
		var live = workspaces.Count(state => state.Record.Run is { } run && Agents.AgentRunner.IsAlive(run));
		var replay = workspaces.Count(state => state.Record.Status == WorkspaceStatus.NeedsReplay);
		var data = new Dictionary<string, object?>
		{
			["in_monorepo"] = true,
			["root"] = monorepo.Root,
			["kind"] = monorepo.Kind,
			["url"] = monorepo.Url,
			["user"] = monorepo.User,
			["seq"] = main.Seq,
			["commit"] = main.CommitId.Hex,
			["last_landing"] = Short(main.Commit.Intent),
			["open_workspaces"] = workspaces.Count,
			["live_runs"] = live,
			["needs_replay"] = replay,
			["workspace"] = current?.Name
		};
		if (workspaces.Count > 0)
		{
			data["table"] = Table(["WORKSPACE", "STATUS", "BASE", "RUN", "LAST LANDING"], workspaces.Select(state => (IReadOnlyList<string>)
			[
				state.Name, state.Record.Status, $"seq {state.Record.BaseSeq}", state.Record.Run is { } run ? (Agents.AgentRunner.IsAlive(run) ? "live" : run.Status) : "-",
				state.Record.LastLand?.Outcome ?? "-"
			]));
		}

		return Ok(invocation, $"{(monorepo.IsRemote ? $"Clone of {monorepo.Url}" : "Local monorepo")} at seq {main.Seq}: {workspaces.Count} open workspace(s), " +
			$"{live} live run(s), {replay} needing a replay.{(current is null ? "" : $" You are in workspace '{current.Name}'.")}", data);
	}

	public static Task<int> VersionAsync(Invocation invocation)
	{
		var version = typeof(MonorepoCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
		return Task.FromResult(Ok(invocation, $"bassia {version}", new Dictionary<string, object?>
		{
			["version"] = version,
			["runtime"] = Environment.Version.ToString(),
			["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription
		}));
	}

	// ----- config -----

	private static MonorepoConfig Config() => Monorepo.Current().Config;

	public static Task<int> ConfigListAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var entries = MonorepoConfig.Keys.Select(key =>
		{
			var (value, source) = monorepo.Config.Resolve(key.Key);
			return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["key"] = key.Key,
				["value"] = key.Key == "server.token" && value.Length > 0 ? "(set)" : value,
				["source"] = source,
				["default"] = key.Default,
				["environment"] = key.Environment,
				["description"] = key.Description
			};
		}).ToList();
		return Task.FromResult(Ok(invocation, $"{entries.Count} setting(s) in '{Path.Combine(monorepo.BassiaDir, MonorepoConfig.FileName)}'. " +
			"Repository policy (conflicts, leases, ignore rules) is versioned in .bassia.toml at the root of the tree.",
			new Dictionary<string, object?> { ["setting"] = entries }));
	}

	public static Task<int> ConfigGetAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var key = MonorepoConfig.Find(invocation.Require("key"));
		var (value, source) = monorepo.Config.Resolve(key.Key);
		return Task.FromResult(Ok(invocation, $"{key.Key} = {value} ({source})", new Dictionary<string, object?>
		{
			["key"] = key.Key,
			["value"] = value,
			["source"] = source,
			["description"] = key.Description
		}));
	}

	public static Task<int> ConfigSetAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var key = MonorepoConfig.Find(invocation.Require("key"));
		var value = invocation.Require("value");
		monorepo.Config.Set(key.Key, value);
		return Task.FromResult(Ok(invocation, $"Set {key.Key} = {value}.", new Dictionary<string, object?> { ["key"] = key.Key, ["value"] = value }));
	}

	public static Task<int> ConfigUnsetAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var key = MonorepoConfig.Find(invocation.Require("key"));
		monorepo.Config.Set(key.Key, null);
		return Task.FromResult(Ok(invocation, $"Unset {key.Key}; it is {monorepo.Config.Get(key.Key)} ({monorepo.Config.Resolve(key.Key).Source}) now.",
			new Dictionary<string, object?> { ["key"] = key.Key }));
	}

	// ----- import and export -----

	public static async Task<int> ImportAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var urls = invocation.List("git");
		var prefixes = invocation.List("prefix");
		if (prefixes.Count > 0 && prefixes.Count != urls.Count)
		{
			throw invocation.Usage("-prefix needs one folder per repository of -git.");
		}

		var sources = urls.Select((url, index) => new GitSource(url, PathNames.Normalize(prefixes.Count > 0 ? prefixes[index] : DefaultPrefix(url)))).ToList();
		if (sources.Select(source => source.Prefix).Distinct().Count() != sources.Count)
		{
			throw invocation.Usage("Two repositories would go into the same folder; name the folders with -prefix.");
		}

		var history = invocation.Has("history");
		var limit = invocation.Int("limit", 1000, 1);
		var importer = new GitImporter(monorepo) { Progress = line => Console.Error.WriteLine("bassia: " + line) };
		var reports = await importer.ImportAsync(sources, history, limit);
		var main = await monorepo.Repo.GetMainAsync();
		return Ok(invocation, $"Imported {reports.Count} repositor{(reports.Count == 1 ? "y" : "ies")} ({reports.Sum(report => report.Commits)} commit(s){(history ? "" : ", baselines")}); mainline is at seq {main.Seq}.",
			new Dictionary<string, object?>
			{
				["seq"] = main.Seq,
				["commit"] = main.CommitId.Hex,
				["repository"] = Tables(reports, report => new Dictionary<string, object?>
				{
					["url"] = report.Url,
					["prefix"] = report.Prefix,
					["commits"] = report.Commits,
					["files"] = report.Files,
					["bytes"] = report.Bytes,
					["skipped"] = report.Skipped.ToList()
				})
			});
	}

	private static string DefaultPrefix(string url)
	{
		var name = url.TrimEnd('/', '\\').Split('/', '\\', ':')[^1];
		return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
	}

	public static async Task<int> ExportAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var dir = Path.GetFullPath(invocation.Require("git"));
		var branch = invocation.Get("branch") ?? "main";
		var (from, to, commits) = await new GitExporter(monorepo).ExportAsync(dir, branch);
		return Ok(invocation, commits == 0
				? $"'{dir}' is up to date with mainline (seq {to})."
				: $"Exported seq {from + 1}..{to} as {commits} commit(s) to branch '{branch}' of '{dir}'.",
			new Dictionary<string, object?> { ["git"] = dir, ["branch"] = branch, ["from_seq"] = from, ["to_seq"] = to, ["commits"] = commits });
	}

	// ----- maintenance -----

	public static async Task<int> GcAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		if (monorepo.Maintenance is null)
		{
			return Fail(invocation, "Garbage collection runs where the blobs are: on the server ('bassia-server gc'), not in a clone.");
		}

		var grace = TimeSpan.FromHours(invocation.Int("grace-hours", monorepo.Config.GetInt("gc.grace_hours")));
		var retention = TimeSpan.FromDays(invocation.Int("retention-days", monorepo.Config.GetInt("gc.retention_days")));
		var report = await new GarbageCollector(monorepo.Repo, monorepo.Maintenance).CollectAsync(grace, retention, invocation.Has("dry-run"));
		return Ok(invocation, $"{(report.DryRun ? "Would sweep" : "Swept")} {report.Swept} of {report.Blobs} blob(s) ({Size(report.SweptBytes)}); {report.Live} live object(s) " +
			$"from {report.Roots} head(s); {report.Young} unreferenced blob(s) are younger than the grace period; {report.PrunedWorkspaces} closed workspace(s) pruned.",
			new Dictionary<string, object?>
			{
				["dry_run"] = report.DryRun,
				["roots"] = report.Roots,
				["live"] = report.Live,
				["blobs"] = report.Blobs,
				["swept"] = report.Swept,
				["swept_bytes"] = report.SweptBytes,
				["young"] = report.Young,
				["pruned_workspaces"] = report.PrunedWorkspaces
			});
	}

	public static async Task<int> ScrubAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		if (monorepo.Maintenance is null)
		{
			return Fail(invocation, "Scrubbing runs where the blobs are: on the server ('bassia-server scrub'), not in a clone.");
		}

		var sample = invocation.Has("sample") ? invocation.Int("sample", 100, 1) : (int?)null;
		var report = await new GarbageCollector(monorepo.Repo, monorepo.Maintenance).ScrubAsync(sample);
		var ok = report.Corrupt.Count == 0 && report.Unreadable.Count == 0;
		var data = new Dictionary<string, object?>
		{
			["checked"] = report.Checked,
			["corrupt"] = report.Corrupt.Select(id => id.Hex).ToList(),
			["unreadable"] = report.Unreadable.Select(id => id.Hex).ToList()
		};
		return ok
			? Ok(invocation, $"Checked {report.Checked} blob(s): every one matches its id.", data)
			: Fail(invocation, $"Checked {report.Checked} blob(s): {report.Corrupt.Count} corrupt, {report.Unreadable.Count} unreadable.", data);
	}
}
