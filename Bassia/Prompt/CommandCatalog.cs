namespace Bassia.Prompt;

using System.Text;
using Bassia.Cli;

/// <summary>A proposed command line that fits the command table, or why it does not.</summary>
internal sealed record CommandCheck(CommandSpec? Spec, IReadOnlyList<string> Args, string? Error, bool ReadOnly)
{
	public bool Valid => Error is null;
}

/// <summary>
/// The command line as the LLM sees it and as <c>bassia prompt</c> checks it. The catalog is generated from
/// <see cref="CommandTable"/>, so it says exactly what the parser accepts; a proposed command is checked with that
/// same parser (<see cref="Invocation.Parse"/>) before anything runs.
/// </summary>
internal static class CommandCatalog
{
	/// <summary>Commands the prompt layer never runs: itself (no recursion) and the dashboard (it never returns).</summary>
	public static readonly IReadOnlySet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "prompt", "web" };

	/// <summary>
	/// Commands that only read: they run without confirmation. Anything else - including every command added later -
	/// changes something and is confirmed first.
	/// </summary>
	public static readonly IReadOnlySet<string> ReadOnlyCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"status", "config list", "config get", "version",
		"component list", "component show", "component survey",
		"tag list", "tag show", "graph", "log",
		"run list", "run show", "run logs", "run wait", "run diff",
		"integration plan", "integration list", "integration show", "integration logs", "integration wait",
		"skill list", "skill show", "help"
	};

	public static IEnumerable<CommandSpec> Offered => CommandTable.Commands.Where(spec => !Excluded.Contains(spec.Name));

	/// <summary>Every offered command: usage, summary, details, switches and examples.</summary>
	public static string Render()
	{
		var builder = new StringBuilder();
		foreach (var spec in Offered)
		{
			builder.Append("## ").Append(spec.FullName).Append(ReadOnlyCommands.Contains(spec.FullName) ? " (read-only)" : "").Append('\n');
			builder.Append(spec.Summary).Append('\n');
			builder.Append("usage: ").Append(spec.UsageLine).Append('\n');
			if (spec.Positional is not null)
			{
				builder.Append("positional: a bare argument stands for -").Append(spec.Positional).Append('\n');
			}

			if (spec.Details is not null)
			{
				builder.Append("details: ").Append(spec.Details).Append('\n');
			}

			foreach (var option in spec.Switches.Where(option => !option.Hidden))
			{
				builder.Append("  ").Append(option.Synopsis).Append(option.Required ? " (required)" : "")
					.Append(option.Rest ? " (rest of line, last)" : "").Append(": ").Append(option.Description).Append('\n');
			}

			foreach (var example in spec.Examples)
			{
				builder.Append("  e.g. ").Append(example).Append('\n');
			}

			builder.Append('\n');
		}

		return builder.ToString();
	}

	/// <summary>
	/// Checks <paramref name="args"/> (without the leading <c>bassia</c>) against the command table: global
	/// <c>-C &lt;path&gt;</c> switches, then a known command and subcommand whose switches parse.
	/// </summary>
	public static CommandCheck Check(IReadOnlyList<string> args)
	{
		var words = args.ToList();
		if (words.Count > 0 && string.Equals(words[0], "bassia", StringComparison.OrdinalIgnoreCase))
		{
			words.RemoveAt(0);
		}

		var index = 0;
		while (index < words.Count && words[index] == "-C")
		{
			if (index + 1 >= words.Count)
			{
				return Invalid(words, "-C requires a path.");
			}

			index += 2;
		}

		if (index >= words.Count)
		{
			return Invalid(words, "No command given.");
		}

		var name = words[index];
		if (Excluded.Contains(name))
		{
			return Invalid(words, $"'bassia {name.ToLowerInvariant()}' cannot be run from a prompt.");
		}

		var group = CommandTable.Group(name);
		if (group.Count == 0)
		{
			return Invalid(words, CommandTable.Replaced.TryGetValue(name, out var replacement)
				? replacement
				: $"Unknown command '{name}'.");
		}

		var rest = words[(index + 1)..];
		CommandSpec? spec;
		if (group.Count == 1 && group[0].Sub is null)
		{
			spec = group[0];
		}
		else
		{
			spec = rest.Count == 0 ? null : group.FirstOrDefault(command => string.Equals(command.Sub, rest[0], StringComparison.OrdinalIgnoreCase));
			if (spec is null)
			{
				return Invalid(words, $"'bassia {group[0].Name}' needs one of the subcommands {string.Join(", ", group.Select(command => command.Sub))}.");
			}

			rest = rest[1..];
		}

		try
		{
			Invocation.Parse(spec, rest);
		}
		catch (CliUsageException ex)
		{
			return new CommandCheck(spec, words, ex.Message, false);
		}

		var readOnly = ReadOnlyCommands.Contains(spec.FullName)
			&& !(spec.Name == "graph" && rest.Any(word => word.TrimStart('-').Equals("out", StringComparison.OrdinalIgnoreCase)));
		return new CommandCheck(spec, words, null, readOnly);
	}

	private static CommandCheck Invalid(IReadOnlyList<string> args, string error) => new(null, args, error, false);

	/// <summary>The command line as a person would type it, quoting words with spaces.</summary>
	public static string Display(IReadOnlyList<string> args) =>
		"bassia " + string.Join(' ', args.Select(word => word.Length == 0 || word.Any(char.IsWhiteSpace) || word.Contains('"')
			? $"\"{word.Replace("\"", "\\\"")}\""
			: word));
}
