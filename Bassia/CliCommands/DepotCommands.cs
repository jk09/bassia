namespace Bassia;

using Bassia.Cli;
using Bassia.Core;
using Bassia.Git;

/// <summary>
/// <c>bassia clone</c> and <c>bassia fetch</c>: a monorepo that is a cache of another one, its depot. The clone holds the
/// depot's meta-repo and each component's history without file content; views and agentic runs fetch the content they
/// load, and runs push their results back to the depot.
/// </summary>
internal static class DepotCommands
{
	// ----- bassia clone -----

	public static async Task<int> CloneAsync(Invocation invocation)
	{
		var from = invocation.Require("from");
		var url = Directory.Exists(from) ? Path.GetFullPath(from) : from;
		var depot = new Depot(url);
		var root = Path.GetFullPath(invocation.Get("path") ?? Path.Combine(Environment.CurrentDirectory, DefaultFolder(url)));
		var lazy = invocation.Has("lazy");
		if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
		{
			return ProgramCli.WriteResult(false, invocation.Command, $"'{root}' is not empty; clone into an empty or new folder (-path).");
		}

		var created = !Directory.Exists(root);
		try
		{
			Directory.CreateDirectory(root);
			var clone = await GitClient.In(root).RunAsync(["clone", "--quiet", depot.MetaRepoUrl, Monorepo.MetaRepoFolderName]);
			if (clone.ExitCode != 0)
			{
				throw new MonorepoException($"'{from}' is not a Bassia monorepo: its meta-repo '{depot.MetaRepoUrl}' could not be cloned ({clone.Error.Trim()}).");
			}

			Directory.CreateDirectory(Path.Combine(root, Monorepo.DefaultWorkspaceFolderName));
			ConfigFile.Set(root, ConfigFile.Find("depot.url"), url, ConfigLayer.User);
			if (lazy)
			{
				ConfigFile.Set(root, ConfigFile.Find("depot.lazy"), "true", ConfigLayer.User);
			}

			var monorepo = Monorepo.Load(root);
			var graph = monorepo.Graph();
			var imported = new List<string>();
			if (!lazy)
			{
				foreach (var component in monorepo.Components)
				{
					await depot.EnsureStoreAsync(graph.Storage, component.Name);
					imported.Add(component.Name);
				}
			}

			return ProgramCli.WriteResult(true, invocation.Command,
				$"Cloned '{url}' into '{root}': the meta-repo and {(lazy ? "no component yet (each is imported when a view maps it)" : $"the history of {imported.Count} component(s), without file content")}. " +
				"Map what you need with 'bassia view create -name <view> -spec <file>'.",
				new Dictionary<string, object?>
				{
					["path"] = root,
					["depot"] = url,
					["lazy"] = lazy,
					["components"] = monorepo.Components.Select(component => component.Name).ToList(),
					["imported"] = imported
				});
		}
		catch
		{
			if (created)
			{
				Storage.Git.GitComponentStorage.DeleteDirectory(root);
			}
			else
			{
				foreach (var entry in Directory.EnumerateFileSystemEntries(root).ToList())
				{
					if (Directory.Exists(entry))
					{
						Storage.Git.GitComponentStorage.DeleteDirectory(entry);
					}
					else
					{
						File.Delete(entry);
					}
				}
			}

			throw;
		}
	}

	/// <summary>The folder a clone goes to by default: the depot's last path segment, as <c>git clone</c> names one.</summary>
	private static string DefaultFolder(string url)
	{
		var name = url.TrimEnd('/', '\\').Split('/', '\\', ':').LastOrDefault(part => part.Length > 0) ?? "monorepo";
		return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
	}

	// ----- bassia fetch -----

	public static async Task<int> FetchAsync(Invocation invocation)
	{
		var root = Monorepo.FindRoot(Environment.CurrentDirectory)
			?? throw new MonorepoException($"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo.");
		var depot = Monorepo.Load(root).Depot
			?? throw new MonorepoException("This monorepo is no clone: depot.url is not set. 'bassia clone -from <depot>' makes one.");

		var before = (await GitClient.In(Path.Combine(root, Monorepo.MetaRepoFolderName)).RunOrThrowAsync(["rev-parse", "HEAD"])).Trim();
		var pull = await GitClient.In(Path.Combine(root, Monorepo.MetaRepoFolderName)).RunAsync(["pull", "--quiet", "--ff-only"]);
		if (pull.ExitCode != 0)
		{
			throw new MonorepoException($"The meta-repo could not be brought up to the depot's: {pull.Error.Trim()}");
		}

		var after = (await GitClient.In(Path.Combine(root, Monorepo.MetaRepoFolderName)).RunOrThrowAsync(["rev-parse", "HEAD"])).Trim();
		var monorepo = Monorepo.Load(root);
		var graph = monorepo.Graph();
		var lazy = monorepo.Config.Value("depot.lazy") == "true";
		var fetched = new List<string>();
		var imported = new List<string>();
		foreach (var component in monorepo.Components)
		{
			if (graph.Storage.Exists(component.Name))
			{
				await graph.Storage.Open(component.Name).FetchUpstreamAsync();
				fetched.Add(component.Name);
			}
			else if (!lazy)
			{
				await depot.EnsureStoreAsync(graph.Storage, component.Name);
				imported.Add(component.Name);
			}
		}

		return ProgramCli.WriteResult(true, invocation.Command,
			$"Fetched from '{depot.Url}': the meta-repo {(before == after ? "was up to date" : $"moved {before[..7]}..{after[..7]}")}, {fetched.Count} component(s) updated, {imported.Count} imported.",
			new Dictionary<string, object?>
			{
				["depot"] = depot.Url,
				["meta_repo_from"] = before,
				["meta_repo_to"] = after,
				["fetched"] = fetched,
				["imported"] = imported
			});
	}
}
