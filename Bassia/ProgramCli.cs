namespace Bassia;

using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bassia.CliCommands;
using Bassia.Git;
using PowerArgs;

internal static class ProgramCli
{
	// Relaxed escaping keeps quotes and paths in messages readable; the output is still valid JSON.
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	// bassia's subcommands (commit, setup, agent) parse their own arguments by hand: their grammars - nested
	// subcommands, "-run" swallowing the rest of the command line verbatim - don't fit PowerArgs' declarative
	// argument binding. PowerArgs' action framework is used only to route the first argument to the matching
	// CliActions method below, which then reads its share of the untouched original arguments from here.
	private static string[] rawArgs = [];
	private static int exitCode;

	public static async Task<int> RunAsync(string[] args)
	{
		if (args.Length == 0 || IsHelp(args[0]))
		{
			PrintHelp();
			return 0;
		}

		rawArgs = args;
		exitCode = 0;

		try
		{
			await Args.InvokeActionAsync<CliActions>(args);
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

		[ArgActionMethod, ArgDescription("Register a component")]
		public Task Setup() => RecordAsync(SetupCommand.RunAsync(SubArgs()));

		[ArgActionMethod, ArgDescription("Materialize selected components and run an agentic command over them")]
		public Task Agent() => RecordAsync(AgentCommand.RunAsync(SubArgs()));

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

		var json = JsonSerializer.Serialize(payload, JsonOptions);
		if (ok)
		{
			Console.WriteLine(json);
		}
		else
		{
			Console.Error.WriteLine(json);
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
		Console.WriteLine("Usage: bassia <command>");
		Console.WriteLine();
		Console.WriteLine("Commands:");
		Console.WriteLine("  status                 Show the working tree status");
		Console.WriteLine("  log                    Show the latest commits");
		Console.WriteLine("  branch                 List local branches");
		Console.WriteLine("  commit -m \"message\"  Create a commit");
		Console.WriteLine("  init [directory]       Initialize a Bassia monorepo (default: the current empty folder)");
		Console.WriteLine("  setup add-component <url>");
		Console.WriteLine("                         Clone a repo as a bare Bassia monorepo component");
		Console.WriteLine("  agent -select <component@tag>[,<component@tag>...] [-pin <component@tag>] -run <command>");
		Console.WriteLine("                         Materialize the selected components in an isolated workspace,");
		Console.WriteLine("                         run the agent command there, then commit, tag and push the results");
		Console.WriteLine("  agent retry <run-id>   Retry committing/pushing components that failed in a previous run");
		Console.WriteLine("  agent abandon <run-id> Discard a run's workspace and cache (sources of truth are untouched)");
		Console.WriteLine("  help                   Show this help");
	}
}
