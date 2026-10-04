namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Prompt;

/// <summary><c>bassia prompt</c> - the command line driven by an ask in natural language - and <c>bassia skill ...</c>.</summary>
internal static class PromptCommands
{
	/// <summary>How proposed commands are run; tests run them in-process instead of starting bassia again.</summary>
	internal static Func<string, ICommandExecutor> ExecutorFactory { get; set; } = directory => new SelfExecutor(directory);

	/// <summary>Whether to ask before a command that changes something; null means no one can be asked.</summary>
	internal static Func<Func<string, string?, Confirmation>?> ConfirmFactory { get; set; } =
		() => Console.IsInputRedirected ? null : AskOnConsole;

	// ----- bassia prompt <ask...> -----

	public static async Task<int> PromptAsync(Invocation invocation)
	{
		var ask = invocation.Require("ask");
		var directory = Environment.CurrentDirectory;
		var root = Monorepo.FindRoot(directory);
		IReadOnlyList<string> components = [];
		if (root is not null)
		{
			components = Monorepo.Load(root).Components.Select(component => component.Name).ToList();
		}

		var backendName = invocation.Get("backend") ?? Setting(root, ConfigFile.Find("llm.backend"));
		var backend = LlmBackends.Create(backendName, new LlmSettings(invocation.Get("llm") ?? Setting(root, ConfigFile.Find("llm.command")), invocation.Get("model"), directory));
		var skills = SkillStore.Load(root);
		var session = new PromptSession(backend, ExecutorFactory(directory), skills,
			new PromptContext(directory, root, components), ConfirmFactory(), Console.Error);

		var outcome = await session.RunAsync(new PromptOptions(
			ask,
			invocation.Int("max-rounds", 8, min: 1, max: 50),
			invocation.Has("dry-run"),
			invocation.Has("yes"),
			invocation.List("skill")), CancellationToken.None);

		return ProgramCli.WriteResult(outcome.Ok, invocation.Command, outcome.Message, new Dictionary<string, object?>
		{
			["ask"] = ask,
			["backend"] = backend.Name,
			["llm"] = backend.Description,
			["rounds"] = outcome.Rounds,
			["skills_loaded"] = outcome.LoadedSkills.ToList(),
			["step"] = outcome.Steps.Select(step => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["round"] = step.Round,
				["command_line"] = step.CommandLine,
				["args"] = step.Args.ToList(),
				["why"] = step.Why,
				["status"] = step.Status,
				["read_only"] = step.ReadOnly,
				["exit_code"] = step.ExitCode,
				["message"] = step.Message
			}).ToList()
		});
	}

	private static string Setting(string? root, ConfigKey key) =>
		root is not null && ConfigFile.Get(root, key) is { } value && !string.IsNullOrWhiteSpace(value) ? value : key.Default;

	private static Confirmation AskOnConsole(string commandLine, string? why)
	{
		Console.Error.Write($"Run '{commandLine}'{(string.IsNullOrWhiteSpace(why) ? "" : $" ({why})")}? [y]es, [n]o, [a]ll: ");
		var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
		return answer switch
		{
			"y" or "yes" => Confirmation.Yes,
			"a" or "all" => Confirmation.All,
			_ => Confirmation.No
		};
	}

	// ----- bassia skill list | show -----

	public static Task<int> SkillListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var skills = SkillStore.Load(monorepo.Root);
		var directory = SkillStore.DirectoryOf(monorepo.Root);
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command,
			skills.Count == 0
				? $"No skills. Add one as {Path.Combine(directory, "<name>", SkillStore.FileName)} and commit it to the meta-repo."
				: $"{skills.Count} skill(s) in '{directory}'.",
			new Dictionary<string, object?>
			{
				["path"] = directory,
				["table"] = skills.Count == 0 ? null : new TomlText(AsciiTable.Render(["SKILL", "DESCRIPTION"],
					skills.Select(skill => (IReadOnlyList<string>)[skill.Name, skill.Description]), 120)),
				["skill"] = skills.Select(skill => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = skill.Name,
					["description"] = skill.Description,
					["path"] = skill.Path
				}).ToList()
			}));
	}

	public static Task<int> SkillShowAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var skills = SkillStore.Load(monorepo.Root);
		var name = invocation.Require("name");
		var skill = SkillStore.Find(skills, name)
			?? throw new PromptException($"Unknown skill '{name}'. {(skills.Count == 0 ? "This monorepo has no skills." : $"Known skills: {string.Join(", ", skills.Select(candidate => candidate.Name))}.")}");
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command, skill.Description, new Dictionary<string, object?>
		{
			["name"] = skill.Name,
			["description"] = skill.Description,
			["path"] = skill.Path,
			["body"] = new TomlText(skill.Body)
		}));
	}
}
