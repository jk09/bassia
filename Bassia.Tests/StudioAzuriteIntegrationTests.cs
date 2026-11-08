namespace Bassia.Tests;

using System.Net;
using System.Text;
using Azure.Storage.Blobs;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Server;
using Bassia.Studio;
using Bassia.Studio.Runs;
using Npgsql;
using Xunit.Abstractions;

/// <summary>
/// Bassia Studio on the Azure backend (bassia-681e8902): a <c>bassia-server</c> with its objects in Azure Blob Storage on
/// Azurite and its state in PostgreSQL (<c>BASSIA_TEST_POSTGRES</c>; skipped without it), and Studio in front of it with the local run host. Through Studio a person
/// creates and edits workspaces, lands, meets a conflict, syncs, resolves it and lands; holds a lease; starts an agentic
/// run as a job that clones the server, reports its output live and lands; and replays a run that needs it. The pages
/// show what happened, the watch stream tells Studio about each landing, and the blob container and the database hold
/// the result.
///
/// Without Azurite (<c>BASSIA_TEST_AZURE_BLOB</c>) it checks nothing, like the other cloud tests, unless
/// <c>BASSIA_TEST_REQUIRE_AZURITE</c> is set; CI sets it.
/// </summary>
[Trait("Category", "Azurite")]
public sealed class StudioAzuriteIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
	private const string Token = "studio-cloud";
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(180);
	private readonly TempDirectory temp = new();
	private readonly List<IDisposable> clients = [];

	public Task InitializeAsync() => Task.CompletedTask;

	public Task DisposeAsync()
	{
		foreach (var client in clients)
		{
			client.Dispose();
		}

		NpgsqlConnection.ClearAllPools();
		temp.Dispose();
		return Task.CompletedTask;
	}

	[PostgresFact]
	public async Task Studio_OnAzuriteAndPostgres_ManagesWorkspacesConflictsLeasesAndCloudRuns()
	{
		if (Backends.AzureBlob is not { } azure)
		{
			Assert.True(Environment.GetEnvironmentVariable("BASSIA_TEST_REQUIRE_AZURITE") is not { Length: > 0 },
				"BASSIA_TEST_REQUIRE_AZURITE is set but BASSIA_TEST_AZURE_BLOB is not: there is no Azurite to test against.");
			output.WriteLine("BASSIA_TEST_AZURE_BLOB is not set: nothing to test against.");
			return;
		}

		// ----- the server on Azurite (and PostgreSQL), Studio in front of it -----

		var container = "studio" + Guid.NewGuid().ToString("N")[..12];
		var postgres = Backends.RequiredPostgres();
		await using var server = new ServerApp(new ServerOptions
		{
			Data = temp.Combine("server"),
			Url = "http://127.0.0.1:0",
			Token = Token,
			Storage = "azure",
			AzureConnection = azure,
			AzureContainer = container,
			Postgres = postgres
		});
		await server.StartAsync();
		var options = new StudioOptions
		{
			Server = server.Address!,
			Token = Token,
			Url = "http://127.0.0.1:0",
			Cache = temp.Combine("studio-cache"),
			RunHost = "local",
			Bassia = $"\"{Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet"}\" \"{typeof(ProgramCli).Assembly.Location}\"",
			Runs = temp.Combine("runs")
		};
		await using var studio = new StudioApp(options);
		await studio.StartAsync();
		output.WriteLine($"Server {server.Address} on Azurite container '{container}' and PostgreSQL; Studio {studio.Address}.");
		using var http = new HttpClient { BaseAddress = new Uri(studio.Address!) };
		var landings = new List<long>();
		studio.Watch.Changed += change =>
		{
			if (change.Kind == "landed" && change.Seq is { } seq)
			{
				lock (landings)
				{
					landings.Add(seq);
				}
			}
		};
		await UntilAsync(() => studio.Watch.Connected, "Studio follows the server's change stream");

		StudioActions As(string user, StudioRole role = StudioRole.Contributor)
		{
			var client = new MonorepoClient(options with { Cache = temp.Combine("cache-" + user) });
			clients.Add(client);
			return new StudioActions(client, new StudioUser(user, role));
		}

		async Task<Leaf?> LeafAsync(string workspace, string path)
		{
			var state = await server.Repo.ResolveWorkspaceAsync(workspace);
			return await server.Repo.Trees.GetAsync((await server.Repo.Objects.ReadCommitAsync(state.Record.Working)).Tree, path);
		}

		async Task<string?> MainlineAsync(string path)
		{
			var main = await server.Repo.GetMainAsync();
			return await server.Repo.Trees.GetAsync(main.Tree, path) is { } leaf ? Encoding.UTF8.GetString(await server.Repo.Objects.ReadContentAsync(leaf)).TrimEnd() : null;
		}

		async Task<string> PageAsync(string path)
		{
			using var response = await http.GetAsync(path);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
		}

		// ----- workspaces: create, edit in the browser, land -----

		var ann = As("ann");
		var seed = await ann.CreateAsync("seed", "Start the service");
		await ann.SaveTextAsync(seed.Id, "service/app.txt", "version 1\n", null);
		await ann.SaveTextAsync(seed.Id, "service/notes.md", "# Notes\n", null);
		var big = new byte[200_000];
		new Random(7).NextBytes(big);
		await ann.SaveFileAsync(seed.Id, "assets/blob.bin", big, null);
		Assert.Equal(LandOutcome.Landed, (await ann.LandAsync(seed.Id)).Outcome);
		Assert.Equal("version 1", await MainlineAsync("service/app.txt"));

		// ----- a conflict: two people rewrite the same file; the second syncs, resolves and lands -----

		var bob = As("bob");
		var mine = await ann.CreateAsync("ann-change", "Version 2 by ann");
		await ann.SaveTextAsync(mine.Id, "service/app.txt", "version 2 (ann)\n", (await LeafAsync(mine.Id, "service/app.txt"))!.Id);
		var theirs = await bob.CreateAsync("bob-change", "Version 2 by bob");
		await bob.SaveTextAsync(theirs.Id, "service/app.txt", "version 2 (bob)\n", (await LeafAsync(theirs.Id, "service/app.txt"))!.Id);
		Assert.Equal(LandOutcome.Landed, (await bob.LandAsync(theirs.Id)).Outcome);
		Assert.Equal(LandOutcome.NeedsReplay, (await ann.LandAsync(mine.Id)).Outcome);
		// The conflicts page reads the server's overview, which the server reconciles in the background after a landing.
		await UntilAsync(() => server.Reconciling.Idle, "the server's reconciliation settles");
		var conflicts = await PageAsync("/conflicts");
		Assert.Contains("ann-change", conflicts);
		Assert.Contains("service/app.txt", conflicts);
		Assert.Equal(["service/app.txt"], (await ann.SyncAsync(mine.Id)).Conflicts);
		await ann.ResolveAsync(mine.Id, "service/app.txt", ConflictChoice.Merged, "version 2 (ann and bob)\n");
		Assert.Equal(LandOutcome.Landed, (await ann.LandAsync(mine.Id)).Outcome);
		Assert.Equal("version 2 (ann and bob)", await MainlineAsync("service/app.txt"));

		// ----- a lease in PostgreSQL keeps others out until released -----

		var locker = await bob.CreateAsync("locker");
		Assert.True((await bob.AcquireLeaseAsync("assets", LeaseMode.Exclusive, locker.Id, TimeSpan.FromMinutes(5))).Granted);
		var other = await ann.CreateAsync("other");
		Assert.False((await ann.AcquireLeaseAsync("assets/blob.bin", LeaseMode.Exclusive, other.Id, TimeSpan.FromMinutes(5))).Granted);
		Assert.Contains("locker", await PageAsync("/leases"));
		var lease = Assert.Single(await server.Repo.Leases.ListAsync());
		await bob.ReleaseLeaseAsync(lease.Id);
		Assert.Empty(await server.Repo.Leases.ListAsync());

		// ----- an agentic run as a job: clones the server, reports live, lands -----

		var runs = studio.Runs;
		var carol = new StudioUser("carol", StudioRole.Contributor);
		var run = await runs.StartAsync(new RunSpec(Command: "echo from the cloud job> service/job.txt && echo job output", Name: "cloud-job", Land: true), carol);
		await UntilAsync(() => run.Finished is not null, $"run {run.RunId} ends", () => string.Join('\n', run.Output));
		Assert.True(run.Status == RunStatus.Succeeded, $"{run.Status}: {run.Message}\n{string.Join('\n', run.Output)}");
		Assert.Contains("job output", run.Output);
		Assert.Equal("from the cloud job", await MainlineAsync("service/job.txt"));
		Assert.Equal("carol", (await server.Repo.ResolveWorkspaceAsync(run.WorkspaceId!)).Record.Owner);

		// ----- a run whose landing needs a replay is replayed on the new mainline with the same change -----

		var task = await runs.StartAsync(new RunSpec(Command: "echo version-3-by-the-task> service/app.txt", Name: "task"), carol);
		await UntilAsync(() => task.Finished is not null, $"run {task.RunId} ends", () => string.Join('\n', task.Output));
		Assert.True(task.Status == RunStatus.Succeeded, $"{task.Status}: {task.Message}\n{string.Join('\n', task.Output)}");
		var fix = await bob.CreateAsync("hotfix");
		await bob.SaveTextAsync(fix.Id, "service/app.txt", "version 3 (hotfix)\n", (await LeafAsync(fix.Id, "service/app.txt"))!.Id);
		Assert.Equal(LandOutcome.Landed, (await bob.LandAsync(fix.Id)).Outcome);
		var change = (await server.Repo.Objects.ReadCommitAsync((await server.Repo.ResolveWorkspaceAsync(task.WorkspaceId!)).Record.Working)).ChangeId;
		var replay = await runs.StartAsync(new RunSpec(ReplayOf: task.WorkspaceId, Land: true), carol);
		await UntilAsync(() => replay.Finished is not null, $"replay {replay.RunId} ends", () => string.Join('\n', replay.Output));
		Assert.True(replay.Status == RunStatus.Succeeded, $"{replay.Status}: {replay.Message}\n{string.Join('\n', replay.Output)}");
		Assert.Equal("version-3-by-the-task", await MainlineAsync("service/app.txt"));
		Assert.Equal(change, (await server.Repo.GetMainAsync()).Commit.ChangeId);

		// ----- what Studio shows, what the watch stream told it, what Azure and PostgreSQL hold -----

		var main = await server.Repo.GetMainAsync();
		var log = await PageAsync("/log");
		Assert.Contains("Version 2 by ann", log);
		Assert.Contains("Start the service", log);
		Assert.Contains("version-3-by-the-task", await PageAsync("/depot?path=service/app.txt"));
		Assert.Contains("version 1", await PageAsync("/depot?path=service/app.txt&seq=1"));
		Assert.Equal(big, await http.GetByteArrayAsync("/raw?path=assets/blob.bin"));
		Assert.Contains("cloud-job", await PageAsync("/runs"));
		await UntilAsync(() => { lock (landings) { return landings.Contains(main.Seq); } }, "the watch stream reported every landing");

		var blobs = new BlobContainerClient(azure, container);
		var count = 0;
		await foreach (var _ in blobs.GetBlobsAsync())
		{
			count++;
		}

		Assert.True(count > 10, $"the objects are in Azurite (found {count} blobs)");
		await using (var db = new NpgsqlConnection(postgres))
		{
			await db.OpenAsync();
			await using var heads = new NpgsqlCommand("select count(*) from bassia_heads", db);
			Assert.True((long)(await heads.ExecuteScalarAsync())! > 5, "workspace and mainline heads are in PostgreSQL");
		}

		output.WriteLine($"Mainline at seq {main.Seq}; {count} blobs in Azurite; {landings.Count} landings seen on the watch stream.");
	}

	private static async Task UntilAsync(Func<bool> condition, string what, Func<string>? detail = null)
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		while (!condition())
		{
			Assert.True(clock.Elapsed < Patience, $"Timed out: {what}. {detail?.Invoke()}");
			await Task.Delay(100);
		}
	}
}
