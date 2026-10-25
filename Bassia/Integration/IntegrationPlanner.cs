namespace Bassia.Integration;

using Bassia.CliCommands.Agent;
using Bassia.Core;
using Bassia.Locks;
using Bassia.Storage;

/// <summary>What the user decided on top of the triage: a base per component and a strategy per step.</summary>
internal sealed record IntegrationChoices
{
	public static readonly IntegrationChoices None = new();

	/// <summary>Component name -> the branch or tag to integrate onto. Unlisted components use their default branch.</summary>
	public IReadOnlyDictionary<string, string> Onto { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>(component, run id) -> strategy. <c>*</c> as the component applies to every component of the run.</summary>
	public IReadOnlyDictionary<(string Component, string RunId), MergeStrategy> Strategies { get; init; } =
		new Dictionary<(string, string), MergeStrategy>();

	/// <summary>The structural merge (weave) tried where git conflicts; null leaves every conflict to the resolver.</summary>
	public StructuralMerge? Structural { get; init; }

	/// <summary>One merge policy for every component instead of each component's configured one (tests, previews).</summary>
	public MergePolicy? Policy { get; init; }

	/// <summary>One lock policy for every component instead of each component's <c>lock.conflict</c> (tests, previews).</summary>
	public LockConflictPolicy? LockPolicy { get; init; }

	public MergeStrategy? StrategyFor(string component, string runId) =>
		Strategies.TryGetValue((component, runId), out var strategy) ? strategy
		: Strategies.TryGetValue(("*", runId), out strategy) ? strategy
		: null;
}

/// <summary>
/// The triage: decides, per component, how every selected run's result will be integrated, without changing any
/// ref. Results are considered oldest run first. Each is classified against the integration head as git's
/// syntax-based merge sees it; the ones git can merge on its own are chained onto a simulated head, in order, so a
/// result that merges cleanly onto the base but conflicts with an earlier result is caught here too. Where git
/// conflicts, the structural merge (weave, when enabled) is asked the same question at the level of functions,
/// classes and keys; what it merges cleanly is chained just like git's merges, because it is just as deterministic.
/// Whatever neither can merge - or weave merges only with warnings - goes to the semantic queue, which runs after
/// every deterministic step, so the resolver sees as much of the integration as git and weave could build and is
/// asked to decide only what they could not.
/// </summary>
internal static class IntegrationPlanner
{
	/// <summary>A run can be integrated once it has pushed a result tag into at least one component.</summary>
	public static bool HasResults(RunMetadata run) => run.Components.Any(IsResult);

	private static bool IsResult(ComponentRun component) =>
		component.ResultStatus == ResultStatus.Pushed && component.ResultCommit is not null && component.ResultTag is not null;

	public static Task<IReadOnlyList<ComponentIntegration>> PlanAsync(Monorepo monorepo, IReadOnlyList<RunMetadata> runs, IntegrationChoices? choices = null) =>
		PlanAsync(monorepo.Graph(), IntegrationSettings.For(monorepo), runs, choices);

	/// <summary>The triage of <paramref name="runs"/> on <paramref name="graph"/>, with the policies of <paramref name="settings"/>.</summary>
	public static async Task<IReadOnlyList<ComponentIntegration>> PlanAsync(MonorepoGraph graph, IntegrationSettings settings, IReadOnlyList<RunMetadata> runs, IntegrationChoices? choices = null)
	{
		choices ??= IntegrationChoices.None;
		if (runs.Count == 0)
		{
			throw new IntegrationException("Select at least one agentic run to integrate.");
		}

		foreach (var run in runs.Where(run => !HasResults(run)))
		{
			throw new IntegrationException($"Agentic run '{run.RunId}' ({run.Status}) has no result pushed to any component; there is nothing to integrate.");
		}

		var ordered = runs.DistinctBy(run => run.RunId).OrderBy(run => run.Created, StringComparer.Ordinal).ThenBy(run => run.RunId, StringComparer.Ordinal).ToList();
		var touched = ordered.SelectMany(run => run.Components.Where(IsResult).Select(component => component.Name)).ToHashSet(StringComparer.Ordinal);

		foreach (var name in choices.Onto.Keys.Where(name => !touched.Contains(name)))
		{
			throw new IntegrationException($"-onto names '{name}', which none of the selected runs changed.");
		}

		var plan = new List<ComponentIntegration>();
		foreach (var definition in graph.Registry.Components.Where(component => touched.Contains(component.Name)))
		{
			plan.Add(await PlanComponentAsync(graph, settings, definition.Name, ordered, choices));
		}

		return plan;
	}

	private static async Task<ComponentIntegration> PlanComponentAsync(MonorepoGraph graph, IntegrationSettings settings, string name, IReadOnlyList<RunMetadata> runs, IntegrationChoices choices)
	{
		if (!graph.Storage.Exists(name))
		{
			throw new IntegrationException($"Component '{name}' has no local repository at '{graph.Storage.LocationOf(name)}'.");
		}

		var store = graph.Storage.Open(name);
		var baseRef = choices.Onto.GetValueOrDefault(name) ?? await DefaultBranchAsync(store, name);
		var baseCommit = await Revisions.ResolveAsync(store, baseRef)
			?? throw new IntegrationException($"'{baseRef}' does not exist in component '{name}' ('{store.Location}').");

		var component = new ComponentIntegration { Name = name, BaseRef = baseRef, BaseCommit = baseCommit };
		var baseScan = await ScanAsync(graph, store, name, baseCommit);
		var candidates = new List<IntegrationStep>();
		var caughtUp = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
		foreach (var run in runs)
		{
			if (run.Components.FirstOrDefault(entry => entry.Name == name) is not { } result || !IsResult(result))
			{
				continue;
			}

			var sourceCommit = await Revisions.ResolveAsync(store, result.ResultTag!)
				?? throw new IntegrationException($"The result tag '{result.ResultTag}' of run '{run.RunId}' is missing from component '{name}'.");
			if (sourceCommit != result.ResultCommit)
			{
				throw new IntegrationException($"The result tag '{result.ResultTag}' in component '{name}' points at {sourceCommit[..7]}, not at the recorded result {result.ResultCommit![..7]}.");
			}

			var step = new IntegrationStep
			{
				RunId = run.RunId,
				Rationale = AgentCommand.SummarizeCommand(run.Command),
				SourceTag = result.ResultTag!,
				SourceCommit = sourceCommit
			};
			step.LockConflicts.AddRange(result.LockConflicts.Select(conflict => conflict.Describe()));
			if (await ScanAsync(graph, store, name, sourceCommit) is { } scan)
			{
				step.BoundaryViolations.AddRange(scan.AddedOver(baseScan).Select(violation => violation.Message));
			}

			caughtUp[run.RunId] = result.CaughtUp.Select(catchUp => catchUp.Run).ToHashSet(StringComparer.Ordinal);
			candidates.Add(step);
		}

		candidates = AfterWhatTheyContain(candidates, caughtUp);

		// Chain every result git (or the structural merge) can merge on its own onto a simulated head, oldest first.
		var policy = choices.Policy ?? settings.MergePolicyFor(name);
		var lockPolicy = choices.LockPolicy ?? settings.LockPolicyFor(name);
		var acceptWarnings = policy.Warnings == WarningsPolicy.Accept;
		var head = baseCommit;
		List<IntegrationStep> deterministic = [], semantic = [], manual = [], skipped = [];
		foreach (var step in candidates)
		{
			var chosen = choices.StrategyFor(name, step.RunId);
			var probe = await ClassifyAsync(store, head, step, choices.Structural, acceptWarnings);
			var unmerged = step.Triage == Triage.Conflict && step.Structural != StructuralTriage.Clean
				&& !(acceptWarnings && step.Structural == StructuralTriage.Warnings);

			// A result that brought others in (lock wait) carries their commits: merging it merges them too, so it can be
			// no less careful than they are.
			var builtOn = candidates.TakeWhile(other => other != step).Where(other => caughtUp[step.RunId].Contains(other.RunId)).ToList();
			var heldBack = builtOn.FirstOrDefault(other => other.Strategy is MergeStrategy.Manual or MergeStrategy.Skip);
			var reviewed = builtOn.FirstOrDefault(other => other.Strategy == MergeStrategy.Semantic);

			if (chosen == MergeStrategy.Skip)
			{
				step.Strategy = MergeStrategy.Skip;
				step.Overridden = true;
				skipped.Add(step);
			}
			else if (step.Triage == Triage.UpToDate)
			{
				step.Strategy = MergeStrategy.Syntactic;
				deterministic.Add(step);
			}
			else if (chosen == MergeStrategy.Manual)
			{
				step.Strategy = MergeStrategy.Manual;
				step.Overridden = true;
				step.Note = "left for a human by -manual";
				manual.Add(step);
			}
			else if (chosen is null && heldBack is not null)
			{
				step.Strategy = MergeStrategy.Manual;
				step.Note = $"it builds on the result of run {RunMetadata.Key(heldBack.RunId)}, which is {(heldBack.Strategy == MergeStrategy.Skip ? "left out" : "left for a human")}";
				manual.Add(step);
			}
			else if (chosen is null && step.LockConflicts.Count > 0 && lockPolicy == LockConflictPolicy.Manual)
			{
				step.Strategy = MergeStrategy.Manual;
				step.Note = $"{LockNote(step)}; lock.conflict = manual leaves that for a human";
				manual.Add(step);
			}
			else if (chosen is null && step.Triage != Triage.UpToDate && step.BoundaryViolations.Count > 0 && policy.ManualBoundaries)
			{
				step.Strategy = MergeStrategy.Manual;
				step.Note = $"it adds {step.BoundaryViolations.Count} boundary violation(s), which merge.boundaries = manual leaves for a human";
				manual.Add(step);
			}
			else if (chosen != MergeStrategy.Semantic && step.Triage == Triage.Conflict && policy.ManualPathReason(step.Conflicts) is { } reason)
			{
				step.Strategy = MergeStrategy.Manual;
				step.Note = reason;
				manual.Add(step);
			}
			else if (chosen != MergeStrategy.Semantic && unmerged && step.Structural == StructuralTriage.Warnings && policy.Warnings == WarningsPolicy.Manual)
			{
				step.Strategy = MergeStrategy.Manual;
				step.Note = "weave merged it with warnings, which merge.warnings = manual leaves for a human";
				manual.Add(step);
			}
			else if (chosen != MergeStrategy.Semantic && unmerged && policy.ManualSemantic)
			{
				step.Strategy = MergeStrategy.Manual;
				step.Note = "neither git nor weave can merge it, and merge.semantic = manual leaves that for a human";
				manual.Add(step);
			}
			else if (chosen == MergeStrategy.Semantic || unmerged || (chosen is null && (reviewed is not null || (step.LockConflicts.Count > 0 && lockPolicy == LockConflictPolicy.Resolver))))
			{
				// A result that rewrote what another run held goes to the resolver even when git merges it: a clean
				// textual merge of two rewrites can still drop what one of them meant.
				step.Strategy = MergeStrategy.Semantic;
				step.Overridden = chosen == MergeStrategy.Semantic && !unmerged;
				if (chosen is null && step.LockConflicts.Count > 0 && lockPolicy == LockConflictPolicy.Resolver)
				{
					step.Note = $"{LockNote(step)}; lock.conflict = resolver has the resolver review it";
				}
				else if (chosen is null && !unmerged && reviewed is not null)
				{
					step.Note = $"it builds on the result of run {RunMetadata.Key(reviewed.RunId)}, which goes to the resolver";
				}

				semantic.Add(step);
			}
			else
			{
				// Git's own merge, or the structural merge where git conflicts and weave does not.
				step.Strategy = step.Triage == Triage.Conflict ? MergeStrategy.Structural : MergeStrategy.Syntactic;
				deterministic.Add(step);
				head = probe.Triage == Triage.FastForward ? step.SourceCommit : await MergeProbe.SimulateMergeAsync(store, head, step.SourceCommit, probe.Snapshot!);
			}
		}

		// What the resolver will face first: the semantic results against everything git and weave merged.
		foreach (var step in semantic)
		{
			await ClassifyAsync(store, head, step, choices.Structural);
		}

		// Which results collide with each other on their own - the reason a result that merges cleanly onto the base
		// can still need the resolver, and what the semantic brief points the resolver at. What the structural merge
		// combines cleanly is no collision.
		var active = candidates.Where(step => step.Strategy != MergeStrategy.Skip).ToList();
		for (var i = 0; i < active.Count; i++)
		{
			for (var j = i + 1; j < active.Count; j++)
			{
				var probe = await MergeProbe.ProbeAsync(store, active[i].SourceCommit, active[j].SourceCommit);
				if (probe.Triage == Triage.Conflict && choices.Structural is { } structural)
				{
					probe = await MergeProbe.ProbeAsync(store, active[i].SourceCommit, active[j].SourceCommit, structural);
				}

				if (probe.Triage == Triage.Conflict)
				{
					active[i].ConflictsWith.Add(active[j].RunId);
					active[j].ConflictsWith.Add(active[i].RunId);
				}
			}
		}

		component.Steps.AddRange([.. deterministic, .. semantic, .. manual, .. skipped]);
		return component;
	}

	/// <summary>The code boundary scan of a commit, or null when the component cannot be scanned (it then adds nothing).</summary>
	private static async Task<CodeScan?> ScanAsync(MonorepoGraph graph, IComponentStore store, string name, string commit)
	{
		try
		{
			return await CodeBoundaries.ScanAsync(graph.Registry, store, name, commit);
		}
		catch (Exception ex) when (ex is StorageException or MonorepoException)
		{
			return null;
		}
	}

	private static string LockNote(IntegrationStep step) =>
		step.LockConflicts[0] + (step.LockConflicts.Count > 1 ? $" (and {step.LockConflicts.Count - 1} more lock conflict(s))" : "");

	/// <summary>
	/// The steps in run order, except that a result which brought other runs' results in (<c>bassia lock wait</c>) comes
	/// after them: it builds on them, so merging them first leaves it only its own change to add.
	/// </summary>
	private static List<IntegrationStep> AfterWhatTheyContain(List<IntegrationStep> steps, IReadOnlyDictionary<string, IReadOnlySet<string>> caughtUp)
	{
		var remaining = steps.ToList();
		var ordered = new List<IntegrationStep>();
		while (remaining.Count > 0)
		{
			var next = remaining.FirstOrDefault(step => !remaining.Any(other => other != step && caughtUp.GetValueOrDefault(step.RunId)?.Contains(other.RunId) == true))
				?? remaining[0];
			ordered.Add(next);
			remaining.Remove(next);
		}

		return ordered;
	}

	/// <summary>
	/// Records on the step how <paramref name="head"/> and its result merge: git's verdict always, and where git
	/// conflicts, the structural merge's. Returns the probe whose tree a deterministic merge would produce: the
	/// structural one when git conflicts and weave merges cleanly (or with warnings the policy accepts), git's otherwise.
	/// </summary>
	private static async Task<MergeProbe.Result> ClassifyAsync(IComponentStore store, string head, IntegrationStep step, StructuralMerge? structural, bool acceptWarnings = false)
	{
		var probe = await MergeProbe.ProbeAsync(store, head, step.SourceCommit);
		step.Triage = probe.Triage;
		step.Conflicts.Clear();
		step.Conflicts.AddRange(probe.Conflicts);
		step.Structural = null;
		step.StructuralConflicts.Clear();
		step.StructuralWarnings.Clear();
		if (probe.Triage != Triage.Conflict || structural is null)
		{
			return probe;
		}

		var structured = await MergeProbe.ProbeAsync(store, head, step.SourceCommit, structural);
		step.StructuralConflicts.AddRange(structured.Conflicts);
		step.StructuralWarnings.AddRange(structured.Warnings);
		step.Structural = structured.Triage == Triage.Conflict ? StructuralTriage.Conflict
			: structured.Warnings.Count > 0 ? StructuralTriage.Warnings
			: StructuralTriage.Clean;
		return step.Structural == StructuralTriage.Clean || (acceptWarnings && step.Structural == StructuralTriage.Warnings) ? structured : probe;
	}

	/// <summary>The component's default branch: what <c>component add</c> imported, usually <c>main</c>.</summary>
	private static async Task<string> DefaultBranchAsync(IComponentStore store, string name) =>
		await store.DefaultBranchAsync()
			?? throw new IntegrationException($"Component '{name}' has no default branch; name a base with -onto {name}@<branch-or-tag>.");
}

/// <summary>Asks a component's store how two commits would merge, without a working copy and without moving any ref.</summary>
internal static class MergeProbe
{
	/// <param name="Snapshot">The merged content, to chain the next merge onto (see <see cref="SimulateMergeAsync"/>).</param>
	/// <param name="Warnings">The structural merge driver's warnings; always empty for the store's own merge.</param>
	internal sealed record Result(Triage Triage, string? Snapshot, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Warnings);

	/// <param name="structural">Merge with the structural driver (weave) instead of the store's own merge alone.</param>
	public static async Task<Result> ProbeAsync(IComponentStore store, string ours, string theirs, StructuralMerge? structural = null)
	{
		if (await store.IsAncestorAsync(theirs, ours))
		{
			return new Result(Triage.UpToDate, null, [], []);
		}

		if (await store.IsAncestorAsync(ours, theirs))
		{
			return new Result(Triage.FastForward, null, [], []);
		}

		var merge = await store.MergeAsync(ours, theirs, structural?.Driver);
		return new Result(merge.Clean ? Triage.Clean : Triage.Conflict, merge.Snapshot, merge.Conflicts, merge.Warnings);
	}

	/// <summary>
	/// The merge commit a merge would make, referenced by nothing: it lets the triage chain the next merge onto it. A
	/// store may forget it in due course (git's garbage collection removes it).
	/// </summary>
	public static Task<string> SimulateMergeAsync(IComponentStore store, string ours, string theirs, string snapshot) =>
		store.CommitSnapshotAsync(snapshot, [ours, theirs], "bassia integration triage");
}
