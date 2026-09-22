namespace Bassia;

using System.ComponentModel;
using Bassia.CliCommands.Agent;


using Bassia.Git;
using PowerArgs;

internal static class ProgramCli
{
	// bassia's subcommands (commit, add-component, agent) parse their own arguments by hand: their grammars - nested
	// subcommands, "-run" swallowing the rest of the command line verbatim - don't fit PowerArgs' declarative
	// argument binding. PowerArgs' action framework is used only to route the first argument to the matching
	// CliActions method below, which then reads its share of the untouched original arguments from here. Only
	// that first token is ever handed to PowerArgs: giving it the subcommand's own arguments too makes it parse
	// them against the (parameterless) action method, which throws UnexpectedArgException on inputs its
	// declarative binder can't make sense of - e.g. a bare token with no preceding flag, which is exactly what a
	// shell produces when it flattens a comma-separated array argument (`-select 'a', 'b'`) into extra words.
	private static string[] rawArgs = [];
	private static int exitCode;

	public static async Task<int> RunAsync(string[] args)
	{
		var (remainingArgs, workingDirectoryError) = ConsumeWorkingDirectoryOption(args);
		if (workingDirectoryError is not null)
		{
			Console.Error.WriteLine(workingDirectoryError);
			return 2;
		}

		args = remainingArgs;

		if (args.Length == 0 || IsHelp(args[0]))
		{
			PrintHelp();
			return 0;
		}

		rawArgs = args;
		exitCode = 0;

		try
		{
			await Args.InvokeActionAsync<CliActions>(args[0]);
			return exitCode;
		}
		catch (UnknownActionArgException)
		{
			return UnknownCommand(args[0].ToLowerInvariant());
		}
		catch (Win32Exception)
		{
			Console.Error.WriteLine("Git was not found. Install Git and ensure it is available on PATH.");
			return 1;
		}
	}

	// PowerArgs' action framework: matches bassia's first command-line argument (case-insensitively) to one of
	// these methods. AllowUnexpectedArgs lets the rest of the command line - including "-"-prefixed tokens such
	// as commit's "-m" or agent's "-select"/"-run" - pass through unvalidated, since SubArgs() below hands it to
	// the same hand-written parsers the CLI used before this rewrite. Each action delegates to its own service
	// class, which constructs whatever GitClient it needs itself.
	[AllowUnexpectedArgs, ArgExceptionBehavior(ArgExceptionPolicy.DontHandleExceptions)]
	public sealed class CliActions
	{
		[ArgActionMethod, ArgDescription("Show the working tree status")]
		public Task Status() => RecordAsync(StatusCommand.RunAsync());

		[ArgActionMethod, ArgDescription("Show the latest commits")]
		public Task Log() => RecordAsync(LogCommand.RunAsync());

		[ArgActionMethod, ArgDescription("List local branches")]
		public Task Branch() => RecordAsync(BranchCommand.RunAsync());

		[ArgActionMethod, ArgDescription("Create a commit")]
		public Task Commit() => RecordAsync(CommitCommand.RunAsync(SubArgs()));

		[ArgActionMethod, ArgDescription("Initialize a Bassia monorepo")]
		public Task Init() => RecordAsync(InitCommand.RunAsync(SubArgs()));

		[ArgActionMethod, ArgShortcut("add-component"), ArgDescription("Register a component")]
		public Task AddComponent() => RecordAsync(AddComponentCommand.RunAsync(SubArgs()));

		[ArgActionMethod, ArgDescription("Materialize selected components and run an agentic command over them")]
		public Task Agent() => RecordAsync(AgentCommand.RunAsync(SubArgs()));

		[ArgActionMethod, ArgDescription("Open the interactive frontend")]
		public Task Ui() => RecordAsync(UiCommand.RunAsync());

		private static string[] SubArgs() => rawArgs[1..];

		private static async Task RecordAsync(Task<int> command) => exitCode = await command;
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

	internal static async Task<int> RunGitAsync(GitClient git, IReadOnlyList<string> arguments)
	{
		var result = await git.RunAsync(arguments);
		if (!string.IsNullOrEmpty(result.Output))
		{
			Console.Write(result.Output);
		}

		if (result.ExitCode != 0 && !string.IsNullOrEmpty(result.Error))
		{
			Console.Error.Write(result.Error);
		}

		return result.ExitCode;
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
				return (args, "Usage: bassia -C <path> <command> ...");
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

	private static bool IsHelp(string argument) => argument is "-h" or "--help" or "help";

	private static int UnknownCommand(string command)
	{
		Console.Error.WriteLine($"Unknown command '{command}'. Run 'bassia --help' for usage.");
		return 2;
	}

	private static void PrintHelp()
	{
		Console.WriteLine("bassia - a Git-based version control CLI");
		Console.WriteLine();
		Console.WriteLine("Usage: bassia [-C <path>] <command>");
		Console.WriteLine();
		Console.WriteLine("  -C <path>              Run as if bassia was started in <path> instead of the current directory");
		Console.WriteLine();
		Console.WriteLine("Commands:");
		Console.WriteLine("  status                 Show the working tree status");
		Console.WriteLine("  log                    Show the latest commits");
		Console.WriteLine("  branch                 List local branches");
		Console.WriteLine("  commit -m \"message\"  Create a commit");
		Console.WriteLine("  init [directory]       Initialize a Bassia monorepo (default: the current empty folder)");
		Console.WriteLine("  add-component <url> [name]");
		Console.WriteLine("                         Clone a repo as a bare Bassia monorepo component");
		Console.WriteLine("  agent -select <component@tag>[,<component@tag>...] -run <command>");
		Console.WriteLine("                         Materialize the selected components (the full reference closure,");
		Console.WriteLine("                         each at an annotated tag) in an isolated run folder, run the agent");
		Console.WriteLine("                         command there, then commit, tag and push the results");
		Console.WriteLine("  agent retry <run-id>   Retry committing/pushing components that failed in a previous run");
		Console.WriteLine("  agent abandon <run-id> Discard a run's folder (sources of truth are untouched)");
		Console.WriteLine("  ui                     Open the interactive frontend: components, their refs and");
		Console.WriteLine("                         dependency graph, agentic runs, tagging and starting runs");
		Console.WriteLine("  help                   Show this help");
	}
}
