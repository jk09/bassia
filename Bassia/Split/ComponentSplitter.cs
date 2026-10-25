namespace Bassia.Split;

using Bassia.Core;
using Bassia.Storage;
using Tomlyn;
using Tomlyn.Model;

/// <summary>A split plan that does not fit the component or the monorepo: every problem, reported together.</summary>
internal sealed class SplitPlanException(IReadOnlyList<string> problems, IReadOnlyList<string> unallocated, IReadOnlyList<string> ambiguous)
	: Exception(problems.Count == 1 ? $"The split plan has a problem: {problems[0]}" : $"The split plan has {problems.Count} problems; the first: {problems[0]}")
{
	public IReadOnlyList<string> Problems { get; } = problems;
	public IReadOnlyList<string> Unallocated { get; } = unallocated;
	public IReadOnlyList<string> Ambiguous { get; } = ambiguous;
}

/// <summary>A checked plan: the source, its branch and tip, and the tip's files allocated to the parts.</summary>
internal sealed class SplitAllocation
{
	public required SplitPlan Plan { get; init; }
	public required ComponentDefinition Source { get; init; }
	public required IComponentStore Store { get; init; }
	public string SourceLocation => Store.Location;
	public required string Branch { get; init; }
	public required string TipCommit { get; init; }

	/// <summary>The tip's files per part (<c>shared</c> files included), in plan order.</summary>
	public required IReadOnlyList<IReadOnlyList<TipFile>> Files { get; init; }
	public required IReadOnlyList<TipFile> Shared { get; init; }
	public required IReadOnlyList<TipFile> Dropped { get; init; }

	/// <summary>Owner of each tip path: a part index, -1 for shared, -2 for dropped.</summary>
	public required IReadOnlyDictionary<string, int> TipOwner { get; init; }

	/// <summary>Every component after the split: the parts added, referrers rewired, the source gone; parts in dependency order.</summary>
	public required IReadOnlyList<ComponentDefinition> After { get; init; }
	public required IReadOnlyList<SplitReferrer> Referrers { get; init; }

	public IReadOnlyList<ComponentDefinition> PartDefinitions =>
		After.Where(component => Plan.Parts.Any(part => part.Name == component.Name)).ToList();
}

/// <summary>A part as built: its repository, commits and tags.</summary>
internal sealed record SplitPartResult(string Name, string Path, int Files, long Bytes, int Commits, string Head, IReadOnlyList<string> Tags, IReadOnlyList<string> DroppedTags, int RenamedPaths);

internal sealed record SplitResult(string Id, SplitAllocation Allocation, IReadOnlyList<SplitPartResult> Parts, string SourceTag, string? MetaRepoCommit, IReadOnlyList<string> UnmergedResults);

/// <summary>
/// Breaks one component into several according to a <see cref="SplitPlan"/>: checks the plan against the component's
/// tip, rewrites the history for each part into a new repository, verifies each part's tip, and only then registers the
/// parts, rewires the references and retires the source. The source repository is never rewritten.
/// </summary>
internal static class ComponentSplitter
{
	public const string IdPrefix = "split-";
	public const string TagPrefix = "split/";
	public const string Trailer = "Split-from";

	public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N");

	public static string Key(string id) => id[IdPrefix.Length..];

	public static string TagName(string id) => TagPrefix + Key(id);

	// ----- checking a plan -----

	/// <summary>Checks <paramref name="plan"/> against the monorepo and the source's tip; throws <see cref="SplitPlanException"/> listing every problem.</summary>
	public static async Task<SplitAllocation> AllocateAsync(MonorepoGraph graph, SplitPlan plan, string? sourceName, List<string> problems)
	{
		if (sourceName is not null && plan.Source is not null && plan.Source != sourceName)
		{
			problems.Add($"-name names '{sourceName}' but the plan's source is '{plan.Source}'.");
		}

		var name = sourceName ?? plan.Source;
		if (name is null)
		{
			problems.Add("The plan has no 'source'; name the component to split with 'source = \"<component>\"' or -name.");
			throw Fail(problems);
		}

		var registry = graph.Registry;
		var source = registry.Find(name);
		if (source is null)
		{
			problems.Add($"The source '{name}' is not a registered component.");
			throw Fail(problems);
		}

		if (!graph.Storage.Exists(name))
		{
			problems.Add($"The source '{name}' has no repository at '{graph.Storage.LocationOf(name)}'.");
			throw Fail(problems);
		}

		var store = graph.Storage.Open(name);
		var branch = plan.Branch ?? await store.DefaultBranchAsync() ?? "main";
		var tip = await store.BranchTipAsync(branch);
		if (tip is null)
		{
			problems.Add($"The source '{name}' has no branch '{branch}'.");
			throw Fail(problems);
		}

		var files = await ListTipAsync(store, tip);
		var perPart = plan.Parts.Select(_ => new List<TipFile>()).ToList();
		var shared = new List<TipFile>();
		var dropped = new List<TipFile>();
		var owner = new Dictionary<string, int>(StringComparer.Ordinal);
		var unallocated = new List<string>();
		var ambiguous = new List<string>();

		foreach (var file in files)
		{
			var isShared = plan.Shared.Matches(file.Path);
			var isDropped = plan.Drop.Matches(file.Path);
			if (isShared && isDropped)
			{
				ambiguous.Add($"{file.Path} (shared, drop)");
				continue;
			}

			if (isShared || isDropped)
			{
				(isShared ? shared : dropped).Add(file);
				owner[file.Path] = isShared ? -1 : -2;
				continue;
			}

			var matching = Enumerable.Range(0, plan.Parts.Count).Where(index => plan.Parts[index].Paths.Matches(file.Path)).ToList();
			switch (matching.Count)
			{
				case 0:
					unallocated.Add(file.Path);
					break;
				case 1:
					perPart[matching[0]].Add(file);
					owner[file.Path] = matching[0];
					break;
				default:
					ambiguous.Add($"{file.Path} ({string.Join(", ", matching.Select(index => plan.Parts[index].Name))})");
					break;
			}
		}

		if (unallocated.Count > 0)
		{
			problems.Add($"{unallocated.Count} file(s) at the tip of '{name}' belong to no part, e.g. {string.Join(", ", unallocated.Take(5))}. " +
				"Allocate them to a part, or list them under 'shared' or 'drop'.");
		}

		if (ambiguous.Count > 0)
		{
			problems.Add($"{ambiguous.Count} file(s) match more than one part, e.g. {string.Join("; ", ambiguous.Take(5))}. " +
				"Narrow the patterns (a '!pattern' excludes), or list shared files under 'shared'.");
		}

		for (var index = 0; index < plan.Parts.Count; index++)
		{
			if (perPart[index].Count == 0)
			{
				problems.Add($"Part '{plan.Parts[index].Name}' gets no file at the tip of '{name}'.");
			}

			perPart[index].AddRange(shared);
		}

		var after = CheckRegistration(registry, graph.Storage, plan, source, problems, out var referrers);

		if (problems.Count > 0)
		{
			throw Fail(problems, unallocated, ambiguous);
		}

		return new SplitAllocation
		{
			Plan = plan,
			Source = source,
			Store = store,
			Branch = branch,
			TipCommit = tip,
			Files = perPart,
			Shared = shared,
			Dropped = dropped,
			TipOwner = owner,
			After = after,
			Referrers = referrers
		};
	}

	/// <summary>Checks names and references, and returns the components as they will be after the split.</summary>
	private static List<ComponentDefinition> CheckRegistration(ComponentRegistry registry, IComponentStorage storage, SplitPlan plan, ComponentDefinition source, List<string> problems, out List<SplitReferrer> referrers)
	{
		var partNames = plan.Parts.Select(part => part.Name).ToHashSet(StringComparer.Ordinal);
		foreach (var part in plan.Parts)
		{
			if (registry.Contains(part.Name))
			{
				problems.Add($"Part '{part.Name}': a component of that name is already registered.");
			}
			else if (storage.Exists(part.Name))
			{
				problems.Add($"Part '{part.Name}': '{storage.LocationOf(part.Name)}' already exists.");
			}
		}

		bool Known(string name) => partNames.Contains(name) || (name != source.Name && registry.Contains(name));

		var parts = new List<ComponentDefinition>();
		foreach (var part in plan.Parts)
		{
			var references = part.References ?? source.References;
			foreach (var reference in references)
			{
				if (reference.Name == part.Name)
				{
					problems.Add($"Part '{part.Name}' cannot reference itself.");
				}
				else if (reference.Name == source.Name)
				{
					problems.Add($"Part '{part.Name}' references the source '{source.Name}', which the split retires; reference its parts instead.");
				}
				else if (!Known(reference.Name))
				{
					problems.Add($"Part '{part.Name}' references '{reference.Name}', which is neither registered nor a part.");
				}
			}

			parts.Add(new ComponentDefinition(part.Name, part.Url ?? "", references)
			{
				Tags = part.Tags ?? source.Tags,
				Visibility = part.Visibility ?? source.Visibility,
				Owners = part.Owners ?? source.Owners,
				Exports = part.Exports
			});
		}

		var referring = registry.ReferrersOf(source.Name).Select(referrer => referrer.Name).ToHashSet(StringComparer.Ordinal);
		var rewiring = new List<SplitReferrer>();
		foreach (var referrer in plan.Referrers)
		{
			if (!referring.Contains(referrer.Name))
			{
				problems.Add($"Referrer '{referrer.Name}' does not reference the source '{source.Name}'" +
					(referring.Count == 0 ? "; nothing does." : $"; only {string.Join(", ", referring)} do."));
				continue;
			}

			foreach (var reference in referrer.References.Where(reference => !Known(reference.Name) || reference.Name == referrer.Name))
			{
				problems.Add(reference.Name == source.Name
					? $"Referrer '{referrer.Name}' still references the source '{source.Name}', which the split retires."
					: reference.Name == referrer.Name
						? $"Referrer '{referrer.Name}' cannot reference itself."
						: $"Referrer '{referrer.Name}' references '{reference.Name}', which is neither registered nor a part.");
			}

			rewiring.Add(referrer);
		}

		// A referrer the plan does not mention references every part instead of the source.
		foreach (var name in referring.Where(name => plan.Referrers.All(referrer => referrer.Name != name)))
		{
			var component = registry.Require(name);
			var references = component.References
				.SelectMany(reference => reference.Name == source.Name ? plan.Parts.Select(part => new ComponentReference(part.Name, part.Name)) : [reference])
				.DistinctBy(reference => reference.Name)
				.ToList();
			rewiring.Add(new SplitReferrer(name, references));
		}

		referrers = rewiring;

		var after = registry.Components
			.Where(component => component.Name != source.Name)
			.Select(component => rewiring.FirstOrDefault(referrer => referrer.Name == component.Name) is { } rewired
				? component with { References = rewired.References }
				: component)
			.ToList();

		// Parts in dependency order (a part after the parts it references), so each can be registered in turn.
		var ordered = new List<ComponentDefinition>();
		var visiting = new HashSet<string>(StringComparer.Ordinal);
		void Visit(ComponentDefinition part, Stack<string> path)
		{
			if (ordered.Contains(part))
			{
				return;
			}

			if (!visiting.Add(part.Name))
			{
				problems.Add($"The parts reference each other in a cycle: {string.Join(" -> ", path.Reverse().SkipWhile(name => name != part.Name).Append(part.Name))}.");
				return;
			}

			path.Push(part.Name);
			foreach (var reference in part.References)
			{
				if (parts.FirstOrDefault(candidate => candidate.Name == reference.Name) is { } dependency)
				{
					Visit(dependency, path);
				}
			}

			path.Pop();
			if (!ordered.Contains(part))
			{
				ordered.Add(part);
			}
		}

		foreach (var part in parts)
		{
			Visit(part, new Stack<string>());
		}

		after.AddRange(ordered);
		if (problems.Count == 0)
		{
			CheckAcyclic(after, problems);
		}

		if (problems.Count == 0)
		{
			try
			{
				problems.AddRange(Boundaries.Added(registry, ComponentRegistry.FromDefinitions(after, registry.Boundaries)).Select(violation => violation.Message));
			}
			catch (MonorepoException ex)
			{
				problems.Add(ex.Message);
			}
		}

		return after;
	}

	private static void CheckAcyclic(IReadOnlyList<ComponentDefinition> components, List<string> problems)
	{
		var state = new Dictionary<string, bool>(StringComparer.Ordinal);
		bool Visit(ComponentDefinition component, List<string> path)
		{
			if (state.TryGetValue(component.Name, out var done))
			{
				if (!done)
				{
					problems.Add($"After the split the components would reference each other in a cycle: {string.Join(" -> ", path.SkipWhile(name => name != component.Name).Append(component.Name))}.");
				}

				return done;
			}

			state[component.Name] = false;
			path.Add(component.Name);
			foreach (var reference in component.References)
			{
				if (components.FirstOrDefault(candidate => candidate.Name == reference.Name) is { } next && !Visit(next, path))
				{
					return false;
				}
			}

			path.RemoveAt(path.Count - 1);
			state[component.Name] = true;
			return true;
		}

		foreach (var component in components)
		{
			if (!Visit(component, []))
			{
				return;
			}
		}
	}

	private static SplitPlanException Fail(List<string> problems, List<string>? unallocated = null, List<string>? ambiguous = null) =>
		new(problems, unallocated ?? [], ambiguous ?? []);

	// ----- performing a split -----

	/// <summary>
	/// Performs a checked split: the storage writes each part's history into a new store, the source is tagged
	/// <c>split/&lt;id&gt;</c> at its tip, and the meta-repo registers the parts, rewires the referrers and retires the
	/// source in one revision. On failure everything created is removed again; the source's history is never rewritten.
	/// </summary>
	public static async Task<SplitResult> SplitAsync(MonorepoGraph graph, SplitAllocation allocation)
	{
		var id = NewId();
		var tag = TagName(id);
		var source = allocation.Store;
		var timestamp = DateTimeOffset.UtcNow.ToString("o");
		var sourceTagged = false;

		try
		{
			var parts = await graph.Storage.SplitAsync(source, new SplitHistoryRequest(allocation, id, tag, part => RecordMessage(id, allocation, part, timestamp)));
			var unmerged = await UnmergedResultsAsync(source, allocation.TipCommit);

			await source.CreateTagAsync(tag, allocation.TipCommit, RecordMessage(id, allocation, null, timestamp));
			sourceTagged = true;

			var changes = allocation.PartDefinitions.Select(part => (RegistryChange)new RegistryChange.Add(part))
				.Concat(allocation.Referrers.Select(referrer => new RegistryChange.SetReferences(referrer.Name, referrer.References)))
				.Append(new RegistryChange.Remove(allocation.Source.Name))
				.ToList();
			var commit = await graph.MetaRepo.CommitAsync(changes, RecordMessage(id, allocation, null, timestamp));
			return new SplitResult(id, allocation, parts, tag, commit, unmerged);
		}
		catch
		{
			if (sourceTagged)
			{
				await source.DeleteTagAsync(tag);
			}

			foreach (var part in allocation.Plan.Parts.Where(part => graph.Storage.Exists(part.Name) && !graph.Registry.Contains(part.Name)))
			{
				await graph.Storage.DeleteAsync(part.Name);
			}

			throw;
		}
	}

	/// <summary>Run and integration result tags not merged into the source's branch: work the split leaves with the retired source.</summary>
	private static async Task<IReadOnlyList<string>> UnmergedResultsAsync(IComponentStore source, string tip)
	{
		var unmerged = new List<string>();
		foreach (var tag in (await source.ListTagsAsync()).Where(tag => tag.Name.StartsWith(RunMetadata.RefPrefix, StringComparison.Ordinal) || tag.Name.StartsWith("integration/", StringComparison.Ordinal)))
		{
			if (!await source.IsAncestorAsync(tag.Commit, tip))
			{
				unmerged.Add(tag.Name);
			}
		}

		return unmerged;
	}

	/// <summary>The files at a commit as a split allocates them: its files and the units nested in it, in path order.</summary>
	internal static async Task<List<TipFile>> ListTipAsync(IComponentStore store, string commit) =>
		(await store.ListFilesAsync(commit)).Select(file => new TipFile(file.Path, file.Mode, file.Object, file.Size))
			.Concat((await store.ListNestedAsync(commit)).Select(unit => new TipFile(unit.Path, "160000", unit.Commit, 0)))
			.OrderBy(file => file.Path, StringComparer.Ordinal)
			.ToList();

	/// <summary>
	/// The split record: a subject and a TOML <c>[split]</c> table. Each part's record commit and the <c>split/&lt;id&gt;</c>
	/// tags carry it; the meta-repo commit carries it without a part.
	/// </summary>
	private static string RecordMessage(string id, SplitAllocation allocation, SplitPart? part, string created)
	{
		var plan = allocation.Plan;
		var record = new TomlTable
		{
			["id"] = id,
			["created"] = created,
			["source"] = allocation.Source.Name,
			["source_commit"] = allocation.TipCommit,
			["branch"] = allocation.Branch,
			["tag"] = TagName(id),
			["parts"] = ToArray(plan.Parts.Select(item => item.Name))
		};

		if (part is not null)
		{
			record["part"] = part.Name;
			record["paths"] = ToArray(part.Paths.Texts);
		}

		if (!plan.Shared.IsEmpty) record["shared"] = ToArray(plan.Shared.Texts);
		if (!plan.Drop.IsEmpty) record["drop"] = ToArray(plan.Drop.Texts);
		record["follow_renames"] = plan.FollowRenames;

		var subject = part is null
			? $"split({Key(id)[..8]}): {allocation.Source.Name} into {string.Join(", ", plan.Parts.Select(item => item.Name))}"
			: $"split({Key(id)[..8]}): {part.Name} from {allocation.Source.Name}";
		return $"{subject}\n\n{TomlSerializer.Serialize(new TomlTable { ["split"] = record })}";
	}

	private static TomlArray ToArray(IEnumerable<string> values)
	{
		var array = new TomlArray();
		foreach (var value in values)
		{
			array.Add(value);
		}

		return array;
	}
}
