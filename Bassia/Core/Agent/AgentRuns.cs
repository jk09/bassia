namespace Bassia.Core;

using Bassia.Storage;

/// <summary>
/// What an agentic run holds while its agent works: in the CLI, <see cref="Locks.RunLocks"/>. The engine stops it when the
/// agent is done, checks for stale results before committing, and finishes it once the results are published.
/// </summary>
internal interface IAgentRunLocks : IAsyncDisposable
{
	Task StopAsync();

	Task CheckStaleAsync();

	Task FinishAsync();
}

/// <summary>How agentic runs are carried out around the component graph: where they work, how commits are named, how the agent is started.</summary>
internal sealed class AgentRunOptions
{
	/// <summary>Folder holding one folder per run.</summary>
	public required string WorkspaceDir { get; init; }

	/// <summary>Subject template of a run's result commits (see <see cref="ResultCommitMessage"/>).</summary>
	public string CommitSubject { get; init; } = Monorepo.DefaultCommitSubject;

	/// <summary>Runs the agent in the run folder; returns its exit code, or null when it was cancelled and stopped.</summary>
	public required Func<RunMetadata, AgentRunContext?, Task<int?>> RunAgent { get; init; }

	/// <summary>Starts what the run holds while its agent works; by default nothing is held.</summary>
	public Func<RunMetadata, Action<string>, IAgentRunLocks> StartLocks { get; init; } = (_, _) => new NoLocks();

	/// <summary>Called when the run folder is ready and recorded, just before the agent starts.</summary>
	public Action<RunMetadata>? Prepared { get; init; }

	private sealed class NoLocks : IAgentRunLocks
	{
		public Task StopAsync() => Task.CompletedTask;

		public Task CheckStaleAsync() => Task.CompletedTask;

		public Task FinishAsync() => Task.CompletedTask;

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}

/// <summary>
/// Agentic runs on the component graph: a run selects a version of every component in a reference closure, gets each as
/// a working copy in its own run folder (a referenced component appears inside its referrer as a link to its own working
/// copy), lets an agent change them, and turns every changed working copy into a commit on the run's branch, tagged
/// <c>agent-run/&lt;key&gt;/&lt;n&gt;</c> and published to the component's store. Every step is recorded as a new version
/// of the run's record. Everything goes through the storage contract, so runs work on any backend.
/// </summary>
internal sealed class AgentRuns(MonorepoGraph graph, IRunRecordStore records, AgentRunOptions options)
{
	public IRunRecordStore Records { get; } = records;

	// ----- starting a run -----

	/// <summary>
	/// The whole run: resolve <paramref name="select"/>, materialize, record, run <paramref name="command"/>, commit, tag and
	/// publish. <paramref name="context"/> is how a caller watches and interrupts it; <paramref name="runId"/> is given when
	/// the id had to be known before the run starts.
	/// </summary>
	public async Task<AgentRunOutcome> StartAsync(string select, string command, AgentRunContext? context = null, string? runId = null,
		string? lockFiles = null, string? lockFolders = null, Action<IReadOnlyList<ComponentDefinition>>? check = null)
	{
		var cancellation = context?.Cancellation ?? CancellationToken.None;
		var (closure, resolved) = await ResolveSelectionAsync(select);
		check?.Invoke(closure);

		// Up to here nothing outside the stores has been touched, so a cancellation simply unwinds.
		cancellation.ThrowIfCancellationRequested();

		await Records.EnsureRepositoryAsync();
		runId ??= RunMetadata.NewRunId();
		var runDir = Path.Combine(options.WorkspaceDir, runId);
		var branch = RunMetadata.RefBase(runId);
		Directory.CreateDirectory(runDir);

		var metadata = new RunMetadata
		{
			RunId = runId,
			Status = "started",
			Created = RunMetadata.Timestamp(),
			Select = select,
			Command = command,
			WorkspacePath = runDir,
			LockFiles = string.IsNullOrWhiteSpace(lockFiles) ? null : lockFiles,
			LockFolders = string.IsNullOrWhiteSpace(lockFolders) ? null : lockFolders,
			User = Actor.Current?.User.Id
		};

		foreach (var (component, commitIsh, commit) in resolved)
		{
			metadata.Components.Add(new ComponentRun
			{
				Name = component.Name,
				CommitIsh = commitIsh,
				Commit = commit,
				Path = Path.Combine(runDir, component.Name),
				Branch = branch
			});
		}

		try
		{
			await MaterializeAsync(metadata, closure, context);
		}
		catch
		{
			DiscardRunFolder(metadata);
			throw;
		}

		var startTag = await Records.CommitAsync(metadata);
		Report(context, runId, AgentRunPhase.Preparing, $"{runId}: metadata committed as {startTag} in '{Records.Location}'.");
		options.Prepared?.Invoke(metadata);
		Report(context, runId, AgentRunPhase.Agent, $"{runId}: running agent command in '{runDir}'.");

		// While the agent works, what it changes is held for the run and checked against what other runs hold.
		await using var locks = options.StartLocks(metadata, message => Report(context, runId, AgentRunPhase.Agent, message));
		int? exitCode;
		try
		{
			exitCode = await options.RunAgent(metadata, context);
		}
		finally
		{
			await locks.StopAsync();
		}

		metadata.Finished = RunMetadata.Timestamp();

		// A null exit code means the agent process was killed on request; the record says so and keeps the run
		// folder, so what the agent had already written can still be inspected (or abandoned, which discards it).
		if (exitCode is null)
		{
			metadata.Status = "cancelled";
			await locks.FinishAsync();
			var cancelledTag = await Records.CommitAsync(metadata);
			Report(context, runId, AgentRunPhase.Cancelled, $"{runId}: cancelled; the agent process tree was stopped.");
			return new AgentRunOutcome(false,
				$"Agentic run '{runId}' was cancelled; the agent command was stopped and nothing was committed. The workspace '{runDir}' is kept for inspection.",
				metadata, Records, [startTag, cancelledTag]);
		}

		metadata.AgentExitCode = exitCode;
		if (metadata.AgentExitCode != 0)
		{
			metadata.Status = "failed";
			await locks.FinishAsync();
			var failedTag = await Records.CommitAsync(metadata);
			return new AgentRunOutcome(false,
				$"Agent command exited with code {metadata.AgentExitCode}; nothing was committed. The workspace '{runDir}' is kept for inspection.",
				metadata, Records, [startTag, failedTag]);
		}

		await locks.CheckStaleAsync();
		Report(context, runId, AgentRunPhase.Finalizing, $"{runId}: committing, tagging and pushing the component results.");
		using (ActingThroughAgent(metadata))
		{
			foreach (var component in metadata.Components)
			{
				await FinalizeComponentAsync(metadata, component, context);
			}
		}

		await CheckBoundariesAsync(metadata, context);

		// Released once the results are published, so a run waiting for these paths can bring them in at once.
		await locks.FinishAsync();
		metadata.Status = OverallStatus(metadata);
		var finalTag = await Records.CommitAsync(metadata);
		var ok = metadata.Status == "completed";
		return new AgentRunOutcome(ok,
			ok ? CompletedMessage(metadata) : $"Agentic run '{runId}' finished with failures; run 'bassia agent retry -id {runId}' or 'bassia agent abandon -id {runId}'.",
			metadata, Records, [startTag, finalTag]);
	}

	// ----- selection -----

	/// <summary>
	/// Checks a <c>-select</c> value and resolves every component's tag, hash or default branch to its commit, without
	/// touching the filesystem: every entry names a registered component, either bare (the tip of its default branch)
	/// or with an annotated tag or a commit hash, and the selection covers its own reference closure. Returns the
	/// closure in dependency order with each component's selector (the branch name for a bare entry) and resolved commit.
	/// </summary>
	public async Task<(IReadOnlyList<ComponentDefinition> Closure, IReadOnlyList<(ComponentDefinition Component, string CommitIsh, string Commit)> Resolved)>
		ResolveSelectionAsync(string select)
	{
		var selected = ComponentSelection.ParseList("-select", select, allowBare: true);
		if (selected.Count == 0)
		{
			throw new AgentException("-select names no component; expected <component>[@<tag|hash>][,<component>[@<tag|hash>]...].");
		}

		foreach (var selection in selected)
		{
			if (!graph.Registry.Contains(selection.Component))
			{
				throw new AgentException($"Component '{selection.Component}' is not registered in components.toml.");
			}
		}

		var closure = graph.Registry.Closure(selected.Select(selection => selection.Component));
		RequireCompleteClosure(selected, closure);

		var commitIshByName = selected.ToDictionary(selection => selection.Component, selection => selection.CommitIsh, StringComparer.Ordinal);
		var resolved = new List<(ComponentDefinition Component, string CommitIsh, string Commit)>();
		foreach (var component in closure)
		{
			resolved.Add(commitIshByName[component.Name] is { } commitIsh
				? (component, commitIsh, await ResolveCommitAsync(component.Name, commitIsh))
				: await ResolveDefaultBranchAsync(component));
		}

		return (closure, resolved);
	}

	/// <summary>
	/// A run must select every component it materializes: the reference closure of the selection has to be the
	/// selection itself. A component nobody named is never pulled in implicitly: each one's version (a tag, a hash or
	/// its default branch's tip) is the caller's explicit choice.
	/// </summary>
	private static void RequireCompleteClosure(IReadOnlyList<ComponentSelection> selected, IReadOnlyList<ComponentDefinition> closure)
	{
		var selectedNames = selected.Select(selection => selection.Component).ToHashSet(StringComparer.Ordinal);
		var missing = closure.Where(component => !selectedNames.Contains(component.Name)).Select(component => component.Name).ToList();
		if (missing.Count == 0)
		{
			return;
		}

		var referrers = missing.ToDictionary(
			name => name,
			name => closure.First(component => component.References.Any(reference => reference.Name == name)).Name,
			StringComparer.Ordinal);

		var details = string.Join(", ", missing.Select(name => $"'{name}' (referenced by '{referrers[name]}')"));
		throw new AgentException(
			$"-select must cover the full component closure; missing: {details}. " +
			$"Add each as <component> or <component>@<tag|hash>, for example: -select {string.Join(",", selected.Select(Format))},{missing[0]}.");

		static string Format(ComponentSelection selection) =>
			selection.CommitIsh is null ? selection.Component : $"{selection.Component}@{selection.CommitIsh}";
	}

	/// <summary>
	/// Resolves a bare <c>-select</c> entry: the tip of the component's default branch at this moment. The branch name is
	/// recorded as the selector and the commit as the run's immutable provenance, so the record stays reproducible although
	/// the branch moves on.
	/// </summary>
	private async Task<(ComponentDefinition Component, string CommitIsh, string Commit)> ResolveDefaultBranchAsync(ComponentDefinition component)
	{
		var store = RequireStore(component.Name);
		var branch = await store.DefaultBranchAsync();
		if (branch is null)
		{
			throw new AgentException(
				$"Component '{component.Name}' ('{store.Location}') has no default branch: its HEAD is detached. " +
				$"Select it as {component.Name}@<tag|hash>.");
		}

		var commit = await store.BranchTipAsync(branch) ?? throw new AgentException(
			$"Component '{component.Name}' ('{store.Location}') has no commit on its default branch '{branch}'. " +
			$"Select it as {component.Name}@<tag|hash>.");
		return (component, branch, commit);
	}

	private IComponentStore RequireStore(string componentName) =>
		graph.Storage.Exists(componentName)
			? graph.Storage.Open(componentName)
			: throw new AgentException($"Component '{componentName}' has no local repository at '{graph.Storage.LocationOf(componentName)}'. Run 'bassia component add' first.");

	/// <summary>
	/// Resolves a component's commit-ish. It must be an annotated tag or a commit hash (see <see cref="IsHashSelector"/>),
	/// since it is logged as the run's immutable provenance.
	/// </summary>
	private async Task<string> ResolveCommitAsync(string componentName, string commitIsh)
	{
		var store = RequireStore(componentName);
		if (IsHashSelector(commitIsh) && await ResolveHashAsync(store, componentName, commitIsh) is { } commit)
		{
			return commit;
		}

		var resolved = await store.ResolveRefAsync(commitIsh)
			?? throw new AgentException($"'{commitIsh}' does not exist in component '{componentName}' ('{store.Location}').");
		if (resolved.Kind != RefKind.AnnotatedTag)
		{
			throw new AgentException($"'{componentName}@{commitIsh}' is a commit, not an annotated tag. Select an annotated tag or a commit hash (or the bare component name for its default branch's tip), " +
				$"or create a tag with: bassia component tag -name {componentName} -tag <name>");
		}

		return resolved.Commit;
	}

	/// <summary>
	/// A selector read as a commit hash: 6 to 64 lowercase hex digits (a SHA-1 or SHA-256 hash or a prefix of one).
	/// Anything else is a ref name.
	/// </summary>
	public static bool IsHashSelector(string commitIsh) =>
		commitIsh.Length is >= 6 and <= 64 && commitIsh.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

	/// <summary>
	/// Resolves a hash selector to its commit, or returns null when it is only a ref name (no object id starts with it),
	/// so the caller resolves it as a tag. The selector must mean one thing: a prefix shared by several objects fails, and
	/// one that is both a ref and an object id prefix fails too, where a store would silently prefer the ref.
	/// </summary>
	private static async Task<string?> ResolveHashAsync(IComponentStore store, string componentName, string hash)
	{
		var reference = (await store.ResolveRefAsync(hash))?.FullName;
		var candidates = await store.FindObjectsAsync(hash);

		if (candidates.Count == 0)
		{
			return reference is not null ? null : throw new AgentException($"'{hash}' does not exist in component '{componentName}' ('{store.Location}').");
		}

		if (reference is not null)
		{
			throw new AgentException(
				$"'{componentName}@{hash}' is ambiguous: it names the ref '{reference}' and is also the prefix of the object hash {candidates[0].Id}" +
				(candidates.Count > 1 ? $" (and {candidates.Count - 1} more)" : "") +
				$". Select the ref as '{componentName}@{reference}' or the commit with a longer hash.");
		}

		if (candidates.Count > 1)
		{
			throw new AgentException($"'{componentName}@{hash}' does not name a single object in component '{componentName}' ('{store.Location}'); select it with a longer hash. " +
				$"The short object ID {hash} is ambiguous: {string.Join(", ", candidates.Select(candidate => $"{candidate.Kind} {candidate.Id}"))}.");
		}

		return candidates[0].Kind == "commit"
			? candidates[0].Id
			: throw new AgentException($"'{componentName}@{hash}' is a {candidates[0].Kind}, not a commit. Select a commit hash or an annotated tag.");
	}

	// ----- materialization -----

	private async Task MaterializeAsync(RunMetadata metadata, IReadOnlyList<ComponentDefinition> closure, AgentRunContext? context)
	{
		var runs = metadata.Components.ToDictionary(component => component.Name, StringComparer.Ordinal);
		var workspaces = new Dictionary<string, IComponentWorkspace>(StringComparer.Ordinal);

		foreach (var component in metadata.Components)
		{
			(context?.Cancellation ?? CancellationToken.None).ThrowIfCancellationRequested();
			Report(context, metadata.RunId, AgentRunPhase.Preparing,
				$"{metadata.RunId}: checkout of '{component.Name}' @ {component.CommitIsh} ({component.Commit[..7]}) -> '{component.Path}'.");
			workspaces[component.Name] = await graph.Storage.Open(component.Name).CheckoutAsync(component.Commit, component.Path, component.Branch);
		}

		// A referenced component appears inside the referencing component as a link (junction) to its sibling
		// working copy, so every reference to it in the run resolves to the same files and the same commit.
		try
		{
			await Workspaces.NestAsync(closure, runs.ToDictionary(pair => pair.Key, pair => pair.Value.Path, StringComparer.Ordinal), workspaces,
				(owner, reference) => runs[owner].Junctions[reference.Path] = reference.Name);
		}
		catch (MonorepoException ex)
		{
			throw new AgentException(ex.Message);
		}
	}

	internal static void DiscardRunFolder(RunMetadata metadata)
	{
		// Remove links first so a recursive delete can never reach into another working copy.
		foreach (var component in metadata.Components)
		{
			foreach (var junction in component.Junctions.Keys)
			{
				DirectoryLinks.Delete(Path.Combine(component.Path, junction));
			}
		}

		if (Directory.Exists(metadata.WorkspacePath))
		{
			// Read-only files (git's pack files) make Directory.Delete fail on Windows.
			foreach (var file in Directory.EnumerateFiles(metadata.WorkspacePath, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
			{
				File.SetAttributes(file, FileAttributes.Normal);
			}

			Directory.Delete(metadata.WorkspacePath, recursive: true);
		}
	}

	// ----- commit / tag / publish -----

	/// <summary>
	/// Commits the component's working copy on its run branch, tags the commit and publishes branch + tag to the
	/// component's store. Idempotent, so a partially failed sequence can be retried: steps already done are skipped.
	/// </summary>
	private async Task FinalizeComponentAsync(RunMetadata metadata, ComponentRun component, AgentRunContext? context = null)
	{
		if (component.ResultStatus is ResultStatus.Pushed or ResultStatus.Unchanged)
		{
			return;
		}

		try
		{
			var workspace = graph.Storage.OpenWorkspace(component.Name, component.Path);

			// The tag is settled before the commit exists so the commit message can name it. A retry keeps the tag
			// recorded by the earlier attempt.
			var tag = component.ResultTag ?? await NextResultTagAsync(workspace, metadata.RunId);
			if (component.ResultCommit is null)
			{
				var message = ResultCommitMessage.Render(options.CommitSubject, metadata, component, tag, SummarizeCommand(metadata.Command));
				var commit = await workspace.CommitAsync(message);
				if (commit is null)
				{
					component.ResultStatus = ResultStatus.Unchanged;
					Report(context, metadata.RunId, AgentRunPhase.Finalizing, $"{metadata.RunId}: '{component.Name}' unchanged.");
					return;
				}

				component.ResultTag = tag;
				component.ResultCommit = commit;
				component.ResultStatus = ResultStatus.Committed;
			}

			var existingTag = await workspace.TagCommitAsync(tag);
			if (existingTag is null)
			{
				await workspace.TagAsync(tag, component.ResultCommit, $"{metadata.RunId}: result of the agentic run in component '{component.Name}'");
			}
			else if (existingTag != component.ResultCommit)
			{
				throw new StorageException($"tag '{tag}' already exists in '{component.Path}' and does not point at the result commit {component.ResultCommit[..7]}.");
			}

			await workspace.PublishAsync(component.Branch, [tag]);
			component.ResultStatus = ResultStatus.Pushed;
			component.ResultError = null;
			Report(context, metadata.RunId, AgentRunPhase.Finalizing,
				$"{metadata.RunId}: '{component.Name}' committed as {component.ResultCommit[..7]}, tagged {tag}, pushed to '{graph.Storage.LocationOf(component.Name)}'.");
		}
		catch (StorageException ex)
		{
			component.ResultStatus = ResultStatus.Failed;
			component.ResultError = ex.Message;
			Report(context, metadata.RunId, AgentRunPhase.Finalizing, $"{metadata.RunId}: '{component.Name}' failed: {ex.Message}");
		}
	}

	/// <summary>
	/// The run's result commits and tags are the user's work done through the agent: authored by the user, committed and
	/// tagged as <c>&lt;agent&gt; via bassia</c> (see <see cref="Actor"/>). Nothing changes without a current user.
	/// </summary>
	private static IDisposable ActingThroughAgent(RunMetadata metadata) =>
		Actor.Use(Actor.Current?.Through(Actor.AgentOf(metadata.Command)));

	/// <summary>
	/// Scans each published result for code boundary violations and records those the commit the run started from did not
	/// have. It only records: the result stays published, and the integration decides what the violations mean
	/// (<c>merge.boundaries</c>). A scan that fails is reported and leaves the component without a record.
	/// </summary>
	private async Task CheckBoundariesAsync(RunMetadata metadata, AgentRunContext? context = null)
	{
		foreach (var component in metadata.Components.Where(component => component.ResultStatus == ResultStatus.Pushed && component.ResultCommit is not null))
		{
			try
			{
				var store = graph.Storage.Open(component.Name);
				var before = await CodeBoundaries.ScanAsync(graph.Registry, store, component.Name, component.Commit);
				var after = await CodeBoundaries.ScanAsync(graph.Registry, store, component.Name, component.ResultCommit!);
				component.BoundaryViolations.Clear();
				component.BoundaryViolations.AddRange(after.AddedOver(before));
				foreach (var violation in component.BoundaryViolations)
				{
					Report(context, metadata.RunId, AgentRunPhase.Finalizing, $"{metadata.RunId}: boundary violation ({violation.KindName}): {violation.Message}");
				}
			}
			catch (Exception ex) when (ex is StorageException or MonorepoException)
			{
				Report(context, metadata.RunId, AgentRunPhase.Finalizing, $"{metadata.RunId}: '{component.Name}' could not be checked against its boundaries: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// <c>agent-run/&lt;key&gt;/&lt;counter&gt;</c> with the next unused counter. Every result tag of the run sits on the run
	/// branch of this working copy (taken from the store, so earlier published results are visible too), which makes the
	/// working copy's own tag list the complete sequence.
	/// </summary>
	internal static async Task<string> NextResultTagAsync(IComponentWorkspace workspace, string runId)
	{
		var tags = await workspace.ListTagsAsync($"{RunMetadata.RefBase(runId)}/");
		return RunMetadata.TagName(runId, RunMetadata.HighestIndex(tags) + 1);
	}

	/// <summary>
	/// One-line summary for commit subjects: the longest quoted part of the command (usually the prompt) when
	/// there is one, otherwise the command itself, truncated to a conventional subject length.
	/// </summary>
	public static string SummarizeCommand(string command)
	{
		var summary = command.Split('\n')[0].Trim();
		var quoted = summary.Split('"');
		if (quoted.Length >= 3)
		{
			// Odd indexes are the segments between quote pairs.
			var longest = quoted.Where((_, index) => index % 2 == 1).OrderByDescending(segment => segment.Length).First().Trim();
			if (longest.Length >= 8)
			{
				summary = longest;
			}
		}

		return summary.Length > 72 ? summary[..69] + "..." : summary;
	}

	private static string OverallStatus(RunMetadata metadata) =>
		metadata.Components.All(component => component.ResultStatus is ResultStatus.Pushed or ResultStatus.Unchanged) ? "completed" : "partial";

	private static string CompletedMessage(RunMetadata metadata)
	{
		if (metadata.Components.Any(component => component.ResultStatus == ResultStatus.Pushed))
		{
			var conflicts = metadata.Components.SelectMany(component => component.LockConflicts).ToList();
			var violations = metadata.Components.SelectMany(component => component.BoundaryViolations).ToList();
			return (conflicts.Count == 0
				? $"Agentic run '{metadata.RunId}' completed."
				: $"Agentic run '{metadata.RunId}' completed with {conflicts.Count} lock conflict(s): {string.Join("; ", conflicts.Take(3).Select(conflict => conflict.Describe()))}" +
					$"{(conflicts.Count > 3 ? "; ..." : "")}. The integration treats its result by lock.conflict.") +
				(violations.Count == 0 ? ""
					: $" Its result adds {violations.Count} boundary violation(s): {string.Join(" ", violations.Take(3).Select(violation => violation.Message))}" +
						$"{(violations.Count > 3 ? " ..." : "")} The integration treats them by merge.boundaries.");
		}

		// A clean run with nothing to commit usually means the agent answered inline instead of editing files
		// (e.g. it lacked permission to write) or wrote outside every component working copy.
		return $"Agentic run '{metadata.RunId}' completed, but the agent command changed no component; nothing was committed. " +
			$"Check that the command may edit files and that it writes inside a component folder of '{metadata.WorkspacePath}'.";
	}

	// ----- retry / abandon -----

	/// <summary>Finishes the commit/tag/publish sequence of a run whose earlier attempt failed part-way.</summary>
	public async Task<AgentRunOutcome> RetryAsync(RunMetadata metadata)
	{
		if (metadata.Status is "abandoned")
		{
			throw new AgentException($"Agentic run '{metadata.RunId}' was abandoned; nothing to retry.");
		}

		if (metadata.Status is "started" or "failed" or "cancelled")
		{
			throw new AgentException($"Agentic run '{metadata.RunId}' has status '{metadata.Status}'; only runs whose commit/push sequence failed can be retried.");
		}

		using (ActingThroughAgent(metadata))
		{
			foreach (var component in metadata.Components)
			{
				await FinalizeComponentAsync(metadata, component);
			}
		}

		await CheckBoundariesAsync(metadata);
		metadata.Status = OverallStatus(metadata);
		metadata.Finished = RunMetadata.Timestamp();
		var tag = await Records.CommitAsync(metadata);
		var ok = metadata.Status == "completed";
		return new AgentRunOutcome(ok,
			ok ? $"Agentic run '{metadata.RunId}' completed." : $"Agentic run '{metadata.RunId}' still has failed components.",
			metadata, Records, [tag]);
	}

	/// <summary>Discards a run's folder and records the run as abandoned; what it published stays where it is.</summary>
	public Task<AgentRunOutcome> AbandonAsync(RunMetadata metadata) => AbandonAsync(Records, metadata);

	/// <summary>Records a run whose process is gone without its final record (killed, crashed) as cancelled.</summary>
	public Task<string> RecordCancelledAsync(RunMetadata metadata) => RecordCancelledAsync(Records, metadata);

	/// <summary>Discards a run's folder and records the run in <paramref name="records"/> as abandoned.</summary>
	public static async Task<AgentRunOutcome> AbandonAsync(IRunRecordStore records, RunMetadata metadata)
	{
		if (metadata.Status is "abandoned")
		{
			throw new AgentException($"Agentic run '{metadata.RunId}' is already abandoned.");
		}

		DiscardRunFolder(metadata);
		metadata.Status = "abandoned";
		metadata.Finished = RunMetadata.Timestamp();
		var tag = await records.CommitAsync(metadata);
		return new AgentRunOutcome(true,
			$"Agentic run '{metadata.RunId}' abandoned; its run folder was discarded. Results already pushed to source-of-truth repos were left in place.",
			metadata, records, [tag]);
	}

	/// <summary>Records a run in <paramref name="records"/> as cancelled.</summary>
	public static async Task<string> RecordCancelledAsync(IRunRecordStore records, RunMetadata metadata)
	{
		metadata.Status = "cancelled";
		metadata.Finished ??= RunMetadata.Timestamp();
		return await records.CommitAsync(metadata);
	}

	/// <summary>
	/// One progress step. Without a context - the scriptable CLI - it is the <c>bassia: ...</c> stderr line the
	/// command has always written; with one it goes to the caller instead, which is how the web dashboard keeps
	/// the running commentary of several parallel runs off its console and on their pages.
	/// </summary>
	private static void Report(AgentRunContext? context, string runId, AgentRunPhase phase, string message)
	{
		if (context?.OnStep is { } onStep)
		{
			onStep(new AgentRunStep(runId, phase, message));
		}
		else
		{
			Console.Error.WriteLine($"bassia: {message}");
		}
	}
}
