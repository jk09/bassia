namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: keeping storage healthy - garbage collection of what nothing references any more, and scrubbing stored
/// blobs against their ids. A client collects its own cache; a local monorepo collects and scrubs its blob store.
/// </summary>
[Trait("Scenario", "Maintenance")]
public sealed class MaintenanceScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	[ScenarioFact]
	public async Task GarbageCollection_KeepsEverythingMainlineAndOpenWorkspacesNeed()
	{
		var local = await InitLocalAsync();
		await local.LandFilesAsync("keep", "keep", new Dictionary<string, string?> { ["kept.txt"] = "kept\n" });
		await local.OkAsync("workspace", "create", "draft");
		local.Write("draft", "draft.txt", "unlanded but open\n");
		await local.OkAsync("workspace", "snapshot", "draft");
		await local.OkAsync("workspace", "create", "dropped");
		local.Write("dropped", "dropped.txt", "abandoned work\n");
		await local.OkAsync("workspace", "snapshot", "dropped");
		await local.OkAsync("workspace", "close", "dropped", "-abandon");

		var dry = await local.OkAsync("gc", "-dry-run", "-grace-hours", "0", "-retention-days", "0");
		Assert.True(dry.Bool("dry_run"));
		var collected = await local.OkAsync("gc", "-grace-hours", "0", "-retention-days", "0");
		Assert.True(collected.Long("live") > 0);

		// Everything still reachable reads back intact.
		Assert.Equal("kept\n", await local.CatAsync("kept.txt"));
		await local.OkAsync("workspace", "create", "after-gc");
		Assert.Equal("kept\n", local.Read("after-gc", "kept.txt"));
		Assert.Equal("unlanded but open\n", local.Read("draft", "draft.txt"));
		Assert.Equal("open", (await local.OkAsync("workspace", "show", "draft")).Str("status"));
		Assert.Empty((await local.OkAsync("scrub")).Strings("corrupt"));
	}

	[ScenarioFact]
	public async Task Scrub_ReHashesTheBlobStore_AndFindsACorruptBlob()
	{
		var local = await InitLocalAsync();
		await local.LandFilesAsync("data", "data", new Dictionary<string, string?> { ["data.txt"] = "precious data\n" });
		var clean = await local.OkAsync("scrub");
		Assert.True(clean.Long("checked") > 0);
		Assert.Empty(clean.Strings("corrupt"));

		// Flip a byte in one stored blob, as a failing disk would.
		var objects = Path.Combine(local.Root, ".bassia", "objects");
		var victim = Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).OrderByDescending(file => new FileInfo(file).Length).First();
		var bytes = File.ReadAllBytes(victim);
		bytes[bytes.Length / 2] ^= 0xFF;
		File.SetAttributes(victim, FileAttributes.Normal);
		File.WriteAllBytes(victim, bytes);

		var dirty = await local.FailAsync("scrub");
		Assert.NotEmpty(dirty.Strings("corrupt").Concat(dirty.Strings("unreadable")));
	}

	[ScenarioFact]
	public async Task OnAClient_GcPrunesOnlyItsOwnCache_AndScrubPointsToTheServer()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("x", "x", new Dictionary<string, string?> { ["x.txt"] = "x\n" });
		await alice.OkAsync("gc");
		Assert.Equal("x\n", await alice.CatAsync("x.txt"));
		Assert.Contains("server", (await alice.FailAsync("scrub")).Str("error"));
	}
}
