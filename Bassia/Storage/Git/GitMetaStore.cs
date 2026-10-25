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

	public Task<IReadOnlyList<Core.BoundaryRule>> LoadBoundariesAsync() => Task.FromResult(ReadBoundaries(Root));

	/// <summary>The components registered in <c>components.toml</c> under <paramref name="root"/>, as written.</summary>
	public static IReadOnlyList<ComponentDefinition> Read(string root) => ReadComponents(ReadToml(ComponentsFile.PathOf(root)));

	/// <summary>The boundary rules (<c>[[boundary]]</c>) of <c>components.toml</c> under <paramref name="root"/>, as written.</summary>
	public static IReadOnlyList<Core.BoundaryRule> ReadBoundaries(string root) => ReadBoundaries(ReadToml(ComponentsFile.PathOf(root)));

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
						ComponentsFile.Add(Root, add.Component);
						break;
					case RegistryChange.Remove remove:
						ComponentsFile.Remove(Root, remove.Name);
						break;
					case RegistryChange.SetReferences set:
						ComponentsFile.SetReferences(Root, set.Name, set.References);
						break;
					case RegistryChange.SetTags set:
						ComponentsFile.SetList(Root, set.Name, "tags", set.Tags.Count == 0 ? null : set.Tags);
						break;
					case RegistryChange.SetVisibility set:
						ComponentsFile.SetList(Root, set.Name, "visibility", set.Visibility);
						break;
					case RegistryChange.SetOwners set:
						ComponentsFile.SetList(Root, set.Name, "owners", set.Owners.Count == 0 ? null : set.Owners);
						break;
					case RegistryChange.SetExports set:
						ComponentsFile.SetList(Root, set.Name, "exports", set.Exports);
						break;
					case RegistryChange.AddBoundary add:
						ComponentsFile.AddBoundary(Root, add.Rule);
						break;
					case RegistryChange.RemoveBoundary remove:
						ComponentsFile.RemoveBoundary(Root, remove.Index);
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
			components.Add(new ComponentDefinition(name, url, ReadReferences(name, componentTable), forkOf, forkCommit)
			{
				Tags = Strings(componentTable, "tags", $"Component '{name}'") ?? [],
				Visibility = Strings(componentTable, "visibility", $"Component '{name}'"),
				Owners = Strings(componentTable, "owners", $"Component '{name}'") ?? [],
				Exports = Strings(componentTable, "exports", $"Component '{name}'")
			});
		}

		return components;
	}

	// [[boundary]] source = "type:app", allow = ["type:lib"], deny = [...], description = "..."
	private static List<Core.BoundaryRule> ReadBoundaries(TomlTable table)
	{
		var rules = new List<Core.BoundaryRule>();
		if (!table.TryGetValue("boundary", out var entries))
		{
			return rules;
		}

		if (entries is not TomlTableArray tables)
		{
			throw new MonorepoException("components.toml: 'boundary' must be an array of tables ([[boundary]]).");
		}

		foreach (var (rule, index) in tables.Select((rule, index) => (rule, index)))
		{
			var where = $"components.toml [[boundary]] #{index + 1}";
			foreach (var key in rule.Keys.Where(key => key is not ("source" or "allow" or "deny" or "description")))
			{
				throw new MonorepoException($"{where}: unknown key '{key}'; expected source, allow, deny and description.");
			}

			var source = rule.TryGetValue("source", out var sourceValue) ? sourceValue as string ?? throw new MonorepoException($"{where}: 'source' must be a string.") : "";
			var description = rule.TryGetValue("description", out var descriptionValue) ? descriptionValue as string : null;
			rules.Add(new Core.BoundaryRule(source, Strings(rule, "allow", where) ?? [], Strings(rule, "deny", where) ?? [], description));
		}

		return rules;
	}

	/// <summary>An array of strings under <paramref name="key"/>, or null when the key is absent.</summary>
	private static List<string>? Strings(TomlTable table, string key, string where)
	{
		if (!table.TryGetValue(key, out var value) || value is null)
		{
			return null;
		}

		if (value is not TomlArray array || array.Any(item => item is not string))
		{
			throw new MonorepoException($"{where}: '{key}' must be an array of strings.");
		}

		return array.Cast<string>().ToList();
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
