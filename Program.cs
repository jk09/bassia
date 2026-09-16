using System.ComponentModel;
using System.Diagnostics;

return await MiniRepoCli.RunAsync(args);

internal static class MiniRepoCli
{
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
			Console.Error.WriteLine("Usage: minirepo commit -m \"message\"");
			return 2;
		}

		return await RunGitAsync(git, ["commit", "-m", args[1]]);
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
		Console.Error.WriteLine($"Unknown command '{command}'. Run 'minirepo --help' for usage.");
		return 2;
	}

	private static void PrintHelp()
	{
		Console.WriteLine("MiniRepo - a Git-based version control CLI");
		Console.WriteLine();
		Console.WriteLine("Usage: minirepo <command>");
		Console.WriteLine();
		Console.WriteLine("Commands:");
		Console.WriteLine("  status                 Show the working tree status");
		Console.WriteLine("  log                    Show the latest commits");
		Console.WriteLine("  branch                 List local branches");
		Console.WriteLine("  commit -m \"message\"  Create a commit");
		Console.WriteLine("  help                   Show this help");
	}
}

internal sealed class GitClient
{
	private readonly string workingDirectory;

	public GitClient(string workingDirectory)
	{
		this.workingDirectory = workingDirectory;
	}

	public async Task<GitResult> RunAsync(IReadOnlyList<string> arguments)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "git",
				WorkingDirectory = workingDirectory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};

		foreach (var argument in arguments)
		{
			process.StartInfo.ArgumentList.Add(argument);
		}

		process.Start();
		var outputTask = process.StandardOutput.ReadToEndAsync();
		var errorTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();

		return new GitResult(process.ExitCode, await outputTask, await errorTask);
	}
}

internal sealed record GitResult(int ExitCode, string Output, string Error);
