namespace Bassia;

using Bassia.Git;

/// <summary><c>bassia branch</c>: lists the meta-repo's local branches.</summary>
internal static class BranchCommand
{
	public static Task<int> RunAsync() =>
		ProgramCli.RunGitAsync(new GitClient(Environment.CurrentDirectory), ["branch", "--list"]);
}
