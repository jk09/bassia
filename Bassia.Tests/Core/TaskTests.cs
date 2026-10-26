namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Integration;
using Bassia.Storage;
using static MemoryMonorepo;

/// <summary>Component tasks (<c>bassia.toml</c>), their plans, the affected set and the integration's verification gate.</summary>
public class TaskTests
{
	private const string LibTasks = """
		[tasks.build]
		command = "make lib"

		[tasks.test]
		command = "make test"
		depends_on = ["build"]
		""";

	private const string AppTasks = """
		[tasks.build]
		command = "make app"
		depends_on = ["^build"]

		[tasks.test]
		command = "make app-test"
		depends_on = ["build"]
		""";

	private static ComponentRegistry Registry() => ComponentRegistry.FromDefinitions(
	[
		new ComponentDefinition("core", "", []),
		new ComponentDefinition("lib", "", Refs("core")),
		new ComponentDefinition("app", "", Refs("lib")),
		new ComponentDefinition("tool", "", Refs("core"))
	]);

	private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, ComponentTask>> Tasks(params (string Component, string Text)[] files) =>
		files.ToDictionary(file => file.Component, file => TaskFile.Parse(file.Component, file.Text), StringComparer.Ordinal);

	[Fact]
	public void Parse_ReadsTasks_AndRejectsWhatIsNotATask()
	{
		var tasks = TaskFile.Parse("app", AppTasks);

		Assert.Equal(["build", "test"], tasks.Keys.Order());
		Assert.Equal(["^build"], tasks["build"].DependsOn);
		Assert.Contains("needs a 'command'", Assert.Throws<MonorepoException>(() => TaskFile.Parse("app", "[tasks.build]\ndepends_on = []\n")).Message);
		Assert.Contains("unknown key 'cmd'", Assert.Throws<MonorepoException>(() => TaskFile.Parse("app", "[tasks.build]\ncommand = \"x\"\ncmd = \"y\"\n")).Message);
		Assert.Contains("'depends_on' must be", Assert.Throws<MonorepoException>(() => TaskFile.Parse("app", "[tasks.build]\ncommand = \"x\"\ndepends_on = \"^build\"\n")).Message);
		Assert.Empty(TaskFile.Parse("app", "[other]\nx = 1\n"));
	}

	[Fact]
	public void Plan_RunsDependenciesFirst_UpstreamComponentsBeforeTheirReferrers()
	{
		var tasks = Tasks(("core", "[tasks.build]\ncommand = \"make core\"\n"), ("lib", LibTasks.Replace("command = \"make lib\"", "command = \"make lib\"\ndepends_on = [\"^build\"]")), ("app", AppTasks));

		var plan = TaskPlan.Plan(Registry(), ["app"], ["test"], tasks);

		Assert.Equal(["core:build", "lib:build", "app:build", "app:test"], plan.Select(task => task.Id));
	}

	[Fact]
	public void Plan_UnknownDependency_ACycle_OrATaskNobodyDefines_Throw()
	{
		var registry = Registry();

		Assert.Contains("depends on 'lint', which app does not define",
			Assert.Throws<MonorepoException>(() => TaskPlan.Plan(registry, ["app"], ["build"], Tasks(("app", "[tasks.build]\ncommand = \"x\"\ndepends_on = [\"lint\"]\n")))).Message);
		Assert.Contains("cycle: app:a -> app:b -> app:a",
			Assert.Throws<MonorepoException>(() => TaskPlan.Plan(registry, ["app"], ["a"], Tasks(("app", "[tasks.a]\ncommand = \"x\"\ndepends_on = [\"b\"]\n[tasks.b]\ncommand = \"y\"\ndepends_on = [\"a\"]\n")))).Message);
		Assert.Contains("No component of app defines the task(s) deploy",
			Assert.Throws<MonorepoException>(() => TaskPlan.Plan(registry, ["app"], ["deploy"], Tasks(("app", AppTasks)))).Message);
	}

	[Fact]
	public void Affected_IsTheChangeAndItsTransitiveReferrers_InDependencyOrder()
	{
		Assert.Equal(["core", "lib", "app", "tool"], Affected.Of(Registry(), ["core"]).Select(component => component.Name));
		Assert.Equal(["lib", "app"], Affected.Of(Registry(), ["lib"]).Select(component => component.Name));
		Assert.Equal(["app"], Affected.Of(Registry(), ["app"]).Select(component => component.Name));
	}

	[Fact]
	public async Task Runner_StopsAtTheFirstFailure_AndReportsEachTask()
	{
		var plan = TaskPlan.Plan(Registry(), ["app"], ["test"], Tasks(("lib", LibTasks), ("app", AppTasks)));
		var folder = Path.Combine(Path.GetTempPath(), $"bassia-tasks-{Guid.NewGuid():N}");
		try
		{
			var results = await TaskRunner.RunAsync(plan, folder, Path.Combine(folder, "logs"), new Dictionary<string, string>(),
				request => Task.FromResult<int?>(request.Task.Id == "app:build" ? 2 : 0));

			Assert.Equal(["lib:build", "app:build"], results.Select(result => $"{result.Component}:{result.Task}"));
			Assert.Equal([0, 2], results.Select(result => result.ExitCode));
		}
		finally
		{
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, recursive: true);
			}
		}
	}

	// ----- the cache -----

	private const string CachedLibTasks = """
		[tasks.build]
		command = "make lib"
		cache = true
		outputs = ["bin"]

		[tasks.test]
		command = "make test"
		depends_on = ["build"]
		""";

	[Fact]
	public void Parse_ReadsCacheAndOutputs_AndRejectsBadValues()
	{
		var build = TaskFile.Parse("lib", CachedLibTasks)["build"];

		Assert.True(build.Cache);
		Assert.Equal(["bin"], build.OutputPatterns);
		Assert.False(TaskFile.Parse("lib", CachedLibTasks)["test"].Cache);
		Assert.Contains("'cache' must be true or false", Assert.Throws<MonorepoException>(() => TaskFile.Parse("lib", "[tasks.b]\ncommand = \"x\"\ncache = \"yes\"\n")).Message);
		Assert.Contains("'outputs' must be an array", Assert.Throws<MonorepoException>(() => TaskFile.Parse("lib", "[tasks.b]\ncommand = \"x\"\noutputs = \"bin\"\n")).Message);
	}

	[Fact]
	public void Key_ChangesWithTheTaskThePlatformAndTheClosure_NotWithUnrelatedComponents()
	{
		var registry = Registry();
		var build = new PlannedTask("lib", TaskFile.Parse("lib", CachedLibTasks)["build"]);
		var fingerprints = new Dictionary<string, string> { ["core"] = "commit:1", ["lib"] = "commit:2", ["app"] = "commit:3", ["tool"] = "commit:4" };

		var key = TaskCache.Key(registry, build, fingerprints);

		Assert.Equal(key, TaskCache.Key(registry, build, new Dictionary<string, string>(fingerprints) { ["app"] = "commit:changed" }));
		Assert.NotEqual(key, TaskCache.Key(registry, build, new Dictionary<string, string>(fingerprints) { ["core"] = "commit:changed" }));
		Assert.NotEqual(key, TaskCache.Key(registry, build with { Task = build.Task with { Command = "make lib -j" } }, fingerprints));
		Assert.Null(TaskCache.Key(registry, build, new Dictionary<string, string> { ["lib"] = "commit:2" }));
	}

	[Fact]
	public async Task Runner_RestoresACachedTaskAndItsOutputs_RunsUncachedOnes_AndNeverStoresAFailure()
	{
		var root = Path.Combine(Path.GetTempPath(), $"bassia-cache-{Guid.NewGuid():N}");
		try
		{
			var registry = Registry();
			var plan = TaskPlan.Plan(registry, ["lib"], ["test"], Tasks(("lib", CachedLibTasks)));
			var caching = new TaskCaching(new TaskCache(Path.Combine(root, "cache")), registry,
				new Dictionary<string, string> { ["core"] = "commit:1", ["lib"] = "commit:2" });
			var ran = new List<string>();
			Task<int?> Run(TaskRequest request)
			{
				ran.Add(request.Task.Id);
				if (request.Task.Task.Name == "build")
				{
					Directory.CreateDirectory(Path.Combine(request.Directory, "bin"));
					File.WriteAllText(Path.Combine(request.Directory, "bin", "lib.dll"), "built");
					File.WriteAllText(Path.Combine(request.Directory, "notes.txt"), "not an output");
					File.WriteAllText(request.LogPath, "building lib\n");
				}

				return Task.FromResult<int?>(0);
			}

			async Task<IReadOnlyList<TaskResult>> RunInAsync(string folder)
			{
				Directory.CreateDirectory(Path.Combine(folder, "lib"));
				return await TaskRunner.RunAsync(plan, folder, Path.Combine(folder, "logs"), new Dictionary<string, string>(), Run, caching: caching);
			}

			var first = await RunInAsync(Path.Combine(root, "first"));
			var second = await RunInAsync(Path.Combine(root, "second"));

			Assert.Equal(["lib:build", "lib:test", "lib:test"], ran);
			Assert.Equal([false, false], first.Select(result => result.Cached));
			Assert.Equal([true, false], second.Select(result => result.Cached));
			Assert.Equal("built", File.ReadAllText(Path.Combine(root, "second", "lib", "bin", "lib.dll")));
			Assert.False(File.Exists(Path.Combine(root, "second", "lib", "notes.txt")));
			Assert.Equal("building lib\n", File.ReadAllText(second[0].LogPath));

			// A failed run is not stored: with another closure it runs again, fails, and stays uncached.
			var failing = caching with { Fingerprints = new Dictionary<string, string> { ["core"] = "commit:9", ["lib"] = "commit:2" } };
			Directory.CreateDirectory(Path.Combine(root, "third", "lib"));
			await TaskRunner.RunAsync(plan, Path.Combine(root, "third"), Path.Combine(root, "third", "logs"), new Dictionary<string, string>(),
				_ => Task.FromResult<int?>(1), caching: failing);
			Assert.Equal(1, caching.Cache.Size().Entries);
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	// ----- the integration's gate -----

	/// <summary>lib and app (nesting lib), each with tasks, tagged v1.</summary>
	private static async Task<MemoryMonorepo> MonorepoAsync()
	{
		var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib", null, ("lib.txt", "lib\n"), (TaskFile.FileName, LibTasks));
		await monorepo.AddAsync("app", Refs("lib"), ("app.txt", "app\n"), (TaskFile.FileName, AppTasks));
		return monorepo;
	}

	private static async Task<RunMetadata> ChangeLibAsync(MemoryMonorepo monorepo)
	{
		var outcome = await monorepo.Runs(metadata =>
		{
			File.WriteAllText(Path.Combine(metadata.WorkspacePath, "lib", "lib.txt"), "lib changed\n");
			return 0;
		}).StartAsync("lib@v1", "agent \"change lib\"", Quiet());
		Assert.True(outcome.Ok, outcome.Message);
		return outcome.Metadata;
	}

	private static async Task<IntegrationOutcome> IntegrateAsync(MemoryMonorepo monorepo, RunMetadata run, Func<TaskRequest, int?> runTask, List<string> ran)
	{
		var settings = new IntegrationSettings
		{
			WorkspaceDir = monorepo.WorkspaceDir,
			MergePolicyFor = _ => MergePolicy.Default with { Verify = ["test"] },
			RunResolver = _ => throw new InvalidOperationException("no resolver expected"),
			RunTask = request =>
			{
				ran.Add(request.Task.Id);

				// The referrer sees what it references nested where the run would nest it.
				if (request.Task.Component == "app")
				{
					Assert.True(File.Exists(Path.Combine(request.Directory, "lib", "lib.txt")));
					Assert.Equal("lib changed\n", File.ReadAllText(Path.Combine(request.Directory, "lib", "lib.txt")));
				}

				return Task.FromResult(runTask(request));
			}
		};
		var plan = await IntegrationPlanner.PlanAsync(monorepo.Graph, settings, [run]);
		return await IntegrationRunner.RunAsync(monorepo.Graph, settings, monorepo.Records, [run], plan, "fake-resolver", new IntegrationContext { OnProgress = _ => { } });
	}

	[Fact]
	public async Task Gate_RunsTheAffectedComponentsTasks_AndPublishesWhenTheyPass()
	{
		using var monorepo = await MonorepoAsync();
		var run = await ChangeLibAsync(monorepo);
		var ran = new List<string>();

		var outcome = await IntegrateAsync(monorepo, run, _ => 0, ran);

		Assert.True(outcome.Ok, outcome.Message);
		Assert.Equal("completed", outcome.Record.Status);
		Assert.Equal(["lib:build", "lib:test", "app:build", "app:test"], ran);
		Assert.Equal(4, outcome.Record.Verification.Count);
		Assert.Equal(ResultStatus.Pushed, Assert.Single(outcome.Record.Components).ResultStatus);
	}

	[Fact]
	public async Task Gate_RestoresCachedTasksOfUnchangedComponents()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib", null, ("lib.txt", "lib\n"), (TaskFile.FileName, CachedLibTasks));
		await monorepo.AddAsync("app", Refs("lib"), ("app.txt", "app\n"), (TaskFile.FileName, AppTasks.Replace("command = \"make app\"", "command = \"make app\"\ncache = true")));
		var cache = new TaskCache(Path.Combine(monorepo.WorkspaceDir, ".task-cache"));
		var ran = new List<string>();

		async Task<IntegrationOutcome> IntegrateAppChangeAsync(string content)
		{
			var outcome = await monorepo.Runs(metadata =>
			{
				File.WriteAllText(Path.Combine(metadata.WorkspacePath, "app", "app.txt"), content);
				return 0;
			}).StartAsync("app,lib", "agent \"change app\"", Quiet());
			var settings = new IntegrationSettings
			{
				WorkspaceDir = monorepo.WorkspaceDir,
				MergePolicyFor = _ => MergePolicy.Default with { Verify = ["build"] },
				RunResolver = _ => throw new InvalidOperationException("no resolver expected"),
				RunTask = request =>
				{
					ran.Add(request.Task.Id);
					return Task.FromResult<int?>(0);
				},
				TaskCache = cache
			};
			var plan = await IntegrationPlanner.PlanAsync(monorepo.Graph, settings, [outcome.Metadata]);
			return await IntegrationRunner.RunAsync(monorepo.Graph, settings, monorepo.Records, [outcome.Metadata], plan, "fake-resolver", new IntegrationContext { OnProgress = _ => { } });
		}

		var first = await IntegrateAppChangeAsync("app 1\n");
		await IntegrationRunner.AdvanceAsync(monorepo.Graph, monorepo.Records, first.Record);
		var second = await IntegrateAppChangeAsync("app 2\n");

		Assert.True(second.Ok, second.Message);
		Assert.Equal(["lib:build", "app:build", "app:build"], ran);
		Assert.Equal([true, false], second.Record.Verification.Select(result => result.Cached));
		Assert.Equal([true, false], IntegrationRecord.FromToml(second.Record.ToToml()).Verification.Select(result => result.Cached));
	}

	[Fact]
	public async Task Gate_AFailingReferrerTest_PublishesNothing()
	{
		using var monorepo = await MonorepoAsync();
		var run = await ChangeLibAsync(monorepo);
		var tagsBefore = (await monorepo.Graph.Store("lib").ListTagsAsync("integration/")).Count;
		var ran = new List<string>();

		var outcome = await IntegrateAsync(monorepo, run, request => request.Task.Id == "app:test" ? 1 : 0, ran);

		Assert.False(outcome.Ok);
		Assert.Equal("partial", outcome.Record.Status);
		Assert.Contains("the verification failed: app:test exited 1", outcome.Message);
		var lib = Assert.Single(outcome.Record.Components);
		Assert.Equal(ResultStatus.Failed, lib.ResultStatus);
		Assert.Contains("verification failed: app:test exited 1", lib.ResultError);
		Assert.Equal(tagsBefore, (await monorepo.Graph.Store("lib").ListTagsAsync("integration/")).Count);

		var recorded = IntegrationRecord.FromToml(outcome.Record.ToToml());
		Assert.Equal([0, 0, 0, 1], recorded.Verification.Select(result => result.ExitCode));
	}
}
