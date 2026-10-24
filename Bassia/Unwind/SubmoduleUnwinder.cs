namespace Bassia.Unwind;

using System.Text;
using Bassia.Core;
using Bassia.Storage;

/// <summary>A component taking part in an unwind: the one unwound, a submodule's component added for it, or one reused.</summary>
internal sealed class UnwindComponent
{
	public required string Name { get; init; }
	public required string Url { get; init; }

	/// <summary>Its store while the unwind is planned: the component's own, or a staged import for a new component.</summary>
	public required IComponentStore Store { get; set; }

	public string RepoDir => Store.Location;

	/// <summary>Whether components.toml does not register it yet: a component this unwind adds.</summary>
	public required bool IsNew { get; init; }

	/// <summary>The references it has (registered) before the unwind.</summary>
	public List<ComponentReference> References { get; init; } = [];

	/// <summary>The references the unwind adds: each submodule's component at the submodule's path.</summary>
	public List<ComponentReference> AddedReferences { get; } = [];

	public string? MainBranch { get; set; }
	public UnwindContext? Main { get; set; }
}

/// <summary>
/// A commit of a component whose submodules are unwound: the tip of its main branch, or a commit a submodule pins.
/// When it has <see cref="Pins"/>, the unwind makes <see cref="Unwound"/> on top of it, without the gitlinks, and tags
/// it, with every pinned commit below it, <see cref="Tag"/> (<c>unwind/&lt;component&gt;/&lt;n&gt;</c>).
/// </summary>
internal sealed class UnwindContext
{
	public required UnwindComponent Component { get; init; }
	public required string Commit { get; init; }
	public List<UnwindPin> Pins { get; } = [];
	public bool IsMain { get; set; }
	public string? Unwound { get; set; }
	public string? Tag { get; set; }

	/// <summary>The commit a run selects for this context: the unwound commit, or the commit itself when it pins nothing.</summary>
	public string Effective => Unwound ?? Commit;
}

/// <summary>One submodule of a context: where it was, what it pinned, and the component and context it became.</summary>
internal sealed record UnwindPin(Gitlink Link, string Url, string Identity, UnwindComponent Component, bool Added, UnwindContext Target);

/// <summary>A linking tag: the commit with submodules it is named after, and per component the commit it tags there.</summary>
internal sealed record UnwindTag(string Name, UnwindContext Context, IReadOnlyList<UnwindContext> Members, IReadOnlyList<string> Conflicts);

/// <summary>
/// Turns the git submodules of a component into components ("unwinding"), recursively. The whole tree is planned
/// first - components to add (cloned into a staging folder) or reuse, pinned commits fetched and checked, cycles and
/// clashes rejected - so a failing plan changes nothing. Applying it then, per commit that has submodules, makes one
/// commit on top that removes the gitlinks and their <c>.gitmodules</c> sections; on main that commit becomes the new
/// tip, elsewhere it stays off main. Each such commit and the commits its submodules pinned (unwound in turn) are linked
/// by one annotated tag of the same name in every component involved. The submodules become references at their paths.
/// </summary>
internal sealed class SubmoduleUnwinder : IDisposable
{
	public const string TagPrefix = "unwind/";

	private readonly MonorepoGraph graph;
	private readonly IStagingArea staging;
	private readonly List<UnwindComponent> components = [];
	private readonly Dictionary<string, UnwindComponent> byIdentity = new(StringComparer.Ordinal);
	private readonly Dictionary<(string Component, string Commit), UnwindContext> contexts = [];
	private readonly List<UnwindTag> tags = [];
	private readonly Dictionary<string, Lazy<UnwindComponent>> registered = new(StringComparer.Ordinal);

	private SubmoduleUnwinder(MonorepoGraph graph, UnwindComponent root)
	{
		this.graph = graph;
		Root = root;
		staging = graph.Storage.BeginStaging();
	}

	public UnwindComponent Root { get; }

	/// <summary>Every component of the plan, the root first.</summary>
	public IReadOnlyList<UnwindComponent> Components => components;

	/// <summary>Every commit visited, in the order found.</summary>
	public IEnumerable<UnwindContext> Contexts => contexts.Values;

	/// <summary>The commits that have submodules, in the order found.</summary>
	public IEnumerable<UnwindContext> Unwinding => contexts.Values.Where(context => context.Pins.Count > 0);

	public IReadOnlyList<UnwindTag> Tags => tags;

	/// <summary>
	/// Plans unwinding <paramref name="root"/> in <paramref name="graph"/>: a registered component, or one being
	/// added (<see cref="UnwindComponent.IsNew"/>, already imported into its store).
	/// </summary>
	public static async Task<SubmoduleUnwinder> PlanAsync(MonorepoGraph graph, UnwindComponent root)
	{
		var unwinder = new SubmoduleUnwinder(graph, root);
		try
		{
			await unwinder.PlanAsync();
			return unwinder;
		}
		catch
		{
			unwinder.Dispose();
			throw;
		}
	}

	private async Task PlanAsync()
	{
		components.Add(Root);
		foreach (var identity in await IdentitiesAsync(Root.Store, Root.Url))
		{
			byIdentity.TryAdd(identity, Root);
		}

		foreach (var definition in graph.Registry.Components.Where(definition => definition.Name != Root.Name))
		{
			var existing = new Lazy<UnwindComponent>(() => new UnwindComponent
			{
				Name = definition.Name,
				Url = definition.Url,
				Store = graph.Storage.Open(definition.Name),
				IsNew = false,
				References = definition.References.ToList()
			});
			var store = graph.Storage.Exists(definition.Name) ? graph.Storage.Open(definition.Name) : null;
			foreach (var identity in await IdentitiesAsync(store, definition.Url))
			{
				registered.TryAdd(identity, existing);
			}
		}

		// Every component of the tree has its main tip unwound too, so each can be selected by its bare name.
		var pending = new Queue<UnwindComponent>([Root]);
		var done = new HashSet<string>(StringComparer.Ordinal);
		while (pending.TryDequeue(out var component))
		{
			if (!done.Add(component.Name))
			{
				continue;
			}

			var branch = await component.Store.DefaultBranchAsync();
			var tip = branch is null ? null : await component.Store.BranchTipAsync(branch);
			if (branch is null || tip is null)
			{
				if (component == Root)
				{
					throw new MonorepoException($"Component '{component.Name}' has no default branch with commits to unwind.");
				}

				continue; // an empty repository has nothing to unwind on main; its pinned commits were handled
			}

			component.MainBranch = branch;
			component.Main = await VisitAsync(component, tip, [], pending);
			component.Main.IsMain = true;
		}

		CheckReferenceCycles();
		await PlanTagsAsync();
	}

	private async Task<UnwindContext> VisitAsync(UnwindComponent component, string commit, IReadOnlyList<string> chain, Queue<UnwindComponent> pending)
	{
		if (chain.Contains(component.Name))
		{
			throw new MonorepoException($"Submodules form a cycle: {string.Join(" -> ", chain.SkipWhile(name => name != component.Name).Append(component.Name))}.");
		}

		if (contexts.TryGetValue((component.Name, commit), out var known))
		{
			return known;
		}

		var context = new UnwindContext { Component = component, Commit = commit };
		contexts[(component.Name, commit)] = context;
		var path = chain.Append(component.Name).ToList();
		var superprojectUrl = await component.Store.UpstreamAsync() ?? component.Url;

		foreach (var link in (await component.Store.ListNestedAsync(commit)).Select(unit => new Gitlink(unit.Path, unit.Commit, unit.Name, unit.Url)))
		{
			if (string.IsNullOrWhiteSpace(link.Url))
			{
				throw new MonorepoException(
					$"Component '{component.Name}' at {commit[..10]} has a submodule at '{link.Path}' that .gitmodules gives no URL for; it cannot be identified.");
			}

			var url = RepoIdentity.Resolve(link.Url, superprojectUrl);
			var identity = RepoIdentity.Of(url);
			var (target, added) = await ComponentForAsync(identity, url, component);
			if (target == component)
			{
				throw new MonorepoException($"Component '{component.Name}' contains itself as a submodule at '{link.Path}'.");
			}

			await EnsureCommitAsync(target, link, url, component, commit);
			var targetContext = await VisitAsync(target, link.Commit, path, pending);
			context.Pins.Add(new UnwindPin(link, url, identity, target, added, targetContext));
			pending.Enqueue(target);

			var references = component.References.Concat(component.AddedReferences).ToList();
			if (references.FirstOrDefault(reference => reference.Path == link.Path) is { } atPath)
			{
				if (atPath.Name != target.Name)
				{
					throw new MonorepoException(
						$"Component '{component.Name}' already nests component '{atPath.Name}' at '{link.Path}', where its submodule is '{target.Name}' ({url}).");
				}
			}
			else
			{
				component.AddedReferences.Add(new ComponentReference(target.Name, link.Path));
			}
		}

		return context;
	}

	/// <summary>The component a submodule identity maps to: one already in the plan, a registered one, or a new clone.</summary>
	private async Task<(UnwindComponent Component, bool Added)> ComponentForAsync(string identity, string url, UnwindComponent parent)
	{
		if (byIdentity.TryGetValue(identity, out var known))
		{
			return (known, known.IsNew && known != Root);
		}

		if (registered.TryGetValue(identity, out var existing))
		{
			var component = existing.Value;
			if (!components.Contains(component))
			{
				components.Add(component);
			}

			byIdentity[identity] = component;
			return (component, false);
		}

		var name = UniqueName(ComponentCommands.DeriveComponentName(url), parent.Name);
		IComponentStore store;
		try
		{
			store = await staging.ImportAsync(name, url);
		}
		catch (StorageException ex)
		{
			throw new StorageException($"Could not clone the submodule '{url}' of component '{parent.Name}': {ex.Message}");
		}

		var added = new UnwindComponent { Name = name, Url = url, Store = store, IsNew = true };
		components.Add(added);
		foreach (var alias in await IdentitiesAsync(store, url))
		{
			byIdentity.TryAdd(alias, added);
		}

		byIdentity[identity] = added;
		return (added, true);
	}

	/// <summary>The name derived from the URL; on a clash with another component, prefixed with the parent's name, then numbered.</summary>
	private string UniqueName(string derived, string parent)
	{
		bool Taken(string name) =>
			graph.Registry.Contains(name) || components.Any(component => component.Name == name) || graph.Storage.Exists(name);

		if (!Taken(derived))
		{
			return derived;
		}

		var prefixed = $"{parent}-{derived}";
		var candidate = prefixed;
		for (var counter = 2; Taken(candidate); counter++)
		{
			candidate = $"{prefixed}-{counter}";
		}

		return candidate;
	}

	/// <summary>Makes sure the pinned commit is in the component's repository, fetching it from the submodule URL if needed.</summary>
	private static async Task EnsureCommitAsync(UnwindComponent target, Gitlink link, string url, UnwindComponent parent, string parentCommit)
	{
		if (!await target.Store.EnsureCommitAsync(link.Commit, url))
		{
			throw new MonorepoException(
				$"Component '{parent.Name}' at {parentCommit[..10]} pins its submodule '{link.Path}' ({url}) at {link.Commit}, " +
				$"which is not in component '{target.Name}' and could not be fetched from '{url}'. Nothing was changed.");
		}
	}

	/// <summary>A component is known by the URL it was added from and by its repository's origin, which may differ (a local mirror).</summary>
	private static async Task<IReadOnlyList<string>> IdentitiesAsync(IComponentStore? store, string url)
	{
		var identities = new List<string>();
		if (url.Length > 0)
		{
			identities.Add(RepoIdentity.Of(url));
		}

		if (store is not null && await store.UpstreamAsync() is { } origin)
		{
			identities.Add(RepoIdentity.Of(origin));
		}

		return identities.Distinct().ToList();
	}

	/// <summary>The references the unwind adds must keep the component graph acyclic, as components.toml requires.</summary>
	private void CheckReferenceCycles()
	{
		var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var definition in graph.Registry.Components)
		{
			edges[definition.Name] = definition.References.Select(reference => reference.Name).ToList();
		}

		foreach (var component in components)
		{
			var list = edges.TryGetValue(component.Name, out var existing) ? existing : edges[component.Name] = component.References.Select(reference => reference.Name).ToList();
			list.AddRange(component.AddedReferences.Select(reference => reference.Name));
		}

		var state = new Dictionary<string, bool>(StringComparer.Ordinal); // false: in progress, true: done
		var stack = new List<string>();
		void Visit(string name)
		{
			if (state.TryGetValue(name, out var finished))
			{
				if (!finished)
				{
					throw new MonorepoException(
						$"Unwinding would make the component references cyclic: {string.Join(" -> ", stack.SkipWhile(item => item != name).Append(name))}. Nothing was changed.");
				}

				return;
			}

			state[name] = false;
			stack.Add(name);
			foreach (var next in edges.GetValueOrDefault(name) ?? [])
			{
				Visit(next);
			}

			stack.RemoveAt(stack.Count - 1);
			state[name] = true;
		}

		foreach (var name in edges.Keys)
		{
			Visit(name);
		}
	}

	/// <summary>
	/// One tag per commit with submodules, over that commit and everything it pins, transitively. A component reached
	/// twice at different commits keeps the first (nearest) one; the other is reported as a conflict and stays reachable
	/// through its own parent's tag. Tags are numbered after the <c>unwind/&lt;component&gt;/&lt;n&gt;</c> tags already in
	/// any repository involved.
	/// </summary>
	private async Task PlanTagsAsync()
	{
		var nextByKey = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var context in Unwinding.ToList())
		{
			var members = new List<UnwindContext>();
			var conflicts = new List<string>();
			var queue = new Queue<UnwindContext>([context]);
			while (queue.TryDequeue(out var current))
			{
				if (members.FirstOrDefault(member => member.Component == current.Component) is { } kept)
				{
					if (kept != current)
					{
						conflicts.Add($"{current.Component.Name} is pinned at {kept.Commit[..10]} and at {current.Commit[..10]}; the tag keeps {kept.Commit[..10]}");
					}

					continue;
				}

				members.Add(current);
				foreach (var pin in current.Pins)
				{
					queue.Enqueue(pin.Target);
				}
			}

			var key = context.Component.Name;
			if (!nextByKey.TryGetValue(key, out var next))
			{
				var highest = -1;
				foreach (var component in components)
				{
					var existing = await component.Store.ListTagsAsync($"{TagPrefix}{key}/");
					highest = Math.Max(highest, RunMetadata.HighestIndex(existing.Select(tag => tag.Name)));
				}

				next = highest + 1;
			}

			nextByKey[key] = next + 1;
			context.Tag = $"{TagPrefix}{key}/{next}";
			tags.Add(new UnwindTag(context.Tag, context, members, conflicts));
		}
	}

	/// <summary>
	/// Applies the plan: the unwind commits, the new components promoted from staging, main branches advanced, the tags
	/// created, and the new components and references registered in one meta-repo revision, which is returned. The
	/// registry changes are checked before anything is written.
	/// </summary>
	public async Task<string?> ApplyAsync(string metaRepoMessage, IReadOnlyList<ComponentReference>? rootReferences = null)
	{
		// Referenced components first, so every change names registered components only.
		var changes = new List<RegistryChange>();
		foreach (var component in DependencyOrder())
		{
			if (component.IsNew)
			{
				var references = (component == Root ? rootReferences ?? [] : []).Concat(component.AddedReferences).ToList();
				changes.Add(new RegistryChange.Add(new ComponentDefinition(component.Name, component.Url, references)));
			}
			else if (component.AddedReferences.Count > 0)
			{
				changes.Add(new RegistryChange.SetReferences(component.Name, component.References.Concat(component.AddedReferences).ToList()));
			}
		}

		graph.Registry.Apply(changes);

		foreach (var context in Unwinding)
		{
			context.Unwound = await context.Component.Store.CommitWithoutNestedAsync(context.Commit, context.Pins.Select(pin => pin.Link.Path).ToList(), CommitMessage(context));
		}

		foreach (var component in components.Where(component => component.IsNew && component != Root))
		{
			component.Store = await staging.PromoteAsync(component.Store);
		}

		foreach (var component in components.Where(component => component.Main is { Pins.Count: > 0 }))
		{
			await component.Store.SetBranchAsync(component.MainBranch!, component.Main!.Unwound!, component.Main.Commit);
		}

		foreach (var tag in tags)
		{
			var message = TagMessage(tag);
			foreach (var member in tag.Members)
			{
				await member.Component.Store.CreateTagAsync(tag.Name, member.Effective, message);
			}
		}

		return await graph.MetaRepo.CommitAsync(changes, metaRepoMessage);
	}

	private IEnumerable<UnwindComponent> DependencyOrder()
	{
		var ordered = new List<UnwindComponent>();
		var visited = new HashSet<UnwindComponent>();
		void Visit(UnwindComponent component)
		{
			if (!visited.Add(component))
			{
				return;
			}

			foreach (var reference in component.AddedReferences)
			{
				if (components.FirstOrDefault(candidate => candidate.Name == reference.Name) is { } target)
				{
					Visit(target);
				}
			}

			ordered.Add(component);
		}

		foreach (var component in components)
		{
			Visit(component);
		}

		return ordered;
	}

	private static string CommitMessage(UnwindContext context)
	{
		var builder = new StringBuilder($"bassia: unwind submodule(s) {string.Join(", ", context.Pins.Select(pin => pin.Link.Path))} into components\n\n");
		builder.Append("Each submodule is now a Bassia component, nested here as a reference at its path:\n\n");
		foreach (var pin in context.Pins)
		{
			builder.Append($"- {pin.Link.Path} -> {pin.Component.Name} @ {pin.Link.Commit} ({pin.Url})\n");
		}

		return builder.ToString();
	}

	private static string TagMessage(UnwindTag tag)
	{
		var builder = new StringBuilder($"{tag.Name}: {tag.Context.Component.Name} at {tag.Context.Commit[..10]} with its submodules unwound into components\n\n");
		foreach (var member in tag.Members)
		{
			builder.Append($"{member.Component.Name} {member.Effective}\n");
		}

		foreach (var conflict in tag.Conflicts)
		{
			builder.Append($"conflict: {conflict}\n");
		}

		return builder.ToString();
	}

	public void Dispose() => staging.Dispose();
}
