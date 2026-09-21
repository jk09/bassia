namespace Bassia;

using Bassia.Git;

/// <summary><c>bassia init</c>: initializes a Bassia monorepo.</summary>
internal static class InitCommand
{
	private const string DefaultConfigToml =
		"# Bassia meta-repo configuration\n" +
		"\n" +
		"[agent.commit]\n" +
		"# Message of the commits an agentic run makes in a component. Placeholders: {run_id} (agent-run-<id>),\n" +
		"# {short_id} (first 8 digits of <id>), {summary} (from the agent command), {component}, and {metadata}, which\n" +
		"# expands to a TOML record of the run ([agentic_run] / [agentic_run.component]).\n" +
		"message = \"\"\"\n" + Monorepo.DefaultCommitMessage + "\"\"\"\n";

	private const string DefaultComponentsToml =
		"# Bassia monorepo components\n" +
		"# Each registered component is recorded as:\n" +
		"# [[component]]\n" +
		"# name = \"component_1\"\n" +
		"# url = \"https://github.com/myrepo/component_1.git\"\n";

	public static async Task<int> RunAsync(string[] args)
	{
		if (args.Length > 1)
		{
			return ProgramCli.WriteResult(false, "init", "Usage: bassia init [directory]");
		}

		var root = Path.GetFullPath(args.Length == 1 ? args[0] : Environment.CurrentDirectory);
		var metaRepoDir = Path.Combine(root, ".bassia");
		var workspaceDir = Path.Combine(root, ".workspace");

		if (Directory.Exists(metaRepoDir))
		{
			return ProgramCli.WriteResult(false, "init", $"'{root}' is already a Bassia monorepo; '.bassia' already exists.");
		}

		if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
		{
			return ProgramCli.WriteResult(false, "init", $"'{root}' is not empty. Run 'bassia init' in an empty folder or volume.");
		}

		Directory.CreateDirectory(metaRepoDir);
		Directory.CreateDirectory(workspaceDir);

		var git = new GitClient(Environment.CurrentDirectory);
		var initResult = await git.RunAsync(["init", "--quiet", metaRepoDir]);
		if (initResult.ExitCode != 0)
		{
			return ProgramCli.WriteResult(false, "init", $"git init failed: {initResult.Error.Trim()}");
		}

		await File.WriteAllTextAsync(Path.Combine(metaRepoDir, "config.toml"), DefaultConfigToml);
		await File.WriteAllTextAsync(Path.Combine(metaRepoDir, "components.toml"), DefaultComponentsToml);

		var commitResult = await GitClient.In(metaRepoDir).CommitAllAsync("Initialize Bassia meta-repo");
		if (commitResult.ExitCode != 0)
		{
			return ProgramCli.WriteResult(false, "init", $"git commit failed: {commitResult.Error.Trim()}");
		}

		return ProgramCli.WriteResult(true, "init", $"Initialized Bassia monorepo at '{root}'.", new Dictionary<string, object?>
		{
			["path"] = root,
			["metaRepo"] = metaRepoDir
		});
	}
}
