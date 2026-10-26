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
