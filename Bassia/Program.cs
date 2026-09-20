namespace Bassia;

using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bassia.Git;

static class Program
{
	public static async Task<int> Main(string[] args)
	{
		return await ProgramCli.RunAsync(args);
	}
}

internal static class ProgramCli
{
	// Relaxed escaping keeps quotes and paths in messages readable; the output is still valid JSON.
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	private const string DefaultConfigToml = "# Bassia meta-repo configuration\n";

	private const string DefaultComponentsToml =
		"# Bassia monorepo components\n" +
		"# Each registered component is recorded as:\n" +
		"# [[component]]\n" +
		"# name = \"component_1\"\n" +
		"# url = \"https://github.com/myrepo/component_1.git\"\n";

	public static async Task<int> RunAsync(string[] args)
	{
		if (args.Length == 0 || IsHelp(args[0]))
		{
			PrintHelp();
			return 0;
		}

		var git = new GitClient(Environment.CurrentDirectory);
		var command = args[0].ToLowerInvariant();

		try
		{
			return command switch
			{
				"status" => await RunGitAsync(git, ["status", "--short", "--branch"]),
				"log" => await RunGitAsync(git, ["log", "--oneline", "--decorate", "-n", "20"]),
				"branch" => await RunGitAsync(git, ["branch", "--list"]),
				"commit" => await CommitAsync(git, args[1..]),
				"setup" => await SetupAsync(git, args[1..]),
				"agent" => await AgentCommand.RunAsync(git, args[1..]),
				_ => UnknownCommand(command)
			};
		}
		catch (Win32Exception)
		{
			Console.Error.WriteLine("Git was not found. Install Git and ensure it is available on PATH.");
			return 1;
		}
	}

	private static async Task<int> CommitAsync(GitClient git, string[] args)
	{
		if (args.Length != 2 || (args[0] != "-m" && args[0] != "--message") || string.IsNullOrWhiteSpace(args[1]))
		{
			Console.Error.WriteLine("Usage: bassia commit -m \"message\"");
			return 2;
		}

		return await RunGitAsync(git, ["commit", "-m", args[1]]);
	}

	private static async Task<int> SetupAsync(GitClient git, string[] args)
	{
		if (args.Length == 0)
		{
			return WriteResult(false, "setup", "Usage: bassia setup <init|add-component> [arguments]");
		}

		var subcommand = args[0].ToLowerInvariant();
		return subcommand switch
		{
			"init" => await SetupInitAsync(git, args[1..]),
			"add-component" => await SetupAddComponentAsync(git, args[1..]),
			_ => WriteResult(false, "setup", $"Unknown setup subcommand '{subcommand}'.")
		};
	}

	private static async Task<int> SetupInitAsync(GitClient git, string[] args)
	{
		if (args.Length != 0)
		{
			return WriteResult(false, "setup init", "Usage: bassia setup init");
		}

		var root = Environment.CurrentDirectory;
		var metaRepoDir = Path.Combine(root, ".bassia");
		var workspaceDir = Path.Combine(root, ".workspace");

		if (Directory.Exists(metaRepoDir))
		{
			return WriteResult(false, "setup init", $"'{root}' is already a Bassia monorepo; '.bassia' already exists.");
		}

		if (Directory.EnumerateFileSystemEntries(root).Any())
		{
			return WriteResult(false, "setup init", $"'{root}' is not empty. Run 'bassia setup init' in an empty folder or volume.");
		}

		Directory.CreateDirectory(metaRepoDir);
		Directory.CreateDirectory(workspaceDir);

		var initResult = await git.RunAsync(["init", "--quiet", metaRepoDir]);
		if (initResult.ExitCode != 0)
		{
			return WriteResult(false, "setup init", $"git init failed: {initResult.Error.Trim()}");
		}

		await File.WriteAllTextAsync(Path.Combine(metaRepoDir, "config.toml"), DefaultConfigToml);
		await File.WriteAllTextAsync(Path.Combine(metaRepoDir, "components.toml"), DefaultComponentsToml);

		return WriteResult(true, "setup init", $"Initialized Bassia monorepo at '{root}'.", new Dictionary<string, object?>
		{
			["path"] = root,
			["metaRepo"] = metaRepoDir
		});
	}

	private static async Task<int> SetupAddComponentAsync(GitClient git, string[] args)
	{
		if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
		{
			return WriteResult(false, "setup add-component", "Usage: bassia setup add-component <repository-url>");
		}

		var url = args[0];
		var root = Environment.CurrentDirectory;
		var metaRepoDir = Path.Combine(root, ".bassia");
		var componentsTomlPath = Path.Combine(metaRepoDir, "components.toml");

		if (!Directory.Exists(metaRepoDir))
		{
			return WriteResult(false, "setup add-component", $"'{root}' is not a Bassia monorepo. Run 'bassia setup init' first.");
		}

		string name;
		try
		{
			name = DeriveComponentName(url);
		}
		catch (ArgumentException ex)
		{
			return WriteResult(false, "setup add-component", ex.Message);
		}

		var componentDir = Path.Combine(root, name);
		var componentGitDir = Path.Combine(componentDir, ".git");

		if (Directory.Exists(componentDir))
		{
			return WriteResult(false, "setup add-component", $"Component '{name}' already exists at '{componentDir}'.");
		}

		var cloneResult = await git.RunAsync(["clone", "--bare", url, componentGitDir]);
		if (cloneResult.ExitCode != 0)
		{
			if (Directory.Exists(componentDir))
			{
				Directory.Delete(componentDir, recursive: true);
			}

			return WriteResult(false, "setup add-component", $"git clone failed: {cloneResult.Error.Trim()}");
		}

		await File.AppendAllTextAsync(componentsTomlPath, $"\n[[component]]\nname = {TomlString(name)}\nurl = {TomlString(url)}\n");

		return WriteResult(true, "setup add-component", $"Added component '{name}' from '{url}'.", new Dictionary<string, object?>
		{
			["name"] = name,
			["url"] = url,
			["path"] = componentDir
		});
	}

	// TOML basic string: backslashes (Windows paths) and quotes must be escaped.
	private static string TomlString(string value) =>
		"\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

	private static string DeriveComponentName(string url)
	{
		var trimmed = url.Trim().TrimEnd('/', '\\');
		var lastSegment = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
		if (string.IsNullOrWhiteSpace(lastSegment))
		{
			throw new ArgumentException($"Could not derive a component name from '{url}'.");
		}

		if (lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			lastSegment = lastSegment[..^4];
		}

		return lastSegment;
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

	private static async Task<int> RunGitAsync(GitClient git, IReadOnlyList<string> arguments)
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
		Console.WriteLine("  setup init             Initialize a Bassia monorepo in the current empty folder");
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
