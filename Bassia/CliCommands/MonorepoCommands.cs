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

	// ----- bassia config list | get | set -----

	public static Task<int> ConfigListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var entries = ConfigFile.Keys.Select(key => (Key: key, Set: ConfigFile.Get(monorepo.Root, key))).ToList();
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command, $"{entries.Count} setting(s) in '{ConfigFile.PathOf(monorepo.Root)}'.",
			new Dictionary<string, object?>
			{
				["path"] = ConfigFile.PathOf(monorepo.Root),
				["table"] = new TomlText(AsciiTable.Render(["KEY", "VALUE", "SOURCE"],
					entries.Select(entry => (IReadOnlyList<string>)[entry.Key.Key, entry.Set ?? entry.Key.Default, entry.Set is null ? "default" : "config"]), 72)),
				["setting"] = entries.Select(entry => (IReadOnlyDictionary<string, object?>)Setting(entry.Key, entry.Set)).ToList()
			}));
	}

	public static Task<int> ConfigGetAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var key = ConfigFile.Find(invocation.Require("key"));
		var set = ConfigFile.Get(monorepo.Root, key);
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command, $"{key.Key} = {set ?? key.Default}{(set is null ? " (default)" : "")}", Setting(key, set)));
	}

	public static async Task<int> ConfigSetAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var key = ConfigFile.Find(invocation.Require("key"));
		var value = invocation.Require("value");
		ConfigFile.Set(monorepo.Root, key, value);
		var commit = await CommitMetaRepoAsync(monorepo.Root, $"Set {key.Key} = {value}");
		return ProgramCli.WriteResult(true, invocation.Command, $"Set {key.Key} = {value}.", new Dictionary<string, object?>(Setting(key, value))
		{
			["meta_repo_commit"] = commit
		});
	}

	private static Dictionary<string, object?> Setting(ConfigKey key, string? set) => new()
	{
		["key"] = key.Key,
		["value"] = set ?? key.Default,
		["source"] = set is null ? "default" : "config",
		["default"] = key.Default,
		["description"] = key.Description
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
