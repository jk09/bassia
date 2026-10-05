namespace Bassia.Prompt;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Bassia.Cli;

/// <summary>
/// Where the ask is made: the working directory, and the monorepo, its components and its effective configuration
/// when inside one.
/// </summary>
internal sealed record PromptContext(string WorkingDirectory, string? MonorepoRoot, IReadOnlyList<string> Components, IReadOnlyList<ConfigValue>? Settings = null)
{
	public static string Platform =>
		OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : RuntimeInformation.OSDescription;
}

/// <summary>How a session runs: the ask, the round limit, and what may run without asking.</summary>
internal sealed record PromptOptions(string Ask, int MaxRounds = 8, bool DryRun = false, bool Yes = false, IReadOnlyList<string>? Skills = null);

/// <summary>A person's answer to "run this command?".</summary>
internal enum Confirmation
{
	No,
	Yes,
	All
}

/// <summary>
/// One proposed command and what became of it: <c>ok</c> or <c>failed</c> (it ran), <c>rejected</c> (it does not fit
/// the command table), <c>declined</c> (it changes something and was not confirmed), <c>planned</c> (a dry run stopped
/// before it), or <c>skipped</c> (an earlier command of its round did not succeed).
/// </summary>
internal sealed record PromptStep(int Round, IReadOnlyList<string> Args, string? Why, string Status, bool ReadOnly, int? ExitCode = null, string? Message = null, string? ResultText = null)
{
	public string CommandLine => CommandCatalog.Display(Args);
}

internal sealed record PromptOutcome(bool Ok, string Message, IReadOnlyList<PromptStep> Steps, int Rounds, IReadOnlyList<string> LoadedSkills);

/// <summary>
/// The step loop of <c>bassia prompt</c>. Each round sends the ask, the context, the skills and the results so far to
/// the LLM, reads the commands it answers with, checks each against the command table, has the ones that change
/// something confirmed, and runs them. It ends when the LLM is done, a command is declined, or the rounds run out.
/// </summary>
internal sealed class PromptSession(
	ILlmBackend backend,
	ICommandExecutor executor,
	IReadOnlyList<Skill> skills,
	PromptContext context,
	Func<string, string?, Confirmation>? confirm,
	TextWriter? progress = null)
{
	/// <summary>How much of one result goes back to the LLM; a patch or a long log is cut there.</summary>
	internal const int MaxResultChars = 6000;

	private static readonly Regex SkillMention = new(@"(?<![\w/\\:.])/(?<name>[A-Za-z0-9][\w.-]*)");

	private readonly TextWriter progress = progress ?? TextWriter.Null;

	public async Task<PromptOutcome> RunAsync(PromptOptions options, CancellationToken cancellation)
	{
		var loaded = new List<Skill>();
		foreach (var name in options.Skills ?? [])
		{
			var skill = SkillStore.Find(skills, name)
				?? throw new PromptException($"Unknown skill '{name}'. {KnownSkills()}");
			AddOnce(loaded, skill);
		}

		foreach (Match mention in SkillMention.Matches(options.Ask))
		{
			if (SkillStore.Find(skills, mention.Groups["name"].Value) is { } skill)
			{
				AddOnce(loaded, skill);
			}
		}

		var system = SystemPrompt();
		var steps = new List<PromptStep>();
		var notes = new List<string>();
		var approveAll = options.Yes;

		for (var round = 1; round <= options.MaxRounds; round++)
		{
			progress.WriteLine($"bassia prompt: asking {backend.Name} (round {round})...");
			var replyText = await backend.CompleteAsync(new LlmRequest(system, RoundPrompt(options.Ask, loaded, steps, notes)), cancellation);
			PromptReply reply;
			try
			{
				reply = PromptReply.Parse(replyText);
			}
			catch (PromptException ex)
			{
				throw new PromptException($"{ex.Message} The reply was: {Truncate(replyText, 2000)}");
			}

			var newSkills = 0;
			var notesBefore = notes.Count;
			foreach (var name in reply.LoadSkills)
			{
				if (SkillStore.Find(skills, name) is not { } skill)
				{
					notes.Add($"Round {round}: there is no skill '{name}'. {KnownSkills()}");
				}
				else if (AddOnce(loaded, skill))
				{
					newSkills++;
				}
			}

			var roundOk = true;
			for (var index = 0; index < reply.Commands.Count; index++)
			{
				var proposed = reply.Commands[index];
				if (!roundOk)
				{
					steps.Add(new PromptStep(round, proposed.Args, proposed.Why, "skipped", false));
					continue;
				}

				var check = CommandCatalog.Check(proposed.Args);
				if (!check.Valid)
				{
					steps.Add(new PromptStep(round, check.Args, proposed.Why, "rejected", false, 2, check.Error));
					progress.WriteLine($"bassia prompt: rejected '{CommandCatalog.Display(check.Args)}': {check.Error}");
					roundOk = false;
					continue;
				}

				var line = CommandCatalog.Display(check.Args);
				if (!check.ReadOnly && options.DryRun)
				{
					// A dry run changes nothing: what follows depends on this command, so the plan ends here.
					steps.Add(new PromptStep(round, check.Args, proposed.Why, "planned", false));
					foreach (var rest in reply.Commands.Skip(index + 1))
					{
						steps.Add(new PromptStep(round, rest.Args, rest.Why, "planned", CommandCatalog.Check(rest.Args).ReadOnly));
					}

					var planned = steps.Count(step => step.Status == "planned");
					return new PromptOutcome(true, $"Dry run: {planned} command(s) planned, nothing changed. {reply.Answer}".Trim(), steps, round, Names(loaded));
				}

				if (!check.ReadOnly && !approveAll)
				{
					var answer = confirm?.Invoke(line, proposed.Why) ?? Confirmation.No;
					if (answer == Confirmation.No)
					{
						var reason = confirm is null
							? $"'{line}' changes the monorepo and needs confirmation: run interactively, or add -yes to run such commands without asking."
							: $"Declined '{line}'.";
						steps.Add(new PromptStep(round, check.Args, proposed.Why, "declined", false, null, reason));
						return new PromptOutcome(false, reason, steps, round, Names(loaded));
					}

					approveAll = answer == Confirmation.All;
				}

				progress.WriteLine($"> {line}");
				var outcome = await executor.ExecuteAsync(check.Args, cancellation);
				var result = outcome.Result;
				var message = result is null ? null
					: result.TryGetValue("message", out var text) ? text as string
					: result.TryGetValue("error", out var error) ? error as string : null;
				var ok = outcome.ExitCode == 0;
				steps.Add(new PromptStep(round, check.Args, proposed.Why, ok ? "ok" : "failed", check.ReadOnly, outcome.ExitCode, message, outcome.ResultText));
				roundOk = ok;
			}

			var ran = reply.Commands.Count > 0;
			if ((reply.Done && roundOk) || (!ran && newSkills == 0 && notes.Count == notesBefore))
			{
				// Done - or stuck: a round without a command, a new skill or a note to react to would only repeat.
				var executed = steps.LastOrDefault(step => step.Status is "ok" or "failed" or "rejected");
				var succeeded = executed is null || executed.Status == "ok";
				var summary = reply.Answer ?? (steps.Count == 0
					? "Nothing to run."
					: $"{steps.Count(step => step.Status == "ok")} command(s) run{(succeeded ? "" : $"; '{executed!.CommandLine}' did not succeed: {executed.Message}")}.");
				return new PromptOutcome(succeeded, summary, steps, round, Names(loaded));
			}
		}

		var last = steps.LastOrDefault(step => step.Status is "failed" or "rejected");
		return new PromptOutcome(false,
			$"Not finished after {options.MaxRounds} round(s){(last is null ? "" : $"; the last problem was '{last.CommandLine}': {last.Message}")}. Raise -max-rounds or narrow the ask.",
			steps, options.MaxRounds, Names(loaded));
	}

	private static bool AddOnce(List<Skill> loaded, Skill skill)
	{
		if (loaded.Any(candidate => candidate.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}

		loaded.Add(skill);
		return true;
	}

	private static IReadOnlyList<string> Names(IEnumerable<Skill> loaded) => loaded.Select(skill => skill.Name).ToList();

	private string KnownSkills() =>
		skills.Count == 0 ? "This monorepo has no skills." : $"Known skills: {string.Join(", ", skills.Select(skill => skill.Name))}.";

	/// <summary>The standing instructions: the reply format, the rules, the grammar and the command catalog.</summary>
	internal static string SystemPrompt() =>
		"""
		You operate Bassia - a version control system for a monorepo made of git repositories (components) registered
		in a meta-repo (.bassia) - through its command line. Turn the user's ask into bassia commands. You do not run
		anything yourself: Bassia runs the commands you list, in order, and when you are not done it shows you their
		TOML results in the next round.

		Reply with exactly one TOML document and nothing else:

		done = true                 # true when nothing more is needed after the commands below have run
		answer = "..."              # for the user: what was done, or the answer to their question (required when done)
		load_skills = ["name"]      # optional: skills whose instructions you need before planning
		[[command]]                 # zero or more, run in order; a round stops at the first command that fails
		args = ["init", "-path", 'C:\mono']   # the words after 'bassia', one item per word, no shell quoting
		why = "..."                 # one short line

		Rules:
		- Write paths and anything containing a backslash as TOML literal strings ('C:\mono'), never as "C:\mono".
		- Use only the commands and switches of the catalog below; a command that does not parse is rejected and you
		  are told why.
		- When you need facts first (component names, run ids, tags, status), list read-only commands that show them
		  and set done = false; plan the rest once you see their results. Do not guess ids.
		- To work on a monorepo other than the working directory, start args with "-C", '<path>'.
		- Commands that change something are confirmed by the user before they run.
		- When the ask cannot be done with bassia, or is ambiguous, run nothing, set done = true and say why in answer.
		- When a skill fits the ask, load it and follow it.
		- With done = true next to commands, write answer as the outcome once they have all succeeded: it is shown only
		  then; if one fails you get its result in the next round instead.
		- When you have seen the results you need, set done = true with no commands and answer from those results.

		""" +
		$"Command line grammar: {Help.GlobalUsage}. Switches are case-insensitive and may be written -name or --name. A command's main " +
		"argument (its positional switch) may be given without the switch. A rest-of-line switch takes everything after it and comes last. " +
		"Lists are comma-separated. Every command prints a TOML result with ok, command, message (or error) and its data.\n\n" +
		"# Commands\n\n" + CommandCatalog.Render();

	/// <summary>One round's prompt: context, skills, the ask, and the results so far.</summary>
	internal string RoundPrompt(string ask, IReadOnlyList<Skill> loaded, IReadOnlyList<PromptStep> steps, IReadOnlyList<string> notes)
	{
		var builder = new StringBuilder();
		builder.Append("# Context\n\n");
		builder.Append("working_directory = ").Append(Literal(context.WorkingDirectory)).Append('\n');
		builder.Append("platform = \"").Append(PromptContext.Platform).Append("\"\n");
		if (context.MonorepoRoot is null)
		{
			builder.Append("# The working directory is not inside a Bassia monorepo.\n");
		}
		else
		{
			builder.Append("monorepo_root = ").Append(Literal(context.MonorepoRoot)).Append('\n');
			builder.Append("components = [").Append(string.Join(", ", context.Components.Select(name => $"\"{name}\""))).Append("]\n");
			if (context.Settings is { Count: > 0 } settings)
			{
				builder.Append("\n# Configuration: each setting's effective value and its layer (default, monorepo = .bassia/config.toml, ")
					.Append("user = .bassia/config.user.toml). Change one with 'config set' (add -user for the user layer), remove one with 'config unset'.\n\n");
				foreach (var setting in settings)
				{
					builder.Append(setting.Key.Key).Append(" = ").Append(ConfigFile.Quote(setting.Value)).Append("   # ").Append(setting.Source).Append('\n');
				}
			}
		}

		builder.Append("\n# Skills\n\n");
		if (skills.Count == 0)
		{
			builder.Append("(none)\n");
		}

		foreach (var skill in skills)
		{
			builder.Append("- ").Append(skill.Name).Append(": ").Append(skill.Description).Append('\n');
		}

		foreach (var skill in loaded)
		{
			builder.Append("\n## Skill: ").Append(skill.Name).Append("\n\n").Append(skill.Body).Append('\n');
		}

		builder.Append("\n# Ask\n\n").Append(ask.Trim()).Append('\n');

		if (steps.Count > 0 || notes.Count > 0)
		{
			builder.Append("\n# Results so far\n");
			foreach (var group in steps.GroupBy(step => step.Round))
			{
				builder.Append("\n## Round ").Append(group.Key).Append('\n');
				foreach (var step in group)
				{
					builder.Append("\n$ ").Append(step.CommandLine).Append("   # ").Append(step.Status)
						.Append(step.ExitCode is { } code ? $", exit code {code}" : "").Append('\n');
					var text = step.ResultText ?? step.Message;
					if (!string.IsNullOrWhiteSpace(text))
					{
						builder.Append(Truncate(text, MaxResultChars)).Append('\n');
					}
				}
			}

			foreach (var note in notes)
			{
				builder.Append('\n').Append(note).Append('\n');
			}

			builder.Append("\nPlan the next round, or set done = true with the answer.\n");
		}

		return builder.ToString();
	}

	private static string Literal(string value) => value.Contains('\'') ? ConfigFile.Quote(value) : $"'{value}'";

	private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + $"\n... ({text.Length - max} more characters cut)";
}
