namespace Bassia.Integration;

using Bassia.CliCommands.Agent;
using Bassia.Git;

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

	public static async Task<IReadOnlyList<ComponentIntegration>> PlanAsync(Monorepo monorepo, IReadOnlyList<RunMetadata> runs, IntegrationChoices? choices = null)
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
		foreach (var definition in monorepo.Components.Where(component => touched.Contains(component.Name)))
		{
			plan.Add(await PlanComponentAsync(monorepo, definition.Name, ordered, choices));
		}

		return plan;
	}

	private static async Task<ComponentIntegration> PlanComponentAsync(Monorepo monorepo, string name, IReadOnlyList<RunMetadata> runs, IntegrationChoices choices)
	{
		var sourceDir = monorepo.SourceRepoDir(name);
		if (!Directory.Exists(sourceDir))
		{
			throw new IntegrationException($"Component '{name}' has no local repository at '{sourceDir}'.");
		}

		var git = GitClient.In(sourceDir);
		var baseRef = choices.Onto.GetValueOrDefault(name) ?? await DefaultBranchAsync(git, name);
		var baseCommit = await ResolveAsync(git, baseRef)
			?? throw new IntegrationException($"'{baseRef}' does not exist in component '{name}' ('{sourceDir}').");

		var component = new ComponentIntegration { Name = name, BaseRef = baseRef, BaseCommit = baseCommit };
		var candidates = new List<IntegrationStep>();
		foreach (var run in runs)
		{
			if (run.Components.FirstOrDefault(entry => entry.Name == name) is not { } result || !IsResult(result))
			{
				continue;
			}

			var sourceCommit = await ResolveAsync(git, result.ResultTag!)
				?? throw new IntegrationException($"The result tag '{result.ResultTag}' of run '{run.RunId}' is missing from component '{name}'.");
			if (sourceCommit != result.ResultCommit)
			{
				throw new IntegrationException($"The result tag '{result.ResultTag}' in component '{name}' points at {sourceCommit[..7]}, not at the recorded result {result.ResultCommit![..7]}.");
			}

			candidates.Add(new IntegrationStep
			{
				RunId = run.RunId,
				Rationale = AgentCommand.SummarizeCommand(run.Command),
				SourceTag = result.ResultTag!,
				SourceCommit = sourceCommit
			});
		}

		// Chain every result git (or the structural merge) can merge on its own onto a simulated head, oldest first.
		var policy = choices.Policy ?? monorepo.MergePolicyFor(name);
		var acceptWarnings = policy.Warnings == WarningsPolicy.Accept;
		var head = baseCommit;
		List<IntegrationStep> deterministic = [], semantic = [], manual = [], skipped = [];
		foreach (var step in candidates)
		{
			var chosen = choices.StrategyFor(name, step.RunId);
			var probe = await ClassifyAsync(git, head, step, choices.Structural, acceptWarnings);
			var unmerged = step.Triage == Triage.Conflict && step.Structural != StructuralTriage.Clean
				&& !(acceptWarnings && step.Structural == StructuralTriage.Warnings);

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
			else if (chosen == MergeStrategy.Semantic || unmerged)
			{
				step.Strategy = MergeStrategy.Semantic;
				step.Overridden = !unmerged;
				semantic.Add(step);
			}
			else
			{
				// Git's own merge, or the structural merge where git conflicts and weave does not.
				step.Strategy = step.Triage == Triage.Conflict ? MergeStrategy.Structural : MergeStrategy.Syntactic;
				deterministic.Add(step);
				head = probe.Triage == Triage.FastForward ? step.SourceCommit : await MergeProbe.SimulateMergeAsync(git, head, step.SourceCommit, probe.Tree!);
			}
		}

		// What the resolver will face first: the semantic results against everything git and weave merged.
		foreach (var step in semantic)
		{
			await ClassifyAsync(git, head, step, choices.Structural);
		}

		// Which results collide with each other on their own - the reason a result that merges cleanly onto the base
		// can still need the resolver, and what the semantic brief points the resolver at. What the structural merge
		// combines cleanly is no collision.
		var active = candidates.Where(step => step.Strategy != MergeStrategy.Skip).ToList();
		for (var i = 0; i < active.Count; i++)
		{
			for (var j = i + 1; j < active.Count; j++)
			{
				var probe = await MergeProbe.ProbeAsync(git, active[i].SourceCommit, active[j].SourceCommit);
				if (probe.Triage == Triage.Conflict && choices.Structural is { } structural)
				{
					probe = await MergeProbe.ProbeAsync(git, active[i].SourceCommit, active[j].SourceCommit, structural);
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

	/// <summary>
	/// Records on the step how <paramref name="head"/> and its result merge: git's verdict always, and where git
	/// conflicts, the structural merge's. Returns the probe whose tree a deterministic merge would produce: the
	/// structural one when git conflicts and weave merges cleanly (or with warnings the policy accepts), git's otherwise.
	/// </summary>
	private static async Task<MergeProbe.Result> ClassifyAsync(GitClient git, string head, IntegrationStep step, StructuralMerge? structural, bool acceptWarnings = false)
	{
		var probe = await MergeProbe.ProbeAsync(git, head, step.SourceCommit);
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

		var structured = await MergeProbe.ProbeAsync(git, head, step.SourceCommit, structural);
		step.StructuralConflicts.AddRange(structured.Conflicts);
		step.StructuralWarnings.AddRange(structured.Warnings);
		step.Structural = structured.Triage == Triage.Conflict ? StructuralTriage.Conflict
			: structured.Warnings.Count > 0 ? StructuralTriage.Warnings
			: StructuralTriage.Clean;
		return step.Structural == StructuralTriage.Clean || (acceptWarnings && step.Structural == StructuralTriage.Warnings) ? structured : probe;
	}

	/// <summary>The branch the component repo's <c>HEAD</c> names: what <c>component add</c> cloned, usually <c>main</c>.</summary>
	private static async Task<string> DefaultBranchAsync(GitClient git, string name)
	{
		var head = await git.RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"]);
		return head.ExitCode == 0 && head.Output.Trim().Length > 0
			? head.Output.Trim()
			: throw new IntegrationException($"Component '{name}' has no default branch; name a base with -onto {name}@<branch-or-tag>.");
	}

	internal static async Task<string?> ResolveAsync(GitClient git, string commitIsh)
	{
		var result = await git.RunAsync(["rev-parse", "--quiet", "--verify", $"{commitIsh}^{{commit}}"]);
		return result.ExitCode == 0 ? result.Output.Trim() : null;
	}
}

/// <summary>Asks git how two commits would merge, without a working tree and without moving any ref.</summary>
internal static class MergeProbe
{
	/// <param name="Warnings">The structural merge driver's warnings; always empty for git's own merge.</param>
	internal sealed record Result(Triage Triage, string? Tree, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Warnings);

	/// <param name="structural">Merge with the structural driver (weave) instead of git's line merge alone.</param>
	public static async Task<Result> ProbeAsync(GitClient git, string ours, string theirs, StructuralMerge? structural = null)
	{
		if (await IsAncestorAsync(git, theirs, ours))
		{
			return new Result(Triage.UpToDate, null, [], []);
		}

		if (await IsAncestorAsync(git, ours, theirs))
		{
			return new Result(Triage.FastForward, null, [], []);
		}

		// merge-tree --write-tree (git 2.38+) runs the real three-way merge in the object database: exit code 0 is a
		// clean merge and prints the tree, 1 is a conflict and lists the conflicted paths after the tree. It honours a
		// merge driver given with -c, which is how the structural merge is probed without touching the repo.
		var merge = await git.RunAsync([.. structural?.GitOptions ?? [], "merge-tree", "--write-tree", "--name-only", "--no-messages", ours, theirs]);
		var lines = merge.Output.Replace("\r", "").Split('\n');
		var warnings = structural is null ? [] : StructuralMerge.Warnings(merge.Error);
		return merge.ExitCode switch
		{
			0 => new Result(Triage.Clean, lines[0].Trim(), [], warnings),
			1 => new Result(Triage.Conflict, lines[0].Trim(), lines.Skip(1).TakeWhile(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToList(), warnings),
			_ => throw new IntegrationException(
				$"git merge-tree failed in '{git.WorkingDirectory}' (the triage needs git 2.38 or later): {merge.Error.Trim()}")
		};
	}

	/// <summary>
	/// The merge commit git would make, as an unreferenced object: it lets the triage chain the next merge onto it.
	/// Nothing points at it, so git's garbage collection removes it in due course.
	/// </summary>
	public static Task<string> SimulateMergeAsync(GitClient git, string ours, string theirs, string tree) =>
		git.RunOrThrowAsync(["-c", "user.name=Bassia triage", "-c", "user.email=triage@bassia.invalid",
			"commit-tree", tree, "-p", ours, "-p", theirs, "-m", "bassia integration triage"]);

	public static async Task<bool> IsAncestorAsync(GitClient git, string ancestor, string descendant)
	{
		var result = await git.RunAsync(["merge-base", "--is-ancestor", ancestor, descendant]);
		return result.ExitCode switch
		{
			0 => true,
			1 => false,
			_ => throw new GitException($"git merge-base --is-ancestor failed in '{git.WorkingDirectory}': {result.Error.Trim()}")
		};
	}
}
