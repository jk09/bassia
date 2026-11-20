namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: several people and agents on different machines (clients) share one server - they see each other's
/// landings and workspaces, pick up a workspace started elsewhere, and land concurrently on one linear mainline.
/// </summary>
[Trait("Scenario", "Collaboration")]
public sealed class CollaborationScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	[ScenarioFact]
	public async Task ALandingOnOneClient_IsSeenByEveryOther()
	{
		var alice = await CloneAsync("alice");
		var bob = await CloneAsync("bob");
		await alice.LandFilesAsync("hello", "alice says hello", new Dictionary<string, string?> { ["hello.txt"] = "from alice\n" });

		Assert.Equal(1L, await bob.MainSeqAsync());
		Assert.Equal("from alice\n", await bob.CatAsync("hello.txt"));
		var landing = (await bob.OkAsync("mainline", "log", "-limit", "1")).Tables("landing").Single();
		Assert.Equal("alice", landing.Str("author"));

		await bob.OkAsync("workspace", "create", "bobs");
		Assert.Equal("from alice\n", bob.Read("bobs", "hello.txt"));
	}

	[ScenarioFact]
	public async Task Workspaces_AreVisibleToEveryClient_AndNamesAreUniqueAcrossThem()
	{
		var alice = await CloneAsync("alice");
		var bob = await CloneAsync("bob");
		await alice.OkAsync("workspace", "create", "shared-name");

		var listed = (await bob.OkAsync("workspace", "list")).Tables("workspace");
		var workspace = Assert.Single(listed);
		Assert.Equal("shared-name", workspace.Str("name"));
		Assert.Equal("alice", workspace.Str("owner"));
		await bob.FailAsync("workspace", "create", "shared-name");
	}

	[ScenarioFact]
	public async Task AWorkspaceStartedOnOneMachine_IsCheckedOutAndFinishedOnAnother()
	{
		var alice = await CloneAsync("alice");
		var bob = await CloneAsync("bob");
		await alice.OkAsync("workspace", "create", "handover", "-intent", "half done by alice");
		alice.Write("handover", "part1.txt", "alice's part\n");
		await alice.OkAsync("workspace", "snapshot", "handover");

		var checkedOut = await bob.OkAsync("workspace", "checkout", "handover");
		Assert.Equal("alice's part\n", bob.Read("handover", "part1.txt"));
		bob.Write("handover", "part2.txt", "bob's part\n");
		var landed = await bob.OkAsync("workspace", "land", "handover");
		Assert.Equal("landed", landed.Str("outcome"));
		Assert.Equal("alice's part\n", await alice.CatAsync("part1.txt"));
		Assert.Equal("bob's part\n", await alice.CatAsync("part2.txt"));
		Assert.True(checkedOut.Bool("ok"));
	}

	[ScenarioFact]
	public async Task ConcurrentLandingsFromManyClients_GiveOneLinearMainline_WithEveryChange()
	{
		var clients = new List<ScenarioClient>();
		for (var i = 0; i < 4; i++)
		{
			clients.Add(await CloneAsync($"client{i}"));
		}

		for (var i = 0; i < clients.Count; i++)
		{
			await clients[i].OkAsync("workspace", "create", $"work-{i}");
			clients[i].Write($"work-{i}", $"area{i}/change.txt", $"change {i}\n");
			await clients[i].OkAsync("workspace", "snapshot", $"work-{i}");
		}

		// The in-process CLI runs one command at a time; the server still sees landings racing with reconciliation.
		var results = await Task.WhenAll(clients.Select((client, i) => client.OkAsync("workspace", "land", $"work-{i}")));
		Assert.All(results, result => Assert.Equal("landed", result.Str("outcome")));
		Assert.Equal(Enumerable.Range(1, clients.Count).Select(i => (long)i), results.Select(result => result.Long("seq")).Order());
		for (var seq = 1; seq <= clients.Count; seq++)
		{
			await clients[0].RunAsync("mainline", "show", "-seq", seq.ToString());
			await clients[0].RunAsync("mainline", "ls", "-seq", seq.ToString());
		}

		for (var i = 0; i < clients.Count; i++)
		{
			Assert.Equal($"change {i}\n", await clients[0].CatAsync($"area{i}/change.txt"));
		}
	}

	[ScenarioFact]
	public async Task ThePersonActing_IsRecorded_AsOwnerAndAuthor()
	{
		var agent = await CloneAsync("ci-machine", user: "build-agent-7");
		var created = await agent.OkAsync("workspace", "create", "automated");
		Assert.Equal("build-agent-7", created.Str("owner"));
		agent.Write("automated", "generated.txt", "generated\n");
		await agent.OkAsync("workspace", "land", "automated");
		Assert.Equal("build-agent-7", (await agent.OkAsync("mainline", "log", "-limit", "1")).Tables("landing").Single().Str("author"));
	}
}
