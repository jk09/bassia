namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Git;

/// <summary><c>bassia tag ...</c>: tags across components - how many components each tag spans, and creating one in several.</summary>
internal static class TagCommands
{
	// ----- bassia tag list -----

	public static async Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var only = invocation.List("component");
		foreach (var name in only)
		{
			ComponentCommands.RequireComponent(monorepo, name);
		}

		var prefix = invocation.Get("prefix") ?? "";
		var min = invocation.Int("min", 1, 1);
		var tags = (await MultiComponentTag.ReadAllAsync(monorepo))
			.Where(tag => tag.Name.StartsWith(prefix, StringComparison.Ordinal))
			.Where(tag => only.Count == 0 || tag.Components.Any(component => only.Contains(component.Component)))
			.Where(tag => tag.Components.Count >= min)
			.ToList();

		var multi = tags.Count(tag => tag.Components.Count > 1);
		return ProgramCli.WriteResult(true, invocation.Command, $"{tags.Count} tag(s), {multi} of them in more than one component.", new Dictionary<string, object?>
		{
			["count"] = tags.Count,
			["multi_component"] = multi,
			["table"] = tags.Count == 0 ? null : new TomlText(AsciiTable.Render(["TAG", "COMPONENTS", "KIND", "TAGGED"],
				tags.Select(tag => (IReadOnlyList<string>)
				[
					tag.Name, tag.Components.Count.ToString(), tag.Kind, string.Join(",", tag.Components.Select(component => component.Component))
				]))),
			["tag"] = tags.Select(tag => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["name"] = tag.Name,
				["kind"] = tag.Kind,
				["components"] = tag.Components.Count,
				["component"] = tag.Components.Select(component => component.Component).ToList(),
				["commit"] = tag.Components.Select(component => $"{component.Component}@{component.Commit}").ToList(),
				["annotated"] = tag.Components.All(component => component.Annotated),
				["latest"] = tag.Components.Max(component => component.Date),
				["select"] = tag.Select
			}).ToList()
		});
	}

	// ----- bassia tag show -----

	public static async Task<int> ShowAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var name = invocation.Require("tag");
		var tag = (await MultiComponentTag.ReadAllAsync(monorepo)).FirstOrDefault(candidate => candidate.Name == name)
			?? throw new MonorepoException($"No component has the tag '{name}'. List the tags with 'bassia tag list'.");

		var entries = new List<IReadOnlyDictionary<string, object?>>();
		foreach (var component in tag.Components)
		{
			var git = GitClient.In(monorepo.SourceRepoDir(component.Component));
			var message = component.Annotated ? await git.RunOrThrowAsync(["for-each-ref", "--format=%(contents)", $"refs/tags/{name}"]) : null;
			var commitSubject = await git.RunOrThrowAsync(["log", "-1", "--format=%s", component.Commit]);
			entries.Add(new Dictionary<string, object?>
			{
				["name"] = component.Component,
				["commit"] = component.Commit,
				["subject"] = commitSubject,
				["annotated"] = component.Annotated,
				["date"] = component.Date,
				["message"] = message
			});
		}

		return ProgramCli.WriteResult(true, invocation.Command, $"Tag '{name}' ({tag.Kind}) is in {tag.Components.Count} component(s).", new Dictionary<string, object?>
		{
			["tag"] = name,
			["kind"] = tag.Kind,
			["components"] = tag.Components.Count,
			["select"] = tag.Select,
			["component"] = entries
		});
	}

	// ----- bassia tag create -----

	public static async Task<int> CreateAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var name = invocation.Require("tag");
		var check = await new GitClient(monorepo.Root).RunAsync(["check-ref-format", $"refs/tags/{name}"]);
		if (check.ExitCode != 0)
		{
			throw invocation.Usage($"'{name}' is not a valid tag name.");
		}

		var selection = invocation.List("select");
		if (selection.Count == 0)
		{
			throw invocation.Usage("-select names no component.");
		}

		// Resolve everything first: either every component gets the tag or none does.
		var targets = new List<(string Component, string Ref, string Commit, GitClient Git)>();
		foreach (var entry in selection)
		{
			var at = entry.IndexOf('@');
			var componentName = at < 0 ? entry : entry[..at];
			var target = at < 0 ? "HEAD" : entry[(at + 1)..];
			var component = ComponentCommands.RequireComponent(monorepo, componentName);
			if (targets.Any(item => item.Component == component.Name))
			{
				throw invocation.Usage($"-select names component '{component.Name}' more than once.");
			}

			var git = GitClient.In(ComponentCommands.RequireRepo(monorepo, component.Name));
			if ((await git.RunAsync(["rev-parse", "--verify", "--quiet", $"refs/tags/{name}"])).ExitCode == 0)
			{
				throw new MonorepoException($"Component '{component.Name}' already has the tag '{name}'; no tag was created.");
			}

			var commit = await git.RunAsync(["rev-parse", "--verify", "--quiet", $"{target}^{{commit}}"]);
			if (commit.ExitCode != 0)
			{
				throw new GitException($"'{target}' does not name a commit in component '{component.Name}'; no tag was created.");
			}

			targets.Add((component.Name, target, commit.Output.Trim(), git));
		}

		var message = invocation.Get("message") ?? $"{name}: {string.Join(", ", targets.Select(item => $"{item.Component} {item.Commit[..10]}"))}";
		var created = new List<GitClient>();
		foreach (var item in targets)
		{
			var result = await item.Git.RunAsync(["tag", "-a", name, "-m", message, item.Commit]);
			if (result.ExitCode != 0)
			{
				foreach (var git in created)
				{
					await git.RunAsync(["tag", "-d", name]);
				}

				throw new GitException($"git tag failed in component '{item.Component}': {result.Error.Trim()}; no tag was kept.");
			}

			created.Add(item.Git);
		}

		var select = string.Join(",", targets.Select(item => $"{item.Component}@{name}"));
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Tagged {targets.Count} component(s) '{name}'; select them for a run with -select {select}.",
			new Dictionary<string, object?>
			{
				["tag"] = name,
				["components"] = targets.Count,
				["select"] = select,
				["tag_message"] = message,
				["component"] = targets.Select(item => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = item.Component,
					["ref"] = item.Ref,
					["commit"] = item.Commit
				}).ToList()
			});
	}
}
