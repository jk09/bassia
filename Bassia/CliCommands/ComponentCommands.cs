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
		var violations = monorepo.Registry.Violations();

		var entries = statuses.Select(status => new Dictionary<string, object?>
		{
			["name"] = status.Name,
			["url"] = status.Definition.Url,
			["fork_of"] = status.Definition.ForkOf,
			["path"] = monorepo.SourceRepoDir(status.Name),
			["has_repo"] = status.HasRepo,
			["references"] = status.Definition.References.Select(Describe).ToList(),
			["tags"] = status.Definition.Tags,
			["visibility"] = status.Definition.Visibility,
			["owners"] = status.Definition.Owners,
			["exports"] = status.Definition.Exports,
			["referenced_by"] = graph.ReferrersOf(status.Name).Select(referrer => referrer.Name).ToList(),
			["annotated_tags"] = status.AnnotatedTags,
			["branches"] = status.Branches,
			["latest_tag"] = status.LatestTag,
			["runs"] = status.RecordedRuns,
			["live_runs"] = live.GetValueOrDefault(status.Name)
		}).ToList();

		return ProgramCli.WriteResult(true, invocation.Command, $"{entries.Count} component(s){ViolationSuffix(violations)}.", new Dictionary<string, object?>
		{
			["table"] = new TomlText(AsciiTable.Render(["NAME", "ARCH TAGS", "TAGS", "BRANCHES", "RUNS", "LIVE", "NEEDS", "USED BY"],
				statuses.Select(status => (IReadOnlyList<string>)
				[
					status.Name, Join(status.Definition.Tags), status.AnnotatedTags.ToString(), status.Branches.ToString(), status.RecordedRuns.ToString(),
					live.GetValueOrDefault(status.Name).ToString(),
					Join(status.Definition.References.Select(Describe)), Join(graph.ReferrersOf(status.Name).Select(referrer => referrer.Name))
				]))),
			["component"] = entries,
			["warnings"] = Warnings(violations)
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
			if (invocation.Has("tags") || invocation.Has("visibility") || invocation.Has("owners"))
			{
				throw invocation.Usage("-tags, -visibility and -owners cannot be combined with -unwind; set them afterwards with 'bassia component set'.");
			}

			return await UnwindCommands.AddUnwoundAsync(invocation, monorepo, name, url, references);
		}

		var graph = monorepo.Graph();
		var added = await graph.ImportAsync(name, url, references, Attributes(invocation, ComponentAttributes.None));
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Added component '{name}' from '{url}'. Tag a baseline for runs with 'bassia component tag -name {name} -tag <tag>'.",
			new Dictionary<string, object?>
			{
				["name"] = name,
				["url"] = url,
				["path"] = added.Location,
				["references"] = references.Select(Describe).ToList(),
				["tags"] = added.Component.Tags,
				["visibility"] = added.Component.Visibility,
				["owners"] = added.Component.Owners,
				["exports"] = added.Component.Exports,
				["meta_repo_commit"] = added.Revision,
				["warnings"] = Warnings(graph.Registry.Violations())
			});
	}

	// ----- bassia component create -----

	public static async Task<int> CreateAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var name = invocation.Require("name");
		var template = invocation.Get("template") is { } templateName ? ComponentTemplate.Load(monorepo.Root, templateName) : null;
		var references = invocation.Has("references") || template is null
			? ComponentsFile.ParseReferences(invocation.List("references"))
			: template.References;
		var branch = invocation.Get("branch") ?? "main";
		var url = invocation.Get("url") ?? "";
		var graph = monorepo.Graph();
		var created = await graph.CreateAsync(name, references, branch, url, Attributes(invocation, template?.Attributes ?? ComponentAttributes.None),
			template?.FilesFor(name), template is null ? null : $"Apply template '{template.Name}'");
		return ProgramCli.WriteResult(true, invocation.Command,
			template is null
				? $"Created component '{name}' with an empty first commit on '{branch}'; select it for a run with -select {name}."
				: $"Created component '{name}' from template '{template.Name}' ({template.Files.Count} file(s)) on '{branch}'; select it for a run with -select {name}.",
			new Dictionary<string, object?>
			{
				["name"] = name,
				["url"] = url.Length == 0 ? null : url,
				["branch"] = branch,
				["template"] = template?.Name,
				["files"] = template?.FilesFor(name).Keys.ToList(),
				["path"] = created.Location,
				["references"] = references.Select(Describe).ToList(),
				["tags"] = created.Component.Tags,
				["visibility"] = created.Component.Visibility,
				["owners"] = created.Component.Owners,
				["exports"] = created.Component.Exports,
				["meta_repo_commit"] = created.Revision,
				["warnings"] = Warnings(graph.Registry.Violations())
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

	// ----- bassia template list -----

	public static Task<int> TemplateListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var templates = ComponentTemplate.List(monorepo.Root);
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command,
			templates.Count == 0
				? $"No templates; add one as {ComponentTemplate.TemplatesDir(monorepo.Root)}/<name>/{ComponentTemplate.FileName} with its files under files/."
				: $"{templates.Count} template(s): {string.Join(", ", templates.Select(template => template.Name))}.",
			new Dictionary<string, object?>
			{
				["path"] = ComponentTemplate.TemplatesDir(monorepo.Root),
				["template"] = templates.Select(template => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = template.Name,
					["description"] = template.Description,
					["tags"] = template.Attributes.Tags,
					["visibility"] = template.Attributes.Visibility,
					["exports"] = template.Attributes.Exports,
					["owners"] = template.Attributes.Owners,
					["references"] = template.References.Select(Describe).ToList(),
					["files"] = template.Files
				}).ToList()
			}));
	}

	// ----- bassia component show -----

	public static async Task<int> ShowAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var component = RequireComponent(monorepo, invocation.Require("name"));
		var graph = new ComponentGraph(monorepo.Components);
		var state = await monorepo.Graph().StateAsync(component.Name);
		var violations = monorepo.Registry.Violations().Where(violation => violation.From == component.Name || violation.To == component.Name).ToList();
		var runs = (await monorepo.RunRecords().ListLatestAsync())
			.Where(run => run.Components.Any(entry => entry.Name == component.Name))
			.ToList();

		return ProgramCli.WriteResult(true, invocation.Command,
			$"Component '{component.Name}': {state.Branches.Count} branch(es), {state.Tags.Count} tag(s), {runs.Count} run(s){ViolationSuffix(violations)}.",
			new Dictionary<string, object?>
			{
				["name"] = component.Name,
				["url"] = component.Url,
				["fork_of"] = component.ForkOf,
				["fork_commit"] = component.ForkCommit,
				["path"] = state.Location,
				["has_repo"] = state.HasStore,
				["default_branch"] = state.DefaultBranch,
				["tags"] = component.Tags,
				["visibility"] = component.Visibility,
				["owners"] = component.Owners,
				["exports"] = component.Exports,
				["references"] = component.References.Select(Describe).ToList(),
				["referenced_by"] = graph.ReferrersOf(component.Name).Select(referrer => referrer.Name).ToList(),
				["warnings"] = Warnings(violations),
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
		Exclusive(invocation, "references", "clear-references");
		Exclusive(invocation, "tags", "clear-tags");
		Exclusive(invocation, "owners", "clear-owners");
		Exclusive(invocation, "exports", "clear-exports");
		if (new[] { invocation.Has("visibility"), invocation.Has("public"), invocation.Has("private") }.Count(given => given) > 1)
		{
			throw invocation.Usage("Give only one of -visibility, -public and -private.");
		}

		string[] changes = ["references", "clear-references", "tags", "clear-tags", "visibility", "public", "private", "owners", "clear-owners", "exports", "clear-exports"];
		if (!changes.Any(invocation.Has))
		{
			throw invocation.Usage("Give what to change: -references or -clear-references, -tags or -clear-tags, -visibility, -public or -private, " +
				"-owners or -clear-owners, -exports or -clear-exports.");
		}

		var references = invocation.Has("clear-references") ? []
			: invocation.Has("references") ? ComponentsFile.ParseReferences(invocation.List("references")) : null;
		var tags = invocation.Has("clear-tags") ? [] : invocation.Has("tags") ? invocation.List("tags") : null;
		var owners = invocation.Has("clear-owners") ? [] : invocation.Has("owners") ? invocation.List("owners") : null;
		var setVisibility = invocation.Has("visibility") || invocation.Has("public") || invocation.Has("private");
		IReadOnlyList<string>? visibility = invocation.Has("private") ? [] : invocation.Has("visibility") ? invocation.List("visibility") : null;
		var graph = monorepo.Graph();
		var setExports = invocation.Has("exports") || invocation.Has("clear-exports");
		var exports = invocation.Has("exports") ? invocation.List("exports") : null;
		var changed = await graph.SetAttributesAsync(component.Name, tags, setVisibility, visibility, owners, references, setExports, exports);
		var updated = graph.Registry;
		var now = updated.Require(component.Name);
		var described = now.References.Select(Describe).ToList();
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Component '{now.Name}' references {(described.Count == 0 ? "nothing" : string.Join(", ", described))}; " +
			$"tags {(now.Tags.Count == 0 ? "none" : string.Join(", ", now.Tags))}; {DescribeVisibility(now.Visibility)}.",
			new Dictionary<string, object?>
			{
				["name"] = now.Name,
				["references"] = described,
				["tags"] = now.Tags,
				["visibility"] = now.Visibility,
				["owners"] = now.Owners,
				["exports"] = now.Exports,
				["closure"] = updated.Closure([now.Name]).Select(item => item.Name).ToList(),
				["meta_repo_commit"] = changed.Revision,
				["graph"] = new TomlText(new ComponentGraph(updated.Components).RenderText(ascii: true)),
				["warnings"] = Warnings(updated.Violations())
			});
	}

	private static void Exclusive(Invocation invocation, string set, string clear)
	{
		if (invocation.Has(set) && invocation.Has(clear))
		{
			throw invocation.Usage($"Give either -{set} or -{clear}, not both.");
		}
	}

	internal static string DescribeVisibility(IReadOnlyList<string>? visibility) =>
		visibility is null ? "public" : visibility.Count == 0 ? "private" : $"visible to {string.Join(", ", visibility)}";

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
		var graph = monorepo.Graph();
		var fork = await graph.ForkAsync(parent.Name, name, invocation.Get("ref"), references, url, Attributes(invocation, ComponentAttributes.Of(parent)));
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
				["tags"] = fork.Component.Tags,
				["visibility"] = fork.Component.Visibility,
				["owners"] = fork.Component.Owners,
				["exports"] = fork.Component.Exports,
				["meta_repo_commit"] = meta.Length > 0 ? meta : null,
				["warnings"] = Warnings(graph.Registry.Violations())
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

		var names = components.Select(component => component.Name).ToHashSet(StringComparer.Ordinal);
		var violations = monorepo.Registry.Violations().Where(violation => names.Contains(violation.From)).ToList();
		return ProgramCli.WriteResult(true, invocation.Command,
			(output is null ? $"Dependency graph of {components.Count} component(s) ({format})" : $"Dependency graph of {components.Count} component(s) written to '{output}' ({format})") +
				ViolationSuffix(violations) + ".",
			new Dictionary<string, object?>
			{
				["format"] = format,
				["out"] = output,
				["graph"] = output is null ? new TomlText(rendered) : null,
				["component"] = components.Select(component => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = component.Name,
					["tags"] = component.Tags,
					["references"] = component.References.Select(Describe).ToList(),
					["referenced_by"] = graph.ReferrersOf(component.Name).Select(referrer => referrer.Name).ToList()
				}).ToList(),
				["violations"] = violations.Count,
				["warnings"] = Warnings(violations)
			});
	}

	// ----- helpers -----

	/// <summary>The <c>-tags</c>, <c>-visibility</c> and <c>-owners</c> given, each falling back to <paramref name="defaults"/>.</summary>
	internal static ComponentAttributes Attributes(Invocation invocation, ComponentAttributes defaults) => new(
		invocation.Has("tags") ? invocation.List("tags") : defaults.Tags,
		invocation.Has("visibility") ? invocation.List("visibility") : defaults.Visibility,
		invocation.Has("owners") ? invocation.List("owners") : defaults.Owners,
		invocation.Has("exports") ? invocation.List("exports") : defaults.Exports);

	/// <summary>The boundary violations as the <c>warnings</c> of a result, or null (no key) when there are none.</summary>
	internal static IReadOnlyList<string>? Warnings(IReadOnlyList<BoundaryViolation> violations) =>
		violations.Count == 0 ? null : violations.Select(violation => violation.Message).ToList();

	internal static string ViolationSuffix(IReadOnlyList<BoundaryViolation> violations) =>
		violations.Count == 0 ? "" : $"; {violations.Count} boundary violation(s) - see 'bassia boundary check'";

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
