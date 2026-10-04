namespace Bassia;

using System.ComponentModel;
using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Integration;

internal static class ProgramCli
{
	/// <summary>
	/// Runs one command line: <c>bassia [-C &lt;path&gt;] &lt;command&gt; [&lt;subcommand&gt;] [-switch [value]]...</c>. The command
	/// and its subcommand are looked up in <see cref="CommandTable"/>, the rest is parsed against that command's
	/// switches, and the handler prints a TOML result. Exit codes: 0 success, 1 failure, 2 a command line that does
	/// not parse (an unknown command, subcommand or switch, or a missing value).
	/// </summary>
	public static async Task<int> RunAsync(string[] args)
	{
		var (remainingArgs, workingDirectoryError) = ConsumeWorkingDirectoryOption(args);
		if (workingDirectoryError is not null)
		{
			return UsageError("bassia", workingDirectoryError);
		}

		args = remainingArgs;
		if (args.Length == 0 || IsHelp(args[0]))
		{
			return Help.Show(args.Skip(1).ToList());
		}

		var name = args[0];
		if (CommandTable.Replaced.TryGetValue(name, out var replacement))
		{
			return UsageError(name.ToLowerInvariant(), $"{replacement} Run 'bassia help' for every command.");
		}

		var group = CommandTable.Group(name);
		if (group.Count == 0)
		{
			return UsageError(name.ToLowerInvariant(), $"Unknown command '{name.ToLowerInvariant()}'. Run 'bassia help' for every command.");
		}

		CommandSpec spec;
		var rest = args[1..];
		if (group.Count == 1 && group[0].Sub is null)
		{
			spec = group[0];
		}
		else
		{
			var subcommands = string.Join(", ", group.Select(command => command.Sub));
			if (rest.Length == 0 || IsHelp(rest[0]))
			{
				return Help.Show([group[0].Name]);
			}

			spec = group.FirstOrDefault(command => string.Equals(command.Sub, rest[0], StringComparison.OrdinalIgnoreCase))!;
			if (spec is null)
			{
				return UsageError(group[0].Name, $"'bassia {group[0].Name}' has no subcommand '{rest[0]}'; it has {subcommands}. Run 'bassia help {group[0].Name}'.");
			}

			rest = rest[1..];
		}

		if (spec.Name == "help")
		{
			return Help.Show(rest.Where(word => !IsHelp(word) && word != "-command" && word != "--command").ToList());
		}

		try
		{
			var invocation = Invocation.Parse(spec, rest);
			return invocation.HelpRequested
				? Help.Show(spec.Sub is null ? [spec.Name] : [spec.Name, spec.Sub])
				: await spec.Handler(invocation);
		}
		catch (CliUsageException ex)
		{
			return UsageError(ex.Command, ex.Message);
		}
		catch (Win32Exception)
		{
			return WriteResult(false, spec.FullName, "Git was not found. Install Git and ensure it is available on PATH.");
		}
		catch (Exception ex) when (IsReportable(ex))
		{
			return WriteResult(false, spec.FullName, ex.Message);
		}
	}

	/// <summary>The failures a command reports as its TOML error rather than as a crash.</summary>
	internal static bool IsReportable(Exception ex) =>
		ex is AgentException or MonorepoException or GitException or IntegrationException or Prompt.PromptException or IOException or UnauthorizedAccessException;

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

		var toml = TomlResult.Serialize(payload);
		if (ok)
		{
			Console.WriteLine(toml);
		}
		else
		{
			Console.Error.WriteLine(toml);
		}

		return ok ? 0 : 1;
	}

	// Mirrors "git -C <path>": run as if bassia had started in <path>. May repeat; each occurrence resolves
	// relative to the directory left by the previous one, matching git's chaining behavior.
	private static (string[] Args, string? Error) ConsumeWorkingDirectoryOption(string[] args)
	{
		var index = 0;
		while (index < args.Length && args[index] == "-C")
		{
			if (index + 1 >= args.Length)
			{
				return (args, "-C requires a path. Usage: bassia -C <path> <command> ...");
			}

			var path = args[index + 1];
			try
			{
				var target = Path.GetFullPath(path);
				if (!Directory.Exists(target))
				{
					return (args, $"Cannot change to '{path}': No such directory.");
				}

				Environment.CurrentDirectory = target;
			}
			catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
			{
				return (args, $"Cannot change to '{path}': {ex.Message}");
			}

			index += 2;
		}

		return (args[index..], null);
	}

	private static bool IsHelp(string argument) => argument is "-h" or "--help" or "-help" or "help";
}
