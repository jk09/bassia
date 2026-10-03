using Tomlyn.Model;

namespace Bassia.Tests.Unwind;

/// <summary>
/// <c>component unwind</c> and <c>component add -unwind</c> over upstream repositories with (nested) submodules, and
/// the <c>tag</c> commands over the tags they leave. Gitlinks are written straight into the index, so the upstreams
/// need no submodule clones.
/// </summary>
public class UnwindCommandTests
{
	/// <summary>
	/// Upstreams: <c>core</c> (C1, C2); <c>lib</c> whose L1 pins core@C1 at ext/core and L2 (main) pins core@C2;
	/// <c>app</c> pinning lib@L1 at vendor/lib by a relative URL; <c>tool</c> pinning lib@L2 at deps/lib.
	/// </summary>
	private sealed class Upstreams : IDisposable
	{
		private readonly TempDirectory temp = new();

		public string Dir(string name) => Path.Combine(temp.Path, name);
		public string C1 = "", C2 = "", L1 = "", L2 = "", App = "", Tool = "";

		public static async Task<Upstreams> CreateAsync()
		{
			var upstreams = new Upstreams();
			upstreams.C1 = await CommitAsync(upstreams.Dir("core"), "core 1", ("core.txt", "1"));
			upstreams.C2 = await CommitAsync(upstreams.Dir("core"), "core 2", ("core.txt", "2"));
			upstreams.L1 = await CommitAsync(upstreams.Dir("lib"), "lib 1", [("lib.txt", "1")], [("ext/core", upstreams.C1, upstreams.Dir("core"))]);
			upstreams.L2 = await CommitAsync(upstreams.Dir("lib"), "lib 2", [("lib.txt", "2")], [("ext/core", upstreams.C2, upstreams.Dir("core"))]);
			upstreams.App = await CommitAsync(upstreams.Dir("app"), "app 1", [("app.txt", "1")], [("vendor/lib", upstreams.L1, "../lib")]);
			upstreams.Tool = await CommitAsync(upstreams.Dir("tool"), "tool 1", [("tool.txt", "1")], [("deps/lib", upstreams.L2, upstreams.Dir("lib") + ".git")]);
			return upstreams;
		}

		public static Task<string> CommitAsync(string repo, string message, params (string Path, string Content)[] files) =>
			CommitAsync(repo, message, files, []);

		/// <summary>Commits <paramref name="files"/> and exactly <paramref name="submodules"/> as gitlinks, with a matching .gitmodules.</summary>
		public static async Task<string> CommitAsync(string repo, string message, (string Path, string Content)[] files, (string Path, string Commit, string Url)[] submodules)
		{
			if (!Directory.Exists(Path.Combine(repo, ".git")))
			{
				Directory.CreateDirectory(repo);
				await TestEnvironment.GitAsync(repo, "init", "--quiet", "--initial-branch=main");
			}

			foreach (var (file, content) in files)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, file))!);
				await File.WriteAllTextAsync(Path.Combine(repo, file), content);
			}

			var gitmodules = Path.Combine(repo, ".gitmodules");
			if (submodules.Length > 0)
			{
				await File.WriteAllTextAsync(gitmodules, string.Concat(submodules.Select(submodule =>
					$"[submodule \"{submodule.Path}\"]\n\tpath = {submodule.Path}\n\turl = {submodule.Url}\n")));
			}
			else if (File.Exists(gitmodules))
			{
				File.Delete(gitmodules);
			}

			await TestEnvironment.GitAsync(repo, "add", "--all");
			foreach (var submodule in submodules)
			{
				await TestEnvironment.GitAsync(repo, "update-index", "--add", "--cacheinfo", $"160000,{submodule.Commit},{submodule.Path}");
			}

			await TestEnvironment.GitAsync(repo, "commit", "--quiet", "-m", message);
			return await TestEnvironment.GitAsync(repo, "rev-parse", "HEAD");
		}

		public void Dispose() => temp.Dispose();
	}

	private static TomlTable Result(string text) => CliSurfaceTests.Result(text);

	private static IEnumerable<TomlTable> Tables(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) ? ((TomlTableArray)value).Cast<TomlTable>() : [];

	private static Task<string> Git(string directory, params string[] args) => TestEnvironment.GitAsync(directory, args);

	private static async Task<string> GitlinksAsync(string repo, string commit) =>
		string.Join("\n", (await Git(repo, "ls-tree", "-r", commit)).Split('\n').Where(line => line.StartsWith("160000")));

	[Fact]
	public async Task AddUnwind_TurnsNestedSubmodulesIntoReferencedComponents_AndLinksThePinsWithTags()
	{
		using var upstreams = await Upstreams.CreateAsync();
		await using var monorepo = await MonorepoFixture.CreateAsync();

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "add", "-url", upstreams.Dir("app"), "-unwind");

		Assert.True(exitCode == 0, error);
		var result = Result(output);
		Assert.Equal(["app", "lib", "core"], Tables(result, "component").Select(component => (string)component["name"]));
		Assert.Equal(["unwound", "added", "added"], Tables(result, "component").Select(component => (string)component["action"]));

		// components.toml: each submodule is a reference at its path; one meta-repo commit.
		var monorepoModel = Monorepo.Load(monorepo.Root);
		Assert.Equal([new ComponentReference("lib", "vendor/lib")], monorepoModel.FindComponent("app")!.References);
		Assert.Equal([new ComponentReference("core", "ext/core")], monorepoModel.FindComponent("lib")!.References);
		Assert.Empty(monorepoModel.FindComponent("core")!.References);
		Assert.Equal("Add component 'app' from '" + upstreams.Dir("app") + "' and unwind its submodules (adds lib, core)",
			await Git(monorepo.MetaRepo, "log", "-1", "--format=%s"));
		Assert.Empty(await Git(monorepo.MetaRepo, "status", "--porcelain"));
		Assert.Empty(Directory.GetDirectories(monorepo.Root, ".unwind-staging-*"));

		// Main of app and lib: one commit on top of the old tip, without gitlinks or .gitmodules.
		var app = monorepo.SourceRepo("app");
		var lib = monorepo.SourceRepo("lib");
		Assert.Equal(upstreams.App, await Git(app, "rev-parse", "main~1"));
		Assert.Empty(await GitlinksAsync(app, "main"));
		Assert.Empty(await Git(app, "ls-tree", "main", ".gitmodules"));
		Assert.Equal("1", await Git(app, "show", "main:app.txt"));
		Assert.Equal(upstreams.L2, await Git(lib, "rev-parse", "main~1"));
		Assert.Empty(await GitlinksAsync(lib, "main"));

		// unwind/app/0 links app's unwind commit with lib's pinned L1 (itself unwound, off main) and core's C1.
		var appTag = Tables(result, "tag").Single(tag => ((string)tag["name"]).StartsWith("unwind/app/"));
		Assert.Equal("unwind/app/0", appTag["name"]);
		Assert.Equal(3L, appTag["components"]);
		Assert.Equal(await Git(app, "rev-parse", "main"), await Git(app, "rev-parse", "unwind/app/0^{commit}"));
		Assert.Equal(upstreams.L1, await Git(lib, "rev-parse", "unwind/app/0^{commit}~1"));
		Assert.Empty(await GitlinksAsync(lib, "unwind/app/0"));
		Assert.NotEqual("0", await Git(lib, "rev-list", "--count", "unwind/app/0", "^main"));
		Assert.Equal(upstreams.C1, await Git(monorepo.SourceRepo("core"), "rev-parse", "unwind/app/0^{commit}"));

		// The pinned combination is runnable, with the submodules junctioned at their paths.
		(exitCode, output, error) = await monorepo.RunStartAsync("-select", "app@unwind/app/0,lib@unwind/app/0,core@unwind/app/0", "-run", "echo");
		Assert.True(exitCode == 0, error);
		var runId = TestEnvironment.RunIdOf(output);
		Assert.Equal("1", (await File.ReadAllTextAsync(Path.Combine(monorepo.Checkout(runId, "app"), "vendor", "lib", "lib.txt"))).Trim());
		Assert.Equal("1", (await File.ReadAllTextAsync(Path.Combine(monorepo.Checkout(runId, "app"), "vendor", "lib", "ext", "core", "core.txt"))).Trim());

		// So are the main branches.
		(exitCode, output, error) = await monorepo.RunStartAsync("-select", "app,lib,core", "-run", "echo");
		Assert.True(exitCode == 0, error);
		runId = TestEnvironment.RunIdOf(output);
		Assert.Equal("2", (await File.ReadAllTextAsync(Path.Combine(monorepo.Checkout(runId, "lib"), "ext", "core", "core.txt"))).Trim());
	}

	[Fact]
	public async Task Unwind_SameSubmoduleInTwoComponentsAtDifferentCommits_ReusesTheComponentAndTagsEachCombination()
	{
		using var upstreams = await Upstreams.CreateAsync();
		await using var monorepo = await MonorepoFixture.CreateAsync();
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "add", "-url", upstreams.Dir("app"), "-unwind")).ExitCode);
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "add", "-url", upstreams.Dir("tool"))).ExitCode);

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "unwind", "tool");

		Assert.True(exitCode == 0, error);
		var result = Result(output);
		var submodule = Tables(result, "submodule").Single(row => (string)row["parent"] == "tool");
		Assert.Equal("lib", submodule["component"]);
		Assert.Equal("reused", submodule["action"]);
		Assert.Equal(upstreams.L2, submodule["pinned"]);
		Assert.Equal([new ComponentReference("lib", "deps/lib")], Monorepo.Load(monorepo.Root).FindComponent("tool")!.References);
		Assert.False(Directory.Exists(monorepo.SourceRepo("tool-lib")));

		// lib is pinned at L1 by app and at L2 by tool: each combination has its own identically named tags.
		var lib = monorepo.SourceRepo("lib");
		Assert.Equal(upstreams.L1, await Git(lib, "rev-parse", "unwind/app/0^{commit}~1"));
		Assert.Equal(upstreams.L2, await Git(lib, "rev-parse", "unwind/tool/0^{commit}~1"));
		Assert.Equal(upstreams.C2, await Git(monorepo.SourceRepo("core"), "rev-parse", "unwind/tool/0^{commit}"));

		(exitCode, output, error) = await monorepo.RunStartAsync("-select", "tool@unwind/tool/0,lib@unwind/tool/0,core@unwind/tool/0", "-run", "echo");
		Assert.True(exitCode == 0, error);
		var runId = TestEnvironment.RunIdOf(output);
		Assert.Equal("2", (await File.ReadAllTextAsync(Path.Combine(monorepo.Checkout(runId, "tool"), "deps", "lib", "lib.txt"))).Trim());

		// tag list counts the components of each tag.
		(exitCode, output, error) = await monorepo.BassiaAsync("tag", "list", "-prefix", "unwind/", "-min", "2");
		Assert.True(exitCode == 0, error);
		var tags = Tables(Result(output), "tag").ToDictionary(tag => (string)tag["name"], tag => (long)tag["components"]);
		Assert.Equal(3L, tags["unwind/app/0"]);
		Assert.Equal(3L, tags["unwind/tool/0"]);
		Assert.All(Tables(Result(output), "tag"), tag => Assert.Equal("unwind", tag["kind"]));
	}

	[Fact]
	public async Task Unwind_DryRun_ChangesNothing()
	{
		using var upstreams = await Upstreams.CreateAsync();
		await using var monorepo = await MonorepoFixture.CreateAsync();
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "add", "-url", upstreams.Dir("app"))).ExitCode);
		var components = await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml"));

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "unwind", "-name", "app", "-dry-run");

		Assert.True(exitCode == 0, error);
		var result = Result(output);
		Assert.True((bool)result["dry_run"]);
		Assert.Equal(3, Tables(result, "submodule").Count()); // app -> lib@L1, lib@L1 -> core@C1, lib's main L2 -> core@C2
		Assert.Contains("unwind/app/0", Tables(result, "tag").Select(tag => (string)tag["name"]));
		Assert.Equal(components, await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml")));
		Assert.Equal(upstreams.App, await Git(monorepo.SourceRepo("app"), "rev-parse", "main"));
		Assert.Empty(await Git(monorepo.SourceRepo("app"), "tag", "--list"));
		Assert.False(Directory.Exists(monorepo.SourceRepo("lib")));
		Assert.Empty(Directory.GetDirectories(monorepo.Root, ".unwind-staging-*"));
	}

	[Fact]
	public async Task Unwind_PinnedCommitMissing_FailsAndChangesNothing()
	{
		using var upstreams = await Upstreams.CreateAsync();
		await using var monorepo = await MonorepoFixture.CreateAsync();
		var missing = new string('e', 40);
		await Upstreams.CommitAsync(upstreams.Dir("broken"), "broken", [("b.txt", "1")], [("ext/core", missing, upstreams.Dir("core"))]);
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "add", "-url", upstreams.Dir("broken"))).ExitCode);
		var components = await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml"));

		var (exitCode, _, error) = await monorepo.BassiaAsync("component", "unwind", "broken");

		Assert.Equal(1, exitCode);
		Assert.Contains(missing, error);
		Assert.Contains("ext/core", error);
		Assert.Equal(components, await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml")));
		Assert.False(Directory.Exists(monorepo.SourceRepo("core")));
		Assert.Empty(Directory.GetDirectories(monorepo.Root, ".unwind-staging-*"));

		// component add -unwind leaves nothing behind either.
		(exitCode, _, error) = await monorepo.BassiaAsync("component", "add", "-url", upstreams.Dir("broken"), "-name", "broken2", "-unwind");
		Assert.Equal(1, exitCode);
		Assert.False(Directory.Exists(monorepo.SourceRepo("broken2")));
		Assert.Null(Monorepo.Load(monorepo.Root).FindComponent("broken2"));
	}

	[Fact]
	public async Task Unwind_SubmodulesThatContainTheirParent_AreRejectedAsACycle()
	{
		using var upstreams = await Upstreams.CreateAsync();
		await using var monorepo = await MonorepoFixture.CreateAsync();
		var x1 = await Upstreams.CommitAsync(upstreams.Dir("x"), "x 1", ("x.txt", "1"));
		var y1 = await Upstreams.CommitAsync(upstreams.Dir("y"), "y 1", [("y.txt", "1")], [("x", x1, upstreams.Dir("x"))]);
		await Upstreams.CommitAsync(upstreams.Dir("x"), "x 2", [("x.txt", "2")], [("y", y1, upstreams.Dir("y"))]);

		var (exitCode, _, error) = await monorepo.BassiaAsync("component", "add", "-url", upstreams.Dir("x"), "-unwind");

		Assert.Equal(1, exitCode);
		Assert.Contains("cycle", error);
		Assert.Empty(Monorepo.Load(monorepo.Root).Components);
		Assert.False(Directory.Exists(monorepo.SourceRepo("x")));
		Assert.False(Directory.Exists(monorepo.SourceRepo("y")));
	}

	[Fact]
	public async Task Unwind_NameTakenByAnotherRepository_PrefixesTheParentName()
	{
		using var upstreams = await Upstreams.CreateAsync();
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib"); // an unrelated repository named lib

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "add", "-url", upstreams.Dir("app"), "-unwind");

		Assert.True(exitCode == 0, error);
		Assert.Equal([new ComponentReference("app-lib", "vendor/lib")], Monorepo.Load(monorepo.Root).FindComponent("app")!.References);
		Assert.Contains(Tables(Result(output), "component"), component => (string)component["name"] == "app-lib" && (string)component["action"] == "added");
	}

	[Fact]
	public async Task Unwind_ComponentWithoutSubmodules_ReportsNothingToDo()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("plain");

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "unwind", "plain");

		Assert.True(exitCode == 0, error);
		Assert.Contains("nothing to unwind", (string)Result(output)["message"]);
	}

	[Fact]
	public async Task TagCreate_TagsEveryComponentOrNone_AndListShowCountThem()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.AddComponentAsync("tool", tag: "release-1");

		var (exitCode, output, error) = await monorepo.BassiaAsync("tag", "create", "release-1", "-select", "app,lib");
		Assert.True(exitCode == 0, error);
		Assert.Equal("app@release-1,lib@release-1", Result(output)["select"]);

		// Already in tool: nothing is created anywhere.
		(exitCode, _, error) = await monorepo.BassiaAsync("tag", "create", "-tag", "release-1", "-select", "app,tool");
		Assert.Equal(1, exitCode);
		(exitCode, _, error) = await monorepo.BassiaAsync("tag", "create", "-tag", "release-2", "-select", "app,tool@nope");
		Assert.Equal(1, exitCode);
		Assert.Empty(await Git(monorepo.SourceRepo("app"), "tag", "--list", "release-2"));

		(exitCode, output, error) = await monorepo.BassiaAsync("tag", "list");
		Assert.True(exitCode == 0, error);
		var tags = Tables(Result(output), "tag").ToDictionary(tag => (string)tag["name"]);
		Assert.Equal(3L, tags["release-1"]["components"]);
		Assert.Equal(2L, tags["v0"]["components"]);
		Assert.Equal("other", tags["release-1"]["kind"]);
		Assert.Contains("release-1", (string)Result(output)["table"]);

		(exitCode, output, error) = await monorepo.BassiaAsync("tag", "list", "-min", "3");
		Assert.True(exitCode == 0, error);
		Assert.Equal(["release-1"], Tables(Result(output), "tag").Select(tag => (string)tag["name"]));

		(exitCode, output, error) = await monorepo.BassiaAsync("tag", "list", "-component", "tool");
		Assert.True(exitCode == 0, error);
		Assert.Equal(["release-1"], Tables(Result(output), "tag").Select(tag => (string)tag["name"]));

		(exitCode, output, error) = await monorepo.BassiaAsync("tag", "show", "release-1");
		Assert.True(exitCode == 0, error);
		Assert.Equal(["app", "lib", "tool"], Tables(Result(output), "component").Select(component => (string)component["name"]));

		(exitCode, _, _) = await monorepo.BassiaAsync("tag", "show", "missing");
		Assert.Equal(1, exitCode);
	}
}
