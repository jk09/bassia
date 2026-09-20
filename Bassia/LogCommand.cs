namespace Bassia;

using Bassia.Git;

/// <summary><c>bassia log</c>: shows the meta-repo's latest commits.</summary>
internal static class LogCommand
{
	public static Task<int> RunAsync() =>
		ProgramCli.RunGitAsync(new GitClient(Environment.CurrentDirectory), ["log", "--oneline", "--decorate", "-n", "20"]);
}
