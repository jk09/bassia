namespace Bassia.Cli;

using System.CommandLine;
using System.Globalization;

/// <summary>
/// What a command handler sees of its command line: the values of the command's switches, read from the library's
/// <see cref="ParseResult"/>. A switch given as the command's bare argument (<see cref="BassiaCommand.Positional"/>)
/// reads the same as when given by name, and a rest-of-line switch reads as the text the normalizer cut off.
/// Lists are comma-separated, given once or repeated.
/// </summary>
internal sealed class Invocation
{
	private readonly ParseResult parse;
	private readonly RestOfLine? rest;

	public Invocation(BassiaCommand command, ParseResult parse, RestOfLine? rest = null)
	{
		Spec = command;
		this.parse = parse;
		this.rest = rest;
	}

	public BassiaCommand Spec { get; }

	/// <summary>The command's name as results report it, e.g. <c>agent start</c>.</summary>
	public string Command => Spec.FullName;

	/// <summary>The words of a command's bare arguments, for the commands that take several (<c>bassia help workspace land</c>).</summary>
	public IReadOnlyList<string> Words =>
		Spec.PositionalArgument is { } argument && parse.GetResult(argument) is { Implicit: false } given ? given.Tokens.Select(token => token.Value).ToList() : [];

	public bool Has(string name) => Find(name) is { } option && Tokens(option) is not null;

	/// <summary>The switch's value; repeated occurrences of a list switch are joined with commas.</summary>
	public string? Get(string name) =>
		Find(name) is { } option && Tokens(option) is { Count: > 0 } tokens ? string.Join(",", tokens) : null;

	public string Require(string name) => Get(name) ?? throw Usage($"-{name} is required.");

	/// <summary>A comma-separated list switch, given once or repeated.</summary>
	public IReadOnlyList<string> List(string name) =>
		Find(name) is { } option && Tokens(option) is { } tokens
			? tokens.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
			: [];

	public int Int(string name, int defaultValue, int min = 0, int max = int.MaxValue)
	{
		if (Get(name) is not { } text)
		{
			return defaultValue;
		}

		return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
			? value
			: throw Usage($"-{name} must be a whole number from {min} to {max}; got '{text}'.");
	}

	public CliUsageException Usage(string message) => Usage(Spec, message);

	public static CliUsageException Usage(BassiaCommand command, string message) =>
		new(command.FullName, $"{message} Usage: {command.UsageLine}. Run 'bassia help {command.FullName}' for details and examples.");

	private Option? Find(string name) => Spec.FindSwitch(name);

	/// <summary>The values the switch was given (empty for a flag that was given), or null when it was not given at all.</summary>
	private List<string>? Tokens(Option option)
	{
		if (option is RestOfLineOption)
		{
			return rest is { } text && text.Switch == option.SwitchName() ? [text.Text] : null;
		}

		var named = parse.GetResult(option) is { Implicit: false } result ? option.IsFlag() ? [] : result.Tokens.Select(token => token.Value).ToList() : null;

		// The bare argument stands for the command's positional switch.
		var bare = Spec.Positional is { } positional && string.Equals(positional, option.SwitchName(), StringComparison.OrdinalIgnoreCase)
			&& Spec.PositionalArgument is { } argument && parse.GetResult(argument) is { Implicit: false, Tokens.Count: > 0 } given
				? given.Tokens.Select(token => token.Value).ToList()
				: null;
		return named is not null && bare is not null
			? throw Usage($"Give -{option.SwitchName()} either by name or as the bare argument, not both.")
			: named ?? bare;
	}

	/// <summary>
	/// One quoted string, or the command's own words re-joined into one shell line. A word the user's shell had to
	/// quote (whitespace, quotes, backslashes, backticks) is quoted again so it reaches <c>sh</c> as exactly that word;
	/// other words (<c>&amp;&amp;</c>, <c>|</c>, <c>&gt;</c>, globs, <c>$VAR</c>) stay shell syntax.
	/// </summary>
	internal static string JoinRest(IReadOnlyList<string> words) =>
		words.Count == 1 ? words[0] : string.Join(' ', words.Select(QuoteWord));

	private static string QuoteWord(string word)
	{
		if (OperatingSystem.IsWindows())
		{
			return word.Any(char.IsWhiteSpace) ? $"\"{word}\"" : word;
		}

		return word.Length == 0 || word.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '\\' or '`')
			? $"'{word.Replace("'", "'\\''", StringComparison.Ordinal)}'"
			: word;
	}
}

/// <summary>The text a rest-of-line switch took: everything after it on the command line, joined into one shell line.</summary>
internal sealed record RestOfLine(string Switch, string Text);
