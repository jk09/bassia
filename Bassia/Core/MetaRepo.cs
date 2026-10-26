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
		new(store, ComponentRegistry.FromDefinitions(await store.LoadAsync(), await store.LoadBoundariesAsync(), await store.LoadConformanceAsync(),
			await store.LoadUsersAsync()));

	/// <summary>A meta-repo whose registry was already read from <paramref name="store"/>.</summary>
	public static MetaRepo Open(IMetaStore store, ComponentRegistry registry) => new(store, registry);

	/// <summary>
	/// The registry <paramref name="changes"/> would make; throws when a change does not fit the graph, or when a change to
	/// the graph adds a boundary violation that was not there before (<see cref="BoundaryViolationException"/>), unless
	/// <paramref name="allowNewViolations"/>. Violations already in the graph are kept, and so are those a change to the
	/// rules themselves reveals: a rule can be introduced into a monorepo that does not follow it yet.
	/// </summary>
	public ComponentRegistry Check(IReadOnlyList<RegistryChange> changes, bool allowNewViolations = false)
	{
		var next = Registry.Apply(changes);
		if (!allowNewViolations && changes.Any(change => !change.IsRuleChange) && Boundaries.Added(Registry, next) is { Count: > 0 } added)
		{
			throw new BoundaryViolationException(added);
		}

		return next;
	}

	/// <summary>
	/// Checks <paramref name="changes"/> (see <see cref="Check"/>) and commits them as one revision; returns the revision id
	/// (null when the store recorded nothing new). Nothing is persisted when a change does not fit.
	/// </summary>
	public async Task<string?> CommitAsync(IReadOnlyList<RegistryChange> changes, string message, bool allowNewViolations = false)
	{
		var next = Check(changes, allowNewViolations);
		var revision = await store.CommitAsync(changes, message);
		Registry = next;
		return revision;
	}

	public Task<string?> CommitAsync(RegistryChange change, string message) => CommitAsync([change], message);
}
