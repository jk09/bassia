namespace Bassia;

using Bassia.Storage;
using System.Reflection;
using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Core;
using Bassia.Git;
using Bassia.Graph;
using Bassia.Integration;
using Bassia.Locks;

/// <summary><c>bassia status</c>, <c>bassia config ...</c> and <c>bassia version</c>: the monorepo as a whole.</summary>
internal static class MonorepoCommands
{
	// ----- bassia status -----

	public static async Task<int> StatusAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var store = monorepo.RunRecords();
		var runs = await store.ListLatestAsync();
		var integrations = await monorepo.Integrations().ListLatestAsync();
		var jobs = monorepo.Jobs;
		var liveRuns = (await jobs.ListAsync("run")).Where(jobs.IsAlive).Select(job => job.Id).Distinct().ToList();
		var liveIntegrations = (await jobs.ListAsync("integration")).Where(jobs.IsAlive).Select(job => job.Id).Distinct().ToList();

		// Hand edits not committed yet exist only where the meta-repo is a working tree on this machine.
		var changed = monorepo.Backend is LocalBackend
			? (await GitClient.In(monorepo.MetaRepoDir).RunAsync(["status", "--porcelain"])).Output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList()
			: [];
		var head = (await monorepo.Backend.Meta.LogAsync(new Storage.LogQuery(Limit: 1))).FirstOrDefault();

		var graph = new ComponentGraph(monorepo.Components);
		var locks = monorepo.Locks;
		var entries = (await locks.ReadAllAsync());
		var held = entries.Where(entry => locks.StateOf(entry) == LockState.Live).Sum(entry => entry.Holds.Count);
		var waiting = entries.Count(entry => entry.Waits.Any(wait => locks.IsWaiting(entry, wait)));
		var violations = monorepo.Registry.Violations();
		return ProgramCli.WriteResult(true, invocation.Command,
			$"{monorepo.Components.Count} component(s), {runs.Count} recorded run(s) ({liveRuns.Count} live), {integrations.Count} integration(s)" +
			(held + waiting > 0 ? $", {held} path(s) held by live runs, {waiting} run(s) waiting" : "") + ComponentCommands.ViolationSuffix(violations) + ".",
			new Dictionary<string, object?>
			{
				["root"] = monorepo.Root,
				["meta_repo"] = monorepo.MetaRepoDir,
				["meta_repo_head"] = head is null ? null : $"{head.Commit.Id[..Math.Min(7, head.Commit.Id.Length)]} {head.Commit.Subject}",
				["meta_repo_clean"] = changed.Count == 0,
				["meta_repo_changes"] = changed,
				["workspace"] = monorepo.WorkspaceDir,
				["runs_repo"] = monorepo.RunsRepoDir,
				["components"] = monorepo.Components.Count,
				["component_names"] = monorepo.Components.Select(component => component.Name).ToList(),
				["live_runs"] = liveRuns,
				["live_integrations"] = liveIntegrations,
				["graph"] = new TomlText(graph.RenderText(ascii: true)),
				["boundaries"] = monorepo.Registry.Boundaries.Count,
				["boundary_violations"] = violations.Count,
				["runs"] = CountByStatus(runs.Select(run => run.Status), runs.Count),
				["integrations"] = CountByStatus(integrations.Select(record => record.Status), integrations.Count),
				["locks"] = new Dictionary<string, object?>
				{
					["live"] = held,
					["released"] = entries.Where(entry => locks.StateOf(entry) == LockState.Released).Sum(entry => entry.Holds.Count),
					["expired"] = entries.Where(entry => locks.StateOf(entry) == LockState.Expired).Sum(entry => entry.Holds.Count),
					["waiting"] = waiting,
					["conflicts"] = runs.Sum(run => run.Components.Sum(component => component.LockConflicts.Count))
				}
			});
	}

	private static IReadOnlyDictionary<string, object?> CountByStatus(IEnumerable<string> statuses, int total)
	{
		var counts = new Dictionary<string, object?> { ["total"] = total };
		foreach (var group in statuses.GroupBy(status => status).OrderBy(group => group.Key, StringComparer.Ordinal))
		{
			counts[group.Key] = group.Count();
		}

		return counts;
	}

	// ----- bassia config list | get | set | unset -----

	public static Task<int> ConfigListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var config = monorepo.Config;
		var entries = ConfigFile.Keys.Concat(config.ComponentKeys()).Select(config.Resolve).ToList();
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command,
			$"{entries.Count} setting(s): {entries.Count(entry => entry.Layer == ConfigLayer.User)} from the user layer, " +
			$"{entries.Count(entry => entry.Layer == ConfigLayer.Monorepo)} from the monorepo, the rest default.",
			new Dictionary<string, object?>
			{
				["path"] = monorepo.Backend.Config.LocationOf(ConfigLayer.Monorepo),
				["user_path"] = monorepo.Backend.Config.LocationOf(ConfigLayer.User),
				["table"] = new TomlText(AsciiTable.Render(["KEY", "VALUE", "SOURCE"],
					entries.Select(entry => (IReadOnlyList<string>)[entry.Key.Key, entry.Value, entry.Source]), 72)),
				["setting"] = entries.Select(entry => (IReadOnlyDictionary<string, object?>)Setting(monorepo, entry)).ToList()
			}));
	}

	public static Task<int> ConfigGetAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var value = monorepo.Config.Resolve(ConfigFile.Find(invocation.Require("key")));
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command, $"{value.Key.Key} = {value.Value} ({value.Source})", Setting(monorepo, value)));
	}

	public static async Task<int> ConfigSetAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var key = ConfigKeyFor(monorepo, invocation.Require("key"));
		var layer = invocation.Has("user") ? ConfigLayer.User : ConfigLayer.Monorepo;
		var value = ConfigFile.Normalize(key, invocation.Require("value"));
		var commit = await monorepo.Backend.Config.SetAsync(key, value, layer);
		var reloaded = Monorepo.Load(monorepo.Root);
		return ProgramCli.WriteResult(true, invocation.Command, $"Set {key.Key} = {value} in the {Layer(layer)} layer.",
			new Dictionary<string, object?>(Setting(reloaded, reloaded.Config.Resolve(key))) { ["meta_repo_commit"] = commit });
	}

	public static async Task<int> ConfigUnsetAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var key = ConfigFile.Find(invocation.Require("key"));
		var layer = invocation.Has("user") ? ConfigLayer.User : ConfigLayer.Monorepo;
		var (removed, commit) = await monorepo.Backend.Config.UnsetAsync(key, layer);
		var reloaded = Monorepo.Load(monorepo.Root);
		var now = reloaded.Config.Resolve(key);
		return ProgramCli.WriteResult(true, invocation.Command,
			removed ? $"Unset {key.Key} in the {Layer(layer)} layer; it is now {now.Value} ({now.Source})." : $"{key.Key} was not set in the {Layer(layer)} layer; nothing changed.",
			new Dictionary<string, object?>(Setting(reloaded, now)) { ["removed"] = removed, ["meta_repo_commit"] = commit });
	}

	private static string Layer(ConfigLayer layer) => layer == ConfigLayer.User ? "user" : "monorepo";

	/// <summary>A key to write; a per-component key must name a registered component.</summary>
	private static ConfigKey ConfigKeyFor(Monorepo monorepo, string name)
	{
		var key = ConfigFile.Find(name);
		return key.Component is null || monorepo.FindComponent(key.Component) is not null
			? key
			: throw new MonorepoException($"'{key.Component}' in {key.Key} is not a registered component.");
	}

	private static Dictionary<string, object?> Setting(Monorepo monorepo, ConfigValue value) => new()
	{
		["key"] = value.Key.Key,
		["value"] = value.Value,
		["source"] = value.Source,
		["layer"] = value.Layer == ConfigLayer.Default ? "default" : Layer(value.Layer),
		["monorepo_value"] = monorepo.Config.Raw(ConfigLayer.Monorepo, value.Key),
		["user_value"] = monorepo.Config.Raw(ConfigLayer.User, value.Key),
		["default"] = value.Key.Default,
		["allowed"] = value.Key.Allowed,
		["description"] = value.Key.Description
	};


	// ----- bassia version -----

	public static Task<int> VersionAsync(Invocation invocation)
	{
		var assembly = typeof(MonorepoCommands).Assembly;
		var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString() ?? "unknown";
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command, $"bassia {version}", new Dictionary<string, object?>
		{
			["version"] = version,
			["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
			["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription
		}));
	}
}
