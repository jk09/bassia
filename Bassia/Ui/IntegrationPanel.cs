namespace Bassia.Ui;

using Bassia.CliCommands.Agent;
using Bassia.Integration;
using Spectre.Console;
using Spectre.Console.Rendering;

/// <summary>
/// The integration control panel (view <c>3</c>): the runs that can be integrated, the triage of the chosen ones per
/// component - which steps git merges by syntax, which go to the resolver for a semantic merge, which are skipped -
/// the live progress of a running integration, and the integrations recorded so far.
/// </summary>
internal static class IntegrationPanel
{
	private const string Spinner = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

	public static IRenderable Candidates(IReadOnlyList<RunMetadata> candidates, IReadOnlySet<string> chosen, int cursor)
	{
		if (candidates.Count == 0)
		{
			return new Markup("[grey](no agentic run has pushed a result yet; runs become integrable once they complete)[/]");
		}

		var table = new Table().Border(TableBorder.Rounded).Title("Runs with results [grey](space toggles, a all/none)[/]")
			.AddColumns("", "Run", "Status", "Components", "Rationale");
		for (var i = 0; i < candidates.Count; i++)
		{
			var run = candidates[i];
			var mark = chosen.Contains(run.RunId) ? "[green][[x]][/]" : "[grey][[ ]][/]";
			var id = RunMetadata.ShortKey(run.RunId);
			table.AddRow(
				(i == cursor ? "[bold yellow]▸[/] " : "  ") + mark,
				i == cursor ? $"[bold yellow]{id}[/]" : id,
				Markup.Escape(run.Status),
				Markup.Escape(string.Join(", ", run.Components.Where(component => component.ResultStatus == ResultStatus.Pushed).Select(component => component.Name))),
				Markup.Escape(AgentCommand.SummarizeCommand(run.Command)));
		}

		return table;
	}

	/// <summary>
	/// The triage (before an integration) or the progress (during and after one): one row per step, grouped by
	/// component in execution order.
	/// </summary>
	public static IRenderable Steps(IReadOnlyList<ComponentIntegration> components, string title, string? activeComponent, string? activeRunId, int frame)
	{
		var table = new Table().Border(TableBorder.Rounded).Title(title)
			.AddColumns("Component", "#", "Run", "Triage", "Strategy", "Conflicts", "Outcome");
		foreach (var component in components)
		{
			var header = $"[bold]{Markup.Escape(component.Name)}[/] [grey]onto {Markup.Escape(component.BaseRef)} ({component.BaseCommit[..7]})[/]";
			for (var i = 0; i < component.Steps.Count; i++)
			{
				var step = component.Steps[i];
				var active = component.Name == activeComponent && step.RunId == activeRunId && step.Outcome == StepOutcome.Pending;
				table.AddRow(
					i == 0 ? header : "",
					step.Strategy == MergeStrategy.Skip ? "-" : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
					RunMetadata.ShortKey(step.RunId),
					TriageText(step.Triage),
					StrategyMarkup(step),
					ConflictsMarkup(step),
					active ? $"[deepskyblue1]{Spinner[frame % Spinner.Length]} {(step.IsSemantic ? "resolving" : "merging")}[/]" : OutcomeMarkup(step));
			}

			if (component.ResultTag is not null || component.ResultError is not null)
			{
				table.AddRow("", "", "", "", "", "", component.ResultError is null
					? $"[green]→ {Markup.Escape(component.ResultTag!)}[/]{(component.Advanced ? $" [grey](advanced {Markup.Escape(component.BaseRef)})[/]" : "")}"
					: $"[red]{Markup.Escape(component.ResultError)}[/]");
			}
		}

		return table;
	}

	/// <summary>One line: how many steps go which way.</summary>
	public static string Summary(IReadOnlyList<ComponentIntegration> plan)
	{
		var steps = plan.SelectMany(component => component.Steps).ToList();
		var upToDate = steps.Count(step => step.Triage == Triage.UpToDate && step.Strategy != MergeStrategy.Skip);
		var syntactic = steps.Count(step => step.Strategy == MergeStrategy.Syntactic) - upToDate;
		var semantic = steps.Count(step => step.Strategy == MergeStrategy.Semantic);
		var skipped = steps.Count(step => step.Strategy == MergeStrategy.Skip);
		return $"[green]{syntactic} by git (syntax)[/] · [magenta]{semantic} by the resolver (semantic)[/] · [grey]{upToDate} up to date · {skipped} skipped[/]";
	}

	public static IRenderable History(IReadOnlyList<IntegrationRecord> records)
	{
		var table = new Table().Border(TableBorder.Rounded).Title("Integrations")
			.AddColumns("Integration", "Status", "Runs", "Result", "Created");
		foreach (var record in records.Take(8))
		{
			var tagged = record.Components.Where(component => component.ResultTag is not null).ToList();
			var result = tagged.Count == 0
				? "[grey]-[/]"
				: $"{Markup.Escape(tagged[0].ResultTag!)} [grey]in {Markup.Escape(string.Join(", ", tagged.Select(component => component.Name + (component.Advanced ? "*" : ""))))}[/]";
			table.AddRow(
				IntegrationRecord.ShortKey(record.IntegrationId),
				StatusMarkup(record.Status),
				string.Join(", ", record.Runs.Select(RunMetadata.ShortKey)),
				result,
				Markup.Escape(record.Created.Length >= 16 ? record.Created[..16].Replace('T', ' ') : record.Created));
		}

		if (table.Rows.Count == 0)
		{
			table.AddRow("[grey]none yet[/]", "", "", "", "");
		}

		return table;
	}

	public static string TriageText(Triage triage) => triage switch
	{
		Triage.UpToDate => "[grey]up to date[/]",
		Triage.FastForward => "fast-forward",
		Triage.Clean => "clean",
		_ => "[red]conflict[/]"
	};

	public static string StrategyMarkup(IntegrationStep step)
	{
		var text = step.Strategy switch
		{
			MergeStrategy.Syntactic => "[green]SYNTAX[/]",
			MergeStrategy.Semantic => "[magenta]SEMANTIC[/]",
			_ => "[grey]SKIP[/]"
		};
		return step.Overridden ? text + "[yellow]*[/]" : text;
	}

	private static string ConflictsMarkup(IntegrationStep step)
	{
		var parts = new List<string>();
		if (step.Conflicts.Count > 0)
		{
			parts.Add(Markup.Escape(string.Join(", ", step.Conflicts)));
		}

		if (step.ConflictsWith.Count > 0)
		{
			parts.Add($"[grey]vs {string.Join(", ", step.ConflictsWith.Select(RunMetadata.ShortKey))}[/]");
		}

		return parts.Count == 0 ? "[grey]-[/]" : string.Join(" ", parts);
	}

	public static string OutcomeMarkup(IntegrationStep step) => step.Outcome switch
	{
		StepOutcome.Pending => "[grey]pending[/]",
		StepOutcome.UpToDate => "[grey]up to date[/]",
		StepOutcome.Merged => $"[green]merged[/] [grey]{step.Commit?[..7]}[/]",
		StepOutcome.Resolved => $"[magenta]resolved[/] [grey]{step.Commit?[..7]}[/]",
		StepOutcome.Skipped => "[grey]skipped[/]",
		_ => $"[red]failed[/] [grey]{Markup.Escape(step.Note ?? "")}[/]"
	};

	public static string StatusMarkup(string status) => status switch
	{
		"completed" => "[green]completed[/]",
		"started" => "[blue]started[/]",
		"partial" => "[orange1]partial[/]",
		"cancelled" => "[grey]cancelled[/]",
		_ => Markup.Escape(status)
	};
}
