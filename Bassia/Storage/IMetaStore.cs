namespace Bassia.Storage;

/// <summary>
/// One change to the component registry. The meta-repo persists changes rather than whole registries, so a store can
/// apply them its own way: the git store edits <c>components.toml</c> line by line, a database store in a transaction.
/// </summary>
internal abstract record RegistryChange
{
	/// <summary>Registers <paramref name="Component"/>.</summary>
	public sealed record Add(ComponentDefinition Component) : RegistryChange;

	/// <summary>Unregisters the component named <paramref name="Name"/>.</summary>
	public sealed record Remove(string Name) : RegistryChange;

	/// <summary>Replaces the references of the component named <paramref name="Name"/>.</summary>
	public sealed record SetReferences(string Name, IReadOnlyList<ComponentReference> References) : RegistryChange;
}

/// <summary>
/// Where the meta-repo keeps the component registry, with history: each commit of changes is one revision. The git
/// store is the <c>.bassia</c> repository and its <c>components.toml</c>.
/// </summary>
internal interface IMetaStore
{
	/// <summary>Where the meta-repo lives, as the backend names it.</summary>
	string Location { get; }

	/// <summary>The registered components as of the latest revision, in registration order.</summary>
	Task<IReadOnlyList<ComponentDefinition>> LoadAsync();

	/// <summary>
	/// Applies <paramref name="changes"/> (already validated) as one revision described by <paramref name="message"/>
	/// and returns its id, or null when the store records nothing new. Either every change is applied or none.
	/// </summary>
	Task<string?> CommitAsync(IReadOnlyList<RegistryChange> changes, string message);
}

/// <summary>Where agentic run records are kept: each record version is appended to the run's history.</summary>
internal interface IRunRecordStore
{
	/// <summary>Where the records live, as the backend names it.</summary>
	string Location { get; }

	/// <summary>Creates the store on first use; safe to call from parallel runs.</summary>
	Task EnsureRepositoryAsync();

	/// <summary>Every recorded run at its latest version, newest first.</summary>
	Task<IReadOnlyList<RunMetadata>> ListLatestAsync();

	/// <summary>The latest version of a run's record, or null when the run is unknown.</summary>
	Task<RunMetadata?> LoadLatestAsync(string runId);

	/// <summary>Appends <paramref name="metadata"/> as the run's next version (setting its lineage); returns the version's tag.</summary>
	Task<string> CommitAsync(RunMetadata metadata);
}
