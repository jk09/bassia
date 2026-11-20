namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Tomlyn.Model;
using Xunit.Abstractions;

/// <summary>
/// Scenario: agents coordinate on parts of the tree with leases - exclusive or intent holds on a path prefix, with a
/// time to live and a fencing token; a prefix listed under <c>[leases] required</c> in <c>.bassia.toml</c> lands only
/// with a valid exclusive lease.
/// </summary>
[Trait("Scenario", "Leases")]
public sealed class LeaseScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	private static string LandWith(TomlTable lease) => $"{lease.Str("id")}:{lease.Long("token")}";

	[ScenarioFact]
	public async Task AnExclusiveLease_ExcludesOverlappingOnes_UntilReleased()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "one");
		await alice.OkAsync("workspace", "create", "two");

		var lease = await alice.OkAsync("lease", "acquire", "src/billing/", "-workspace", "one", "-mode", "exclusive");
		Assert.Equal("exclusive", lease.Str("mode"));
		Assert.True(lease.Long("token") > 0);

		var blocked = await alice.FailAsync("lease", "acquire", "src/billing/rounding/", "-workspace", "two");
		Assert.NotEmpty(blocked.Tables("blocking"));

		// Disjoint prefixes never block each other.
		await alice.OkAsync("lease", "acquire", "src/shipping/", "-workspace", "two");

		var listed = await alice.OkAsync("lease", "list");
		Assert.Contains(listed.Tables("lease"), entry => entry.Str("prefix").TrimEnd('/') == "src/billing" && entry.Str("mode") == "exclusive");
		Assert.NotEmpty((await alice.OkAsync("lease", "list", "-prefix", "src/billing")).Tables("lease"));

		await alice.OkAsync("lease", "release", "-id", lease.Str("id"), "-token", lease.Long("token").ToString());
		await alice.OkAsync("lease", "acquire", "src/billing/rounding/", "-workspace", "two");
	}

	[ScenarioFact]
	public async Task IntentLeases_Coexist()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "one");
		await alice.OkAsync("workspace", "create", "two");
		await alice.OkAsync("lease", "acquire", "docs/", "-workspace", "one", "-mode", "intent");
		await alice.OkAsync("lease", "acquire", "docs/", "-workspace", "two", "-mode", "intent");
		Assert.Equal(2, (await alice.OkAsync("lease", "list", "-prefix", "docs/")).Tables("lease").Count(lease => lease.Str("mode") == "intent" && lease.Str("prefix").StartsWith("docs")));
	}

	[ScenarioFact]
	public async Task ARenewedLease_KeepsItsToken_AndLivesLonger()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "one");
		var lease = await alice.OkAsync("lease", "acquire", "lib/", "-workspace", "one", "-ttl", "60");
		var renewed = await alice.OkAsync("lease", "renew", "-id", lease.Str("id"), "-token", lease.Long("token").ToString(), "-ttl", "3600");
		Assert.Equal(lease.Long("token"), renewed.Long("token"));
		Assert.True(DateTimeOffset.Parse(renewed.Str("expires")) > DateTimeOffset.Parse(lease.Str("expires")));
	}

	[ScenarioFact]
	public async Task ARequiredLease_GuardsLanding_AndAStaleFencingTokenIsRefused()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("policy", "require leases on billing", new Dictionary<string, string?>
		{
			[".bassia.toml"] = "[leases]\nrequired = [\"billing/\"]\n",
			["billing/rates.txt"] = "1.0\n"
		});
		await alice.OkAsync("workspace", "create", "rates");
		alice.Write("rates", "billing/rates.txt", "1.1\n");

		// Without a lease the landing is rejected.
		var rejected = await alice.RunAsync("workspace", "land", "rates");
		Assert.Equal("rejected", rejected.Result.Str("outcome"));

		// A short lease, expired and granted again, gets a new token: the old one is refused.
		var first = await alice.OkAsync("lease", "acquire", "billing/", "-workspace", "rates", "-ttl", "1");
		await Task.Delay(TimeSpan.FromSeconds(2.5));
		var second = await alice.OkAsync("lease", "acquire", "billing/", "-workspace", "rates", "-ttl", "600");
		Assert.True(second.Long("token") > first.Long("token"));
		Assert.Equal("rejected", (await alice.RunAsync("workspace", "land", "rates", "-lease-token", LandWith(first))).Result.Str("outcome"));

		var landed = await alice.OkAsync("workspace", "land", "rates", "-lease-token", LandWith(second));
		Assert.Equal("landed", landed.Str("outcome"));
		Assert.Equal("1.1\n", await alice.CatAsync("billing/rates.txt"));
	}

	[ScenarioFact]
	public async Task Leases_AreSeenByEveryClientOfTheServer()
	{
		var alice = await CloneAsync("alice");
		var bob = await CloneAsync("bob");
		await alice.OkAsync("workspace", "create", "alice-work");
		await bob.OkAsync("workspace", "create", "bob-work");
		await alice.OkAsync("lease", "acquire", "shared/", "-workspace", "alice-work");

		Assert.NotEmpty((await bob.OkAsync("lease", "list")).Tables("lease"));
		await bob.FailAsync("lease", "acquire", "shared/", "-workspace", "bob-work");
	}
}
