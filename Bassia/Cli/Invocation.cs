namespace Bassia.Cli;

using System.Globalization;

/// <summary>
/// A parsed command line for one <see cref="CommandSpec"/>: which switches were given and with what values. The
/// grammar is the same for every command: <c>-switch value</c> or <c>--switch value</c> (case-insensitive), flags
/// on their own, at most one bare argument standing for the command's <see cref="CommandSpec.Positional"/> switch,
/// and a rest-of-line switch last.
/// </summary>
internal sealed class Invocation
{
	private readonly Dictionary<string, List<string>> values = new(StringComparer.OrdinalIgnoreCase);

	private Invocation(CommandSpec spec) => Spec = spec;

	public CommandSpec Spec { get; }

	/// <summary>The command's name as results report it, e.g. <c>run start</c>.</summary>
	public string Command => Spec.FullName;

	public bool HelpRequested { get; private set; }

	public static Invocation Parse(CommandSpec spec, IReadOnlyList<string> args)
	{
		var invocation = new Invocation(spec);
		for (var i = 0; i < args.Count; i++)
		{
			var token = args[i];
			if (SwitchName(token) is not { } name)
			{
				// A rest-of-line positional (the ask of 'bassia prompt') starts at the first bare word.
				if (spec.Positional is not null && spec.FindSwitch(spec.Positional) is { Rest: true } restPositional
					&& !invocation.values.ContainsKey(restPositional.Name))
				{
					invocation.values[restPositional.Name] = [JoinRest(args.Skip(i).ToList())];
					break;
				}

				if (spec.Positional is null || invocation.values.ContainsKey(spec.Positional))
				{
					throw invocation.Usage($"Unexpected argument '{token}'.");
				}

				invocation.values[spec.Positional] = [token];
				continue;
			}

			if (name.Equals("help", StringComparison.OrdinalIgnoreCase) || name == "h")
			{
				invocation.HelpRequested = true;
				continue;
			}

			var option = spec.FindSwitch(name) ?? throw invocation.Usage($"Unknown switch '{token}'.");
			if (option.IsFlag)
			{
				invocation.values[option.Name] = [];
				continue;
			}

			if (option.Rest)
			{
				var words = args.Skip(i + 1).ToList();
				if (words.Count == 0)
				{
					throw invocation.Usage($"-{option.Name} requires {Article(option.Value!)} {option.Value}.");
				}

				invocation.values[option.Name] = [JoinRest(words)];
				break;
			}

			if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1])
				|| (SwitchName(args[i + 1]) is { } next && (spec.FindSwitch(next) is not null || next.Equals("help", StringComparison.OrdinalIgnoreCase))))
			{
				throw invocation.Usage($"-{option.Name} requires a value (<{option.Value}>).");
			}

			if (!invocation.values.TryGetValue(option.Name, out var list))
			{
				invocation.values[option.Name] = list = [];
			}

			list.Add(args[++i]);
		}

		if (!invocation.HelpRequested)
		{
			foreach (var option in spec.Switches.Where(option => option.Required && !invocation.values.ContainsKey(option.Name)))
			{
				throw invocation.Usage($"-{option.Name} is required.");
			}
		}

		return invocation;
	}

	/// <summary>The switch name of a <c>-name</c>/<c>--name</c> token, or null for a value (including negative numbers and a lone dash).</summary>
	private static string? SwitchName(string token)
	{
		var name = token.StartsWith("--", StringComparison.Ordinal) ? token[2..] : token.StartsWith('-') ? token[1..] : null;
		return string.IsNullOrEmpty(name) || char.IsDigit(name[0]) ? null : name;
	}

	/// <summary>One quoted string, or the command's own words re-joined into one shell line.</summary>
	private static string JoinRest(IReadOnlyList<string> words) =>
		words.Count == 1 ? words[0] : string.Join(' ', words.Select(word => word.Any(char.IsWhiteSpace) ? $"\"{word}\"" : word));

	private static string Article(string noun) => "aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "an" : "a";

	public bool Has(string name) => values.ContainsKey(name);

	/// <summary>The switch's value; repeated occurrences of a list switch are joined with commas.</summary>
	public string? Get(string name) =>
		values.TryGetValue(name, out var list) && list.Count > 0 ? string.Join(",", list) : null;

	public string Require(string name) => Get(name) ?? throw Usage($"-{name} is required.");

	/// <summary>A comma-separated list switch, given once or repeated.</summary>
	public IReadOnlyList<string> List(string name) =>
		values.TryGetValue(name, out var list)
			? list.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
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

	public CliUsageException Usage(string message) =>
		new(Command, $"{message} Usage: {Spec.UsageLine}. Run 'bassia help {Command}' for details and examples.");
}
