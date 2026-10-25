namespace Bassia.Cli;

/// <summary>
/// Help as TOML, like every other result: the overview of all commands, every command in full (<c>-all</c>), the
/// subcommands of a group, or one command's summary, usage, switches and examples - generated from
/// <see cref="CommandTable"/>. Bassia has no LLM of its own; an agent learns it from this help and drives it with
/// ordinary command lines, so the overview also explains the concepts and the usual workflow.
/// </summary>
internal static class Help
{
	public const string GlobalUsage = "bassia [-C <path>] <command> [<subcommand>] [-switch [value]]...";

	/// <summary>What a bare <c>bassia</c> prints for a person at a terminal, before the guide; 'bassia help' has everything.</summary>
	public const string ShortText =
		"bassia - a monorepo of git repositories (components), built for AI agents working in parallel.\n" +
		"\n" +
		"Usage: " + GlobalUsage + "\n" +
		"\n" +
		"  bassia help [<command>]     every command, or one command's switches and examples\n" +
		"  bassia help -all            every command in full, for an agent to learn bassia in one call\n" +
		"  bassia tutorial             guided setup of a monorepo, or options for the one you are in\n" +
		"\n" +
		"Every command prints its result as TOML; add -human to read it as Markdown.";

	/// <summary>What Bassia's parts are, for an agent that has only this help to go by.</summary>
	private static readonly (string Name, string Meaning)[] Concepts =
	[
		("monorepo", "A folder holding the meta-repo, one bare git repository per component, the workspace and the run store. Commands find it from the current directory upwards, or from -C <path>."),
		("component", "One git repository of the monorepo (a library, service, app, tool), stored bare next to the meta-repo and registered in .bassia/components.toml. It has a single main branch; to work in parallel on a line of its own, fork it (component fork)."),
		("reference", "A dependency of one component on another (component[:path]). A run checks out the referenced component nested inside the referring one at that path (default: its name). References form an acyclic graph (graph, component show)."),
		("boundary", "A rule over component tags (type:app, scope:billing) in components.toml: which components may reference which, nx-style, with wildcards (boundary list|add|remove|check). A component's visibility limits who may reference it. A change to the graph that adds a violation is refused; violations already there are warnings."),
		("task", "A command a component defines in bassia.toml at its root ([tasks.build] command = ..., depends_on = [\"^build\"]); task run runs tasks over a checkout of a selection, affected tells what a change affects, and merge.verify makes the integration run them before it publishes."),
		("meta-repo", "The .bassia git repository: components.toml (components and references), config.toml (settings, committed), config.user.toml (your own settings, not committed) and skills/. Commands that change it commit it themselves."),
		("tag", "An annotated tag; one tag of the same name in several components marks a combination that belongs together (a release, a baseline). A run selects components at a tag, a commit hash or the main-branch tip."),
		("agentic run", "An agent command run in an isolated run folder under the workspace (.workspace/agent-run-<key>) holding the selected components and what they reference. Afterwards each changed component is committed, tagged agent-run/<key>/<n> and pushed back; main is not touched. Ids look like agent-run-brave-otter-3f2a91; the <key> or a 4+ character prefix of it also works."),
		("agent command", "What a run executes: -run <command...> verbatim, or -prompt <text> appended as one quoted argument to agent.command (default: claude -p --permission-mode acceptEdits). Bassia only starts it; the agent is yours to choose."),
		("lock", "While a run's agent works, the paths matched by lock.files / lock.folders (or -lock-files / -lock-folders) that it changes are held for the run; other runs see the holds (lock list|check), wait for them (lock wait) and record a lock conflict when they change a held path."),
		("integration", "Merging runs' results onto the components' main branches: git first, then the structural merge (weave), then the resolver command (integration.resolver) for semantic conflicts. Results are tagged integration/<key>/<n>; 'integration advance' (or merge.advance = auto) fast-forwards the main branches to them."),
		("configuration", "Layered settings: config.user.toml over config.toml over Bassia's defaults (config list|get|set|unset). Merge and lock settings can be set per component."),
		("skill", "Reusable instructions for a recurring task, kept in the meta-repo as .bassia/skills/<name>/SKILL.md; read them with skill list and skill show.")
	];

	/// <summary>The usual sequence of commands, in order.</summary>
	private static readonly string[] Workflow =
	[
		"bassia init -path <folder>: create the monorepo (or run inside an existing one; 'bassia status' tells which).",
		"bassia component add -url <url> [-references <c[:path]>,...] or component create -name <name>: register the components and what they reference.",
		"bassia component tag / tag create: mark baselines (optional; a run may also start from main-branch tips or commit hashes).",
		"bassia agent start -select <component[@tag|hash],...> (-prompt <text> | -run <command...>) [-detach]: run an agent; follow it with agent list|show|logs|wait|diff.",
		"bassia integration plan -runs <run-id,...|all>: triage how the results would merge, without changing anything.",
		"bassia integration start -runs <run-id,...|all>: merge them; follow with integration show|wait, then integration advance to move the main branches.",
		"bassia status, graph, log, tag list, lock list: look at the monorepo at any time."
	];

	/// <summary>How an agent learns the rest.</summary>
	private static readonly string[] Discover =
	[
		"bassia help -all: every command in full - usage, details, switches and examples - in one result.",
		"bassia help <command> [<subcommand>], or <command> [<subcommand>] -help: one command; bassia help <group>: a group's subcommands.",
		"read_only = true marks a command that changes nothing in the monorepo; interactive = true marks one meant for a person (it reads stdin, asks for confirmation or serves until Ctrl-C). Run the read-only commands that are not interactive freely to explore.",
		"bassia status: whether the current directory is in a monorepo, and what is in it and going on.",
		"bassia config list: every setting with its value, layer and meaning.",
		"bassia skill list, skill show -name <skill>: the monorepo's own instructions for recurring tasks."
	];

	public static Task<int> RunAsync(Invocation invocation) =>
		Task.FromResult(Show((invocation.Get("command") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)));

	/// <summary>Help for the words after <c>bassia help</c>: none, <c>-all</c>, a command or group, or a group and subcommand.</summary>
	public static int Show(IReadOnlyList<string> words)
	{
		if (words.Any(word => word.StartsWith('-') && word.TrimStart('-').Equals("all", StringComparison.OrdinalIgnoreCase)))
		{
			return All();
		}

		words = words.Where(word => !word.StartsWith('-')).ToList();
		if (words.Count == 0)
		{
			return Overview();
		}

		var group = CommandTable.Group(words[0]);
		if (group.Count == 0)
		{
			return ProgramCli.UsageError("help", CommandTable.Replaced.TryGetValue(words[0], out var replacement)
				? replacement
				: $"Unknown command '{words[0]}'. Run 'bassia help' for the list of commands.");
		}

		if (words.Count == 1)
		{
			return group.Count == 1 && group[0].Sub is null ? Command(group[0]) : Group(words[0], group);
		}

		var spec = group.FirstOrDefault(candidate => string.Equals(candidate.Sub, words[1], StringComparison.OrdinalIgnoreCase));
		return spec is null
			? ProgramCli.UsageError("help", $"'bassia {group[0].Name}' has no subcommand '{words[1]}'; it has {string.Join(", ", group.Select(candidate => candidate.Sub))}.")
			: Command(spec);
	}

	private static Dictionary<string, object?> Basics() => new()
	{
		["usage"] = GlobalUsage,
		["global_switches"] = new List<string>
		{
			"-C <path>: run as if bassia was started in <path>; may repeat, each relative to the previous one",
			"-human: print the result as Markdown for a person instead of TOML"
		},
		["grammar"] = "Switches are case-insensitive and may be written -name or --name. A command's main argument may be given without its switch " +
			"(bassia agent show brave-otter-3f2a91). A rest-of-line switch (-run, -resolve, the message of feedback send) takes everything after it " +
			"and comes last. -run and -resolve are shell command lines: one argument is used verbatim, several are joined with spaces (a word " +
			"with whitespace is wrapped in double quotes), so pass a command with its own quotes as one argument. Lists are comma-separated.",
		["result"] = "A result's first line is a TOML comment marking it (the first line of this result), then come ok, command, message (or error) " +
			"and the command's data. When a command streams other output first (agent start without -detach), parse from the last line that is " +
			"exactly that marker.",
		["exit_codes"] = "0: ok, the result is on stdout. 1: the command failed, the result (with error) is on stderr. 2: the command line does not " +
			"parse (unknown command, subcommand or switch, missing value), the result is on stderr."
	};

	private static int Overview()
	{
		var data = Basics();
		data["concept"] = Concepts.Select(concept => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["name"] = concept.Name,
			["meaning"] = concept.Meaning
		}).ToList();
		data["workflow"] = Workflow.ToList();
		data["discover"] = Discover.ToList();

		// A person gets a table; the TOML has every command's full summary as an entry.
		if (HumanOutput.Enabled)
		{
			data["table"] = new TomlText(AsciiTable.Render(["COMMAND", "SUMMARY"],
				CommandTable.Commands.Select(command => (IReadOnlyList<string>)[command.FullName, command.Summary]), 160));
		}
		else
		{
			data["commands"] = CommandTable.Commands.Select(command => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["name"] = command.FullName,
				["summary"] = command.Summary,
				["usage"] = command.UsageLine,
				["read_only"] = command.ReadOnly,
				["interactive"] = command.Interactive
			}).ToList();
		}

		return ProgramCli.WriteResult(true, "help",
			"bassia - a Git-based monorepo of components, built for AI agents working in parallel. Bassia has no LLM of its own: an agent " +
			"learns it from this help and drives it with bassia command lines. Every command prints a TOML result. Run 'bassia help -all' for " +
			"every command in full, or 'bassia help <command>' for one.",
			data);
	}

	private static int All()
	{
		var data = Basics();
		data["commands"] = CommandTable.Commands.Select(Describe).ToList();
		return ProgramCli.WriteResult(true, "help",
			$"Every bassia command in full: {CommandTable.Commands.Count} commands, each with its usage, details, switches and examples. " +
			"Run 'bassia help' for the concepts and the usual workflow.",
			data);
	}

	private static int Group(string name, IReadOnlyList<CommandSpec> group) =>
		ProgramCli.WriteResult(true, "help",
			$"'bassia {group[0].Name}' has the subcommands {string.Join(", ", group.Select(command => command.Sub))}. Run 'bassia help {group[0].Name} <subcommand>' for switches and examples.",
			new Dictionary<string, object?>
			{
				["topic"] = group[0].Name,
				["usage"] = $"bassia {group[0].Name} <subcommand> [-switch [value]]...",
				["entry"] = group.Select(command => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = command.FullName,
					["summary"] = command.Summary,
					["usage"] = command.UsageLine,
					["read_only"] = command.ReadOnly,
					["interactive"] = command.Interactive,
					["examples"] = command.Examples.ToList()
				}).ToList()
			});

	private static int Command(CommandSpec spec)
	{
		var data = new Dictionary<string, object?> { ["topic"] = spec.FullName };
		foreach (var (key, value) in Describe(spec).Where(entry => entry.Key is not ("name" or "summary")))
		{
			data[key] = value;
		}

		return ProgramCli.WriteResult(true, "help", spec.Summary, data);
	}

	/// <summary>Everything about one command, as <c>help &lt;command&gt;</c> and <c>help -all</c> print it.</summary>
	private static IReadOnlyDictionary<string, object?> Describe(CommandSpec spec) => new Dictionary<string, object?>
	{
		["name"] = spec.FullName,
		["summary"] = spec.Summary,
		["usage"] = spec.UsageLine,
		["read_only"] = spec.ReadOnly,
		["interactive"] = spec.Interactive,
		["details"] = spec.Details,
		["positional"] = spec.Positional is null ? null : $"-{spec.Positional}",
		["examples"] = spec.Examples.ToList(),
		["switch"] = spec.Switches.Where(option => !option.Hidden).Select(option => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["name"] = $"-{option.Name}",
			["value"] = option.Value,
			["required"] = option.Required,
			["rest_of_line"] = option.Rest,
			["description"] = option.Description
		}).ToList()
	};
}
