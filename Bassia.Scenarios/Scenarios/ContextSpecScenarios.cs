namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: a workspace holds only part of a large monorepo - its context-spec (rules, or a profile from
/// <c>.bassia.toml</c>) selects the paths it contains; changes outside it are refused; landing carries the rest along.
/// </summary>
[Trait("Scenario", "ContextSpecs")]
public sealed class ContextSpecScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	private static async Task SeedAsync(ScenarioClient client) => await client.LandFilesAsync("seed", "seed", new Dictionary<string, string?>
	{
		["README.md"] = "readme\n",
		["api/server.cs"] = "server\n",
		["api/testdata/big.json"] = "{}\n",
		["web/app.js"] = "app\n",
		["libs/shared/util.cs"] = "util\n",
		[".bassia.toml"] = "[context.profiles]\napi = [\"*\", \"api/\", \"libs/shared/\", \"!api/testdata/\"]\n"
	});

	[ScenarioFact]
	public async Task AContextSpec_SelectsWhatTheFolderHolds()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice);

		var created = await alice.OkAsync("workspace", "create", "api-fix", "-context", "*,api/,!api/testdata/");
		Assert.Equal(["*", "api/", "!api/testdata/"], created.Strings("context"));
		Assert.True(alice.Exists("api-fix", "README.md"));
		Assert.True(alice.Exists("api-fix", "api/server.cs"));
		Assert.False(alice.Exists("api-fix", "api/testdata/big.json"));
		Assert.False(alice.Exists("api-fix", "web/app.js"));
		// A directory outside the context shows as an empty folder.
		Assert.True(Directory.Exists(alice.PathIn("api-fix", "web")));
		Assert.True(alice.Exists("api-fix", ".bassia.toml"));
	}

	[ScenarioFact]
	public async Task AProfile_ExpandsToItsRules()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice);
		var created = await alice.OkAsync("workspace", "create", "by-profile", "-context", "@api");
		Assert.Equal(["*", "api/", "libs/shared/", "!api/testdata/"], created.Strings("context"));
		Assert.True(alice.Exists("by-profile", "libs/shared/util.cs"));
		Assert.False(alice.Exists("by-profile", "web/app.js"));
	}

	[ScenarioFact]
	public async Task ChangesOutsideTheContext_AreRefused_ByEveryCommandThatSnapshots()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice);
		await alice.OkAsync("workspace", "create", "narrow", "-context", "api/");
		alice.Write("narrow", "web/sneaky.js", "x\n");

		Assert.Contains("web/sneaky.js", (await alice.FailAsync("workspace", "status", "narrow")).Str("error"));
		Assert.Contains("web/sneaky.js", (await alice.FailAsync("workspace", "land", "narrow")).Str("error"));

		// Widening the context accepts it.
		await alice.OkAsync("workspace", "context", "narrow", "-add", "web/");
		Assert.True(alice.Exists("narrow", "web/app.js"));
		Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "narrow")).Str("outcome"));
		Assert.Equal("x\n", await alice.CatAsync("web/sneaky.js"));
	}

	[ScenarioFact]
	public async Task LandingANarrowWorkspace_KeepsEverythingOutsideItsContext()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice);
		await alice.OkAsync("workspace", "create", "web-only", "-context", "web/");
		alice.Write("web-only", "web/app.js", "app v2\n");
		Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "web-only")).Str("outcome"));

		Assert.Equal("app v2\n", await alice.CatAsync("web/app.js"));
		Assert.Equal("server\n", await alice.CatAsync("api/server.cs"));
		Assert.Equal("{}\n", await alice.CatAsync("api/testdata/big.json"));
	}

	[ScenarioFact]
	public async Task ChangingTheContext_IsAnOperation_ThatUndoReverts()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice);
		await alice.OkAsync("workspace", "create", "grow", "-context", "api/");
		var shown = await alice.OkAsync("workspace", "context", "grow");
		Assert.True(shown.Long("paths_in_context") >= 2);

		await alice.OkAsync("workspace", "context", "grow", "-set", "web/");
		Assert.True(alice.Exists("grow", "web/app.js"));
		Assert.False(alice.Exists("grow", "api/server.cs"));

		await alice.OkAsync("workspace", "undo", "grow");
		Assert.Equal(["api/"], (await alice.OkAsync("workspace", "context", "grow")).Strings("context"));
		Assert.True(alice.Exists("grow", "api/server.cs"));
	}

	[ScenarioFact]
	public async Task AContextThatDropsAChangedPath_IsRefused()
	{
		var alice = await CloneAsync();
		await SeedAsync(alice);
		await alice.OkAsync("workspace", "create", "busy", "-context", "api/,web/");
		alice.Write("busy", "web/app.js", "changed\n");
		var refused = await alice.FailAsync("workspace", "context", "busy", "-remove", "web/");
		Assert.Contains("web/app.js", refused.Str("error"));
	}
}
