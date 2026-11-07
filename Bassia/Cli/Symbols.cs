namespace Bassia.Cli;

using System.CommandLine;
using System.Text;

/// <summary>
/// A command of the command line - <c>bassia &lt;name&gt; [&lt;sub&gt;]</c> - as a <see cref="System.CommandLine.Command"/>
/// with what the library has no slot for: the handler, examples and details for help, and the option a single bare
/// argument stands for. A command with subcommands is a group and has no handler. The tree of these commands,
/// declared in <see cref="CommandTable"/>, is the one source of what is accepted, what help says and what an error suggests.
/// </summary>
internal sealed class BassiaCommand : Command
{
	public BassiaCommand(string name, string summary) : base(name, summary)
	{
	}

	public Func<Invocation, Task<int>>? Handler { get; init; }

	public IReadOnlyList<string> Examples { get; init; } = [];

	public string? Details { get; init; }

	/// <summary>A usage line to show instead of the one derived from the options.</summary>
	public string? Usage { get; init; }

	/// <summary>
	/// The option a single bare argument stands for: <c>bassia agent show brave-otter</c> is <c>bassia agent show -id brave-otter</c>.
	/// Null when the command takes none.
	/// </summary>
	public string? Positional { get; init; }

	/// <summary>The argument that carries <see cref="Positional"/> (several words when its option is a rest-of-line one).</summary>
	public Argument? PositionalArgument { get; init; }

	/// <summary>What the command does, in a sentence.</summary>
	public string Summary => Description ?? "";

	public bool IsGroup => Subcommands.Count > 0;

	/// <summary>The command as results report it, e.g. <c>agent start</c>.</summary>
	public string FullName => string.Join(' ', Lineage.Select(command => command.Name));

	/// <summary>The subcommands of a group, in declaration order.</summary>
	public IEnumerable<BassiaCommand> Nested => Subcommands.OfType<BassiaCommand>();

	/// <summary>The command and its ancestors below the root, outermost first.</summary>
	public IEnumerable<BassiaCommand> Lineage =>
		Parents.OfType<BassiaCommand>().Reverse().Append(this);

	/// <summary>The command's own switches, hidden ones included.</summary>
	public IEnumerable<Option> Switches => Options;

	public IEnumerable<Option> VisibleSwitches => Options.Where(option => !option.Hidden);

	/// <summary>Whether the command only reads (<see cref="CommandCatalog.ReadOnlyCommands"/>).</summary>
	public bool ReadOnly => CommandCatalog.ReadOnlyCommands.Contains(FullName);

	/// <summary>Whether the command is meant for a person (<see cref="CommandCatalog.InteractiveCommands"/>).</summary>
	public bool Interactive => CommandCatalog.InteractiveCommands.Contains(FullName);

	public Option? FindSwitch(string name) =>
		Options.FirstOrDefault(option => string.Equals(option.Name.TrimStart('-'), name, StringComparison.OrdinalIgnoreCase));

	public BassiaCommand? FindChild(string name) =>
		Nested.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase));

	/// <summary>The usage line: required switches first, then the optional ones in brackets, rest-of-line switches last.</summary>
	public string UsageLine
	{
		get
		{
			if (Usage is not null)
			{
				return Usage;
			}

			var builder = new StringBuilder("bassia ").Append(FullName);
			var visible = VisibleSwitches.ToList();
			foreach (var option in visible.Where(option => option.IsRequired() && !option.IsRest()))
			{
				builder.Append(' ').Append(option.Synopsis());
			}

			foreach (var option in visible.Where(option => !option.IsRequired() && !option.IsRest()))
			{
				builder.Append(" [").Append(option.Synopsis()).Append(']');
			}

			foreach (var option in visible.Where(option => option.IsRest()))
			{
				builder.Append(option.IsRequired() ? $" {option.Synopsis()}" : $" [{option.Synopsis()}]");
			}

			return builder.ToString();
		}
	}
}

/// <summary>An <see cref="Option"/> that knows whether the command needs it (the library's own flag is also off when a bare argument may stand in for it).</summary>
internal interface ISwitch
{
	bool Needed { get; }
}

/// <summary>A switch of a command: <c>-name value</c>, or <c>-name</c> alone when <typeparamref name="T"/> is <see cref="bool"/> (a flag).</summary>
internal class SwitchOption<T> : Option<T>, ISwitch
{
	public SwitchOption(string name, string? valueName, string description, bool needed = false, bool hidden = false)
		: base("-" + name)
	{
		Description = description;
		HelpName = valueName;
		Hidden = hidden;
		Needed = needed;
		Required = needed;
		// A list takes one value per occurrence (-load a -load b), never the words after it.
		Arity = typeof(T) == typeof(bool) ? ArgumentArity.Zero : typeof(T).IsArray ? ArgumentArity.OneOrMore : ArgumentArity.ExactlyOne;
		AllowMultipleArgumentsPerToken = false;
	}

	public bool Needed { get; }
}

/// <summary>
/// A switch that takes the rest of the command line verbatim - the agent command of <c>-run</c>, the message of
/// <c>feedback send</c> - so it has to come last. The library never sees its text: <see cref="CommandLineNormalizer"/>
/// cuts it off first, because the text may itself look like switches.
/// </summary>
internal sealed class RestOfLineOption : SwitchOption<string>
{
	public RestOfLineOption(string name, string valueName, string description, bool needed = false)
		: base(name, valueName, description, needed)
	{
		// The library would also demand the token it never sees.
		Required = false;
	}
}

/// <summary>What help and error messages need to know about an <see cref="Option"/>.</summary>
internal static class OptionExtensions
{
	public static string SwitchName(this Option option) => option.Name.TrimStart('-');

	/// <summary>A switch without a value: <c>-land</c>.</summary>
	public static bool IsFlag(this Option option) => option.ValueType == typeof(bool);

	public static bool IsRest(this Option option) => option is RestOfLineOption;

	public static bool IsRequired(this Option option) => option is ISwitch { Needed: true } || option.Required;

	/// <summary>The value's placeholder in usage lines: <c>dir</c> in <c>-path &lt;dir&gt;</c>.</summary>
	public static string? ValueName(this Option option) => option.IsFlag() ? null : option.HelpName;

	public static string Synopsis(this Option option) =>
		option.IsFlag() ? option.Name
		: option.IsRest() ? $"{option.Name} <{option.HelpName}...>"
		: $"{option.Name} <{option.HelpName}>";
}

/// <summary>A command line that does not fit its command: reported as a TOML error with exit code 2.</summary>
internal sealed class CliUsageException(string command, string message) : Exception(message)
{
	public string Command { get; } = command;
}
