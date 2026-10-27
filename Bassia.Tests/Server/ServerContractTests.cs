namespace Bassia.Tests.Server;

using System.Net;
using Bassia.Storage;
using Bassia.Storage.Remote;

/// <summary>
/// The server's contracts as the remote backend reaches them - records, jobs, the task cache, idempotent writes, the
/// merge queue - and the ways to log in: OIDC in the browser and with client credentials, GitHub's device flow, a
/// token; git's credential helper; and a server on the cloud storage.
/// </summary>
[Trait("Category", "Server")]
public class ServerContractTests
{
	/// <summary>A browser for <c>bassia login</c>: follows the login URL to its redirect, which completes the login.</summary>
	[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
	private static string Browser(string folder)
	{
		var script = Path.Combine(folder, "browser.sh");
		File.WriteAllText(script, "#!/bin/sh\ncurl -s -L -o /dev/null \"$1\"\n");
		File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		return script;
	}

	private static async Task<T> WithEnvironmentAsync<T>(string name, string? value, Func<Task<T>> action)
	{
		var previous = Environment.GetEnvironmentVariable(name);
		Environment.SetEnvironmentVariable(name, value);
		try
		{
			return await action();
		}
		finally
		{
			Environment.SetEnvironmentVariable(name, previous);
		}
	}

	[Fact]
	public async Task Health_Readiness_AndRequestsWithoutACredential()
	{
		await using var host = await ServerHost.StartAsync();
		using var http = new HttpClient();

		Assert.Equal("ok\n", await http.GetStringAsync($"{host.Url}/healthz"));
		Assert.Equal("ready\n", await http.GetStringAsync($"{host.Url}/readyz"));
		Assert.Equal("bassia", (await ServerClient.InfoAsync(host.Url))!.Name);

		var api = await http.GetAsync($"{host.Url}{Api.Prefix}/me");
		Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
		Assert.Contains("bassia login", await api.Content.ReadAsStringAsync());
		var forged = new HttpRequestMessage(HttpMethod.Get, $"{host.Url}{Api.Prefix}/me");
		forged.Headers.Authorization = new("Bearer", new Bassia.Server.Auth.ServerTokens(host.State.Config with { SigningKey = new string('x', 40) }).Issue("alice", TimeSpan.FromHours(1)));
		Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(forged)).StatusCode);

		// Git is asked for credentials (basic authentication, the token as the password).
		var git = await http.GetAsync($"{host.Url}/git/app/info/refs?service=git-upload-pack");
		Assert.Equal(HttpStatusCode.Unauthorized, git.StatusCode);
		Assert.Contains("Basic", git.Headers.WwwAuthenticate.ToString());
	}

	[Fact]
	public void Configuration_FromTheEnvironment_IsWhatAContainerIsGiven()
	{
		var variables = new Dictionary<string, string?>
		{
			["BASSIA_ADMINS"] = "alice, bob",
			["BASSIA_DB_HOST"] = "db.internal",
			["BASSIA_DB_USER"] = "bassia",
			["BASSIA_DB_PASSWORD"] = "pass;word",
			["BASSIA_BUCKET"] = "histories",
			["BASSIA_TABLE"] = "bassia",
			["BASSIA_REGION"] = "eu-central-1",
			["BASSIA_SERVER_SIGNING_KEY"] = new string('k', 40),
			["BASSIA_OIDC"] = """[{"name": "okta", "issuer": "https://example.okta.com", "audience": "bassia", "client_id": "cli"}]""",
			["BASSIA_GITHUB_CLIENT_ID"] = "gh-app"
		};
		var saved = variables.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
		try
		{
			foreach (var (key, value) in variables)
			{
				Environment.SetEnvironmentVariable(key, value);
			}

			var config = Bassia.Server.ServerConfig.FromEnvironment();

			Assert.Equal(["alice", "bob"], config.Admins);
			Assert.Equal("postgres", config.DatabaseProvider);
			Assert.Contains("Password=\"pass;word\"", config.DatabaseConnection);
			Assert.Equal(("cloud", "histories", "bassia", "eu-central-1"), (config.StorageKind, config.Bucket, config.Table, config.Region));
			Assert.Equal("https://example.okta.com", Assert.Single(config.Oidc).Issuer);
			Assert.Equal("gh-app", config.GitHub!.ClientId);
			Assert.Equal("http://0.0.0.0:8080", config.Url);
		}
		finally
		{
			foreach (var (key, value) in saved)
			{
				Environment.SetEnvironmentVariable(key, value);
			}
		}
	}

	// ----- logging in -----

	[Fact]
	public async Task Login_WithOidcInTheBrowser_MapsTheVerifiedEmail_AndRefreshesTheToken()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // The test's browser is a shell script.
		}

		await using var provider = await FakeIdentityProvider.StartAsync();
		await using var host = await ServerHost.StartAsync(auth: provider.ServerAuth);
		var (exit, output, error) = await WithEnvironmentAsync("BROWSER", OperatingSystem.IsWindows() ? null : Browser(host.Temp), () =>
			TestEnvironment.RunInDirectoryAsync(host.Temp, "login", "-server", host.Url, "-provider", "fake"));

		Assert.True(exit == 0, error + output);
		Assert.Contains("as 'bob'", output);
		Assert.Contains("\"devs\"", output);
		Assert.Equal("bob", (await host.State.Access.UserOfAsync(provider.Url, provider.Subject)));

		// An expired access token is refreshed with the refresh token on the next call.
		var credential = (await ServerCredentials.LoadAsync(host.Url))!;
		Assert.Equal("oidc", credential.Kind);
		await ServerCredentials.SaveAsync(credential with { Expires = DateTimeOffset.UtcNow.AddMinutes(-1) });
		Assert.Equal("bob", (await new ServerClient(host.Url).GetAsync<Me>("/me")).User);
		Assert.Equal(1, provider.Issued["refresh_token"]);
	}

	[Fact]
	public async Task Login_WithClientCredentials_TakesALinkToARegisteredUser()
	{
		await using var provider = await FakeIdentityProvider.StartAsync();
		await using var host = await ServerHost.StartAsync(auth: provider.ServerAuth);

		var (refusedExit, refused, refusedError) = await TestEnvironment.RunInDirectoryAsync(host.Temp, "login", "-server", host.Url, "-provider", "fake",
			"-client-id", "build-worker", "-client-secret", "s3cret");
		Assert.NotEqual(0, refusedExit);
		Assert.Contains("not linked", refusedError + refused);

		var (linkExit, _, linkError) = await host.RunAsync("alice", host.Temp, "access", "link", "-server", host.Url, "-provider", provider.Url, "-subject", "build-worker", "-user", "carol");
		Assert.True(linkExit == 0, linkError);
		var (exit, output, error) = await TestEnvironment.RunInDirectoryAsync(host.Temp, "login", "-server", host.Url, "-provider", "fake",
			"-client-id", "build-worker", "-client-secret", "s3cret");
		Assert.True(exit == 0, error + output);
		Assert.Contains("as 'carol'", output);
	}

	[Fact]
	public async Task Login_WithGitHub_ExchangesGitHubsTokenForTheServersOwn()
	{
		await using var provider = await FakeIdentityProvider.StartAsync();
		await using var host = await ServerHost.StartAsync(auth: provider.ServerAuth);

		var (exit, output, error) = await TestEnvironment.RunInDirectoryAsync(host.Temp, "login", "-server", host.Url, "-provider", "github");

		Assert.True(exit == 0, error + output);
		Assert.Contains("as 'bob'", output);
		var credential = (await ServerCredentials.LoadAsync(host.Url))!;
		Assert.Equal("server", credential.Kind);
		Assert.NotNull(credential.RefreshToken);
		await ServerCredentials.SaveAsync(credential with { Expires = DateTimeOffset.UtcNow.AddMinutes(-1) });
		Assert.Equal("bob", (await new ServerClient(host.Url).GetAsync<Me>("/me")).User);
		Assert.NotEqual(credential.RefreshToken, (await ServerCredentials.LoadAsync(host.Url))!.RefreshToken);

		var (logoutExit, _, _) = await TestEnvironment.RunInDirectoryAsync(host.Temp, "logout", "-server", host.Url);
		Assert.Equal(0, logoutExit);
		Assert.Null(await ServerCredentials.LoadAsync(host.Url));
	}

	[Fact]
	public async Task Credential_GivesGitTheTokenOfTheServerItAsksAbout()
	{
		await using var host = await ServerHost.StartAsync();
		var token = host.TokenFor("bob");
		var (loginExit, _, loginError) = await TestEnvironment.RunInDirectoryAsync(host.Temp, "login", "-server", host.Url, "-token", token);
		Assert.True(loginExit == 0, loginError);

		var uri = new Uri(host.Url);
		var input = Console.In;
		Console.SetIn(new StringReader($"protocol=http\nhost={uri.Host}:{uri.Port}\npath=git/app\n\n"));
		try
		{
			var (exit, output, _) = await TestEnvironment.RunInDirectoryAsync(host.Temp, "credential", "get");
			Assert.Equal(0, exit);
			Assert.Equal($"username=bassia\npassword={token}\n", output);
		}
		finally
		{
			Console.SetIn(input);
		}
	}

	// ----- the contracts over the API -----

	[Fact]
	public async Task Records_AreWrittenOncePerVersion_AndReadBack()
	{
		await using var host = await ServerHost.StartAsync();
		using var _ = host.AsUser("alice");
		var records = new RemoteRecordStore(new ServerClient(host.Url));

		Assert.Equal(-1, await records.LatestIndexAsync("integration/x"));
		Assert.Equal("integration/x/0", await records.WriteAsync("integration/x", 0, "integration.toml", "id = 0\n", "first"));
		await Assert.ThrowsAsync<StorageException>(() => records.WriteAsync("integration/x", 0, "integration.toml", "id = 0\n", "again"));
		await Assert.ThrowsAsync<StorageException>(() => records.WriteAsync("integration/x", 2, "integration.toml", "id = 2\n", "skipping"));
		Assert.Equal("integration/x/1", await records.WriteAsync("integration/x", 1, "integration.toml", "id = 1\n", "second"));

		Assert.Equal(("id = 1\n", 1), await records.ReadLatestAsync("integration/x", "integration.toml"));
		Assert.Null(await records.ReadLatestAsync("integration/y", "integration.toml"));
		Assert.Equal(["x"], await records.ListKeysAsync("integration/"));
	}

	[Fact]
	public async Task AWriteRetriedWithItsIdempotencyKey_IsAppliedOnce()
	{
		await using var host = await ServerHost.StartAsync();
		using var http = new HttpClient();
		async Task<HttpResponseMessage> PostAsync()
		{
			var request = new HttpRequestMessage(HttpMethod.Post, $"{host.Url}{Api.Prefix}/records")
			{
				Content = System.Net.Http.Json.JsonContent.Create(new RecordWrite("integration/k", 0, "integration.toml", "x = 1\n", "once"), options: Api.Json)
			};
			request.Headers.Authorization = new("Bearer", host.TokenFor("alice"));
			request.Headers.Add(Api.IdempotencyHeader, "the-same-key");
			return await http.SendAsync(request);
		}

		var first = await PostAsync();
		var retry = await PostAsync();

		Assert.Equal(HttpStatusCode.OK, first.StatusCode);
		Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
		Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
		Assert.Equal(0, await host.State.Records.LatestIndexAsync("integration/k"));
	}

	[Fact]
	public async Task Jobs_AreSeenFromAnotherClient_WithTheirOutput_AndStopOnRequest()
	{
		await using var host = await ServerHost.StartAsync();
		using var _ = host.AsUser("alice");
		var mine = new RemoteJobScheduler(new ServerClient(host.Url), new LocalJobScheduler(Path.Combine(host.Temp, "mine")));
		var theirs = new RemoteJobScheduler(new ServerClient(host.Url), new LocalJobScheduler(Path.Combine(host.Temp, "theirs")));
		var log = Path.Combine(host.Temp, "job.log");
		await File.WriteAllTextAsync(log, "first\n");

		using (var handle = mine.Attach("agent-run-quiet-fern-91ab22", "run", log, detached: true))
		{
			var job = (await theirs.FindAsync("agent-run-quiet-fern-91ab22"))!;
			Assert.True(theirs.IsAlive(job));
			Assert.Equal(Environment.MachineName, job.Host);
			Assert.Equal(["agent-run-quiet-fern-91ab22"], (await theirs.ListAsync("run")).Select(item => item.Id));

			await File.AppendAllTextAsync(log, "second\n");
			await theirs.RequestStopAsync(job.Id);
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while (!handle.Cancellation.IsCancellationRequested && clock.Elapsed < TimeSpan.FromSeconds(15))
			{
				await Task.Delay(100);
			}

			Assert.True(handle.Cancellation.IsCancellationRequested);

			// The output is uploaded with the heartbeats: the beat that saw the stop may have uploaded before "second".
			while ((await theirs.ReadLogAsync(job))!.Lines.Count < 2 && clock.Elapsed < TimeSpan.FromSeconds(15))
			{
				await Task.Delay(100);
			}

			Assert.Equal(["first", "second"], (await theirs.ReadLogAsync(job))!.Lines);
			Assert.Equal("second\n", (await theirs.ReadLogAsync(job, -7))!.Text);
		}

		var finished = (await theirs.FindAsync("agent-run-quiet-fern-91ab22"))!;
		Assert.False(theirs.IsAlive(finished));
	}

	[Fact]
	public async Task TaskCache_StoresAndRestoresThroughTheServer()
	{
		await using var host = await ServerHost.StartAsync();
		using var _ = host.AsUser("alice");
		var cache = new RemoteTaskCache(new ServerClient(host.Url), host.Temp);
		var task = new Bassia.Core.PlannedTask("lib", new Bassia.Core.ComponentTask("build", "make", [], Cache: true, Outputs: ["out/**"]));
		var built = Path.Combine(host.Temp, "built");
		Directory.CreateDirectory(Path.Combine(built, "out"));
		await File.WriteAllTextAsync(Path.Combine(built, "out", "lib.a"), "binary");
		var log = Path.Combine(host.Temp, "build.log");
		await File.WriteAllTextAsync(log, "built\n");

		Assert.Null(await cache.TryRestoreAsync("abc123", task, Path.Combine(host.Temp, "restored"), Path.Combine(host.Temp, "restored.log")));
		await cache.StoreAsync("abc123", task, new Bassia.Core.TaskResult("lib", "build", "make", 0, 1.5, log), built, []);
		Assert.Equal(1, (await cache.SizeAsync()).Entries);

		var restored = Path.Combine(host.Temp, "restored");
		Directory.CreateDirectory(restored);
		var result = await cache.TryRestoreAsync("abc123", task, restored, Path.Combine(host.Temp, "restored.log"));
		Assert.NotNull(result);
		Assert.Equal("binary", await File.ReadAllTextAsync(Path.Combine(restored, "out", "lib.a")));
		Assert.Equal("built\n", await File.ReadAllTextAsync(Path.Combine(host.Temp, "restored.log")));
	}

	[Fact]
	public async Task Landing_IsSerializedPerComponent_AndRefusesBranchesThatMoved()
	{
		await using var host = await ServerHost.StartAsync();
		using var _ = host.AsUser("alice");
		var client = new ServerClient(host.Url);
		var app = host.Depot.SourceRepo("app");
		var main = await TestEnvironment.GitAsync(app, "rev-parse", "main");
		var next = await TestEnvironment.GitAsync(app, "commit-tree", "-p", main, "-m", "next", $"{main}^{{tree}}");

		// The slot of app is taken: a landing waits for it; a stale expectation is refused, naming what moved.
		Assert.True(await host.State.Access.TakeLandingAsync("app", "someone-else", TimeSpan.FromSeconds(1)));
		var clock = System.Diagnostics.Stopwatch.StartNew();
		await client.PostAsync("/land", new LandRequest([new BranchMove("app", "main", next, main)]));
		Assert.True(clock.Elapsed > TimeSpan.FromMilliseconds(500));
		Assert.Equal(next, await TestEnvironment.GitAsync(app, "rev-parse", "main"));

		var refused = await Assert.ThrowsAsync<ServerException>(() => client.PostAsync("/land", new LandRequest([new BranchMove("app", "main", main, main)])));
		Assert.Equal(HttpStatusCode.Conflict, refused.Status);
		Assert.Contains("\"moved\"", refused.Body);
		Assert.Equal(next, await TestEnvironment.GitAsync(app, "rev-parse", "main"));
	}

	// ----- a server on the cloud storage -----

	[Fact]
	public async Task ACloudServer_StartsWithAnEmptyMetaRepo_ThatItsAdminFillsFromAClone()
	{
		await using var host = await ServerHost.StartAsync(cloud: true);
		var alice = await host.CloneAsync("alice", "alice");
		foreach (var user in new[] { "alice", "bob" })
		{
			var (userExit, _, userError) = await host.RunAsync("alice", alice, "user", "add", "-id", user, "-name", user, "-email", $"{user}@example.com");
			Assert.True(userExit == 0, userError);
		}

		var (createExit, createOutput, createError) = await host.RunAsync("alice", alice, "component", "create", "-name", "lib");
		Assert.True(createExit == 0, createError + createOutput);
		Assert.True(host.State.Storage.Exists("lib"));

		await host.RunAsync("alice", host.Temp, "access", "grant", "-server", host.Url, "-to", "bob", "-permission", "run");
		var bob = await host.CloneAsync("bob", "bob");
		Assert.Contains("name = \"lib\"", await File.ReadAllTextAsync(Path.Combine(bob, ".bassia", "components.toml")));
		Assert.True(Directory.Exists(Path.Combine(bob, "lib")));
	}
}
