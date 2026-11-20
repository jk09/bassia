namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: two people or agents change the same file. What happens follows the per-path conflict policy in the
/// versioned <c>.bassia.toml</c>: <c>replay</c> (the default) refuses the second landing as needs-replay;
/// <c>textmerge</c> merges similar line edits; <c>conflict</c> stores conflicts with markers to resolve by hand. Sync
/// rebases a workspace onto the newest mainline, and the conflict overview shows who will collide.
/// </summary>
[Trait("Scenario", "Merge")]
public sealed class MergeScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	private const string Lines = "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10\n";

	private static async Task SeedAsync(ScenarioClient client, string? policy)
	{
		var files = new Dictionary<string, string?> { ["notes.txt"] = Lines, ["other.txt"] = "other\n" };
		if (policy is not null)
		{
			files[".bassia.toml"] = $"[conflicts]\ndefault = \"{policy}\"\n";
		}

		await client.LandFilesAsync("seed", "seed", files);
	}

	[ScenarioFact]
	public async Task ByDefault_BothRewritingAPath_RefusesTheSecondLanding_AsNeedsReplay()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice, null);
		await alice.OkAsync("workspace", "create", "first");
		await alice.OkAsync("workspace", "create", "second");
		alice.Write("first", "notes.txt", Lines.Replace("line 2", "line two"));
		alice.Write("second", "notes.txt", Lines.Replace("line 9", "line nine"));

		await alice.OkAsync("workspace", "land", "first");
		var check = await alice.OkAsync("workspace", "check", "second");
		Assert.Equal("needs-replay", check.Str("outcome"));
		Assert.False(check.Bool("can_land"));
		var conflict = Assert.Single(check.Tables("conflict"));
		Assert.Equal("notes.txt", conflict.Str("path"));
		Assert.Equal("replay", conflict.Str("policy"));

		var refused = await alice.RunAsync("workspace", "land", "second");
		Assert.Equal("needs-replay", refused.Result.Str("outcome"));
		Assert.Equal("needs-replay", (await alice.OkAsync("workspace", "show", "second")).Table("last_land").Str("outcome"));
		Assert.Equal(Lines.Replace("line 2", "line two"), await alice.CatAsync("notes.txt"));
	}

	[ScenarioFact]
	public async Task TextMerge_MergesEditsToDifferentLines_AndLands()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice, "textmerge");
		await alice.OkAsync("workspace", "create", "first");
		await alice.OkAsync("workspace", "create", "second");
		alice.Write("first", "notes.txt", Lines.Replace("line 2", "line two"));
		alice.Write("second", "notes.txt", Lines.Replace("line 9", "line nine"));

		await alice.OkAsync("workspace", "land", "first");
		var landed = await alice.OkAsync("workspace", "land", "second");
		Assert.Equal("landed", landed.Str("outcome"));
		Assert.Contains(landed.Strings("merged"), merged => merged.StartsWith("notes.txt", StringComparison.Ordinal));
		Assert.Equal(Lines.Replace("line 2", "line two").Replace("line 9", "line nine"), await alice.CatAsync("notes.txt"));
	}

	[ScenarioFact]
	public async Task TextMerge_EditsToTheSameLine_FallBackToReplay()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice, "textmerge");
		await alice.OkAsync("workspace", "create", "first");
		await alice.OkAsync("workspace", "create", "second");
		alice.Write("first", "notes.txt", Lines.Replace("line 5", "line five (alice)"));
		alice.Write("second", "notes.txt", Lines.Replace("line 5", "line five (bob)"));

		await alice.OkAsync("workspace", "land", "first");
		Assert.Equal("needs-replay", (await alice.OkAsync("workspace", "check", "second")).Str("outcome"));
	}

	[ScenarioFact]
	public async Task PolicyRules_ChoosePerPath_FirstMatchingRuleWins()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("seed", "seed", new Dictionary<string, string?>
		{
			[".bassia.toml"] = "[conflicts]\ndefault = \"replay\"\n\n[[conflicts.rule]]\npaths = [\"docs/**\"]\npolicy = \"textmerge\"\n",
			["docs/guide.md"] = Lines,
			["src/code.txt"] = Lines
		});
		await alice.OkAsync("workspace", "create", "first");
		await alice.OkAsync("workspace", "create", "second");
		alice.Write("first", "docs/guide.md", Lines.Replace("line 2", "line two"));
		alice.Write("first", "src/code.txt", Lines.Replace("line 2", "line two"));
		alice.Write("second", "docs/guide.md", Lines.Replace("line 9", "line nine"));
		alice.Write("second", "src/code.txt", Lines.Replace("line 9", "line nine"));
		await alice.OkAsync("workspace", "land", "first");

		var check = await alice.OkAsync("workspace", "check", "second");
		Assert.Equal("needs-replay", check.Str("outcome"));
		Assert.Equal(["src/code.txt"], check.Tables("conflict").Select(conflict => conflict.Str("path")));
	}

	[ScenarioFact]
	public async Task Sync_RebasesOntoTheNewestMainline_TakingDisjointChangesForFree()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice, null);
		await alice.OkAsync("workspace", "create", "mine");
		alice.Write("mine", "mine.txt", "mine\n");
		await alice.LandFilesAsync("theirs", "theirs", new Dictionary<string, string?> { ["other.txt"] = "other changed\n" });

		var synced = await alice.OkAsync("workspace", "sync", "mine");
		Assert.Equal(2L, synced.Long("to_seq"));
		Assert.Empty(synced.Strings("conflicts"));
		Assert.Equal("other changed\n", alice.Read("mine", "other.txt"));
		Assert.Equal("mine\n", alice.Read("mine", "mine.txt"));
		Assert.Equal(2L, (await alice.OkAsync("workspace", "show", "mine")).Long("base_seq"));
	}

	[ScenarioFact]
	public async Task ConflictPolicy_SyncWritesMarkers_MainlineRefusesThem_EditingResolves()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice, "conflict");
		await alice.OkAsync("workspace", "create", "mine");
		alice.Write("mine", "notes.txt", Lines.Replace("line 5", "line five (mine)"));
		await alice.LandFilesAsync("theirs", "theirs", new Dictionary<string, string?> { ["notes.txt"] = Lines.Replace("line 5", "line five (theirs)") });

		var synced = await alice.OkAsync("workspace", "sync", "mine");
		Assert.Equal(["notes.txt"], synced.Strings("conflicts"));
		var marked = alice.Read("mine", "notes.txt");
		Assert.Contains("<<<<<<<", marked);
		Assert.Contains("line five (mine)", marked);
		Assert.Contains("line five (theirs)", marked);
		Assert.Contains(">>>>>>>", marked);

		var status = await alice.OkAsync("workspace", "status", "mine");
		Assert.Contains("notes.txt", status.Strings("conflicts"));
		var refused = await alice.RunAsync("workspace", "land", "mine");
		Assert.Equal("conflict", refused.Result.Str("outcome"));

		// Editing the file resolves the conflict.
		alice.Write("mine", "notes.txt", Lines.Replace("line 5", "line five (both)"));
		Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "mine")).Str("outcome"));
		Assert.Equal(Lines.Replace("line 5", "line five (both)"), await alice.CatAsync("notes.txt"));
	}

	[ScenarioFact]
	public async Task SyncThenResolve_LetsANeedsReplayWorkspaceLand_WithoutAReplay()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice, null);
		await alice.OkAsync("workspace", "create", "late");
		alice.Write("late", "notes.txt", Lines.Replace("line 3", "line three (late)"));
		await alice.LandFilesAsync("early", "early", new Dictionary<string, string?> { ["notes.txt"] = Lines.Replace("line 3", "line three (early)") });
		Assert.Equal("needs-replay", (await alice.OkAsync("workspace", "check", "late")).Str("outcome"));

		await alice.OkAsync("workspace", "sync", "late");
		alice.Write("late", "notes.txt", Lines.Replace("line 3", "line three (early, then late)"));
		Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "late")).Str("outcome"));
	}

	[ScenarioFact]
	public async Task TheConflictOverview_ShowsWhatEachOpenWorkspaceWouldDo_AndWhoOverlaps()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice, null);
		await alice.OkAsync("workspace", "create", "a");
		await alice.OkAsync("workspace", "create", "b");
		await alice.OkAsync("workspace", "create", "c");
		alice.Write("a", "notes.txt", "a\n");
		alice.Write("b", "notes.txt", "b\n");
		alice.Write("c", "c.txt", "c\n");
		foreach (var name in new[] { "a", "b", "c" })
		{
			await alice.OkAsync("workspace", "snapshot", name);
		}

		var overview = await alice.OkAsync("workspace", "conflicts");
		Assert.Equal(await alice.MainSeqAsync(), overview.Long("main_seq"));
		var workspaces = overview.Tables("workspace").ToDictionary(workspace => workspace.Str("name"));
		Assert.Equal(["a", "b", "c"], workspaces.Keys.Order());
		var overlap = Assert.Single(overview.Tables("overlap"));
		Assert.Equal("notes.txt", overlap.Str("path"));
		Assert.Equal(2, overlap.Strings("workspaces").Count);

		// After 'a' lands, 'b' would need a replay and 'c' still lands.
		await alice.OkAsync("workspace", "land", "a");
		overview = await alice.OkAsync("workspace", "conflicts");
		workspaces = overview.Tables("workspace").ToDictionary(workspace => workspace.Str("name"));
		Assert.Equal("needs-replay", workspaces["b"].Str("outcome"));
		Assert.Equal("would-land", workspaces["c"].Str("outcome"));
		Assert.Equal(1L, overview.Long("can_land"));
		Assert.Equal(1L, overview.Long("blocked"));
	}
}

/// <summary>
/// Scenario: code merged by a semantic (language-aware) merge driver - weave's <c>weave-driver</c> - which lands only
/// after the server's validation hook passes. Needs <c>weave-driver</c> on PATH.
/// </summary>
[Trait("Scenario", "Merge")]
public sealed class SemanticMergeScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	protected override IReadOnlyList<string> ServerArguments =>
		["-semantic-driver", "weave-driver", "-validate", OperatingSystem.IsWindows() ? "exit 0" : "true"];

	private const string Code = """
		public static class Calc
		{
			public static int Add(int a, int b)
			{
				return a + b;
			}

			public static int Sub(int a, int b)
			{
				return a - b;
			}
		}

		""";

	[ScenarioFact(Needs.Semantic)]
	public async Task EditsToDifferentMethods_AreMergedByTheDriver_AndLandAfterValidation()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("seed", "seed", new Dictionary<string, string?>
		{
			[".bassia.toml"] = "[conflicts]\ndefault = \"replay\"\n\n[[conflicts.rule]]\npaths = [\"**/*.cs\"]\npolicy = \"semantic\"\n",
			["src/Calc.cs"] = Code
		});
		await alice.OkAsync("workspace", "create", "add");
		await alice.OkAsync("workspace", "create", "sub");
		alice.Write("add", "src/Calc.cs", Code.Replace("return a + b;", "return checked(a + b);"));
		alice.Write("sub", "src/Calc.cs", Code.Replace("return a - b;", "return checked(a - b);"));

		await alice.OkAsync("workspace", "land", "add");
		var landed = await alice.OkAsync("workspace", "land", "sub");
		Assert.Equal("landed", landed.Str("outcome"));
		Assert.NotEmpty(landed.Strings("merged"));
		var merged = await alice.CatAsync("src/Calc.cs");
		Assert.Contains("checked(a + b)", merged);
		Assert.Contains("checked(a - b)", merged);
	}
}
