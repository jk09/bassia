namespace Bassia.CliCommands.Agent;

/// <summary>
/// One <c>component@commit-ish</c> pair from <c>-select</c>, or a bare <c>component</c> (a null
/// <see cref="CommitIsh"/>) where the option allows it.
/// </summary>
internal sealed record ComponentSelection(string Component, string? CommitIsh)
{
	/// <summary>
	/// Parses a comma-separated list of <c>component@commit-ish</c> pairs; with <paramref name="allowBare"/> an entry
	/// may also be a bare component name.
	/// </summary>
	public static List<ComponentSelection> ParseList(string option, string value, bool allowBare = false)
	{
		var selections = new List<ComponentSelection>();
		foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var separator = item.LastIndexOf('@');
			if (separator < 0 && allowBare)
			{
				Add(new ComponentSelection(item, null));
				continue;
			}

			if (separator <= 0 || separator == item.Length - 1)
			{
				throw new AgentException($"Invalid {option} entry '{item}': expected <component>@<commit-ish>{(allowBare ? " or <component>" : "")}.");
			}

			Add(new ComponentSelection(item[..separator].Trim(), item[(separator + 1)..].Trim()));
		}

		return selections;

		void Add(ComponentSelection selection)
		{
			if (selections.Any(existing => existing.Component == selection.Component))
			{
				throw new AgentException($"Component '{selection.Component}' is listed more than once in {option}.");
			}

			selections.Add(selection);
		}
	}
}
