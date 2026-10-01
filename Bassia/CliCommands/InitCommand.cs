namespace Bassia;

using Bassia.Cli;
using Bassia.Git;

/// <summary><c>bassia init</c>: initializes a Bassia monorepo.</summary>
internal static class InitCommand
{
	private const string DefaultConfigToml =
		"# Bassia meta-repo configuration. Read and change it with 'bassia config list|get|set'.\n" +
		"\n" +
		"[agent]\n" +
		"# Agent command of a run started from a prompt ('bassia run start -prompt ...', 'bassia web');\n" +
		"# the prompt is appended as one quoted argument.\n" +
		"command = \"" + Monorepo.DefaultAgentCommand + "\"\n" +
		"\n" +
		"[agent.commit]\n" +
		"# Subject line of the commits an agentic run makes in a component. The body is always a TOML record of the run.\n" +
		"# Placeholders: {run_id} (agent-run-<key>), {short_id} (the <key>, e.g. magical-otter-vt9j3p), {summary} (from the agent command), {component}.\n" +
		"subject = \"" + Monorepo.DefaultCommitSubject + "\"\n" +
		"\n" +
		"[integration]\n" +
		"# Command that resolves a semantic merge during 'bassia integration start'. It runs in the component's working tree with the\n" +
		"# merge brief on stdin (and its path in BASSIA_MERGE_BRIEF), and must leave the merge resolved but uncommitted.\n" +
		"resolver = \"" + Monorepo.DefaultResolver + "\"\n";

	private const string DefaultComponentsToml =
		"# Bassia monorepo components\n" +
		"# Each registered component is recorded as:\n" +
		"# [[component]]\n" +
		"# name = \"component_1\"\n" +
		"# url = \"https://github.com/myrepo/component_1.git\"\n";

	public static async Task<int> RunAsync(Invocation invocation)
	{
		var root = Path.GetFullPath(invocation.Get("path") ?? Environment.CurrentDirectory);
		var metaRepoDir = Path.Combine(root, ".bassia");
		var workspaceDir = Path.Combine(root, ".workspace");

		if (Directory.Exists(metaRepoDir))
		{
			return ProgramCli.WriteResult(false, "init", $"'{root}' is already a Bassia monorepo; '.bassia' already exists.");
		}

		if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
		{
			return ProgramCli.WriteResult(false, "init", $"'{root}' is not empty. Run 'bassia init' in an empty folder or volume, or name one with -path.");
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

		return ProgramCli.WriteResult(true, "init", $"Initialized Bassia monorepo at '{root}'. Add components with 'bassia component add -url <url>'.", new Dictionary<string, object?>
		{
			["path"] = root,
			["meta_repo"] = metaRepoDir,
			["workspace"] = workspaceDir
		});
	}
}
