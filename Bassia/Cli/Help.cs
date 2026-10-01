namespace Bassia.Cli;

/// <summary>
/// Help as TOML, like every other result: the overview of all commands, the subcommands of a group, or one
/// command's summary, usage, switches and examples - generated from <see cref="CommandTable"/>.
/// </summary>
internal static class Help
{
	public const string GlobalUsage = "bassia [-C <path>] <command> [<subcommand>] [-switch [value]]...";

	public static Task<int> RunAsync(Invocation invocation) =>
		Task.FromResult(Show((invocation.Get("command") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)));

	/// <summary>Help for the words after <c>bassia help</c>: none, a command or group, or a group and subcommand.</summary>
	public static int Show(IReadOnlyList<string> words)
	{
		if (words.Count == 0)
		{
			return Overview();
		}

		var group = CommandTable.Group(words[0]);
		if (group.Count == 0)
		{
			return ProgramCli.UsageError("help", $"Unknown command '{words[0]}'. Run 'bassia help' for the list of commands.");
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

	private static int Overview() =>
		ProgramCli.WriteResult(true, "help",
			"bassia - a Git-based monorepo of components, operated by agents. Every command prints a TOML result: on stdout with exit code 0 " +
			"on success, on stderr with exit code 1 on failure (2 for a command line that does not parse). Run 'bassia help <command>' for a " +
			"command's switches and examples.",
			new Dictionary<string, object?>
			{
				["usage"] = GlobalUsage,
				["global_switches"] = new List<string> { "-C <path>: run as if bassia was started in <path>; may repeat, each relative to the previous one" },
				["grammar"] = "Switches are case-insensitive and may be written -name or --name. A command's main argument may be given without its switch " +
					"(bassia run show brave-otter-3f2a91). A rest-of-line switch (-run, -resolve) takes everything after it and comes last. Lists are comma-separated.",
				["result"] = "A result opens with the line '# bassia result', then ok, command, message (or error), then the command's data. When a command " +
					"streams other output first (run start without -detach), parse from the last '# bassia result' line.",
				["table"] = new TomlText(AsciiTable.Render(["COMMAND", "SUMMARY"],
					CommandTable.Commands.Select(command => (IReadOnlyList<string>)[command.FullName, command.Summary]), 160))
			});

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
					["examples"] = command.Examples.ToList()
				}).ToList()
			});

	private static int Command(CommandSpec spec) =>
		ProgramCli.WriteResult(true, "help", spec.Summary, new Dictionary<string, object?>
		{
			["topic"] = spec.FullName,
			["usage"] = spec.UsageLine,
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
		});
}
