namespace Bassia.Core;

using System.Diagnostics;
using System.Text;
using Bassia.Storage;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// One task of a component: a shell command run in the component's folder, after the tasks it depends on. An entry
/// <c>x</c> of <paramref name="DependsOn"/> is the component's own task <c>x</c>; <c>^x</c> is task <c>x</c> of every
/// component in its closure that defines it. A <paramref name="Cache"/>d task is not run again while its inputs are
/// unchanged; <paramref name="Outputs"/> (path patterns relative to the component) are what the cache restores.
/// </summary>
internal sealed record ComponentTask(string Name, string Command, IReadOnlyList<string> DependsOn, bool Cache = false, IReadOnlyList<string>? Outputs = null)
{
	public IReadOnlyList<string> OutputPatterns => Outputs ?? [];
}

/// <summary>
/// A component's tasks, from <c>bassia.toml</c> at its root:
/// <code>
/// [tasks.build]
/// command = "dotnet build"
/// depends_on = ["^build"]
/// cache = true
/// outputs = ["bin", "obj"]
/// </code>
/// </summary>
internal static class TaskFile
{
	public const string FileName = "bassia.toml";

	/// <summary>The tasks in <paramref name="text"/>, by name; throws naming <paramref name="component"/> when it is not valid.</summary>
	public static IReadOnlyDictionary<string, ComponentTask> Parse(string component, string text)
	{
		TomlTable table;
		try
		{
			table = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
		}
		catch (TomlException ex)
		{
			throw new MonorepoException($"Component '{component}': {FileName} is not valid TOML: {ex.Message}");
		}

		var tasks = new Dictionary<string, ComponentTask>(StringComparer.Ordinal);
		if (!table.TryGetValue("tasks", out var value))
		{
			return tasks;
		}

		if (value is not TomlTable taskTables)
		{
			throw new MonorepoException($"Component '{component}': 'tasks' in {FileName} must be a table of [tasks.<name>] tables.");
		}

		foreach (var (name, entry) in taskTables)
		{
			var where = $"Component '{component}': task '{name}' in {FileName}";
			if (entry is not TomlTable task || !task.TryGetValue("command", out var command) || command is not string commandText || string.IsNullOrWhiteSpace(commandText))
			{
				throw new MonorepoException($"{where} needs a 'command'.");
			}

			foreach (var key in task.Keys.Where(key => key is not ("command" or "depends_on" or "description" or "cache" or "outputs")))
			{
				throw new MonorepoException($"{where} has the unknown key '{key}'; expected command, depends_on, cache, outputs and description.");
			}

			var cache = task.TryGetValue("cache", out var cacheValue) && (cacheValue as bool? ?? throw new MonorepoException($"{where}: 'cache' must be true or false."));
			List<string>? outputs = null;
			if (task.TryGetValue("outputs", out var outputsValue))
			{
				if (outputsValue is not TomlArray outputArray || outputArray.Any(item => item is not string))
				{
					throw new MonorepoException($"{where}: 'outputs' must be an array of path patterns.");
				}

				outputs = outputArray.Cast<string>().ToList();
				try
				{
					_ = new Split.PathPatterns(outputs);
				}
				catch (ArgumentException ex)
				{
					throw new MonorepoException($"{where}: 'outputs': {ex.Message}");
				}
			}

			var dependsOn = new List<string>();
			if (task.TryGetValue("depends_on", out var depends))
			{
				if (depends is not TomlArray array || array.Any(item => item is not string text || text.TrimStart('^').Length == 0))
				{
					throw new MonorepoException($"{where}: 'depends_on' must be an array of task names ('x', or '^x' for the referenced components' task x).");
				}

				dependsOn.AddRange(array.Cast<string>());
			}

			tasks[name] = new ComponentTask(name, commandText, dependsOn, cache, outputs);
		}

		return tasks;
	}

	/// <summary>The tasks of <paramref name="component"/> at <paramref name="commit"/>; none without a <c>bassia.toml</c>.</summary>
	public static async Task<IReadOnlyDictionary<string, ComponentTask>> ReadAsync(IComponentStore store, string component, string commit) =>
		await store.ReadFileAsync(commit, FileName) is { } content ? Parse(component, Encoding.UTF8.GetString(content)) : new Dictionary<string, ComponentTask>();

	/// <summary>The tasks of <paramref name="component"/> checked out in <paramref name="folder"/>; none without a <c>bassia.toml</c>.</summary>
	public static IReadOnlyDictionary<string, ComponentTask> Read(string folder, string component)
	{
		var path = Path.Combine(folder, FileName);
		return File.Exists(path) ? Parse(component, File.ReadAllText(path)) : new Dictionary<string, ComponentTask>();
	}
}

/// <summary>One task of one component to run.</summary>
internal sealed record PlannedTask(string Component, ComponentTask Task)
{
	public string Id => $"{Component}:{Task.Name}";
}

/// <summary>The order to run tasks in: every task after the tasks it depends on.</summary>
internal static class TaskPlan
{
	/// <summary>
	/// The tasks <paramref name="targets"/> names, of each of <paramref name="components"/> that defines one, with what
	/// they depend on, in an order that runs each after its dependencies. Throws when a target is defined by none of them,
	/// a dependency names a task the component does not define, or tasks depend on each other in a cycle.
	/// </summary>
	public static IReadOnlyList<PlannedTask> Plan(ComponentRegistry registry, IReadOnlyList<string> components, IReadOnlyList<string> targets,
		IReadOnlyDictionary<string, IReadOnlyDictionary<string, ComponentTask>> tasks)
	{
		var ordered = new List<PlannedTask>();
		var state = new Dictionary<string, bool>(StringComparer.Ordinal); // false: in progress, true: done
		var path = new Stack<string>();

		IReadOnlyDictionary<string, ComponentTask> TasksOf(string component) =>
			tasks.TryGetValue(component, out var defined) ? defined : new Dictionary<string, ComponentTask>();

		void Visit(string component, ComponentTask task)
		{
			var id = $"{component}:{task.Name}";
			if (state.TryGetValue(id, out var done))
			{
				if (!done)
				{
					throw new MonorepoException($"Tasks depend on each other in a cycle: {string.Join(" -> ", path.Reverse().SkipWhile(item => item != id).Append(id))}.");
				}

				return;
			}

			state[id] = false;
			path.Push(id);
			foreach (var dependency in task.DependsOn)
			{
				if (dependency.StartsWith('^'))
				{
					var name = dependency[1..];
					// The closure in dependency order, without the component itself: what it references, nearest last.
					foreach (var referenced in registry.Closure([component]).Where(item => item.Name != component).Reverse())
					{
						if (TasksOf(referenced.Name).TryGetValue(name, out var upstream))
						{
							Visit(referenced.Name, upstream);
						}
					}
				}
				else if (TasksOf(component).TryGetValue(dependency, out var own))
				{
					Visit(component, own);
				}
				else
				{
					throw new MonorepoException($"Task '{task.Name}' of component '{component}' depends on '{dependency}', which {component} does not define.");
				}
			}

			path.Pop();
			state[id] = true;
			ordered.Add(new PlannedTask(component, task));
		}

		var any = false;
		foreach (var target in targets)
		{
			foreach (var component in components)
			{
				if (TasksOf(component).TryGetValue(target, out var task))
				{
					any = true;
					Visit(component, task);
				}
			}
		}

		if (!any && targets.Count > 0)
		{
			throw new MonorepoException($"No component of {string.Join(", ", components)} defines the task(s) {string.Join(", ", targets)} in its {TaskFile.FileName}.");
		}

		return ordered;
	}
}

/// <summary>A task to run: its command, where, with which environment and log file, and how to watch and stop it.</summary>
internal sealed record TaskRequest(PlannedTask Task, string Directory, IReadOnlyDictionary<string, string> Environment, string LogPath, CancellationToken Cancellation);

/// <summary>How a task ended: its exit code (null when it was cancelled), how long it took and where its output is.</summary>
/// <remarks><paramref name="Cached"/>: restored from the task cache instead of run; <paramref name="Seconds"/> is then the original run's.</remarks>
internal sealed record TaskResult(string Component, string Task, string Command, int? ExitCode, double Seconds, string LogPath, bool Cached = false)
{
	public bool Ok => ExitCode == 0;
}

/// <summary>
/// The cache a task run uses: the cache, the graph (for each task's closure) and each component's content fingerprint
/// - its commit for a checkout made from one, a hash of its files for a working copy that may have changed.
/// </summary>
internal sealed record TaskCaching(Storage.ITaskCache Cache, ComponentRegistry Registry, IReadOnlyDictionary<string, string> Fingerprints);

/// <summary>Runs planned tasks in a materialized folder, one after another, until one fails.</summary>
internal static class TaskRunner
{
	/// <summary>The default runner: the task's command in the platform shell, its output written to its log file.</summary>
	public static async Task<int?> RunShellAsync(TaskRequest request)
	{
		await using var log = new StreamWriter(request.LogPath, append: false);
		log.AutoFlush = true;
		var gate = new object();
		return await ShellCommand.RunAsync(request.Task.Task.Command, request.Directory, request.Environment, null,
			line =>
			{
				lock (gate)
				{
					log.WriteLine(line);
				}
			},
			request.Cancellation);
	}

	/// <summary>
	/// Runs <paramref name="plan"/> in <paramref name="folder"/> (each task in <c>&lt;folder&gt;/&lt;component&gt;</c>), its logs
	/// in <paramref name="logFolder"/>, and stops after the first task that fails; returns what ran.
	/// </summary>
	/// <remarks>
	/// With <paramref name="caching"/>, a task marked <c>cache = true</c> is restored from the cache when its key has an
	/// entry, and stored there when it passes.
	/// </remarks>
	public static async Task<IReadOnlyList<TaskResult>> RunAsync(IReadOnlyList<PlannedTask> plan, string folder, string logFolder,
		IReadOnlyDictionary<string, string> environment, Func<TaskRequest, Task<int?>>? run = null, Action<TaskResult>? finished = null,
		CancellationToken cancellation = default, TaskCaching? caching = null)
	{
		run ??= RunShellAsync;
		Directory.CreateDirectory(logFolder);
		var results = new List<TaskResult>();
		foreach (var task in plan)
		{
			var log = Path.Combine(logFolder, $"{task.Component}-{task.Task.Name}.log");
			var componentFolder = Path.Combine(folder, task.Component);
			var key = caching is not null && task.Task.Cache ? TaskCache.Key(caching.Registry, task, caching.Fingerprints) : null;
			var result = key is null ? null : await caching!.Cache.TryRestoreAsync(key, task, componentFolder, log);
			if (result is null)
			{
				var env = new Dictionary<string, string>(environment) { ["BASSIA_TASK"] = task.Task.Name, ["BASSIA_COMPONENT"] = task.Component };
				var clock = Stopwatch.StartNew();
				var exitCode = await run(new TaskRequest(task, componentFolder, env, log, cancellation));
				result = new TaskResult(task.Component, task.Task.Name, task.Task.Command, exitCode, Math.Round(clock.Elapsed.TotalSeconds, 2), log);
				if (key is not null)
				{
					var nested = caching!.Registry.Find(task.Component)?.References.Select(reference => reference.Path).ToList() ?? [];
					await caching.Cache.StoreAsync(key, task, result, componentFolder, nested);
				}
			}

			results.Add(result);
			finished?.Invoke(result);
			if (!result.Ok)
			{
				break;
			}
		}

		return results;
	}
}

/// <summary>What a change affects: the changed components and every component that references one of them, transitively.</summary>
internal static class Affected
{
	/// <summary>The affected components of <paramref name="changed"/>, in dependency order (a component after what it references).</summary>
	public static IReadOnlyList<ComponentDefinition> Of(ComponentRegistry registry, IEnumerable<string> changed)
	{
		var affected = new HashSet<string>(StringComparer.Ordinal);
		var queue = new Queue<string>(changed.Select(name => registry.Require(name).Name));
		while (queue.TryDequeue(out var name))
		{
			if (affected.Add(name))
			{
				foreach (var referrer in registry.ReferrersOf(name))
				{
					queue.Enqueue(referrer.Name);
				}
			}
		}

		return registry.DependencyOrder().Where(component => affected.Contains(component.Name)).ToList();
	}
}
