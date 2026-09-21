namespace Bassia.CliCommands.Agent;

using System.Diagnostics;
using Bassia;
using Bassia.Git;

/// <summary>
/// <c>bassia agent</c>: materializes a selected monorepo state in an isolated workspace area, runs an agent
/// command there, and commits/tags/pushes the resulting component changes back to the source-of-truth repos.
/// </summary>
internal static class AgentCommand
{
	public static async Task<int> RunAsync(string[] args)
	{
		if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
		{
			return ProgramCli.WriteResult(false, "agent", Usage);
		}

		var git = new GitClient(Environment.CurrentDirectory);

		try
		{
			return args[0].ToLowerInvariant() switch
			{
				"retry" => await RetryAsync(git, args[1..]),
				"abandon" => await AbandonAsync(git, args[1..]),
				_ => await StartAsync(git, args)
			};
		}
		catch (Exception ex) when (ex is AgentException or MonorepoException or GitException or IOException or UnauthorizedAccessException)
		{
			return ProgramCli.WriteResult(false, "agent", ex.Message);
		}
	}

	private const string Usage =
		"Usage: bassia agent -select <component@tag>[,<component@tag>...] -run <agent command>\n" +
		"       bassia agent retry <run-id>\n" +
		"       bassia agent abandon <run-id>";

	private static Monorepo LoadMonorepo()
	{
		var root = Monorepo.FindRoot(Environment.CurrentDirectory)
			?? throw new AgentException($"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia init' first.");
		return Monorepo.Load(root);
	}

	// ----- bassia agent -select ... -run ... -----

	private static async Task<int> StartAsync(GitClient git, string[] args)
	{
		var (select, command) = ParseStartArguments(args);
		var monorepo = LoadMonorepo();

		var selected = ComponentSelection.ParseList("-select", select);
		foreach (var selection in selected)
		{
			if (monorepo.FindComponent(selection.Component) is null)
			{
				throw new AgentException($"Component '{selection.Component}' is not registered in components.toml.");
			}
		}

		var closure = monorepo.Closure(selected.Select(selection => selection.Component));
		RequireCompleteClosure(selected, closure);

		// Resolve every commit-ish before touching the filesystem, so a bad selection leaves no trace.
		var commitIshByName = selected.ToDictionary(selection => selection.Component, selection => selection.CommitIsh, StringComparer.Ordinal);
		var resolved = new List<(ComponentDefinition Component, string CommitIsh, string Commit)>();
		foreach (var component in closure)
		{
			var commitIsh = commitIshByName[component.Name];
			resolved.Add((component, commitIsh, await ResolveCommitAsync(monorepo, component.Name, commitIsh)));
		}

		var store = new RunMetadataStore(git, monorepo.RunsRepoDir);
		await store.EnsureRepositoryAsync();
		var runId = RunMetadata.NewRunId();
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
			await MaterializeAsync(git, monorepo, metadata, closure);
		}
		catch
		{
			DiscardRunFolder(metadata);
			throw;
		}

		var startTag = await store.CommitAsync(metadata);
		Progress($"{runId}: metadata committed as {startTag} in '{store.RepoDir}'.");
		Progress($"{runId}: running agent command in '{runDir}'.");

		metadata.AgentExitCode = await RunAgentProcessAsync(monorepo, metadata);
		metadata.Finished = RunMetadata.Timestamp();

		if (metadata.AgentExitCode != 0)
		{
			metadata.Status = "failed";
			var failedTag = await store.CommitAsync(metadata);
			return ProgramCli.WriteResult(false, "agent",
				$"Agent command exited with code {metadata.AgentExitCode}; nothing was committed. The workspace '{runDir}' is kept for inspection.",
				ResultData(metadata, store, [startTag, failedTag]));
		}

		foreach (var component in metadata.Components)
		{
			await FinalizeComponentAsync(monorepo, metadata, component);
		}

		metadata.Status = OverallStatus(metadata);
		var finalTag = await store.CommitAsync(metadata);
		var ok = metadata.Status == "completed";
		return ProgramCli.WriteResult(ok, "agent",
			ok ? CompletedMessage(metadata) : $"Agentic run '{runId}' finished with failures; run 'bassia agent retry {runId}' or 'bassia agent abandon {runId}'.",
			ResultData(metadata, store, [startTag, finalTag]));
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

	private static (string Select, string Command) ParseStartArguments(string[] args)
	{
		string? select = null;
		string? command = null;

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "-select":
					select = string.Join(",", new[] { select, RequireValue(args, ref i) }.Where(value => value is not null));
					break;
				case "-run":
					// -run takes the rest of the command line: either one quoted string or the command's own words.
					var commandArguments = args[(i + 1)..];
					if (commandArguments.Length == 0)
					{
						throw new AgentException("-run requires the agent command to run.");
					}

					command = commandArguments.Length == 1
						? commandArguments[0]
						: string.Join(' ', commandArguments.Select(argument => argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument));
					i = args.Length;
					break;
				default:
					throw new AgentException($"Unknown argument '{args[i]}'.\n{Usage}");
			}
		}

		if (string.IsNullOrWhiteSpace(select) || string.IsNullOrWhiteSpace(command))
		{
			throw new AgentException($"Both -select and -run are required.\n{Usage}");
		}

		return (select, command);
	}

	/// <summary>
	/// A run must select every component it materializes: the reference closure of the selection has to be the
	/// selection itself. Inferring a version for a component nobody named would put a mutable reference into an
	/// otherwise tag-pinned, reproducible run record.
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
			$"Add each as <component>@<tag>, for example: -select {selected[0].Component}@{selected[0].CommitIsh},{missing[0]}@<tag>.");
	}

	private static string RequireValue(string[] args, ref int index)
	{
		if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
		{
			throw new AgentException($"{args[index]} requires a value.");
		}

		return args[++index];
	}

	/// <summary>
	/// Resolves a component's commit in its source-of-truth repo. The commit-ish must be an annotated tag, since it
	/// is logged as the run's immutable provenance.
	/// </summary>
	private static async Task<string> ResolveCommitAsync(Monorepo monorepo, string componentName, string commitIsh)
	{
		var sourceDir = monorepo.SourceRepoDir(componentName);
		if (!Directory.Exists(sourceDir))
		{
			throw new AgentException($"Component '{componentName}' has no local repository at '{sourceDir}'. Run 'bassia add-component' first.");
		}

		var source = GitClient.In(sourceDir);
		var type = await source.RunAsync(["cat-file", "-t", commitIsh]);
		if (type.ExitCode != 0)
		{
			throw new AgentException($"'{commitIsh}' does not exist in component '{componentName}' ('{sourceDir}').");
		}

		if (type.Output.Trim() != "tag")
		{
			throw new AgentException($"'{componentName}@{commitIsh}' is a {type.Output.Trim()}, not an annotated tag. Create one with: git -C \"{sourceDir}\" tag -a <name> -m <message>");
		}

		return await source.RunOrThrowAsync(["rev-parse", "--verify", $"{commitIsh}^{{commit}}"]);
	}

	// ----- materialization -----

	private static async Task MaterializeAsync(GitClient git, Monorepo monorepo, RunMetadata metadata, IReadOnlyList<ComponentDefinition> closure)
	{
		var runs = metadata.Components.ToDictionary(component => component.Name, StringComparer.Ordinal);

		foreach (var component in metadata.Components)
		{
			Progress($"{metadata.RunId}: checkout of '{component.Name}' @ {component.CommitIsh} ({component.Commit[..7]}) -> '{component.Path}'.");
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
				// without touching the owner's tracked .gitignore.
				var excludePath = Path.Combine(owner.Path, ".git", "info", "exclude");
				Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
				await File.AppendAllTextAsync(excludePath, $"/{reference.Path.Replace('\\', '/')}/\n");
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

	private static async Task<int> RunAgentProcessAsync(Monorepo monorepo, RunMetadata metadata)
	{
		var startInfo = new ProcessStartInfo
		{
			WorkingDirectory = metadata.WorkspacePath,
			UseShellExecute = false
		};

		if (OperatingSystem.IsWindows())
		{
			startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
			// /s keeps the quoted command intact; cmd strips only the outer quotes.
			startInfo.Arguments = $"/d /s /c \"{metadata.Command}\"";
		}
		else
		{
			startInfo.FileName = "/bin/sh";
			startInfo.ArgumentList.Add("-c");
			startInfo.ArgumentList.Add(metadata.Command);
		}

		startInfo.Environment["BASSIA_ROOT"] = monorepo.Root;
		startInfo.Environment["BASSIA_RUN_ID"] = metadata.RunId;
		startInfo.Environment["BASSIA_RUN_DIR"] = metadata.WorkspacePath;

		using var process = Process.Start(startInfo)
			?? throw new AgentException("Could not start the agent command.");
		await process.WaitForExitAsync();
		return process.ExitCode;
	}

	// ----- commit / tag / push -----

	/// <summary>
	/// Commits the component's working tree on its run branch, tags the commit and pushes branch + tag to the
	/// source-of-truth repo. Idempotent, so a partially failed sequence can be retried: steps already done are skipped.
	/// </summary>
	private static async Task FinalizeComponentAsync(Monorepo monorepo, RunMetadata metadata, ComponentRun component)
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
					Progress($"{metadata.RunId}: '{component.Name}' unchanged.");
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
			Progress($"{metadata.RunId}: '{component.Name}' committed as {component.ResultCommit[..7]}, tagged {tag}, pushed to '{monorepo.SourceRepoDir(component.Name)}'.");
		}
		catch (GitException ex)
		{
			component.ResultStatus = ResultStatus.Failed;
			component.ResultError = ex.Message;
			Progress($"{metadata.RunId}: '{component.Name}' failed: {ex.Message}");
		}
	}

	/// <summary>
	/// <c>agent/run-&lt;id&gt;/&lt;counter&gt;</c> with the next unused counter. Every result tag of the run sits on the
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

	// ----- bassia agent retry <run-id> -----

	private static async Task<int> RetryAsync(GitClient git, string[] args)
	{
		var (monorepo, store, metadata) = await LoadRunAsync(git, args, "retry");
		if (metadata.Status is "abandoned")
		{
			throw new AgentException($"Agentic run '{metadata.RunId}' was abandoned; nothing to retry.");
		}

		if (metadata.Status is "started" or "failed")
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
		return ProgramCli.WriteResult(ok, "agent retry",
			ok ? $"Agentic run '{metadata.RunId}' completed." : $"Agentic run '{metadata.RunId}' still has failed components.",
			ResultData(metadata, store, [tag]));
	}

	// ----- bassia agent abandon <run-id> -----

	private static async Task<int> AbandonAsync(GitClient git, string[] args)
	{
		var (_, store, metadata) = await LoadRunAsync(git, args, "abandon");
		if (metadata.Status is "abandoned")
		{
			throw new AgentException($"Agentic run '{metadata.RunId}' is already abandoned.");
		}

		DiscardRunFolder(metadata);
		metadata.Status = "abandoned";
		metadata.Finished = RunMetadata.Timestamp();
		var tag = await store.CommitAsync(metadata);
		return ProgramCli.WriteResult(true, "agent abandon",
			$"Agentic run '{metadata.RunId}' abandoned; its run folder was discarded. Results already pushed to source-of-truth repos were left in place.",
			ResultData(metadata, store, [tag]));
	}

	private static async Task<(Monorepo, RunMetadataStore, RunMetadata)> LoadRunAsync(GitClient git, string[] args, string subcommand)
	{
		if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
		{
			throw new AgentException($"Usage: bassia agent {subcommand} <run-id>");
		}

		var runId = RunMetadata.NormalizeRunId(args[0]);
		var monorepo = LoadMonorepo();
		var store = new RunMetadataStore(git, monorepo.RunsRepoDir);
		var metadata = Directory.Exists(store.RepoDir) ? await store.LoadLatestAsync(runId) : null;
		return (monorepo, store, metadata ?? throw new AgentException($"Unknown agentic run '{runId}'."));
	}

	// ----- output -----

	private static Dictionary<string, object?> ResultData(RunMetadata metadata, RunMetadataStore store, IReadOnlyList<string> metadataTags) => new()
	{
		["runId"] = metadata.RunId,
		["status"] = metadata.Status,
		["workspace"] = metadata.WorkspacePath,
		["agentExitCode"] = metadata.AgentExitCode,
		["metadataRepo"] = store.RepoDir,
		["metadataTags"] = metadataTags,
		["components"] = metadata.Components.Select(component => new Dictionary<string, object?>
		{
			["name"] = component.Name,
			["commitish"] = component.CommitIsh,
			["commit"] = component.Commit,
			["path"] = component.Path,
			["branch"] = component.Branch,
			["resultStatus"] = component.ResultStatus.ToString().ToLowerInvariant(),
			["resultCommit"] = component.ResultCommit,
			["resultTag"] = component.ResultTag,
			["resultError"] = component.ResultError
		}).ToList()
	};

	private static void Progress(string message) => Console.Error.WriteLine($"bassia: {message}");
}
