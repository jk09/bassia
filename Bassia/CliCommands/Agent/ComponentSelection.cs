namespace Bassia.CliCommands.Agent;

/// <summary>One <c>component@commit-ish</c> pair from <c>-select</c>.</summary>
internal sealed record ComponentSelection(string Component, string CommitIsh)
{
	/// <summary>Parses a comma-separated list of <c>component@commit-ish</c> pairs.</summary>
	public static List<ComponentSelection> ParseList(string option, string value)
	{
		var selections = new List<ComponentSelection>();
		foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var separator = item.LastIndexOf('@');
			if (separator <= 0 || separator == item.Length - 1)
			{
				throw new AgentException($"Invalid {option} entry '{item}': expected <component>@<commit-ish>.");
			}

			var selection = new ComponentSelection(item[..separator].Trim(), item[(separator + 1)..].Trim());
			if (selections.Any(existing => existing.Component == selection.Component))
			{
				throw new AgentException($"Component '{selection.Component}' is listed more than once in {option}.");
			}

			selections.Add(selection);
		}

		return selections;
	}
}
