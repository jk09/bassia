namespace Bassia.Tutorial;

using Bassia.Git;
using Bassia.Graph;
using Bassia.Prompt;
using Bassia.Split;

/// <summary>
/// What a guide did: whether it ended well, its closing words, the monorepo it worked on, the commands it ran, and a
/// command line to hand over to (<c>prompt</c>, <c>web</c>) when the user picked one.
/// </summary>
internal sealed record TutorialOutcome(bool Ok, string Message, string? Root, IReadOnlyList<string> Commands, IReadOnlyList<string>? Then = null);

/// <summary>The user ended the guide by closing its input.</summary>
internal sealed class TutorialStopped() : Exception("The input ended.");

/// <summary>
/// The guide behind a bare <c>bassia</c> at a terminal and <c>bassia tutorial</c>. Outside a monorepo it walks a
/// newcomer through setting one up: the repositories that become components, which needs which, and the root folder.
/// Nothing is changed before the plan - the same <c>bassia</c> commands a person would type - is confirmed. Inside a
/// monorepo it shows where the user is and offers a menu of next steps. Every change is made by a <c>bassia</c>
/// command run through <paramref name="executor"/>. At any question, <c>?</c> explains the step, and
/// <c>? &lt;question&gt;</c> asks the LLM.
/// </summary>
internal sealed class TutorialSession(TextReader input, TextWriter output, ICommandExecutor executor, Func<ILlmBackend> llm, string workingDirectory)
{
	private sealed record Step(string Name, string Help);

	private sealed record NewComponent(string Name, string Url, string? DerivedName);

	private const string Concepts =
		"Bassia keeps several git repositories together as one monorepo. Each repository becomes a component - a library, a service, " +
		"an app - and stays an ordinary git repository. A small git repository of its own, the meta-repo (the .bassia folder), records " +
		"the components and which component needs which. When you, or an AI agent, work on a component, Bassia checks out that component " +
		"together with everything it needs, and nothing else.";

	private static readonly Step Offer = new("Welcome", Concepts + "\n\nThe setup takes three questions and changes nothing until you confirm the plan at the end.");

	private static readonly Step Components = new("Components",
		"Give each repository you want in the monorepo: a URL as you would pass to 'git clone' (https://github.com/you/lib.git, " +
		"git@github.com:you/lib.git) or a folder on this computer that is a git repository. Bassia clones it into the monorepo; the " +
		"original is not changed. Each component gets a short name used in every bassia command; the default is the repository's name. " +
		"You can add more later with 'bassia component add -url <url>'.");

	private static readonly Step Needs = new("Dependencies",
		"A component needs another when it cannot be built or worked on without it: an app that uses a library needs the library. " +
		"Bassia calls such a dependency a reference. When an agent works on the app, Bassia checks out the library inside the app's folder " +
		"(at a subfolder named after it), so the app finds it where it expects it. A component cannot need itself, even through others. " +
		"A different subfolder can be set later with 'bassia component set -name <component> -references <other>:<subfolder>'.");

	private static readonly Step RootFolder = new("Root folder",
		"The monorepo is one folder, its root. It holds the meta-repo (.bassia), one folder per component with that component's git " +
		"repository, and a workspace (.workspace) where agents get their checkouts. The folder must be empty or not exist yet, and must " +
		"not be inside another monorepo. Every bassia command run inside it (or with -C <folder>) works on this monorepo.");

	private static readonly Step Plan = new("Plan",
		"These are the commands Bassia runs for you, in this order: 'init' creates the root folder and the meta-repo, and each " +
		"'component add' clones one repository and records it, with -references naming the components it needs. A component is added " +
		"after the ones it needs. Answer n to change nothing; you can run the commands yourself later.");

	private static readonly Step Menu = new("Menu",
		"Pick a number. Adding components and setting dependencies change the meta-repo (each change is a commit in .bassia); a run " +
		"starts an AI agent on a checkout of the components you select and the ones they need, and never touches their main branches " +
		"until you integrate it. Every command is shown before it runs, so you can type it yourself next time.");

	private readonly List<string> ran = [];

	/// <summary>
	/// Runs the guide: the setup outside a monorepo (after a yes to the offer when <paramref name="offer"/> is set), the
	/// menu inside one. The end of the input ends it at any question.
	/// </summary>
	public async Task<TutorialOutcome> RunAsync(bool offer, CancellationToken cancellation)
	{
		var root = Monorepo.FindRoot(workingDirectory);
		try
		{
			return root is null ? await SetupAsync(offer, cancellation) : await WelcomeAsync(root, cancellation);
		}
		catch (TutorialStopped)
		{
			var message = ran.Count == 0
				? "Stopped; nothing was changed. Run 'bassia tutorial' to start again."
				: $"Stopped after {ran.Count} command(s): {string.Join("; ", ran)}.";
			output.WriteLine(message);
			return new TutorialOutcome(true, message, root, ran);
		}
	}

	// ----- a new monorepo -----

	private async Task<TutorialOutcome> SetupAsync(bool offer, CancellationToken cancellation)
	{
		output.WriteLine();
		output.WriteLine("You are not inside a Bassia monorepo.");
		output.WriteLine();
		output.WriteLine(Wrap(Concepts));
		output.WriteLine();
		if (offer)
		{
			if (!await AskYesNoAsync("Set up a new Bassia monorepo now? [y/N] ", false, Offer, cancellation))
			{
				const string later = "No monorepo was set up. Run 'bassia tutorial' whenever you want the guided setup.";
				output.WriteLine(later);
				return new TutorialOutcome(true, later, null, ran);
			}

			output.WriteLine();
		}

		output.WriteLine("Three questions: which repositories, which needs which, and where the monorepo goes. Nothing changes until");
		output.WriteLine("you confirm the plan at the end. Type ? at any question for help, or ? followed by a question to ask the LLM.");

		Heading("Step 1 of 3: components");
		output.WriteLine(Wrap("A component is one of your git repositories: a URL like https://github.com/you/lib.git or a folder on this " +
			"computer. Bassia clones it; the original stays as it is. Enter one per line, and an empty line when you are done."));
		var components = await AskComponentsAsync([], cancellation);

		var needs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
		if (components.Count > 1)
		{
			Heading("Step 2 of 3: dependencies");
			output.WriteLine(Wrap("A component may need others: an app may need a library to build. When you or an agent work on the app, " +
				"Bassia brings the library along, nested in the app's folder. For each component, enter the numbers or names of the ones it " +
				"needs, separated by commas, or just Enter for none."));
			var names = components.Select(component => component.Name).ToList();
			WriteNumbered(names);
			foreach (var component in components)
			{
				needs[component.Name] = await AskNeedsAsync(component.Name, names, name => needs.GetValueOrDefault(name) ?? [], null, cancellation);
			}
		}
		else
		{
			Heading("Step 2 of 3: dependencies");
			output.WriteLine(components.Count == 0
				? "No components, so no dependencies to set."
				: "With a single component there is nothing it could need yet; dependencies come with the next component.");
		}

		Heading("Step 3 of 3: the root folder");
		output.WriteLine(Wrap("The monorepo lives in one folder, its root: the meta-repo (.bassia) goes there, next to one folder per " +
			"component and a workspace for agents. It must be empty or new."));
		var root = await AskRootAsync(cancellation);

		var commands = new List<IReadOnlyList<string>> { new List<string> { "init", "-path", root } };
		commands.AddRange(AddCommands(root, Ordered(components, needs), needs));

		Heading("The plan");
		output.WriteLine("Bassia will run these commands - the same ones you could type yourself:");
		output.WriteLine();
		foreach (var command in commands)
		{
			output.WriteLine($"  {CommandCatalog.Display(command)}");
		}

		output.WriteLine();
		if (!await AskYesNoAsync("Run them now? [Y/n] ", true, Plan, cancellation))
		{
			const string declined = "Nothing was changed. The commands above set up the same monorepo whenever you run them.";
			output.WriteLine(declined);
			return new TutorialOutcome(true, declined, null, ran);
		}

		output.WriteLine();
		if (await RunAllAsync(commands, cancellation) is { } failure)
		{
			return new TutorialOutcome(false, failure, Directory.Exists(Path.Combine(root, Monorepo.MetaRepoFolderName)) ? root : null, ran);
		}

		var monorepo = Monorepo.Load(root);
		var graph = new ComponentGraph(monorepo.Components);
		var main = graph.Roots().FirstOrDefault()?.Name ?? "<component>";
		Heading("Done");
		output.WriteLine($"Your monorepo is ready at {root}.");
		if (monorepo.Components.Count > 0)
		{
			output.WriteLine();
			output.WriteLine("Its components, each above the ones it needs:");
			output.WriteLine();
			output.WriteLine(Indent(graph.RenderText()));
		}

		output.WriteLine();
		output.WriteLine("What next - run these inside the root folder, or anywhere with -C <root>:");
		output.WriteLine();
		output.WriteLine("  bassia status -human                  what is in the monorepo and what is going on");
		output.WriteLine("  bassia component add -url <url>       add another repository");
		output.WriteLine($"  bassia run start -select {main} -prompt \"<what to do>\"");
		output.WriteLine($"                                        let an AI agent work on {main} and what it needs");
		output.WriteLine("  bassia web                            a dashboard of the monorepo in your browser");
		output.WriteLine("  bassia prompt <what you want>         say what you want in plain English");
		output.WriteLine("  bassia                                this guide again, with options for the monorepo");
		return new TutorialOutcome(true, $"Set up the monorepo at '{root}' with {monorepo.Components.Count} component(s).", root, ran);
	}

	private async Task<List<NewComponent>> AskComponentsAsync(IReadOnlyCollection<string> registered, CancellationToken cancellation)
	{
		var components = new List<NewComponent>();
		while (true)
		{
			var answer = await ReadAsync($"Repository {components.Count + 1} (Enter when done): ", Components, cancellation);
			if (answer.Length == 0)
			{
				if (components.Count > 0 || registered.Count > 0
					|| await AskYesNoAsync("No repositories yet. Go on without any? You can add them later. [y/N] ", false, Components, cancellation))
				{
					return components;
				}

				continue;
			}

			var url = Locate(answer);
			if (components.Any(component => component.Url == url))
			{
				output.WriteLine("  That one is already on the list.");
				continue;
			}

			var check = await new GitClient(workingDirectory).RunAsync(["ls-remote", url, "HEAD"], null,
				new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
			if (check.ExitCode != 0)
			{
				output.WriteLine($"  There is no git repository Bassia can read at '{answer}': {FirstLine(check.Error)}");
				output.WriteLine("  Check the URL or folder and try again.");
				continue;
			}

			string? derived;
			try
			{
				derived = ComponentCommands.DeriveComponentName(url);
			}
			catch (MonorepoException)
			{
				derived = null;
			}

			var taken = registered.Concat(components.Select(component => component.Name)).ToList();
			var name = await AskNameAsync(derived, taken, cancellation);
			components.Add(new NewComponent(name, url, derived));
			output.WriteLine($"  '{name}' will be a component.");
		}
	}

	private async Task<string> AskNameAsync(string? derived, IReadOnlyList<string> taken, CancellationToken cancellation)
	{
		var suggestion = derived is not null && NameProblem(derived, taken) is null ? derived : null;
		while (true)
		{
			var answer = await ReadAsync(suggestion is null ? "  Name it: " : $"  Name it [{suggestion}]: ", Components, cancellation);
			var name = answer.Length == 0 ? suggestion : answer;
			if (name is null)
			{
				output.WriteLine("  Give it a short name, like lib or web-app.");
				continue;
			}

			if (NameProblem(name, taken) is { } problem)
			{
				output.WriteLine($"  {problem}");
				continue;
			}

			return name;
		}
	}

	private static string? NameProblem(string name, IReadOnlyList<string> taken) =>
		!SplitPlan.IsValidName(name) ? $"'{name}' cannot be a name: use letters, digits, '.', '_' and '-', starting with a letter or digit."
		: taken.Contains(name, StringComparer.OrdinalIgnoreCase) ? $"There is already a component named '{name}'; pick another name."
		: null;

	/// <summary>
	/// Asks which of <paramref name="choices"/> <paramref name="component"/> needs, by number or name; Enter means none,
	/// or the current value when <paramref name="current"/> is given. Refuses an unknown name, itself and a cycle
	/// through <paramref name="needsOf"/>.
	/// </summary>
	private async Task<IReadOnlyList<string>> AskNeedsAsync(string component, IReadOnlyList<string> choices,
		Func<string, IReadOnlyList<string>> needsOf, IReadOnlyList<string>? current, CancellationToken cancellation)
	{
		var question = current is null
			? $"  {component} needs: "
			: $"  {component} needs (now: {(current.Count == 0 ? "nothing" : string.Join(", ", current))}; Enter keeps it, - for nothing): ";
		while (true)
		{
			var answer = await ReadAsync(question, Needs, cancellation);
			if (answer.Length == 0)
			{
				return current ?? [];
			}

			if (answer == "-")
			{
				return [];
			}

			var picked = new List<string>();
			string? problem = null;
			foreach (var word in answer.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
			{
				var name = int.TryParse(word, out var number) && number >= 1 && number <= choices.Count
					? choices[number - 1]
					: choices.FirstOrDefault(choice => choice.Equals(word, StringComparison.OrdinalIgnoreCase));
				if (name is null)
				{
					problem = $"There is no component '{word}'. Use the numbers or names listed above.";
					break;
				}

				if (name == component)
				{
					problem = "A component cannot need itself.";
					break;
				}

				if (!picked.Contains(name))
				{
					picked.Add(name);
				}
			}

			if (problem is null && picked.Select(name => PathTo(name, component, needsOf)).FirstOrDefault(path => path is not null) is { } cycle)
			{
				problem = $"That would be a cycle: {component} -> {string.Join(" -> ", cycle)}. A component cannot need itself, even through others.";
			}

			if (problem is not null)
			{
				output.WriteLine($"  {problem}");
				continue;
			}

			return picked;
		}
	}

	/// <summary>A chain of needs from <paramref name="from"/> to <paramref name="to"/> (both included), or null.</summary>
	private static IReadOnlyList<string>? PathTo(string from, string to, Func<string, IReadOnlyList<string>> needsOf)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		List<string>? Walk(string name)
		{
			if (name == to)
			{
				return [name];
			}

			if (!seen.Add(name))
			{
				return null;
			}

			foreach (var next in needsOf(name))
			{
				if (Walk(next) is { } rest)
				{
					rest.Insert(0, name);
					return rest;
				}
			}

			return null;
		}

		return Walk(from);
	}

	private async Task<string> AskRootAsync(CancellationToken cancellation)
	{
		var suggestion = SuggestRoot();
		while (true)
		{
			var answer = await ReadAsync($"Folder [{suggestion}]: ", RootFolder, cancellation);
			string path;
			try
			{
				path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(answer.Length == 0 ? suggestion : answer, workingDirectory));
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
			{
				output.WriteLine($"  '{answer}' is not a folder path: {ex.Message}");
				continue;
			}

			if (File.Exists(path))
			{
				output.WriteLine($"  '{path}' is a file. Give a folder that is empty or does not exist yet.");
			}
			else if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
			{
				output.WriteLine($"  '{path}' is not empty. Give a folder that is empty or does not exist yet, so nothing else ends up in the monorepo.");
			}
			else if (Monorepo.FindRoot(path) is { } outer)
			{
				output.WriteLine($"  '{path}' is inside the monorepo '{outer}'. Give a folder outside it.");
			}
			else
			{
				return path;
			}
		}
	}

	/// <summary>The current folder when it is empty, else a new or empty <c>monorepo</c> folder in it.</summary>
	private string SuggestRoot()
	{
		if (!Directory.EnumerateFileSystemEntries(workingDirectory).Any())
		{
			return workingDirectory;
		}

		for (var n = 1; ; n++)
		{
			var candidate = Path.Combine(workingDirectory, n == 1 ? "monorepo" : $"monorepo-{n}");
			if (!Directory.Exists(candidate) && !File.Exists(candidate) || Directory.Exists(candidate) && !Directory.EnumerateFileSystemEntries(candidate).Any())
			{
				return candidate;
			}
		}
	}

	/// <summary>A folder given as an answer becomes its full path, so the clone finds it from the monorepo; anything else is a URL.</summary>
	private string Locate(string answer)
	{
		try
		{
			var path = Path.GetFullPath(answer, workingDirectory);
			return Directory.Exists(path) ? Path.TrimEndingDirectorySeparator(path) : answer;
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return answer;
		}
	}

	/// <summary>The new components, each after the ones it needs.</summary>
	private static List<NewComponent> Ordered(IReadOnlyList<NewComponent> components, IReadOnlyDictionary<string, IReadOnlyList<string>> needs)
	{
		var ordered = new List<NewComponent>();
		void Visit(NewComponent component)
		{
			if (ordered.Contains(component))
			{
				return;
			}

			foreach (var name in needs.GetValueOrDefault(component.Name) ?? [])
			{
				if (components.FirstOrDefault(candidate => candidate.Name == name) is { } needed)
				{
					Visit(needed);
				}
			}

			ordered.Add(component);
		}

		foreach (var component in components)
		{
			Visit(component);
		}

		return ordered;
	}

	private static IEnumerable<IReadOnlyList<string>> AddCommands(string root, IEnumerable<NewComponent> components,
		IReadOnlyDictionary<string, IReadOnlyList<string>> needs)
	{
		foreach (var component in components)
		{
			var args = new List<string> { "-C", root, "component", "add", "-url", component.Url };
			if (component.Name != component.DerivedName)
			{
				args.AddRange(["-name", component.Name]);
			}

			if (needs.GetValueOrDefault(component.Name) is { Count: > 0 } needed)
			{
				args.AddRange(["-references", string.Join(",", needed)]);
			}

			yield return args;
		}
	}

	// ----- an existing monorepo -----

	private async Task<TutorialOutcome> WelcomeAsync(string root, CancellationToken cancellation)
	{
		output.WriteLine();
		output.WriteLine($"You are in the Bassia monorepo at {root}.");
		var status = await executor.ExecuteAsync(["-C", root, "status"], cancellation);
		if (status.Result is { } result)
		{
			output.WriteLine(MessageOf(status) ?? "");
			if (result.TryGetValue("graph", out var graph) && graph is string tree && result.TryGetValue("components", out var count) && count is long and > 0)
			{
				output.WriteLine();
				output.WriteLine(Indent(tree));
			}
		}

		output.WriteLine("Type ? at any question for help, or ? followed by a question to ask the LLM.");
		while (true)
		{
			var monorepo = Monorepo.Load(root);
			output.WriteLine();
			if (monorepo.Components.Count == 0)
			{
				output.WriteLine("The monorepo has no components yet: start with 1.");
			}

			output.WriteLine("  1. Add components");
			output.WriteLine("  2. Set what a component needs (its dependencies)");
			output.WriteLine("  3. Show the status");
			output.WriteLine("  4. Start an agentic run");
			output.WriteLine("  5. Ask in plain English (bassia prompt)");
			output.WriteLine("  6. Open the web dashboard (bassia web)");
			output.WriteLine("  q. Quit");
			var choice = (await ReadAsync("Choice: ", Menu, cancellation)).ToLowerInvariant();
			switch (choice)
			{
				case "1":
					await AddComponentsAsync(monorepo, cancellation);
					break;
				case "2":
					await SetNeedsAsync(monorepo, cancellation);
					break;
				case "3":
					var shown = await executor.ExecuteAsync(["-C", root, "status", "-human"], cancellation);
					output.WriteLine();
					output.WriteLine((shown.Output + shown.Error).TrimEnd());
					break;
				case "4":
					await StartRunAsync(monorepo, cancellation);
					break;
				case "5":
					var ask = await ReadAsync("What do you want to do? ", Menu, cancellation);
					if (ask.Length > 0)
					{
						return HandOff(root, ["-C", root, "prompt", ask]);
					}

					break;
				case "6":
					return HandOff(root, ["-C", root, "web"]);
				case "q" or "quit" or "exit":
					var bye = ran.Count == 0 ? "Nothing was changed." : $"Ran {ran.Count} command(s).";
					output.WriteLine(bye);
					return new TutorialOutcome(true, bye, root, ran);
				case "":
					break;
				default:
					output.WriteLine($"'{choice}' is not on the menu; pick a number or q.");
					break;
			}
		}
	}

	private TutorialOutcome HandOff(string root, IReadOnlyList<string> args)
	{
		output.WriteLine($"> {CommandCatalog.Display(args)}");
		return new TutorialOutcome(true, $"Handed over to '{CommandCatalog.Display(args)}'.", root, ran, args);
	}

	private async Task AddComponentsAsync(Monorepo monorepo, CancellationToken cancellation)
	{
		var registered = monorepo.Components.Select(component => component.Name).ToList();
		var components = await AskComponentsAsync(registered, cancellation);
		if (components.Count == 0)
		{
			return;
		}

		var names = registered.Concat(components.Select(component => component.Name)).ToList();
		var needs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
		IReadOnlyList<string> NeedsOf(string name) =>
			needs.GetValueOrDefault(name) ?? monorepo.FindComponent(name)?.References.Select(reference => reference.Name).ToList() ?? [];
		if (names.Count > 1)
		{
			output.WriteLine("What does each new component need? Numbers or names, separated by commas; Enter for nothing.");
			WriteNumbered(names);
			foreach (var component in components)
			{
				needs[component.Name] = await AskNeedsAsync(component.Name, names, NeedsOf, null, cancellation);
			}
		}

		await ConfirmAndRunAsync(AddCommands(monorepo.Root, Ordered(components, needs), needs).ToList(), cancellation);
	}

	private async Task SetNeedsAsync(Monorepo monorepo, CancellationToken cancellation)
	{
		var names = monorepo.Components.Select(component => component.Name).ToList();
		if (names.Count < 2)
		{
			output.WriteLine("Dependencies need at least two components; add one first.");
			return;
		}

		WriteNumbered(names);
		var component = await AskComponentAsync("Which component? ", names, cancellation);
		var definition = monorepo.FindComponent(component)!;
		var current = definition.References.Select(reference => reference.Name).ToList();
		IReadOnlyList<string> NeedsOf(string name) => monorepo.FindComponent(name)?.References.Select(reference => reference.Name).ToList() ?? [];
		var needs = await AskNeedsAsync(component, names, name => name == component ? [] : NeedsOf(name), current, cancellation);
		if (needs.SequenceEqual(current))
		{
			output.WriteLine("No change.");
			return;
		}

		// A reference kept from before keeps its subfolder.
		var references = needs.Select(name => definition.References.FirstOrDefault(reference => reference.Name == name) is { } kept && kept.Path != kept.Name
			? $"{name}:{kept.Path}"
			: name).ToList();
		IReadOnlyList<string> command = references.Count == 0
			? ["-C", monorepo.Root, "component", "set", "-name", component, "-clear-references"]
			: ["-C", monorepo.Root, "component", "set", "-name", component, "-references", string.Join(",", references)];
		await ConfirmAndRunAsync([command], cancellation);
	}

	private async Task StartRunAsync(Monorepo monorepo, CancellationToken cancellation)
	{
		if (monorepo.Components.Count == 0)
		{
			output.WriteLine("A run needs components; add one first.");
			return;
		}

		var names = monorepo.Components.Select(component => component.Name).ToList();
		var suggestion = string.Join(",", new ComponentGraph(monorepo.Components).Roots().Select(component => component.Name));
		WriteNumbered(names);
		List<string> selected;
		while (true)
		{
			var answer = await ReadAsync($"Components to work on (numbers or names; what they need comes along) [{suggestion}]: ", Menu, cancellation);
			var words = (answer.Length == 0 ? suggestion : answer).Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
			selected = words.Select(word => int.TryParse(word, out var number) && number >= 1 && number <= names.Count
				? names[number - 1]
				: names.FirstOrDefault(name => name.Equals(word, StringComparison.OrdinalIgnoreCase))).OfType<string>().Distinct().ToList();
			if (selected.Count == words.Length && selected.Count > 0)
			{
				break;
			}

			output.WriteLine("  Use the numbers or names listed above.");
		}

		string task;
		do
		{
			task = await ReadAsync("What should the agent do? ", Menu, cancellation);
		}
		while (task.Length == 0);

		await ConfirmAndRunAsync([["-C", monorepo.Root, "run", "start", "-select", string.Join(",", selected), "-detach", "-prompt", task]], cancellation);
	}

	private async Task<string> AskComponentAsync(string question, IReadOnlyList<string> names, CancellationToken cancellation)
	{
		while (true)
		{
			var answer = await ReadAsync(question, Menu, cancellation);
			var name = int.TryParse(answer, out var number) && number >= 1 && number <= names.Count
				? names[number - 1]
				: names.FirstOrDefault(candidate => candidate.Equals(answer, StringComparison.OrdinalIgnoreCase));
			if (name is not null)
			{
				return name;
			}

			output.WriteLine("  Use a number or name listed above.");
		}
	}

	private async Task ConfirmAndRunAsync(IReadOnlyList<IReadOnlyList<string>> commands, CancellationToken cancellation)
	{
		output.WriteLine();
		foreach (var command in commands)
		{
			output.WriteLine($"  {CommandCatalog.Display(command)}");
		}

		if (await AskYesNoAsync(commands.Count == 1 ? "Run it? [Y/n] " : "Run them? [Y/n] ", true, Menu, cancellation))
		{
			await RunAllAsync(commands, cancellation);
		}
	}

	// ----- running, asking, explaining -----

	/// <summary>Runs the commands in order, each shown with its result; returns what went wrong, or null when all succeeded.</summary>
	private async Task<string?> RunAllAsync(IReadOnlyList<IReadOnlyList<string>> commands, CancellationToken cancellation)
	{
		for (var index = 0; index < commands.Count; index++)
		{
			var line = CommandCatalog.Display(commands[index]);
			output.WriteLine($"> {line}");
			var outcome = await executor.ExecuteAsync(commands[index], cancellation);
			var message = MessageOf(outcome) ?? FirstLine(outcome.Error + outcome.Output);
			if (outcome.ExitCode == 0)
			{
				ran.Add(line);
				output.WriteLine($"  {message}");
				continue;
			}

			output.WriteLine($"  It failed: {message}");
			var failure = $"'{line}' failed: {message}";
			if (ran.Count > 0)
			{
				output.WriteLine($"Done before it: {string.Join("; ", ran)}.");
			}

			var rest = commands.Skip(index + 1).Select(command => CommandCatalog.Display(command)).ToList();
			if (rest.Count > 0)
			{
				output.WriteLine("Not run yet:");
				foreach (var command in rest)
				{
					output.WriteLine($"  {command}");
				}
			}

			output.WriteLine("Fix the cause and run the failed command and the rest yourself, or run 'bassia tutorial' again.");
			return failure;
		}

		return null;
	}

	private static string? MessageOf(CommandOutcome outcome) =>
		outcome.Result is { } result
			? (result.TryGetValue("message", out var message) ? message : result.TryGetValue("error", out var error) ? error : null) as string
			: null;

	private async Task<bool> AskYesNoAsync(string question, bool defaultYes, Step step, CancellationToken cancellation)
	{
		while (true)
		{
			var answer = (await ReadAsync(question, step, cancellation)).ToLowerInvariant();
			switch (answer)
			{
				case "":
					return defaultYes;
				case "y" or "yes":
					return true;
				case "n" or "no":
					return false;
				default:
					output.WriteLine("  Answer y or n.");
					break;
			}
		}
	}

	/// <summary>One answer, trimmed; <c>?</c> and <c>? &lt;question&gt;</c> are answered here and asked again.</summary>
	private async Task<string> ReadAsync(string question, Step step, CancellationToken cancellation)
	{
		while (true)
		{
			output.Write(question);
			output.Flush();
			var line = input.ReadLine();
			if (line is null)
			{
				output.WriteLine();
				throw new TutorialStopped();
			}

			line = line.Trim();
			if (!line.StartsWith('?'))
			{
				return line;
			}

			await ExplainAsync(step, line[1..].Trim(), cancellation);
		}
	}

	private async Task ExplainAsync(Step step, string question, CancellationToken cancellation)
	{
		output.WriteLine();
		if (question.Length > 0)
		{
			try
			{
				var backend = llm();
				output.WriteLine($"(asking {backend.Name}...)");
				var reply = await backend.CompleteAsync(new LlmRequest(ExplainSystemPrompt, $"# Where they are\n\n{step.Name}: {step.Help}\n\n# Their question\n\n{question}\n"), cancellation);
				output.WriteLine(reply.Trim());
				output.WriteLine();
				return;
			}
			catch (PromptException ex)
			{
				output.WriteLine($"The LLM could not answer ({ex.Message}). Set it up with 'bassia config set llm.command <command>'. Here is the built-in help:");
				output.WriteLine();
			}
		}

		output.WriteLine(Wrap($"{step.Name}: {step.Help}"));
		if (question.Length == 0)
		{
			output.WriteLine("Ask your own question with '? <question>'; the LLM bassia prompt uses answers it.");
		}

		output.WriteLine();
	}

	private static string ExplainSystemPrompt =>
		"You help a person who is new to Bassia and is being guided through it on the command line. " + Concepts +
		"\n\nAnswer their question about the step they are at, in plain text without Markdown, in at most 8 short lines. When a bassia " +
		"command helps, show it. Do not ask questions back; the guide continues after your answer.\n\n# Commands\n\n" + CommandCatalog.Render();

	private void Heading(string title)
	{
		output.WriteLine();
		output.WriteLine(title);
		output.WriteLine(new string('-', title.Length));
	}

	private void WriteNumbered(IReadOnlyList<string> names)
	{
		for (var i = 0; i < names.Count; i++)
		{
			output.WriteLine($"  {i + 1}. {names[i]}");
		}
	}

	private static string FirstLine(string text) =>
		text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "no details.";

	private static string Indent(string text) => string.Join('\n', text.Replace("\r", "").Split('\n').Select(line => "  " + line));

	/// <summary>Breaks prose into lines of at most 110 characters.</summary>
	private static string Wrap(string text)
	{
		const int width = 110;
		var lines = new List<string>();
		foreach (var paragraph in text.Replace("\r", "").Split('\n'))
		{
			var line = "";
			foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				if (line.Length > 0 && line.Length + 1 + word.Length > width)
				{
					lines.Add(line);
					line = word;
				}
				else
				{
					line = line.Length == 0 ? word : $"{line} {word}";
				}
			}

			lines.Add(line);
		}

		return string.Join('\n', lines);
	}
}
