namespace Bassia.Prompt;

using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

/// <summary>One command the LLM wants run: its arguments (without <c>bassia</c>) and why.</summary>
internal sealed record ProposedCommand(IReadOnlyList<string> Args, string? Why);

/// <summary>
/// The LLM's answer to one round, in TOML:
/// <code>
/// done = false
/// answer = "..."
/// load_skills = ["release"]
/// [[command]]
/// args = ["init", "-path", 'C:\mono']
/// why = "..."
/// </code>
/// </summary>
internal sealed record PromptReply(IReadOnlyList<ProposedCommand> Commands, bool Done, string? Answer, IReadOnlyList<string> LoadSkills)
{
	private static readonly Regex Fence = new(@"```[a-zA-Z]*[ \t]*\r?\n(?<body>.*?)```", RegexOptions.Singleline);
	private static readonly Regex TomlStart = new(@"^[ \t]*(\[\[command\]\]|done[ \t]*=|answer[ \t]*=|load_skills[ \t]*=)", RegexOptions.Multiline);

	/// <summary>
	/// Reads a reply. Models like to wrap TOML in a code fence or to say something first, so a fenced block that
	/// parses wins, then the whole text, then the text from the first line that opens the expected TOML.
	/// </summary>
	public static PromptReply Parse(string text)
	{
		var candidates = new List<string>();
		candidates.AddRange(Fence.Matches(text).Select(match => match.Groups["body"].Value));
		candidates.Add(text);
		if (TomlStart.Match(text) is { Success: true } start)
		{
			candidates.Add(text[start.Index..]);
		}

		string? firstError = null;
		foreach (var candidate in candidates)
		{
			try
			{
				var table = TomlSerializer.Deserialize<TomlTable>(candidate);
				if (table is not null && (table.ContainsKey("command") || table.ContainsKey("done") || table.ContainsKey("answer") || table.ContainsKey("load_skills")))
				{
					return FromTable(table);
				}
			}
			catch (TomlException ex)
			{
				firstError ??= ex.Message;
			}
			catch (PromptException ex)
			{
				firstError ??= ex.Message;
			}
		}

		throw new PromptException($"The LLM's reply is not the expected TOML{(firstError is null ? "" : $" ({firstError.Split('\n')[0]})")}.");
	}

	private static PromptReply FromTable(TomlTable table)
	{
		var commands = new List<ProposedCommand>();
		if (table.TryGetValue("command", out var commandValue))
		{
			if (commandValue is not TomlTableArray entries)
			{
				throw new PromptException("'command' must be an array of tables ([[command]]).");
			}

			foreach (TomlTable entry in entries)
			{
				var args = entry.TryGetValue("args", out var argsValue) ? argsValue switch
				{
					TomlArray array => array.Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList(),
					string line => SplitLine(line),
					_ => throw new PromptException("A command's 'args' must be an array of strings.")
				} : throw new PromptException("Every [[command]] needs 'args'.");
				if (args.Count > 0 && string.Equals(args[0], "bassia", StringComparison.OrdinalIgnoreCase))
				{
					args.RemoveAt(0);
				}

				commands.Add(new ProposedCommand(args, entry.TryGetValue("why", out var why) ? why as string : null));
			}
		}

		var skills = table.TryGetValue("load_skills", out var skillValue) && skillValue is TomlArray skillArray
			? skillArray.OfType<string>().ToList()
			: [];
		var answer = table.TryGetValue("answer", out var answerValue) ? answerValue as string : null;
		var done = table.TryGetValue("done", out var doneValue) && doneValue is bool flag
			? flag
			: commands.Count == 0 && skills.Count == 0;
		return new PromptReply(commands, done, string.IsNullOrWhiteSpace(answer) ? null : answer.Trim(), skills);
	}

	/// <summary>A command line given as one string: words split at whitespace, double quotes group words.</summary>
	internal static List<string> SplitLine(string line)
	{
		var words = new List<string>();
		var word = new StringBuilder();
		var quoted = false;
		var any = false;
		foreach (var c in line)
		{
			if (c == '"')
			{
				quoted = !quoted;
				any = true;
			}
			else if (char.IsWhiteSpace(c) && !quoted)
			{
				if (any)
				{
					words.Add(word.ToString());
					word.Clear();
					any = false;
				}
			}
			else
			{
				word.Append(c);
				any = true;
			}
		}

		if (any)
		{
			words.Add(word.ToString());
		}

		return words;
	}
}

/// <summary>A prompt that cannot go on: the backend failed, the reply cannot be read, or a skill is unknown.</summary>
internal sealed class PromptException(string message) : Exception(message);
