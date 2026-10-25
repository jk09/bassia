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

	public static Task<int> CheckAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var violations = monorepo.Registry.Violations();
		if (invocation.Get("name") is { } name)
		{
			var component = ComponentCommands.RequireComponent(monorepo, name);
			violations = violations.Where(violation => violation.From == component.Name || violation.To == component.Name).ToList();
		}

		var strict = invocation.Has("strict");
		var ok = !strict || violations.Count == 0;
		var references = monorepo.Components.Sum(component => component.References.Count);
		var message = violations.Count == 0
			? $"No boundary violations: {references} reference(s) checked against {monorepo.Registry.Boundaries.Count} rule(s) and the components' visibility."
			: $"{violations.Count} boundary violation(s) in {references} reference(s); the first: {violations[0].Message}";
		return Task.FromResult(ProgramCli.WriteResult(ok, invocation.Command, message, new Dictionary<string, object?>
		{
			["strict"] = strict,
			["rules"] = monorepo.Registry.Boundaries.Count,
			["references"] = references,
			["violations"] = violations.Count,
			["table"] = violations.Count == 0 ? null : new TomlText(AsciiTable.Render(["FROM", "TO", "KIND", "BREAKS"],
				violations.Select(violation => (IReadOnlyList<string>)
				[
					violation.From, violation.To, violation.Kind.ToString().ToLowerInvariant(), violation.Constraint
				]), 72)),
			["violation"] = violations.Count == 0 ? null : Describe(violations),
			["warnings"] = ok ? ComponentCommands.Warnings(violations) : null
		}));
	}

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
