namespace Bassia.Integration;

using Bassia.Git;
using Tomlyn;
using Tomlyn.Model;

/// <summary>One progress step of an integration: what happened, and the record as it stands right after it.</summary>
internal sealed record IntegrationProgress(string Message, IntegrationRecord Snapshot, string? Component, string? RunId);

/// <summary>How a caller watches and interrupts an integration; without one it reports on stderr like <c>bassia agent</c>.</summary>
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
/// <c>.workspace/integration-&lt;id&gt;/</c>, on the branch <c>integration/&lt;id&gt;</c> started at the component's base:
/// first every step git can merge on its own (oldest run first), then every step that needs the resolver, each with
/// the semantic brief on its stdin. The result is tagged <c>integration/&lt;id&gt;/&lt;n&gt;</c> - one identically named
/// annotated tag across the components, so the integration can be selected as the baseline of the next run - and
/// branch and tag are pushed to the component's source-of-truth repo. Nothing else there moves until
/// <see cref="AdvanceAsync"/> is asked to.
/// </summary>
internal static class IntegrationRunner
{
	public static async Task<IntegrationOutcome> RunAsync(
		GitClient git,
		Monorepo monorepo,
		IReadOnlyList<RunMetadata> runs,
		IReadOnlyList<ComponentIntegration> plan,
		string resolver,
		IntegrationContext? context = null)
	{
		var cancellation = context?.Cancellation ?? CancellationToken.None;
		if (plan.Count == 0)
		{
			throw new IntegrationException("The plan is empty; there is nothing to integrate.");
		}

		cancellation.ThrowIfCancellationRequested();
		var store = new IntegrationStore(new RunMetadataStore(git, monorepo.RunsRepoDir));
		await store.EnsureRepositoryAsync();

		var id = IntegrationRecord.NewId();
		var draft = new IntegrationRecord
		{
			IntegrationId = id,
			Status = "started",
			Created = RunMetadata.Timestamp(),
			Resolver = resolver,
			WorkspacePath = Path.Combine(monorepo.WorkspaceDir, id)
		};
		draft.Runs.AddRange(plan.SelectMany(component => component.Steps).Select(step => step.RunId).Distinct(StringComparer.Ordinal)
			.OrderBy(runId => runs.FirstOrDefault(run => run.RunId == runId)?.Created, StringComparer.Ordinal));
		draft.Components.AddRange(plan);

		// The plan belongs to the caller (the frontend keeps drawing it); the integration works on its own copy.
		var record = draft.Clone();
		var tags = new List<string> { await store.CommitAsync(record) };
		Directory.CreateDirectory(record.WorkspacePath);
		var session = new Session(monorepo, record, runs, context);
		session.Report($"{id}: recorded as {tags[0]}; integrating {record.Components.Count} component(s).");

		foreach (var component in record.Components)
		{
			if (session.Cancelled)
			{
				break;
			}

			await session.IntegrateAsync(component);
		}

		record.Finished = RunMetadata.Timestamp();
		var failed = record.AllSteps.Any(step => step.Outcome == StepOutcome.Failed)
			|| record.Components.Any(component => component.ResultStatus is ResultStatus.Failed);
		record.Status = session.Cancelled ? "cancelled" : failed ? "partial" : "completed";
		tags.Add(await store.CommitAsync(record));
		session.Report($"{id}: {record.Status}.");

		return new IntegrationOutcome(record.Status == "completed", Summary(record), record, tags);
	}

	internal static string Summary(IntegrationRecord record)
	{
		var steps = record.AllSteps.ToList();
		var merged = steps.Count(step => step.Outcome == StepOutcome.Merged);
		var resolved = steps.Count(step => step.Outcome == StepOutcome.Resolved);
		var failed = steps.Count(step => step.Outcome == StepOutcome.Failed);
		var tagged = record.Components.Where(component => component.ResultTag is not null && component.ResultStatus == ResultStatus.Pushed).ToList();
		var where = tagged.Count == 0
			? "no component changed"
			: $"{tagged[0].ResultTag} in {string.Join(", ", tagged.Select(component => component.Name))}";

		return record.Status switch
		{
			"completed" => $"Integration '{record.IntegrationId}' completed: {merged} result(s) merged by git, {resolved} resolved semantically; {where}.",
			"cancelled" => $"Integration '{record.IntegrationId}' was cancelled; {where}. The workspace '{record.WorkspacePath}' is kept for inspection.",
			_ => $"Integration '{record.IntegrationId}' finished with failures: {merged} merged, {resolved} resolved, {failed} failed; {where}. " +
				$"The workspace '{record.WorkspacePath}' and the briefs in it are kept for inspection."
		};
	}

	/// <summary>
	/// Fast-forwards every integrated component's base branch to the integration result, but only if the branch has
	/// not moved since the integration started (a compare-and-swap on the ref). A base given as a tag is never moved.
	/// </summary>
	public static async Task<IntegrationOutcome> AdvanceAsync(GitClient git, Monorepo monorepo, IntegrationRecord record)
	{
		if (record.Status is "started" or "cancelled")
		{
			throw new IntegrationException($"Integration '{record.IntegrationId}' has status '{record.Status}'; only a finished integration can be advanced.");
		}

		var errors = new List<string>();
		var advanced = new List<string>();
		foreach (var component in record.Components.Where(component => component.ResultStatus == ResultStatus.Pushed && component.ResultCommit is not null))
		{
			if (component.Advanced)
			{
				continue;
			}

			var source = GitClient.In(monorepo.SourceRepoDir(component.Name));
			var branch = await source.RunAsync(["show-ref", "--verify", "--quiet", $"refs/heads/{component.BaseRef}"]);
			if (branch.ExitCode != 0)
			{
				errors.Add($"'{component.Name}': the base '{component.BaseRef}' is not a branch; select the result as {component.Name}@{component.ResultTag} instead");
				continue;
			}

			var update = await source.RunAsync(["update-ref", "-m", $"bassia integrate advance {record.IntegrationId}",
				$"refs/heads/{component.BaseRef}", component.ResultCommit!, component.BaseCommit]);
			if (update.ExitCode != 0)
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

		var store = new IntegrationStore(new RunMetadataStore(git, monorepo.RunsRepoDir));
		var tag = advanced.Count > 0 ? await store.CommitAsync(record) : null;
		var message = errors.Count == 0
			? $"Advanced {string.Join(", ", advanced)} to integration '{record.IntegrationId}'."
			: $"{(advanced.Count > 0 ? $"Advanced {string.Join(", ", advanced)}; " : "")}not advanced: {string.Join("; ", errors)}.";
		return new IntegrationOutcome(errors.Count == 0, message, record, tag is null ? [] : [tag]);
	}

	/// <summary>The state of one executing integration, so the per-component and per-step code shares it.</summary>
	private sealed class Session(Monorepo monorepo, IntegrationRecord record, IReadOnlyList<RunMetadata> runs, IntegrationContext? context)
	{
		private CancellationToken Cancellation => context?.Cancellation ?? CancellationToken.None;

		public bool Cancelled { get; private set; }

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
			if (component.Steps.All(step => step.Strategy == MergeStrategy.Skip))
			{
				Settle(component);
				Report($"'{component.Name}': every result is skipped; nothing to integrate.", component.Name);
				return;
			}

			component.Branch = IntegrationRecord.RefBase(record.IntegrationId);
			component.Path = Path.Combine(record.WorkspacePath, component.Name);
			var sourceDir = monorepo.SourceRepoDir(component.Name);

			try
			{
				await GitClient.In(record.WorkspacePath).RunOrThrowAsync(["clone", "--quiet", "--no-checkout", sourceDir, component.Path]);
				var checkout = GitClient.In(component.Path);
				await checkout.RunOrThrowAsync(["checkout", "--quiet", "-b", component.Branch, component.BaseCommit]);
				Report($"'{component.Name}': integrating onto {component.BaseRef} ({component.BaseCommit[..7]}) in '{component.Path}'.", component.Name);

				// Syntactic steps first; one git cannot merge after all is demoted to the back of the semantic queue.
				var executed = new List<IntegrationStep>();
				var semantic = component.Steps.Where(step => step.Strategy == MergeStrategy.Semantic).ToList();
				foreach (var step in component.Steps.Where(step => step.Strategy == MergeStrategy.Syntactic))
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

				await PublishAsync(checkout, component, sourceDir);
			}
			catch (GitException ex)
			{
				component.ResultStatus = ResultStatus.Failed;
				component.ResultError = ex.Message;
				Report($"'{component.Name}' failed: {ex.Message}", component.Name);
			}
		}

		/// <summary>Skipped steps are recorded as such; the only pending steps left are the ones a cancellation cut off.</summary>
		private static void Settle(ComponentIntegration component)
		{
			foreach (var step in component.Steps.Where(step => step.Strategy == MergeStrategy.Skip))
			{
				step.Outcome = StepOutcome.Skipped;
			}

			if (component.Steps.All(step => step.Outcome is StepOutcome.Skipped))
			{
				component.ResultStatus = ResultStatus.Unchanged;
			}
		}

		/// <returns>False when git stopped on a conflict after all: the step then goes to the resolver.</returns>
		private async Task<bool> MergeSyntacticallyAsync(GitClient checkout, ComponentIntegration component, IntegrationStep step)
		{
			var head = await checkout.RunOrThrowAsync(["rev-parse", "HEAD"]);
			if (await MergeProbe.IsAncestorAsync(checkout, step.SourceCommit, head))
			{
				step.Outcome = StepOutcome.UpToDate;
				step.Commit = head;
				Report($"'{component.Name}': run {Short(step)} is already contained; nothing to merge.", component.Name, step.RunId);
				return true;
			}

			Report($"'{component.Name}': merging run {Short(step)} with git.", component.Name, step.RunId);
			var merge = await checkout.RunAsync(["merge", "--quiet", "--no-ff", "--no-edit", "-m", MergeCommitMessage(component, step), step.SourceCommit]);
			if (merge.ExitCode != 0)
			{
				await AbortMergeAsync(checkout);
				step.Strategy = MergeStrategy.Semantic;
				step.Triage = Triage.Conflict;
				step.Note = "git could not merge it onto the steps before it; handed to the resolver";
				Report($"'{component.Name}': run {Short(step)} conflicts after all; it moves to the semantic queue.", component.Name, step.RunId);
				return false;
			}

			step.Outcome = StepOutcome.Merged;
			step.Commit = await checkout.RunOrThrowAsync(["rev-parse", "HEAD"]);
			Report($"'{component.Name}': run {Short(step)} merged by git as {step.Commit[..7]}.", component.Name, step.RunId);
			return true;
		}

		private async Task MergeSemanticallyAsync(GitClient checkout, ComponentIntegration component, IntegrationStep step, IReadOnlyList<IntegrationStep> before)
		{
			var head = await checkout.RunOrThrowAsync(["rev-parse", "HEAD"]);
			if (await MergeProbe.IsAncestorAsync(checkout, step.SourceCommit, head))
			{
				step.Outcome = StepOutcome.UpToDate;
				step.Commit = head;
				return;
			}

			// diff3 puts the common ancestor into every conflict, so the resolver sees what each side changed, not
			// just where they ended up.
			var merge = await checkout.RunAsync(["-c", "merge.conflictStyle=diff3", "merge", "--quiet", "--no-ff", "--no-commit", step.SourceCommit]);
			if (merge.ExitCode is not (0 or 1) || !await MergeInProgressAsync(checkout))
			{
				await AbortMergeAsync(checkout);
				Fail(component, step, $"git could not start the merge: {(merge.Error + merge.Output).Trim()}");
				return;
			}

			var conflicts = Lines(await checkout.RunOrThrowAsync(["diff", "--name-only", "--diff-filter=U"]));
			step.Conflicts.Clear();
			step.Conflicts.AddRange(conflicts);

			var briefText = MergeBrief.Render(await BriefAsync(checkout, component, step, before, head, conflicts));
			step.Brief = Path.Combine(record.WorkspacePath, $"{component.Name}.{Short(step)}.merge.md");
			await File.WriteAllTextAsync(step.Brief, briefText);
			Report(conflicts.Count == 0
					? $"'{component.Name}': asking the resolver to review run {Short(step)} semantically."
					: $"'{component.Name}': asking the resolver to merge run {Short(step)} ({conflicts.Count} conflicted file(s)).",
				component.Name, step.RunId);

			var exitCode = await ShellCommand.RunAsync(record.Resolver, component.Path!,
				new Dictionary<string, string>
				{
					["BASSIA_ROOT"] = monorepo.Root,
					["BASSIA_INTEGRATION_ID"] = record.IntegrationId,
					["BASSIA_COMPONENT"] = component.Name,
					["BASSIA_RUN_ID"] = step.RunId,
					["BASSIA_MERGE_BRIEF"] = step.Brief
				},
				briefText, context?.OnOutput, Cancellation);

			if (exitCode is null)
			{
				await AbortMergeAsync(checkout);
				step.Note = "cancelled while the resolver was working";
				Cancelled = true;
				return;
			}

			if (exitCode != 0)
			{
				await AbortMergeAsync(checkout);
				Fail(component, step, $"the resolver exited with code {exitCode}");
				return;
			}

			if (!await MergeInProgressAsync(checkout))
			{
				// The resolver committed on its own: fine, as long as the result really is in the history now.
				var now = await checkout.RunOrThrowAsync(["rev-parse", "HEAD"]);
				if (now != head && await MergeProbe.IsAncestorAsync(checkout, step.SourceCommit, now))
				{
					step.Outcome = StepOutcome.Resolved;
					step.Commit = now;
					step.Note = "the resolver committed the merge itself";
					Report($"'{component.Name}': run {Short(step)} resolved as {now[..7]}.", component.Name, step.RunId);
				}
				else
				{
					await checkout.RunOrThrowAsync(["reset", "--quiet", "--hard", head]);
					Fail(component, step, "the resolver abandoned the merge");
				}

				return;
			}

			await checkout.RunOrThrowAsync(["add", "--all"]);
			var unresolved = conflicts.Where(file => HasConflictMarkers(Path.Combine(component.Path!, file))).ToList();
			unresolved.AddRange(Lines(await checkout.RunOrThrowAsync(["diff", "--name-only", "--diff-filter=U"])).Except(unresolved));
			if (unresolved.Count > 0)
			{
				await AbortMergeAsync(checkout);
				Fail(component, step, $"conflicts remain in {string.Join(", ", unresolved)}");
				return;
			}

			await checkout.RunOrThrowAsync(["commit", "--quiet", "--no-edit", "-m", MergeCommitMessage(component, step)]);
			step.Outcome = StepOutcome.Resolved;
			step.Commit = await checkout.RunOrThrowAsync(["rev-parse", "HEAD"]);
			Report($"'{component.Name}': run {Short(step)} resolved semantically as {step.Commit[..7]}.", component.Name, step.RunId);
		}

		private async Task<MergeBrief.Input> BriefAsync(GitClient checkout, ComponentIntegration component, IntegrationStep step,
			IReadOnlyList<IntegrationStep> before, string head, IReadOnlyList<string> conflicts)
		{
			var mergeBase = (await checkout.RunAsync(["merge-base", head, step.SourceCommit])).Output.Trim();
			var range = mergeBase.Length > 0 ? mergeBase : component.BaseCommit;

			async Task<MergeBrief.Side> SideAsync(string title, string tip, IReadOnlyList<string> rationales) => new(
				title,
				rationales,
				(await checkout.RunAsync(["log", "--no-color", "--format=commit %h%n%B", $"{range}..{tip}"])).Output,
				(await checkout.RunAsync(["diff", "--no-color", range, tip])).Output);

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
				await SideAsync("Ours", head, integrated),
				await SideAsync("Theirs", step.SourceCommit, [step.Rationale]));
		}

		private async Task PublishAsync(GitClient checkout, ComponentIntegration component, string sourceDir)
		{
			var head = await checkout.RunOrThrowAsync(["rev-parse", "HEAD"]);
			if (head == component.BaseCommit)
			{
				component.ResultStatus = ResultStatus.Unchanged;
				Report($"'{component.Name}': nothing new on top of {component.BaseRef}; no tag.", component.Name);
				return;
			}

			component.ResultCommit = head;
			component.ResultStatus = ResultStatus.Committed;
			var existing = Lines(await checkout.RunOrThrowAsync(["tag", "--list", $"{component.Branch}/*"]));
			var tag = component.ResultTag = $"{component.Branch}/{RunMetadata.HighestIndex(existing) + 1}";
			var integrated = component.Steps.Count(step => step.Outcome is StepOutcome.Merged or StepOutcome.Resolved);
			await checkout.RunOrThrowAsync(["tag", "-a", tag, "-m",
				$"{record.IntegrationId}: {integrated} agentic run result(s) integrated into '{component.Name}' onto {component.BaseRef}", head]);
			await checkout.RunOrThrowAsync(["push", "--quiet", sourceDir,
				$"refs/heads/{component.Branch}:refs/heads/{component.Branch}", $"refs/tags/{tag}:refs/tags/{tag}"]);
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
			if (step.IsSemantic)
			{
				var conflicts = new TomlArray();
				foreach (var file in step.Conflicts)
				{
					conflicts.Add(file);
				}

				table["conflicts"] = conflicts;
				table["resolver"] = record.Resolver;
			}

			var subject = $"integrate({IntegrationRecord.ShortKey(record.IntegrationId)}): {Short(step)} {step.Rationale}";
			return $"{subject}\n\n{TomlSerializer.Serialize(new TomlTable { ["integration"] = table })}";
		}

		private static string Short(IntegrationStep step) => RunMetadata.ShortKey(step.RunId);
	}

	private static async Task<bool> MergeInProgressAsync(GitClient checkout) =>
		(await checkout.RunAsync(["rev-parse", "--quiet", "--verify", "MERGE_HEAD"])).ExitCode == 0;

	private static async Task AbortMergeAsync(GitClient checkout)
	{
		if (await MergeInProgressAsync(checkout))
		{
			await checkout.RunOrThrowAsync(["merge", "--abort"]);
		}
	}

	private static bool HasConflictMarkers(string path) =>
		File.Exists(path) && File.ReadLines(path).Any(line => line.StartsWith("<<<<<<<", StringComparison.Ordinal) || line.StartsWith(">>>>>>>", StringComparison.Ordinal));

	private static List<string> Lines(string text) =>
		text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
