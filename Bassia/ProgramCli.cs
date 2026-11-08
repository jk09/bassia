namespace Bassia;

using System.CommandLine;
using System.CommandLine.Parsing;
using Bassia.Cli;

internal static class ProgramCli
{
	/// <summary>
	/// Whether a person is at a terminal: stdin, stdout and stderr none redirected. Only then does a bare <c>bassia</c>
	/// print the short help; agents and scripts get the full help as TOML.
	/// </summary>
	internal static Func<bool> Interactive { get; set; } = () => !Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected;

	/// <summary>
	/// What the library may not do on its own: bassia takes <c>@profile</c> as a value (not a response file) and its
	/// switches are never bundled.
	/// </summary>
	private static readonly ParserConfiguration Parser = new() { ResponseFileTokenReplacer = null, EnablePosixBundling = false };

	/// <summary>
	/// Runs one command line: <c>bassia [-C &lt;path&gt;] &lt;command&gt; [&lt;subcommand&gt;] [-switch [value]]...</c>. The
	/// line is parsed by <c>System.CommandLine</c> against <see cref="CommandTable.Root"/>, which declares every command,
	/// switch and argument; the handler of the command it names prints a TOML result. Exit codes: 0 success, 1 failure,
	/// 2 a command line that does not parse (an unknown command, subcommand or switch, or a missing value).
	/// </summary>
	public static async Task<int> RunAsync(string[] args)
	{
		HumanOutput.Enabled = false;
		var root = CommandTable.Root;
		var line = CommandLineNormalizer.Normalize(root, args);
		var parse = root.Parse(line.Tokens, Parser);
		HumanOutput.Enabled = parse.GetValue(CommandTable.Human);
		if (ChangeWorkingDirectory(parse.GetValue(CommandTable.WorkingDirectory) ?? []) is { } workingDirectoryError)
		{
			return UsageError("bassia", workingDirectoryError);
		}

		var helpRequested = parse.GetValue(CommandTable.HelpRequested);
		var unmatched = parse.UnmatchedTokens;
		if (parse.CommandResult.Command is not BassiaCommand command)
		{
			if (unmatched.Count == 0 || IsHelp(unmatched[0]))
			{
				return unmatched.Count == 0 && !helpRequested && !HumanOutput.Enabled && Interactive()
					? Print(Help.ShortText)
					: Help.Show([]);
			}

			var name = unmatched[0];
			return UsageError(name.ToLowerInvariant(), CommandTable.Replaced.TryGetValue(name, out var replacement)
				? $"{replacement} Run 'bassia help' for every command."
				: $"Unknown command '{name.ToLowerInvariant()}'. Run 'bassia help' for every command.");
		}

		if (command.IsGroup)
		{
			var path = command.Lineage.Select(part => part.Name).ToList();
			return unmatched.Count == 0 || IsHelp(unmatched[0]) || helpRequested
				? Help.Show(path)
				: UsageError(command.FullName, $"'bassia {command.FullName}' has no subcommand '{unmatched[0]}'; it has {string.Join(", ", command.Nested.Select(child => child.Name))}. Run 'bassia help {command.FullName}'.");
		}

		if (helpRequested && command.Name != "help")
		{
			return Help.Show(command.Lineage.Select(part => part.Name).ToList());
		}

		try
		{
			if (UsageProblem(command, parse, line) is { } problem)
			{
				return UsageError(problem.Command, problem.Message);
			}

			return await command.Handler!(new Invocation(command, parse, line.Rest));
		}
		catch (CliUsageException ex)
		{
			return UsageError(ex.Command, ex.Message);
		}
		catch (Exception ex) when (IsReportable(ex))
		{
			return WriteResult(false, command.FullName, ex.Message);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// An exception no command expects is a crash: say so, and file it as an issue if the user agreed to that.
			var report = await Feedback.CrashReporter.HandleAsync(command.FullName, ex);
			return WriteResult(false, command.FullName, $"Bassia stopped unexpectedly ({ex.GetType().Name}: {ex.Message}).",
				report is null ? null : new Dictionary<string, object?> { ["report"] = report });
		}
	}

	/// <summary>Why the command line does not fit its command, if it does not: what the library found, and the rest-of-line switch it never saw.</summary>
	private static CliUsageException? UsageProblem(BassiaCommand command, ParseResult parse, Normalized line)
	{
		var rest = line.Rest;
		if (line.UnknownSwitches.Count > 0)
		{
			return Invocation.Usage(command, $"Unknown switch '{line.UnknownSwitches[0]}'.");
		}

		if (rest is { Text.Length: 0 } && command.FindSwitch(rest.Switch) is { } restOption)
		{
			var noun = restOption.ValueName() ?? "value";
			return Invocation.Usage(command, $"-{rest.Switch} requires {("aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "an" : "a")} {noun}.");
		}

		// A problem with a token ("-path" without a value) comes before a command-level one ("-name is required").
		if (parse.Errors.OrderBy(error => error.SymbolResult is CommandResult).FirstOrDefault() is { } first)
		{
			return Invocation.Usage(command, first.Message);
		}

		// What the library cannot check: a needed switch may be given as the command's bare argument, or be rest-of-line text.
		var invocation = new Invocation(command, parse, rest);
		foreach (var option in command.Options)
		{
			if (!invocation.Has(option.SwitchName()) && option.IsRequired())
			{
				return Invocation.Usage(command, $"-{option.SwitchName()} is required.");
			}
		}

		return null;
	}

	private static int Print(string text)
	{
		Console.WriteLine(text);
		return 0;
	}

	/// <summary>The failures a command reports as its TOML error rather than as a crash.</summary>
	internal static bool IsReportable(Exception ex) =>
		ex is MonorepoException or Stores.SchemaException or Core.Storage.VersionException or Interop.GitException or Feedback.FeedbackException or IOException
			or UnauthorizedAccessException or HttpRequestException or TaskCanceledException { InnerException: TimeoutException };

	/// <summary>A command line that does not parse: a TOML error on stderr, exit code 2.</summary>
	internal static int UsageError(string command, string message)
	{
		WriteResult(false, command, message);
		return 2;
	}

	internal static int WriteResult(bool ok, string command, string message, IReadOnlyDictionary<string, object?>? data = null)
	{
		var payload = new Dictionary<string, object?>
		{
			["ok"] = ok,
			["command"] = command,
			[ok ? "message" : "error"] = message
		};

		if (data is not null)
		{
			foreach (var (key, value) in data)
			{
				if (payload.ContainsKey(key) || key is "ok" or "command" or "message" or "error")
				{
					throw new InvalidOperationException($"A result's data may not set '{key}', which every result reserves.");
				}

				payload[key] = value;
			}
		}

		var text = HumanOutput.Enabled ? HumanOutput.Render(payload) : TomlResult.Serialize(payload);
		if (ok)
		{
			Console.WriteLine(text);
		}
		else
		{
			Console.Error.WriteLine(text);
		}

		return ok ? 0 : 1;
	}

	// Mirrors "git -C <path>": run as if bassia had started in <path>. May repeat; each occurrence resolves
	// relative to the directory left by the previous one, matching git's chaining behavior.
	private static string? ChangeWorkingDirectory(IEnumerable<string> paths)
	{
		foreach (var path in paths)
		{
			try
			{
				var target = Path.GetFullPath(path);
				if (!Directory.Exists(target))
				{
					return $"Cannot change to '{path}': No such directory.";
				}

				Environment.CurrentDirectory = target;
			}
			catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
			{
				return $"Cannot change to '{path}': {ex.Message}";
			}
		}

		return null;
	}

	private static bool IsHelp(string argument) => argument is "-h" or "--help" or "-help" or "help";
}
