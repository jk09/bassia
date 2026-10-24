namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Core;
using Bassia.Git;
using Bassia.Graph;

/// <summary><c>bassia component ...</c> and <c>bassia graph</c>: the registered components and their dependencies.</summary>
internal static class ComponentCommands
{
	// ----- bassia component list -----

	public static async Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var runs = await monorepo.RunRecords().ListLatestAsync();
		var statuses = await ComponentStatus.ReadAllAsync(monorepo, runs);
		var graph = new ComponentGraph(monorepo.Components);
		var live = LiveRunsPerComponent(monorepo, runs);

		var entries = statuses.Select(status => new Dictionary<string, object?>
		{
			["name"] = status.Name,
			["url"] = status.Definition.Url,
			["fork_of"] = status.Definition.ForkOf,
			["path"] = monorepo.SourceRepoDir(status.Name),
			["has_repo"] = status.HasRepo,
			["references"] = status.Definition.References.Select(Describe).ToList(),
			["referenced_by"] = graph.ReferrersOf(status.Name).Select(referrer => referrer.Name).ToList(),
			["annotated_tags"] = status.AnnotatedTags,
			["branches"] = status.Branches,
			["latest_tag"] = status.LatestTag,
			["runs"] = status.RecordedRuns,
			["live_runs"] = live.GetValueOrDefault(status.Name)
		}).ToList();

		return ProgramCli.WriteResult(true, invocation.Command, $"{entries.Count} component(s).", new Dictionary<string, object?>
		{
			["table"] = new TomlText(AsciiTable.Render(["NAME", "TAGS", "BRANCHES", "RUNS", "LIVE", "NEEDS", "USED BY"],
				statuses.Select(status => (IReadOnlyList<string>)
				[
					status.Name, status.AnnotatedTags.ToString(), status.Branches.ToString(), status.RecordedRuns.ToString(),
					live.GetValueOrDefault(status.Name).ToString(),
					Join(status.Definition.References.Select(Describe)), Join(graph.ReferrersOf(status.Name).Select(referrer => referrer.Name))
				]))),
			["component"] = entries
		});
	}

	// ----- bassia component add -----

	public static async Task<int> AddAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var url = invocation.Require("url");
		var name = invocation.Get("name") ?? DeriveComponentName(url);
		var references = ComponentsFile.ParseReferences(invocation.List("references"));
		if (invocation.Has("unwind"))
		{
			return await UnwindCommands.AddUnwoundAsync(invocation, monorepo, name, url, references);
		}

		var added = await monorepo.Graph().ImportAsync(name, url, references);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Added component '{name}' from '{url}'. Tag a baseline for runs with 'bassia component tag -name {name} -tag <tag>'.",
			new Dictionary<string, object?>
			{
				["name"] = name,
				["url"] = url,
				["path"] = added.Location,
				["references"] = references.Select(Describe).ToList(),
				["meta_repo_commit"] = added.Revision
			});
	}

	// ----- bassia component create -----

	public static async Task<int> CreateAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var name = invocation.Require("name");
		var references = ComponentsFile.ParseReferences(invocation.List("references"));
		var branch = invocation.Get("branch") ?? "main";
		var url = invocation.Get("url") ?? "";
		var created = await monorepo.Graph().CreateAsync(name, references, branch, url);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Created component '{name}' with an empty first commit on '{branch}'; select it for a run with -select {name}.",
			new Dictionary<string, object?>
			{
				["name"] = name,
				["url"] = url.Length == 0 ? null : url,
				["branch"] = branch,
				["path"] = created.Location,
				["references"] = references.Select(Describe).ToList(),
				["meta_repo_commit"] = created.Revision
			});
	}

	internal static string DeriveComponentName(string url)
	{
		var trimmed = url.Trim().TrimEnd('/', '\\');
		var lastSegment = trimmed.Split(['/', '\\', ':'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
		if (string.IsNullOrWhiteSpace(lastSegment))
		{
			throw new MonorepoException($"Could not derive a component name from '{url}'; give one with -name.");
		}

		return lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? lastSegment[..^4] : lastSegment;
	}

	// ----- bassia component show -----

	public static async Task<int> ShowAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var component = RequireComponent(monorepo, invocation.Require("name"));
		var graph = new ComponentGraph(monorepo.Components);
		var state = await monorepo.Graph().StateAsync(component.Name);
		var runs = (await monorepo.RunRecords().ListLatestAsync())
			.Where(run => run.Components.Any(entry => entry.Name == component.Name))
			.ToList();

		return ProgramCli.WriteResult(true, invocation.Command,
			$"Component '{component.Name}': {state.Branches.Count} branch(es), {state.Tags.Count} tag(s), {runs.Count} run(s).",
			new Dictionary<string, object?>
			{
				["name"] = component.Name,
				["url"] = component.Url,
				["fork_of"] = component.ForkOf,
				["fork_commit"] = component.ForkCommit,
				["path"] = state.Location,
				["has_repo"] = state.HasStore,
				["default_branch"] = state.DefaultBranch,
				["references"] = component.References.Select(Describe).ToList(),
				["referenced_by"] = graph.ReferrersOf(component.Name).Select(referrer => referrer.Name).ToList(),
				["closure"] = monorepo.Closure([component.Name]).Select(item => item.Name).ToList(),
				["graph"] = new TomlText(new ComponentGraph(monorepo.Closure([component.Name])).RenderText(ascii: true)),
				["branch"] = state.Branches.Select(item => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = item.Name,
					["commit"] = item.Commit,
					["subject"] = item.Subject
				}).ToList(),
				["tag"] = state.Tags.Select(item => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = item.Name,
					["annotated"] = item.Annotated,
					["commit"] = item.Commit,
					["subject"] = item.Subject
				}).ToList(),
				["run"] = runs.Select(run =>
				{
					var entry = run.Components.First(item => item.Name == component.Name);
					return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
					{
						["run_id"] = run.RunId,
						["status"] = run.Status,
						["created"] = run.Created,
						["commitish"] = entry.CommitIsh,
						["result_status"] = entry.ResultStatus.ToString().ToLowerInvariant(),
						["result_tag"] = entry.ResultTag
					};
				}).ToList()
			});
	}

	// ----- bassia component set -----

	public static async Task<int> SetAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var component = RequireComponent(monorepo, invocation.Require("name"));
		if (invocation.Has("references") == invocation.Has("clear-references"))
		{
			throw invocation.Usage("Give either -references or -clear-references.");
		}

		var references = invocation.Has("clear-references") ? [] : ComponentsFile.ParseReferences(invocation.List("references"));
		var graph = monorepo.Graph();
		var changed = await graph.SetReferencesAsync(component.Name, references);
		var described = references.Select(Describe).ToList();
		var commit = changed.Revision;
		var updated = graph.Registry;
		return ProgramCli.WriteResult(true, invocation.Command,
			described.Count == 0 ? $"Component '{component.Name}' now references nothing." : $"Component '{component.Name}' now references {string.Join(", ", described)}.",
			new Dictionary<string, object?>
			{
				["name"] = component.Name,
				["references"] = described,
				["closure"] = updated.Closure([component.Name]).Select(item => item.Name).ToList(),
				["meta_repo_commit"] = commit,
				["graph"] = new TomlText(new ComponentGraph(updated.Components).RenderText(ascii: true))
			});
	}

	// ----- bassia component remove -----

	public static async Task<int> RemoveAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var component = RequireComponent(monorepo, invocation.Require("name"));
		var removed = await monorepo.Graph().RemoveAsync(component.Name, invocation.Has("purge"));
		var (sourceDir, purged, commit) = (removed.Location, removed.Purged, removed.Revision);

		return ProgramCli.WriteResult(true, invocation.Command,
			purged
				? $"Component '{component.Name}' unregistered and its repository '{sourceDir}' deleted."
				: $"Component '{component.Name}' unregistered; its repository '{sourceDir}' was kept (add -purge to delete it).",
			new Dictionary<string, object?>
			{
				["name"] = component.Name,
				["path"] = sourceDir,
				["purged"] = purged,
				["meta_repo_commit"] = commit
			});
	}

	// ----- bassia component tag -----

	public static async Task<int> TagAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var component = RequireComponent(monorepo, invocation.Require("name"));
		var tag = invocation.Require("tag");
		RequireRepo(monorepo, component.Name);
		var tagged = await monorepo.Graph().TagAsync(component.Name, tag, invocation.Get("ref"), invocation.Get("message"));
		var (target, message, commit) = (tagged.Target, tagged.Message, tagged.Commit);

		return ProgramCli.WriteResult(true, invocation.Command,
			$"Tagged {component.Name}@{tag} at {commit[..10]}; select it for a run with -select {component.Name}@{tag}.",
			new Dictionary<string, object?>
			{
				["name"] = component.Name,
				["tag"] = tag,
				["ref"] = target,
				["commit"] = commit,
				["tag_message"] = message,
				["select"] = $"{component.Name}@{tag}"
			});
	}

	// ----- bassia component fork -----

	public static async Task<int> ForkAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var parent = RequireComponent(monorepo, invocation.Require("name"));
		var name = invocation.Require("as");
		var references = invocation.Has("references") ? ComponentsFile.ParseReferences(invocation.List("references")) : null;
		var url = invocation.Get("url") ?? "";
		RequireRepo(monorepo, parent.Name);
		var fork = await monorepo.Graph().ForkAsync(parent.Name, name, invocation.Get("ref"), references, url);
		var (commit, branch, forkDir, tags, meta) = (fork.Commit, fork.Branch, fork.Location, fork.Tags, fork.Revision ?? "");
		references = fork.Component.References;

		return ProgramCli.WriteResult(true, invocation.Command,
			$"Forked '{parent.Name}' as '{name}' at {commit[..10]} on '{branch}'; select it for a run with -select {name}@<tag> after tagging a baseline with 'bassia component tag -name {name} -tag <tag>'.",
			new Dictionary<string, object?>
			{
				["name"] = name,
				["fork_of"] = parent.Name,
				["fork_commit"] = commit,
				["branch"] = branch,
				["url"] = url,
				["path"] = forkDir,
				["tags"] = tags,
				["references"] = references.Select(Describe).ToList(),
				["meta_repo_commit"] = meta.Length > 0 ? meta : null
			});
	}

	// ----- bassia graph -----

	public static async Task<int> GraphAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var components = invocation.Get("name") is { } name
			? monorepo.Closure([RequireComponent(monorepo, name).Name])
			: monorepo.Components;
		var graph = new ComponentGraph(components);
		var format = (invocation.Get("format") ?? "board").ToLowerInvariant();

		string rendered;
		switch (format)
		{
			case "board":
				var runs = await monorepo.RunRecords().ListLatestAsync();
				var statuses = (await ComponentStatus.ReadAllAsync(monorepo, runs)).Where(status => components.Contains(status.Definition)).ToList();
				rendered = new ComponentBoard(statuses, graph, invocation.Int("width", 100, 30, 1000)).RenderAscii(LiveRunsPerComponent(monorepo, runs));
				break;
			case "tree":
				rendered = graph.RenderText(ascii: true);
				break;
			case "mermaid":
				rendered = graph.ToMermaidMarkdown();
				break;
			case "svg":
				rendered = graph.ToSvg();
				break;
			default:
				throw invocation.Usage($"Unknown -format '{format}'; expected board, tree, mermaid or svg.");
		}

		var output = invocation.Get("out");
		if (output is not null)
		{
			output = Path.GetFullPath(output);
			await File.WriteAllTextAsync(output, rendered);
		}

		return ProgramCli.WriteResult(true, invocation.Command,
			output is null ? $"Dependency graph of {components.Count} component(s) ({format})." : $"Dependency graph of {components.Count} component(s) written to '{output}' ({format}).",
			new Dictionary<string, object?>
			{
				["format"] = format,
				["out"] = output,
				["graph"] = output is null ? new TomlText(rendered) : null,
				["component"] = components.Select(component => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = component.Name,
					["references"] = component.References.Select(Describe).ToList(),
					["referenced_by"] = graph.ReferrersOf(component.Name).Select(referrer => referrer.Name).ToList()
				}).ToList()
			});
	}

	// ----- helpers -----

	internal static ComponentDefinition RequireComponent(Monorepo monorepo, string name) =>
		monorepo.FindComponent(name)
			?? throw new MonorepoException($"Component '{name}' is not registered. Registered: {Join(monorepo.Components.Select(component => component.Name))}.");

	internal static string RequireRepo(Monorepo monorepo, string name)
	{
		var sourceDir = monorepo.SourceRepoDir(name);
		return Directory.Exists(sourceDir) ? sourceDir : throw new MonorepoException($"Component '{name}' has no repository at '{sourceDir}'.");
	}

	/// <summary>How many live runs work on each component: recorded runs still <c>started</c> whose process is alive.</summary>
	internal static IReadOnlyDictionary<string, int> LiveRunsPerComponent(Monorepo monorepo, IReadOnlyList<RunMetadata> runs)
	{
		var jobs = new JobRegistry(monorepo);
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var run in runs.Where(run => run.Status == "started" && JobRegistry.IsAlive(jobs.Find(run.RunId))))
		{
			foreach (var component in run.Components)
			{
				counts[component.Name] = counts.GetValueOrDefault(component.Name) + 1;
			}
		}

		return counts;
	}

	/// <summary><c>lib</c>, or <c>lib:vendor/lib</c> when it is nested at another path - the form <c>-references</c> takes.</summary>
	internal static string Describe(ComponentReference reference) => MonorepoGraph.Describe(reference);

	private static string Join(IEnumerable<string> values)
	{
		var joined = string.Join(",", values);
		return joined.Length == 0 ? "-" : joined;
	}
}
