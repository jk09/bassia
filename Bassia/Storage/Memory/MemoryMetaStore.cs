namespace Bassia.Storage.Memory;

/// <summary>The reference <see cref="IMetaStore"/>: the registry and its revisions in memory.</summary>
internal sealed class MemoryMetaStore : IMetaStore
{
	private readonly List<ComponentDefinition> components = [];

	/// <summary>Every revision committed, oldest first: its id, message and the changes it applied.</summary>
	public List<(string Id, string Message, IReadOnlyList<RegistryChange> Changes)> Revisions { get; } = [];

	public string Location => "memory:meta-repo";

	public Task<IReadOnlyList<ComponentDefinition>> LoadAsync() => Task.FromResult<IReadOnlyList<ComponentDefinition>>(components.ToList());

	public Task<string?> CommitAsync(IReadOnlyList<RegistryChange> changes, string message)
	{
		if (changes.Count == 0)
		{
			return Task.FromResult<string?>(null);
		}

		foreach (var change in changes)
		{
			switch (change)
			{
				case RegistryChange.Add add:
					components.Add(add.Component);
					break;
				case RegistryChange.Remove remove:
					components.RemoveAll(component => component.Name == remove.Name);
					break;
				case RegistryChange.SetReferences set:
					var index = components.FindIndex(component => component.Name == set.Name);
					components[index] = components[index] with { References = set.References };
					break;
			}
		}

		var id = $"r{Revisions.Count + 1}";
		Revisions.Add((id, message, changes));
		return Task.FromResult<string?>(id);
	}
}

/// <summary>The reference <see cref="IRunRecordStore"/>: every version of every record (runs, integrations) in memory.</summary>
internal sealed class MemoryRunRecordStore : IRunRecordStore
{
	private readonly Dictionary<string, List<string>> versions = new(StringComparer.Ordinal);

	public string Location => "memory:agentic-runs";

	public Task EnsureRepositoryAsync() => Task.CompletedTask;

	// ----- records -----

	public Task<int> LatestIndexAsync(string refBase)
	{
		lock (versions)
		{
			return Task.FromResult(versions.TryGetValue(refBase, out var list) ? list.Count - 1 : -1);
		}
	}

	public Task<IReadOnlyList<string>> ListKeysAsync(string refPrefix)
	{
		lock (versions)
		{
			return Task.FromResult<IReadOnlyList<string>>(versions.Keys.Where(key => key.StartsWith(refPrefix, StringComparison.Ordinal))
				.Select(key => key[refPrefix.Length..]).ToList());
		}
	}

	public Task<(string Content, int Lineage)?> ReadLatestAsync(string refBase, string fileName)
	{
		lock (versions)
		{
			return Task.FromResult<(string, int)?>(versions.TryGetValue(refBase, out var list) ? (list[^1], list.Count - 1) : null);
		}
	}

	public Task<string> WriteAsync(string refBase, int lineage, string fileName, string content, string message)
	{
		lock (versions)
		{
			if (!versions.TryGetValue(refBase, out var list))
			{
				versions[refBase] = list = [];
			}

			if (lineage != list.Count)
			{
				throw new StorageException($"Version {lineage} of '{refBase}' cannot follow version {list.Count - 1}.");
			}

			list.Add(content);
			return Task.FromResult($"{refBase}/{lineage}");
		}
	}

	/// <summary>Every version of a record's file, oldest first.</summary>
	public IReadOnlyList<string> Versions(string refBase)
	{
		lock (versions)
		{
			return versions.TryGetValue(refBase, out var list) ? list.ToList() : [];
		}
	}

	// ----- run records -----

	public async Task<IReadOnlyList<RunMetadata>> ListLatestAsync()
	{
		var runs = new List<RunMetadata>();
		foreach (var key in await ListKeysAsync(RunMetadata.RefPrefix))
		{
			runs.Add((await LoadLatestAsync(RunMetadata.NormalizeRunId(key)))!);
		}

		return runs.OrderByDescending(run => run.Created, StringComparer.Ordinal).ToList();
	}

	public async Task<RunMetadata?> LoadLatestAsync(string runId)
	{
		if (await ReadLatestAsync(RunMetadata.RefBase(runId), RunMetadata.FileName) is not { } found)
		{
			return null;
		}

		var metadata = RunMetadata.FromToml(found.Content);
		metadata.Lineage = found.Lineage;
		return metadata;
	}

	/// <summary>Every version of a run's record, oldest first.</summary>
	public IReadOnlyList<RunMetadata> History(string runId) => Versions(RunMetadata.RefBase(runId)).Select(RunMetadata.FromToml).ToList();

	public async Task<string> CommitAsync(RunMetadata metadata)
	{
		metadata.Lineage = await LatestIndexAsync(RunMetadata.RefBase(metadata.RunId)) + 1;
		return await WriteAsync(RunMetadata.RefBase(metadata.RunId), metadata.Lineage, RunMetadata.FileName, metadata.ToToml(),
			$"{metadata.RunId} #{metadata.Lineage}: {metadata.Status}");
	}
}
