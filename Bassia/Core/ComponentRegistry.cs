namespace Bassia.Core;

using Bassia.Storage;

/// <summary>
/// The component graph the meta-repo maintains: registered components (program units) and the references between them
/// (a component nests another as a subfolder). Immutable and storage-free: every change returns a new registry, and a
/// change that would break the graph - a duplicate or invalid name, a reference to nothing, a cycle, a removed component
/// still referenced - throws <see cref="MonorepoException"/> and changes nothing.
/// </summary>
/// <remarks>
/// A registry read from a hand-edited file is taken as it is (<see cref="FromDefinitions"/> checks names and paths only),
/// so a dangling reference or a cycle there is reported where it matters - by <see cref="Closure"/> - instead of making
/// the whole monorepo unreadable.
/// </remarks>
internal sealed class ComponentRegistry
{
	private readonly Dictionary<string, ComponentDefinition> byName;

	private ComponentRegistry(IReadOnlyList<ComponentDefinition> components, IReadOnlyList<BoundaryRule> boundaries)
	{
		Components = components;
		Boundaries = boundaries;
		byName = components.ToDictionary(component => component.Name, StringComparer.Ordinal);
	}

	public static ComponentRegistry Empty { get; } = new([], []);

	/// <summary>Registered components, in registration order.</summary>
	public IReadOnlyList<ComponentDefinition> Components { get; }

	/// <summary>The boundary rules (<c>[[boundary]]</c> in <c>components.toml</c>), in order.</summary>
	public IReadOnlyList<BoundaryRule> Boundaries { get; }

	/// <summary>Every reference that breaks a boundary rule or a visibility (see <see cref="Core.Boundaries"/>).</summary>
	public IReadOnlyList<BoundaryViolation> Violations() => Core.Boundaries.Check(this);

	/// <summary>
	/// A registry of <paramref name="components"/> and <paramref name="boundaries"/> as they were stored: names unique and
	/// non-empty, reference paths relative and inside the component, no component referencing itself, valid tags and rules.
	/// Violations of the rules are kept: they are reported, not refused (see <see cref="MetaRepo"/>).
	/// </summary>
	public static ComponentRegistry FromDefinitions(IEnumerable<ComponentDefinition> components, IEnumerable<BoundaryRule>? boundaries = null)
	{
		var list = new List<ComponentDefinition>();
		foreach (var component in components)
		{
			if (string.IsNullOrWhiteSpace(component.Name))
			{
				throw new MonorepoException("components.toml contains a [[component]] entry without a name.");
			}

			if (list.Any(existing => existing.Name == component.Name))
			{
				throw new MonorepoException($"components.toml registers the component '{component.Name}' more than once.");
			}

			CheckReferences(component.Name, component.References);
			CheckAttributes(component);
			list.Add(component);
		}

		var rules = (boundaries ?? []).ToList();
		for (var i = 0; i < rules.Count; i++)
		{
			rules[i].Validate($"[[boundary]] #{i + 1}");
		}

		return new ComponentRegistry(list, rules);
	}

	public ComponentDefinition? Find(string name) => byName.GetValueOrDefault(name);

	public bool Contains(string name) => byName.ContainsKey(name);

	public ComponentDefinition Require(string name) =>
		Find(name) ?? throw new MonorepoException($"Component '{name}' is not registered. Registered: {JoinNames(Components)}.");

	/// <summary>The components that reference <paramref name="name"/>, in registration order.</summary>
	public IReadOnlyList<ComponentDefinition> ReferrersOf(string name) =>
		Components.Where(component => component.References.Any(reference => reference.Name == name)).ToList();

	/// <summary>
	/// The reference-graph closure of <paramref name="roots"/> (roots included), in dependency order: a component precedes
	/// every component it references. Throws when the graph reachable from the roots has a cycle or names an unregistered
	/// component.
	/// </summary>
	public IReadOnlyList<ComponentDefinition> Closure(IEnumerable<string> roots)
	{
		var ordered = new List<ComponentDefinition>();
		var state = new Dictionary<string, bool>(StringComparer.Ordinal); // false: in progress, true: done
		var path = new Stack<string>();

		void Visit(string name)
		{
			if (state.TryGetValue(name, out var done))
			{
				if (!done)
				{
					var cycle = string.Join(" -> ", path.Reverse().SkipWhile(item => item != name).Append(name));
					throw new MonorepoException($"components.toml contains a cyclic component reference: {cycle}.");
				}

				return;
			}

			var component = Find(name) ?? throw new MonorepoException($"Component '{name}' is referenced but not registered in components.toml.");
			state[name] = false;
			path.Push(name);
			foreach (var reference in component.References)
			{
				Visit(reference.Name);
			}

			path.Pop();
			state[name] = true;
			ordered.Add(component);
		}

		foreach (var root in roots)
		{
			Visit(root);
		}

		ordered.Reverse();
		return ordered;
	}

	/// <summary>Every component after the components it references (the order to create them in).</summary>
	public IReadOnlyList<ComponentDefinition> DependencyOrder() => Closure(Components.Select(component => component.Name)).Reverse().ToList();

	// ----- changes -----

	/// <summary>Applies <paramref name="changes"/> in order; throws at the first that does not fit, leaving this registry as it is.</summary>
	public ComponentRegistry Apply(IEnumerable<RegistryChange> changes) => changes.Aggregate(this, (registry, change) => registry.Apply(change));

	public ComponentRegistry Apply(RegistryChange change) => change switch
	{
		RegistryChange.Add add => Add(add.Component),
		RegistryChange.Remove remove => Remove(remove.Name),
		RegistryChange.SetReferences set => SetReferences(set.Name, set.References),
		RegistryChange.SetTags set => Replace(set.Name, component => component with { Tags = set.Tags }),
		RegistryChange.SetVisibility set => Replace(set.Name, component => component with { Visibility = set.Visibility }),
		RegistryChange.SetOwners set => Replace(set.Name, component => component with { Owners = set.Owners }),
		RegistryChange.AddBoundary add => AddBoundary(add.Rule),
		RegistryChange.RemoveBoundary remove => RemoveBoundary(remove.Index),
		_ => throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown registry change.")
	};

	/// <summary>Registers a component; its name must be new and valid and everything it references registered.</summary>
	public ComponentRegistry Add(ComponentDefinition component)
	{
		ValidateName(component.Name);
		if (Contains(component.Name))
		{
			throw new MonorepoException($"Component '{component.Name}' is already registered.");
		}

		CheckReferences(component.Name, component.References);
		CheckAttributes(component);
		RequireRegistered(component.References);
		return new ComponentRegistry([.. Components, component], Boundaries);
	}

	/// <summary>Unregisters a component nothing references.</summary>
	public ComponentRegistry Remove(string name)
	{
		var component = Require(name);
		var referrers = ReferrersOf(name);
		if (referrers.Count > 0)
		{
			throw new MonorepoException(
				$"Component '{component.Name}' is referenced by {string.Join(", ", referrers.Select(referrer => referrer.Name))}; " +
				$"remove those references first with 'bassia component set -name <component> -references ...'.");
		}

		return new ComponentRegistry(Components.Where(item => item.Name != name).ToList(), Boundaries);
	}

	/// <summary>Replaces a component's references; they must name registered components and keep the graph acyclic.</summary>
	public ComponentRegistry SetReferences(string name, IReadOnlyList<ComponentReference> references)
	{
		var component = Require(name);
		CheckReferences(name, references);
		RequireRegistered(references);
		var next = new ComponentRegistry(Components.Select(item => item.Name == name ? component with { References = references } : item).ToList(), Boundaries);
		next.Closure([name]);
		return next;
	}

	/// <summary>Replaces a component's definition by <paramref name="change"/> of it; its tags and visibility must be valid.</summary>
	public ComponentRegistry Replace(string name, Func<ComponentDefinition, ComponentDefinition> change)
	{
		var changed = change(Require(name));
		CheckAttributes(changed);
		return new ComponentRegistry(Components.Select(item => item.Name == name ? changed : item).ToList(), Boundaries);
	}

	/// <summary>Appends a valid boundary rule.</summary>
	public ComponentRegistry AddBoundary(BoundaryRule rule)
	{
		rule.Validate($"[[boundary]] #{Boundaries.Count + 1}");
		return new ComponentRegistry(Components, [.. Boundaries, rule]);
	}

	/// <summary>Removes the boundary rule at <paramref name="index"/> (0-based).</summary>
	public ComponentRegistry RemoveBoundary(int index)
	{
		if (index < 0 || index >= Boundaries.Count)
		{
			throw new MonorepoException(Boundaries.Count == 0
				? "There are no boundary rules to remove."
				: $"There is no boundary rule #{index + 1}; the rules are #1 to #{Boundaries.Count}.");
		}

		return new ComponentRegistry(Components, Boundaries.Where((_, i) => i != index).ToList());
	}

	/// <summary>
	/// A component name: not empty, no whitespace, no path separator or colon (a reference's <c>name:path</c> form), and
	/// no leading dot (the monorepo's own folders start with one).
	/// </summary>
	public static void ValidateName(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or ':'))
		{
			throw new MonorepoException($"'{name}' is not a valid component name: no whitespace, path separators or leading dot.");
		}
	}

	private static void CheckReferences(string owner, IReadOnlyList<ComponentReference> references)
	{
		foreach (var reference in references)
		{
			if (reference.Name == owner)
			{
				throw new MonorepoException($"Component '{owner}' cannot reference itself.");
			}

			var path = reference.Path;
			if (Path.IsPathRooted(path) || path.Split(['/', '\\']).Any(segment => segment is "" or "." or ".."))
			{
				throw new MonorepoException($"Component '{owner}': reference path '{path}' must be a relative path inside the component.");
			}
		}

		if (references.GroupBy(reference => reference.Path, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } clash)
		{
			throw new MonorepoException($"Component '{owner}' nests more than one component at '{clash.Key}'.");
		}
	}

	private static void CheckAttributes(ComponentDefinition component)
	{
		Core.Boundaries.ValidateTags(component.Name, component.Tags);
		Core.Boundaries.ValidateVisibility(component.Name, component.Visibility);
		if (component.Owners.Any(owner => string.IsNullOrWhiteSpace(owner) || owner.Any(c => char.IsWhiteSpace(c) || c == ',')))
		{
			throw new MonorepoException($"Component '{component.Name}': an owner must not be empty or contain whitespace or commas.");
		}
	}

	private void RequireRegistered(IReadOnlyList<ComponentReference> references)
	{
		foreach (var reference in references.Where(reference => !Contains(reference.Name)))
		{
			throw new MonorepoException($"-references names '{reference.Name}', which is not registered. Add it first.");
		}
	}

	private static string JoinNames(IEnumerable<ComponentDefinition> components)
	{
		var joined = string.Join(",", components.Select(component => component.Name));
		return joined.Length == 0 ? "-" : joined;
	}
}
