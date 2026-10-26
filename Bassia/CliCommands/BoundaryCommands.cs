namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Core;

/// <summary>
/// <c>bassia boundary list | add | remove | check</c>: the boundary rules of <c>components.toml</c> - which tagged
/// components may reference which - and the references that break them or a component's visibility.
/// </summary>
internal static class BoundaryCommands
{
	// ----- bassia boundary list -----

	public static Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var rules = monorepo.Registry.Boundaries;
		var violations = monorepo.Registry.Violations();
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command,
			$"{rules.Count} boundary rule(s){ComponentCommands.ViolationSuffix(violations)}.",
			new Dictionary<string, object?>
			{
				["table"] = rules.Count == 0 ? null : new TomlText(AsciiTable.Render(["#", "SOURCE", "ALLOW", "DENY", "DESCRIPTION"],
					rules.Select((rule, index) => (IReadOnlyList<string>)
					[
						(index + 1).ToString(), rule.Source, Join(rule.Allow), Join(rule.Deny), rule.Description ?? "-"
					]))),
				["boundary"] = Describe(rules),
				["violations"] = violations.Count,
				["visibility"] = monorepo.Components.Where(component => component.Visibility is not null)
					.Select(component => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
					{
						["component"] = component.Name,
						["visible_to"] = component.Visibility
					}).ToList(),
				["warnings"] = ComponentCommands.Warnings(violations)
			}));
	}

	// ----- bassia boundary add -----

	public static async Task<int> AddAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var allow = invocation.List("allow");
		var deny = invocation.List("deny");
		if (allow.Count == 0 && deny.Count == 0)
		{
			throw invocation.Usage("Give -allow, -deny or both.");
		}

		var rule = new BoundaryRule(invocation.Require("source"), allow, deny, invocation.Get("description"));
		var graph = monorepo.Graph();
		var change = await graph.AddBoundaryAsync(rule);
		var violations = graph.Registry.Violations();
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Added boundary rule #{change.Rules.Count}: {rule.Text}{ComponentCommands.ViolationSuffix(violations)}.",
			new Dictionary<string, object?>
			{
				["index"] = change.Rules.Count,
				["rule"] = rule.Text,
				["boundary"] = Describe(change.Rules),
				["meta_repo_commit"] = change.Revision,
				["violations"] = violations.Count,
				["warnings"] = ComponentCommands.Warnings(violations)
			});
	}

	// ----- bassia boundary remove -----

	public static async Task<int> RemoveAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var index = invocation.Int("index", 0, 1);
		if (index == 0)
		{
			throw invocation.Usage("-index is required.");
		}

		var rule = index <= monorepo.Registry.Boundaries.Count ? monorepo.Registry.Boundaries[index - 1] : null;
		var graph = monorepo.Graph();
		var change = await graph.RemoveBoundaryAsync(index - 1);
		var violations = graph.Registry.Violations();
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Removed boundary rule #{index}: {rule!.Text}; {change.Rules.Count} rule(s) left{ComponentCommands.ViolationSuffix(violations)}.",
			new Dictionary<string, object?>
			{
				["removed"] = rule.Text,
				["boundary"] = Describe(change.Rules),
				["meta_repo_commit"] = change.Revision,
				["violations"] = violations.Count,
				["warnings"] = ComponentCommands.Warnings(violations)
			});
	}

	// ----- bassia boundary check -----

	public static async Task<int> CheckAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var violations = monorepo.Registry.Violations();
		var components = monorepo.Components.ToList();
		if (invocation.Get("name") is { } name)
		{
			var component = ComponentCommands.RequireComponent(monorepo, name);
			violations = violations.Where(violation => violation.From == component.Name || violation.To == component.Name).ToList();
			components = [component];
		}

		var scans = invocation.Has("code") ? await ScanTipsAsync(monorepo, components) : [];
		var code = scans.SelectMany(scan => scan.Violations).ToList();
		var strict = invocation.Has("strict");
		var total = violations.Count + code.Count;
		var ok = !strict || total == 0;
		var references = monorepo.Components.Sum(component => component.References.Count);
		var checkedWhat = $"{references} reference(s) checked against {monorepo.Registry.Boundaries.Count} rule(s) and the components' visibility" +
			(invocation.Has("code") ? $", and {scans.Sum(scan => scan.Dependencies)} dependencies in the code of {scans.Count} component(s)" : "");
		var messages = violations.Select(violation => violation.Message).Concat(code.Select(violation => violation.Message)).ToList();
		var message = total == 0
			? $"No boundary violations: {checkedWhat}."
			: $"{total} boundary violation(s) ({checkedWhat}); the first: {messages[0]}";
		return ProgramCli.WriteResult(ok, invocation.Command, message, new Dictionary<string, object?>
		{
			["strict"] = strict,
			["rules"] = monorepo.Registry.Boundaries.Count,
			["references"] = references,
			["violations"] = violations.Count,
			["code_violations"] = invocation.Has("code") ? code.Count : null,
			["table"] = total == 0 ? null : new TomlText(AsciiTable.Render(["FROM", "TO", "KIND", "BREAKS"],
				violations.Select(violation => (IReadOnlyList<string>)
				[
					violation.From, violation.To, violation.Kind.ToString().ToLowerInvariant(), violation.Constraint
				]).Concat(code.Select(violation => (IReadOnlyList<string>)
				[
					violation.Component, violation.Target ?? "-", violation.KindName, $"{violation.File}:{violation.Line} {violation.Specifier}"
				])), 72)),
			["violation"] = violations.Count == 0 ? null : Describe(violations),
			["code_violation"] = code.Count == 0 ? null : Describe(code),
			["component"] = scans.Count == 0 ? null : scans.Select(scan => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["name"] = scan.Component,
				["commit"] = scan.Commit,
				["files"] = scan.Files,
				["dependencies"] = scan.Dependencies,
				["used_references"] = scan.UsedReferences,
				["unused_references"] = scan.UnusedReferences
			}).ToList(),
			["warnings"] = ok && total > 0 ? messages : null
		});
	}

	/// <summary>Each component with a store scanned at the tip of its default branch; one without a branch is left out.</summary>
	private static async Task<IReadOnlyList<CodeScan>> ScanTipsAsync(Monorepo monorepo, IReadOnlyList<ComponentDefinition> components)
	{
		var graph = monorepo.Graph();
		var scans = new List<CodeScan>();
		foreach (var component in components.Where(component => graph.Storage.Exists(component.Name)))
		{
			var store = graph.Storage.Open(component.Name);
			if (await store.DefaultBranchAsync() is { } branch && await store.BranchTipAsync(branch) is { } tip)
			{
				scans.Add(await CodeBoundaries.ScanAsync(graph.Registry, store, component.Name, tip));
			}
		}

		return scans;
	}

	internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> Describe(IReadOnlyList<CodeViolation> violations) =>
		violations.Select(violation => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["component"] = violation.Component,
			["file"] = violation.File,
			["line"] = violation.Line,
			["specifier"] = violation.Specifier,
			["kind"] = violation.KindName,
			["target"] = violation.Target,
			["message"] = violation.Message
		}).ToList();

	internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> Describe(IReadOnlyList<BoundaryViolation> violations) =>
		violations.Select(violation => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["from"] = violation.From,
			["to"] = violation.To,
			["kind"] = violation.Kind.ToString().ToLowerInvariant(),
			["breaks"] = violation.Constraint,
			["message"] = violation.Message
		}).ToList();

	private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Describe(IReadOnlyList<BoundaryRule> rules) =>
		rules.Select((rule, index) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["index"] = index + 1,
			["source"] = rule.Source,
			["allow"] = rule.Allow.Count == 0 ? null : rule.Allow,
			["deny"] = rule.Deny.Count == 0 ? null : rule.Deny,
			["description"] = rule.Description
		}).ToList();

	private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "-" : string.Join(",", values);
}
