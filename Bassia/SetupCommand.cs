namespace Bassia;

using Bassia.Git;

/// <summary><c>bassia setup</c>: registers components with the meta-repo.</summary>
internal static class SetupCommand
{
	public static async Task<int> RunAsync(string[] args)
	{
		if (args.Length == 0)
		{
			return ProgramCli.WriteResult(false, "setup", "Usage: bassia setup <add-component> [arguments]");
		}

		var subcommand = args[0].ToLowerInvariant();
		return subcommand switch
		{
			"add-component" => await AddComponentAsync(args[1..]),
			_ => ProgramCli.WriteResult(false, "setup", $"Unknown setup subcommand '{subcommand}'.")
		};
	}

	private static async Task<int> AddComponentAsync(string[] args)
	{
		if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
		{
			return ProgramCli.WriteResult(false, "setup add-component", "Usage: bassia setup add-component <repository-url>");
		}

		var url = args[0];
		var root = Environment.CurrentDirectory;
		var metaRepoDir = Path.Combine(root, ".bassia");
		var componentsTomlPath = Path.Combine(metaRepoDir, "components.toml");

		if (!Directory.Exists(metaRepoDir))
		{
			return ProgramCli.WriteResult(false, "setup add-component", $"'{root}' is not a Bassia monorepo. Run 'bassia init' first.");
		}

		string name;
		try
		{
			name = DeriveComponentName(url);
		}
		catch (ArgumentException ex)
		{
			return ProgramCli.WriteResult(false, "setup add-component", ex.Message);
		}

		var componentDir = Path.Combine(root, name);
		var componentGitDir = Path.Combine(componentDir, ".git");

		if (Directory.Exists(componentDir))
		{
			return ProgramCli.WriteResult(false, "setup add-component", $"Component '{name}' already exists at '{componentDir}'.");
		}

		var git = new GitClient(root);
		var cloneResult = await git.RunAsync(["clone", "--bare", url, componentGitDir]);
		if (cloneResult.ExitCode != 0)
		{
			if (Directory.Exists(componentDir))
			{
				Directory.Delete(componentDir, recursive: true);
			}

			return ProgramCli.WriteResult(false, "setup add-component", $"git clone failed: {cloneResult.Error.Trim()}");
		}

		await File.AppendAllTextAsync(componentsTomlPath, $"\n[[component]]\nname = {TomlString(name)}\nurl = {TomlString(url)}\n");

		return ProgramCli.WriteResult(true, "setup add-component", $"Added component '{name}' from '{url}'.", new Dictionary<string, object?>
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
