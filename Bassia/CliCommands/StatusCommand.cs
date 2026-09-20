namespace Bassia;

using Bassia.Git;

/// <summary><c>bassia status</c>: shows the meta-repo's working tree status.</summary>
internal static class StatusCommand
{
	public static Task<int> RunAsync() =>
		ProgramCli.RunGitAsync(new GitClient(Environment.CurrentDirectory), ["status", "--short", "--branch"]);
}
