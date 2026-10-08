namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Graph;

/// <summary><c>bassia component ...</c> and <c>bassia graph</c>: the registered components and their dependencies.</summary>
internal static class ComponentCommands
{
	// ----- bassia component list -----

	public static async Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var runs = await new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir).ListLatestAsync();
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
		var root = Monorepo.FindRoot(Environment.CurrentDirectory)
			?? throw new MonorepoException($"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia init' first.");
		var url = invocation.Require("url");
		var name = invocation.Get("name") ?? DeriveComponentName(url);
		var references = ComponentsFile.ParseReferences(invocation.List("references"));
		var monorepo = Monorepo.Load(root);

		if (monorepo.FindComponent(name) is not null)
		{
			throw new MonorepoException($"Component '{name}' is already registered.");
		}

		var componentDir = monorepo.SourceRepoDir(name);
		if (Directory.Exists(componentDir) || File.Exists(componentDir))
		{
			throw new MonorepoException($"'{componentDir}' already exists; choose another name with -name.");
		}

		foreach (var reference in references.Where(reference => monorepo.FindComponent(reference.Name) is null))
		{
			throw new MonorepoException($"-references names '{reference.Name}', which is not registered. Add it first.");
		}

		var clone = await new GitClient(root).RunAsync(["clone", "--bare", url, Path.Combine(componentDir, ".git")]);
		if (clone.ExitCode != 0)
		{
			if (Directory.Exists(componentDir))
			{
				Directory.Delete(componentDir, recursive: true);
			}

			throw new GitException($"git clone failed: {clone.Error.Trim()}");
		}

		if (invocation.Has("unwind"))
		{
			return await UnwindCommands.AddUnwoundAsync(invocation, monorepo, name, url, componentDir, references);
		}

		ComponentsFile.Add(root, name, url, references);
		var commit = await MonorepoCommands.CommitMetaRepoAsync(root, $"Add component '{name}' from '{url}'");
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Added component '{name}' from '{url}'. Tag a baseline for runs with 'bassia component tag -name {name} -tag <tag>'.",
			new Dictionary<string, object?>
			{
				["name"] = name,
				["url"] = url,
				["path"] = componentDir,
				["references"] = references.Select(Describe).ToList(),
				["meta_repo_commit"] = commit
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
		var sourceDir = monorepo.SourceRepoDir(component.Name);
		var hasRepo = Directory.Exists(sourceDir);
		var refs = hasRepo ? await GitRef.ListAsync(GitClient.In(sourceDir)) : [];
		var defaultBranch = hasRepo ? await GitClient.In(sourceDir).RunAsync(["symbolic-ref", "--short", "HEAD"]) : null;
		var runs = (await new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir).ListLatestAsync())
			.Where(run => run.Components.Any(entry => entry.Name == component.Name))
			.ToList();

		return ProgramCli.WriteResult(true, invocation.Command,
			$"Component '{component.Name}': {refs.Count(item => item.Kind == GitRefKind.Branch)} branch(es), {refs.Count(item => item.IsTag)} tag(s), {runs.Count} run(s).",
			new Dictionary<string, object?>
			{
				["name"] = component.Name,
				["url"] = component.Url,
				["fork_of"] = component.ForkOf,
				["fork_commit"] = component.ForkCommit,
				["path"] = sourceDir,
				["has_repo"] = hasRepo,
				["default_branch"] = defaultBranch is { ExitCode: 0 } ? defaultBranch.Output.Trim() : null,
				["references"] = component.References.Select(Describe).ToList(),
				["referenced_by"] = graph.ReferrersOf(component.Name).Select(referrer => referrer.Name).ToList(),
				["closure"] = monorepo.Closure([component.Name]).Select(item => item.Name).ToList(),
				["graph"] = new TomlText(new ComponentGraph(monorepo.Closure([component.Name])).RenderText(ascii: true)),
				["branch"] = refs.Where(item => item.Kind == GitRefKind.Branch).Select(item => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = item.Name,
					["commit"] = item.Commit,
					["subject"] = item.Subject
				}).ToList(),
				["tag"] = refs.Where(item => item.IsTag).Select(item => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = item.Name,
					["annotated"] = item.Kind == GitRefKind.AnnotatedTag,
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
		ComponentsFile.SetReferences(monorepo.Root, component.Name, references);
		var described = references.Select(Describe).ToList();
		var commit = await MonorepoCommands.CommitMetaRepoAsync(monorepo.Root,
			described.Count == 0 ? $"Component '{component.Name}' references nothing" : $"Component '{component.Name}' references {string.Join(", ", described)}");
		var updated = Monorepo.Load(monorepo.Root);
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
		var referrers = new ComponentGraph(monorepo.Components).ReferrersOf(component.Name);
		if (referrers.Count > 0)
		{
			throw new MonorepoException(
				$"Component '{component.Name}' is referenced by {string.Join(", ", referrers.Select(referrer => referrer.Name))}; " +
				$"remove those references first with 'bassia component set -name <component> -references ...'.");
		}

		ComponentsFile.Remove(monorepo.Root, component.Name);
		var commit = await MonorepoCommands.CommitMetaRepoAsync(monorepo.Root, $"Remove component '{component.Name}'");
		var sourceDir = monorepo.SourceRepoDir(component.Name);
		var purged = false;
		if (invocation.Has("purge") && Directory.Exists(sourceDir))
		{
			DeleteDirectory(sourceDir);
			purged = true;
		}

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
		var source = GitClient.In(RequireRepo(monorepo, component.Name));
		var target = invocation.Get("ref") ?? "HEAD";
		var commit = await source.RunAsync(["rev-parse", "--verify", "--quiet", $"{target}^{{commit}}"]);
		if (commit.ExitCode != 0)
		{
			throw new GitException($"'{target}' does not name a commit in component '{component.Name}'.");
		}

		var message = invocation.Get("message") ?? $"{tag}: baseline of '{component.Name}'";
		var result = await source.RunAsync(["tag", "-a", tag, "-m", message, commit.Output.Trim()]);
		if (result.ExitCode != 0)
		{
			throw new GitException($"git tag failed in component '{component.Name}': {result.Error.Trim()}");
		}

		return ProgramCli.WriteResult(true, invocation.Command,
			$"Tagged {component.Name}@{tag} at {commit.Output.Trim()[..10]}; select it for a run with -select {component.Name}@{tag}.",
			new Dictionary<string, object?>
			{
				["name"] = component.Name,
				["tag"] = tag,
				["ref"] = target,
				["commit"] = commit.Output.Trim(),
				["tag_message"] = message,
				["select"] = $"{component.Name}@{tag}"
			});
	}

	// ----- bassia component fork -----

	/// <summary>Run and integration tags belong to the parent's runs; a fork starts without them.</summary>
	private static readonly string[] ForeignTagPrefixes = ["agent-run/", "integration/"];

	public static async Task<int> ForkAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var parent = RequireComponent(monorepo, invocation.Require("name"));
		var name = invocation.Require("as");
		if (name.StartsWith('.') || name.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or ':'))
		{
			throw new MonorepoException($"'{name}' is not a valid component name: no whitespace, path separators or leading dot.");
		}

		if (monorepo.FindComponent(name) is not null)
		{
			throw new MonorepoException($"Component '{name}' is already registered.");
		}

		var forkDir = monorepo.SourceRepoDir(name);
		if (Directory.Exists(forkDir) || File.Exists(forkDir))
		{
			throw new MonorepoException($"'{forkDir}' already exists; choose another name with -as.");
		}

		var references = invocation.Has("references") ? ComponentsFile.ParseReferences(invocation.List("references")) : parent.References;
		foreach (var reference in references.Where(reference => monorepo.FindComponent(reference.Name) is null))
		{
			throw new MonorepoException($"-references names '{reference.Name}', which is not registered.");
		}

		var url = invocation.Get("url") ?? "";
		var parentDir = RequireRepo(monorepo, parent.Name);
		var source = GitClient.In(parentDir);
		var head = await source.RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"]);
		if (head.ExitCode != 0 || head.Output.Trim().Length == 0)
		{
			throw new GitException($"Component '{parent.Name}' has no main branch to fork.");
		}

		var branch = head.Output.Trim();
		var target = invocation.Get("ref") ?? branch;
		var resolved = await source.RunAsync(["rev-parse", "--verify", "--quiet", $"{target}^{{commit}}"]);
		if (resolved.ExitCode != 0)
		{
			throw new GitException($"'{target}' does not name a commit in component '{parent.Name}'.");
		}

		var commit = resolved.Output.Trim();
		var onMain = await source.RunAsync(["merge-base", "--is-ancestor", commit, $"refs/heads/{branch}"]);
		if (onMain.ExitCode != 0)
		{
			throw new GitException($"'{target}' ({commit[..10]}) is not on the main branch '{branch}' of component '{parent.Name}'.");
		}

		var tags = (await source.RunOrThrowAsync(["tag", "--merged", commit, "--format=%(refname:strip=2)"]))
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(tag => !ForeignTagPrefixes.Any(prefix => tag.StartsWith(prefix, StringComparison.Ordinal)))
			.ToList();

		string meta;
		try
		{
			var clone = await new GitClient(monorepo.Root).RunAsync(
				["clone", "--bare", "--single-branch", "--no-tags", "--branch", branch, parentDir, Path.Combine(forkDir, ".git")]);
			if (clone.ExitCode != 0)
			{
				throw new GitException($"git clone failed: {clone.Error.Trim()}");
			}

			var fork = GitClient.In(forkDir);
			if (tags.Count > 0)
			{
				await fork.RunOrThrowAsync(["fetch", "--quiet", "--no-tags", "origin", .. tags.Select(tag => $"+refs/tags/{tag}:refs/tags/{tag}")]);
			}

			await fork.RunOrThrowAsync(["remote", "remove", "origin"]);
			if (url.Length > 0)
			{
				await fork.RunOrThrowAsync(["remote", "add", "origin", url]);
			}

			var tip = await fork.RunOrThrowAsync(["rev-parse", $"refs/heads/{branch}"]);
			if (tip != commit)
			{
				// Fork before the head: drop the later commits, which only the clone brought along.
				await fork.RunOrThrowAsync(["update-ref", $"refs/heads/{branch}", commit]);
				await fork.RunOrThrowAsync(["reflog", "expire", "--expire=now", "--all"]);
				await fork.RunOrThrowAsync(["gc", "--quiet", "--prune=now"]);
			}

			ComponentsFile.Add(monorepo.Root, name, url, references, parent.Name, commit);
			meta = await MonorepoCommands.CommitMetaRepoAsync(monorepo.Root, $"Fork component '{parent.Name}' as '{name}' at {commit[..10]}") ?? "";
		}
		catch
		{
			if (Directory.Exists(forkDir))
			{
				DeleteDirectory(forkDir);
			}

			throw;
		}

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
				var runs = await new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir).ListLatestAsync();
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

	/// <summary>Deletes a folder tree, including the read-only pack files git leaves behind.</summary>
	private static void DeleteDirectory(string directory)
	{
		foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(directory, recursive: true);
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
	internal static string Describe(ComponentReference reference) =>
		reference.Path == reference.Name ? reference.Name : $"{reference.Name}:{reference.Path}";

	private static string Join(IEnumerable<string> values)
	{
		var joined = string.Join(",", values);
		return joined.Length == 0 ? "-" : joined;
	}
}
