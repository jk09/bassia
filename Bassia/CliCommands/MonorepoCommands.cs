namespace Bassia;

using System.Reflection;
using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Graph;
using Bassia.Integration;

/// <summary><c>bassia status</c>, <c>bassia config ...</c> and <c>bassia version</c>: the monorepo as a whole.</summary>
internal static class MonorepoCommands
{
	// ----- bassia status -----

	public static async Task<int> StatusAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var git = new GitClient(monorepo.Root);
		var store = new RunMetadataStore(git, monorepo.RunsRepoDir);
		var runs = await store.ListLatestAsync();
		var integrations = await new IntegrationStore(store).ListLatestAsync();
		var jobs = new JobRegistry(monorepo);
		var liveRuns = jobs.List("run").Where(JobRegistry.IsAlive).Select(job => job.Id).Distinct().ToList();
		var liveIntegrations = jobs.List("integration").Where(JobRegistry.IsAlive).Select(job => job.Id).Distinct().ToList();

		var changes = await GitClient.In(monorepo.MetaRepoDir).RunAsync(["status", "--porcelain"]);
		var changed = changes.Output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
		var head = await GitClient.In(monorepo.MetaRepoDir).RunAsync(["log", "-1", "--format=%h %s"]);

		var graph = new ComponentGraph(monorepo.Components);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"{monorepo.Components.Count} component(s), {runs.Count} recorded run(s) ({liveRuns.Count} live), {integrations.Count} integration(s).",
			new Dictionary<string, object?>
			{
				["root"] = monorepo.Root,
				["meta_repo"] = monorepo.MetaRepoDir,
				["meta_repo_head"] = head.ExitCode == 0 ? head.Output.Trim() : null,
				["meta_repo_clean"] = changed.Count == 0,
				["meta_repo_changes"] = changed,
				["workspace"] = monorepo.WorkspaceDir,
				["runs_repo"] = monorepo.RunsRepoDir,
				["components"] = monorepo.Components.Count,
				["component_names"] = monorepo.Components.Select(component => component.Name).ToList(),
				["live_runs"] = liveRuns,
				["live_integrations"] = liveIntegrations,
				["graph"] = new TomlText(graph.RenderText(ascii: true)),
				["runs"] = CountByStatus(runs.Select(run => run.Status), runs.Count),
				["integrations"] = CountByStatus(integrations.Select(record => record.Status), integrations.Count)
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
				["path"] = ConfigFile.PathOf(monorepo.Root),
				["user_path"] = ConfigFile.PathOf(monorepo.Root, ConfigLayer.User),
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
		ConfigFile.Set(monorepo.Root, key, value, layer);
		var commit = layer == ConfigLayer.Monorepo ? await CommitMetaRepoAsync(monorepo.Root, $"Set {key.Key} = {value}") : null;
		var reloaded = Monorepo.Load(monorepo.Root);
		return ProgramCli.WriteResult(true, invocation.Command, $"Set {key.Key} = {value} in the {Layer(layer)} layer.",
			new Dictionary<string, object?>(Setting(reloaded, reloaded.Config.Resolve(key))) { ["meta_repo_commit"] = commit });
	}

	public static async Task<int> ConfigUnsetAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var key = ConfigFile.Find(invocation.Require("key"));
		var layer = invocation.Has("user") ? ConfigLayer.User : ConfigLayer.Monorepo;
		var removed = ConfigFile.Unset(monorepo.Root, key, layer);
		var commit = removed && layer == ConfigLayer.Monorepo ? await CommitMetaRepoAsync(monorepo.Root, $"Unset {key.Key}") : null;
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

	/// <summary>Commits every change in the meta-repo and returns the new commit, or null when nothing changed.</summary>
	internal static async Task<string?> CommitMetaRepoAsync(string root, string message)
	{
		var metaRepo = GitClient.In(Path.Combine(root, Monorepo.MetaRepoFolderName));
		var status = await metaRepo.RunOrThrowAsync(["status", "--porcelain"]);
		if (status.Length == 0)
		{
			return null;
		}

		var result = await metaRepo.CommitAllAsync(message);
		if (result.ExitCode != 0)
		{
			throw new GitException($"git commit failed in the meta-repo: {result.Error.Trim()}");
		}

		return await metaRepo.RunOrThrowAsync(["rev-parse", "HEAD"]);
	}

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
