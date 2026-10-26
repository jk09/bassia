namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Core;

/// <summary>
/// <c>bassia conformance list | add | remove | check</c>: the conformance rules of <c>components.toml</c> - what
/// components with given tags must have - and where the components fall short of them.
/// </summary>
internal static class ConformanceCommands
{
	public static Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var rules = monorepo.Registry.Conformance;
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command, $"{rules.Count} conformance rule(s).", new Dictionary<string, object?>
		{
			["table"] = rules.Count == 0 ? null : new TomlText(AsciiTable.Render(["#", "COMPONENTS", "NEEDS", "DESCRIPTION"],
				rules.Select((rule, index) => (IReadOnlyList<string>)[(index + 1).ToString(), rule.Components, rule.Text[(rule.Components.Length + " needs ".Length)..], rule.Description ?? "-"]))),
			["conformance"] = Describe(rules)
		}));
	}

	public static async Task<int> AddAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var rule = new ConformanceRule(invocation.Require("components"), invocation.List("files"), invocation.List("tasks"),
			invocation.Has("owners"), invocation.Has("exports"), invocation.Get("description"));
		var graph = monorepo.Graph();
		var rules = await graph.AddConformanceAsync(rule);
		var findings = await ConformanceChecker.CheckAllAsync(graph.Registry, graph.Storage);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Added conformance rule #{rules.Count}: {rule.Text}{FindingSuffix(findings)}.",
			new Dictionary<string, object?>
			{
				["index"] = rules.Count,
				["rule"] = rule.Text,
				["conformance"] = Describe(rules),
				["findings"] = findings.Count,
				["warnings"] = Messages(findings)
			});
	}

	public static async Task<int> RemoveAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var index = invocation.Int("index", 0, 1);
		if (index == 0)
		{
			throw invocation.Usage("-index is required.");
		}

		var rule = index <= monorepo.Registry.Conformance.Count ? monorepo.Registry.Conformance[index - 1] : null;
		var rules = await monorepo.Graph().RemoveConformanceAsync(index - 1);
		return ProgramCli.WriteResult(true, invocation.Command, $"Removed conformance rule #{index}: {rule!.Text}; {rules.Count} rule(s) left.",
			new Dictionary<string, object?> { ["removed"] = rule.Text, ["conformance"] = Describe(rules) });
	}

	public static async Task<int> CheckAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var components = invocation.Get("name") is { } name ? [ComponentCommands.RequireComponent(monorepo, name)] : monorepo.Components.ToList();
		var findings = await ConformanceChecker.CheckAllAsync(monorepo.Registry, monorepo.Graph().Storage, components);
		var strict = invocation.Has("strict");
		var ok = !strict || findings.Count == 0;
		return ProgramCli.WriteResult(ok, invocation.Command,
			findings.Count == 0
				? $"Every component meets the {monorepo.Registry.Conformance.Count} conformance rule(s) that apply to it."
				: $"{findings.Count} unmet conformance requirement(s) in {findings.Select(finding => finding.Component).Distinct().Count()} component(s); the first: {findings[0].Message}",
			new Dictionary<string, object?>
			{
				["strict"] = strict,
				["rules"] = monorepo.Registry.Conformance.Count,
				["findings"] = findings.Count,
				["table"] = findings.Count == 0 ? null : new TomlText(AsciiTable.Render(["COMPONENT", "MISSING", "RULE"],
					findings.Select(finding => (IReadOnlyList<string>)[finding.Component, finding.Missing, finding.Rule]), 72)),
				["finding"] = findings.Count == 0 ? null : Describe(findings),
				["warnings"] = ok ? Messages(findings) : null
			});
	}

	internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> Describe(IReadOnlyList<ConformanceFinding> findings) =>
		findings.Select(finding => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["component"] = finding.Component,
			["missing"] = finding.Missing,
			["rule"] = finding.Rule,
			["message"] = finding.Message
		}).ToList();

	private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Describe(IReadOnlyList<ConformanceRule> rules) =>
		rules.Select((rule, index) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["index"] = index + 1,
			["components"] = rule.Components,
			["files"] = rule.Files.Count == 0 ? null : rule.Files,
			["tasks"] = rule.Tasks.Count == 0 ? null : rule.Tasks,
			["owners"] = rule.Owners ? true : null,
			["exports"] = rule.Exports ? true : null,
			["description"] = rule.Description
		}).ToList();

	private static IReadOnlyList<string>? Messages(IReadOnlyList<ConformanceFinding> findings) =>
		findings.Count == 0 ? null : findings.Select(finding => finding.Message).ToList();

	private static string FindingSuffix(IReadOnlyList<ConformanceFinding> findings) =>
		findings.Count == 0 ? "; every component meets it" : $"; {findings.Count} unmet requirement(s) - see 'bassia conformance check'";
}
