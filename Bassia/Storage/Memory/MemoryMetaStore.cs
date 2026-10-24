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

/// <summary>The reference <see cref="IRunRecordStore"/>: every version of every run record in memory.</summary>
internal sealed class MemoryRunRecordStore : IRunRecordStore
{
	private readonly Dictionary<string, List<string>> versions = new(StringComparer.Ordinal);

	public string Location => "memory:agentic-runs";

	public Task EnsureRepositoryAsync() => Task.CompletedTask;

	public async Task<IReadOnlyList<RunMetadata>> ListLatestAsync()
	{
		var runs = new List<RunMetadata>();
		foreach (var runId in versions.Keys.ToList())
		{
			runs.Add((await LoadLatestAsync(runId))!);
		}

		return runs.OrderByDescending(run => run.Created, StringComparer.Ordinal).ToList();
	}

	public Task<RunMetadata?> LoadLatestAsync(string runId)
	{
		if (!versions.TryGetValue(runId, out var list))
		{
			return Task.FromResult<RunMetadata?>(null);
		}

		var metadata = RunMetadata.FromToml(list[^1]);
		metadata.Lineage = list.Count - 1;
		return Task.FromResult<RunMetadata?>(metadata);
	}

	/// <summary>Every version of a run's record, oldest first.</summary>
	public IReadOnlyList<RunMetadata> History(string runId) =>
		versions.TryGetValue(runId, out var list) ? list.Select(RunMetadata.FromToml).ToList() : [];

	public Task<string> CommitAsync(RunMetadata metadata)
	{
		if (!versions.TryGetValue(metadata.RunId, out var list))
		{
			versions[metadata.RunId] = list = [];
		}

		metadata.Lineage = list.Count;
		list.Add(metadata.ToToml());
		return Task.FromResult(RunMetadata.TagName(metadata.RunId, metadata.Lineage));
	}
}
