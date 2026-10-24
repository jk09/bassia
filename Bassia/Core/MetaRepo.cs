namespace Bassia.Core;

using Bassia.Storage;

/// <summary>
/// The meta-repo as a unit of its own: it maintains the component graph (<see cref="ComponentRegistry"/>) and nothing
/// else, whatever keeps the components' histories. Every change is checked against the graph first and then persisted
/// through the <see cref="IMetaStore"/> as one revision, so the stored registry never holds a graph the meta-repo would
/// refuse.
/// </summary>
internal sealed class MetaRepo
{
	private readonly IMetaStore store;

	private MetaRepo(IMetaStore store, ComponentRegistry registry)
	{
		this.store = store;
		Registry = registry;
	}

	/// <summary>The component graph as of the latest revision.</summary>
	public ComponentRegistry Registry { get; private set; }

	public string Location => store.Location;

	public static async Task<MetaRepo> OpenAsync(IMetaStore store) =>
		new(store, ComponentRegistry.FromDefinitions(await store.LoadAsync()));

	/// <summary>A meta-repo whose registry was already read from <paramref name="store"/>.</summary>
	public static MetaRepo Open(IMetaStore store, ComponentRegistry registry) => new(store, registry);

	/// <summary>
	/// Checks <paramref name="changes"/> against the graph and commits them as one revision; returns the revision id
	/// (null when the store recorded nothing new). Nothing is persisted when a change does not fit.
	/// </summary>
	public async Task<string?> CommitAsync(IReadOnlyList<RegistryChange> changes, string message)
	{
		var next = Registry.Apply(changes);
		var revision = await store.CommitAsync(changes, message);
		Registry = next;
		return revision;
	}

	public Task<string?> CommitAsync(RegistryChange change, string message) => CommitAsync([change], message);
}
