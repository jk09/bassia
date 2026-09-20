namespace Bassia;

using Bassia.Git;

/// <summary><c>bassia add-component</c>: registers a component with the meta-repo.</summary>
internal static class AddComponentCommand
{
	public static async Task<int> RunAsync(string[] args)
	{
		if (args.Length is < 1 or > 2 || string.IsNullOrWhiteSpace(args[0]))
		{
			return ProgramCli.WriteResult(false, "add-component", "Usage: bassia add-component <repository-url> [<name>]");
		}

		var url = args[0];
		var root = Environment.CurrentDirectory;
		var metaRepoDir = Path.Combine(root, ".bassia");
		var componentsTomlPath = Path.Combine(metaRepoDir, "components.toml");

		if (!Directory.Exists(metaRepoDir))
		{
			return ProgramCli.WriteResult(false, "add-component", $"'{root}' is not a Bassia monorepo. Run 'bassia init' first.");
		}

		string name;
		if (args.Length == 2)
		{
			if (string.IsNullOrWhiteSpace(args[1]))
			{
				return ProgramCli.WriteResult(false, "add-component", "Usage: bassia add-component <repository-url> [<name>]");
			}

			name = args[1];
		}
		else
		{
			try
			{
				name = DeriveComponentName(url);
			}
			catch (ArgumentException ex)
			{
				return ProgramCli.WriteResult(false, "add-component", ex.Message);
			}
		}

		var componentDir = Path.Combine(root, name);
		var componentGitDir = Path.Combine(componentDir, ".git");

		if (Directory.Exists(componentDir))
		{
			return ProgramCli.WriteResult(false, "add-component", $"Component '{name}' already exists at '{componentDir}'.");
		}

		var git = new GitClient(root);
		var cloneResult = await git.RunAsync(["clone", "--bare", url, componentGitDir]);
		if (cloneResult.ExitCode != 0)
		{
			if (Directory.Exists(componentDir))
			{
				Directory.Delete(componentDir, recursive: true);
			}

			return ProgramCli.WriteResult(false, "add-component", $"git clone failed: {cloneResult.Error.Trim()}");
		}

		await File.AppendAllTextAsync(componentsTomlPath, $"\n[[component]]\nname = {TomlString(name)}\nurl = {TomlString(url)}\n");

		var commitResult = await GitClient.In(metaRepoDir).CommitAllAsync($"Add component '{name}' from '{url}'");
		if (commitResult.ExitCode != 0)
		{
			return ProgramCli.WriteResult(false, "add-component", $"git commit failed: {commitResult.Error.Trim()}");
		}

		return ProgramCli.WriteResult(true, "add-component", $"Added component '{name}' from '{url}'.", new Dictionary<string, object?>
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
}
