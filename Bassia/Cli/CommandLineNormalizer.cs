namespace Bassia.Cli;

using System.CommandLine;

/// <summary>
/// The command line as the library should see it. <c>System.CommandLine</c> is case-sensitive, has no notion of a
/// rest-of-line switch and spells long options its own way, while bassia has always accepted <c>-name</c> and
/// <c>--name</c> in any case, command names in any case, and the rest of the line verbatim after <c>-run</c> (or, for
/// <c>feedback send</c>, after the first bare word). This pass walks the words once, using the declared tree:
/// command words and switches are rewritten to their canonical spelling, and rest-of-line text is cut off.
/// </summary>
internal static class CommandLineNormalizer
{
	public static Normalized Normalize(RootCommand root, IReadOnlyList<string> args)
	{
		var tokens = new List<string>();
		var unknown = new List<string>();
		var current = (Command)root;
		var inCommandWords = true;
		var expectValue = false;
		for (var i = 0; i < args.Count; i++)
		{
			var token = args[i];
			if (expectValue)
			{
				tokens.Add(token);
				expectValue = false;
				continue;
			}

			if (SwitchName(token) is { } name)
			{
				if (Find(root, current, name) is not { } option)
				{
					// Left in place the library would take it for the command's bare argument.
					unknown.Add(token);
					tokens.Add(token);
					continue;
				}

				if (option is RestOfLineOption)
				{
					return new Normalized(tokens, new RestOfLine(option.SwitchName(), RestText(args, i + 1)), unknown);
				}

				tokens.Add(option.Name);
				expectValue = !option.IsFlag();
				continue;
			}

			if (inCommandWords && current.Subcommands.FirstOrDefault(command => string.Equals(command.Name, token, StringComparison.OrdinalIgnoreCase)) is { } sub)
			{
				tokens.Add(sub.Name);
				current = sub;
				continue;
			}

			inCommandWords = false;

			// 'bassia feedback send <message...>': everything from the first bare word on is the message.
			if (current is BassiaCommand { PositionalArgument: { } argument, Positional: { } positional } command
				&& command.FindSwitch(positional) is RestOfLineOption restOption)
			{
				return new Normalized(tokens, new RestOfLine(restOption.SwitchName(), RestText(args, i)), unknown);
			}

			tokens.Add(token);
		}

		return new Normalized(tokens, null, unknown);
	}

	private static string RestText(IReadOnlyList<string> args, int from) =>
		from >= args.Count ? "" : Invocation.JoinRest(args.Skip(from).ToList());

	/// <summary>The switch name of a <c>-name</c>/<c>--name</c> token, or null for a value (including negative numbers and a lone dash).</summary>
	private static string? SwitchName(string token)
	{
		var name = token.StartsWith("--", StringComparison.Ordinal) ? token[2..] : token.StartsWith('-') ? token[1..] : null;
		return string.IsNullOrEmpty(name) || char.IsDigit(name[0]) ? null : name;
	}

	/// <summary>The option of the command (or a global one on the root) with this name or alias, in any case.</summary>
	private static Option? Find(RootCommand root, Command command, string name) =>
		command.Options.Concat(root.Options.Where(option => option.Recursive)).FirstOrDefault(option =>
			option.Names().Any(candidate => string.Equals(candidate.TrimStart('-'), name, StringComparison.OrdinalIgnoreCase)));

	private static IEnumerable<string> Names(this Option option) => option.Aliases.Prepend(option.Name);
}

/// <summary>A command line ready for the library, the rest-of-line text it must not see, and the switch-like words no command declares.</summary>
internal sealed record Normalized(IReadOnlyList<string> Tokens, RestOfLine? Rest, IReadOnlyList<string> UnknownSwitches);
