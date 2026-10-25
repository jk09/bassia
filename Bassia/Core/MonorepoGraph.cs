namespace Bassia.Core;

using Bassia.Split;
using Bassia.Storage;
using Bassia.Unwind;

/// <summary>A registered component and the meta-repo revision that registered or changed it (null when nothing new was recorded).</summary>
internal sealed record ComponentChange(ComponentDefinition Component, string Location, string? Revision);

/// <summary>
/// Where a component sits in the architecture, given when it is created, imported or forked: its tags, visibility (null:
/// public) and owners.
/// </summary>
internal sealed record ComponentAttributes(IReadOnlyList<string> Tags, IReadOnlyList<string>? Visibility, IReadOnlyList<string> Owners,
	IReadOnlyList<string>? Exports = null)
{
	public static ComponentAttributes None { get; } = new([], null, []);

	public static ComponentAttributes Of(ComponentDefinition component) => new(component.Tags, component.Visibility, component.Owners, component.Exports);

	public ComponentDefinition ApplyTo(ComponentDefinition component) =>
		component with { Tags = Tags, Visibility = Visibility, Owners = Owners, Exports = Exports };
}

/// <summary>A boundary rule change: the rules after it and the revision that recorded it.</summary>
internal sealed record BoundaryChange(IReadOnlyList<BoundaryRule> Rules, string? Revision);

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
	/// <remarks>
	/// With <paramref name="files"/> (path -> content), a second commit described by <paramref name="filesMessage"/> adds
	/// them on top - a component started from a template.
	/// </remarks>
	public async Task<ComponentChange> CreateAsync(string name, IReadOnlyList<ComponentReference> references, string defaultBranch = "main", string url = "",
		ComponentAttributes? attributes = null, IReadOnlyDictionary<string, byte[]>? files = null, string? filesMessage = null)
	{
		var definition = (attributes ?? ComponentAttributes.None).ApplyTo(new ComponentDefinition(name, url, references));
		CheckNew(definition, "-name");
		var store = await Storage.CreateAsync(name, defaultBranch, url, $"Create component '{name}'");
		if (files is { Count: > 0 })
		{
			try
			{
				await AddFilesAsync(store, defaultBranch, files, filesMessage ?? $"Add the first files of '{name}'");
			}
			catch
			{
				await Storage.DeleteAsync(name);
				throw;
			}
		}

		return await RegisterAsync(definition, store, $"Create component '{name}'");
	}

	/// <summary>Commits <paramref name="files"/> on top of <paramref name="branch"/> through a working copy, and publishes the branch.</summary>
	private static async Task AddFilesAsync(IComponentStore store, string branch, IReadOnlyDictionary<string, byte[]> files, string message)
	{
		var tip = await store.BranchTipAsync(branch) ?? throw new MonorepoException($"Component '{store.Name}' has no branch '{branch}'.");
		var folder = Path.Combine(Path.GetTempPath(), $"bassia-files-{Guid.NewGuid():N}");
		try
		{
			var workspace = await store.CheckoutAsync(tip, folder, branch);
			foreach (var (path, content) in files)
			{
				var target = Path.GetFullPath(Path.Combine(folder, path));
				if (!target.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				{
					throw new MonorepoException($"'{path}' is not a path inside the component.");
				}

				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				await File.WriteAllBytesAsync(target, content);
			}

			if (await workspace.CommitAsync(message) is not null)
			{
				await workspace.PublishAsync(branch, []);
			}
		}
		finally
		{
			Workspaces.Delete(folder);
		}
	}

	/// <summary>Imports the repository at <paramref name="url"/> as the component <paramref name="name"/> and registers it.</summary>
	public async Task<ComponentChange> ImportAsync(string name, string url, IReadOnlyList<ComponentReference> references, ComponentAttributes? attributes = null)
	{
		var definition = (attributes ?? ComponentAttributes.None).ApplyTo(new ComponentDefinition(name, url, references));
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
		CheckNew(new ComponentDefinition(name, url, references), "-name", allowNewViolations: true);
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

	private void CheckNew(ComponentDefinition definition, string nameSwitch, bool allowNewViolations = false)
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

		// The registry checks everything else about the definition, the boundaries included; checking before the store
		// exists keeps a failure clean.
		MetaRepo.Check([new RegistryChange.Add(definition)], allowNewViolations);
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

	/// <summary>
	/// Changes a component's references, tags, visibility and owners - each only when given (<paramref name="visibility"/>
	/// only with <paramref name="setVisibility"/>, null making it public) - in one revision; refused as a whole when the
	/// result does not fit the graph or adds a boundary violation.
	/// </summary>
	public async Task<ComponentChange> SetAttributesAsync(string name, IReadOnlyList<string>? tags = null, bool setVisibility = false,
		IReadOnlyList<string>? visibility = null, IReadOnlyList<string>? owners = null, IReadOnlyList<ComponentReference>? references = null,
		bool setExports = false, IReadOnlyList<string>? exports = null)
	{
		var component = Registry.Require(name);
		var changes = new List<RegistryChange>();
		var said = new List<string>();
		if (references is not null)
		{
			changes.Add(new RegistryChange.SetReferences(component.Name, references));
			said.Add(references.Count == 0 ? "references nothing" : $"references {string.Join(", ", references.Select(Describe))}");
		}

		if (tags is not null)
		{
			changes.Add(new RegistryChange.SetTags(component.Name, tags));
			said.Add(tags.Count == 0 ? "no tags" : $"tags {string.Join(", ", tags)}");
		}

		if (setVisibility)
		{
			changes.Add(new RegistryChange.SetVisibility(component.Name, visibility));
			said.Add(visibility is null ? "public" : visibility.Count == 0 ? "private" : $"visible to {string.Join(", ", visibility)}");
		}

		if (owners is not null)
		{
			changes.Add(new RegistryChange.SetOwners(component.Name, owners));
			said.Add(owners.Count == 0 ? "no owners" : $"owned by {string.Join(", ", owners)}");
		}

		if (setExports)
		{
			changes.Add(new RegistryChange.SetExports(component.Name, exports));
			said.Add(exports is null ? "exports everything" : $"exports {string.Join(", ", exports)}");
		}

		var revision = changes.Count == 0 ? null
			: await MetaRepo.CommitAsync(changes, said.Count == 1 && references is not null ? $"Component '{component.Name}' {said[0]}" : $"Component '{component.Name}': {string.Join("; ", said)}");
		return new ComponentChange(Registry.Require(name), Storage.LocationOf(name), revision);
	}

	/// <summary>Adds a boundary rule; the violations it reveals in the graph are kept (see <see cref="MetaRepo.Check"/>).</summary>
	public async Task<BoundaryChange> AddBoundaryAsync(BoundaryRule rule)
	{
		var revision = await MetaRepo.CommitAsync(new RegistryChange.AddBoundary(rule), $"Add boundary rule: {rule.Text}");
		return new BoundaryChange(Registry.Boundaries, revision);
	}

	/// <summary>Removes the boundary rule at <paramref name="index"/> (0-based).</summary>
	public async Task<BoundaryChange> RemoveBoundaryAsync(int index)
	{
		Registry.RemoveBoundary(index);
		var rule = Registry.Boundaries[index];
		var revision = await MetaRepo.CommitAsync(new RegistryChange.RemoveBoundary(index), $"Remove boundary rule #{index + 1}: {rule.Text}");
		return new BoundaryChange(Registry.Boundaries, revision);
	}

	/// <summary>Adds a conformance rule; it only reports, so nothing is refused for what it finds.</summary>
	public async Task<IReadOnlyList<ConformanceRule>> AddConformanceAsync(ConformanceRule rule)
	{
		await MetaRepo.CommitAsync(new RegistryChange.AddConformance(rule), $"Add conformance rule: {rule.Text}");
		return Registry.Conformance;
	}

	/// <summary>Removes the conformance rule at <paramref name="index"/> (0-based).</summary>
	public async Task<IReadOnlyList<ConformanceRule>> RemoveConformanceAsync(int index)
	{
		Registry.RemoveConformance(index);
		var rule = Registry.Conformance[index];
		await MetaRepo.CommitAsync(new RegistryChange.RemoveConformance(index), $"Remove conformance rule #{index + 1}: {rule.Text}");
		return Registry.Conformance;
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
	public async Task<ForkResult> ForkAsync(string parent, string name, string? target = null, IReadOnlyList<ComponentReference>? references = null, string url = "",
		ComponentAttributes? attributes = null)
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

		var definition = (attributes ?? ComponentAttributes.Of(source)).ApplyTo(new ComponentDefinition(name, url, references, source.Name, commit));
		MetaRepo.Check([new RegistryChange.Add(definition)]);
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
