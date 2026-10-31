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

	/// <summary>What a bare <c>bassia</c> prints for a person at a terminal; 'bassia help' has everything.</summary>
	public const string ShortText =
		"bassia - a client-server monorepo for fleets of coding agents: a workspace per run, one trunk to land on.\n" +
		"\n" +
		"Usage: " + GlobalUsage + "\n" +
		"\n" +
		"  bassia help [<command>]     every command, or one command's switches and examples\n" +
		"  bassia help -all            every command in full, for an agent to learn bassia in one call\n" +
		"  bassia status               where you are: the monorepo, mainline, open workspaces and runs\n" +
		"\n" +
		"Every command prints its result as TOML; add -human to read it as Markdown.";

	/// <summary>What Bassia's parts are, for an agent that has only this help to go by.</summary>
	private static readonly (string Name, string Meaning)[] Concepts =
	[
		("monorepo", "One tree of files, versioned as immutable content-addressed objects (chunks, trees, commits) in blob storage. A folder with .bassia/ is either a local monorepo (bassia init: the core runs in-process over a local-disk blob store and SQLite) or a clone of a bassia-server (bassia clone: the same core, over the server's stores). Commands find it from the current folder upwards, or from -C <path>."),
		("mainline", "The single trunk. Every landing gets the next seq (1, 2, 3, ...) and mainline's history is linear. There are no branches or tags: read it by seq (mainline log|show|ls|cat|changed)."),
		("workspace", "Where work happens instead of a branch: a folder holding a tree, with a base (mainline at some seq, or another workspace it is stacked on with -on) and a working commit. Commits are implicit: every command that reads a workspace snapshots its folder first. Clone a workspace (workspace clone) to branch off it."),
		("landing", "Moving a workspace's change onto mainline (workspace land; workspace check says whether it can). Only the paths the workspace touched are compared with mainline since its base seq: disjoint changes land with no merge; a path both rewrote follows its conflict policy. The landed commit keeps the workspace's ChangeId."),
		("change id", "The stable identity of a unit of work: it survives snapshots, sync, landing and replay. The commit id (a hash) changes every time."),
		("conflict policy", "Per path glob in .bassia.toml at the root of the tree: replay (default: the landing is refused and the task re-run on the new head), textmerge (line-based three-way merge while both sides stay similar), semantic (a language-aware merge driver, landed only after the land.validate hook passes), conflict (stored as a conflict in the workspace; mainline refuses it)."),
		("replay", "Rebase by replay: 'agent replay' runs a workspace's recorded task (prompt, agent, model) again on mainline's head, in a new workspace with the same ChangeId, instead of merging text no one reviewed."),
		("sync", "Rebase a workspace onto its newest base (workspace sync): untouched paths cost nothing; paths both sides changed follow their policy, and what stays unresolved is written into the folder with conflict markers."),
		("operation log", "Every mutation of mainline or a workspace is an operation pointing at a view; undo and restore write a new operation with an earlier view, so a wrong or abandoned run is recoverable."),
		("lease", "An advisory hold on a path prefix (lease acquire), exclusive or intent, with a time to live and a fencing token. Exclusive leases on disjoint prefixes never block each other; prefixes named lease_required in .bassia.toml refuse a landing without one."),
		("agentic run", "agent start runs a coding agent (agent.command, Claude Code by default) in a workspace of its own, records the task as the change's replay recipe, snapshots the result and with -land lands it. Runs are followed with agent list|show|logs|wait|stop."),
		("git", "An import and export format at the edge: import -git brings repositories in, each under its own folder (baselines, or -history); export -git writes mainline to a git repository incrementally."),
		("configuration", "Settings of a monorepo folder in .bassia/config.toml (config list|get|set|unset): who acts, the agent command, the server, the merge driver and validation hook, garbage collection.")
	];

	/// <summary>The usual sequence of commands, in order.</summary>
	private static readonly string[] Workflow =
	[
		"bassia init -path <folder> (or bassia clone -from <server-url>): a monorepo folder.",
		"bassia import -git <url>,<url>: bring existing git repositories in, each under its own folder (optional).",
		"bassia agent start -prompt <text> [-land] [-detach]: a workspace per run; or bassia workspace create <name> and edit its folder yourself.",
		"bassia workspace check <name>: can it land? needs-replay names the paths mainline rewrote meanwhile.",
		"bassia workspace land <name>: onto mainline as the next seq; or agent replay <name> / workspace sync <name> when it conflicts.",
		"bassia status, workspace list, agent list, mainline log, lease list, web: look at everything at any time."
	];

	/// <summary>How an agent learns the rest.</summary>
	private static readonly string[] Discover =
	[
		"bassia help -all: every command in full - usage, details, switches and examples - in one result.",
		"bassia help <command> [<subcommand>], or <command> [<subcommand>] -help: one command; bassia help <group>: a group's subcommands.",
		"read_only = true marks a command that changes nothing; interactive = true marks one meant for a person. Run the read-only commands that are not interactive freely to explore.",
		"bassia status: whether the current folder is in a monorepo, and what is going on in it.",
		"bassia config list: every setting with its value, source and meaning."
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
			"(bassia workspace show fix-login). A rest-of-line switch (-run, the message of feedback send) takes everything after it " +
			"and comes last. -run is a shell command line: one argument is used verbatim, several are joined with spaces (a word " +
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
			"bassia - a client-server monorepo for fleets of coding agents: a workspace per agentic run, one trunk to land on. Bassia has no LLM of its own: an agent " +
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
