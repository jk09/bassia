namespace Bassia.Storage.Git;

using Bassia.Git;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// <see cref="IMetaStore"/> on the <c>.bassia</c> git repository: the registry is <c>components.toml</c>, changed line by
/// line (<see cref="ComponentsFile"/>) so hand-written comments and layout survive, and every revision is a commit of the
/// meta-repo.
/// </summary>
internal sealed class GitMetaStore(string root) : IMetaStore
{
	public string Root { get; } = root;

	public string Location => Path.Combine(Root, Monorepo.MetaRepoFolderName);

	public Task<IReadOnlyList<ComponentDefinition>> LoadAsync() => Task.FromResult(Read(Root));

	/// <summary>The components registered in <c>components.toml</c> under <paramref name="root"/>, as written.</summary>
	public static IReadOnlyList<ComponentDefinition> Read(string root) => ReadComponents(ReadToml(ComponentsFile.PathOf(root)));

	public async Task<string?> CommitAsync(IReadOnlyList<RegistryChange> changes, string message)
	{
		var path = ComponentsFile.PathOf(Root);
		var original = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
		try
		{
			foreach (var change in changes)
			{
				switch (change)
				{
					case RegistryChange.Add add:
						ComponentsFile.Add(Root, add.Component.Name, add.Component.Url, add.Component.References, add.Component.ForkOf, add.Component.ForkCommit);
						break;
					case RegistryChange.Remove remove:
						ComponentsFile.Remove(Root, remove.Name);
						break;
					case RegistryChange.SetReferences set:
						ComponentsFile.SetReferences(Root, set.Name, set.References);
						break;
				}
			}

			return await CommitAllAsync(Root, message);
		}
		catch
		{
			if (original is not null && await File.ReadAllTextAsync(path) != original)
			{
				await File.WriteAllTextAsync(path, original);
			}

			throw;
		}
	}

	/// <summary>Commits every change in the meta-repo and returns the new commit, or null when nothing changed.</summary>
	public static async Task<string?> CommitAllAsync(string root, string message)
	{
		var metaRepo = GitClient.In(Path.Combine(root, Monorepo.MetaRepoFolderName));
		var status = await metaRepo.RunOrThrowAsync(["status", "--porcelain"]);
		if (status.Length == 0)
		{
			return null;
		}

		var result = await metaRepo.CommitAllAsync(message);
		if (result.ExitCode != 0)
		{
			throw new GitException($"git commit failed in the meta-repo: {result.Error.Trim()}");
		}

		return await metaRepo.RunOrThrowAsync(["rev-parse", "HEAD"]);
	}

	private static TomlTable ReadToml(string path)
	{
		if (!File.Exists(path))
		{
			return new TomlTable();
		}

		try
		{
			return TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? new TomlTable();
		}
		catch (TomlException ex)
		{
			throw new MonorepoException($"Could not parse '{path}': {ex.Message}");
		}
	}

	private static List<ComponentDefinition> ReadComponents(TomlTable table)
	{
		var components = new List<ComponentDefinition>();
		if (!table.TryGetValue("component", out var entries) || entries is not TomlTableArray componentTables)
		{
			return components;
		}

		foreach (var componentTable in componentTables)
		{
			var name = componentTable.TryGetValue("name", out var nameValue) ? nameValue as string : null;
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new MonorepoException("components.toml contains a [[component]] entry without a name.");
			}

			if (components.Any(component => component.Name == name))
			{
				throw new MonorepoException($"components.toml registers the component '{name}' more than once.");
			}

			var url = componentTable.TryGetValue("url", out var urlValue) ? urlValue as string ?? "" : "";
			var forkOf = componentTable.TryGetValue("fork_of", out var forkOfValue) ? forkOfValue as string : null;
			var forkCommit = componentTable.TryGetValue("fork_commit", out var forkCommitValue) ? forkCommitValue as string : null;
			components.Add(new ComponentDefinition(name, url, ReadReferences(name, componentTable), forkOf, forkCommit));
		}

		return components;
	}

	// references = ["lib", { name = "other", path = "libs/other" }]
	private static List<ComponentReference> ReadReferences(string owner, TomlTable componentTable)
	{
		var references = new List<ComponentReference>();
		if (!componentTable.TryGetValue("references", out var value) || value is null)
		{
			return references;
		}

		if (value is not TomlArray array)
		{
			throw new MonorepoException($"Component '{owner}': 'references' must be an array.");
		}

		foreach (var item in array)
		{
			switch (item)
			{
				case string name when !string.IsNullOrWhiteSpace(name):
					references.Add(new ComponentReference(name, name));
					break;
				case TomlTable table when table.TryGetValue("name", out var nameValue) && nameValue is string name && !string.IsNullOrWhiteSpace(name):
					var path = table.TryGetValue("path", out var pathValue) && pathValue is string customPath && !string.IsNullOrWhiteSpace(customPath)
						? customPath
						: name;
					if (Path.IsPathRooted(path) || path.Split(['/', '\\']).Any(segment => segment is "" or "." or ".."))
					{
						throw new MonorepoException($"Component '{owner}': reference path '{path}' must be a relative path inside the component.");
					}

					references.Add(new ComponentReference(name, path));
					break;
				default:
					throw new MonorepoException($"Component '{owner}': each reference must be a component name or {{ name = \"...\", path = \"...\" }}.");
			}

			if (references[^1].Name == owner)
			{
				throw new MonorepoException($"Component '{owner}' cannot reference itself.");
			}
		}

		return references;
	}
}
