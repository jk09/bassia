namespace Bassia.Cli;

using System.Text;

/// <summary>
/// One switch of a command: <c>-name value</c>, or <c>-name</c> alone when <see cref="Value"/> is null (a flag).
/// A <see cref="Rest"/> switch takes the rest of the command line verbatim - the agent command of <c>-run</c> or the
/// resolver of <c>-resolve</c> - so it has to come last. A <see cref="Hidden"/> switch is accepted but not listed in
/// help: the background process <c>-detach</c> starts is told its id and log file that way.
/// </summary>
internal sealed record SwitchSpec(string Name, string? Value, string Description, bool Required = false, bool Rest = false, bool Hidden = false)
{
	public bool IsFlag => Value is null;

	public string Synopsis => IsFlag ? $"-{Name}" : Rest ? $"-{Name} <{Value}...>" : $"-{Name} <{Value}>";
}

/// <summary>
/// One command of the command line - <c>bassia &lt;name&gt; [&lt;sub&gt;]</c> - with everything needed to parse, run and
/// explain it. The help, the usage line in an error and what the parser accepts all come from this one record, so
/// they cannot drift apart. <see cref="Positional"/> names the switch a single bare argument stands for
/// (<c>bassia run show brave-otter-3f2a91</c> is <c>bassia run show -id brave-otter-3f2a91</c>).
/// </summary>
internal sealed record CommandSpec(
	string Name,
	string? Sub,
	string Summary,
	IReadOnlyList<SwitchSpec> Switches,
	IReadOnlyList<string> Examples,
	Func<Invocation, Task<int>> Handler,
	string? Positional = null,
	string? Details = null,
	string? Usage = null)
{
	public string FullName => Sub is null ? Name : $"{Name} {Sub}";

	public SwitchSpec? FindSwitch(string name) =>
		Switches.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

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
			var visible = Switches.Where(candidate => !candidate.Hidden).ToList();
			foreach (var option in visible.Where(candidate => candidate.Required && !candidate.Rest))
			{
				builder.Append(' ').Append(option.Synopsis);
			}

			foreach (var option in visible.Where(candidate => !candidate.Required && !candidate.Rest))
			{
				builder.Append(" [").Append(option.Synopsis).Append(']');
			}

			foreach (var option in visible.Where(candidate => candidate.Rest))
			{
				builder.Append(option.Required ? $" {option.Synopsis}" : $" [{option.Synopsis}]");
			}

			return builder.ToString();
		}
	}
}

/// <summary>A command line that does not fit its command: reported as a TOML error with exit code 2.</summary>
internal sealed class CliUsageException(string command, string message) : Exception(message)
{
	public string Command { get; } = command;
}
