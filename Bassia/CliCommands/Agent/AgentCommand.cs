namespace Bassia.CliCommands.Agent;

using Bassia;
using Bassia.Git;

/// <summary>
/// <c>bassia run start</c>: materializes a selected monorepo state in an isolated workspace area, runs an agent
/// command there, and commits/tags/pushes the resulting component changes back to the source-of-truth repos.
/// </summary>
internal static class AgentCommand
{
	public static Monorepo LoadMonorepo()
	{
		var root = Monorepo.FindRoot(Environment.CurrentDirectory)
			?? throw new AgentException($"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia init' first.");
		return Monorepo.Load(root);
	}

	// ----- bassia run start -select ... -run ... -----

	/// <summary>
	/// The full <c>bassia run start -select ... -run ...</c> sequence, shared by the CLI and the frontends.
	/// <paramref name="context"/> is how a caller watches and interrupts the run; without one the sequence reports on
	/// stderr and lets the agent inherit the console (see <see cref="AgentRunContext"/>). <paramref name="runId"/>
	/// is given when the id had to be known before the run starts - a detached run reports it straight away.
	/// </summary>
	internal static async Task<AgentRunOutcome> StartRunAsync(GitClient git, Monorepo monorepo, string select, string command, AgentRunContext? context = null, string? runId = null)
	{
		var cancellation = context?.Cancellation ?? CancellationToken.None;
		var (closure, resolved) = await ResolveSelectionAsync(monorepo, select);

		// Up to here nothing outside git's object database has been touched, so a cancellation simply unwinds.
		cancellation.ThrowIfCancellationRequested();

		var store = new RunMetadataStore(git, monorepo.RunsRepoDir);
		await store.EnsureRepositoryAsync();
		runId ??= RunMetadata.NewRunId();
		var runDir = Path.Combine(monorepo.WorkspaceDir, runId);
		var branch = RunMetadata.RefBase(runId);
		Directory.CreateDirectory(runDir);

		var metadata = new RunMetadata
		{
			RunId = runId,
			Status = "started",
			Created = RunMetadata.Timestamp(),
			Select = select,
			Command = command,
			WorkspacePath = runDir
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
			await MaterializeAsync(git, monorepo, metadata, closure, context);
		}
		catch
		{
			DiscardRunFolder(metadata);
			throw;
		}

		var startTag = await store.CommitAsync(metadata);
		Report(context, runId, AgentRunPhase.Preparing, $"{runId}: metadata committed as {startTag} in '{store.RepoDir}'.");
		Report(context, runId, AgentRunPhase.Agent, $"{runId}: running agent command in '{runDir}'.");

		var exitCode = await RunAgentProcessAsync(monorepo, metadata, context);
		metadata.Finished = RunMetadata.Timestamp();

		// A null exit code means the agent process was killed on request; the record says so and keeps the run
		// folder, so what the agent had already written can still be inspected (or abandoned, which discards it).
		if (exitCode is null)
		{
			metadata.Status = "cancelled";
			var cancelledTag = await store.CommitAsync(metadata);
			Report(context, runId, AgentRunPhase.Cancelled, $"{runId}: cancelled; the agent process tree was stopped.");
			return new AgentRunOutcome(false,
				$"Agentic run '{runId}' was cancelled; the agent command was stopped and nothing was committed. The workspace '{runDir}' is kept for inspection.",
				metadata, store, [startTag, cancelledTag]);
		}

		metadata.AgentExitCode = exitCode;

		if (metadata.AgentExitCode != 0)
		{
			metadata.Status = "failed";
			var failedTag = await store.CommitAsync(metadata);
			return new AgentRunOutcome(false,
				$"Agent command exited with code {metadata.AgentExitCode}; nothing was committed. The workspace '{runDir}' is kept for inspection.",
				metadata, store, [startTag, failedTag]);
		}

		Report(context, runId, AgentRunPhase.Finalizing, $"{runId}: committing, tagging and pushing the component results.");
		foreach (var component in metadata.Components)
		{
			await FinalizeComponentAsync(monorepo, metadata, component, context);
		}

		metadata.Status = OverallStatus(metadata);
		var finalTag = await store.CommitAsync(metadata);
		var ok = metadata.Status == "completed";
		return new AgentRunOutcome(ok,
			ok ? CompletedMessage(metadata) : $"Agentic run '{runId}' finished with failures; run 'bassia run retry -id {runId}' or 'bassia run abandon -id {runId}'.",
			metadata, store, [startTag, finalTag]);
	}

	/// <summary>
	/// Checks a <c>-select</c> value and resolves every component's tag, hash or default branch to its commit, without
	/// touching the filesystem: every entry names a registered component, either bare (the tip of its default branch)
	/// or with an annotated tag or a commit hash, and the selection covers its own reference closure. Returns the
	/// closure in dependency order with each component's selector (the branch name for a bare entry) and resolved commit.
	/// </summary>
	internal static async Task<(IReadOnlyList<ComponentDefinition> Closure, IReadOnlyList<(ComponentDefinition Component, string CommitIsh, string Commit)> Resolved)>
		ResolveSelectionAsync(Monorepo monorepo, string select)
	{
		var selected = ComponentSelection.ParseList("-select", select, allowBare: true);
		if (selected.Count == 0)
		{
			throw new AgentException("-select names no component; expected <component>[@<tag|hash>][,<component>[@<tag|hash>]...].");
		}

		foreach (var selection in selected)
		{
			if (monorepo.FindComponent(selection.Component) is null)
			{
				throw new AgentException($"Component '{selection.Component}' is not registered in components.toml.");
			}
		}

		var closure = monorepo.Closure(selected.Select(selection => selection.Component));
		RequireCompleteClosure(selected, closure);

		var commitIshByName = selected.ToDictionary(selection => selection.Component, selection => selection.CommitIsh, StringComparer.Ordinal);
		var resolved = new List<(ComponentDefinition Component, string CommitIsh, string Commit)>();
		foreach (var component in closure)
		{
			resolved.Add(commitIshByName[component.Name] is { } commitIsh
				? (component, commitIsh, await ResolveCommitAsync(monorepo, component.Name, commitIsh))
				: await ResolveDefaultBranchAsync(monorepo, component));
		}

		return (closure, resolved);
	}

	private static string CompletedMessage(RunMetadata metadata)
	{
		if (metadata.Components.Any(component => component.ResultStatus == ResultStatus.Pushed))
		{
			return $"Agentic run '{metadata.RunId}' completed.";
		}

		// A clean run with nothing to commit usually means the agent answered inline instead of editing files
		// (e.g. it lacked permission to write) or wrote outside every component checkout.
		return $"Agentic run '{metadata.RunId}' completed, but the agent command changed no component; nothing was committed. " +
			$"Check that the command may edit files and that it writes inside a component folder of '{metadata.WorkspacePath}'.";
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
	/// Resolves a bare <c>-select</c> entry: the tip of the component's default branch (the branch its source-of-truth
	/// repo's HEAD names) at this moment. The branch name is recorded as the selector and the commit as the run's
	/// immutable provenance, so the record stays reproducible although the branch moves on.
	/// </summary>
	private static async Task<(ComponentDefinition Component, string CommitIsh, string Commit)> ResolveDefaultBranchAsync(Monorepo monorepo, ComponentDefinition component)
	{
		var sourceDir = RequireSourceDir(monorepo, component.Name);
		var source = GitClient.In(sourceDir);
		var head = await source.RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"]);
		var branch = head.ExitCode == 0 ? head.Output.Trim() : "";
		if (branch.Length == 0)
		{
			throw new AgentException(
				$"Component '{component.Name}' ('{sourceDir}') has no default branch: its HEAD is detached. " +
				$"Select it as {component.Name}@<tag|hash>.");
		}

		var commit = await source.RunAsync(["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}"]);
		if (commit.ExitCode != 0)
		{
			throw new AgentException(
				$"Component '{component.Name}' ('{sourceDir}') has no commit on its default branch '{branch}'. " +
				$"Select it as {component.Name}@<tag|hash>.");
		}

		return (component, branch, commit.Output.Trim());
	}

	private static string RequireSourceDir(Monorepo monorepo, string componentName)
	{
		var sourceDir = monorepo.SourceRepoDir(componentName);
		return Directory.Exists(sourceDir)
			? sourceDir
			: throw new AgentException($"Component '{componentName}' has no local repository at '{sourceDir}'. Run 'bassia component add' first.");
	}

	/// <summary>
	/// Resolves a component's commit in its source-of-truth repo. The commit-ish must be an annotated tag or a commit
	/// hash (see <see cref="IsHashSelector"/>), since it is logged as the run's immutable provenance.
	/// </summary>
	private static async Task<string> ResolveCommitAsync(Monorepo monorepo, string componentName, string commitIsh)
	{
		var sourceDir = RequireSourceDir(monorepo, componentName);
		var source = GitClient.In(sourceDir);
		if (IsHashSelector(commitIsh) && await ResolveHashAsync(source, sourceDir, componentName, commitIsh) is { } commit)
		{
			return commit;
		}

		var type = await source.RunAsync(["cat-file", "-t", commitIsh]);
		if (type.ExitCode != 0)
		{
			throw new AgentException($"'{commitIsh}' does not exist in component '{componentName}' ('{sourceDir}').");
		}

		if (type.Output.Trim() != "tag")
		{
			throw new AgentException($"'{componentName}@{commitIsh}' is a {type.Output.Trim()}, not an annotated tag. Select an annotated tag or a commit hash (or the bare component name for its default branch's tip), or create a tag with: git -C \"{sourceDir}\" tag -a <name> -m <message>");
		}

		return await source.RunOrThrowAsync(["rev-parse", "--verify", $"{commitIsh}^{{commit}}"]);
	}

	/// <summary>
	/// A selector read as a commit hash: 6 to 64 lowercase hex digits (a SHA-1 or SHA-256 hash or a prefix of one).
	/// Anything else is a ref name.
	/// </summary>
	internal static bool IsHashSelector(string commitIsh) =>
		commitIsh.Length is >= 6 and <= 64 && commitIsh.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

	/// <summary>
	/// Resolves a hash selector to its commit, or returns null when it is only a ref name (no object hash starts with
	/// it), so the caller resolves it as a tag. The selector must mean one thing: a prefix shared by several objects
	/// fails with git's own ambiguity error, and one that is both a ref and an object hash prefix fails too, where git
	/// would silently prefer the ref.
	/// </summary>
	private static async Task<string?> ResolveHashAsync(GitClient source, string sourceDir, string componentName, string hash)
	{
		// A hash selector is all hex digits, so it can never be mistaken for an option.
		var symbolic = await source.RunAsync(["rev-parse", "--symbolic-full-name", hash]);
		var reference = symbolic.ExitCode == 0 ? symbolic.Output.Trim() : "";
		var candidates = (await source.RunOrThrowAsync(["rev-parse", $"--disambiguate={hash}"]))
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		if (candidates.Length == 0)
		{
			return reference.Length > 0 ? null : throw new AgentException($"'{hash}' does not exist in component '{componentName}' ('{sourceDir}').");
		}

		if (reference.Length > 0)
		{
			throw new AgentException(
				$"'{componentName}@{hash}' is ambiguous: it names the ref '{reference}' and is also the prefix of the object hash {candidates[0]}" +
				(candidates.Length > 1 ? $" (and {candidates.Length - 1} more)" : "") +
				$". Select the ref as '{componentName}@{reference}' or the commit with a longer hash.");
		}

		var resolved = await source.RunAsync(["rev-parse", "--verify", hash]);
		if (resolved.ExitCode != 0)
		{
			throw new AgentException($"'{componentName}@{hash}' does not name a single object in component '{componentName}' ('{sourceDir}'); select it with a longer hash. git: {resolved.Error.Trim()}");
		}

		var commit = resolved.Output.Trim();
		var type = (await source.RunOrThrowAsync(["cat-file", "-t", commit])).Trim();
		return type == "commit"
			? commit
			: throw new AgentException($"'{componentName}@{hash}' is a {type}, not a commit. Select a commit hash or an annotated tag.");
	}

	// ----- materialization -----

	private static async Task MaterializeAsync(GitClient git, Monorepo monorepo, RunMetadata metadata, IReadOnlyList<ComponentDefinition> closure, AgentRunContext? context)
	{
		var runs = metadata.Components.ToDictionary(component => component.Name, StringComparer.Ordinal);

		foreach (var component in metadata.Components)
		{
			(context?.Cancellation ?? CancellationToken.None).ThrowIfCancellationRequested();
			Report(context, metadata.RunId, AgentRunPhase.Preparing,
				$"{metadata.RunId}: checkout of '{component.Name}' @ {component.CommitIsh} ({component.Commit[..7]}) -> '{component.Path}'.");
			Directory.CreateDirectory(Path.GetDirectoryName(component.Path)!);
			await git.RunOrThrowAsync(["clone", "--quiet", "--no-checkout", monorepo.SourceRepoDir(component.Name), component.Path]);
			await GitClient.In(component.Path).RunOrThrowAsync(["checkout", "--quiet", "-b", component.Branch, component.Commit]);
		}

		// A referenced component appears inside the referencing component as a link (junction) to its sibling
		// checkout, so every reference to it in the run resolves to the same working tree and the same commit.
		foreach (var definition in closure)
		{
			var owner = runs[definition.Name];
			foreach (var reference in definition.References)
			{
				var target = runs[reference.Name];
				var linkPath = Path.Combine(owner.Path, reference.Path);
				if (Directory.Exists(linkPath) || File.Exists(linkPath))
				{
					throw new AgentException($"Component '{definition.Name}' already contains '{reference.Path}'; cannot nest component '{reference.Name}' there.");
				}

				Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
				await DirectoryLinks.CreateAsync(linkPath, target.Path);
				owner.Junctions[reference.Path] = reference.Name;

				// The nested component's files belong to its own repo: hide them from the owner's index
				// without touching the owner's tracked .gitignore. No trailing slash: git sees a symlink as a file,
				// and a "/path/" pattern matches directories only, so the owner would commit the link itself.
				var excludePath = Path.Combine(owner.Path, ".git", "info", "exclude");
				Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
				await File.AppendAllTextAsync(excludePath, $"/{reference.Path.Replace('\\', '/')}\n");
			}
		}
	}

	private static void DiscardRunFolder(RunMetadata metadata)
	{
		// Remove links first so a recursive delete can never reach into another checkout.
		foreach (var component in metadata.Components)
		{
			foreach (var junction in component.Junctions.Keys)
			{
				DirectoryLinks.Delete(Path.Combine(component.Path, junction));
			}
		}

		if (Directory.Exists(metadata.WorkspacePath))
		{
			ClearReadOnlyAttributes(metadata.WorkspacePath);
			Directory.Delete(metadata.WorkspacePath, recursive: true);
		}
	}

	// Git marks pack files read-only, which makes Directory.Delete fail on Windows.
	private static void ClearReadOnlyAttributes(string directory)
	{
		foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}
	}

	// ----- agent process -----

	/// <summary>
	/// Runs the agent command in the run folder and returns its exit code, or <c>null</c> when it was cancelled and
	/// its process tree killed. Without <see cref="AgentRunContext.OnOutput"/> the process inherits this console, so
	/// a caller of <c>bassia run start</c> sees the agent's output as it happens; with one, both pipes are redirected
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

	// ----- commit / tag / push -----

	/// <summary>
	/// Commits the component's working tree on its run branch, tags the commit and pushes branch + tag to the
	/// source-of-truth repo. Idempotent, so a partially failed sequence can be retried: steps already done are skipped.
	/// </summary>
	private static async Task FinalizeComponentAsync(Monorepo monorepo, RunMetadata metadata, ComponentRun component, AgentRunContext? context = null)
	{
		if (component.ResultStatus is ResultStatus.Pushed or ResultStatus.Unchanged)
		{
			return;
		}

		var checkout = GitClient.In(component.Path);
		try
		{
			if (component.ResultCommit is null)
			{
				await checkout.RunOrThrowAsync(["add", "--all"]);
				var status = await checkout.RunOrThrowAsync(["status", "--porcelain"]);
				if (status.Length == 0)
				{
					component.ResultStatus = ResultStatus.Unchanged;
					Report(context, metadata.RunId, AgentRunPhase.Finalizing, $"{metadata.RunId}: '{component.Name}' unchanged.");
					return;
				}
			}

			// The tag is settled before the commit exists so the commit message can name it. A retry keeps the tag
			// recorded by the earlier attempt.
			var tag = component.ResultTag ??= await NextResultTagAsync(checkout, metadata.RunId);
			if (component.ResultCommit is null)
			{
				var message = ResultCommitMessage.Render(monorepo.CommitSubject, metadata, component, tag, SummarizeCommand(metadata.Command));
				await checkout.RunOrThrowAsync(["commit", "--quiet", "-m", message]);
				component.ResultCommit = await checkout.RunOrThrowAsync(["rev-parse", "HEAD"]);
				component.ResultStatus = ResultStatus.Committed;
			}

			var existingTag = await checkout.RunAsync(["rev-parse", "--quiet", "--verify", $"refs/tags/{tag}^{{commit}}"]);
			if (existingTag.ExitCode != 0)
			{
				await checkout.RunOrThrowAsync(["tag", "-a", tag, "-m", $"{metadata.RunId}: result of the agentic run in component '{component.Name}'", component.ResultCommit]);
			}
			else if (existingTag.Output.Trim() != component.ResultCommit)
			{
				throw new GitException($"tag '{tag}' already exists in '{component.Path}' and does not point at the result commit {component.ResultCommit[..7]}.");
			}

			await checkout.RunOrThrowAsync(["push", "--quiet", monorepo.SourceRepoDir(component.Name),
				$"refs/heads/{component.Branch}:refs/heads/{component.Branch}", $"refs/tags/{tag}:refs/tags/{tag}"]);
			component.ResultStatus = ResultStatus.Pushed;
			component.ResultError = null;
			Report(context, metadata.RunId, AgentRunPhase.Finalizing, $"{metadata.RunId}: '{component.Name}' committed as {component.ResultCommit[..7]}, tagged {tag}, pushed to '{monorepo.SourceRepoDir(component.Name)}'.");
		}
		catch (GitException ex)
		{
			component.ResultStatus = ResultStatus.Failed;
			component.ResultError = ex.Message;
			Report(context, metadata.RunId, AgentRunPhase.Finalizing, $"{metadata.RunId}: '{component.Name}' failed: {ex.Message}");
		}
	}

	/// <summary>
	/// <c>agent-run/&lt;key&gt;/&lt;counter&gt;</c> with the next unused counter. Every result tag of the run sits on the
	/// run branch of this checkout (cloned from the source of truth, so earlier pushed results are visible too), which
	/// makes the checkout's own tag list the complete sequence.
	/// </summary>
	internal static async Task<string> NextResultTagAsync(GitClient checkout, string runId)
	{
		var tags = await checkout.RunOrThrowAsync(["tag", "--list", $"{RunMetadata.RefBase(runId)}/*"]);
		return RunMetadata.TagName(runId, RunMetadata.HighestIndex(tags.Split('\n', StringSplitOptions.RemoveEmptyEntries)) + 1);
	}

	/// <summary>
	/// One-line summary for commit subjects: the longest quoted part of the command (usually the prompt) when
	/// there is one, otherwise the command itself, truncated to a conventional subject length.
	/// </summary>
	internal static string SummarizeCommand(string command)
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

	// ----- bassia run retry / abandon -----

	/// <summary>Finishes the commit/tag/push sequence of a run whose earlier attempt failed part-way.</summary>
	internal static async Task<AgentRunOutcome> RetryAsync(Monorepo monorepo, RunMetadataStore store, RunMetadata metadata)
	{
		if (metadata.Status is "abandoned")
		{
			throw new AgentException($"Agentic run '{metadata.RunId}' was abandoned; nothing to retry.");
		}

		if (metadata.Status is "started" or "failed" or "cancelled")
		{
			throw new AgentException($"Agentic run '{metadata.RunId}' has status '{metadata.Status}'; only runs whose commit/push sequence failed can be retried.");
		}

		foreach (var component in metadata.Components)
		{
			await FinalizeComponentAsync(monorepo, metadata, component);
		}

		metadata.Status = OverallStatus(metadata);
		metadata.Finished = RunMetadata.Timestamp();
		var tag = await store.CommitAsync(metadata);
		var ok = metadata.Status == "completed";
		return new AgentRunOutcome(ok,
			ok ? $"Agentic run '{metadata.RunId}' completed." : $"Agentic run '{metadata.RunId}' still has failed components.",
			metadata, store, [tag]);
	}

	/// <summary>Discards a run's folder and records the run as abandoned; what it pushed stays where it is.</summary>
	internal static async Task<AgentRunOutcome> AbandonAsync(RunMetadataStore store, RunMetadata metadata)
	{
		if (metadata.Status is "abandoned")
		{
			throw new AgentException($"Agentic run '{metadata.RunId}' is already abandoned.");
		}

		DiscardRunFolder(metadata);
		metadata.Status = "abandoned";
		metadata.Finished = RunMetadata.Timestamp();
		var tag = await store.CommitAsync(metadata);
		return new AgentRunOutcome(true,
			$"Agentic run '{metadata.RunId}' abandoned; its run folder was discarded. Results already pushed to source-of-truth repos were left in place.",
			metadata, store, [tag]);
	}

	/// <summary>Records a run whose process is gone without its final record (killed, crashed) as cancelled.</summary>
	internal static async Task<string> RecordCancelledAsync(RunMetadataStore store, RunMetadata metadata)
	{
		metadata.Status = "cancelled";
		metadata.Finished ??= RunMetadata.Timestamp();
		return await store.CommitAsync(metadata);
	}

	// ----- output -----

	internal static Dictionary<string, object?> ResultData(RunMetadata metadata, RunMetadataStore store, IReadOnlyList<string> metadataTags) => new()
	{
		["run_id"] = metadata.RunId,
		["status"] = metadata.Status,
		["workspace"] = metadata.WorkspacePath,
		["agent_exit_code"] = metadata.AgentExitCode,
		["metadata_repo"] = store.RepoDir,
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
			["result_error"] = component.ResultError
		}).ToList()
	};

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
