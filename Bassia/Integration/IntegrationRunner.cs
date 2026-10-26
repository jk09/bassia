namespace Bassia.Integration;

using Bassia.Core;
using Bassia.Git;
using Bassia.Storage;
using Tomlyn;
using Tomlyn.Model;

/// <summary>One progress step of an integration: what happened, and the record as it stands right after it.</summary>
internal sealed record IntegrationProgress(string Message, IntegrationRecord Snapshot, string? Component, string? RunId);

/// <summary>How a caller watches and interrupts an integration; without one it reports on stderr like <c>bassia agent start</c>.</summary>
internal sealed class IntegrationContext
{
	/// <summary>Stops the integration; a resolver that is working is killed with its process tree.</summary>
	public CancellationToken Cancellation { get; init; }

	public Action<IntegrationProgress>? OnProgress { get; init; }

	/// <summary>Receives the resolver's output line by line; without it the resolver inherits the console.</summary>
	public Action<string>? OnOutput { get; init; }
}

internal sealed record IntegrationOutcome(bool Ok, string Message, IntegrationRecord Record, IReadOnlyList<string> RecordTags);

/// <summary>
/// Executes an integration plan. Every component is integrated in its own checkout under
/// <c>.workspace/integration-&lt;key&gt;/</c>, on the branch <c>integration/&lt;key&gt;</c> started at the component's base:
/// first every step git can merge on its own or with the structural merge driver (oldest run first), then every step
/// that needs the resolver, each with the semantic brief on its stdin. The result is tagged <c>integration/&lt;key&gt;/&lt;n&gt;</c> - one identically named
/// annotated tag across the components, so the integration can be selected as the baseline of the next run - and
/// branch and tag are pushed to the component's source-of-truth repo. Nothing else there moves until
/// <see cref="AdvanceAsync"/> is asked to.
/// </summary>
internal static class IntegrationRunner
{
	/// <summary>The status of an integration that merged everything except the steps its merge policy left for a human.</summary>
	public const string NeedsAttentionStatus = "needs_attention";

	public static Task<IntegrationOutcome> RunAsync(
		GitClient git,
		Monorepo monorepo,
		IReadOnlyList<RunMetadata> runs,
		IReadOnlyList<ComponentIntegration> plan,
		string resolver,
		IntegrationContext? context = null,
		string? id = null,
		StructuralMerge? structural = null) =>
		RunAsync(monorepo.Graph(), IntegrationSettings.For(monorepo), new RunMetadataStore(git, monorepo.RunsRepoDir), runs, plan, resolver, context, id, structural);

	/// <summary>Executes <paramref name="plan"/> on <paramref name="graph"/>, recording the integration in <paramref name="records"/>.</summary>
	public static async Task<IntegrationOutcome> RunAsync(
		MonorepoGraph graph,
		IntegrationSettings settings,
		IRecordStore records,
		IReadOnlyList<RunMetadata> runs,
		IReadOnlyList<ComponentIntegration> plan,
		string resolver,
		IntegrationContext? context = null,
		string? id = null,
		StructuralMerge? structural = null)
	{
		var cancellation = context?.Cancellation ?? CancellationToken.None;
		if (plan.Count == 0)
		{
			throw new IntegrationException("The plan is empty; there is nothing to integrate.");
		}

		cancellation.ThrowIfCancellationRequested();
		var store = new IntegrationStore(records);
		await store.EnsureRepositoryAsync();

		id ??= IntegrationRecord.NewId();
		var draft = new IntegrationRecord
		{
			IntegrationId = id,
			Status = "started",
			Created = RunMetadata.Timestamp(),
			Resolver = resolver,
			StructuralDriver = structural?.Command,
			WorkspacePath = Path.Combine(settings.WorkspaceDir, id)
		};
		draft.Runs.AddRange(plan.SelectMany(component => component.Steps).Select(step => step.RunId).Distinct(StringComparer.Ordinal)
			.OrderBy(runId => runs.FirstOrDefault(run => run.RunId == runId)?.Created, StringComparer.Ordinal));
		draft.Components.AddRange(plan);

		// The plan belongs to the caller, which may keep showing it; the integration works on its own copy.
		var record = draft.Clone();
		var tags = new List<string> { await store.CommitAsync(record) };
		Directory.CreateDirectory(record.WorkspacePath);
		var session = new Session(graph, settings, record, runs, context, structural);
		session.Report($"{id}: recorded as {tags[0]}; integrating {record.Components.Count} component(s).");

		foreach (var component in record.Components)
		{
			if (session.Cancelled)
			{
				break;
			}

			await session.IntegrateAsync(component);
		}

		// Nothing is published before the gate (merge.verify) has passed on everything the integration affects.
		if (await session.VerifyAsync())
		{
			await session.PublishAsync();
		}

		record.Finished = RunMetadata.Timestamp();
		var failed = record.AllSteps.Any(step => step.Outcome == StepOutcome.Failed)
			|| record.Components.Any(component => component.ResultStatus is ResultStatus.Failed);
		var attention = record.AllSteps.Any(step => step.Outcome == StepOutcome.NeedsAttention);
		record.Status = session.Cancelled ? "cancelled" : failed ? "partial" : attention ? NeedsAttentionStatus : "completed";
		tags.Add(await store.CommitAsync(record));
		session.Report($"{id}: {record.Status}.");

		// merge.advance = auto: the components whose policy says so move their base branches at once.
		var message = Summary(record);
		var ok = record.Status is "completed" or NeedsAttentionStatus;
		if (ok && record.Components.Any(component => settings.MergePolicyFor(component.Name).AutoAdvance && component.ResultStatus == ResultStatus.Pushed))
		{
			var advanced = await AdvanceAsync(graph, records, record, component => settings.MergePolicyFor(component.Name).AutoAdvance);
			tags.AddRange(advanced.RecordTags);
			message += $" merge.advance = auto: {advanced.Message}";
			ok = advanced.Ok;
			session.Report(advanced.Message);
		}

		return new IntegrationOutcome(ok, message, record, tags);
	}

	internal static string Summary(IntegrationRecord record)
	{
		var steps = record.AllSteps.ToList();
		var merged = steps.Count(step => step.Outcome == StepOutcome.Merged && step.Strategy == MergeStrategy.Syntactic);
		var structured = steps.Count(step => step.Outcome == StepOutcome.Merged && step.Strategy == MergeStrategy.Structural);
		var resolved = steps.Count(step => step.Outcome == StepOutcome.Resolved);
		var failed = steps.Count(step => step.Outcome == StepOutcome.Failed);
		var manual = steps.Count(step => step.Outcome == StepOutcome.NeedsAttention);
		var tagged = record.Components.Where(component => component.ResultTag is not null && component.ResultStatus == ResultStatus.Pushed).ToList();
		var where = tagged.Count == 0
			? "no component changed"
			: $"{tagged[0].ResultTag} in {string.Join(", ", tagged.Select(component => component.Name))}";

		return record.Status switch
		{
			"completed" => $"Integration '{record.IntegrationId}' completed: {merged} result(s) merged by git, {Structured()}{resolved} resolved semantically; {where}.",
			_ when record.Verification.FirstOrDefault(result => !result.Ok) is { } failedTask =>
				$"Integration '{record.IntegrationId}' merged its results, but the verification failed: {failedTask.Component}:{failedTask.Task} exited " +
				$"{failedTask.ExitCode?.ToString() ?? "(cancelled)"} (log '{failedTask.LogPath}'); nothing was published. The workspace '{record.WorkspacePath}' is kept for inspection.",
			NeedsAttentionStatus => $"Integration '{record.IntegrationId}' merged {merged} result(s) by git, {Structured()}{resolved} semantically, and left {manual} for a human " +
				$"as the merge policy says; {where}. 'bassia integration show {IntegrationRecord.Key(record.IntegrationId)}' lists them.",
			"cancelled" => $"Integration '{record.IntegrationId}' was cancelled; {where}. The workspace '{record.WorkspacePath}' is kept for inspection.",
			_ => $"Integration '{record.IntegrationId}' finished with failures: {merged} merged, {Structured()}{resolved} resolved, {failed} failed; {where}. " +
				$"The workspace '{record.WorkspacePath}' and the briefs in it are kept for inspection."
		};

		// Only an integration that had the structural merge mentions it, so the summary reads as before without it.
		string Structured() => record.StructuralDriver is null && structured == 0 ? "" : $"{structured} merged structurally by weave, ";
	}

	/// <summary>
	/// Fast-forwards every integrated component's base branch to the integration result, but only if the branch has
	/// not moved since the integration started (a compare-and-swap on the ref). A base given as a tag is never moved.
	/// </summary>
	public static Task<IntegrationOutcome> AdvanceAsync(GitClient git, Monorepo monorepo, IntegrationRecord record, Func<ComponentIntegration, bool>? which = null) =>
		AdvanceAsync(monorepo.Graph(), new RunMetadataStore(git, monorepo.RunsRepoDir), record, which);

	public static async Task<IntegrationOutcome> AdvanceAsync(MonorepoGraph graph, IRecordStore records, IntegrationRecord record, Func<ComponentIntegration, bool>? which = null)
	{
		if (record.Status is "started" or "cancelled")
		{
			throw new IntegrationException($"Integration '{record.IntegrationId}' has status '{record.Status}'; only a finished integration can be advanced.");
		}

		var errors = new List<string>();
		var advanced = new List<string>();
		foreach (var component in record.Components.Where(component => component.ResultStatus == ResultStatus.Pushed && component.ResultCommit is not null && (which?.Invoke(component) ?? true)))
		{
			if (component.Advanced)
			{
				continue;
			}

			var source = graph.Storage.Exists(component.Name) ? graph.Storage.Open(component.Name) : null;
			if (source is null || await source.BranchTipAsync(component.BaseRef) is null)
			{
				errors.Add($"'{component.Name}': the base '{component.BaseRef}' is not a branch; select the result as {component.Name}@{component.ResultTag} instead");
				continue;
			}

			try
			{
				await source.SetBranchAsync(component.BaseRef, component.ResultCommit!, component.BaseCommit);
			}
			catch (StorageException)
			{
				errors.Add($"'{component.Name}': '{component.BaseRef}' moved since the integration was built on {component.BaseCommit[..7]}; integrate again onto it");
				continue;
			}

			component.Advanced = true;
			advanced.Add($"{component.Name}:{component.BaseRef}");
		}

		if (advanced.Count == 0 && errors.Count == 0)
		{
			return new IntegrationOutcome(true, $"Integration '{record.IntegrationId}' has nothing left to advance.", record, []);
		}

		var store = new IntegrationStore(records);
		var tag = advanced.Count > 0 ? await store.CommitAsync(record) : null;
		var message = errors.Count == 0
			? $"Advanced {string.Join(", ", advanced)} to integration '{record.IntegrationId}'."
			: $"{(advanced.Count > 0 ? $"Advanced {string.Join(", ", advanced)}; " : "")}not advanced: {string.Join("; ", errors)}.";
		return new IntegrationOutcome(errors.Count == 0, message, record, tag is null ? [] : [tag]);
	}

	/// <summary>The state of one executing integration, so the per-component and per-step code shares it.</summary>
	private sealed class Session(MonorepoGraph graph, IntegrationSettings settings, IntegrationRecord record, IReadOnlyList<RunMetadata> runs, IntegrationContext? context, StructuralMerge? structural)
	{
		private CancellationToken Cancellation => context?.Cancellation ?? CancellationToken.None;

		public bool Cancelled { get; private set; }

		/// <summary>The checkouts of the components merged and waiting to be published.</summary>
		private readonly Dictionary<string, IComponentWorkspace> merged = new(StringComparer.Ordinal);

		public void Report(string message, string? component = null, string? runId = null)
		{
			if (context?.OnProgress is { } onProgress)
			{
				onProgress(new IntegrationProgress(message, record.Clone(), component, runId));
			}
			else
			{
				Console.Error.WriteLine($"bassia: {message}");
			}
		}

		public async Task IntegrateAsync(ComponentIntegration component)
		{
			if (component.Steps.All(step => step.Strategy is MergeStrategy.Skip or MergeStrategy.Manual))
			{
				Settle(component);
				Report($"'{component.Name}': every result is skipped or left for a human; nothing to integrate.", component.Name);
				return;
			}

			component.Branch = IntegrationRecord.RefBase(record.IntegrationId);
			component.Path = Path.Combine(record.WorkspacePath, component.Name);
			var sourceDir = graph.Storage.LocationOf(component.Name);

			try
			{
				var checkout = await graph.Storage.Open(component.Name).CheckoutAsync(component.BaseCommit, component.Path, component.Branch);
				Report($"'{component.Name}': integrating onto {component.BaseRef} ({component.BaseCommit[..7]}) in '{component.Path}'.", component.Name);

				// Deterministic steps first; one git or weave cannot merge after all is demoted to the back of the semantic queue.
				var executed = new List<IntegrationStep>();
				var semantic = component.Steps.Where(step => step.Strategy == MergeStrategy.Semantic).ToList();
				foreach (var step in component.Steps.Where(step => step.IsDeterministic))
				{
					if (Cancelled || Cancellation.IsCancellationRequested)
					{
						Cancelled = true;
						break;
					}

					if (await MergeSyntacticallyAsync(checkout, component, step))
					{
						executed.Add(step);
					}
					else
					{
						semantic.Add(step);
					}
				}

				foreach (var step in semantic)
				{
					if (Cancelled || Cancellation.IsCancellationRequested)
					{
						Cancelled = true;
						break;
					}

					await MergeSemanticallyAsync(checkout, component, step, executed);
					executed.Add(step);
				}

				// The record lists the steps in the order they actually ran; the ones never reached stay pending.
				var remaining = component.Steps.Where(step => !executed.Contains(step)).ToList();
				component.Steps.Clear();
				component.Steps.AddRange([.. executed, .. remaining]);
				Settle(component);

				if (Cancelled)
				{
					Report($"'{component.Name}': cancelled; nothing of it was published.", component.Name);
					return;
				}

				merged[component.Name] = checkout;
			}
			catch (Exception ex)
			{
				// Whatever goes wrong in one component (git, the file system, a resolver that cannot start) is that
				// component's failure: it must never leave the integration unrecorded, stuck at 'started' while other
				// components were already pushed.
				component.ResultStatus = ResultStatus.Failed;
				component.ResultError = ex.Message;
				Report($"'{component.Name}' failed: {ex.Message}", component.Name);
			}
		}

		/// <summary>
		/// Skipped steps are recorded as such, manual ones as needing attention; the only pending steps left are the ones
		/// a cancellation cut off.
		/// </summary>
		private static void Settle(ComponentIntegration component)
		{
			foreach (var step in component.Steps.Where(step => step.Strategy == MergeStrategy.Skip))
			{
				step.Outcome = StepOutcome.Skipped;
			}

			foreach (var step in component.Steps.Where(step => step.Strategy == MergeStrategy.Manual))
			{
				step.Outcome = StepOutcome.NeedsAttention;
			}

			if (component.Steps.All(step => step.Outcome is StepOutcome.Skipped or StepOutcome.NeedsAttention))
			{
				component.ResultStatus = ResultStatus.Unchanged;
			}
		}

		/// <summary>A syntactic step is merged by git alone; a structural one by git with the structural merge driver.</summary>
		/// <returns>
		/// False when the merge stopped on a conflict after all, or the structural merge warned: the step then goes to the
		/// resolver.
		/// </returns>
		private async Task<bool> MergeSyntacticallyAsync(IComponentWorkspace checkout, ComponentIntegration component, IntegrationStep step)
		{
			var driver = step.Strategy == MergeStrategy.Structural ? structural : null;
			var head = await checkout.HeadAsync();
			if (await checkout.ContainsAsync(step.SourceCommit))
			{
				step.Outcome = StepOutcome.UpToDate;
				step.Commit = head;
				Report($"'{component.Name}': run {Short(step)} is already contained; nothing to merge.", component.Name, step.RunId);
				return true;
			}

			if (step.Strategy == MergeStrategy.Structural && driver is null)
			{
				// Planned with weave, executed without it (an integration started from an edited plan): the resolver decides.
				step.Strategy = MergeStrategy.Semantic;
				step.Note = "planned for the structural merge, which this integration does not have; handed to the resolver";
				Report($"'{component.Name}': run {Short(step)} needs the structural merge, which is not enabled; it moves to the semantic queue.", component.Name, step.RunId);
				return false;
			}

			Report(driver is null
					? $"'{component.Name}': merging run {Short(step)} with git."
					: $"'{component.Name}': merging run {Short(step)} with git and the structural merge driver ({driver.Command}).",
				component.Name, step.RunId);
			var merge = await checkout.MergeAsync(step.SourceCommit, new WorkspaceMerge(MergeCommitMessage(component, step), Driver: driver?.Driver));
			if (merge.Status != MergeStatus.Merged)
			{
				// Only a conflict is the resolver's business. Anything else (no git identity, a failing hook, a locked
				// index) would fail its merge commit just the same, so it fails the step with the store's own message.
				var conflictedFiles = merge.Conflicts.ToList();
				var conflicted = merge.Status == MergeStatus.Conflicted && conflictedFiles.Count > 0;
				await checkout.ResetAsync(head);
				if (!conflicted)
				{
					Fail(component, step, $"the merge failed: {merge.Error}");
					return true;
				}

				step.Triage = Triage.Conflict;
				var policy = settings.MergePolicyFor(component.Name);
				var pathReason = policy.ManualPathReason(conflictedFiles);
				if (pathReason is not null || policy.ManualSemantic)
				{
					step.Strategy = MergeStrategy.Manual;
					step.Outcome = StepOutcome.NeedsAttention;
					step.Conflicts.Clear();
					step.Conflicts.AddRange(conflictedFiles);
					step.Note = pathReason ?? "it conflicts onto the steps before it, and merge.semantic = manual leaves that for a human";
					Report($"'{component.Name}': run {Short(step)} conflicts after all; it is left for a human.", component.Name, step.RunId);
					return true;
				}

				step.Strategy = MergeStrategy.Semantic;
				step.Note = driver is null
					? "git could not merge it onto the steps before it; handed to the resolver"
					: "git and the structural merge could not merge it onto the steps before it; handed to the resolver";
				Report($"'{component.Name}': run {Short(step)} conflicts after all; it moves to the semantic queue.", component.Name, step.RunId);
				return false;
			}

			var warningsPolicy = settings.MergePolicyFor(component.Name).Warnings;
			if (driver is not null && warningsPolicy != WarningsPolicy.Accept && merge.Warnings is { Count: > 0 } warnings)
			{
				// Clean, but weave doubts it means what both sides meant: like the triage, leave that to the resolver -
				// or to a human, when merge.warnings says so.
				await checkout.ResetAsync(head);
				if (warningsPolicy == WarningsPolicy.Manual)
				{
					step.Strategy = MergeStrategy.Manual;
					step.Outcome = StepOutcome.NeedsAttention;
					step.Structural = StructuralTriage.Warnings;
					step.StructuralWarnings.Clear();
					step.StructuralWarnings.AddRange(warnings);
					step.Note = "weave merged it with warnings, which merge.warnings = manual leaves for a human";
					Report($"'{component.Name}': run {Short(step)} merged structurally with {warnings.Count} warning(s); it is left for a human.", component.Name, step.RunId);
					return true;
				}

				step.Strategy = MergeStrategy.Semantic;
				step.Structural = StructuralTriage.Warnings;
				step.StructuralWarnings.Clear();
				step.StructuralWarnings.AddRange(warnings);
				step.Note = "the structural merge completed it with warnings; handed to the resolver for review";
				Report($"'{component.Name}': run {Short(step)} merged structurally with {warnings.Count} warning(s); it moves to the semantic queue.", component.Name, step.RunId);
				return false;
			}

			step.Outcome = StepOutcome.Merged;
			step.Commit = await checkout.HeadAsync();
			Report($"'{component.Name}': run {Short(step)} merged {(driver is null ? "by git" : "structurally by weave")} as {step.Commit[..7]}.", component.Name, step.RunId);
			return true;
		}

		private async Task MergeSemanticallyAsync(IComponentWorkspace checkout, ComponentIntegration component, IntegrationStep step, IReadOnlyList<IntegrationStep> before)
		{
			var head = await checkout.HeadAsync();
			if (await checkout.ContainsAsync(step.SourceCommit))
			{
				step.Outcome = StepOutcome.UpToDate;
				step.Commit = head;
				return;
			}

			// With the structural merge enabled, the merge starts with weave as the driver, so the resolver only gets what
			// weave could not merge, in entity-labelled conflicts. What git alone conflicts on is probed first, so the
			// record and the brief show both: the head may be a merge of this integration that only the working copy has, so
			// the probe is a merge in the working copy, undone at once.
			if (structural is not null)
			{
				var probe = await checkout.MergeAsync(step.SourceCommit, new WorkspaceMerge("", Commit: false));
				await checkout.ResetAsync(head);
				step.Conflicts.Clear();
				step.Conflicts.AddRange(probe.Conflicts);
			}

			// diff3 puts the common ancestor into every conflict the store's own merge leaves, so the resolver sees what
			// each side changed, not just where they ended up.
			var merge = await checkout.MergeAsync(step.SourceCommit, new WorkspaceMerge("", Commit: false, Diff3: true, Driver: structural?.Driver));
			if (merge.Status == MergeStatus.Failed || !await checkout.MergeInProgressAsync())
			{
				await checkout.ResetAsync(head);
				Fail(component, step, $"the merge could not start: {merge.Error}");
				return;
			}

			var conflicts = merge.Conflicts.ToList();
			if (structural is null)
			{
				step.Conflicts.Clear();
				step.Conflicts.AddRange(conflicts);
			}
			else if (step.Conflicts.Count > 0)
			{
				// What the structural merge made of git's conflicts, as it is in the working tree now.
				var warnings = merge.Warnings;
				step.StructuralConflicts.Clear();
				step.StructuralConflicts.AddRange(conflicts);
				step.StructuralWarnings.Clear();
				step.StructuralWarnings.AddRange(warnings);
				step.Structural = conflicts.Count > 0 ? StructuralTriage.Conflict : warnings.Count > 0 ? StructuralTriage.Warnings : StructuralTriage.Clean;
			}

			var briefText = MergeBrief.Render(await BriefAsync(checkout, component, step, before, head, conflicts));
			int? exitCode;
			try
			{
				step.Brief = Path.Combine(record.WorkspacePath, $"{component.Name}.{Short(step)}.merge.md");
				await File.WriteAllTextAsync(step.Brief, briefText);
				Report(conflicts.Count == 0
						? $"'{component.Name}': asking the resolver to review run {Short(step)} semantically{(step.StructuralWarnings.Count > 0 ? $" ({step.StructuralWarnings.Count} structural merge warning(s))" : "")}."
						: $"'{component.Name}': asking the resolver to merge run {Short(step)} ({conflicts.Count} conflicted file(s)).",
					component.Name, step.RunId);

				exitCode = await settings.RunResolver(new ResolverRequest(record.Resolver, component.Path!,
					new Dictionary<string, string>
					{
						["BASSIA_ROOT"] = settings.Root,
						["BASSIA_INTEGRATION_ID"] = record.IntegrationId,
						["BASSIA_COMPONENT"] = component.Name,
						["BASSIA_RUN_ID"] = step.RunId,
						["BASSIA_MERGE_BRIEF"] = step.Brief
					},
					briefText, context?.OnOutput, Cancellation));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				// The brief could not be written or the resolver could not be started: this step fails, the rest go on.
				await checkout.ResetAsync(head);
				Fail(component, step, $"the resolver could not be run: {ex.Message}");
				return;
			}

			if (exitCode is null)
			{
				await checkout.ResetAsync(head);
				step.Note = "cancelled while the resolver was working";
				Cancelled = true;
				return;
			}

			if (exitCode != 0)
			{
				await checkout.ResetAsync(head);
				Fail(component, step, $"the resolver exited with code {exitCode}");
				return;
			}

			if (!await checkout.MergeInProgressAsync())
			{
				// The resolver committed on its own: fine, as long as the result really is in the history now.
				var now = await checkout.HeadAsync();
				if (now != head && await checkout.ContainsAsync(step.SourceCommit))
				{
					step.Outcome = StepOutcome.Resolved;
					step.Commit = now;
					step.Note = "the resolver committed the merge itself";
					Report($"'{component.Name}': run {Short(step)} resolved as {now[..7]}.", component.Name, step.RunId);
				}
				else
				{
					await checkout.ResetAsync(head);
					Fail(component, step, "the resolver abandoned the merge");
				}

				return;
			}

			var unresolved = conflicts.Concat(step.Conflicts).Distinct(StringComparer.Ordinal)
				.Where(file => HasConflictMarkers(Path.Combine(component.Path!, file))).ToList();
			if (unresolved.Count > 0)
			{
				await checkout.ResetAsync(head);
				Fail(component, step, $"conflicts remain in {string.Join(", ", unresolved)}");
				return;
			}

			step.Commit = await checkout.CommitAsync(MergeCommitMessage(component, step));
			step.Outcome = StepOutcome.Resolved;
			Report($"'{component.Name}': run {Short(step)} resolved semantically as {step.Commit[..7]}.", component.Name, step.RunId);
		}

		private async Task<MergeBrief.Input> BriefAsync(IComponentWorkspace checkout, ComponentIntegration component, IntegrationStep step,
			IReadOnlyList<IntegrationStep> before, string head, IReadOnlyList<string> conflicts)
		{
			var range = await checkout.MergeBaseAsync(head, step.SourceCommit) ?? component.BaseCommit;

			async Task<MergeBrief.Side> SideAsync(string title, string tip, IReadOnlyList<string> rationales) => new(
				title,
				rationales,
				string.Concat((await checkout.LogAsync(range, tip)).Select(commit => $"commit {commit.Id[..7]}\n{commit.Message.TrimEnd('\n')}\n\n")),
				await checkout.DiffAsync(range, tip));

			var integrated = before.Where(previous => previous.Outcome is StepOutcome.Merged or StepOutcome.Resolved)
				.Select(previous => $"run `{previous.RunId}` - {previous.Rationale}").ToList();

			return new MergeBrief.Input(
				record.IntegrationId,
				component.Name,
				component.BaseRef,
				component.BaseCommit,
				step,
				runs.FirstOrDefault(run => run.RunId == step.RunId),
				conflicts,
				step.ConflictsWith,
				structural is null ? null : new MergeBrief.StructuralInfo(structural.Command, step.Conflicts, step.StructuralWarnings),
				await SideAsync("Ours", head, integrated),
				await SideAsync("Theirs", step.SourceCommit, [step.Rationale]));
		}

		/// <summary>Publishes every merged component (see <see cref="PublishAsync(IComponentWorkspace, ComponentIntegration, string)"/>).</summary>
		public async Task PublishAsync()
		{
			foreach (var component in record.Components.Where(component => merged.ContainsKey(component.Name)))
			{
				try
				{
					await PublishAsync(merged[component.Name], component, graph.Storage.LocationOf(component.Name));
				}
				catch (Exception ex)
				{
					component.ResultStatus = ResultStatus.Failed;
					component.ResultError = ex.Message;
					Report($"'{component.Name}' failed: {ex.Message}", component.Name);
				}
			}
		}

		/// <summary>
		/// The gate: when a component the integration affects - one it changed or one referencing those, transitively - has
		/// <c>merge.verify</c> tasks, the base tips of the affected components it did not change are checked out next to
		/// the merged ones, the references are nested as in a run, and the tasks run (with what they depend on) in
		/// dependency order. False when one fails: every merged component is then failed and nothing is published. A
		/// cancelled integration with a gate publishes nothing either.
		/// </summary>
		public async Task<bool> VerifyAsync()
		{
			var changed = new List<string>();
			foreach (var (name, checkout) in merged)
			{
				if (await checkout.HeadAsync() != record.Components.First(component => component.Name == name).BaseCommit)
				{
					changed.Add(name);
				}
			}

			var registry = graph.Registry;
			var affected = Affected.Of(registry, changed.Where(registry.Contains));
			var gated = affected.Where(component => settings.MergePolicyFor(component.Name).VerifyTasks.Count > 0).ToList();
			if (gated.Count == 0)
			{
				return true;
			}

			if (Cancelled)
			{
				merged.Clear();
				return false;
			}

			try
			{
				var closure = registry.Closure(affected.Select(component => component.Name));
				var folders = new Dictionary<string, string>(StringComparer.Ordinal);
				var workspaces = new Dictionary<string, IComponentWorkspace>(StringComparer.Ordinal);
				var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
				foreach (var component in closure)
				{
					folders[component.Name] = Path.Combine(record.WorkspacePath, component.Name);
					if (merged.TryGetValue(component.Name, out var checkout))
					{
						workspaces[component.Name] = checkout;
						fingerprints[component.Name] = $"commit:{await checkout.HeadAsync()}";
						continue;
					}

					var store = graph.Storage.Open(component.Name);
					var tip = await store.DefaultBranchAsync() is { } branch ? await store.BranchTipAsync(branch) : null;
					workspaces[component.Name] = await store.CheckoutAsync(tip ?? throw new IntegrationException($"Component '{component.Name}' has no main branch to verify against."),
						folders[component.Name], IntegrationRecord.RefBase(record.IntegrationId) + "-verify");
					fingerprints[component.Name] = $"commit:{tip}";
				}

				await Workspaces.NestAsync(closure, folders, workspaces);
				var tasks = closure.ToDictionary(component => component.Name, component => TaskFile.Read(folders[component.Name], component.Name), StringComparer.Ordinal);
				var plan = new List<PlannedTask>();
				foreach (var component in gated)
				{
					var targets = settings.MergePolicyFor(component.Name).VerifyTasks.Where(tasks[component.Name].ContainsKey).ToList();
					if (targets.Count > 0)
					{
						plan.AddRange(TaskPlan.Plan(registry, [component.Name], targets, tasks).Where(task => plan.All(planned => planned.Id != task.Id)));
					}
				}

				Report($"Verifying {string.Join(", ", gated.Select(component => component.Name))}: {(plan.Count == 0 ? "no task defined" : string.Join(", ", plan.Select(task => task.Id)))}.");
				var results = await TaskRunner.RunAsync(plan, record.WorkspacePath, Path.Combine(record.WorkspacePath, ".verify-logs"),
					new Dictionary<string, string> { ["BASSIA_ROOT"] = settings.Root, ["BASSIA_INTEGRATION_ID"] = record.IntegrationId },
					settings.RunTask, result => Report(result.Cached
						? $"verify {result.Component}:{result.Task} restored from the cache."
						: $"verify {result.Component}:{result.Task} exited {result.ExitCode?.ToString() ?? "(cancelled)"} in {result.Seconds}s."),
					Cancellation, settings.TaskCache is { } cache ? new TaskCaching(cache, registry, fingerprints) : null);
				record.Verification.AddRange(results);
				if (results.FirstOrDefault(result => !result.Ok) is not { } failed)
				{
					return true;
				}

				FailMerged($"verification failed: {failed.Component}:{failed.Task} exited {failed.ExitCode?.ToString() ?? "(cancelled)"} (log '{failed.LogPath}')");
				return false;
			}
			catch (Exception ex) when (ex is MonorepoException or IntegrationException or StorageException)
			{
				FailMerged($"verification could not run: {ex.Message}");
				return false;
			}
		}

		private void FailMerged(string reason)
		{
			foreach (var component in record.Components.Where(component => merged.ContainsKey(component.Name)))
			{
				component.ResultStatus = ResultStatus.Failed;
				component.ResultError = reason;
			}

			Report($"Nothing published: {reason}.");
			merged.Clear();
		}

		private async Task PublishAsync(IComponentWorkspace checkout, ComponentIntegration component, string sourceDir)
		{
			var head = await checkout.HeadAsync();
			if (head == component.BaseCommit)
			{
				component.ResultStatus = ResultStatus.Unchanged;
				Report($"'{component.Name}': nothing new on top of {component.BaseRef}; no tag.", component.Name);
				return;
			}

			component.ResultCommit = head;
			component.ResultStatus = ResultStatus.Committed;
			var existing = await checkout.ListTagsAsync($"{component.Branch}/");
			var tag = component.ResultTag = $"{component.Branch}/{RunMetadata.HighestIndex(existing) + 1}";
			var integrated = component.Steps.Count(step => step.Outcome is StepOutcome.Merged or StepOutcome.Resolved);
			await checkout.TagAsync(tag, head,
				$"{record.IntegrationId}: {integrated} agentic run result(s) integrated into '{component.Name}' onto {component.BaseRef}");
			await checkout.PublishAsync(component.Branch, [tag]);
			component.ResultStatus = ResultStatus.Pushed;
			Report($"'{component.Name}': integrated as {head[..7]}, tagged {tag}, pushed to '{sourceDir}'.", component.Name);
		}

		private void Fail(ComponentIntegration component, IntegrationStep step, string reason)
		{
			step.Outcome = StepOutcome.Failed;
			step.Note = reason;
			Report($"'{component.Name}': run {Short(step)} was not integrated: {reason}.", component.Name, step.RunId);
		}

		/// <summary>
		/// Subject plus a TOML record, like a run's result commit, so the component's history says which run each
		/// merge brought in, why, and whether git or the resolver made it.
		/// </summary>
		private string MergeCommitMessage(ComponentIntegration component, IntegrationStep step)
		{
			var table = new TomlTable
			{
				["id"] = record.IntegrationId,
				["component"] = component.Name,
				["strategy"] = IntegrationRecord.Snake(step.Strategy),
				["run_id"] = step.RunId,
				["source_tag"] = step.SourceTag,
				["source_commit"] = step.SourceCommit,
				["rationale"] = step.Rationale
			};
			if (step.Strategy == MergeStrategy.Structural)
			{
				table["conflicts"] = Array(step.Conflicts);
				table["structural_driver"] = structural?.Command ?? "";
			}

			if (step.IsSemantic)
			{
				var conflicts = new TomlArray();
				foreach (var file in step.Conflicts)
				{
					conflicts.Add(file);
				}

				table["conflicts"] = conflicts;
				if (step.Structural is not null)
				{
					table["structural_driver"] = structural?.Command ?? "";
					table["structural_conflicts"] = Array(step.StructuralConflicts);
					if (step.StructuralWarnings.Count > 0)
					{
						table["structural_warnings"] = Array(step.StructuralWarnings);
					}
				}

				table["resolver"] = record.Resolver;
			}

			var subject = $"integrate({IntegrationRecord.Key(record.IntegrationId)}): {Short(step)} {step.Rationale}";
			return $"{subject}\n\n{TomlSerializer.Serialize(new TomlTable { ["integration"] = table })}";
		}

		private static string Short(IntegrationStep step) => RunMetadata.Key(step.RunId);

		private static TomlArray Array(IEnumerable<string> values)
		{
			var array = new TomlArray();
			foreach (var value in values)
			{
				array.Add(value);
			}

			return array;
		}
	}

	/// <summary>Conflict markers left in a file, or the pointer to <c>weave explain</c> the structural merge adds to a conflicted one.</summary>
	private static bool HasConflictMarkers(string path) =>
		File.Exists(path) && File.ReadLines(path).Any(line =>
			line.StartsWith("<<<<<<<", StringComparison.Ordinal) || line.StartsWith(">>>>>>>", StringComparison.Ordinal) || StructuralMerge.IsAnnotation(line));
}
