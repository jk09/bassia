namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Storage;
using Bassia.Storage.Memory;
using static MemoryMonorepo;

/// <summary>
/// Config specs, views and sparse agentic runs on the in-memory storage, in a clone of a depot whose components are
/// upstreams at <c>memory://depot/&lt;name&gt;</c>: what is mapped, what is loaded, what content is fetched - and that
/// nothing else is - and that run results reach the depot.
/// </summary>
public class ViewTests
{
	private const string DepotUrl = "memory://depot";

	private static readonly Depot Depot = new(DepotUrl);

	/// <summary>
	/// A clone of a depot with lib (include/lib.h, src/lib.c, README.md; tagged v1, then a second commit on main) and app
	/// (src/main.c, docs/guide.md, README.md) referencing lib at lib: registered, but without stores until a view maps them.
	/// </summary>
	private static async Task<MemoryMonorepo> CloneAsync()
	{
		var monorepo = new MemoryMonorepo();
		var lib = monorepo.Upstreams.At(Depot.ComponentUrl("lib"));
		lib.Commit("main", "lib", new Dictionary<string, string?> { ["include/lib.h"] = "int f();", ["src/lib.c"] = "int f() { return 1; }", ["README.md"] = "# lib" });
		await lib.CreateTagAsync("v1", (await lib.BranchTipAsync("main"))!, "v1");
		lib.Commit("main", "lib 2", new Dictionary<string, string?> { ["include/lib.h"] = "int f(); int g();" });
		monorepo.Upstreams.At(Depot.ComponentUrl("app")).Commit("main", "app",
			new Dictionary<string, string?> { ["src/main.c"] = "int main;", ["docs/guide.md"] = "guide", ["README.md"] = "# app" });
		await monorepo.Graph.MetaRepo.CommitAsync(new RegistryChange.Add(new ComponentDefinition("lib", Depot.ComponentUrl("lib"), [])), "lib");
		await monorepo.Graph.MetaRepo.CommitAsync(new RegistryChange.Add(new ComponentDefinition("app", Depot.ComponentUrl("app"), Refs("lib"))), "app");
		return monorepo;
	}

	private static Views ViewsOf(MemoryMonorepo monorepo) => new(monorepo.Graph, monorepo.WorkspaceDir, monorepo.WorkspaceDir, Depot);

	private static string[] FilesOnDisk(string folder) =>
		Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
			.Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/')).Where(path => path != ViewManifest.FileName).Order(StringComparer.Ordinal).ToArray();

	// ----- config specs -----

	[Fact]
	public void Parse_ReadsMapSelectAndLoadRules_IgnoringCommentsAndBlankLines()
	{
		var spec = ConfigSpec.Parse("# parser work\nmap app, tools\n\nselect lib v1   # pinned\nselect * main\nload app/src\nload !app/src/gen\nload lib\n");

		Assert.Equal(["app", "tools"], spec.Maps.Select(map => map.Component));
		Assert.Equal("v1", spec.SelectFor("lib")!.Version);
		Assert.Equal("main", spec.SelectFor("app")!.Version);
		Assert.Equal(["src", "!src/gen"], spec.LoadFor("app"));
		Assert.Equal(["**"], spec.LoadFor("lib"));
		Assert.Empty(spec.LoadFor("tools"));
	}

	[Theory]
	[InlineData("load app/src", "maps no component")]
	[InlineData("map app\nfetch lib", "line 2: unknown rule 'fetch'")]
	[InlineData("map app\nselect lib", "line 2: expected 'select <component-pattern> <version>'")]
	[InlineData("map\n", "line 1: 'map' names no component")]
	[InlineData("map app\nload app/my file", "line 2: expected 'load")]
	[InlineData("map app\nload /src", "line 2: '/src' names no component")]
	public void Parse_RejectsMalformedSpecs_NamingTheLine(string text, string expected)
	{
		var error = Assert.Throws<ViewException>(() => ConfigSpec.Parse(text));
		Assert.Contains(expected, error.Message);
	}

	[Fact]
	public async Task Resolve_MapsTheClosure_FirstMatchingSelectWins_AndTheRestFollowTheirDefaultBranch()
	{
		using var monorepo = await CloneAsync();

		var resolved = await Views.ResolveAsync(ConfigSpec.Parse("map app\nselect lib v1\nselect lib nothing\nload app/src"), monorepo.Graph, Depot);

		// In dependency order: a component before what it references.
		Assert.Equal(["app", "lib"], resolved.Components.Select(component => component.Name));
		var lib = resolved.Components[1];
		Assert.Equal("v1", lib.Version);
		Assert.Equal((await monorepo.Storage["lib"].GetTagAsync("v1"))!.Commit, lib.Commit);
		Assert.Empty(lib.Load);
		Assert.Equal("main", resolved.Components[0].Version);
		Assert.Equal(["src"], resolved.Components[0].Load);
		Assert.Equal($"app@{resolved.Components[0].Commit},lib@{lib.Commit}", resolved.PinnedSelect);
		Assert.Equal(["app/src"], resolved.LoadRules);

		// The second rule for lib never applies; an unknown version is an error where a rule picks it.
		var error = await Assert.ThrowsAsync<ViewException>(() => Views.ResolveAsync(ConfigSpec.Parse("map app\nselect app nothing"), monorepo.Graph, Depot));
		Assert.Contains("line 2: 'nothing' is no branch, tag or commit of component 'app'", error.Message);
	}

	[Fact]
	public async Task Resolve_TakesCommitHashes_AndRejectsUnknownComponentsAndStrayLoadRules()
	{
		using var monorepo = await CloneAsync();
		await Depot.EnsureStoreAsync(monorepo.Storage, "lib");
		var first = (await monorepo.Storage["lib"].GetTagAsync("v1"))!.Commit;

		var resolved = await Views.ResolveAsync(ConfigSpec.Parse($"map lib\nselect lib {first[..10]}"), monorepo.Graph, Depot);
		Assert.Equal(first, resolved.Components.Single().Commit);

		var unknown = await Assert.ThrowsAsync<ViewException>(() => Views.ResolveAsync(ConfigSpec.Parse("map nope"), monorepo.Graph, Depot));
		Assert.Contains("line 1: component 'nope' is not registered", unknown.Message);
		var stray = await Assert.ThrowsAsync<ViewException>(() => Views.ResolveAsync(ConfigSpec.Parse("map lib\nload app/src"), monorepo.Graph, Depot));
		Assert.Contains("line 2: 'load' names no mapped component", stray.Message);
	}

	// ----- views -----

	[Fact]
	public async Task Create_ImportsHistoriesOnly_AndDownloadsAndWritesJustTheLoadedFiles()
	{
		using var monorepo = await CloneAsync();
		var views = ViewsOf(monorepo);

		var view = await views.CreateAsync("parser", "map app\nload app/src\nload lib/include");

		// Both stores came from the depot with their history only; only the two loaded files were fetched.
		Assert.Equal(["app", "lib"], monorepo.Storage.Names);
		Assert.Equal(1, monorepo.Storage["app"].HydratedObjects);
		Assert.Equal(1, monorepo.Storage["lib"].HydratedObjects);
		Assert.Equal(["src/main.c"], FilesOnDisk(view.FolderOf(view.Component("app"))));
		Assert.Equal(["include/lib.h"], FilesOnDisk(view.FolderOf(view.Component("lib"))));

		// lib is nested in app as a link to its own folder, as in a run, and the manifest names the view's mapping.
		Assert.True(DirectoryLinks.IsLink(Path.Combine(view.Folder, "app", "lib")));
		Assert.Equal("int f(); int g();", File.ReadAllText(Path.Combine(view.Folder, "app", "lib", "include", "lib.h")));
		var manifest = ViewManifest.FindEnclosing(Path.Combine(view.Folder, "app", "src"))!;
		Assert.Equal("parser", manifest.Name);
		Assert.Equal(["app", "lib"], manifest.Components.Select(component => component.Name));
		Assert.Equal(["include"], manifest.Component("lib").Load);
		Assert.Equal("parser", Assert.Single(views.List()).Name);
	}

	[Fact]
	public async Task Files_ListEverythingMappedFromTheHistory_WithoutFetchingContent()
	{
		using var monorepo = await CloneAsync();
		var views = ViewsOf(monorepo);
		var view = await views.CreateAsync("parser", "map app\nload app/src");

		var files = await views.FilesAsync(view);

		Assert.Equal(["app/README.md", "app/docs/guide.md", "app/src/main.c", "lib/README.md", "lib/include/lib.h", "lib/src/lib.c"], files.Select(file => file.ViewPath));
		Assert.Equal(["app/src/main.c"], files.Where(file => file.Loaded).Select(file => file.ViewPath));
		Assert.All(files.Where(file => !file.Loaded), file => Assert.Equal(-1, file.Size));
		Assert.Equal(0, monorepo.Storage["lib"].HydratedObjects);
		Assert.Equal(["lib/src/lib.c"], (await views.FilesAsync(view, "lib/src")).Select(file => file.ViewPath));
	}

	[Fact]
	public async Task Load_HydratesAndWritesFiles_ThroughNestedReferences_AndRecordsTheRule()
	{
		using var monorepo = await CloneAsync();
		var views = ViewsOf(monorepo);
		var view = await views.CreateAsync("parser", "map app\nload app/src");

		// From inside app, lib/README.md is the nested lib: it loads from lib.
		var loads = await views.LoadAsync(view, ["lib/README.md", "docs"], Path.Combine(view.Folder, "app"));

		Assert.Equal([("lib", "README.md"), ("app", "docs")], loads.Select(load => (load.Component, load.Pattern)));
		Assert.Equal(["README.md"], FilesOnDisk(Path.Combine(view.Folder, "lib")));
		Assert.Equal(["docs/guide.md", "src/main.c"], FilesOnDisk(Path.Combine(view.Folder, "app")).Where(path => !path.StartsWith("lib/")));
		Assert.Equal(1, monorepo.Storage["lib"].HydratedObjects);
		Assert.Equal(["README.md"], ViewManifest.Read(view.Folder).Component("lib").Load);

		// Loading again loads nothing new; a path matching nothing fails before anything is loaded.
		Assert.Empty((await views.LoadAsync(view, ["app/docs"], view.Folder)).Single().Files);
		var error = await Assert.ThrowsAsync<ViewException>(() => views.LoadAsync(view, ["lib/src", "app/nothing"], view.Folder));
		Assert.Contains("'app/nothing' matches no file", error.Message);
		Assert.False(File.Exists(Path.Combine(view.Folder, "lib", "src", "lib.c")));
	}

	[Fact]
	public async Task Update_MovesComponentsWhoseBranchMoved_ButNotOnesWithLocalChanges()
	{
		using var monorepo = await CloneAsync();
		var views = ViewsOf(monorepo);
		var view = await views.CreateAsync("parser", "map app\nload app/src\nload lib/include");
		var before = view.Component("lib").Commit;
		monorepo.Upstreams.Find(Depot.ComponentUrl("lib"))!.Commit("main", "lib 3", new Dictionary<string, string?> { ["include/lib.h"] = "int h();" });
		monorepo.Upstreams.Find(Depot.ComponentUrl("app"))!.Commit("main", "app 2", new Dictionary<string, string?> { ["src/main.c"] = "int main2;" });
		File.WriteAllText(Path.Combine(view.Folder, "app", "src", "main.c"), "local change");

		var moves = await views.UpdateAsync(view);

		var lib = moves.Single(move => move.Component == "lib");
		Assert.True(lib.Moved);
		Assert.Equal(before, lib.From);
		Assert.Equal("int h();", File.ReadAllText(Path.Combine(view.Folder, "lib", "include", "lib.h")));
		Assert.Equal(["include/lib.h"], FilesOnDisk(Path.Combine(view.Folder, "lib")));
		var app = moves.Single(move => move.Component == "app");
		Assert.False(app.Moved);
		Assert.Contains("uncommitted change", app.Reason);
		Assert.Equal("local change", File.ReadAllText(Path.Combine(view.Folder, "app", "src", "main.c")));
		Assert.True(DirectoryLinks.IsLink(Path.Combine(view.Folder, "app", "lib")));
		Assert.Equal(lib.To, ViewManifest.Read(view.Folder).Component("lib").Commit);
	}

	[Fact]
	public async Task Remove_KeepsAViewWithChanges_UnlessForced()
	{
		using var monorepo = await CloneAsync();
		var views = ViewsOf(monorepo);
		var view = await views.CreateAsync("parser", "map lib\nload lib/README.md");
		File.WriteAllText(Path.Combine(view.Folder, "lib", "new.txt"), "new");

		var error = await Assert.ThrowsAsync<ViewException>(() => views.RemoveAsync("parser", force: false));
		Assert.Contains("lib/new.txt", error.Message);
		Assert.True(Directory.Exists(view.Folder));

		Assert.Equal(["lib/new.txt"], await views.RemoveAsync("parser", force: true));
		Assert.False(Directory.Exists(view.Folder));
		Assert.Empty(views.List());
	}

	[Fact]
	public async Task Create_FailsWithoutLeavingAnything_WhenTheUpstreamIsUnreachable()
	{
		using var monorepo = await CloneAsync();
		await Depot.EnsureStoreAsync(monorepo.Storage, "lib");
		monorepo.Storage["lib"].Upstream = "memory://gone/lib";
		var views = ViewsOf(monorepo);

		var error = await Assert.ThrowsAsync<StorageException>(() => views.CreateAsync("parser", "map lib\nload lib"));

		Assert.Contains("cannot be reached", error.Message);
		Assert.False(Directory.Exists(Path.Combine(monorepo.WorkspaceDir, "view-parser")));
		Assert.Empty(views.List());
	}

	// ----- sparse agentic runs -----

	[Fact]
	public async Task SparseRun_CommitsTheAgentsChanges_KeepsUnloadedFiles_AndWritesThroughToTheDepot()
	{
		using var monorepo = await CloneAsync();
		var resolved = await Views.ResolveAsync(ConfigSpec.Parse("map app\nload app/src"), monorepo.Graph, Depot);
		var runs = monorepo.Graph.AgentRuns(monorepo.Records, new AgentRunOptions
		{
			WorkspaceDir = monorepo.WorkspaceDir,
			RunAgent = async (metadata, _) =>
			{
				// The run folder is a view: the agent loads what it needs, then changes it.
				var view = ViewManifest.FindEnclosing(Path.Combine(metadata.WorkspacePath, "app"))!;
				Assert.Equal(ViewManifest.RunKind, view.Kind);
				Assert.False(File.Exists(Path.Combine(metadata.WorkspacePath, "app", "README.md")));
				await ViewsOf(monorepo).LoadAsync(view, ["app/README.md"], metadata.WorkspacePath);
				File.AppendAllText(Path.Combine(metadata.WorkspacePath, "app", "README.md"), "\nmore");
				File.WriteAllText(Path.Combine(metadata.WorkspacePath, "app", "src", "new.c"), "int added;");
				Directory.CreateDirectory(Path.Combine(metadata.WorkspacePath, "lib", "include"));
				File.WriteAllText(Path.Combine(metadata.WorkspacePath, "lib", "include", "x.h"), "int x;");
				return 0;
			},
			WriteThrough = true
		});

		var outcome = await runs.StartAsync(resolved.PinnedSelect, "agent", Quiet(), load: resolved.LoadRules);

		Assert.True(outcome.Ok, outcome.Message);
		Assert.Equal("app/src", outcome.Metadata.Load);
		var app = outcome.Metadata.Components.Single(component => component.Name == "app");
		var lib = outcome.Metadata.Components.Single(component => component.Name == "lib");

		// Files never loaded are in the result as they were; what the agent wrote, anywhere, is the change.
		var store = monorepo.Storage["app"];
		Assert.Equal(["README.md", "docs/guide.md", "src/main.c", "src/new.c"], await FilesAsync(store, app.ResultCommit!));
		Assert.Equal(["README.md", "include/lib.h", "include/x.h", "src/lib.c"], await FilesAsync(monorepo.Storage["lib"], lib.ResultCommit!));
		Assert.Equal(2, store.HydratedObjects);

		// Written through: the depot has the run branch and the result tag.
		var depotApp = monorepo.Upstreams.Find(Depot.ComponentUrl("app"))!;
		Assert.Equal(app.ResultCommit, await depotApp.BranchTipAsync(RunMetadata.RefBase(outcome.Metadata.RunId)));
		Assert.Equal(app.ResultCommit, (await depotApp.GetTagAsync(app.ResultTag!))!.Commit);
		Assert.Equal(lib.ResultCommit, (await monorepo.Upstreams.Find(Depot.ComponentUrl("lib"))!.GetTagAsync(lib.ResultTag!))!.Commit);
	}

	[Fact]
	public async Task WriteThrough_ThatFails_LeavesTheComponentFailed_AndRetryPushesIt()
	{
		using var monorepo = await CloneAsync();
		await Depot.EnsureStoreAsync(monorepo.Storage, "lib");
		var lib = monorepo.Storage["lib"];
		lib.Upstream = "memory://gone/lib";
		var runs = monorepo.Graph.AgentRuns(monorepo.Records, new AgentRunOptions
		{
			WorkspaceDir = monorepo.WorkspaceDir,
			RunAgent = (metadata, _) =>
			{
				File.WriteAllText(Path.Combine(metadata.WorkspacePath, "lib", "new.c"), "int added;");
				return Task.FromResult<int?>(0);
			},
			WriteThrough = true
		});

		// Nothing to load, so nothing to fetch: only the push needs the upstream.
		var outcome = await runs.StartAsync("lib", "agent", Quiet(), load: ["lib/none"]);

		Assert.False(outcome.Ok);
		var component = outcome.Metadata.Components.Single();
		Assert.Equal(ResultStatus.Failed, component.ResultStatus);
		Assert.Contains("cannot reach its upstream", component.ResultError);

		lib.Upstream = Depot.ComponentUrl("lib");
		var retried = await runs.RetryAsync(outcome.Metadata);

		Assert.True(retried.Ok, retried.Message);
		Assert.Equal(component.ResultCommit, (await monorepo.Upstreams.Find(Depot.ComponentUrl("lib"))!.GetTagAsync(component.ResultTag!))!.Commit);
	}

	[Fact]
	public async Task LoadRules_MustNameAComponentOfTheRun()
	{
		using var monorepo = await CloneAsync();
		await Depot.EnsureStoreAsync(monorepo.Storage, "lib");

		var error = await Assert.ThrowsAsync<AgentException>(() => monorepo.Runs(_ => 0).StartAsync("lib", "agent", Quiet(), load: ["app/src"]));

		Assert.Contains("-load 'app/src' names no component of the run", error.Message);
		Assert.False(Directory.Exists(monorepo.WorkspaceDir) && Directory.EnumerateDirectories(monorepo.WorkspaceDir).Any());
	}

	// ----- the storage contract -----

	[Fact]
	public async Task HistoryOnlyStore_ListsWithoutContent_HydratesOnRead_AndFetchesAndPushesItsUpstream()
	{
		var storage = new MemoryComponentStorage();
		var upstream = storage.Upstreams.At("memory://depot/lib");
		var tip = upstream.Commit("main", "lib", new Dictionary<string, string?> { ["a.txt"] = "a", ["b.txt"] = "b" });

		var store = (MemoryComponentStore)await storage.ImportHistoryAsync("lib", "memory://depot/lib");

		Assert.Equal(["a.txt", "b.txt"], await store.MissingContentAsync(tip));
		Assert.All(await store.ListFilesAsync(tip), file => Assert.Equal(-1, file.Size));
		Assert.Equal("a"u8.ToArray(), await store.ReadFileAsync(tip, "a.txt"));
		Assert.Equal(["b.txt"], await store.MissingContentAsync(tip));
		await store.HydrateAsync(tip, ["b.txt"]);
		Assert.Empty(await store.MissingContentAsync(tip));
		Assert.Equal(2, store.HydratedObjects);

		var next = upstream.Commit("main", "lib 2", new Dictionary<string, string?> { ["c.txt"] = "c" });
		await store.FetchUpstreamAsync();
		Assert.Equal(next, await store.BranchTipAsync("main"));
		Assert.Equal(["c.txt"], await store.MissingContentAsync(next));

		var mine = store.Commit("topic", "mine", new Dictionary<string, string?> { ["d.txt"] = "d" });
		await store.CreateTagAsync("t1", mine, "t1");
		await store.PushUpstreamAsync("topic", ["t1"]);
		Assert.Equal(mine, await upstream.BranchTipAsync("topic"));
		Assert.Equal(mine, (await upstream.GetTagAsync("t1"))!.Commit);
	}
}
