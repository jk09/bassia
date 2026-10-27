namespace Bassia.Tests.Server;

using System.Collections.Concurrent;
using System.Net;
using Bassia.Locks;
using Bassia.Server;
using Bassia.Server.Data;
using Bassia.Server.Web;
using Bassia.Storage;
using Bassia.Storage.Remote;
using Microsoft.AspNetCore.Builder;

/// <summary>
/// The dashboard on the server (bassia-d3c6cfe4): every page as the viewer may see it, the sign-in in a browser, and
/// the live changes pushed to browsers on every server instance.
/// </summary>
[Trait("Category", "Server")]
public class ServerDashboardTests
{
	/// <summary>The pages and fragments a viewer reads: none may show a component the viewer may not read.</summary>
	private static readonly string[] Pages =
		["/", "/components", "/graph.svg", "/runs", "/locks", "/tags", "/queue", "/integrations", "/timeline?c=lib", "/config",
		 "/fragment/live", "/fragment/locks", "/fragment/queue"];

	/// <summary>A live run in <paramref name="component"/>: its record, a hold in the lock table and a detached job with output.</summary>
	private static async Task<string> SeedAsync(ServerState state, string component)
	{
		var runId = RunMetadata.NewRunId();
		var now = RunMetadata.Timestamp();
		var run = new RunMetadata { RunId = runId, Status = "started", Created = now, Select = component, Command = "agent", WorkspacePath = "/runs/" + runId, User = "alice" };
		run.Components.Add(new ComponentRun { Name = component, CommitIsh = "v0", Commit = new string('0', 40), Path = $"/runs/{runId}/{component}", Branch = RunMetadata.RefBase(runId) });
		await state.Records.CommitAsync(run);

		var entry = new LockEntry { RunId = runId, Host = "worker", Pid = 1, ProcessStart = "1", Started = now, Heartbeat = now, User = "alice" };
		entry.Holds.Add(new LockHold(new LockKey(component, $"src/{component}-held.c", LockKind.File), "edit", now));
		var (version, _) = await state.Locks.ReadTableAsync();
		Assert.True(await state.Locks.CommitAsync(version, [entry], []));

		await state.Jobs.RegisterAsync(new JobRegistration(runId, "run", 1, "1", "agent.log", true, false, "worker", [component]), "alice");
		await state.Jobs.AppendLogAsync(runId, $"working on {component}-output\n");
		return runId;
	}

	/// <summary>A third component, vault, which only admins may read.</summary>
	private static async Task AddVaultAsync(ServerHost host)
	{
		Assert.Equal(0, (await host.Depot.BassiaAsync("user", "use", "-id", "alice")).ExitCode);
		await host.Depot.AddComponentAsync("vault", files: new Dictionary<string, string> { ["src/vault.c"] = "int v;\n" });
		Assert.Equal(0, (await host.Depot.BassiaAsync("config", "set", "-key", "merge.component.vault.semantic", "-value", "manual")).ExitCode);
	}

	[Fact]
	public void Configuration_OfTheSignIn_FromTheFileAndTheEnvironment()
	{
		var text = """
			state = "state"
			[storage]
			path = "."
			[auth]
			signing_key = "a-test-signing-key-of-at-least-32-characters"
			session_hours = 8
			[[auth.oidc]]
			name = "okta"
			issuer = "https://example.okta.com"
			client_id = "web"
			client_secret = "from-the-file"
			[auth.github]
			client_id = "gh"
			""";
		var config = ServerConfig.Parse(text, Path.GetTempPath());
		Assert.Equal(8, config.SessionHours);
		Assert.Equal("from-the-file", config.Oidc[0].ClientSecret);
		Assert.Null(config.GitHub!.ClientSecret);

		Environment.SetEnvironmentVariable("BASSIA_GITHUB_CLIENT_SECRET", "from-the-environment");
		try
		{
			Assert.Equal("from-the-environment", ServerConfig.Parse(text, Path.GetTempPath()).GitHub!.ClientSecret);
		}
		finally
		{
			Environment.SetEnvironmentVariable("BASSIA_GITHUB_CLIENT_SECRET", null);
		}
	}

	private static HttpClient Browser(ServerHost host, string? user, bool redirects = true)
	{
		var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(host.Url) };
		if (user is not null)
		{
			http.DefaultRequestHeaders.Authorization = new("Bearer", host.TokenFor(user));
		}

		return http;
	}

	[Fact]
	public async Task Dashboard_ShowsEachViewerOnlyTheComponentsTheyMayRead()
	{
		await using var host = await ServerHost.StartAsync();
		await AddVaultAsync(host);
		await host.State.Access.GrantAsync(new Grant("user:bob", "lib", "read"));
		await host.State.Access.GrantAsync(new Grant("user:bob", "app", "read"));
		var visible = await SeedAsync(host.State, "lib");
		var hidden = await SeedAsync(host.State, "vault");

		using var bob = Browser(host, "bob");
		foreach (var page in Pages)
		{
			var response = await bob.GetAsync(page);
			var html = await response.Content.ReadAsStringAsync();
			Assert.True(response.IsSuccessStatusCode, $"{page}: {(int)response.StatusCode} {html}");
			Assert.DoesNotContain("vault", html);
			Assert.DoesNotContain(RunMetadata.Key(hidden), html);
		}

		Assert.Contains(RunMetadata.Key(visible), await bob.GetStringAsync("/runs"));
		Assert.Contains("lib-held.c", await bob.GetStringAsync("/locks"));
		Assert.Contains("lib-output", await bob.GetStringAsync($"/runs/{visible}"));
		Assert.Contains("data-events=\"/events\"", await bob.GetStringAsync("/"));
		Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/components/vault")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/runs/{hidden}")).StatusCode);
		Assert.DoesNotContain("vault-output", await (await bob.GetAsync($"/fragment/run/{hidden}")).Content.ReadAsStringAsync());

		// An admin sees everything.
		using var alice = Browser(host, "alice");
		Assert.Contains("vault", await alice.GetStringAsync("/components"));
		Assert.Contains(RunMetadata.Key(hidden), await alice.GetStringAsync("/runs"));
		Assert.Contains("vault-held.c", await alice.GetStringAsync("/locks"));

		// A viewer who may read nothing sees an empty monorepo.
		using var carol = Browser(host, "carol");
		var empty = await carol.GetStringAsync("/components");
		Assert.DoesNotContain("/components/lib", empty);
		Assert.DoesNotContain(RunMetadata.Key(visible), await carol.GetStringAsync("/runs"));

		// The dashboard only reads.
		Assert.Equal(HttpStatusCode.MethodNotAllowed, (await bob.PostAsync("/runs", null)).StatusCode);
	}

	[Fact]
	public async Task Dashboard_WithoutASignIn_SendsTheBrowserToSignIn()
	{
		await using var host = await ServerHost.StartAsync();
		using var anonymous = Browser(host, null, redirects: false);

		var page = await anonymous.GetAsync("/runs?status=live");
		Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
		Assert.Equal("/login?return=%2Fruns%3Fstatus%3Dlive", page.Headers.Location!.OriginalString);

		var fragment = new HttpRequestMessage(HttpMethod.Get, "/fragment/live");
		fragment.Headers.Add("X-Fragment", "1");
		Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(fragment)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/events")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/style.css")).StatusCode);

		// A forged or expired session cookie is no sign-in.
		var forged = new HttpRequestMessage(HttpMethod.Get, "/");
		forged.Headers.Add("Cookie", $"{WebSignIn.SessionCookie}={host.State.Tokens.Issue("alice", TimeSpan.FromSeconds(-60))}");
		Assert.Equal(HttpStatusCode.Redirect, (await anonymous.SendAsync(forged)).StatusCode);

		// The page after the sign-in is always on this server.
		Assert.Equal("/", WebSignIn.LocalPath("https://evil.example/"));
		Assert.Equal("/", WebSignIn.LocalPath("//evil.example/"));
		Assert.Equal("/", WebSignIn.LocalPath("/\\evil.example/"));
		Assert.Equal("/runs?x=1", WebSignIn.LocalPath("/runs?x=1"));

		// No identity provider has a client for browsers: the page says what to configure.
		Assert.Contains("client_secret", await anonymous.GetStringAsync("/login"));
	}

	[Fact]
	public async Task SignIn_InABrowser_WithOidcOrGitHub_SetsASessionAndSignsOut()
	{
		await using var idp = await FakeIdentityProvider.StartAsync();
		await using var host = await ServerHost.StartAsync(auth: idp.ServerAuth + "\nclient_secret = \"github-secret\"");
		await host.State.Access.GrantAsync(new Grant("group:devs", "lib", "read"));

		using var browser = Browser(host, null);
		var login = await browser.GetStringAsync("/login?return=/runs");
		Assert.Contains("/login/fake?return=%2Fruns", login);
		Assert.Contains("/login/github?return=%2Fruns", login);

		// OIDC: the identity provider logs in okta-bob (bob@example.com, group devs), and the browser lands on the page it asked for.
		var signedIn = await browser.GetAsync("/login/fake?return=/runs");
		Assert.True(signedIn.IsSuccessStatusCode, await signedIn.Content.ReadAsStringAsync());
		Assert.Equal("/runs", signedIn.RequestMessage!.RequestUri!.AbsolutePath);
		var runs = await signedIn.Content.ReadAsStringAsync();
		Assert.Contains("bob", runs);
		Assert.Contains("sign out", runs);
		Assert.Equal(1, idp.Issued.GetValueOrDefault("authorization_code"));

		// The identity provider's groups stay with the sign-in: devs may read lib.
		Assert.Contains("/components/lib", await browser.GetStringAsync("/components"));
		Assert.DoesNotContain("/components/app", await browser.GetStringAsync("/components"));
		Assert.Contains((await host.State.Access.AuditLogAsync(10, null)), entry => entry.Action == "login" && entry.Target == "fake" && entry.User == "bob");

		// Signing out ends it.
		await browser.GetAsync("/logout");
		Assert.Equal("/login", (await browser.GetAsync("/components")).RequestMessage!.RequestUri!.AbsolutePath);

		// GitHub's web flow, to the same user.
		using var github = Browser(host, null);
		var viaGitHub = await github.GetAsync("/login/github");
		Assert.True(viaGitHub.IsSuccessStatusCode, await viaGitHub.Content.ReadAsStringAsync());
		Assert.Equal("/", viaGitHub.RequestMessage!.RequestUri!.AbsolutePath);
		Assert.Contains("sign out", await viaGitHub.Content.ReadAsStringAsync());

		// A callback that did not start here, or whose state does not match, signs nobody in.
		using var stranger = Browser(host, null, redirects: false);
		Assert.Equal(HttpStatusCode.BadRequest, (await stranger.GetAsync("/login/fake/callback?code=x&state=y")).StatusCode);
		var started = await stranger.GetAsync("/login/fake");
		Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await stranger.GetAsync("/login/fake/callback?code=x&state=not-the-state")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync("/login/nobody")).StatusCode);
	}

	/// <summary>The <c>data</c> of every <c>change</c> event a stream sends, as they arrive.</summary>
	private sealed class EventStream : IAsyncDisposable
	{
		private readonly HttpClient http;
		private readonly CancellationTokenSource stop = new();
		private readonly Task reading;

		public ConcurrentQueue<string> Changes { get; } = new();

		private EventStream(HttpClient http, Stream stream, TaskCompletionSource connected)
		{
			this.http = http;
			reading = Task.Run(async () =>
			{
				using var reader = new StreamReader(stream);
				var isChange = false;
				try
				{
					while (await reader.ReadLineAsync(stop.Token) is { } line)
					{
						if (line == ": connected")
						{
							connected.TrySetResult();
						}

						if (line == "event: change")
						{
							isChange = true;
						}
						else if (isChange && line.StartsWith("data: ", StringComparison.Ordinal))
						{
							Changes.Enqueue(line["data: ".Length..]);
							isChange = false;
						}
					}
				}
				catch (Exception ex) when (ex is OperationCanceledException or IOException)
				{
				}
			});
		}

		public static async Task<EventStream> OpenAsync(string url, string token)
		{
			var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
			var request = new HttpRequestMessage(HttpMethod.Get, url + "/events");
			request.Headers.Authorization = new("Bearer", token);
			var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
			Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
			var connected = new TaskCompletionSource();
			var stream = new EventStream(http, await response.Content.ReadAsStreamAsync(), connected);
			await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
			return stream;
		}

		public async Task<bool> SeesAsync(string kind, TimeSpan within)
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while (clock.Elapsed < within)
			{
				if (Changes.Any(change => change.Contains($"\"{kind}\"", StringComparison.Ordinal)))
				{
					return true;
				}

				await Task.Delay(50);
			}

			return false;
		}

		public async ValueTask DisposeAsync()
		{
			await stop.CancelAsync();
			http.Dispose();
			await reading.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
		}
	}

	[Fact]
	public async Task LiveChanges_ReachBrowsersOnEveryInstance_OnlyWhereTheViewerMayRead()
	{
		await using var host = await ServerHost.StartAsync();
		await AddVaultAsync(host);
		await host.State.Access.GrantAsync(new Grant("user:bob", "lib", "read"));

		// A second instance on the same database: the browsers are connected to it, the writes go through the first.
		using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
		listener.Stop();
		var second = await ServerState.CreateAsync(host.State.Config with { Url = url });
		await using var app = ServerApp.Build(second);
		await app.StartAsync();
		try
		{
			await using var bob = await EventStream.OpenAsync(url, host.TokenFor("bob"));
			await using var alice = await EventStream.OpenAsync(url, host.TokenFor("alice"));
			await Task.Delay(ChangeFeed.PollInterval * 2);

			// A run in vault: alice hears of it, bob does not.
			await SeedAsync(host.State, "vault");
			foreach (var kind in new[] { "run", "lock", "job", "log" })
			{
				Assert.True(await alice.SeesAsync(kind, TimeSpan.FromSeconds(10)), $"alice saw no '{kind}' change: {string.Join(" ", alice.Changes)}");
			}

			await Task.Delay(ChangeFeed.PollInterval * 2);
			Assert.Empty(bob.Changes);

			// A run in lib reaches both.
			await SeedAsync(host.State, "lib");
			foreach (var kind in new[] { "run", "lock", "job", "log" })
			{
				Assert.True(await bob.SeesAsync(kind, TimeSpan.FromSeconds(10)), $"bob saw no '{kind}' change: {string.Join(" ", bob.Changes)}");
			}
		}
		finally
		{
			await app.StopAsync();
		}
	}

	[Fact]
	public async Task Metrics_CoverRequests_Git_Locks_TheMergeQueue_AndTheDatabase()
	{
		var measured = new ConcurrentBag<(string Name, double Value, Dictionary<string, object?> Tags)>();
		using var listener = new System.Diagnostics.Metrics.MeterListener();
		listener.InstrumentPublished = (instrument, meter) =>
		{
			if (instrument.Meter.Name == ServerMetrics.MeterName)
			{
				meter.EnableMeasurementEvents(instrument);
			}
		};
		void Add(System.Diagnostics.Metrics.Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
			measured.Add((instrument.Name, value, new Dictionary<string, object?>(tags.ToArray())));
		listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
		listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
		listener.Start();

		await using var host = await ServerHost.StartAsync();
		bool Has(string name, string tag, object? value) => measured.Any(m => m.Name == name && Equals(m.Tags.GetValueOrDefault(tag)?.ToString(), value?.ToString()));

		// A page, by its route.
		using var alice = Browser(host, "alice");
		Assert.True((await alice.GetAsync("/components/lib")).IsSuccessStatusCode);
		Assert.True(Has("bassia.server.request.duration", "route", "/components/{name}"));

		// Git: a clone fetches through the gateway.
		await host.CloneAsync("alice", "clone");
		Assert.True(Has("bassia.git.requests", "service", "upload-pack"));
		Assert.Contains(measured, m => m.Name == "bassia.git.bytes" && m.Tags["direction"] as string == "out" && (string?)m.Tags["service"] == "upload-pack" && m.Value > 0);

		// Locks: a decision on a table that moved is a conflict; one on the current table is committed, with its wait.
		var now = RunMetadata.Timestamp();
		var entry = new LockEntry { RunId = RunMetadata.NewRunId(), Host = "worker", Pid = 1, ProcessStart = "1", Started = now, Heartbeat = now };
		entry.Waits.Add(new LockWait(new LockKey("lib", "src/lib.c", LockKind.File), now, 1, "1"));
		var (version, _) = await host.State.Locks.ReadTableAsync();
		Assert.False(await host.State.Locks.CommitAsync(version + 1, [entry], []));
		Assert.True(await host.State.Locks.CommitAsync(version, [entry], []));
		Assert.True(Has("bassia.lock.decisions", "result", "conflict"));
		Assert.True(Has("bassia.lock.decisions", "result", "committed"));
		Assert.Contains(measured, m => m.Name == "bassia.lock.waits" && m.Value >= 1);

		// The merge queue: a landing waits for its slot and lands (here refused: the branch moved), and leaves the queue.
		var client = new ServerClient(host.Url);
		using (host.AsUser("alice"))
		{
			await Assert.ThrowsAnyAsync<Exception>(() => client.PostAsync("/land", new LandRequest([new BranchMove("lib", "main", new string('1', 40), new string('2', 40))])));
		}

		Assert.Contains(measured, m => m.Name == "bassia.merge_queue.wait");
		Assert.True(Has("bassia.merge_queue.depth", "state", "landing"));

		// The database.
		Assert.True(Has("bassia.db.duration", "operation", "transaction"));
		Assert.True(Has("bassia.db.duration", "operation", "query"));
	}
}
