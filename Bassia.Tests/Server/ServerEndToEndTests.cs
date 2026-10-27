namespace Bassia.Tests.Server;

/// <summary>
/// Two clients against one server (<see cref="ServerHost"/>): what one does - runs, their records and locks,
/// integrations, landings - the other sees, and permissions decide who sees and changes what.
/// </summary>
[Trait("Category", "Server")]
public class ServerEndToEndTests
{
	private static async Task<string> RunAsync(ServerHost host, string user, string clone, string file)
	{
		var (exit, output, error) = await host.RunAsync(user, clone, "agent", "start", "-select", "app@v0,lib@v0", "-run", TestEnvironment.WriteFileCommand($"app/{file}", file));
		Assert.True(exit == 0, error + output);
		return TestEnvironment.RunIdOf(output);
	}

	[Fact]
	public async Task RunsRecordsLocksAndIntegrations_AreSharedBetweenClones_AndLandingTakesIntegrate()
	{
		await using var host = await ServerHost.StartAsync();
		var (grantExit, _, grantError) = await host.RunAsync("alice", host.Temp, "access", "grant", "-server", host.Url, "-to", "bob", "-permission", "run");
		Assert.True(grantExit == 0, grantError);
		var alice = await host.CloneAsync("alice", "alice");
		var bob = await host.CloneAsync("bob", "bob");

		// A run in alice's clone: its result is on the server, its record and its (released) holds are bob's to see.
		var first = await RunAsync(host, "alice", alice, "a.txt");
		var tag = $"agent-run/{first["agent-run-".Length..]}/0";
		Assert.Contains("a.txt", await TestEnvironment.GitAsync(host.Depot.SourceRepo("app"), "ls-tree", "-r", "--name-only", tag));
		var (_, runs, runsError) = await host.RunAsync("bob", bob, "agent", "list");
		Assert.True(runs.Contains(first), runs + runsError);
		var (showExit, show, showError) = await host.RunAsync("bob", bob, "agent", "show", first);
		Assert.True(showExit == 0, showError);
		Assert.Contains("status = \"completed\"", show);
		var (_, locks, _) = await host.RunAsync("bob", bob, "lock", "list");
		Assert.True(locks.Contains("a.txt"), locks);

		// Bob's run and an integration of both, from bob's clone; alice sees the integration.
		var second = await RunAsync(host, "bob", bob, "b.txt");
		var (integrateExit, integration, integrateError) = await host.RunAsync("bob", bob, "integration", "start", "-runs", $"{first},{second}", "-resolve", TestEnvironment.FailingCommand);
		Assert.True(integrateExit == 0, integrateError + integration);
		var integrationId = System.Text.RegularExpressions.Regex.Match(integration, "integration_id = \"([^\"]+)\"").Groups[1].Value;
		var (_, integrations, _) = await host.RunAsync("alice", alice, "integration", "list");
		Assert.Contains(integrationId, integrations);

		// Landing on main takes integrate: refused to bob, done by alice, and main moved on the server.
		var main = await TestEnvironment.GitAsync(host.Depot.SourceRepo("app"), "rev-parse", "main");
		var (bobAdvance, bobOutput, bobError) = await host.RunAsync("bob", bob, "integration", "advance", integrationId);
		Assert.NotEqual(0, bobAdvance);
		Assert.Contains("may not integrate", bobError + bobOutput);
		Assert.Equal(main, await TestEnvironment.GitAsync(host.Depot.SourceRepo("app"), "rev-parse", "main"));
		var (aliceAdvance, aliceOutput, aliceError) = await host.RunAsync("alice", alice, "integration", "advance", integrationId);
		Assert.True(aliceAdvance == 0, aliceError + aliceOutput);
		Assert.Equal("README.md\na.txt\nb.txt\ndocs/guide.md\nsrc/main.c",
			(await TestEnvironment.GitAsync(host.Depot.SourceRepo("app"), "ls-tree", "-r", "--name-only", "main")).Replace("\r", ""));
		Assert.Equal(await TestEnvironment.GitAsync(host.Depot.SourceRepo("app"), "rev-parse", "main"),
			await TestEnvironment.GitAsync(System.IO.Path.Combine(alice, "app"), "rev-parse", "main"));

		var (_, audit, _) = await host.RunAsync("alice", alice, "audit");
		Assert.Contains("land", audit);
		Assert.Contains("git.push", audit);
	}

	[Fact]
	public async Task AUserWithoutRead_SeesNothingOfAComponent_InTheRegistryGitRunsOrViews()
	{
		await using var host = await ServerHost.StartAsync();
		await host.RunAsync("alice", host.Temp, "access", "grant", "-server", host.Url, "-to", "carol", "-permission", "read", "-component", "lib");
		var alice = await host.CloneAsync("alice", "alice");
		var run = await RunAsync(host, "alice", alice, "a.txt");

		var carol = await host.CloneAsync("carol", "carol");
		var registry = await File.ReadAllTextAsync(Path.Combine(carol, ".bassia", "components.toml"));
		Assert.Contains("name = \"lib\"", registry);
		Assert.DoesNotContain("name = \"app\"", registry);
		Assert.True(Directory.Exists(Path.Combine(carol, "lib")));
		Assert.False(Directory.Exists(Path.Combine(carol, "app")));

		var (_, runs, _) = await host.RunAsync("carol", carol, "agent", "list");
		Assert.DoesNotContain(run, runs);
		var (_, locks, _) = await host.RunAsync("carol", carol, "lock", "list");
		Assert.DoesNotContain("a.txt", locks);

		using (ServerHost.As(host.TokenFor("carol")))
		{
			var clone = await Bassia.Git.GitClient.In(host.Temp).RunAsync(["clone", "--quiet", $"{host.Url}/git/app", Path.Combine(host.Temp, "carol-app")]);
			Assert.NotEqual(0, clone.ExitCode);
		}

		var spec = Path.Combine(host.Temp, "app.spec");
		await File.WriteAllTextAsync(spec, "map app\n");
		var (viewExit, viewOutput, viewError) = await host.RunAsync("carol", carol, "view", "create", "-name", "app", "-spec", spec);
		Assert.NotEqual(0, viewExit);
		Assert.Contains("app", viewError + viewOutput);
		Assert.False(Directory.Exists(Path.Combine(carol, ".workspace", "view-app")));
	}

	[Fact]
	public async Task AWorkerToken_RunsOnItsComponentsOnly()
	{
		await using var host = await ServerHost.StartAsync();
		var alice = await host.CloneAsync("alice", "alice");
		var (tokenExit, tokenOutput, tokenError) = await host.RunAsync("alice", alice, "token", "create", "-components", "app", "-minutes", "30");
		Assert.True(tokenExit == 0, tokenError);
		var token = System.Text.RegularExpressions.Regex.Match(tokenOutput, "token = \"([^\"]+)\"").Groups[1].Value;

		using (ServerHost.As(token))
		{
			var git = Bassia.Git.GitClient.In(host.Temp);
			Assert.NotEqual(0, (await git.RunAsync(["clone", "--quiet", $"{host.Url}/git/lib", Path.Combine(host.Temp, "worker-lib")])).ExitCode);
			var app = Path.Combine(host.Temp, "worker-app");
			Assert.Equal(0, (await git.RunAsync(["clone", "--quiet", $"{host.Url}/git/app", app])).ExitCode);
			await File.WriteAllTextAsync(Path.Combine(app, "w.txt"), "w");
			await TestEnvironment.GitAsync(app, "add", "--all");
			await TestEnvironment.GitAsync(app, "commit", "--quiet", "-m", "worker");

			// A run branch is the worker's to push; the default branch is not.
			Assert.Equal(0, (await Bassia.Git.GitClient.In(app).RunAsync(["push", "--quiet", "origin", "HEAD:refs/heads/worker-run"])).ExitCode);
			var main = await Bassia.Git.GitClient.In(app).RunAsync(["push", "--quiet", "origin", "HEAD:refs/heads/main"]);
			Assert.NotEqual(0, main.ExitCode);
			Assert.Contains("integrate", main.Error);

			var client = new Bassia.Storage.Remote.ServerClient(host.Url);
			var refused = await Assert.ThrowsAsync<Bassia.Storage.Remote.ServerException>(() =>
				client.PostAsync<Bassia.Storage.Remote.TokenResponse>("/tokens/worker", new Bassia.Storage.Remote.WorkerTokenRequest(["app"], 10)));
			Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.Status);
		}

		Assert.NotNull(await TestEnvironment.GitAsync(host.Depot.SourceRepo("app"), "rev-parse", "worker-run"));
	}
}
