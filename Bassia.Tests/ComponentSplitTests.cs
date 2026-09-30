using Tomlyn;
using Tomlyn.Model;

namespace Bassia.Tests;

/// <summary>
/// <c>bassia component split</c> and <c>component survey</c>: a component with a branchy history (a move into a part's
/// folder, a move between parts, a merge, tags, a dropped folder, a path reused after its file moved away) is split,
/// and each part's repository must hold exactly its files with their whole timeline.
/// </summary>
public class ComponentSplitTests
{
	private static TomlTable Result(string text) => CliSurfaceTests.Result(text);

	private static IEnumerable<TomlTable> Tables(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) ? ((TomlTableArray)value).Cast<TomlTable>() : [];

	private static string[] Strings(TomlTable table, string key) => ((TomlArray)table[key]).Cast<string>().ToArray();

	private const string Plan = """
		source = "app"
		shared = ["LICENSE", "README.md"]
		drop = ["legacy"]

		[[part]]
		name = "app-core"
		paths = ["src/core/**"]

		[[part]]
		name = "app-ui"
		paths = ["src/ui", "src/x.cs"]
		references = ["app-core", "lib"]
		url = "https://example.invalid/app-ui.git"
		""";

	/// <summary>
	/// A monorepo with <c>lib</c>, <c>app</c> (references lib) and <c>tool</c> (references app). <c>app</c>'s history:
	/// <code>
	/// init         README.md LICENSE src/a.cs src/x.cs src/ui/u.cs src/ui/moved.cs   (tag v0, annotated)
	/// move         src/a.cs -> src/core/a.cs, src/x.cs -> src/core/x.cs
	/// ui work      src/ui/u.cs                  (on a branch)
	/// core work    src/core/a.cs                (on main)
	/// merge                                     (tag v1, lightweight)
	/// legacy       legacy/old.txt
	/// move between src/ui/moved.cs -> src/core/moved.cs
	/// reuse        src/x.cs (a new file, allocated to app-ui)
	/// </code>
	/// </summary>
	private sealed class Scenario : IAsyncDisposable
	{
		private readonly TempDirectory upstreams = new();

		public required MonorepoFixture Monorepo { get; init; }
		public Dictionary<string, string> Commits { get; } = [];

		public string Upstream(string name) => Path.Combine(upstreams.Path, name);

		public static async Task<Scenario> CreateAsync()
		{
			var scenario = new Scenario { Monorepo = await MonorepoFixture.CreateAsync() };
			await scenario.BuildAsync();
			return scenario;
		}

		private async Task BuildAsync()
		{
			var app = Upstream("app");
			await InitAsync(app);
			await WriteAsync(app, "README.md", "# app\n");
			await WriteAsync(app, "LICENSE", "MIT\n");
			await WriteAsync(app, "src/a.cs", "class A {}\n");
			await WriteAsync(app, "src/x.cs", "class X {}\n");
			await WriteAsync(app, "src/ui/u.cs", "class U {}\n");
			await WriteAsync(app, "src/ui/moved.cs", "class Moved {}\n");
			await CommitAsync(app, "init", "init");
			await TestEnvironment.GitAsync(app, "tag", "-a", "v0", "-m", "baseline v0");

			Directory.CreateDirectory(Path.Combine(app, "src", "core"));
			await TestEnvironment.GitAsync(app, "mv", "src/a.cs", "src/core/a.cs");
			await TestEnvironment.GitAsync(app, "mv", "src/x.cs", "src/core/x.cs");
			await CommitAsync(app, "move", "move a and x into core");

			await TestEnvironment.GitAsync(app, "checkout", "--quiet", "-b", "feature");
			await WriteAsync(app, "src/ui/u.cs", "class U { int ui; }\n");
			await CommitAsync(app, "ui", "ui work");
			await TestEnvironment.GitAsync(app, "checkout", "--quiet", "main");
			await WriteAsync(app, "src/core/a.cs", "class A { int core; }\n");
			await CommitAsync(app, "core", "core work", date: "2020-01-02T03:04:05+02:00");
			await TestEnvironment.GitAsync(app, "merge", "--quiet", "--no-ff", "feature", "-m", "merge feature");
			Commits["merge"] = await TestEnvironment.GitAsync(app, "rev-parse", "HEAD");
			await TestEnvironment.GitAsync(app, "tag", "v1");

			await WriteAsync(app, "legacy/old.txt", "old\n");
			await CommitAsync(app, "legacy", "legacy");
			await TestEnvironment.GitAsync(app, "mv", "src/ui/moved.cs", "src/core/moved.cs");
			await CommitAsync(app, "between", "move moved.cs from ui to core");
			await WriteAsync(app, "src/x.cs", "class NewX { /* a different file */ }\n");
			await CommitAsync(app, "reuse", "a new x.cs");

			var lib = Upstream("lib");
			await InitAsync(lib);
			await WriteAsync(lib, "lib.cs", "lib\n");
			await CommitAsync(lib, "lib", "lib");
			var tool = Upstream("tool");
			await InitAsync(tool);
			await WriteAsync(tool, "tool.cs", "tool\n");
			await CommitAsync(tool, "tool", "tool");

			await AddAsync("lib", lib);
			await AddAsync("app", app, "-references", "lib");
			await AddAsync("tool", tool, "-references", "app");
		}

		private async Task AddAsync(string name, string url, params string[] extra)
		{
			var (exitCode, _, error) = await Monorepo.BassiaAsync(["component", "add", "-url", url, "-name", name, .. extra]);
			Assert.True(exitCode == 0, error);
		}

		private static async Task InitAsync(string directory)
		{
			Directory.CreateDirectory(directory);
			await TestEnvironment.GitAsync(directory, "init", "--quiet", "--initial-branch=main");
		}

		private static Task WriteAsync(string repository, string path, string content)
		{
			var full = Path.Combine(repository, path);
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			return File.WriteAllTextAsync(full, content);
		}

		private async Task CommitAsync(string repository, string key, string message, string? date = null)
		{
			await TestEnvironment.GitAsync(repository, "add", "--all");
			await TestEnvironment.GitAsync(repository, date is null
				? ["commit", "--quiet", "-m", message]
				: ["commit", "--quiet", "-m", message, "--date", date]);
			Commits[key] = await TestEnvironment.GitAsync(repository, "rev-parse", "HEAD");
		}

		public async Task<string> WritePlanAsync(string text)
		{
			var path = Path.Combine(upstreams.Path, $"plan-{Guid.NewGuid():N}.toml");
			await File.WriteAllTextAsync(path, text);
			return path;
		}

		public async ValueTask DisposeAsync()
		{
			await Monorepo.DisposeAsync();
			upstreams.Dispose();
		}
	}

	private static Task<string> GitAsync(string directory, params string[] args) => TestEnvironment.GitAsync(directory, args);

	private static async Task<string[]> TipFilesAsync(string repository) =>
		(await GitAsync(repository, "ls-tree", "-r", "--name-only", "main")).Split('\n', StringSplitOptions.RemoveEmptyEntries);

	// ----- the split -----

	[Fact]
	public async Task Split_GivesEachPartExactlyItsFiles_WithTheirWholeHistory_AndRetiresTheSource()
	{
		await using var scenario = await Scenario.CreateAsync();
		var monorepo = scenario.Monorepo;
		var sourceHead = await GitAsync(monorepo.SourceRepo("app"), "rev-parse", "main");

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "split", "-plan", await scenario.WritePlanAsync(Plan));

		Assert.True(exitCode == 0, error);
		var result = Result(output);
		var splitTag = (string)result["tag"];
		Assert.StartsWith("split/", splitTag);
		Assert.Equal(sourceHead, result["source_commit"]);

		var core = monorepo.SourceRepo("app-core");
		var ui = monorepo.SourceRepo("app-ui");

		// Tips: exactly the allocated files (shared ones included), with the source tip's content.
		Assert.Equal(["LICENSE", "README.md", "src/core/a.cs", "src/core/moved.cs", "src/core/x.cs"], await TipFilesAsync(core));
		Assert.Equal(["LICENSE", "README.md", "src/ui/u.cs", "src/x.cs"], await TipFilesAsync(ui));
		foreach (var path in new[] { "src/core/a.cs", "LICENSE" })
		{
			Assert.Equal(await GitAsync(monorepo.SourceRepo("app"), "rev-parse", $"main:{path}"), await GitAsync(core, "rev-parse", $"main:{path}"));
		}

		// Renames are followed: a.cs reaches its first commit under its old path, and so does the file moved from ui to core.
		var aHistory = await GitAsync(core, "log", "--follow", "--format=%s", "main", "--", "src/core/a.cs");
		Assert.Equal(["core work", "move a and x into core", "init"], aHistory.Split('\n'));
		var movedHistory = await GitAsync(core, "log", "--follow", "--name-status", "--format=%s", "main", "--", "src/core/moved.cs");
		Assert.Contains("src/ui/moved.cs\tsrc/core/moved.cs", movedHistory);
		Assert.Contains("A\tsrc/ui/moved.cs", movedHistory);

		// The path reused at the tip: core keeps the old x.cs (up to its move), ui gets only the new file.
		Assert.Equal(["move a and x into core", "init"], (await GitAsync(core, "log", "--follow", "--format=%s", "main", "--", "src/core/x.cs")).Split('\n'));
		Assert.Equal("a new x.cs", await GitAsync(ui, "log", "--format=%s", "main", "--", "src/x.cs"));

		// Only commits that change the part's files, in the original order; the merge collapsed; the record commit on top.
		Assert.Equal(["split(" + splitTag[6..14] + "): app-core from app", "move moved.cs from ui to core", "core work", "move a and x into core", "init"],
			(await GitAsync(core, "log", "--format=%s", "main")).Split('\n'));
		Assert.Equal(["split(" + splitTag[6..14] + "): app-ui from app", "a new x.cs", "move moved.cs from ui to core", "ui work", "init"],
			(await GitAsync(ui, "log", "--format=%s", "main")).Split('\n'));
		Assert.Equal("", await GitAsync(core, "log", "--merges", "--format=%h", "main"));

		// Authors, dates and messages are the originals, with a trailer naming the original commit.
		var original = await GitAsync(monorepo.SourceRepo("app"), "log", "-1", "--format=%an|%ae|%ad|%cn|%s", "--date=iso-strict", scenario.Commits["core"]);
		var rewritten = await GitAsync(core, "log", "-1", "--format=%an|%ae|%ad|%cn|%s", "--date=iso-strict", "main~2");
		Assert.Equal(original, rewritten);
		Assert.Equal($"app@{scenario.Commits["core"]}", await GitAsync(core, "log", "-1", "--format=%(trailers:key=Split-from,valueonly)", "main~2"));

		// Dropped files are gone from every part's history.
		Assert.Equal("", await GitAsync(core, "log", "--all", "--format=%h", "--", "legacy"));
		Assert.Equal("", await GitAsync(ui, "log", "--all", "--format=%h", "--", "legacy"));

		// Tags: the annotated v0 keeps its message, the lightweight v1 moves to the part's commit for the merge.
		Assert.Equal("tag", await GitAsync(core, "cat-file", "-t", "v0"));
		Assert.Equal("baseline v0", await GitAsync(core, "tag", "-l", "--format=%(contents:subject)", "v0"));
		Assert.Equal("commit", await GitAsync(ui, "cat-file", "-t", "v1"));
		Assert.Equal("ui work", await GitAsync(ui, "log", "-1", "--format=%s", "v1"));
		Assert.Equal("core work", await GitAsync(core, "log", "-1", "--format=%s", "v1"));

		// The split record: commit and annotated tag in every part, tag in the source; the source branch is untouched.
		var record = (TomlTable)Toml(await GitAsync(core, "log", "-1", "--format=%b", "main"))["split"];
		Assert.Equal("app", record["source"]);
		Assert.Equal("app-core", record["part"]);
		Assert.Equal(sourceHead, record["source_commit"]);
		Assert.Equal(await GitAsync(core, "rev-parse", "main"), await GitAsync(core, "rev-parse", $"{splitTag}^{{commit}}"));
		Assert.Equal("", await GitAsync(core, "diff", "main~1", "main"));
		Assert.Equal(sourceHead, await GitAsync(monorepo.SourceRepo("app"), "rev-parse", $"{splitTag}^{{commit}}"));
		Assert.Equal(sourceHead, await GitAsync(monorepo.SourceRepo("app"), "rev-parse", "main"));

		// Each part stands alone: no borrowed objects, a consistent repository, the default branch, the plan's url.
		Assert.False(File.Exists(Path.Combine(ui, ".git", "objects", "info", "alternates")));
		await GitAsync(ui, "fsck", "--full", "--no-progress");
		Assert.Equal("main", await GitAsync(ui, "symbolic-ref", "--short", "HEAD"));
		Assert.Equal("https://example.invalid/app-ui.git", await GitAsync(ui, "remote", "get-url", "origin"));

		// The meta-repo: parts registered, tool rewired to every part, app retired but kept on disk.
		var components = await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml"));
		var registered = ((TomlTableArray)Toml(components)["component"]).Cast<TomlTable>().ToDictionary(table => (string)table["name"]);
		Assert.Equal(["lib", "tool", "app-core", "app-ui"], registered.Keys);
		Assert.Equal(["app-core", "app-ui"], ((TomlArray)registered["tool"]["references"]).Cast<string>());
		Assert.Equal(["lib"], ((TomlArray)registered["app-core"]["references"]).Cast<string>());
		Assert.Equal(["app-core", "lib"], ((TomlArray)registered["app-ui"]["references"]).Cast<string>());
		Assert.True(Directory.Exists(monorepo.SourceRepo("app")));
		Assert.Contains("[split]", await GitAsync(monorepo.MetaRepo, "log", "-1", "--format=%B"));
		Assert.Equal("", await GitAsync(monorepo.MetaRepo, "status", "--porcelain"));

		var parts = Tables(result, "part").ToDictionary(part => (string)part["name"]);
		Assert.Equal(4L, parts["app-core"]["commits"]);
		Assert.Equal(["v0", "v1"], Strings(parts["app-ui"], "tags").Order());

		// A run can start from the parts at the split tag.
		await GitAsync(monorepo.SourceRepo("lib"), "tag", "-a", "v0", "-m", "baseline");
		var (runExit, runOutput, runError) = await monorepo.RunStartAsync("-select", $"app-ui@{splitTag},app-core@{splitTag},lib@v0", "-run",
			TestEnvironment.WriteFileCommand("app-ui/new.txt", "hi"));
		Assert.True(runExit == 0, runError);
		Assert.Equal("completed", Result(runOutput)["status"]);
	}

	private static TomlTable Toml(string text) => TomlSerializer.Deserialize<TomlTable>(text)!;

	[Fact]
	public async Task Split_WithoutFollowingRenames_StartsAMovedFileAtItsMove()
	{
		await using var scenario = await Scenario.CreateAsync();
		var plan = Plan.Replace("drop = [\"legacy\"]", "drop = [\"legacy\"]\nfollow_renames = false");

		var (exitCode, _, error) = await scenario.Monorepo.BassiaAsync("component", "split", "-plan", await scenario.WritePlanAsync(plan));

		Assert.True(exitCode == 0, error);
		var core = scenario.Monorepo.SourceRepo("app-core");
		Assert.Equal(["core work", "move a and x into core"], (await GitAsync(core, "log", "--follow", "--format=%s", "main", "--", "src/core/a.cs")).Split('\n'));
	}

	[Fact]
	public async Task DryRun_ReportsTheAllocationAndTheResultingGraph_AndChangesNothing()
	{
		await using var scenario = await Scenario.CreateAsync();
		var monorepo = scenario.Monorepo;
		var before = await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml"));

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "split", await scenario.WritePlanAsync(Plan), "-dry-run");

		Assert.True(exitCode == 0, error);
		var result = Result(output);
		Assert.True((bool)result["dry_run"]);
		Assert.Equal(["legacy/old.txt"], Strings(result, "dropped"));
		var parts = Tables(result, "part").ToDictionary(part => (string)part["name"]);
		Assert.Equal(5L, parts["app-core"]["files"]);
		Assert.Equal(4L, parts["app-ui"]["files"]);
		Assert.Equal(["app-core", "app-ui"], Strings(Tables(result, "referrer").Single(), "references"));
		Assert.Contains("app-ui", (string)result["graph"]);
		Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml")));
		Assert.False(Directory.Exists(monorepo.SourceRepo("app-core")));
		Assert.Equal("", await GitAsync(monorepo.SourceRepo("app"), "tag", "-l", "split/*"));
	}

	[Fact]
	public async Task Plan_FromStdin_WithTheSourceNamedOnTheCommandLine()
	{
		await using var scenario = await Scenario.CreateAsync();
		var originalIn = Console.In;
		Console.SetIn(new StringReader(Plan.Replace("source = \"app\"", "")));
		try
		{
			var (exitCode, output, error) = await scenario.Monorepo.BassiaAsync("component", "split", "-name", "app", "-plan", "-", "-dry-run");

			Assert.True(exitCode == 0, error);
			Assert.Equal("app", Result(output)["source"]);
		}
		finally
		{
			Console.SetIn(originalIn);
		}
	}

	[Fact]
	public async Task InvalidPlan_ReportsEveryProblemAtOnce_AndChangesNothing()
	{
		await using var scenario = await Scenario.CreateAsync();
		var monorepo = scenario.Monorepo;
		var before = await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml"));
		const string plan = """
			source = "app"

			[[part]]
			name = "app-core"
			paths = ["src/core", "src/ui/u.cs"]
			references = ["nowhere"]

			[[part]]
			name = "app-ui"
			paths = ["src/ui"]

			[[part]]
			name = "lib"
			paths = ["docs"]
			colour = "red"
			""";

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "split", "-plan", await scenario.WritePlanAsync(plan));

		Assert.Equal(1, exitCode);
		Assert.Empty(output);
		var result = Result(error);
		Assert.False((bool)result["ok"]);
		var problems = string.Join("\n", Strings(result, "problems"));
		Assert.Contains("unknown key 'colour'", problems);
		Assert.Contains("belong to no part", problems);
		Assert.Contains("match more than one part", problems);
		Assert.Contains("Part 'lib' gets no file", problems);
		Assert.Contains("Part 'lib': a component of that name is already registered", problems);
		Assert.Contains("references 'nowhere'", problems);
		Assert.Contains("README.md", Strings(result, "unallocated"));
		Assert.Contains("src/ui/u.cs (app-core, app-ui)", Strings(result, "ambiguous"));
		Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml")));
		Assert.False(Directory.Exists(monorepo.SourceRepo("app-core")));
	}

	[Fact]
	public async Task PartsThatReferenceEachOtherInACycle_AreRejected()
	{
		await using var scenario = await Scenario.CreateAsync();
		var plan = Plan.Replace("paths = [\"src/core/**\"]", "paths = [\"src/core/**\"]\nreferences = [\"app-ui\"]");

		var (exitCode, _, error) = await scenario.Monorepo.BassiaAsync("component", "split", "-plan", await scenario.WritePlanAsync(plan));

		Assert.Equal(1, exitCode);
		Assert.Contains("cycle", string.Join("\n", Strings(Result(error), "problems")));
	}

	[Fact]
	public async Task AFailureAfterThePartsWereBuilt_RemovesThem_AndRestoresTheMetaRepo()
	{
		await using var scenario = await Scenario.CreateAsync();
		var monorepo = scenario.Monorepo;
		var before = await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml"));
		var hook = Path.Combine(monorepo.MetaRepo, ".git", "hooks", "pre-commit");
		await File.WriteAllTextAsync(hook, "#!/bin/sh\necho refused >&2\nexit 1\n");
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		var (exitCode, _, error) = await monorepo.BassiaAsync("component", "split", "-plan", await scenario.WritePlanAsync(Plan));

		Assert.Equal(1, exitCode);
		Assert.Contains("refused", (string)Result(error)["error"]);
		Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml")));
		Assert.False(Directory.Exists(monorepo.SourceRepo("app-core")));
		Assert.False(Directory.Exists(monorepo.SourceRepo("app-ui")));
		Assert.Equal("", await GitAsync(monorepo.SourceRepo("app"), "tag", "-l", "split/*"));
	}

	// ----- the survey -----

	[Fact]
	public async Task Survey_ReportsFoldersChangeCountsCouplingAndAPlanSkeleton()
	{
		await using var scenario = await Scenario.CreateAsync();

		var (exitCode, output, error) = await scenario.Monorepo.BassiaAsync("component", "survey", "app");

		Assert.True(exitCode == 0, error);
		var result = Result(output);
		Assert.Equal(8L, result["files"]);
		Assert.Equal(["LICENSE", "README.md"], Strings(result, "top_level_files"));
		var folders = Tables(result, "folder").ToDictionary(folder => (string)folder["path"]);
		Assert.Equal(["legacy", "src", "src/core", "src/ui"], folders.Keys.Order(StringComparer.Ordinal));
		Assert.Equal(3L, folders["src/core"]["files"]);
		Assert.Equal(3L, folders["src/core"]["commits"]);
		Assert.Equal(1L, folders["legacy"]["commits"]);
		var pair = Tables(result, "pair").Single(entry => Strings(entry, "folders").SequenceEqual(["src/core", "src/ui"]));
		Assert.Equal(1L, pair["commits"]);

		// The skeleton is a plan: it parses, names the source, and has a part per top-level folder.
		var skeleton = Toml((string)result["plan"]);
		Assert.Equal("app", skeleton["source"]);
		Assert.Equal(["app-legacy", "app-src"], ((TomlTableArray)skeleton["part"]).Cast<TomlTable>().Select(part => (string)part["name"]));
	}
}
