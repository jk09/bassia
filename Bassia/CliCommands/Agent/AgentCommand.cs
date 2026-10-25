namespace Bassia.CliCommands.Agent;

using System.Text;
using Bassia;
using Bassia.Core;
using Bassia.Git;
using Bassia.Locks;
using Bassia.Storage;

/// <summary>
/// <c>bassia agent start|retry|abandon</c> on the monorepo on disk: wires the storage-independent run engine
/// (<see cref="AgentRuns"/>) to the monorepo's git storage, its run records, the agent's shell command and the lock
/// table, and turns outcomes into results.
/// </summary>
internal static class AgentCommand
{
	public static Monorepo LoadMonorepo()
	{
		var root = Monorepo.FindRoot(Environment.CurrentDirectory)
			?? throw new AgentException($"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia init' first.");
		return Monorepo.Load(root);
	}

	// ----- bassia agent start -select ... -run ... -----

	/// <summary>The run engine for <paramref name="monorepo"/>: its component graph and run records, the agent run as a shell command, lock tables held.</summary>
	internal static AgentRuns Runs(Monorepo monorepo, IRunRecordStore? records = null)
	{
		var graph = monorepo.Graph();
		return graph.AgentRuns(records ?? monorepo.RunRecords(), new AgentRunOptions
		{
			WorkspaceDir = monorepo.WorkspaceDir,
			CommitSubject = monorepo.CommitSubject,
			RunAgent = (metadata, context) => RunAgentProcessAsync(monorepo, metadata, context),
			StartLocks = (metadata, report) => RunLocks.Start(monorepo, graph.Storage, metadata, report),
			Prepared = metadata => WriteRunGuide(metadata, RunLocks.Filters(monorepo, metadata), graph.Registry)
		});
	}

	/// <summary>
	/// The full <c>bassia agent start -select ... -run ...</c> sequence, shared by the CLI and the frontends.
	/// <paramref name="context"/> is how a caller watches and interrupts the run; without one the sequence reports on
	/// stderr and lets the agent inherit the console (see <see cref="AgentRunContext"/>). <paramref name="runId"/>
	/// is given when the id had to be known before the run starts - a detached run reports it straight away.
	/// <paramref name="lockFiles"/> and <paramref name="lockFolders"/> are the run's own lock patterns (<c>-lock-files</c>,
	/// <c>-lock-folders</c>), added to the configured ones.
	/// </summary>
	internal static Task<AgentRunOutcome> StartRunAsync(GitClient git, Monorepo monorepo, string select, string command, AgentRunContext? context = null, string? runId = null,
		string? lockFiles = null, string? lockFolders = null) =>
		Runs(monorepo, new RunMetadataStore(git, monorepo.RunsRepoDir)).StartAsync(select, command, context, runId, lockFiles, lockFolders,
			closure => CheckLockPatterns(monorepo, closure, lockFiles, lockFolders));

	/// <summary>Checks a <c>-select</c> value and resolves it (see <see cref="AgentRuns.ResolveSelectionAsync"/>).</summary>
	internal static Task<(IReadOnlyList<ComponentDefinition> Closure, IReadOnlyList<(ComponentDefinition Component, string CommitIsh, string Commit)> Resolved)>
		ResolveSelectionAsync(Monorepo monorepo, string select) => Runs(monorepo).ResolveSelectionAsync(select);

	/// <summary>Checks the run's own lock patterns, and the configured ones, before anything is materialized.</summary>
	internal static void CheckLockPatterns(Monorepo monorepo, IReadOnlyList<ComponentDefinition> closure, string? lockFiles, string? lockFolders)
	{
		var names = closure.Select(component => component.Name).ToList();
		var own = RunLockPatterns.Parse(lockFiles, lockFolders, names);
		foreach (var name in names)
		{
			LockFilter.For(monorepo, name, own);
		}
	}

	/// <summary>
	/// Tells the agent how locking works in this run when its filter locks anything, and what the boundaries of the
	/// selected components are when the monorepo has any: <c>AGENTS.md</c> and <c>CLAUDE.md</c> at the root of the run
	/// folder (its working directory), outside every component checkout, so they are read by agents that look for them
	/// and never committed.
	/// </summary>
	internal static void WriteRunGuide(RunMetadata metadata, IReadOnlyDictionary<string, LockFilter> filters, ComponentRegistry registry)
	{
		var sections = new[] { LockGuide(filters), BoundaryGuide(metadata, registry) }.OfType<string>().ToList();
		if (sections.Count == 0)
		{
			return;
		}

		var text = string.Join("\n", sections);
		File.WriteAllText(Path.Combine(metadata.WorkspacePath, "AGENTS.md"), text);
		File.WriteAllText(Path.Combine(metadata.WorkspacePath, "CLAUDE.md"), text);
	}

	private static string? LockGuide(IReadOnlyDictionary<string, LockFilter> filters)
	{
		var locked = filters.Where(pair => !pair.Value.IsEmpty).ToList();
		if (locked.Count == 0)
		{
			return null;
		}

		var text = new StringBuilder()
			.Append("# Locked paths in this agentic run\n\n")
			.Append("Other agentic runs work on this monorepo at the same time, each in its own folder. Some paths are locked: while a run\n")
			.Append("changes one, it holds it, and no other run should change it until that run is done. Bassia sees which files you\n")
			.Append("change and holds them for this run by itself. Locked here (patterns relative to each component's folder):\n\n");
		foreach (var (component, filter) in locked)
		{
			var (files, folders) = filter.Texts;
			text.Append($"- `{component}/`: ")
				.Append(string.Join("; ", new[]
				{
					files.Count == 0 ? null : $"files {string.Join(", ", files.Select(pattern => $"`{pattern}`"))} one by one",
					folders.Count == 0 ? null : $"folders {string.Join(", ", folders.Select(pattern => $"`{pattern}`"))} as a whole"
				}.OfType<string>()))
				.Append('\n');
		}

		text.Append("\nBefore you rewrite a file or a folder:\n\n")
			.Append("1. `bassia lock check -path <path>` tells whether another run holds it, and whether a finished run changed it after\n")
			.Append("   your checkout was made.\n")
			.Append("2. If it is held, or you are behind on it, `bassia lock wait -path <path>` waits until the holder is done, then holds\n")
			.Append("   the path for you and brings that run's change into your checkout. Re-read every file it lists under `changed`\n")
			.Append("   before you edit it: your view of it is out of date.\n")
			.Append("3. `bassia lock claim -path <path>` holds a path before you start on it, so that others wait for you;\n")
			.Append("   `bassia lock release -path <path>` gives it up early.\n\n")
			.Append("Paths are relative to your working directory (`<component>/<path>` from this folder; a folder ends with `/`).\n")
			.Append("`bassia lock list` shows who holds what. Changing a path another run holds is recorded as a lock conflict, and\n")
			.Append("your result is then reviewed before it is merged. Every command answers in TOML.\n");
		return text.ToString();
	}

	/// <summary>
	/// The boundaries of the run's components - what each may reference and what each exports - when the monorepo has
	/// rules or any selected component has tags, a visibility or exports; otherwise null.
	/// </summary>
	private static string? BoundaryGuide(RunMetadata metadata, ComponentRegistry registry)
	{
		var components = metadata.Components.Select(component => registry.Find(component.Name)).OfType<ComponentDefinition>().ToList();
		if (registry.Boundaries.Count == 0 && components.All(component => component.Tags.Count == 0 && component.Visibility is null && component.Exports is null))
		{
			return null;
		}

		var text = new StringBuilder()
			.Append("# Architectural boundaries in this agentic run\n\n")
			.Append("Each folder here is a component; a component sees the components it references nested inside it (e.g. `app/lib/`).\n")
			.Append("Keep to these boundaries - Bassia checks your result against them and records what breaks them:\n\n")
			.Append("- Use a referenced component only through the folder it is nested at, and only its exported paths.\n")
			.Append("- Never reach a component through another component's nested folder (`app/lib/vendor/core/...`): reference it directly.\n")
			.Append("- Never use paths outside your component's folder (`../other/...`), and add no reference the rules below forbid.\n\n");
		foreach (var component in components)
		{
			text.Append($"- `{component.Name}/`")
				.Append(component.Tags.Count == 0 ? "" : $" (tags {string.Join(", ", component.Tags.Select(tag => $"`{tag}`"))})")
				.Append(component.References.Count == 0 ? ": references nothing" : $": references {string.Join(", ", component.References.Select(reference => $"`{reference.Name}` at `{component.Name}/{reference.Path}/`"))}")
				.Append(component.Exports is null ? "; exports everything" : $"; exports only {string.Join(", ", component.Exports.Select(pattern => $"`{pattern}`"))}")
				.Append(component.Visibility is null ? "" : $"; may be referenced only by {string.Join(", ", component.Visibility.Select(pattern => $"`{pattern}`"))}")
				.Append('\n');
		}

		if (registry.Boundaries.Count > 0)
		{
			text.Append("\nBoundary rules (a component with a tag matching the source may reference only what is allowed, nothing denied):\n\n");
			foreach (var rule in registry.Boundaries)
			{
				text.Append($"- `{rule.Text}`{(rule.Description is null ? "" : $" - {rule.Description}")}\n");
			}
		}

		text.Append("\n`bassia help boundary` and `bassia help boundary check` explain the rules.\n");
		return text.ToString();
	}

	// ----- agent process -----

	/// <summary>
	/// Runs the agent command in the run folder and returns its exit code, or <c>null</c> when it was cancelled and
	/// its process tree killed. Without <see cref="AgentRunContext.OnOutput"/> the process inherits this console, so
	/// a caller of <c>bassia agent start</c> sees the agent's output as it happens; with one, both pipes are redirected
	/// and delivered line by line, which is what lets the web dashboard run an agent behind a live page.
	/// </summary>
	private static Task<int?> RunAgentProcessAsync(Monorepo monorepo, RunMetadata metadata, AgentRunContext? context) =>
		ShellCommand.RunAsync(
			metadata.Command,
			metadata.WorkspacePath,
			new Dictionary<string, string>
			{
				["BASSIA_ROOT"] = monorepo.Root,
				["BASSIA_RUN_ID"] = metadata.RunId,
				["BASSIA_RUN_DIR"] = metadata.WorkspacePath
			},
			standardInput: null,
			context?.OnOutput,
			context?.Cancellation ?? CancellationToken.None);

	internal static bool IsHashSelector(string commitIsh) => AgentRuns.IsHashSelector(commitIsh);

	internal static string SummarizeCommand(string command) => AgentRuns.SummarizeCommand(command);

	internal static Task<string> NextResultTagAsync(GitClient checkout, string runId) =>
		AgentRuns.NextResultTagAsync(new Storage.Git.GitWorkspace(checkout.WorkingDirectory, checkout.WorkingDirectory), runId);

	// ----- bassia agent retry / abandon -----

	/// <summary>Finishes the commit/tag/push sequence of a run whose earlier attempt failed part-way.</summary>
	internal static Task<AgentRunOutcome> RetryAsync(Monorepo monorepo, IRunRecordStore store, RunMetadata metadata) => Runs(monorepo, store).RetryAsync(metadata);

	/// <summary>Discards a run's folder and records the run as abandoned; what it pushed stays where it is.</summary>
	internal static Task<AgentRunOutcome> AbandonAsync(IRunRecordStore store, RunMetadata metadata) => AgentRuns.AbandonAsync(store, metadata);

	/// <summary>Records a run whose process is gone without its final record (killed, crashed) as cancelled.</summary>
	internal static Task<string> RecordCancelledAsync(IRunRecordStore store, RunMetadata metadata) => AgentRuns.RecordCancelledAsync(store, metadata);

	// ----- output -----

	internal static Dictionary<string, object?> ResultData(RunMetadata metadata, IRunRecordStore store, IReadOnlyList<string> metadataTags) => new()
	{
		["run_id"] = metadata.RunId,
		["status"] = metadata.Status,
		["workspace"] = metadata.WorkspacePath,
		["agent_exit_code"] = metadata.AgentExitCode,
		["metadata_repo"] = store.Location,
		["metadata_tags"] = metadataTags,
		// Named as in run.toml, so a caller reads the result and the stored record with the same keys.
		["component"] = metadata.Components.Select(component => new Dictionary<string, object?>
		{
			["name"] = component.Name,
			["commitish"] = component.CommitIsh,
			["commit"] = component.Commit,
			["path"] = component.Path,
			["branch"] = component.Branch,
			["result_status"] = component.ResultStatus.ToString().ToLowerInvariant(),
			["result_commit"] = component.ResultCommit,
			["result_tag"] = component.ResultTag,
			["result_error"] = component.ResultError,
			["held"] = component.Held.Select(key => key.Display).ToList(),
			["lock_conflict"] = LockConflictData(component.LockConflicts),
			["boundary_violation"] = component.BoundaryViolations.Count == 0 ? null : BoundaryCommands.Describe(component.BoundaryViolations),
			["caught_up"] = component.CaughtUp.Select(catchUp => new Dictionary<string, object?>
			{
				["run_id"] = catchUp.Run,
				["tag"] = catchUp.Tag,
				["commit"] = catchUp.Commit,
				["files"] = catchUp.Files
			}).ToList()
		}).ToList(),
		["lock_conflicts"] = metadata.Components.Sum(component => component.LockConflicts.Count),
		["boundary_violations"] = metadata.Components.Sum(component => component.BoundaryViolations.Count)
	};

	/// <summary>Lock conflicts as result entries, named as in the run record.</summary>
	internal static List<Dictionary<string, object?>> LockConflictData(IEnumerable<LockConflict> conflicts) => conflicts.Select(conflict => new Dictionary<string, object?>
	{
		["component"] = conflict.Component,
		["path"] = conflict.Path,
		["kind"] = conflict.Kind,
		["with"] = conflict.With,
		["with_path"] = conflict.WithPath,
		["at"] = conflict.At,
		["description"] = conflict.Describe()
	}).ToList();
}
