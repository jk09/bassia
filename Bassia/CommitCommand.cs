namespace Bassia;

using Bassia.Git;

/// <summary><c>bassia commit</c>: creates a commit in the meta-repo.</summary>
internal static class CommitCommand
{
	public static Task<int> RunAsync(string[] args)
	{
		if (args.Length != 2 || (args[0] != "-m" && args[0] != "--message") || string.IsNullOrWhiteSpace(args[1]))
		{
			Console.Error.WriteLine("Usage: bassia commit -m \"message\"");
			return Task.FromResult(2);
		}

		return ProgramCli.RunGitAsync(new GitClient(Environment.CurrentDirectory), ["commit", "-m", args[1]]);
	}
}
