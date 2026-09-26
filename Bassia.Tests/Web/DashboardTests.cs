using System.Net;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Ui;
using Bassia.Web;
using Microsoft.AspNetCore.Builder;

namespace Bassia.Tests.Web;

public class DashboardTests
{
	/// <summary>The dashboard over a fixture monorepo, served on an ephemeral loopback port.</summary>
	private sealed class Host : IAsyncDisposable
	{
		private readonly WebApplication app;

		private Host(MonorepoFixture fixture)
		{
			var monorepo = Monorepo.Load(fixture.Root);
			var git = new GitClient(fixture.Root);
			Supervisor = new RunSupervisor((select, command, context) => AgentCommand.StartRunAsync(git, monorepo, select, command, context));
			Dashboard = new Dashboard(monorepo, Supervisor);
			app = Dashboard.Build("http://127.0.0.1:0");
		}

		public RunSupervisor Supervisor { get; }
		public Dashboard Dashboard { get; }
		public HttpClient Client { get; private set; } = null!;

		public static async Task<Host> StartAsync(MonorepoFixture fixture)
		{
			var host = new Host(fixture);
			await host.app.StartAsync();
			host.Client = new HttpClient { BaseAddress = new Uri(host.app.Urls.First()) };
			return host;
		}

		public async Task<string> GetAsync(string path, HttpStatusCode expected = HttpStatusCode.OK)
		{
			var response = await Client.GetAsync(path);
			var body = await response.Content.ReadAsStringAsync();
			Assert.True(response.StatusCode == expected, $"GET {path}: {response.StatusCode}\n{body}");
			return body;
		}

		public async ValueTask DisposeAsync()
		{
			Supervisor.CancelAll();
			await Supervisor.WhenAllSettledAsync();
			Client.Dispose();
			await app.StopAsync();
			await app.DisposeAsync();
			Supervisor.Dispose();
		}
	}

	/// <summary>app references lib; other stands alone.</summary>
	private static async Task<MonorepoFixture> ThreeComponentsAsync()
	{
		var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("app");
		await fixture.AddComponentAsync("lib");
		await fixture.AddComponentAsync("other");
		await fixture.SetReferencesAsync("app", "lib");
		return fixture;
	}

	[Fact]
	public async Task EveryMenuPage_RendersWithTheMenu()
	{
		await using var fixture = await ThreeComponentsAsync();
		await using var host = await Host.StartAsync(fixture);

		foreach (var (path, _) in Html.Menu)
		{
			var page = await host.GetAsync(path);
			Assert.Contains("<nav>", page);
			Assert.Contains($"href=\"{path}\" class=\"active\"", page);
		}

		Assert.Contains("font-family", await host.GetAsync("/style.css"));
		Assert.Contains("EventSource", await host.GetAsync("/app.js"));
	}

	[Fact]
	public async Task Home_ShowsTheGraphWithComponentsAsLinks()
	{
		await using var fixture = await ThreeComponentsAsync();
		await using var host = await Host.StartAsync(fixture);

		var home = await host.GetAsync("/");

		Assert.Contains("<svg", home);
		Assert.Contains("<a href=\"/components/lib\">", home);
		Assert.Contains("<div class=\"n\">3</div><div class=\"muted\">components</div>", home);
		Assert.Contains("<a href=\"/components/app\">", await host.GetAsync("/graph.svg"));
	}

	[Fact]
	public async Task Component_ShowsItsRefsRunsAndReferences()
	{
		await using var fixture = await ThreeComponentsAsync();
		var run = await fixture.RunStartAsync("-select", "app@v0,lib@v0", "-run", TestEnvironment.WriteFileCommand("app/a.txt", "a"));
		var runId = TestEnvironment.RunIdOf(run.Output);
		await using var host = await Host.StartAsync(fixture);

		var app = await host.GetAsync("/components/app");
		var lib = await host.GetAsync("/components/lib");

		Assert.Contains("<b>annotated tag</b>", app);
		Assert.Contains(">v0<", app);
		Assert.Contains($"href=\"/runs/{runId}\"", app);
		Assert.Contains("href=\"/components/lib\"", app); // needs
		Assert.Contains("href=\"/components/app\"", lib); // used by
		Assert.Contains("is not registered", await host.GetAsync("/components/nope", HttpStatusCode.NotFound));
	}

	[Fact]
	public async Task Timeline_TreatsAComponentAndItsDependenciesAsOneUnitAndNeverTheWholeMonorepo()
	{
		await using var fixture = await ThreeComponentsAsync();
		await using var host = await Host.StartAsync(fixture);

		var empty = await host.GetAsync("/timeline");
		var unit = await host.GetAsync("/timeline?c=app");

		Assert.Contains("Choose the components to follow", empty);
		Assert.DoesNotContain("Initial commit", empty);
		Assert.Contains("lib joined as dependencies", unit);
		Assert.Contains("href=\"/components/app\">app</a></td>", unit);
		Assert.Contains("href=\"/components/lib\">lib</a></td>", unit);
		Assert.DoesNotContain("href=\"/components/other\">other</a></td>", unit);
		Assert.Contains("is not registered", await host.GetAsync("/timeline?c=nope", HttpStatusCode.BadRequest));
	}

	[Fact]
	public async Task Timeline_MergesTheUnitByDateAndPages()
	{
		await using var fixture = await ThreeComponentsAsync();
		var monorepo = Monorepo.Load(fixture.Root);
		var unit = Timeline.Unit(monorepo, ["app"]);
		Assert.Equal(["app", "lib"], unit);

		// Four commits: the two initial ones, then one result per component from a run over the unit.
		var run = await fixture.RunStartAsync("-select", "app@v0,lib@v0", "-run",
			$"{TestEnvironment.WriteFileCommand("app/a.txt", "a")} && {TestEnvironment.WriteFileCommand("lib/l.txt", "l")}");
		Assert.Equal(0, run.ExitCode);

		var first = await Timeline.ReadAsync(monorepo, unit, page: 1, pageSize: 3);
		var second = await Timeline.ReadAsync(monorepo, unit, page: 2, pageSize: 3);

		Assert.Equal(3, first.Entries.Count);
		Assert.True(first.HasMore);
		Assert.Single(second.Entries);
		Assert.False(second.HasMore);
		var all = first.Entries.Concat(second.Entries).ToList();
		Assert.Equal(all.OrderByDescending(entry => entry.Date).Select(entry => entry.Hash), all.Select(entry => entry.Hash));
		Assert.Equal(4, all.Select(entry => entry.Hash).Distinct().Count());
		Assert.Contains(all, entry => entry.Refs.Contains(RunMetadata.TagName(TestEnvironment.RunIdOf(run.Output), 0)));

		await using var host = await Host.StartAsync(fixture);
		var page = await host.GetAsync("/timeline?c=app&n=3");
		Assert.Contains("older →", page);
		Assert.Contains($"href=\"/runs/{TestEnvironment.RunIdOf(run.Output)}\"", page); // the run's tag links to it
		Assert.Contains("← newer", await host.GetAsync("/timeline?c=app&n=3&page=2"));
	}

	[Fact]
	public async Task Commit_ShowsTheCommitAndRefusesAnythingButAHash()
	{
		await using var fixture = await ThreeComponentsAsync();
		var hash = await TestEnvironment.GitAsync(fixture.SourceRepo("lib"), "rev-parse", "main");
		await using var host = await Host.StartAsync(fixture);

		var page = await host.GetAsync($"/commit/lib/{hash}");

		Assert.Contains("Initial commit", page);
		Assert.Contains("README.md", page); // the diffstat
		Assert.Contains(">v0<", page);
		await host.GetAsync("/commit/lib/--output=x", HttpStatusCode.NotFound);
		await host.GetAsync("/commit/lib/0000000000", HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task NewRun_StartsTheRunInTheBackgroundAndItsPageShowsTheRecord()
	{
		await using var fixture = await ThreeComponentsAsync();
		await using var host = await Host.StartAsync(fixture);

		var form = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["token"] = host.Dashboard.Token,
			["c"] = "app", // lib joins through the reference, at the tag chosen for it
			["tag:app"] = "v0",
			["tag:lib"] = "v0",
			["prompt"] = "write a",
			["command"] = TestEnvironment.WriteFileCommand("app/a.txt", "a")
		});
		var response = await host.Client.PostAsync("/new", form);
		var live = await response.Content.ReadAsStringAsync();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("/runs/s1", response.RequestMessage!.RequestUri!.AbsolutePath);
		Assert.Contains("Agent output", live);
		await host.Supervisor.WhenAllSettledAsync();

		var card = Assert.Single(host.Supervisor.Cards());
		Assert.Equal("app@v0,lib@v0", card.Select);
		Assert.Equal(AgentRunPhase.Completed, card.Phase);
		var done = await host.GetAsync("/runs/s1");
		Assert.Contains("completed", done);
		Assert.Contains(RunMetadata.TagName(card.RunId!, 0), done);
		Assert.Contains(RunMetadata.ShortKey(card.RunId!), await host.GetAsync("/runs"));
		Assert.Contains("event: done", await host.GetAsync("/runs/s1/events"));
	}

	[Fact]
	public async Task NewRun_WithoutAPromptOrComponent_IsSentBackWithTheError()
	{
		await using var fixture = await ThreeComponentsAsync();
		await using var host = await Host.StartAsync(fixture);

		var noComponent = await host.Client.PostAsync("/new", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = host.Dashboard.Token, ["prompt"] = "x" }));
		var noPrompt = await host.Client.PostAsync("/new", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = host.Dashboard.Token, ["c"] = "other", ["tag:other"] = "v0" }));

		Assert.Equal(HttpStatusCode.BadRequest, noComponent.StatusCode);
		Assert.Contains("Choose at least one component", await noComponent.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.BadRequest, noPrompt.StatusCode);
		Assert.Contains("Write a prompt", await noPrompt.Content.ReadAsStringAsync());
		Assert.Empty(host.Supervisor.Cards());
	}

	[Fact]
	public async Task Compose_BuildsTheCommandTheWayTheTerminalFrontendDoes()
	{
		await using var fixture = await ThreeComponentsAsync();
		await using var host = await Host.StartAsync(fixture);

		var command = await host.GetAsync("/new/compose?agent=claude%20-p&model=opus&prompt=do%20it&context=see%20README");

		Assert.Equal(InteractiveSession.ComposeCommand("claude -p", "opus", null, "do it", "see README"), command);
	}

	[Fact]
	public async Task Guard_RefusesAPostWithoutTheTokenAndAForeignHost()
	{
		await using var fixture = await ThreeComponentsAsync();
		await using var host = await Host.StartAsync(fixture);

		var forged = await host.Client.PostAsync("/new", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["c"] = "other", ["tag:other"] = "v0", ["command"] = "echo pwned"
		}));
		var rebinding = new HttpRequestMessage(HttpMethod.Get, "/");
		rebinding.Headers.Host = "attacker.example";
		var foreign = await host.Client.SendAsync(rebinding);

		Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
		Assert.Empty(host.Supervisor.Cards());
		Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
	}

	[Fact]
	public async Task Integrations_ListTheRecordsShowTheirStepsAndPreviewATriage()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var first = await fixture.RunWritingAsync("example", "same.txt", "from-first");
		var second = await fixture.RunWritingAsync("example", "same.txt", "from-second");
		Assert.Equal(0, (await fixture.IntegrationStartAsync("-runs", first, "-resolve", TestEnvironment.FailingCommand)).ExitCode);
		var record = Assert.Single(await new Bassia.Integration.IntegrationStore(new RunMetadataStore(new GitClient(fixture.Root), fixture.RunsRepo)).ListLatestAsync());
		await using var host = await Host.StartAsync(fixture);

		var list = await host.GetAsync("/integrations");
		var detail = await host.GetAsync($"/integrations/{record.IntegrationId}");
		var plan = await host.GetAsync($"/integrations/plan?run={first}&run={second}");

		Assert.Contains($"href=\"/integrations/{record.IntegrationId}\"", list);
		Assert.Contains($"value=\"{second}\"", list); // offered for a triage preview
		Assert.Contains("SYNTACTIC", detail);
		Assert.Contains("merged", detail);
		Assert.Contains("SEMANTIC", plan);
		Assert.Contains("same.txt", plan);
		Assert.Contains("Nothing was changed", plan);
		Assert.Single(await new Bassia.Integration.IntegrationStore(new RunMetadataStore(new GitClient(fixture.Root), fixture.RunsRepo)).ListLatestAsync());
		await host.GetAsync("/integrations/integration-00000000000000000000000000000000", HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Web_OutsideAMonorepo_Fails()
	{
		using var directory = new TempDirectory();

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(directory.Path, "web", "--no-open");

		Assert.Equal(1, exitCode);
		Assert.Contains("is not inside a Bassia monorepo", error);
	}
}
