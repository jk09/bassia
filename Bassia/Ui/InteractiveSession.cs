namespace Bassia.Ui;

using Bassia.CliCommands.Agent;
using Bassia.Git;
using Spectre.Console;

/// <summary>
/// The interactive frontend (<c>bassia ui</c>): menus over the components, their git refs and the agentic runs
/// of one monorepo. Reads through the same model as the scriptable commands and never keeps state of its own;
/// the two mutating actions (annotated tag, run start) go through the same code paths as the CLI.
/// </summary>
internal sealed class InteractiveSession
{
	public const string DefaultAgentCommand = "claude -p --permission-mode acceptEdits";
	private static readonly string[] LifecycleStubs = ["Stop", "Suspend", "Resume", "Hand off", "Integrate results"];

	private readonly IAnsiConsole console;
	private readonly Monorepo monorepo;
	private readonly RunMetadataStore store;
	private readonly Func<string, string, Task<AgentRunOutcome>> startRun;
	private string agentCommand = DefaultAgentCommand;

	public InteractiveSession(IAnsiConsole console, Monorepo monorepo, RunMetadataStore store, Func<string, string, Task<AgentRunOutcome>> startRun)
	{
		this.console = console;
		this.monorepo = monorepo;
		this.store = store;
		this.startRun = startRun;
	}

	public async Task RunAsync()
	{
		while (true)
		{
			Screen("Bassia monorepo", monorepo.Root);
			var choice = await ChooseAsync("What would you like to do?", ["Components", "Agentic runs", "Start an agentic run", "Quit"]);
			try
			{
				switch (choice)
				{
					case "Components": await ComponentsAsync(); break;
					case "Agentic runs": await RunsAsync(); break;
					case "Start an agentic run": await StartRunAsync(); break;
					default: return;
				}
			}
			catch (Exception ex) when (ex is GitException or MonorepoException or AgentException or IOException or UnauthorizedAccessException)
			{
				await ShowErrorAsync(ex.Message);
			}
		}
	}

	// ----- components -----

	private async Task ComponentsAsync()
	{
		while (true)
		{
			Screen("Components", $"{monorepo.Components.Count} registered in components.toml");
			var table = new Table().Border(TableBorder.Rounded).AddColumns("Component", "Source", "References");
			foreach (var component in monorepo.Components)
			{
				table.AddRow(Markup.Escape(component.Name), Markup.Escape(component.Url),
					Markup.Escape(string.Join(", ", component.References.Select(Describe))));
			}

			console.Write(table);

			var choices = new List<string> { "Show dependency graph" };
			choices.AddRange(monorepo.Components.Select(component => $"Open {component.Name}"));
			choices.Add("Back");
			var choice = await ChooseAsync("Component actions", choices);
			if (choice == "Back")
			{
				return;
			}

			if (choice == "Show dependency graph")
			{
				await GraphAsync();
			}
			else
			{
				await ComponentAsync(monorepo.FindComponent(choice["Open ".Length..])!);
			}
		}
	}

	private static string Describe(ComponentReference reference) =>
		reference.Path == reference.Name ? reference.Name : $"{reference.Name} (at {reference.Path})";

	private async Task GraphAsync()
	{
		var graph = new ComponentGraph(monorepo.Components);
		while (true)
		{
			Screen("Dependency graph", "a component nests the components below it");
			console.Write(new Panel(Markup.Escape(graph.RenderText())).Border(BoxBorder.Rounded));

			var choice = await ChooseAsync("Graph actions", ["Export as Markdown (Mermaid)", "Export as SVG", "Back"]);
			switch (choice)
			{
				case "Back":
					return;
				case "Export as SVG":
					await ExportAsync("components.svg", graph.ToSvg());
					break;
				default:
					await ExportAsync("components.md", graph.ToMermaidMarkdown());
					break;
			}
		}
	}

	private async Task ExportAsync(string defaultFileName, string content)
	{
		var path = await console.PromptAsync(new TextPrompt<string>("Write to file:").DefaultValue(Path.Combine(monorepo.Root, defaultFileName)));
		path = Path.GetFullPath(path, monorepo.Root);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, content);
		ShowNotice($"Written {path}");
	}

	private async Task ComponentAsync(ComponentDefinition component)
	{
		var graph = new ComponentGraph(monorepo.Components);
		var sourceDir = monorepo.SourceRepoDir(component.Name);
		while (true)
		{
			Screen($"Component {component.Name}", component.Url);
			var summary = new Grid().AddColumn().AddColumn();
			summary.AddRow("Source repo", Markup.Escape(sourceDir));
			summary.AddRow("References", Markup.Escape(component.References.Count == 0 ? "-" : string.Join(", ", component.References.Select(Describe))));
			var referrers = graph.ReferrersOf(component.Name);
			summary.AddRow("Referenced by", Markup.Escape(referrers.Count == 0 ? "-" : string.Join(", ", referrers.Select(Describe))));
			console.Write(summary);

			if (!Directory.Exists(sourceDir))
			{
				await ShowErrorAsync($"Component '{component.Name}' has no local repository at '{sourceDir}'. Run 'bassia add-component' first.");
				return;
			}

			var git = GitClient.In(sourceDir);
			var refs = await GitRef.ListAsync(git);
			var refTable = new Table().Border(TableBorder.Rounded).Title("Branches and tags").AddColumns("Kind", "Name", "Commit", "Subject");
			foreach (var reference in refs)
			{
				var kind = reference.Kind switch
				{
					GitRefKind.Branch => "branch",
					GitRefKind.AnnotatedTag => "[green]annotated tag[/]",
					_ => "[grey]lightweight tag[/]"
				};
				refTable.AddRow(kind, Markup.Escape(reference.Name), reference.Commit, Markup.Escape(reference.Subject));
			}

			console.Write(refTable);

			var log = await git.RunAsync(["log", "--graph", "--oneline", "--decorate", "--all", "--no-color", "-n", "25"]);
			console.Write(new Panel(Markup.Escape(log.ExitCode == 0 && log.Output.Length > 0 ? log.Output.TrimEnd() : "(no commits)"))
				.Header("Git tree").Border(BoxBorder.Rounded));

			WriteRunsOf(component.Name, await store.ListLatestAsync());

			var choice = await ChooseAsync("Component actions", ["Create annotated tag", "Refresh", "Back"]);
			switch (choice)
			{
				case "Back":
					return;
				case "Create annotated tag":
					await CreateTagAsync(git, refs);
					break;
			}
		}
	}

	private void WriteRunsOf(string componentName, IReadOnlyList<RunMetadata> runs)
	{
		var table = new Table().Border(TableBorder.Rounded).Title("Agentic runs touching this component")
			.AddColumns("Run", "Status", "Base", "Result", "Result tag");
		foreach (var run in runs)
		{
			foreach (var component in run.Components.Where(component => component.Name == componentName))
			{
				table.AddRow(RunMetadata.ShortKey(run.RunId), StatusMarkup(run.Status), Markup.Escape(component.CommitIsh),
					component.ResultStatus.ToString().ToLowerInvariant(), Markup.Escape(component.ResultTag ?? "-"));
			}
		}

		if (table.Rows.Count == 0)
		{
			table.AddRow("[grey]none[/]", "", "", "", "");
		}

		console.Write(table);
	}

	private async Task CreateTagAsync(GitClient git, IReadOnlyList<GitRef> refs)
	{
		const string other = "Enter a commit hash";
		var targets = refs.Select(reference => $"{reference.Name} ({reference.Commit})").Append(other).ToList();
		var target = await ChooseAsync("Tag which reference?", targets);
		target = target == other
			? await console.PromptAsync(new TextPrompt<string>("Commit hash:"))
			: refs[targets.IndexOf(target)].Name;

		var name = await console.PromptAsync(new TextPrompt<string>("Tag name:")
			.Validate(value => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsWhiteSpace), "[red]A tag name cannot be empty or contain whitespace.[/]"));
		var message = await console.PromptAsync(new TextPrompt<string>("Tag message:").Validate(value => !string.IsNullOrWhiteSpace(value), "[red]An annotated tag needs a message.[/]"));

		await git.RunOrThrowAsync(["tag", "-a", name, "-m", message, target]);
		ShowNotice($"Created annotated tag '{name}' at {target}.");
	}

	// ----- agentic runs -----

	private async Task RunsAsync()
	{
		while (true)
		{
			var runs = await store.ListLatestAsync();
			Screen("Agentic runs", $"{runs.Count} recorded in {store.RepoDir}");
			var table = new Table().Border(TableBorder.Rounded).AddColumns("Run", "Status", "Created", "Selection", "Command");
			foreach (var run in runs)
			{
				table.AddRow(RunMetadata.ShortKey(run.RunId), StatusMarkup(run.Status), Markup.Escape(run.Created),
					Markup.Escape(run.Select), Markup.Escape(AgentCommand.SummarizeCommand(run.Command)));
			}

			if (runs.Count == 0)
			{
				table.AddRow("[grey]none[/]", "", "", "", "");
			}

			console.Write(table);

			var choices = runs.Select(run => $"Open {RunMetadata.ShortKey(run.RunId)}").Append("Refresh").Append("Back").ToList();
			var choice = await ChooseAsync("Run actions", choices);
			if (choice == "Back")
			{
				return;
			}

			if (choice != "Refresh")
			{
				await RunAsync(runs[choices.IndexOf(choice)]);
			}
		}
	}

	private async Task RunAsync(RunMetadata run)
	{
		while (true)
		{
			Screen($"Agentic run {RunMetadata.ShortKey(run.RunId)}", run.RunId);
			var summary = new Grid().AddColumn().AddColumn();
			summary.AddRow("Status", StatusMarkup(run.Status));
			summary.AddRow("Created", Markup.Escape(run.Created));
			summary.AddRow("Finished", Markup.Escape(run.Finished ?? "-"));
			summary.AddRow("Selection", Markup.Escape(run.Select));
			summary.AddRow("Command", Markup.Escape(run.Command));
			summary.AddRow("Workspace", Markup.Escape(run.WorkspacePath));
			summary.AddRow("Agent exit code", run.AgentExitCode?.ToString() ?? "-");
			summary.AddRow("Record", $"{RunMetadata.TagName(run.RunId, run.Lineage)} in {Markup.Escape(store.RepoDir)}");
			console.Write(summary);
			WriteComponents(run);

			if (run.Status == "partial")
			{
				console.MarkupLine($"[yellow]Some components failed:[/] run [bold]bassia agent retry {run.RunId}[/] or [bold]bassia agent abandon {run.RunId}[/].");
			}

			var choice = await ChooseAsync("Run actions", [.. LifecycleStubs, "Back"]);
			if (choice == "Back")
			{
				return;
			}

			ShowNotice($"Not implemented yet: {choice}. No changes were made to the run or to any component.");
		}
	}

	private void WriteComponents(RunMetadata run)
	{
		var table = new Table().Border(TableBorder.Rounded).AddColumns("Component", "Base", "Base commit", "Result", "Result commit", "Result tag");
		foreach (var component in run.Components)
		{
			table.AddRow(Markup.Escape(component.Name), Markup.Escape(component.CommitIsh), component.Commit[..Math.Min(7, component.Commit.Length)],
				component.ResultError is null ? component.ResultStatus.ToString().ToLowerInvariant() : $"[red]failed:[/] {Markup.Escape(component.ResultError)}",
				component.ResultCommit is null ? "-" : component.ResultCommit[..Math.Min(7, component.ResultCommit.Length)], Markup.Escape(component.ResultTag ?? "-"));
		}

		console.Write(table);
	}

	private async Task StartRunAsync()
	{
		Screen("Start an agentic run", "select components at annotated tags, then describe the task");
		if (monorepo.Components.Count == 0)
		{
			await ShowErrorAsync("No components are registered; run 'bassia add-component' first.");
			return;
		}

		var chosen = await console.PromptAsync(new MultiSelectionPrompt<string>()
			.Title("Select the components to work on [grey](space toggles, enter confirms)[/]")
			.Required()
			.UseConverter(Markup.Escape)
			.AddChoices(monorepo.Components.Select(component => component.Name)));

		// A run must select its whole closure; ask for a tag for every member, including the ones pulled in by a reference.
		var closure = monorepo.Closure(chosen);
		var selections = new List<string>();
		foreach (var component in closure)
		{
			var sourceDir = monorepo.SourceRepoDir(component.Name);
			if (!Directory.Exists(sourceDir))
			{
				await ShowErrorAsync($"Component '{component.Name}' has no local repository at '{sourceDir}'. Run 'bassia add-component' first.");
				return;
			}

			var tags = (await GitRef.ListAsync(GitClient.In(sourceDir))).Where(reference => reference.Kind == GitRefKind.AnnotatedTag).ToList();
			if (tags.Count == 0)
			{
				await ShowErrorAsync($"Component '{component.Name}' has no annotated tags. Create one from its component view first.");
				return;
			}

			var labels = tags.Select(tag => $"{tag.Name} ({tag.Commit}) {tag.Subject}").ToList();
			var picked = await ChooseAsync($"Tag for {Markup.Escape(component.Name)}", labels);
			selections.Add($"{component.Name}@{tags[labels.IndexOf(picked)].Name}");
		}

		var prompt = await console.PromptAsync(new TextPrompt<string>("Prompt for the agent:").Validate(value => !string.IsNullOrWhiteSpace(value), "[red]The prompt cannot be empty.[/]"));
		var model = await ChooseOptionalAsync("Model", ["sonnet", "opus", "haiku"]);
		var effort = await ChooseOptionalAsync("Effort", ["low", "medium", "high"]);
		var context = await console.PromptAsync(new TextPrompt<string>("Additional context [grey](optional)[/]:").AllowEmpty());
		agentCommand = await console.PromptAsync(new TextPrompt<string>("Agent command:").DefaultValue(agentCommand));

		var select = string.Join(",", selections);
		var command = await console.PromptAsync(new TextPrompt<string>("Command to run [grey](edit if needed)[/]:")
			.DefaultValue(ComposeCommand(agentCommand, model, effort, prompt, context)));

		console.Write(new Panel($"[bold]bassia agent -select[/] {Markup.Escape(select)} [bold]-run[/] {Markup.Escape(command)}").Border(BoxBorder.Rounded));
		if (!await console.PromptAsync(new ConfirmationPrompt("Start this run?")))
		{
			return;
		}

		console.MarkupLine("[grey]Running the agent; its output follows.[/]");
		var outcome = await startRun(select, command);

		// No Screen() here: the agent's own output stays visible above the outcome.
		console.Write(new Rule($"[bold]Agentic run {RunMetadata.ShortKey(outcome.Metadata.RunId)}[/]").LeftJustified());
		console.MarkupLine($"{(outcome.Ok ? "[green]OK[/]" : "[red]Failed[/]")} {Markup.Escape(outcome.Message)}");
		console.MarkupLine($"Status: {StatusMarkup(outcome.Metadata.Status)}; record {Markup.Escape(outcome.MetadataTags[^1])} in {Markup.Escape(outcome.Store.RepoDir)}");
		WriteComponents(outcome.Metadata);
		await PauseAsync();
	}

	private async Task<string?> ChooseOptionalAsync(string title, IReadOnlyList<string> options)
	{
		const string agentDefault = "Agent default", other = "Other...";
		var choice = await ChooseAsync(title, [agentDefault, .. options, other]);
		return choice switch
		{
			agentDefault => null,
			other => await console.PromptAsync(new TextPrompt<string>($"{title}:")),
			_ => choice
		};
	}

	/// <summary>
	/// The <c>-run</c> value: the agent command, optional model/effort flags, and the prompt as one quoted argument.
	/// The value is handed to the platform shell as a single line, so the context is joined with a space, not a newline.
	/// </summary>
	internal static string ComposeCommand(string agentCommand, string? model, string? effort, string prompt, string? context)
	{
		var fullPrompt = string.IsNullOrWhiteSpace(context) ? prompt.Trim() : $"{prompt.Trim()} Context: {context.Trim()}";
		var flags = (model is null ? "" : $" --model {model}") + (effort is null ? "" : $" --effort {effort}");
		return $"{agentCommand.Trim()}{flags} \"{fullPrompt.Replace("\"", "\\\"")}\"";
	}

	// ----- helpers -----

	private void Screen(string title, string subtitle)
	{
		console.Clear();
		console.Write(new Rule($"[bold]{Markup.Escape(title)}[/]").LeftJustified());
		console.MarkupLine($"[grey]{Markup.Escape(subtitle)}[/]");
	}

	private Task<string> ChooseAsync(string title, IReadOnlyList<string> choices) =>
		console.PromptAsync(new SelectionPrompt<string>()
			.Title(title)
			.PageSize(15)
			.EnableSearch()
			.UseConverter(Markup.Escape)
			.AddChoices(choices));

	private static string StatusMarkup(string status) => status switch
	{
		"completed" => "[green]completed[/]",
		"started" => "[blue]started[/]",
		"failed" or "partial" => $"[red]{status}[/]",
		"abandoned" => "[grey]abandoned[/]",
		_ => Markup.Escape(status)
	};

	private void ShowNotice(string message) => console.Write(new Panel(Markup.Escape(message)).Border(BoxBorder.Rounded).BorderColor(Color.Yellow));

	private async Task ShowErrorAsync(string message)
	{
		console.Write(new Panel($"[red]{Markup.Escape(message)}[/]").Header("Error").Border(BoxBorder.Rounded).BorderColor(Color.Red));
		await PauseAsync();
	}

	private Task PauseAsync() => console.PromptAsync(new TextPrompt<string>("[grey]Press enter to continue[/]").AllowEmpty());
}
