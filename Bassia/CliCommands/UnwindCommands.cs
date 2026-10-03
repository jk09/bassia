namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Unwind;

/// <summary><c>bassia component unwind</c> and <c>component add -unwind</c>: a component's git submodules become components.</summary>
internal static class UnwindCommands
{
	// ----- bassia component unwind -----

	public static async Task<int> UnwindAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var definition = ComponentCommands.RequireComponent(monorepo, invocation.Require("name"));
		var root = new UnwindComponent
		{
			Name = definition.Name,
			Url = definition.Url,
			RepoDir = ComponentCommands.RequireRepo(monorepo, definition.Name),
			IsNew = false,
			References = definition.References.ToList()
		};

		using var unwinder = await SubmoduleUnwinder.PlanAsync(monorepo, root);
		var dryRun = invocation.Has("dry-run");
		var count = unwinder.Unwinding.Sum(context => context.Pins.Count);
		if (count == 0)
		{
			return ProgramCli.WriteResult(true, invocation.Command, $"Component '{definition.Name}' has no submodules on '{root.MainBranch}'; nothing to unwind.",
				Describe(unwinder, dryRun, metaRepoCommit: null));
		}

		string? commit = null;
		if (!dryRun)
		{
			commit = await unwinder.ApplyAsync($"Unwind the submodules of component '{definition.Name}'{AddedSuffix(unwinder)}");
		}

		return ProgramCli.WriteResult(true, invocation.Command, Summary(unwinder, dryRun), Describe(unwinder, dryRun, commit));
	}

	// ----- bassia component add -unwind -----

	/// <summary>
	/// Unwinds a component <c>component add</c> has just cloned to <paramref name="componentDir"/> but not registered:
	/// the unwind registers it with the submodules' components in one meta-repo commit. When the plan fails, the clone
	/// is removed again and nothing is registered.
	/// </summary>
	public static async Task<int> AddUnwoundAsync(Invocation invocation, Monorepo monorepo, string name, string url, string componentDir, IReadOnlyList<ComponentReference> references)
	{
		var root = new UnwindComponent { Name = name, Url = url, RepoDir = componentDir, IsNew = true, References = references.ToList() };
		SubmoduleUnwinder unwinder;
		try
		{
			unwinder = await SubmoduleUnwinder.PlanAsync(monorepo, root);
		}
		catch
		{
			DeleteRepo(componentDir);
			throw;
		}

		using (unwinder)
		{
			var commit = await unwinder.ApplyAsync($"Add component '{name}' from '{url}' and unwind its submodules{AddedSuffix(unwinder)}", references);
			var count = unwinder.Unwinding.Sum(context => context.Pins.Count);
			var payload = Describe(unwinder, dryRun: false, commit);
			payload["url"] = url;
			payload["path"] = componentDir;
			return ProgramCli.WriteResult(true, invocation.Command,
				count == 0
					? $"Added component '{name}' from '{url}'; it has no submodules to unwind."
					: $"Added component '{name}' from '{url}'. {Summary(unwinder, dryRun: false)}",
				payload);
		}
	}

	private static string AddedSuffix(SubmoduleUnwinder unwinder)
	{
		var added = unwinder.Components.Where(component => component.IsNew && component != unwinder.Root).Select(component => component.Name).ToList();
		return added.Count == 0 ? "" : $" (adds {string.Join(", ", added)})";
	}

	private static string Summary(SubmoduleUnwinder unwinder, bool dryRun)
	{
		var submodules = unwinder.Unwinding.Sum(context => context.Pins.Count);
		var added = unwinder.Components.Count(component => component.IsNew && component != unwinder.Root);
		var commits = unwinder.Unwinding.Count();
		return (dryRun ? "Would unwind" : "Unwound") +
			$" {submodules} submodule(s) of '{unwinder.Root.Name}' ({added} component(s) added, {unwinder.Components.Count - 1 - added} reused) " +
			$"with {commits} unwind commit(s) and {unwinder.Tags.Count} linking tag(s)." +
			(dryRun ? " Nothing was changed (-dry-run)." : "");
	}

	private static Dictionary<string, object?> Describe(SubmoduleUnwinder unwinder, bool dryRun, string? metaRepoCommit)
	{
		var rows = unwinder.Unwinding.SelectMany(context => context.Pins.Select(pin => (Context: context, Pin: pin))).ToList();
		return new Dictionary<string, object?>
		{
			["name"] = unwinder.Root.Name,
			["dry_run"] = dryRun,
			["meta_repo_commit"] = metaRepoCommit,
			["table"] = rows.Count == 0 ? null : new TomlText(AsciiTable.Render(["PARENT", "AT", "PATH", "COMPONENT", "ACTION", "PINNED", "TAG"],
				rows.Select(row => (IReadOnlyList<string>)
				[
					row.Context.Component.Name, row.Context.IsMain ? row.Context.Component.MainBranch ?? "main" : row.Context.Commit[..10],
					row.Pin.Link.Path, row.Pin.Component.Name, row.Pin.Added ? "added" : "reused", row.Pin.Link.Commit[..10], row.Context.Tag ?? "-"
				]))),
			["submodule"] = rows.Select(row => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["parent"] = row.Context.Component.Name,
				["parent_commit"] = row.Context.Commit,
				["on_main"] = row.Context.IsMain,
				["unwound_commit"] = row.Context.Unwound,
				["path"] = row.Pin.Link.Path,
				["url"] = row.Pin.Url,
				["identity"] = row.Pin.Identity,
				["component"] = row.Pin.Component.Name,
				["action"] = row.Pin.Added ? "added" : "reused",
				["pinned"] = row.Pin.Link.Commit,
				["tag"] = row.Context.Tag
			}).ToList(),
			["component"] = unwinder.Components.Select(component => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["name"] = component.Name,
				["action"] = component == unwinder.Root ? "unwound" : component.IsNew ? "added" : "reused",
				["url"] = component.Url,
				["main_branch"] = component.MainBranch,
				["main_before"] = component.Main?.Commit,
				["main_after"] = component.Main?.Effective,
				["added_references"] = component.AddedReferences.Select(ComponentCommands.Describe).ToList()
			}).ToList(),
			["tag"] = unwinder.Tags.Select(tag => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["name"] = tag.Name,
				["components"] = tag.Members.Count,
				["select"] = string.Join(",", tag.Members.Select(member => $"{member.Component.Name}@{tag.Name}")),
				["member"] = tag.Members.Select(member => $"{member.Component.Name}@{(dryRun ? member.Commit : member.Effective)}").ToList(),
				["conflicts"] = tag.Conflicts.Count == 0 ? null : tag.Conflicts.ToList()
			}).ToList()
		};
	}

	private static void DeleteRepo(string directory)
	{
		if (!Directory.Exists(directory))
		{
			return;
		}

		foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(directory, recursive: true);
	}
}
