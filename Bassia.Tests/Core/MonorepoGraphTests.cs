namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Storage;
using static MemoryMonorepo;

/// <summary>Creating, importing, removing, connecting, tagging and forking components through the graph API, on in-memory storage.</summary>
public class MonorepoGraphTests
{
	[Fact]
	public async Task Create_MakesAStoreWithAnEmptyFirstCommit_AndRegistersIt()
	{
		using var monorepo = new MemoryMonorepo();

		var created = await monorepo.Graph.CreateAsync("docs", [], defaultBranch: "trunk", url: "https://example.com/docs.git");

		Assert.Equal("docs", created.Component.Name);
		Assert.Equal("r1", created.Revision);
		var store = monorepo.Storage["docs"];
		Assert.Equal("trunk", await store.DefaultBranchAsync());
		var tip = await store.BranchTipAsync("trunk");
		Assert.NotNull(tip);
		Assert.Empty(await FilesAsync(store, tip));
		Assert.Equal("https://example.com/docs.git", await store.UpstreamAsync());
		Assert.Equal("Create component 'docs'", monorepo.MetaStore.Revisions[0].Message);
	}

	[Fact]
	public async Task Import_CopiesTheUpstreamHistory_AndRegistersWithReferences()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib");
		var url = monorepo.Upstream("app", ("src/main.c", "int main;"));

		var added = await monorepo.Graph.ImportAsync("app", url, Refs("lib:vendor/lib"));

		Assert.Equal(url, added.Component.Url);
		Assert.Equal(new ComponentReference("lib", "vendor/lib"), Assert.Single(monorepo.Graph.Registry.Require("app").References));
		var store = monorepo.Storage["app"];
		Assert.Equal(["src/main.c"], await FilesAsync(store, (await store.BranchTipAsync("main"))!));
		Assert.Equal(url, await store.UpstreamAsync());
	}

	[Fact]
	public async Task Import_ProblemsAreFoundBeforeAnythingIsWritten()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib");
		var url = monorepo.Upstream("app");

		await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.ImportAsync("lib", url, []));
		await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.ImportAsync("app", url, Refs("missing")));
		await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.ImportAsync("bad name", url, []));
		await Assert.ThrowsAsync<StorageException>(() => monorepo.Graph.ImportAsync("app", "https://example.com/nowhere.git", []));

		Assert.Equal(["lib"], monorepo.Storage.Names);
		Assert.Equal(["lib"], monorepo.Graph.Registry.Components.Select(component => component.Name));
	}

	[Fact]
	public async Task Import_OverAnUnregisteredStore_IsRefused()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.Storage.CreateAsync("app", "main", "", "stray");

		var error = await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.ImportAsync("app", monorepo.Upstream("app"), []));

		Assert.Contains("already exists; choose another name", error.Message);
	}

	[Fact]
	public async Task Remove_KeepsTheStoreUnlessPurged_AndRefusesAReferencedComponent()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib");
		await monorepo.AddAsync("app", Refs("lib"));
		await monorepo.AddAsync("tool");

		await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.RemoveAsync("lib", purge: true));
		var kept = await monorepo.Graph.RemoveAsync("app", purge: false);
		var purged = await monorepo.Graph.RemoveAsync("tool", purge: true);

		Assert.False(kept.Purged);
		Assert.True(purged.Purged);
		Assert.Equal(["app", "lib"], monorepo.Storage.Names);
		Assert.Equal(["lib"], monorepo.Graph.Registry.Components.Select(component => component.Name));
	}

	[Fact]
	public async Task SetReferences_RecordsARevision_AndRejectsACycle()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib");
		await monorepo.AddAsync("app");
		var revisions = monorepo.MetaStore.Revisions.Count;

		var changed = await monorepo.Graph.SetReferencesAsync("app", Refs("lib:third_party/lib"));
		await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.SetReferencesAsync("lib", Refs("app")));

		Assert.Equal("Component 'app' references lib:third_party/lib", monorepo.MetaStore.Revisions[^1].Message);
		Assert.Equal(revisions + 1, monorepo.MetaStore.Revisions.Count);
		Assert.Equal("third_party/lib", Assert.Single(changed.Component.References).Path);
		Assert.Empty(monorepo.Graph.Registry.Require("lib").References);
	}

	[Fact]
	public async Task Tag_DefaultsToTheMainTip_AndResolvesRevisionNames()
	{
		using var monorepo = new MemoryMonorepo();
		var store = await monorepo.AddAsync("lib");
		var first = (await store.BranchTipAsync("main"))!;
		var second = store.Commit("main", "Second", new Dictionary<string, string?> { ["b.txt"] = "b" });

		var head = await monorepo.Graph.TagAsync("lib", "v2");
		var byParent = await monorepo.Graph.TagAsync("lib", "v1-again", "HEAD~1", "the first commit");
		var byPrefix = await monorepo.Graph.TagAsync("lib", "v2-again", second[..8]);

		Assert.Equal(second, head.Commit);
		Assert.Equal("v2: baseline of 'lib'", head.Message);
		Assert.Equal(first, byParent.Commit);
		Assert.Equal(second, byPrefix.Commit);
		var tag = (await store.GetTagAsync("v1-again"))!;
		Assert.True(tag.Annotated);
		Assert.Equal("the first commit", tag.Subject);
	}

	[Fact]
	public async Task Tag_UnknownTargetOrExistingTag_Fails()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib");

		Assert.Contains("does not name a commit", (await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.TagAsync("lib", "x", "nope"))).Message);
		await Assert.ThrowsAsync<StorageException>(() => monorepo.Graph.TagAsync("lib", "v1"));
	}

	[Fact]
	public async Task Fork_TakesTheMainHistoryUpToTheForkPoint_WithItsTagsButNotRunTags()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib");
		var store = await monorepo.AddAsync("app", Refs("lib"));
		var forkPoint = store.Commit("main", "Second", new Dictionary<string, string?> { ["second.txt"] = "2" });
		await store.CreateTagAsync("v2", forkPoint, "v2");
		await store.CreateTagAsync("agent-run/brave-otter-3f2a91/0", forkPoint, "a run result");
		var later = store.Commit("main", "Third", new Dictionary<string, string?> { ["third.txt"] = "3" });
		await store.CreateTagAsync("v3", later, "v3");

		var fork = await monorepo.Graph.ForkAsync("app", "app-next", "v2");

		Assert.Equal(forkPoint, fork.Commit);
		Assert.Equal("main", fork.Branch);
		Assert.Equal(["v1", "v2"], fork.Tags.Order());
		var forked = monorepo.Storage["app-next"];
		Assert.Equal(forkPoint, await forked.BranchTipAsync("main"));
		Assert.Null(await forked.GetCommitAsync(later));
		Assert.Null(await forked.GetTagAsync("agent-run/brave-otter-3f2a91/0"));
		var definition = monorepo.Graph.Registry.Require("app-next");
		Assert.Equal(("app", forkPoint), (definition.ForkOf, definition.ForkCommit));
		Assert.Equal("lib", Assert.Single(definition.References).Name);

		// Independent: a commit in the parent does not reach the fork.
		store.Commit("main", "Fourth", new Dictionary<string, string?> { ["fourth.txt"] = "4" });
		Assert.Equal(forkPoint, await forked.BranchTipAsync("main"));
	}

	[Fact]
	public async Task Fork_OffTheMainBranch_OrUnderATakenName_Fails()
	{
		using var monorepo = new MemoryMonorepo();
		var store = await monorepo.AddAsync("app");
		var side = store.Commit("side", "Off main", new Dictionary<string, string?> { ["side.txt"] = "s" });

		Assert.Contains("is not on the main branch", (await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.ForkAsync("app", "app-side", side))).Message);
		Assert.Contains("already registered", (await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.ForkAsync("app", "app"))).Message);
		Assert.Contains("not a valid component name", (await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.ForkAsync("app", ".fork"))).Message);
		Assert.Equal(["app"], monorepo.Storage.Names);
	}

	[Fact]
	public async Task State_ReportsBranchesAndTags_AndAComponentWithoutStore()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib");
		await monorepo.Storage.DeleteAsync("lib");
		await monorepo.AddAsync("app");

		var app = await monorepo.Graph.StateAsync("app");
		var lib = await monorepo.Graph.StateAsync("lib");

		Assert.True(app.HasStore);
		Assert.Equal("main", app.DefaultBranch);
		Assert.Equal("main", Assert.Single(app.Branches).Name);
		Assert.Equal("v1", Assert.Single(app.Tags).Name);
		Assert.False(lib.HasStore);
		Assert.Throws<MonorepoException>(() => monorepo.Graph.Store("lib"));
	}
}
