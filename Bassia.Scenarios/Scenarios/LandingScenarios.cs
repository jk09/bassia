namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: work lands on mainline, the single linear trunk numbered by seq - check first, land, keep working, and
/// read mainline back (log, show, ls, cat, changed), or put an earlier seq back.
/// </summary>
[Trait("Scenario", "Landing")]
public sealed class LandingScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	[ScenarioFact]
	public async Task CheckThenLand_AddsTheNextSeq_WithTheWorkspacesChangeIdAndIntent()
	{
		var alice = await CloneAsync();
		var created = await alice.OkAsync("workspace", "create", "first", "-intent", "add a readme");
		alice.Write("first", "README.md", "hello\n");

		var check = await alice.OkAsync("workspace", "check", "first");
		Assert.Equal("would-land", check.Str("outcome"));
		Assert.True(check.Bool("can_land"));
		Assert.Equal(0L, await alice.MainSeqAsync());

		var landed = await alice.OkAsync("workspace", "land", "first");
		Assert.Equal("landed", landed.Str("outcome"));
		Assert.Equal(1L, landed.Long("seq"));
		Assert.Equal(1L, await alice.MainSeqAsync());

		var log = (await alice.OkAsync("mainline", "log")).Tables("landing");
		var landing = log.Single(entry => entry.Long("seq") == 1);
		Assert.Equal("add a readme", landing.Str("intent"));
		Assert.Equal(created.Str("change_id"), landing.Str("change_id"));
		Assert.Equal("alice", landing.Str("author"));
		Assert.Equal("hello\n", await alice.CatAsync("README.md"));

		// A landed workspace is no longer open.
		Assert.DoesNotContain((await alice.OkAsync("workspace", "list")).Tables("workspace"), w => w.Str("name") == "first");
		Assert.Equal("landed", (await alice.OkAsync("workspace", "show", "first")).Str("status"));
	}

	[ScenarioFact]
	public async Task MainlineIsLinear_EachLandingIsTheNextSeq()
	{
		var alice = await CloneAsync();
		for (var i = 1; i <= 5; i++)
		{
			Assert.Equal(i, await alice.LandFilesAsync($"step-{i}", $"step {i}", new Dictionary<string, string?> { [$"steps/{i}.txt"] = $"{i}\n" }));
		}

		var seqs = (await alice.OkAsync("mainline", "log")).Tables("landing").Select(landing => landing.Long("seq")).ToList();
		Assert.Equal(seqs.OrderDescending(), seqs);
		Assert.Equal(Enumerable.Range(0, 6).Select(i => (long)i).Reverse(), seqs);
		Assert.Equal(2, (await alice.OkAsync("mainline", "log", "-limit", "2")).Tables("landing").Count);
		Assert.Single((await alice.OkAsync("mainline", "log", "-path", "steps/3.txt")).Tables("landing"));
	}

	[ScenarioFact]
	public async Task AWorkspaceWithoutChanges_LandsNothing()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "idle");
		var check = await alice.OkAsync("workspace", "check", "idle");
		Assert.Equal("nothing", check.Str("outcome"));
		var land = await alice.RunAsync("workspace", "land", "idle");
		Assert.Equal("nothing", land.Result.Str("outcome"));
		Assert.Equal(0L, await alice.MainSeqAsync());
	}

	[ScenarioFact]
	public async Task LandKeep_LeavesTheWorkspaceOpen_OnTheNewHead_WithAFreshChange()
	{
		var alice = await CloneAsync();
		var created = await alice.OkAsync("workspace", "create", "long-lived");
		alice.Write("long-lived", "a.txt", "1\n");
		var first = await alice.OkAsync("workspace", "land", "long-lived", "-keep");
		Assert.Equal("landed", first.Str("outcome"));

		var shown = await alice.OkAsync("workspace", "show", "long-lived");
		Assert.Equal("open", shown.Str("status"));
		Assert.Equal(first.Long("seq"), shown.Long("base_seq"));
		Assert.NotEqual(created.Str("change_id"), shown.Str("change_id"));

		alice.Write("long-lived", "a.txt", "2\n");
		var second = await alice.OkAsync("workspace", "land", "long-lived", "-keep");
		Assert.Equal(first.Long("seq") + 1, second.Long("seq"));
		Assert.Equal("2\n", await alice.CatAsync("a.txt"));
	}

	[ScenarioFact]
	public async Task DisjointChanges_FromTwoWorkspaces_BothLand_WithoutAnyMerge()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("seed", "seed", new Dictionary<string, string?> { ["api/a.txt"] = "a\n", ["web/w.txt"] = "w\n" });
		await alice.OkAsync("workspace", "create", "api-work");
		await alice.OkAsync("workspace", "create", "web-work");
		alice.Write("api-work", "api/a.txt", "a2\n");
		alice.Write("web-work", "web/w.txt", "w2\n");

		Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "api-work")).Str("outcome"));
		var second = await alice.OkAsync("workspace", "land", "web-work");
		Assert.Equal("landed", second.Str("outcome"));
		Assert.Empty(second.Strings("merged"));
		Assert.Equal("a2\n", await alice.CatAsync("api/a.txt"));
		Assert.Equal("w2\n", await alice.CatAsync("web/w.txt"));
	}

	[ScenarioFact]
	public async Task Mainline_CanBeRead_AtAnySeq()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("v1", "v1", new Dictionary<string, string?> { ["doc/a.md"] = "# A\none\nend\n", ["doc/b.md"] = "b\n" });
		await alice.LandFilesAsync("v2", "v2", new Dictionary<string, string?> { ["doc/a.md"] = "# A\ntwo\nend\n", ["doc/b.md"] = null, ["top.txt"] = "top\n" });

		Assert.Equal("# A\none\nend\n", await alice.CatAsync("doc/a.md", 1));
		Assert.Equal("# A\ntwo\nend\n", await alice.CatAsync("doc/a.md"));
		await alice.FailAsync("mainline", "cat", "doc/b.md");

		var root = await alice.OkAsync("mainline", "ls");
		Assert.Equal(["doc", "top.txt"], root.Tables("entry").Select(entry => entry.Str("name")).Order(StringComparer.Ordinal));
		var docAtOne = await alice.OkAsync("mainline", "ls", "doc", "-seq", "1");
		Assert.Equal(["a.md", "b.md"], docAtOne.Tables("entry").Select(entry => entry.Str("name")).Order(StringComparer.Ordinal));

		var shown = await alice.OkAsync("mainline", "show", "-seq", "2", "-patch");
		var changes = shown.Tables("change").ToDictionary(change => change.Str("path"), change => change.Str("status"));
		Assert.Equal("modified", changes["doc/a.md"]);
		Assert.Equal("deleted", changes["doc/b.md"]);
		Assert.Equal("added", changes["top.txt"]);
		Assert.Contains("+two", shown.Str("patch"));

		var changed = await alice.OkAsync("mainline", "changed", "-since", "1");
		Assert.Equal(["doc/a.md", "doc/b.md", "top.txt"], changed.Tables("path").Select(path => path.Str("path")).Order(StringComparer.Ordinal));
		var underDoc = await alice.OkAsync("mainline", "changed", "-since", "1", "-prefix", "doc");
		Assert.DoesNotContain(underDoc.Tables("path"), path => path.Str("path") == "top.txt");
	}

	[ScenarioFact]
	public async Task MainlineRestore_PutsAnEarlierTreeBack_AsANewLanding()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("good", "good state", new Dictionary<string, string?> { ["config.txt"] = "good\n" });
		await alice.LandFilesAsync("bad", "bad change", new Dictionary<string, string?> { ["config.txt"] = "bad\n", ["junk.txt"] = "junk\n" });

		var restored = await alice.OkAsync("mainline", "restore", "-seq", "1");
		Assert.Equal(3L, await alice.MainSeqAsync());
		Assert.Equal("good\n", await alice.CatAsync("config.txt"));
		await alice.FailAsync("mainline", "cat", "junk.txt");
		// History stays linear: the bad landing is still in the log.
		Assert.Contains((await alice.OkAsync("mainline", "log")).Tables("landing"), landing => landing.Str("intent") == "bad change");
		Assert.True(restored.Bool("ok"));
	}

	[ScenarioFact]
	public async Task AStrictLanding_IsRefused_WhenItsReadSetChangedOnMainline()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("seed", "seed", new Dictionary<string, string?> { ["lib/api.txt"] = "v1\n", ["app/use.txt"] = "uses v1\n" });
		await alice.OkAsync("workspace", "create", "relies-on-api");
		alice.Write("relies-on-api", "app/use.txt", "uses v1 more\n");
		await alice.LandFilesAsync("api-change", "change the api", new Dictionary<string, string?> { ["lib/api.txt"] = "v2\n" });

		// Without -strict the disjoint change lands; with -strict and the read set, it is refused.
		var strict = await alice.OkAsync("workspace", "check", "relies-on-api", "-strict", "-reads", "lib");
		Assert.False(strict.Bool("can_land"));
		Assert.NotEqual("would-land", strict.Str("outcome"));
		Assert.Equal("would-land", (await alice.OkAsync("workspace", "check", "relies-on-api")).Str("outcome"));
		Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "relies-on-api", "-strict", "-reads", "docs")).Str("outcome"));
	}

	[ScenarioFact]
	public async Task LargeAndBinaryFiles_RoundTripThroughTheBlobStore()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "big");
		var random = new Random(42);
		var large = new byte[3 * 1024 * 1024 + 123];
		random.NextBytes(large);
		var binary = new byte[] { 0, 1, 2, 255, 254, 0, 10, 13 };
		Directory.CreateDirectory(alice.PathIn("big", "data"));
		File.WriteAllBytes(alice.PathIn("big", "data/large.bin"), large);
		File.WriteAllBytes(alice.PathIn("big", "data/small.bin"), binary);
		Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "big")).Str("outcome"));

		// Another client downloads them from the server's (memory) blob store.
		var bob = await CloneAsync("bob");
		await bob.OkAsync("workspace", "create", "reader");
		Assert.Equal(large, File.ReadAllBytes(bob.PathIn("reader", "data/large.bin")));
		Assert.Equal(binary, File.ReadAllBytes(bob.PathIn("reader", "data/small.bin")));
	}
}
