namespace Bassia.Tests;

/// <summary>
/// <c>bassia clone</c>, <c>bassia fetch</c>, <c>bassia view ...</c> and sparse runs (<c>agent start -spec</c>) on the git
/// storage: a clone of a local depot holds the components' histories as partial clones, and git's own view of the
/// clone's repositories proves which file contents were downloaded.
/// </summary>
public class ViewCommandTests
{
	private const string Spec = "# the app and its lib\nmap app\nselect lib v0\nload app/src\nload lib/include\n";

	/// <summary>A depot with lib (include/lib.h, src/lib.c, README.md) and app (src/main.c, docs/guide.md, README.md) referencing lib.</summary>
	private static async Task<MonorepoFixture> DepotAsync()
	{
		var depot = await MonorepoFixture.CreateAsync();
		await depot.AddComponentAsync("lib", files: new Dictionary<string, string> { ["include/lib.h"] = "int f();\n", ["src/lib.c"] = "int f() { return 1; }\n" });
		await depot.AddComponentAsync("app", files: new Dictionary<string, string> { ["src/main.c"] = "int main;\n", ["docs/guide.md"] = "guide\n" });
		Assert.Equal(0, (await depot.BassiaAsync("component", "set", "app", "-references", "lib")).ExitCode);
		return depot;
	}

	private static async Task<string> CloneAsync(MonorepoFixture depot, TempDirectory temp, params string[] options)
	{
		var clone = Path.Combine(temp.Path, "clone");
		var (exit, _, error) = await TestEnvironment.RunInDirectoryAsync(temp.Path, ["clone", "-from", depot.Root, "-path", clone, .. options]);
		Assert.True(exit == 0, error);
		return clone;
	}

	/// <summary>The paths of the files of a commit whose content the repository does not hold (git lists them without fetching them).</summary>
	private static async Task<string[]> MissingAsync(string repository, string commit = "HEAD")
	{
		var missing = (await TestEnvironment.GitAsync(repository, "rev-list", "--objects", "--missing=print", "--no-walk", commit))
			.Split('\n').Where(line => line.StartsWith('?')).Select(line => line[1..].Trim()).ToHashSet();
		return (await TestEnvironment.GitAsync(repository, "ls-tree", "-r", commit)).Split('\n')
			.Select(line => line.Split('\t')).Where(fields => missing.Contains(fields[0].Split(' ')[2])).Select(fields => fields[1]).Order(StringComparer.Ordinal).ToArray();
	}

	private static string[] FilesOnDisk(string folder) =>
		Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
			.Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
			.Where(path => !path.StartsWith(".git/", StringComparison.Ordinal) && path != ".bassia-view.toml").Order(StringComparer.Ordinal).ToArray();

	[Fact]
	public async Task Clone_HoldsTheMetaRepoAndEveryHistory_WithoutAnyFileContent()
	{
		await using var depot = await DepotAsync();
		using var temp = new TempDirectory();

		var clone = await CloneAsync(depot, temp);

		Assert.Contains("name = \"app\"", await File.ReadAllTextAsync(Path.Combine(clone, ".bassia", "components.toml")));
		Assert.Contains($"url = \"{depot.Root}\"", await File.ReadAllTextAsync(Path.Combine(clone, ".bassia", "config.user.toml")));
		Assert.Equal(["README.md", "include/lib.h", "src/lib.c"], await MissingAsync(Path.Combine(clone, "lib")));
		Assert.Equal(["README.md", "docs/guide.md", "src/main.c"], await MissingAsync(Path.Combine(clone, "app")));
		Assert.Equal(await TestEnvironment.GitAsync(depot.SourceRepo("lib"), "rev-parse", "v0"), await TestEnvironment.GitAsync(Path.Combine(clone, "lib"), "rev-parse", "v0"));
	}

	[Fact]
	public async Task Clone_FailsIntoAFolderInUse_AndFromSomethingThatIsNoMonorepo_LeavingNothing()
	{
		await using var depot = await DepotAsync();
		using var temp = new TempDirectory();
		var used = Path.Combine(temp.Path, "used");
		Directory.CreateDirectory(used);
		await File.WriteAllTextAsync(Path.Combine(used, "x"), "x");

		var (usedExit, _, usedError) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "clone", "-from", depot.Root, "-path", used);
		var (noneExit, _, noneError) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "clone", "-from", Path.Combine(temp.Path, "nothing"), "-path", Path.Combine(temp.Path, "c"));

		Assert.Equal((1, 1), (usedExit, noneExit));
		Assert.Contains("is not empty", usedError);
		Assert.Contains("is not a Bassia monorepo", noneError);
		Assert.False(Directory.Exists(Path.Combine(temp.Path, "c")));
	}

	[Fact]
	public async Task ViewCreate_DownloadsOnlyTheLoadedFiles_AndViewLoadFetchesMoreFromInsideTheView()
	{
		await using var depot = await DepotAsync();
		using var temp = new TempDirectory();
		var clone = await CloneAsync(depot, temp);
		var specFile = Path.Combine(temp.Path, "parser.spec");
		await File.WriteAllTextAsync(specFile, Spec);
		var folder = Path.Combine(temp.Path, "view");

		var (exit, output, error) = await TestEnvironment.RunInDirectoryAsync(clone, "view", "create", "-name", "parser", "-spec", specFile, "-path", folder);

		Assert.True(exit == 0, error);
		Assert.Contains("2 of 6 file(s) loaded", output);
		Assert.Equal(["src/main.c"], FilesOnDisk(Path.Combine(folder, "app")).Where(path => !path.StartsWith("lib/")));
		Assert.Equal(["include/lib.h"], FilesOnDisk(Path.Combine(folder, "lib")));
		Assert.Equal("int f();", (await File.ReadAllTextAsync(Path.Combine(folder, "app", "lib", "include", "lib.h"))).Trim());
		Assert.Equal(["README.md", "src/lib.c"], await MissingAsync(Path.Combine(clone, "lib"), "v0"));
		Assert.Equal(["README.md", "docs/guide.md"], await MissingAsync(Path.Combine(clone, "app")));

		// From inside the view, outside the monorepo: ls reads the history, load fetches through the nested lib.
		var (lsExit, lsOutput, lsError) = await TestEnvironment.RunInDirectoryAsync(Path.Combine(folder, "app"), "view", "ls", "-unloaded");
		Assert.True(lsExit == 0, lsError);
		Assert.Contains("4 file(s) in view 'parser', 0 loaded", lsOutput);
		Assert.Contains("path = \"lib/src/lib.c\"", lsOutput);
		var (loadExit, loadOutput, loadError) = await TestEnvironment.RunInDirectoryAsync(Path.Combine(folder, "app"), "view", "load", "lib/src,docs");
		Assert.True(loadExit == 0, loadError);
		Assert.Contains("Loaded 2 file(s)", loadOutput);
		Assert.Equal("int f() { return 1; }", (await File.ReadAllTextAsync(Path.Combine(folder, "lib", "src", "lib.c"))).Trim());
		Assert.Equal(["README.md"], await MissingAsync(Path.Combine(clone, "lib"), "v0"));
		Assert.Contains("load = [\"include\", \"src\"]", await File.ReadAllTextAsync(Path.Combine(folder, ".bassia-view.toml")));

		var (showExit, showOutput, _) = await TestEnvironment.RunInDirectoryAsync(clone, "view", "show", "parser");
		Assert.Equal(0, showExit);
		Assert.Contains("loaded = 2", showOutput);
		var (missExit, _, missError) = await TestEnvironment.RunInDirectoryAsync(folder, "view", "load", "app/nothing");
		Assert.Equal(1, missExit);
		Assert.Contains("matches no file", missError);
	}

	[Fact]
	public async Task SpecRun_IsSparse_KeepsUnloadedFiles_AndPushesItsResultsToTheDepot()
	{
		await using var depot = await DepotAsync();
		using var temp = new TempDirectory();
		var clone = await CloneAsync(depot, temp);
		var specFile = Path.Combine(temp.Path, "parser.spec");
		await File.WriteAllTextAsync(specFile, Spec);

		var (exit, output, error) = await TestEnvironment.RunInDirectoryAsync(clone, "agent", "start", "-spec", specFile, "-run",
			TestEnvironment.WriteFileCommand("app/src/new.c", "int added;"));

		Assert.True(exit == 0, error);
		Assert.Contains("load = \"app/src,lib/include\"", output);
		var runId = TestEnvironment.RunIdOf(output);
		var runDir = Path.Combine(clone, ".workspace", runId);
		Assert.Contains("view load", await File.ReadAllTextAsync(Path.Combine(runDir, "AGENTS.md")));
		Assert.Contains("kind = \"run\"", await File.ReadAllTextAsync(Path.Combine(runDir, ".bassia-view.toml")));

		// Written through: the depot's app has the result, with the files the run never loaded unchanged.
		var tag = $"agent-run/{runId["agent-run-".Length..]}/0";
		Assert.Equal("README.md\ndocs/guide.md\nsrc/main.c\nsrc/new.c", await TestEnvironment.GitAsync(depot.SourceRepo("app"), "ls-tree", "-r", "--name-only", tag));
		Assert.Equal("guide", await TestEnvironment.GitAsync(depot.SourceRepo("app"), "show", $"{tag}:docs/guide.md"));
		Assert.Equal(["README.md", "docs/guide.md"], await MissingAsync(Path.Combine(clone, "app"), tag));

		var (bothExit, _, bothError) = await TestEnvironment.RunInDirectoryAsync(clone, "agent", "start", "-spec", specFile, "-select", "lib", "-run", "true");
		Assert.Equal(2, bothExit);
		Assert.Contains("exclude each other", bothError);
	}

	[Fact]
	public async Task SelectRun_WithLoad_IsSparseInAMonorepoOfItsOwn()
	{
		await using var monorepo = await DepotAsync();

		var (exit, output, error) = await monorepo.RunStartAsync("-select", "app,lib", "-load", "app/docs", "-run", TestEnvironment.WriteFileCommand("app/docs/more.md", "more"));

		Assert.True(exit == 0, error);
		var runId = TestEnvironment.RunIdOf(output);
		Assert.Equal(["docs/guide.md"], FilesOnDisk(monorepo.Checkout(runId, "app")).Where(path => !path.StartsWith("lib/")).Except(["docs/more.md"]));
		Assert.Empty(FilesOnDisk(monorepo.Checkout(runId, "lib")));
		var tag = $"agent-run/{runId["agent-run-".Length..]}/0";
		Assert.Equal("README.md\ndocs/guide.md\ndocs/more.md\nsrc/main.c", await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "ls-tree", "-r", "--name-only", tag));
	}

	[Fact]
	public async Task FetchAndViewUpdate_FollowTheDepot_AndALazyCloneImportsOnlyWhatAViewMaps()
	{
		await using var depot = await DepotAsync();
		using var temp = new TempDirectory();
		var clone = await CloneAsync(depot, temp, "-lazy");
		Assert.False(Directory.Exists(Path.Combine(clone, "lib")));

		var (createExit, _, createError) = await TestEnvironment.RunInDirectoryAsync(clone, "view", "create", "-name", "lib", "-spec", WriteSpec(temp, "map lib\nload lib/include\n"));
		Assert.True(createExit == 0, createError);
		Assert.True(Directory.Exists(Path.Combine(clone, "lib")));
		Assert.False(Directory.Exists(Path.Combine(clone, "app")));

		// The depot moves lib's main; fetch brings the history, view update the view.
		var work = Path.Combine(temp.Path, "work");
		await TestEnvironment.GitAsync(temp.Path, "clone", "--quiet", depot.SourceRepo("lib"), work);
		await File.WriteAllTextAsync(Path.Combine(work, "include", "lib.h"), "int f(); int g();\n");
		await TestEnvironment.GitAsync(work, "commit", "--quiet", "-am", "more");
		await TestEnvironment.GitAsync(work, "push", "--quiet", "origin", "HEAD:main");

		var (fetchExit, fetchOutput, fetchError) = await TestEnvironment.RunInDirectoryAsync(clone, "fetch");
		Assert.True(fetchExit == 0, fetchError);
		Assert.Contains("1 component(s) updated, 0 imported", fetchOutput);
		var (updateExit, updateOutput, updateError) = await TestEnvironment.RunInDirectoryAsync(clone, "view", "update", "lib");
		Assert.True(updateExit == 0, updateError);
		Assert.Contains("1 component(s) moved", updateOutput);
		Assert.Equal("int f(); int g();", (await File.ReadAllTextAsync(Path.Combine(clone, ".workspace", "view-lib", "lib", "include", "lib.h"))).Trim());
		Assert.Equal(["README.md", "src/lib.c"], await MissingAsync(Path.Combine(clone, "lib"), "main"));

		var (removeExit, _, removeError) = await TestEnvironment.RunInDirectoryAsync(clone, "view", "remove", "lib");
		Assert.True(removeExit == 0, removeError);
		Assert.False(Directory.Exists(Path.Combine(clone, ".workspace", "view-lib")));
		Assert.Contains("0 view(s)", (await TestEnvironment.RunInDirectoryAsync(clone, "view", "list")).Output);
	}

	private static string WriteSpec(TempDirectory temp, string text)
	{
		var path = Path.Combine(temp.Path, $"spec-{Guid.NewGuid():N}");
		File.WriteAllText(path, text);
		return path;
	}
}
