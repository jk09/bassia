namespace Bassia.Ui;

using System.Globalization;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Spectre.Console;

/// <summary>
/// The interactive frontend (<c>bassia ui</c>): a live board over the components, their git refs and the agentic
/// runs of one monorepo. Reads through the same model as the scriptable commands and never keeps state of its
/// own; the mutating actions (annotated tag, run start, run cancel) go through the same code paths as the CLI.
///
/// Navigation is keys, not menus: <c>1</c> and <c>2</c> switch between the two boards from anywhere, so the
/// frontend never imposes a wait of its own. Data entry (the start-a-run wizard, tag details, export paths) stays
/// in Spectre prompts. Runs execute in the background through <see cref="RunSupervisor"/>, so starting one returns
/// to the board immediately and several can run at once.
/// </summary>
internal sealed class InteractiveSession
{
	public const string DefaultAgentCommand = "claude -p --permission-mode acceptEdits";

	/// <summary>Lifecycle actions the run model does not support yet. <c>Stop</c> used to be among them.</summary>
	private static readonly string[] LifecycleStubs = ["Suspend", "Resume", "Hand off", "Integrate results"];

	/// <summary>Repaint interval while something is live. An idle board is not repainted at all, so it does not flicker.</summary>
	private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

	private enum View { Components, Runs }

	private readonly IAnsiConsole console;
	private readonly Monorepo monorepo;
	private readonly RunMetadataStore store;
	private readonly RunSupervisor supervisor;
	private readonly ComponentGraph graph;

	private View view = View.Components;
	private int componentIndex;
	private int runIndex;
	private int frame;
	private bool quit;
	private string? notice;
	private string noticeStyle = "yellow";
	private string agentCommand = DefaultAgentCommand;
	private IReadOnlyList<ComponentStatus> componentStatus = [];
	private IReadOnlyList<RunMetadata> storedRuns = [];

	public InteractiveSession(IAnsiConsole console, Monorepo monorepo, RunMetadataStore store, RunSupervisor supervisor)
	{
		this.console = console;
		this.monorepo = monorepo;
		this.store = store;
		this.supervisor = supervisor;
		graph = new ComponentGraph(monorepo.Components);
	}

	/// <summary>
	/// How long the session waits for a keystroke before giving up. Infinite in a terminal; tests set it so a
	/// script that forgets to quit fails instead of hanging.
	/// </summary>
	internal TimeSpan IdleTimeout { get; set; } = Timeout.InfiniteTimeSpan;

	public async Task RunAsync()
	{
		await RefreshAsync();
		while (!quit)
		{
			Render();
			var key = await NextKeyAsync();
			notice = null;
			try
			{
				await HandleAsync(key);
			}
			catch (Exception ex) when (IsExpected(ex))
			{
				await ShowErrorAsync(ex.Message);
			}
		}
	}

	private static bool IsExpected(Exception ex) =>
		ex is GitException or MonorepoException or AgentException or IOException or UnauthorizedAccessException;

	// ----- the key loop -----

	/// <summary>
	/// Waits for a keystroke, repainting on every tick while a run is live so the boards animate and pick up the
	/// progress of the background runs. A run reaching its end reloads the stored records, so its card turns from
	/// the live view of it into the recorded one without the user asking.
	/// </summary>
	private async Task<ConsoleKeyInfo> NextKeyAsync()
	{
		var waited = TimeSpan.Zero;
		while (!console.Input.IsKeyAvailable())
		{
			await Task.Delay(Tick);
			waited += Tick;
			if (IdleTimeout != Timeout.InfiniteTimeSpan && waited > IdleTimeout)
			{
				throw new TimeoutException($"No input for {IdleTimeout}; the frontend was left waiting on the {view} view.");
			}

			var settled = supervisor.ConsumeSettled();
			if (settled)
			{
				await ReloadRunsAsync();
			}

			if (settled || supervisor.HasLive)
			{
				frame++;
				Render();
			}
		}

		return console.Input.ReadKey(intercept: true) ?? default;
	}

	/// <summary>
	/// The command a key spells, or <c>'\0'</c> for a key that produces no character. Commands are identified by
	/// their character and navigation by <see cref="ConsoleKeyInfo.Key"/>, never the other way round: the two
	/// numberings overlap (<see cref="ConsoleKey.F5"/> and <c>'t'</c> are both 116, <see cref="ConsoleKey.RightArrow"/>
	/// and <c>'\''</c> both 39), so a console that derives one from the other would confuse them.
	/// </summary>
	private static char Command(ConsoleKeyInfo key) =>
		key.KeyChar is >= '!' and <= '~' ? char.ToLowerInvariant(key.KeyChar) : '\0';

	private async Task HandleAsync(ConsoleKeyInfo key)
	{
		// No command key of this frontend shares a number with one of these, so they can be matched first.
		switch (key.Key)
		{
			case ConsoleKey.LeftArrow or ConsoleKey.UpArrow:
				Move(-1);
				return;
			case ConsoleKey.RightArrow or ConsoleKey.DownArrow or ConsoleKey.Tab:
				Move(1);
				return;
			case ConsoleKey.Enter or ConsoleKey.Spacebar:
				await OpenAsync();
				return;

			// F5 and 't' do share one, so F5 counts only when the key really produced no character.
			case ConsoleKey.F5 when key.KeyChar == '\0':
				await RefreshAsync();
				Notify("Refreshed.");
				return;
		}

		switch (Command(key))
		{
			case '1':
				view = View.Components;
				break;
			case '2':
				view = View.Runs;
				break;
			case 'q':
				await QuitAsync();
				break;
			case 'n':
				await StartRunAsync();
				break;
			case 'r':
				await RefreshAsync();
				Notify("Refreshed.");
				break;
			case 'g':
				await GraphAsync();
				break;
			case 't':
				await TagSelectedComponentAsync();
				break;
			case 'x':
				StopSelectedRun();
				break;
			case '?' or 'h':
				await HelpAsync();
				break;
		}
	}

	private void Move(int delta)
	{
		var count = view == View.Components ? Board().Order.Count : Cards().Count;
		if (count == 0)
		{
			return;
		}

		if (view == View.Components)
		{
			componentIndex = ((componentIndex + delta) % count + count) % count;
		}
		else
		{
			runIndex = ((runIndex + delta) % count + count) % count;
		}
	}

	private Task OpenAsync() => view == View.Components ? OpenComponentAsync() : OpenRunAsync();

	private async Task QuitAsync()
	{
		var live = supervisor.LiveCount;
		if (live > 0)
		{
			// The runs are tasks of this process: leaving would orphan their agent processes and lose the record
			// update they still owe, so the frontend offers to stop them and waits until they have.
			console.MarkupLine($"[yellow]{live} agentic run(s) are still running in this session.[/]");
			if (!await console.PromptAsync(new ConfirmationPrompt("Cancel them and quit?")))
			{
				Notify("Still running; the board keeps them.");
				return;
			}

			supervisor.CancelAll();
			console.MarkupLine("[grey]Stopping the running agents...[/]");
			await supervisor.WhenAllSettledAsync();
		}

		quit = true;
	}

	// ----- rendering -----

	private int Width => Math.Max(40, console.Profile.Width - 2);

	private void Render()
	{
		console.Clear();
		console.Write(new Rule($"[bold]Bassia[/] [grey]{Markup.Escape(monorepo.Root)}[/]").LeftJustified());
		console.MarkupLine(Tabs());
		console.WriteLine();

		if (view == View.Components)
		{
			var board = Board();
			// A refresh can drop the component the selection was on; the board and the actions must agree on which
			// card is highlighted, so the index is settled here rather than clamped at each reader.
			componentIndex = Clamp(componentIndex, board.Order.Count);
			console.Write(new Markup(board.Render(componentIndex, ActiveRunsByComponent())));
			console.WriteLine();
			console.MarkupLine(board.Order.Count == 0
				? "[grey]components.toml is empty.[/]"
				: $"[grey]selected[/] [bold]{Markup.Escape(board.Order[componentIndex])}[/]");
		}
		else
		{
			var cards = Cards();
			runIndex = Clamp(runIndex, cards.Count);
			console.Write(new Markup(new RunBoard(cards, Width).Render(runIndex, frame)));
			console.WriteLine();
			if (cards.Count == 0)
			{
				console.MarkupLine($"[grey]no runs recorded in {Markup.Escape(store.RepoDir)}.[/]");
			}
			else
			{
				console.MarkupLine($"[grey]selected[/] [bold]{Markup.Escape(cards[runIndex].Label)}[/] [grey]{Markup.Escape(cards[runIndex].Select)}[/]");
			}
		}

		console.WriteLine();
		WriteNotice();
		console.MarkupLine(Keys());
	}

	private string Tabs()
	{
		var live = supervisor.LiveCount;
		var runs = Cards().Count;
		return Tab(View.Components, "1", $"Components ({monorepo.Components.Count})")
			+ "  "
			+ Tab(View.Runs, "2", live > 0 ? $"Agentic runs ({runs}, {live} live)" : $"Agentic runs ({runs})");
	}

	private string Tab(View target, string key, string label) =>
		view == target
			? $"[black on white] {key} {Markup.Escape(label)} [/]"
			: $"[grey] {key} {Markup.Escape(label)} [/]";

	private void WriteNotice()
	{
		if (notice is not null)
		{
			console.MarkupLine($"[{noticeStyle}]{Markup.Escape(notice)}[/]");
		}
	}

	private string Keys()
	{
		const string shared = "[bold]1[/]/[bold]2[/] view  [bold]arrows[/] select  [bold]enter[/] open  [bold]n[/] new run  [bold]r[/] refresh  [bold]?[/] help  [bold]q[/] quit";
		return view == View.Components
			? $"[grey][bold]t[/] tag  [bold]g[/] graph & export  {shared}[/]"
			: $"[grey][bold]x[/] stop  {shared}[/]";
	}

	private void Notify(string message, string style = "yellow")
	{
		notice = message;
		noticeStyle = style;
	}

	// ----- data -----

	private ComponentBoard Board() => new(componentStatus, graph, Width);

	/// <summary>
	/// The runs board: the runs this session started (live, with their output) first, then everything recorded in
	/// <c>.agentic-runs</c> that is not one of them - including runs another process started.
	/// </summary>
	private IReadOnlyList<RunCard> Cards()
	{
		var live = supervisor.Cards();
		var started = live.Where(card => card.RunId is not null).Select(card => card.RunId!).ToHashSet(StringComparer.Ordinal);
		return [.. live, .. storedRuns.Where(run => !started.Contains(run.RunId)).Select(CardOf)];
	}

	private static RunCard CardOf(RunMetadata run) => new(
		run.RunId,
		run.RunId,
		AgentRunContext.PhaseOfStatus(run.Status),
		run.Select,
		run.Components.Select(component => component.Name).ToList(),
		run.Command,
		string.Join(", ", run.Components.Select(component => $"{component.Name}: {component.ResultStatus.ToString().ToLowerInvariant()}")),
		ParseTimestamp(run.Created) ?? DateTimeOffset.UtcNow,
		ParseTimestamp(run.Finished),
		IsLive: false,
		LastOutput: null);

	private static DateTimeOffset? ParseTimestamp(string? value) =>
		value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
			? parsed
			: null;

	private Dictionary<string, int> ActiveRunsByComponent()
	{
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var name in supervisor.Cards().Where(card => card.IsLive).SelectMany(card => card.Components))
		{
			counts[name] = counts.GetValueOrDefault(name) + 1;
		}

		return counts;
	}

	/// <summary>Reloads everything the boards show that comes from git. Called on entry, on demand, and after a run ends.</summary>
	private async Task RefreshAsync()
	{
		await ReloadRunsAsync();

		var statuses = new List<ComponentStatus>();
		foreach (var component in monorepo.Components)
		{
			var runs = storedRuns.Count(run => run.Components.Any(entry => entry.Name == component.Name));
			var sourceDir = monorepo.SourceRepoDir(component.Name);
			if (!Directory.Exists(sourceDir))
			{
				statuses.Add(new ComponentStatus(component, HasRepo: false, 0, 0, null, runs));
				continue;
			}

			try
			{
				var refs = await GitRef.ListAsync(GitClient.In(sourceDir));
				var tags = refs.Where(reference => reference.Kind == GitRefKind.AnnotatedTag).ToList();
				statuses.Add(new ComponentStatus(component, HasRepo: true, tags.Count,
					refs.Count(reference => reference.Kind == GitRefKind.Branch), tags.LastOrDefault()?.Name, runs));
			}
			catch (GitException)
			{
				statuses.Add(new ComponentStatus(component, HasRepo: false, 0, 0, null, runs));
			}
		}

		componentStatus = statuses;
	}

	private async Task ReloadRunsAsync() => storedRuns = await store.ListLatestAsync();

	// ----- components -----

	private static int Clamp(int index, int count) => count == 0 ? 0 : Math.Clamp(index, 0, count - 1);

	private ComponentStatus? SelectedComponent()
	{
		var order = Board().Order;
		return order.Count == 0 ? null : componentStatus.First(status => status.Name == order[Clamp(componentIndex, order.Count)]);
	}

	private async Task OpenComponentAsync()
	{
		if (SelectedComponent() is not { } selected)
		{
			Notify("No components are registered; run 'bassia add-component <url>' first.");
			return;
		}

		var sourceDir = monorepo.SourceRepoDir(selected.Name);
		if (!Directory.Exists(sourceDir))
		{
			await ShowErrorAsync($"Component '{selected.Name}' has no local repository at '{sourceDir}'. Run 'bassia add-component' first.");
			return;
		}

		var git = GitClient.In(sourceDir);
		var refs = await GitRef.ListAsync(git);
		var log = await TreeAsync(git);

		while (true)
		{
			Screen($"Component {selected.Name}", selected.Definition.Url);
			var summary = new Grid().AddColumn().AddColumn();
			summary.AddRow("Source repo", Markup.Escape(sourceDir));
			summary.AddRow("References", Markup.Escape(Join(selected.Definition.References.Select(Describe))));
			summary.AddRow("Referenced by", Markup.Escape(Join(graph.ReferrersOf(selected.Name).Select(Describe))));
			console.Write(summary);

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
			console.Write(new Panel(Markup.Escape(log)).Header("Git tree").Border(BoxBorder.Rounded));
			WriteRunsOf(selected.Name);
			WriteNotice();
			console.MarkupLine("[grey][bold]t[/] create annotated tag  [bold]r[/] refresh  [bold]esc[/] back  [bold]1[/]/[bold]2[/] view[/]");

			var key = await NextKeyAsync();
			notice = null;
			if (SwitchesView(key))
			{
				return;
			}

			var pressed = Command(key);
			if (pressed == 't')
			{
				await CreateTagAsync(git, refs);
			}
			else if (pressed != 'r')
			{
				if (IsBack(key))
				{
					return;
				}

				continue;
			}

			refs = await GitRef.ListAsync(git);
			log = await TreeAsync(git);
			await RefreshAsync();
		}
	}

	private static async Task<string> TreeAsync(GitClient git)
	{
		var log = await git.RunAsync(["log", "--graph", "--oneline", "--decorate", "--all", "--no-color", "-n", "25"]);
		return log.ExitCode == 0 && log.Output.Length > 0 ? log.Output.TrimEnd() : "(no commits)";
	}

	private void WriteRunsOf(string componentName)
	{
		var table = new Table().Border(TableBorder.Rounded).Title("Agentic runs touching this component")
			.AddColumns("Run", "Status", "Base", "Result", "Result tag");
		foreach (var run in storedRuns)
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

	private async Task TagSelectedComponentAsync()
	{
		if (view != View.Components || SelectedComponent() is not { } selected)
		{
			return;
		}

		var sourceDir = monorepo.SourceRepoDir(selected.Name);
		if (!Directory.Exists(sourceDir))
		{
			await ShowErrorAsync($"Component '{selected.Name}' has no local repository at '{sourceDir}'. Run 'bassia add-component' first.");
			return;
		}

		var git = GitClient.In(sourceDir);
		await CreateTagAsync(git, await GitRef.ListAsync(git));
		await RefreshAsync();
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
		Notify($"Created annotated tag '{name}' at {target}.", "green");
	}

	/// <summary>The tree rendering of the reference graph, with the Mermaid and SVG exports.</summary>
	private async Task GraphAsync()
	{
		if (view != View.Components)
		{
			return;
		}

		while (true)
		{
			Screen("Dependency graph", "a component nests the components below it");
			console.Write(new Panel(Markup.Escape(graph.RenderText())).Border(BoxBorder.Rounded));
			WriteNotice();
			console.MarkupLine("[grey][bold]m[/] export as Markdown (Mermaid)  [bold]v[/] export as SVG  [bold]esc[/] back  [bold]1[/]/[bold]2[/] view[/]");

			var key = await NextKeyAsync();
			notice = null;
			if (SwitchesView(key))
			{
				return;
			}

			switch (Command(key))
			{
				case 'm':
					await ExportAsync("components.md", graph.ToMermaidMarkdown());
					break;
				case 'v':
					await ExportAsync("components.svg", graph.ToSvg());
					break;
				default:
					if (IsBack(key))
					{
						return;
					}

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
		Notify($"Written {path}", "green");
	}

	// ----- agentic runs -----

	private RunCard? SelectedRun()
	{
		var cards = Cards();
		return cards.Count == 0 ? null : cards[Clamp(runIndex, cards.Count)];
	}

	private void StopSelectedRun()
	{
		if (view != View.Runs || SelectedRun() is not { } card)
		{
			return;
		}

		Stop(card);
	}

	private void Stop(RunCard card)
	{
		if (!card.IsLive)
		{
			Notify($"Run {card.Label} is not running in this session; nothing to stop.");
		}
		else if (supervisor.Cancel(card.Key))
		{
			Notify($"Stopping run {card.Label}: the agent process tree is being killed.", "red");
		}
	}

	private async Task OpenRunAsync()
	{
		if (SelectedRun() is not { } card)
		{
			Notify("No agentic runs yet; press 'n' to start one.");
			return;
		}

		var key = card.Key;
		var metadata = card.RunId is null ? null : await store.LoadLatestAsync(card.RunId);

		while (true)
		{
			var current = Cards().FirstOrDefault(candidate => candidate.Key == key) ?? card;
			Screen($"Agentic run {current.Label}", current.RunId ?? "not created yet");

			var summary = new Grid().AddColumn().AddColumn();
			summary.AddRow("Phase", $"[{RunBoard.StyleOf(current.Phase)}]{RunBoard.PhaseText(current.Phase)}[/]");
			summary.AddRow("Elapsed", RunBoard.Elapsed(current.Elapsed));
			summary.AddRow("Selection", Markup.Escape(current.Select));
			summary.AddRow("Command", Markup.Escape(current.Command));
			summary.AddRow("Latest", Markup.Escape(current.Message));
			if (metadata is not null)
			{
				summary.AddRow("Status", StatusMarkup(metadata.Status));
				summary.AddRow("Created", Markup.Escape(metadata.Created));
				summary.AddRow("Finished", Markup.Escape(metadata.Finished ?? "-"));
				summary.AddRow("Workspace", Markup.Escape(metadata.WorkspacePath));
				summary.AddRow("Agent exit code", metadata.AgentExitCode?.ToString() ?? "-");
				summary.AddRow("Record", $"{RunMetadata.TagName(metadata.RunId, metadata.Lineage)} in {Markup.Escape(store.RepoDir)}");
			}

			console.Write(summary);

			if (metadata is not null)
			{
				WriteComponents(metadata);
				if (metadata.Status == "partial")
				{
					console.MarkupLine($"[yellow]Some components failed:[/] run [bold]bassia agent retry {metadata.RunId}[/] or [bold]bassia agent abandon {metadata.RunId}[/].");
				}
			}

			WriteOutput(key);
			WriteNotice();
			console.MarkupLine("[grey][bold]x[/] stop  [bold]m[/] more actions  [bold]r[/] refresh  [bold]esc[/] back  [bold]1[/]/[bold]2[/] view[/]");

			var pressed = await NextKeyAsync();
			notice = null;
			if (SwitchesView(pressed))
			{
				return;
			}

			switch (Command(pressed))
			{
				case 'x':
					Stop(current);
					break;
				case 'm':
					var action = await ChooseAsync("Run actions", [.. LifecycleStubs, "Back"]);
					if (action != "Back")
					{
						Notify($"Not implemented yet: {action}. No changes were made to the run or to any component.");
					}

					break;
				case 'r':
					await ReloadRunsAsync();
					metadata = current.RunId is null ? null : await store.LoadLatestAsync(current.RunId);
					break;
				default:
					if (IsBack(pressed))
					{
						return;
					}

					break;
			}
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

	/// <summary>The tail of the agent's output, for a run this session started; a run it only read back has none.</summary>
	private void WriteOutput(string key)
	{
		var output = supervisor.OutputOf(key);
		if (output.Count == 0)
		{
			return;
		}

		const int shown = 15;
		console.Write(new Panel(Markup.Escape(string.Join('\n', output.TakeLast(shown))))
			.Header($"Agent output (last {Math.Min(shown, output.Count)} of {output.Count} lines)").Border(BoxBorder.Rounded));
	}

	/// <summary>
	/// The start-a-run wizard. It ends by handing the run to the supervisor and returning to the runs board at
	/// once: the run continues in the background and the frontend is ready for the next command immediately.
	/// </summary>
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

		var key = supervisor.Start(select, command);
		view = View.Runs;
		runIndex = 0;
		Notify($"Started run {key} in the background; the board follows it. Press 'n' again to start another.", "green");
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

	private async Task HelpAsync()
	{
		Screen("Keys", "the frontend is driven by single keys; prompts are used only for data entry");
		var keys = new Table().Border(TableBorder.Rounded).AddColumns("Key", "Does");
		keys.AddRow("1 / 2", "switch between the components board and the agentic-runs board, from any screen");
		keys.AddRow("arrows / tab", "move the selection between the rectangles");
		keys.AddRow("enter", "open the selected component or run");
		keys.AddRow("n", "start an agentic run; it runs in the background and the board stays usable");
		keys.AddRow("x", "stop the selected run (kills the agent process tree; the record says 'cancelled')");
		keys.AddRow("t", "create an annotated tag in the selected component");
		keys.AddRow("g", "the dependency tree, with the Mermaid and SVG exports");
		keys.AddRow("r / F5", "reload the components and run records from git");
		keys.AddRow("esc", "leave a detail screen");
		keys.AddRow("q", "quit (offers to stop runs that are still going)");
		console.Write(keys);
		await PauseAsync();
	}

	/// <summary>A view-switching key inside a detail screen: the screen returns and the board takes over.</summary>
	private bool SwitchesView(ConsoleKeyInfo key)
	{
		switch (Command(key))
		{
			case '1':
				view = View.Components;
				return true;
			case '2':
				view = View.Runs;
				return true;
			default:
				return false;
		}
	}

	private static bool IsBack(ConsoleKeyInfo key) =>
		Command(key) == 'q' || key.Key is ConsoleKey.Escape or ConsoleKey.Backspace;

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

	private static string Describe(ComponentReference reference) =>
		reference.Path == reference.Name ? reference.Name : $"{reference.Name} (at {reference.Path})";

	private static string Join(IEnumerable<string> values)
	{
		var joined = string.Join(", ", values);
		return joined.Length == 0 ? "-" : joined;
	}

	private static string StatusMarkup(string status) => status switch
	{
		"completed" => "[green]completed[/]",
		"started" => "[blue]started[/]",
		"failed" or "partial" => $"[red]{status}[/]",
		"cancelled" or "abandoned" => $"[grey]{status}[/]",
		_ => Markup.Escape(status)
	};

	private async Task ShowErrorAsync(string message)
	{
		console.Write(new Panel($"[red]{Markup.Escape(message)}[/]").Header("Error").Border(BoxBorder.Rounded).BorderColor(Color.Red));
		await PauseAsync();
	}

	private Task PauseAsync() => console.PromptAsync(new TextPrompt<string>("[grey]Press enter to continue[/]").AllowEmpty());
}
