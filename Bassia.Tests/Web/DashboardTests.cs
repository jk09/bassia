using System.Net;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Web;
using Microsoft.AspNetCore.Builder;

namespace Bassia.Tests.Web;

public class DashboardTests
{
	/// <summary>The dashboard over a fixture monorepo, served on an ephemeral loopback port.</summary>
	private sealed class Host : IAsyncDisposable
	{
		private readonly WebApplication app;

		private Host(MonorepoFixture fixture) => app = new Dashboard(Monorepo.Load(fixture.Root)).Build("http://127.0.0.1:0");

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
			Client.Dispose();
			await app.StopAsync();
			await app.DisposeAsync();
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
		Assert.Contains("data-live-src", await host.GetAsync("/app.js"));
	}

	[Fact]
	public async Task Home_ShowsTheGraphWithComponentsAsLinks()
	{
		await using var fixture = await ThreeComponentsAsync();
		await using var host = await Host.StartAsync(fixture);

		var home = await host.GetAsync("/");

		Assert.Contains("<svg class=\"chart map\"", home);
		Assert.Contains("<a href=\"/components/lib\" class=\"node\" data-node=\"lib\" data-related=\"lib app\">", home);
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
	public async Task Runs_ShowTheChartAndARunsPathFromBaselineToResult()
	{
		await using var fixture = await ThreeComponentsAsync();
		var run = await fixture.RunStartAsync("-select", "app@v0,lib@v0", "-run", TestEnvironment.WriteFileCommand("app/a.txt", "a"));
		var runId = TestEnvironment.RunIdOf(run.Output);
		await using var host = await Host.StartAsync(fixture);

		var runs = await host.GetAsync("/runs");
		var page = await host.GetAsync($"/runs/{runId}");

		Assert.Contains("<svg class=\"chart gantt\"", runs);
		Assert.Contains($"href=\"/runs/{runId}\" class=\"row\"", runs);
		Assert.Contains("<svg class=\"chart donut\"", runs);
		Assert.Contains("<svg class=\"chart flow\"", page);
		Assert.Contains(RunMetadata.TagName(runId, 0), page); // its result tag in the flow and the table
		Assert.Contains($"bassia run diff {RunMetadata.Key(runId)} -patch", page);
		Assert.Contains("waiting", page); // its result waits in the merge queue
		Assert.Contains("No agentic run", await host.GetAsync("/runs/agent-run-brave-otter-000000", HttpStatusCode.NotFound));
	}

	[Fact]
	public void ComponentRefs_PutTheDefaultBranchAndBaselinesBeforeWhatBassiaMade()
	{
		GitRef Branch(string name) => new(name, GitRefKind.Branch, "c", "");
		GitRef Tag(string name) => new(name, GitRefKind.AnnotatedTag, "c", "");
		var refs = new[]
		{
			Branch("agent-run/fox-aaaaaa"), Branch("integration/owl-bbbbbb"), Branch("main"), Branch("feature"),
			Tag("agent-run/fox-aaaaaa/0"), Tag("integration/owl-bbbbbb/0"), Tag("v1.0")
		};

		var ordered = Dashboard.OrderRefs(refs, "main").Select(reference => reference.Name);

		Assert.Equal(["main", "feature", "v1.0", "integration/owl-bbbbbb/0", "agent-run/fox-aaaaaa/0", "integration/owl-bbbbbb", "agent-run/fox-aaaaaa"], ordered);
	}

	[Fact]
	public void RunTimeline_GivesAShortRunAVisibleBarThatStaysInsideThePlot()
	{
		var now = DateTimeOffset.UtcNow;
		var svg = Charts.RunTimeline(
		[
			new RunBar("agent-run-quick-fox-aaaaaa", "quick-fox", "completed", now.AddHours(-1), now.AddHours(-1).AddSeconds(1), false, "one second"),
			new RunBar("agent-run-late-owl-bbbbbb", "late-owl", "started", now.AddSeconds(-1), null, true, "just started")
		], now);

		var widths = System.Text.RegularExpressions.Regex.Matches(svg, "class=\"bar [^\"]*\" x=\"([0-9.]+)\" y=\"[0-9.]+\" width=\"([0-9.]+)\"")
			.Select(match => (X: double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
				Width: double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
			.ToList();
		Assert.Equal(2, widths.Count);
		Assert.All(widths, bar => Assert.True(bar.Width >= Charts.MinBarWidth, $"bar width {bar.Width}"));
		Assert.All(widths, bar => Assert.True(bar.X + bar.Width <= 190 + 760 + 0.01, $"bar ends at {bar.X + bar.Width}"));
		Assert.Contains("· 1s ·", svg); // the real duration stays in the tooltip
	}

	[Fact]
	public async Task LiveRuns_ComeFromTheJobRegistryWithTheirPhaseAndOutput()
	{
		await using var fixture = await ThreeComponentsAsync();
		var monorepo = Monorepo.Load(fixture.Root);
		var jobs = new JobRegistry(monorepo);
		var log = Path.Combine(jobs.Dir, "agent-run-quiet-fern-91ab22.log");
		using var job = jobs.Attach("agent-run-quiet-fern-91ab22", RunCommands.JobKind, log, detached: true);
		await File.WriteAllTextAsync(log, "bassia: preparing\nhello from the agent\n");
		await using var host = await Host.StartAsync(fixture);

		var overview = await host.GetAsync("/");
		var fragment = await host.GetAsync("/fragment/live");

		Assert.Contains("class=\"livecard\"", overview);
		Assert.Contains("quiet-fern-91ab22", fragment);
		Assert.Contains("hello from the agent", fragment);
		Assert.Contains("<li class=\"now\">preparing</li>", fragment); // no record yet
		Assert.Equal("finalizing", LiveRuns.PhaseOf(["bassia: x: committing, tagging and pushing the component results."]));
		Assert.Equal("agent", LiveRuns.PhaseOf(["bassia: x: running agent command in 'y'.", "agent output"]));
	}

	[Fact]
	public async Task Guard_IsReadOnlyAndRefusesAForeignHost()
	{
		await using var fixture = await ThreeComponentsAsync();
		await using var host = await Host.StartAsync(fixture);

		var post = await host.Client.PostAsync("/runs", new FormUrlEncodedContent(new Dictionary<string, string> { ["command"] = "echo pwned" }));
		var rebinding = new HttpRequestMessage(HttpMethod.Get, "/");
		rebinding.Headers.Host = "attacker.example";
		var foreign = await host.Client.SendAsync(rebinding);

		Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
		Assert.DoesNotContain("<form method=\"post\"", await host.GetAsync("/runs"));
	}

	[Fact]
	public async Task Tags_ChartTheTagsAcrossComponentsAndTellEachOnesStory()
	{
		await using var fixture = await ThreeComponentsAsync();
		Assert.Equal(0, (await fixture.BassiaAsync("tag", "create", "release-1", "-select", "app,lib")).ExitCode);
		var run = await fixture.RunStartAsync("-select", "app@release-1,lib@release-1", "-run", TestEnvironment.WriteFileCommand("lib/l.txt", "l"));
		var runId = TestEnvironment.RunIdOf(run.Output);
		await using var host = await Host.StartAsync(fixture);

		var tags = await host.GetAsync("/tags");
		var multi = await host.GetAsync("/tags?multi=1");
		var baseline = await host.GetAsync("/tag?name=release-1");
		var result = await host.GetAsync($"/tag?name={Uri.EscapeDataString(RunMetadata.TagName(runId, 0))}");

		Assert.Contains("<svg class=\"chart tags\"", tags);
		Assert.Contains("href=\"/tag?name=release-1\" class=\"tagcol k-baseline\"", tags);
		Assert.Contains("class=\"span\"", multi); // release-1 joins app and lib
		Assert.Contains("set by hand", baseline);
		Assert.Contains($"href=\"/runs/{runId}\"", baseline); // the run that started from it
		Assert.Contains("bassia run start -select app@release-1,lib@release-1", baseline);
		Assert.Contains("<svg class=\"chart flow\"", baseline);
		Assert.Contains("run <a class=\"id\"", result); // made by the run
		Assert.Contains("No component has a tag", await host.GetAsync("/tag?name=nope", HttpStatusCode.NotFound));
	}

	[Fact]
	public async Task Config_ShowsEveryLayerAndThePolicyPerComponent()
	{
		await using var fixture = await ThreeComponentsAsync();
		Assert.Equal(0, (await fixture.BassiaAsync("config", "set", "merge.component.lib.semantic", "-value", "manual")).ExitCode);
		Assert.Equal(0, (await fixture.BassiaAsync("config", "set", "merge.advance", "-value", "auto", "-user")).ExitCode);
		await using var host = await Host.StartAsync(fixture);

		var page = await host.GetAsync("/config");

		Assert.Contains("<code>merge.component.lib.semantic</code>", page);
		Assert.Contains("class=\"layer user wins\"", page);
		Assert.Contains("<span class=\"pv v-manual own\"", page);
		Assert.Contains("bassia config set merge.advance -value &quot;auto&quot;", page);
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
		await host.GetAsync("/integrations/integration-brave-otter-000000", HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Queue_ShowsTheTriageLanesTheCollisionsAndWhatNeedsAHuman()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example");
		var first = await fixture.RunWritingAsync("example", "same.txt", "from-first");
		var second = await fixture.RunWritingAsync("example", "same.txt", "from-second");
		Assert.Equal(0, (await fixture.BassiaAsync("config", "set", "merge.semantic", "-value", "manual")).ExitCode);
		await using var host = await Host.StartAsync(fixture);

		var before = await host.GetAsync("/queue");

		Assert.Contains("<svg class=\"chart lanes\"", before);
		Assert.Contains("<svg class=\"chart ring\"", before); // the two runs collide
		Assert.Contains("will need a human", before);
		Assert.Contains("<div class=\"n\">1</div><div class=\"muted\">need attention</div>", before); // planned, not yet left by an integration
		Assert.Contains("<div class=\"n\">1</div><div class=\"muted\">merges need attention</div>", await host.GetAsync("/"));
		Assert.Contains("merge.semantic = manual", before);
		Assert.Contains($"bassia integration start -runs {RunMetadata.Key(first)},{RunMetadata.Key(second)} -detach", before);

		Assert.Equal(0, (await fixture.IntegrationStartAsync("-runs", "all")).ExitCode);
		var after = await host.GetAsync("/queue");
		var overview = await host.GetAsync("/");

		Assert.Contains("needs attention", after);
		Assert.Contains($"bassia integration start -runs {RunMetadata.Key(second)} -semantic {RunMetadata.Key(second)}", after);
		Assert.Contains("bassia integration advance", after); // the first result is integrated, its base not advanced
		Assert.Contains("<div class=\"n\">1</div><div class=\"muted\">merges need attention</div>", overview);
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
