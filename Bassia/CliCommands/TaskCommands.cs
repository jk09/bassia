namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Core;

/// <summary>
/// <c>bassia task list | run</c> and <c>bassia affected</c>: the components' own tasks (<c>bassia.toml</c>), run over a
/// checkout of a selection, and which components a change affects.
/// </summary>
internal static class TaskCommands
{
	// ----- bassia task list -----

	public static async Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var graph = monorepo.Graph();
		var components = invocation.Get("name") is { } name ? [ComponentCommands.RequireComponent(monorepo, name)] : monorepo.Components.ToList();
		var entries = new List<IReadOnlyDictionary<string, object?>>();
		var rows = new List<IReadOnlyList<string>>();
		foreach (var component in components.Where(component => graph.Storage.Exists(component.Name)))
		{
			var store = graph.Storage.Open(component.Name);
			if (await store.DefaultBranchAsync() is not { } branch || await store.BranchTipAsync(branch) is not { } tip)
			{
				continue;
			}

			foreach (var task in (await TaskFile.ReadAsync(store, component.Name, tip)).Values)
			{
				rows.Add([component.Name, task.Name, task.DependsOn.Count == 0 ? "-" : string.Join(",", task.DependsOn), task.Command]);
				entries.Add(new Dictionary<string, object?>
				{
					["component"] = component.Name,
					["name"] = task.Name,
					["command"] = task.Command,
					["depends_on"] = task.DependsOn
				});
			}
		}

		return ProgramCli.WriteResult(true, invocation.Command,
			$"{entries.Count} task(s) in {entries.Select(entry => entry["component"]).Distinct().Count()} component(s), as each one's {TaskFile.FileName} defines them on its default branch.",
			new Dictionary<string, object?>
			{
				["table"] = rows.Count == 0 ? null : new TomlText(AsciiTable.Render(["COMPONENT", "TASK", "DEPENDS ON", "COMMAND"], rows, 72)),
				["task"] = entries
			});
	}

	// ----- bassia task run -----

	public static async Task<int> RunAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var targets = invocation.List("task");
		if (targets.Count == 0)
		{
			throw invocation.Usage("-task is required.");
		}

		var runDir = Environment.GetEnvironmentVariable("BASSIA_RUN_DIR");
		string folder;
		IReadOnlyList<string> selected;
		var materialized = false;
		var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!invocation.Has("select") && runDir is not null && Directory.Exists(runDir))
		{
			// Inside a run: its own checkout, as the agent left it.
			folder = runDir;
			selected = monorepo.Components.Select(component => component.Name).Where(name => Directory.Exists(Path.Combine(runDir, name))).ToList();
			foreach (var name in selected)
			{
				fingerprints[name] = await FolderFingerprintAsync(Path.Combine(runDir, name));
			}
		}
		else
		{
			var select = invocation.Get("select") ?? string.Join(",", monorepo.Components.Select(component => component.Name));
			var (closure, resolved) = await AgentCommand.ResolveSelectionAsync(monorepo, select);
			var key = RunMetadata.Key(RunMetadata.NewRunId());
			folder = Path.Combine(monorepo.WorkspaceDir, $"tasks-{key}");
			await Workspaces.MaterializeAsync(monorepo.Graph().Storage, closure,
				resolved.ToDictionary(entry => entry.Component.Name, entry => entry.Commit, StringComparer.Ordinal), folder, $"tasks/{key}");
			materialized = true;
			selected = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(entry => entry.Split('@')[0]).ToList();
			foreach (var (component, _, commit) in resolved)
			{
				fingerprints[component.Name] = $"commit:{commit}";
			}
		}

		var tasks = monorepo.Components.Where(component => Directory.Exists(Path.Combine(folder, component.Name)))
			.ToDictionary(component => component.Name, component => TaskFile.Read(Path.Combine(folder, component.Name), component.Name), StringComparer.Ordinal);
		var plan = TaskPlan.Plan(monorepo.Registry, selected, targets, tasks);
		var logs = Path.Combine(monorepo.WorkspaceDir, ".task-logs", Path.GetFileName(folder));
		var results = await TaskRunner.RunAsync(plan, folder, logs, new Dictionary<string, string>
		{
			["BASSIA_ROOT"] = monorepo.Root,
			["BASSIA_TASK_DIR"] = folder
		}, finished: result => Console.Error.WriteLine(result.Cached
			? $"{result.Component}:{result.Task} restored from the cache"
			: $"{result.Component}:{result.Task} exited {result.ExitCode?.ToString() ?? "(cancelled)"} in {result.Seconds}s"),
			caching: invocation.Has("no-cache") ? null : new TaskCaching(TaskCache.In(monorepo.WorkspaceDir), monorepo.Registry, fingerprints));

		var ok = results.Count == plan.Count && results.All(result => result.Ok);
		var kept = !materialized || invocation.Has("keep") || !ok;
		if (!kept)
		{
			Workspaces.Delete(folder);
		}

		var failed = results.FirstOrDefault(result => !result.Ok);
		return ProgramCli.WriteResult(ok, invocation.Command,
			ok
				? $"{results.Count} task(s) passed: {string.Join(", ", results.Select(result => $"{result.Component}:{result.Task}{(result.Cached ? " (cached)" : "")}"))}."
				: $"Task {failed!.Component}:{failed.Task} failed with exit code {failed.ExitCode?.ToString() ?? "(cancelled)"}; its log is '{failed.LogPath}'" +
					(materialized ? $" and the checkout is kept at '{folder}'." : "."),
			new Dictionary<string, object?>
			{
				["folder"] = kept ? folder : null,
				["logs"] = logs,
				["planned"] = plan.Select(task => task.Id).ToList(),
				["cached"] = results.Count(result => result.Cached),
				["table"] = results.Count == 0 ? null : new TomlText(AsciiTable.Render(["TASK", "EXIT", "SECONDS", "CACHED"],
					results.Select(result => (IReadOnlyList<string>)[$"{result.Component}:{result.Task}", result.ExitCode?.ToString() ?? "-",
						result.Seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), result.Cached ? "yes" : "-"]))),
				["result"] = Describe(results)
			});
	}

	internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> Describe(IEnumerable<TaskResult> results) =>
		results.Select(result => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["component"] = result.Component,
			["task"] = result.Task,
			["command"] = result.Command,
			["exit_code"] = result.ExitCode,
			["seconds"] = result.Seconds,
			["log"] = result.LogPath,
			["cached"] = result.Cached ? true : null
		}).ToList();

	/// <summary>
	/// A hash of a working copy's files as they are, uncommitted changes included: what git would track (ignored files
	/// and the nested reference paths left out), path and content.
	/// </summary>
	internal static async Task<string> FolderFingerprintAsync(string folder)
	{
		var files = Split.GitBinary.SplitNul(await Split.GitBinary.RunAsync(folder, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"]))
			.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
		using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
		foreach (var file in files)
		{
			var path = Path.Combine(folder, file);
			hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file + "\n"));
			if (File.Exists(path) && !DirectoryLinks.IsLink(path))
			{
				hash.AppendData(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
			}
		}

		return "files:" + Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	// ----- bassia task cache -----

	public static Task<int> CacheAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var cache = TaskCache.In(monorepo.WorkspaceDir);
		var (entries, bytes) = cache.Size();
		if (invocation.Has("clear"))
		{
			cache.Clear();
		}

		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command,
			invocation.Has("clear")
				? $"Cleared the task cache: {entries} entr{(entries == 1 ? "y" : "ies")}, {bytes} byte(s) removed."
				: $"The task cache holds {entries} entr{(entries == 1 ? "y" : "ies")}, {bytes} byte(s).",
			new Dictionary<string, object?>
			{
				["path"] = cache.Folder,
				["entries"] = invocation.Has("clear") ? 0 : entries,
				["bytes"] = invocation.Has("clear") ? 0 : bytes,
				["cleared"] = invocation.Has("clear")
			}));
	}

	// ----- bassia affected -----

	public static async Task<int> AffectedAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		if (invocation.Has("runs") == invocation.Has("components"))
		{
			throw invocation.Usage("Give either -runs or -components.");
		}

		IReadOnlyList<string> changed;
		if (invocation.Has("components"))
		{
			changed = invocation.List("components").Select(name => ComponentCommands.RequireComponent(monorepo, name).Name).ToList();
		}
		else
		{
			var runs = await IntegrationSupport.SelectRunsAsync(new RunMetadataStore(new Git.GitClient(monorepo.Root), monorepo.RunsRepoDir), invocation.Require("runs"));
			changed = runs.SelectMany(run => run.Components).Where(component => component.ResultStatus == ResultStatus.Pushed)
				.Select(component => component.Name).Distinct(StringComparer.Ordinal).Where(monorepo.Registry.Contains).ToList();
		}

		var affected = Affected.Of(monorepo.Registry, changed);
		var closure = monorepo.Closure(affected.Select(component => component.Name));
		var changedSet = changed.ToHashSet(StringComparer.Ordinal);
		return ProgramCli.WriteResult(true, invocation.Command,
			affected.Count == 0
				? "Nothing is affected."
				: $"{affected.Count} component(s) affected by {string.Join(", ", changed)}: {string.Join(", ", affected.Select(component => component.Name))}.",
			new Dictionary<string, object?>
			{
				["changed"] = changed,
				["affected"] = affected.Select(component => component.Name).ToList(),
				["dependents"] = affected.Where(component => !changedSet.Contains(component.Name)).Select(component => component.Name).ToList(),
				["select"] = affected.Count == 0 ? null : string.Join(",", closure.Select(component => component.Name)),
				["graph"] = affected.Count == 0 ? null : new TomlText(new Graph.ComponentGraph(closure).RenderText(ascii: true))
			});
	}
}
