namespace Bassia.Core;

using Bassia.Split;
using Bassia.Storage;
using Bassia.Unwind;

/// <summary>A registered component and the meta-repo revision that registered or changed it (null when nothing new was recorded).</summary>
internal sealed record ComponentChange(ComponentDefinition Component, string Location, string? Revision);

/// <summary>A component <see cref="MonorepoGraph.RemoveAsync"/> unregistered, and whether its store was deleted.</summary>
internal sealed record RemovedComponent(string Name, string Location, bool Purged, string? Revision);

/// <summary>A tag <see cref="MonorepoGraph.TagAsync"/> created.</summary>
internal sealed record ComponentTagResult(string Component, string Tag, string Target, string Commit, string Message);

/// <summary>A fork <see cref="MonorepoGraph.ForkAsync"/> created: the new component, where it forked and the tags it took along.</summary>
internal sealed record ForkResult(ComponentDefinition Component, string Branch, string Commit, IReadOnlyList<string> Tags, string Location, string? Revision);

/// <summary>A component's history at a glance: whether it has a store, its default branch, branches and tags.</summary>
internal sealed record ComponentState(ComponentDefinition Definition, bool HasStore, string Location, string? DefaultBranch,
	IReadOnlyList<StoredBranch> Branches, IReadOnlyList<StoredTag> Tags);

/// <summary>
/// The monorepo as a graph of components (program units): the operations that create, change and connect components,
/// independent of how a component's history is stored. The meta-repo (<see cref="MetaRepo"/>) keeps the graph, the
/// <see cref="IComponentStorage"/> keeps each component's history; every operation checks what it is about to do against
/// both before it changes either, and records the graph change as one meta-repo revision.
/// </summary>
/// <remarks>
/// Splitting (<see cref="PlanSplitAsync"/>, <see cref="SplitAsync"/>), unwrapping nested units (<see cref="PlanUnwrapAsync"/>)
/// and agentic runs (<see cref="AgentRuns"/>) build on the same two dependencies.
/// </remarks>
internal sealed class MonorepoGraph(MetaRepo metaRepo, IComponentStorage storage)
{
	/// <summary>Run and integration tags belong to the parent's runs; a fork starts without them.</summary>
	private static readonly string[] ForeignTagPrefixes = [RunMetadata.RefPrefix, "integration/"];

	public MetaRepo MetaRepo { get; } = metaRepo;

	public IComponentStorage Storage { get; } = storage;

	public ComponentRegistry Registry => MetaRepo.Registry;

	/// <summary>The store of a registered component; throws when it is not registered or has no store.</summary>
	public IComponentStore Store(string name)
	{
		var component = Registry.Require(name);
		return Storage.Exists(component.Name)
			? Storage.Open(component.Name)
			: throw new MonorepoException($"Component '{name}' has no repository at '{Storage.LocationOf(name)}'.");
	}

	// ----- creating and importing -----

	/// <summary>
	/// Creates a new component - its history starts with one commit without files on <paramref name="defaultBranch"/>, so
	/// a run can select it right away - and registers it.
	/// </summary>
	public async Task<ComponentChange> CreateAsync(string name, IReadOnlyList<ComponentReference> references, string defaultBranch = "main", string url = "")
	{
		var definition = new ComponentDefinition(name, url, references);
		CheckNew(definition, "-name");
		var store = await Storage.CreateAsync(name, defaultBranch, url, $"Create component '{name}'");
		return await RegisterAsync(definition, store, $"Create component '{name}'");
	}

	/// <summary>Imports the repository at <paramref name="url"/> as the component <paramref name="name"/> and registers it.</summary>
	public async Task<ComponentChange> ImportAsync(string name, string url, IReadOnlyList<ComponentReference> references)
	{
		var definition = new ComponentDefinition(name, url, references);
		CheckNew(definition, "-name");
		var store = await Storage.ImportAsync(name, url);
		return await RegisterAsync(definition, store, $"Add component '{name}' from '{url}'");
	}

	/// <summary>
	/// Imports the repository at <paramref name="url"/> as <paramref name="name"/> and plans unwrapping its nested units
	/// (see <see cref="SubmoduleUnwinder"/>); <see cref="SubmoduleUnwinder.ApplyAsync"/> then registers it with them. When
	/// the plan fails the import is deleted again and nothing is registered.
	/// </summary>
	public async Task<SubmoduleUnwinder> ImportUnwrappedAsync(string name, string url, IReadOnlyList<ComponentReference> references)
	{
		CheckNew(new ComponentDefinition(name, url, references), "-name");
		var store = await Storage.ImportAsync(name, url);
		try
		{
			return await SubmoduleUnwinder.PlanAsync(this, new UnwindComponent { Name = name, Url = url, Store = store, IsNew = true, References = references.ToList() });
		}
		catch
		{
			await Storage.DeleteAsync(name);
			throw;
		}
	}

	/// <summary>Plans unwrapping the nested units of the registered component <paramref name="name"/>.</summary>
	public Task<SubmoduleUnwinder> PlanUnwrapAsync(string name)
	{
		var definition = Registry.Require(name);
		return SubmoduleUnwinder.PlanAsync(this, new UnwindComponent
		{
			Name = definition.Name,
			Url = definition.Url,
			Store = Store(name),
			IsNew = false,
			References = definition.References.ToList()
		});
	}

	private void CheckNew(ComponentDefinition definition, string nameSwitch)
	{
		ComponentRegistry.ValidateName(definition.Name);
		if (Registry.Contains(definition.Name))
		{
			throw new MonorepoException($"Component '{definition.Name}' is already registered.");
		}

		if (Storage.Exists(definition.Name))
		{
			throw new MonorepoException($"'{Storage.LocationOf(definition.Name)}' already exists; choose another name with {nameSwitch}.");
		}

		// The registry checks everything else about the definition; checking before the store exists keeps a failure clean.
		Registry.Add(definition);
	}

	private async Task<ComponentChange> RegisterAsync(ComponentDefinition definition, IComponentStore store, string message)
	{
		try
		{
			var revision = await MetaRepo.CommitAsync(new RegistryChange.Add(definition), message);
			return new ComponentChange(definition, store.Location, revision);
		}
		catch
		{
			await Storage.DeleteAsync(definition.Name);
			throw;
		}
	}

	// ----- changing the graph -----

	/// <summary>Unregisters a component nothing references; with <paramref name="purge"/> its store is deleted too.</summary>
	public async Task<RemovedComponent> RemoveAsync(string name, bool purge)
	{
		var component = Registry.Require(name);
		var revision = await MetaRepo.CommitAsync(new RegistryChange.Remove(component.Name), $"Remove component '{component.Name}'");
		var purged = false;
		if (purge && Storage.Exists(component.Name))
		{
			await Storage.DeleteAsync(component.Name);
			purged = true;
		}

		return new RemovedComponent(component.Name, Storage.LocationOf(component.Name), purged, revision);
	}

	/// <summary>Replaces what a component references (nests); the graph must stay acyclic.</summary>
	public async Task<ComponentChange> SetReferencesAsync(string name, IReadOnlyList<ComponentReference> references)
	{
		var component = Registry.Require(name);
		var described = references.Select(Describe).ToList();
		var revision = await MetaRepo.CommitAsync(new RegistryChange.SetReferences(component.Name, references),
			described.Count == 0 ? $"Component '{component.Name}' references nothing" : $"Component '{component.Name}' references {string.Join(", ", described)}");
		return new ComponentChange(Registry.Require(name), Storage.LocationOf(name), revision);
	}

	/// <summary><c>lib</c>, or <c>lib:vendor/lib</c> when it is nested at another path.</summary>
	public static string Describe(ComponentReference reference) =>
		reference.Path == reference.Name ? reference.Name : $"{reference.Name}:{reference.Path}";

	// ----- versions -----

	/// <summary>
	/// Creates the annotated tag <paramref name="tag"/> in a component at <paramref name="target"/> (default: the tip of its
	/// default branch) - a baseline an agentic run can select.
	/// </summary>
	public async Task<ComponentTagResult> TagAsync(string name, string tag, string? target = null, string? message = null)
	{
		var component = Registry.Require(name);
		var store = Store(component.Name);
		target ??= "HEAD";
		var commit = await Revisions.ResolveAsync(store, target)
			?? throw new MonorepoException($"'{target}' does not name a commit in component '{component.Name}'.");
		message ??= $"{tag}: baseline of '{component.Name}'";
		await store.CreateTagAsync(tag, commit, message);
		return new ComponentTagResult(component.Name, tag, target, commit, message);
	}

	/// <summary>
	/// Forks <paramref name="parent"/> as the new component <paramref name="name"/>: its default branch's history up to
	/// <paramref name="target"/> (default: the tip), with the tags on it except run and integration tags. The fork has the
	/// parent's references unless <paramref name="references"/> are given, and records where it came from.
	/// </summary>
	public async Task<ForkResult> ForkAsync(string parent, string name, string? target = null, IReadOnlyList<ComponentReference>? references = null, string url = "")
	{
		var source = Registry.Require(parent);
		ComponentRegistry.ValidateName(name);
		if (Registry.Contains(name))
		{
			throw new MonorepoException($"Component '{name}' is already registered.");
		}

		if (Storage.Exists(name))
		{
			throw new MonorepoException($"'{Storage.LocationOf(name)}' already exists; choose another name with -as.");
		}

		references ??= source.References;
		foreach (var reference in references.Where(reference => !Registry.Contains(reference.Name)))
		{
			throw new MonorepoException($"-references names '{reference.Name}', which is not registered.");
		}

		var store = Store(source.Name);
		var branch = await store.DefaultBranchAsync() ?? throw new MonorepoException($"Component '{source.Name}' has no main branch to fork.");
		target ??= branch;
		var commit = await Revisions.ResolveAsync(store, target)
			?? throw new MonorepoException($"'{target}' does not name a commit in component '{source.Name}'.");
		if (await store.BranchTipAsync(branch) is not { } tip || !await store.IsAncestorAsync(commit, tip))
		{
			throw new MonorepoException($"'{target}' ({commit[..10]}) is not on the main branch '{branch}' of component '{source.Name}'.");
		}

		var tags = new List<string>();
		foreach (var tag in await store.ListTagsAsync())
		{
			if (!ForeignTagPrefixes.Any(prefix => tag.Name.StartsWith(prefix, StringComparison.Ordinal)) && await store.IsAncestorAsync(tag.Commit, commit))
			{
				tags.Add(tag.Name);
			}
		}

		var definition = new ComponentDefinition(name, url, references, source.Name, commit);
		Registry.Add(definition);
		var fork = await Storage.ForkAsync(store, name, branch, commit, tags, url);
		var change = await RegisterAsync(definition, fork, $"Fork component '{source.Name}' as '{name}' at {commit[..10]}");
		return new ForkResult(definition, branch, commit, tags, fork.Location, change.Revision);
	}

	// ----- splitting -----

	/// <summary>Checks a split plan against the graph and the source's tip; throws <see cref="SplitPlanException"/> with every problem.</summary>
	public Task<SplitAllocation> PlanSplitAsync(SplitPlan plan, string? sourceName, List<string> problems) =>
		ComponentSplitter.AllocateAsync(this, plan, sourceName, problems);

	/// <summary>Performs a checked split (see <see cref="ComponentSplitter.SplitAsync"/>).</summary>
	public Task<SplitResult> SplitAsync(SplitAllocation allocation) => ComponentSplitter.SplitAsync(this, allocation);

	// ----- reading -----

	/// <summary>What a component's store holds; a component without a store (or an unreadable one) has none of it.</summary>
	public async Task<ComponentState> StateAsync(string name)
	{
		var component = Registry.Require(name);
		var location = Storage.LocationOf(component.Name);
		if (!Storage.Exists(component.Name))
		{
			return new ComponentState(component, false, location, null, [], []);
		}

		try
		{
			var store = Storage.Open(component.Name);
			return new ComponentState(component, true, location, await store.DefaultBranchAsync(), await store.ListBranchesAsync(), await store.ListTagsAsync());
		}
		catch (StorageException)
		{
			return new ComponentState(component, false, location, null, [], []);
		}
	}

	/// <summary>The agentic runs on this graph, recorded in <paramref name="records"/>.</summary>
	public AgentRuns AgentRuns(IRunRecordStore records, AgentRunOptions options) => new(this, records, options);
}
