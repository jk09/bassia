namespace Bassia;

using System.Diagnostics;
using Bassia.Git;

internal sealed class AgentException(string message) : Exception(message);

/// <summary>One <c>component@commit-ish</c> pair from <c>-select</c> or <c>-pin</c>.</summary>
internal sealed record ComponentSelection(string Component, string CommitIsh)
{
	/// <summary>Parses a comma-separated list of <c>component@commit-ish</c> pairs.</summary>
	public static List<ComponentSelection> ParseList(string option, string value)
	{
		var selections = new List<ComponentSelection>();
		foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var separator = item.LastIndexOf('@');
			if (separator <= 0 || separator == item.Length - 1)
			{
				throw new AgentException($"Invalid {option} entry '{item}': expected <component>@<commit-ish>.");
			}

			var selection = new ComponentSelection(item[..separator].Trim(), item[(separator + 1)..].Trim());
			if (selections.Any(existing => existing.Component == selection.Component))
			{
				throw new AgentException($"Component '{selection.Component}' is listed more than once in {option}.");
			}

			selections.Add(selection);
		}

		return selections;
	}
}

/// <summary>
/// <c>bassia agent</c>: materializes a selected monorepo state in an isolated workspace area, runs an agent
/// command there, and commits/tags/pushes the resulting component changes back to the source-of-truth repos.
/// </summary>
internal static class AgentCommand
{
	private const string RunIdPrefix = "agentic-run-";

	public static async Task<int> RunAsync(GitClient git, string[] args)
	{
		if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
		{
			return ProgramCli.WriteResult(false, "agent", Usage);
		}

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
		"Usage: bassia agent -select <component@tag>[,<component@tag>...] [-pin <component@tag>[,...]] -run <agent command>\n" +
		"       bassia agent retry <run-id>\n" +
		"       bassia agent abandon <run-id>";

	private static Monorepo LoadMonorepo()
	{
		var root = Monorepo.FindRoot(Environment.CurrentDirectory)
			?? throw new AgentException($"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia setup init' first.");
		return Monorepo.Load(root);
	}

	// ----- bassia agent -select ... -run ... -----

	private static async Task<int> StartAsync(GitClient git, string[] args)
	{
		var (select, pin, command) = ParseStartArguments(args);
		var monorepo = LoadMonorepo();

		var selected = ComponentSelection.ParseList("-select", select);
		var pinned = pin is null ? [] : ComponentSelection.ParseList("-pin", pin);
		foreach (var selection in selected.Concat(pinned))
		{
			if (monorepo.FindComponent(selection.Component) is null)
			{
				throw new AgentException($"Component '{selection.Component}' is not registered in components.toml.");
			}
		}

		var closure = monorepo.Closure(selected.Select(selection => selection.Component));
		var selectedNames = selected.Select(selection => selection.Component).ToHashSet(StringComparer.Ordinal);
		foreach (var pinnedSelection in pinned)
		{
			if (selectedNames.Contains(pinnedSelection.Component))
			{
				throw new AgentException($"Component '{pinnedSelection.Component}' is both selected and pinned; use -select alone.");
			}

			if (closure.All(component => component.Name != pinnedSelection.Component))
			{
				throw new AgentException($"Pinned component '{pinnedSelection.Component}' is not referenced by any selected component.");
			}
		}

		// Resolve every commit-ish before touching the filesystem, so a bad selection leaves no trace.
		var commitIshByName = selected.Concat(pinned).ToDictionary(selection => selection.Component, selection => selection.CommitIsh, StringComparer.Ordinal);
		var resolved = new List<(ComponentDefinition Component, string CommitIsh, string Commit)>();
		foreach (var component in closure)
		{
			var explicitCommitIsh = commitIshByName.GetValueOrDefault(component.Name);
			var commit = await ResolveCommitAsync(git, monorepo, component.Name, explicitCommitIsh);
			resolved.Add((component, explicitCommitIsh ?? "HEAD", commit));
		}

		var store = new RunMetadataStore(git, monorepo.RunsRepoDir);
		await store.EnsureRepositoryAsync();
		var runId = await AllocateRunIdAsync(git, monorepo, store, closure.Select(component => component.Name));
		var runDir = Path.Combine(monorepo.WorkspaceDir, runId);
		var cacheDir = Path.Combine(monorepo.CacheDir, runId);
		var branch = $"{RunMetadata.TagPrefix}/{runId}";

		var metadata = new RunMetadata
		{
			RunId = runId,
			Status = "started",
			Created = RunMetadata.Timestamp(),
			Select = select,
			Pin = pin,
			Command = command,
			WorkspacePath = runDir,
			CachePath = cacheDir
		};

		foreach (var (component, commitIsh, commit) in resolved)
		{
			var isSelected = selectedNames.Contains(component.Name);
			metadata.Components.Add(new ComponentRun
			{
				Name = component.Name,
				CommitIsh = commitIsh,
				Commit = commit,
				Materialization = isSelected ? Materialization.Checkout : Materialization.Cache,
				Path = Path.Combine(isSelected ? runDir : cacheDir, component.Name),
				Branch = branch
			});
		}

		try
		{
			await MaterializeAsync(git, monorepo, metadata, closure);
		}
		catch
		{
			DiscardRunFolders(metadata);
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
			await FinalizeComponentAsync(git, monorepo, store, metadata, component);
		}

		metadata.Status = OverallStatus(metadata);
		var finalTag = await store.CommitAsync(metadata);
		var ok = metadata.Status == "completed";
		return ProgramCli.WriteResult(ok, "agent",
			ok ? $"Agentic run '{runId}' completed." : $"Agentic run '{runId}' finished with failures; run 'bassia agent retry {runId}' or 'bassia agent abandon {runId}'.",
			ResultData(metadata, store, [startTag, finalTag]));
	}

	private static (string Select, string? Pin, string Command) ParseStartArguments(string[] args)
	{
		string? select = null;
		string? pin = null;
		string? command = null;

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "-select" or "--select":
					select = string.Join(",", new[] { select, RequireValue(args, ref i) }.Where(value => value is not null));
					break;
				case "-pin" or "--pin":
					pin = string.Join(",", new[] { pin, RequireValue(args, ref i) }.Where(value => value is not null));
					break;
				case "-run" or "--run":
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

		return (select, pin, command);
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
	/// Resolves a component's commit in its source-of-truth repo. Explicit commit-ishes must be annotated tags,
	/// since they are logged as the run's immutable provenance; unpinned nested components use the repo's HEAD.
	/// </summary>
	private static async Task<string> ResolveCommitAsync(GitClient git, Monorepo monorepo, string componentName, string? commitIsh)
	{
		var sourceDir = monorepo.SourceRepoDir(componentName);
		if (!Directory.Exists(sourceDir))
		{
			throw new AgentException($"Component '{componentName}' has no local repository at '{sourceDir}'. Run 'bassia setup add-component' first.");
		}

		var source = git.In(sourceDir);
		if (commitIsh is null)
		{
			var head = await source.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD^{commit}"]);
			if (head.ExitCode != 0)
			{
				throw new AgentException($"Component '{componentName}' has no commits at HEAD; pin it with -pin {componentName}@<tag>.");
			}

			return head.Output.Trim();
		}

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

	private static async Task<string> AllocateRunIdAsync(GitClient git, Monorepo monorepo, RunMetadataStore store, IEnumerable<string> componentNames)
	{
		Directory.CreateDirectory(monorepo.WorkspaceDir);
		var used = new HashSet<string>(await store.ListRunIdsAsync(), StringComparer.Ordinal);
		foreach (var directory in Directory.EnumerateDirectories(monorepo.WorkspaceDir, $"{RunIdPrefix}*"))
		{
			used.Add(Path.GetFileName(directory));
		}

		// Result branches/tags are named after the run id, so ids already present in a source-of-truth repo
		// (e.g. from a run on another workspace) must not be reused.
		foreach (var componentName in componentNames)
		{
			var refs = await git.In(monorepo.SourceRepoDir(componentName)).RunOrThrowAsync(
				["for-each-ref", "--format=%(refname)", $"refs/heads/{RunMetadata.TagPrefix}/", $"refs/tags/{RunMetadata.TagPrefix}/"]);
			foreach (var reference in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = reference.Split('/');
				if (parts.Length >= 4)
				{
					used.Add(parts[3]);
				}
			}
		}

		for (var number = 1; ; number++)
		{
			var runId = $"{RunIdPrefix}{number}";
			var runDir = Path.Combine(monorepo.WorkspaceDir, runId);
			if (used.Contains(runId) || Directory.Exists(runDir))
			{
				continue;
			}

			Directory.CreateDirectory(runDir);
			return runId;
		}
	}

	// ----- materialization -----

	private static async Task MaterializeAsync(GitClient git, Monorepo monorepo, RunMetadata metadata, IReadOnlyList<ComponentDefinition> closure)
	{
		var runs = metadata.Components.ToDictionary(component => component.Name, StringComparer.Ordinal);

		foreach (var component in metadata.Components)
		{
			Progress($"{metadata.RunId}: {component.Materialization.ToString().ToLowerInvariant()} of '{component.Name}' @ {component.CommitIsh} ({component.Commit[..7]}) -> '{component.Path}'.");
			Directory.CreateDirectory(Path.GetDirectoryName(component.Path)!);
			await git.RunOrThrowAsync(["clone", "--quiet", "--no-checkout", monorepo.SourceRepoDir(component.Name), component.Path]);
			await git.In(component.Path).RunOrThrowAsync(["checkout", "--quiet", "-b", component.Branch, component.Commit]);
		}

		// Nested components appear inside their referencing component as links (junctions) to the single
		// checkout of that component: a cache checkout, or the direct checkout when it was also selected.
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

	private static void DiscardRunFolders(RunMetadata metadata)
	{
		// Remove links first so a recursive delete can never reach into another checkout.
		foreach (var component in metadata.Components)
		{
			foreach (var junction in component.Junctions.Keys)
			{
				DirectoryLinks.Delete(Path.Combine(component.Path, junction));
			}
		}

		foreach (var directory in new[] { metadata.WorkspacePath, metadata.CachePath })
		{
			if (Directory.Exists(directory))
			{
				ClearReadOnlyAttributes(directory);
				Directory.Delete(directory, recursive: true);
			}
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
	private static async Task FinalizeComponentAsync(GitClient git, Monorepo monorepo, RunMetadataStore store, RunMetadata metadata, ComponentRun component)
	{
		if (component.ResultStatus is ResultStatus.Pushed or ResultStatus.Unchanged)
		{
			return;
		}

		var checkout = git.In(component.Path);
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

				await checkout.RunOrThrowAsync(["commit", "--quiet", "-m", ComponentCommitMessage(store, metadata, component)]);
				component.ResultCommit = await checkout.RunOrThrowAsync(["rev-parse", "HEAD"]);
				component.ResultStatus = ResultStatus.Committed;
			}

			var tag = component.ResultTag ?? RunMetadata.TagName(metadata.RunId, 0);
			var existingTag = await checkout.RunAsync(["rev-parse", "--quiet", "--verify", $"refs/tags/{tag}^{{commit}}"]);
			if (existingTag.ExitCode != 0 || existingTag.Output.Trim() != component.ResultCommit)
			{
				// The checkout is private to this run, so a stale tag (e.g. one cloned from the source repo) may be
				// replaced here; the push below never forces, so the source of truth is still protected.
				await checkout.RunOrThrowAsync(["tag", "--force", "-a", tag, "-m", $"{metadata.RunId}: result of the agentic run in component '{component.Name}'", component.ResultCommit]);
			}

			component.ResultTag = tag;
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

	private static string ComponentCommitMessage(RunMetadataStore store, RunMetadata metadata, ComponentRun component)
	{
		return $"agent({metadata.RunId}): {SummarizeCommand(metadata.Command)}\n\n" +
			$"Agentic run: {metadata.RunId}\n" +
			$"Run metadata: {RunMetadata.TagName(metadata.RunId, 0)} in {store.RepoDir}\n" +
			$"Selection: {metadata.Select}\n" +
			(metadata.Pin is null ? "" : $"Pinned: {metadata.Pin}\n") +
			$"Component: {component.Name} @ {component.CommitIsh} ({component.Commit})\n" +
			$"Command: {metadata.Command}\n";
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
			await FinalizeComponentAsync(git, monorepo, store, metadata, component);
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

		DiscardRunFolders(metadata);
		metadata.Status = "abandoned";
		metadata.Finished = RunMetadata.Timestamp();
		var tag = await store.CommitAsync(metadata);
		return ProgramCli.WriteResult(true, "agent abandon",
			$"Agentic run '{metadata.RunId}' abandoned; its workspace and cache were discarded. Results already pushed to source-of-truth repos were left in place.",
			ResultData(metadata, store, [tag]));
	}

	private static async Task<(Monorepo, RunMetadataStore, RunMetadata)> LoadRunAsync(GitClient git, string[] args, string subcommand)
	{
		if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
		{
			throw new AgentException($"Usage: bassia agent {subcommand} <run-id>");
		}

		var monorepo = LoadMonorepo();
		var store = new RunMetadataStore(git, monorepo.RunsRepoDir);
		var metadata = Directory.Exists(store.RepoDir) ? await store.LoadLatestAsync(args[0]) : null;
		return (monorepo, store, metadata ?? throw new AgentException($"Unknown agentic run '{args[0]}'."));
	}

	// ----- output -----

	private static Dictionary<string, object?> ResultData(RunMetadata metadata, RunMetadataStore store, IReadOnlyList<string> metadataTags) => new()
	{
		["runId"] = metadata.RunId,
		["status"] = metadata.Status,
		["workspace"] = metadata.WorkspacePath,
		["cache"] = metadata.CachePath,
		["agentExitCode"] = metadata.AgentExitCode,
		["metadataRepo"] = store.RepoDir,
		["metadataTags"] = metadataTags,
		["components"] = metadata.Components.Select(component => new Dictionary<string, object?>
		{
			["name"] = component.Name,
			["commitish"] = component.CommitIsh,
			["commit"] = component.Commit,
			["materialization"] = component.Materialization.ToString().ToLowerInvariant(),
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
