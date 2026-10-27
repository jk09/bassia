namespace Bassia.Storage;

/// <summary>
/// One change to the component registry. The meta-repo persists changes rather than whole registries, so a store can
/// apply them its own way: the git store edits <c>components.toml</c> line by line, a database store in a transaction.
/// </summary>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "change")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Add), "add")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Remove), "remove")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SetReferences), "set-references")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SetTags), "set-tags")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SetVisibility), "set-visibility")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SetOwners), "set-owners")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SetExports), "set-exports")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(AddBoundary), "add-boundary")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(RemoveBoundary), "remove-boundary")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(AddConformance), "add-conformance")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(RemoveConformance), "remove-conformance")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SetUser), "set-user")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(RemoveUser), "remove-user")]
internal abstract record RegistryChange
{
	/// <summary>Registers <paramref name="Component"/>.</summary>
	public sealed record Add(ComponentDefinition Component) : RegistryChange;

	/// <summary>Unregisters the component named <paramref name="Name"/>.</summary>
	public sealed record Remove(string Name) : RegistryChange;

	/// <summary>Replaces the references of the component named <paramref name="Name"/>.</summary>
	public sealed record SetReferences(string Name, IReadOnlyList<ComponentReference> References) : RegistryChange;

	/// <summary>Replaces the tags of the component named <paramref name="Name"/>.</summary>
	public sealed record SetTags(string Name, IReadOnlyList<string> Tags) : RegistryChange;

	/// <summary>Replaces the visibility of the component named <paramref name="Name"/>; null makes it public.</summary>
	public sealed record SetVisibility(string Name, IReadOnlyList<string>? Visibility) : RegistryChange;

	/// <summary>Replaces the owners of the component named <paramref name="Name"/>.</summary>
	public sealed record SetOwners(string Name, IReadOnlyList<string> Owners) : RegistryChange;

	/// <summary>Replaces the exports of the component named <paramref name="Name"/>; null exports everything.</summary>
	public sealed record SetExports(string Name, IReadOnlyList<string>? Exports) : RegistryChange;

	/// <summary>Appends a boundary rule.</summary>
	public sealed record AddBoundary(Core.BoundaryRule Rule) : RegistryChange;

	/// <summary>Removes the boundary rule at <paramref name="Index"/> (0-based, in file order).</summary>
	public sealed record RemoveBoundary(int Index) : RegistryChange;

	/// <summary>Appends a conformance rule.</summary>
	public sealed record AddConformance(Core.ConformanceRule Rule) : RegistryChange;

	/// <summary>Removes the conformance rule at <paramref name="Index"/> (0-based, in file order).</summary>
	public sealed record RemoveConformance(int Index) : RegistryChange;

	/// <summary>Registers a user, or replaces the user with the same id.</summary>
	public sealed record SetUser(Core.UserDefinition User) : RegistryChange;

	/// <summary>Removes the user with this id from the user registry.</summary>
	public sealed record RemoveUser(string Id) : RegistryChange;

	/// <summary>Whether the change is to the rules or the users rather than to the graph.</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public bool IsRuleChange => this is AddBoundary or RemoveBoundary or AddConformance or RemoveConformance or SetUser or RemoveUser;
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

	/// <summary>The boundary rules as of the latest revision, in order.</summary>
	Task<IReadOnlyList<Core.BoundaryRule>> LoadBoundariesAsync();

	/// <summary>The conformance rules as of the latest revision, in order.</summary>
	Task<IReadOnlyList<Core.ConformanceRule>> LoadConformanceAsync();

	/// <summary>The registered users as of the latest revision, in registration order.</summary>
	Task<IReadOnlyList<Core.UserDefinition>> LoadUsersAsync();

	/// <summary>The meta-repo's revisions, newest first, as people read history (see <see cref="IComponentStore.LogAsync"/>).</summary>
	Task<IReadOnlyList<LogEntry>> LogAsync(LogQuery query);

	/// <summary>
	/// Applies <paramref name="changes"/> (already validated) as one revision described by <paramref name="message"/>
	/// and returns its id, or null when the store records nothing new. Either every change is applied or none.
	/// </summary>
	Task<string?> CommitAsync(IReadOnlyList<RegistryChange> changes, string message);
}

/// <summary>
/// Where versioned records are kept (agentic runs, integrations): a record is a key (<c>&lt;prefix&gt;&lt;key&gt;</c>, its
/// <c>refBase</c>) with a history of versions numbered from 0 (its lineage), each a named text file.
/// </summary>
internal interface IRecordStore
{
	/// <summary>Where the records live, as the backend names it.</summary>
	string Location { get; }

	/// <summary>Creates the store on first use; safe to call from parallel runs.</summary>
	Task EnsureRepositoryAsync();

	/// <summary>The highest version of the record <paramref name="refBase"/>, or -1 when it has none.</summary>
	Task<int> LatestIndexAsync(string refBase);

	/// <summary>The distinct keys of the records whose refBase starts with <paramref name="refPrefix"/>.</summary>
	Task<IReadOnlyList<string>> ListKeysAsync(string refPrefix);

	/// <summary>The file of the record's latest version, with that version; null when the record is unknown.</summary>
	Task<(string Content, int Lineage)?> ReadLatestAsync(string refBase, string fileName);

	/// <summary>Writes version <paramref name="lineage"/> of the record; returns the version's name (its tag).</summary>
	Task<string> WriteAsync(string refBase, int lineage, string fileName, string content, string message);
}

/// <summary>Where agentic run records are kept: each record version is appended to the run's history.</summary>
internal interface IRunRecordStore : IRecordStore
{
	/// <summary>Every recorded run at its latest version, newest first.</summary>
	Task<IReadOnlyList<RunMetadata>> ListLatestAsync();

	/// <summary>The latest version of a run's record, or null when the run is unknown.</summary>
	Task<RunMetadata?> LoadLatestAsync(string runId);

	/// <summary>Appends <paramref name="metadata"/> as the run's next version (setting its lineage); returns the version's tag.</summary>
	Task<string> CommitAsync(RunMetadata metadata);
}
