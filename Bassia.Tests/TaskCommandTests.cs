namespace Bassia.Tests;

/// <summary><c>bassia task list|run</c>, <c>bassia affected</c> and the integration's <c>merge.verify</c> gate, on the git storage with real shell tasks.</summary>
public class TaskCommandTests
{
	private static string WriteFile(string file) => OperatingSystem.IsWindows() ? $"echo built> {file}" : $"echo built > {file}";

	private static string FileExists(string file) =>
		OperatingSystem.IsWindows() ? $"if exist {file.Replace('/', '\\')} (exit 0) else (exit 1)" : $"test -f {file}";

	/// <summary>lib builds built.txt; app's build needs lib's and its test checks lib's output through the nested lib/.</summary>
	private static async Task<MonorepoFixture> MonorepoAsync(string appTest)
	{
		var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib", files: new Dictionary<string, string>
		{
			["bassia.toml"] = $"[tasks.build]\ncommand = '{WriteFile("built.txt")}'\n"
		});
		await monorepo.AddComponentAsync("app", files: new Dictionary<string, string>
		{
			["bassia.toml"] = $"[tasks.build]\ncommand = '{WriteFile("app-built.txt")}'\ndepends_on = [\"^build\"]\n\n[tasks.test]\ncommand = '{appTest}'\ndepends_on = [\"build\"]\n"
		});
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "app", "-references", "lib")).ExitCode);
		return monorepo;
	}

	[Fact]
	public async Task ListAndRun_RunDependenciesFirst_InACheckoutLaidOutAsARun()
	{
		await using var monorepo = await MonorepoAsync(FileExists("lib/built.txt"));

		var (listExit, listOutput, listError) = await monorepo.BassiaAsync("task", "list");
		Assert.True(listExit == 0, listError);
		Assert.Contains("3 task(s) in 2 component(s)", listOutput);

		var (exit, output, error) = await monorepo.BassiaAsync("task", "run", "-task", "test", "-select", "app,lib");
		Assert.True(exit == 0, error);
		Assert.Contains("3 task(s) passed: lib:build, app:build, app:test", output);
		Assert.DoesNotContain("folder =", output);
		Assert.False(Directory.EnumerateDirectories(monorepo.Workspace, "tasks-*").Any());
	}

	[Fact]
	public async Task Run_AFailingTask_KeepsTheCheckoutAndNamesTheLog()
	{
		await using var monorepo = await MonorepoAsync(FileExists("missing.txt"));

		var (exit, _, error) = await monorepo.BassiaAsync("task", "run", "test", "-select", "app,lib");

		Assert.Equal(1, exit);
		Assert.Contains("Task app:test failed with exit code 1", error);
		Assert.Single(Directory.EnumerateDirectories(monorepo.Workspace, "tasks-*"));
	}

	[Fact]
	public async Task Affected_ListsTheReferrers_AndTheVerifyGateHoldsABrokenIntegrationBack()
	{
		await using var monorepo = await MonorepoAsync(FileExists("lib/feature.txt"));

		var (affectedExit, affectedOutput, affectedError) = await monorepo.BassiaAsync("affected", "-components", "lib");
		Assert.True(affectedExit == 0, affectedError);
		Assert.Contains("affected = [\"lib\", \"app\"]", affectedOutput);
		Assert.Contains("dependents = [\"app\"]", affectedOutput);

		// The run changes lib without the file app's test needs: the gate fails and nothing is published.
		var runId = await monorepo.RunWritingAsync("lib", "other.txt", "x");
		Assert.Contains("affected = [\"lib\", \"app\"]", (await monorepo.BassiaAsync("affected", "-runs", runId)).Output);
		Assert.Equal(0, (await monorepo.BassiaAsync("config", "set", "-key", "merge.verify", "-value", "test")).ExitCode);

		var (exit, output, error) = await monorepo.IntegrationStartAsync("-runs", runId);

		Assert.Equal(1, exit);
		Assert.Contains("the verification failed: app:test exited 1", error + output);
		Assert.Equal("", await TestEnvironment.GitAsync(monorepo.SourceRepo("lib"), "tag", "--list", "integration/*"));
	}

	[Fact]
	public async Task Cache_RestoresAnUnchangedBuildAndItsOutputs_AndRerunsItWhenWhatItNeedsChanged()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		var counter = Path.Combine(monorepo.Root, "..", "builds.txt");
		var build = OperatingSystem.IsWindows() ? $"echo built> built.txt & echo x>> \"{counter}\"" : $"echo built > built.txt; echo x >> \"{counter}\"";
		await monorepo.AddComponentAsync("core");
		await monorepo.AddComponentAsync("lib", files: new Dictionary<string, string>
		{
			["bassia.toml"] = $"[tasks.build]\ncommand = '{build}'\ncache = true\noutputs = [\"built.txt\"]\n"
		});
		await monorepo.AddComponentAsync("app", files: new Dictionary<string, string>
		{
			["bassia.toml"] = $"[tasks.test]\ncommand = '{FileExists("lib/built.txt")}'\ndepends_on = [\"^build\"]\n"
		});
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "app", "-references", "lib")).ExitCode);
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "lib", "-references", "core")).ExitCode);
		int Builds() => File.Exists(counter) ? File.ReadAllLines(counter).Length : 0;

		var (firstExit, firstOutput, firstError) = await monorepo.BassiaAsync("task", "run", "test", "-select", "app,lib,core");
		var (secondExit, secondOutput, secondError) = await monorepo.BassiaAsync("task", "run", "test", "-select", "app,lib,core");

		Assert.True(firstExit == 0, firstError);
		Assert.True(secondExit == 0, secondError);
		Assert.Contains("cached = 0", firstOutput);
		Assert.Contains("lib:build (cached), app:test", secondOutput);
		Assert.Equal(1, Builds());

		// What lib references changed: its build runs again. -no-cache runs it regardless.
		var runId = await monorepo.RunWritingAsync("core", "core.txt", "changed");
		var integrated = await monorepo.IntegrationStartAsync("-runs", runId);
		Assert.True(integrated.ExitCode == 0, integrated.Error);
		var integration = System.Text.RegularExpressions.Regex.Match(integrated.Output, "integration-[a-z0-9-]+").Value;
		Assert.Equal(0, (await monorepo.BassiaAsync("integration", "advance", integration)).ExitCode);
		Assert.Equal(0, (await monorepo.BassiaAsync("task", "run", "test", "-select", "app,lib,core")).ExitCode);
		Assert.Equal(2, Builds());
		Assert.Equal(0, (await monorepo.BassiaAsync("task", "run", "test", "-select", "app,lib,core", "-no-cache")).ExitCode);
		Assert.Equal(3, Builds());

		var (cacheExit, cacheOutput, _) = await monorepo.BassiaAsync("task", "cache");
		Assert.Equal(0, cacheExit);
		Assert.Contains("entries = 2", cacheOutput);
		Assert.Contains("cleared = true", (await monorepo.BassiaAsync("task", "cache", "-clear")).Output);
		Assert.Contains("entries = 0", (await monorepo.BassiaAsync("task", "cache")).Output);
	}
}
