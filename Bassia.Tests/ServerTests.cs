namespace Bassia.Tests;

using System.Net;
using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Core.Workspaces;
using Bassia.Server;
using Bassia.Stores;
using Tomlyn.Model;
using static Bassia.Tests.TestEnvironment;

/// <summary>How bassia-server picks its state backend: PostgreSQL only; none of these needs a database.</summary>
public sealed class ServerStateOptionTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose() => temp.Dispose();

	[Theory]
	[InlineData("memory")]
	public void AStateOtherThanPostgres_IsRefused(string state)
	{
		var error = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["-data", temp.Path, "-state", state, "-postgres", "Host=unused"]));
		Assert.Contains("PostgreSQL only", error.Message);
	}

	[Fact]
	public void StatePostgres_IsStillAccepted()
	{
		var options = ServerOptions.Parse(["-data", temp.Path, "-state", "Postgres", "-postgres", "Host=db"]);
		Assert.Equal("Host=db", options.Postgres);
	}

	[Fact]
	public void AServerWithoutAPostgresConnectionString_DoesNotStart()
	{
		var error = Assert.Throws<ArgumentException>(() => new ServerApp(new ServerOptions { Data = temp.Combine("server") }));
		Assert.Contains("-postgres", error.Message);
	}
}

/// <summary>A bassia-server in-process over the local-disk mock blob storage and PostgreSQL, and clients talking to it over HTTP.</summary>
public sealed class ServerTests : IAsyncLifetime
{
	private readonly TempDirectory temp = new();
	private ServerApp server = null!;
	private const string Token = "s3cret";

	private string Url => server.Address!;

	public async Task InitializeAsync()
	{
		server = new ServerApp(new ServerOptions { Data = temp.Combine("server"), Postgres = Backends.RequiredPostgres(), Url = "http://127.0.0.1:0", Token = Token, Chunks = new ChunkParameters(1024, 4096, 16384) });
		await server.StartAsync();
	}

	public async Task DisposeAsync()
	{
		await server.DisposeAsync();
		temp.Dispose();
	}

	/// <summary>A client's view of the server: the same core over the remote stores.</summary>
	private (Repository Repo, ILanding Landing, ServerConnection Connection) Client(Func<IObjectStore, IObjectStore>? wrap = null)
	{
		var connection = new ServerConnection(Url, Token);
		IObjectStore objects = new RemoteObjectStore(connection);
		objects = wrap?.Invoke(objects) ?? objects;
		var repo = new Repository(objects, new RemoteViewStore(connection), new RemotePathIndex(connection), new RemoteLeaseStore(connection), new ChunkParameters(1024, 4096, 16384));
		return (repo, new RemoteLanding(connection), connection);
	}

	[PostgresFact]
	public async Task ClonesWorkTogether_ThroughTheCommandLine()
	{
		await OkAsync(temp.Path, "clone", "-from", Url, "-path", "one", "-token", Token);
		await OkAsync(temp.Path, "clone", "-from", Url, "-path", "two", "-token", Token);
		var one = temp.Combine("one");
		var two = temp.Combine("two");
		await OkAsync(one, "workspace", "create", "w");
		var big = new byte[100_000];
		new Random(5).NextBytes(big);
		await File.WriteAllBytesAsync(Path.Combine(one, "workspaces", "w", "big.bin"), big);
		await File.WriteAllTextAsync(Path.Combine(one, "workspaces", "w", "a.txt"), "from one\n");
		Assert.Equal("would-land", (await OkAsync(one, "workspace", "check", "w"))["outcome"]);
		await OkAsync(one, "workspace", "land", "w");

		await OkAsync(two, "workspace", "create", "v");
		Assert.Equal(big, await File.ReadAllBytesAsync(Path.Combine(two, "workspaces", "v", "big.bin")));
		Assert.Equal("remote", (await OkAsync(two, "status"))["kind"]);
		var list = await OkAsync(one, "workspace", "list");
		Assert.Contains("v", ((TomlTableArray)list["workspace"]).Select(entry => (string)entry["name"]));
		var leased = await OkAsync(one, "lease", "acquire", "src", "-workspace", "v");
		await FailAsync(two, "lease", "acquire", "src/x", "-workspace", "v2-missing");
		Assert.NotNull(leased["token"]);
	}

	[PostgresFact]
	public async Task ASwarmOfAgentsLandingOverlappingTrees_LosesNoUpdate()
	{
		var (setupRepo, setupLanding, setupConnection) = Client();
		using var _ = setupConnection;
		var setup = new WorkspaceService(setupRepo);
		var seed = await setup.CreateAsync("seed", "setup");
		var seedTree = await setupRepo.Trees.EditAsync((await setupRepo.Objects.ReadCommitAsync(seed.Record.Working)).Tree,
			await Task.WhenAll(Enumerable.Range(0, 4).Select(async index => new PathChange($"shared/f{index}.txt", await setupRepo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes("0\n"))))));
		await setup.SetTreeAsync(seed.Id, seedTree, "seed", null, null);
		Assert.Equal(LandOutcome.Landed, (await setupLanding.LandAsync(new LandRequest(seed.Id))).Outcome);

		const int Agents = 12;
		var landed = await Task.WhenAll(Enumerable.Range(0, Agents).Select(async index =>
		{
			var (repo, landing, connection) = Client();
			using var __ = connection;
			var service = new WorkspaceService(repo);
			ChangeId? change = null;
			for (var attempt = 0; attempt < 40; attempt++)
			{
				var ws = await service.CreateAsync($"agent{index}-{attempt}", $"agent{index}", changeId: change);
				var working = await repo.Objects.ReadCommitAsync(ws.Record.Working);
				change ??= working.ChangeId;
				var tree = await repo.Trees.EditAsync(working.Tree,
				[
					new PathChange($"own/agent{index}.txt", await repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes($"{index}\n"))),
					new PathChange($"shared/f{index % 4}.txt", await repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes($"agent {index}\n")))
				]);
				await service.SetTreeAsync(ws.Id, tree, "work", null, null);
				var result = await landing.LandAsync(new LandRequest(ws.Id));
				if (result.Outcome == LandOutcome.Landed)
				{
					var commit = await repo.Objects.ReadCommitAsync(result.Commit!.Value);
					Assert.Equal(change, commit.ChangeId);
					return result.Seq!.Value;
				}

				Assert.Equal(LandOutcome.NeedsReplay, result.Outcome);
			}

			throw new InvalidOperationException("never landed");
		}));

		Assert.Equal(Agents, landed.Distinct().Count());
		var main = await setupRepo.GetMainAsync();
		Assert.Equal(1 + Agents, main.Seq);
		for (var index = 0; index < Agents; index++)
		{
			var leaf = await setupRepo.Trees.GetAsync(main.Tree, $"own/agent{index}.txt");
			Assert.Equal($"{index}\n", Encoding.UTF8.GetString(await setupRepo.Objects.ReadContentAsync(leaf!)));
		}
	}

	[PostgresFact]
	public async Task StatusOfALargeTree_ReadsNoFileBytesFromTheServer()
	{
		var (repo, landing, connection) = Client();
		using var _ = connection;
		var service = new WorkspaceService(repo);
		var seed = await service.CreateAsync("seed", "setup");
		var content = new string('x', 3000);
		var changes = new List<PathChange>();
		for (var index = 0; index < 400; index++)
		{
			changes.Add(new PathChange($"dir{index % 20}/file{index}.txt", await repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes(content + index))));
		}

		await service.SetTreeAsync(seed.Id, await repo.Trees.EditAsync((await repo.Objects.ReadCommitAsync(seed.Record.Working)).Tree, changes), "seed", null, null);
		await landing.LandAsync(new LandRequest(seed.Id));

		CountingObjectStore? counting = null;
		var (client, clientLanding, clientConnection) = Client(remote => counting = new CountingObjectStore(remote));
		using var __ = clientConnection;
		var workspaces = new WorkspaceClient(client, clientLanding, temp.Combine("state"), "tester");
		var (state, local) = await workspaces.CreateAsync("w", temp.Combine("w"));
		File.WriteAllText(Path.Combine(local.Root, "dir3", "file3.txt"), "changed");

		var bytesBefore = counting!.BytesRead;
		var snapshot = await workspaces.SnapshotAsync((await client.FindWorkspaceAsync(state.Id))!);
		var record = snapshot.Workspace.Record;
		var diff = await client.Diffs.DiffAsync((await client.Objects.ReadCommitAsync(record.Base)).Tree, (await client.Objects.ReadCommitAsync(record.Working)).Tree);
		Assert.Single(diff);
		// Only trees and the commit are read: far less than one file's content per tracked path.
		Assert.True(counting.BytesRead - bytesBefore < 3000, $"status read {counting.BytesRead - bytesBefore} bytes");
	}

	[PostgresFact]
	public async Task TheServer_ValidatesObjects_GuardsMainline_AndSignsBlobUrls()
	{
		using var http = new HttpClient { BaseAddress = new Uri(Url) };
		Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/v1/main")).StatusCode);
		http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);

		var wrong = await http.PutAsync($"api/v1/objects/{ObjectId.Of([1]).Hex}", new ByteArrayContent([2]));
		Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

		// A structural-looking object that does not decode is refused.
		var bad = ObjectCodec.Encode(Tree.Of([new TreeEntry("a", EntryKind.File, ObjectId.Of([1]), 1)]));
		bad[^1] ^= 0xFF;
		var malformed = await http.PutAsync($"api/v1/objects/{ObjectId.Of(bad).Hex}", new ByteArrayContent(bad));
		Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

		var (repo, _, connection) = Client();
		using var _ = connection;
		var main = await repo.GetMainAsync();
		await Assert.ThrowsAsync<VersionException>(async () => await repo.Views.AdvanceHeadAsync(HeadName.Main, main.Head, main.Head));

		// A large chunk travels on a signed URL straight to the blob store, and comes back by redirect.
		var chunk = new byte[400_000];
		new Random(9).NextBytes(chunk);
		var id = ObjectId.Of(chunk);
		await repo.Store.PutAsync(id, chunk);
		Assert.True(File.Exists(server.Disk!.PathOf(id)));
		Assert.Equal(chunk, (await repo.Store.ReadAsync(id))!.Value.ToArray());
		Assert.Equal(HttpStatusCode.Forbidden, (await new HttpClient().GetAsync($"{Url}/blobs/{id.Hex}?op=get&exp=9999999999&sig=00")).StatusCode);
	}

	/// <summary>Waits until the server's background reconciliation has nothing left to do.</summary>
	private async Task ReconciledAsync()
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		while (!server.Reconciling.Idle)
		{
			Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "the server's reconciliation did not settle");
			await Task.Delay(20);
		}
	}

	[PostgresFact]
	public async Task TheServer_ReconcilesSnapshotsAheadOfLanding_AndShowsTheOverviewLive()
	{
		var (repo, landing, connection) = Client();
		using var _ = connection;
		var service = new WorkspaceService(repo);
		async Task<WorkspaceState> WorkspaceAsync(string name, params (string Path, string Text)[] files)
		{
			var ws = await service.CreateAsync(name, "agent-" + name);
			var working = await repo.Objects.ReadCommitAsync(ws.Record.Working);
			var tree = await repo.Trees.EditAsync(working.Tree,
				await Task.WhenAll(files.Select(async file => new PathChange(file.Path, await repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes(file.Text))))));
			return await service.SetTreeAsync(ws.Id, tree, "snapshot", null, null);
		}

		var a = await WorkspaceAsync("a", ("shared.txt", "a\n"), ("a.txt", "a\n"));
		var b = await WorkspaceAsync("b", ("shared.txt", "b\n"));
		await ReconciledAsync();

		var overview = new RemoteConflictOverview(connection);
		var rows = (await overview.OverviewAsync()).Workspaces.ToDictionary(row => row.Name);
		Assert.True(rows["a"].Current);
		Assert.Equal(LandOutcome.WouldLand, rows["a"].Outcome);
		Assert.Equal(["shared.txt"], rows["b"].Overlaps);
		Assert.Equal("shared.txt", Assert.Single((await overview.OverviewAsync()).Overlaps).Path);

		var landed = await landing.LandAsync(new LandRequest(a.Id));
		Assert.Equal(LandOutcome.Landed, landed.Outcome);
		Assert.True(landed.Reconciled);

		// The landing made b stale; the server reconciles it on its own and the overview shows the replay it needs.
		await ReconciledAsync();
		var after = await overview.OverviewAsync();
		var row = Assert.Single(after.Workspaces);
		Assert.Equal(b.Id, row.Workspace);
		Assert.True(row.Current);
		Assert.Equal(LandOutcome.NeedsReplay, row.Outcome);
		Assert.Equal(landed.Seq, row.ReconciledSeq);
		Assert.Contains(after.HotPaths, hot => hot.Path == "shared.txt");
		Assert.Null(await server.Repo.Views.GetHeadAsync(HeadName.Candidate(a.Id)));

		// Candidates are the server's: a client may neither move nor delete one.
		var head = (await server.Repo.Views.GetHeadAsync(HeadName.Candidate(b.Id)))!.Value;
		await Assert.ThrowsAsync<VersionException>(async () => await repo.Views.AdvanceHeadAsync(HeadName.Candidate(b.Id), head, head));
		await Assert.ThrowsAsync<VersionException>(async () => await repo.Views.DeleteHeadAsync(HeadName.Candidate(b.Id), head));
	}

	[PostgresFact]
	public async Task Watch_StreamsLandingsWithTheirPrefixes()
	{
		using var http = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromSeconds(30) };
		http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
		using var response = await http.GetAsync("api/v1/watch", HttpCompletionOption.ResponseHeadersRead);
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
		Assert.StartsWith(": watching", await reader.ReadLineAsync());

		var (repo, landing, connection) = Client();
		using var _ = connection;
		var service = new WorkspaceService(repo);
		var ws = await service.CreateAsync("w", "watcher");
		var tree = await repo.Trees.EditAsync((await repo.Objects.ReadCommitAsync(ws.Record.Working)).Tree,
			[new PathChange("docs/a.md", await repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes("a\n")))]);
		await service.SetTreeAsync(ws.Id, tree, "edit", null, null);
		await landing.LandAsync(new LandRequest(ws.Id));

		string? landed = null;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		while (landed is null && await reader.ReadLineAsync(timeout.Token) is { } line)
		{
			if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("\"landed\"", StringComparison.Ordinal))
			{
				landed = line;
			}
		}

		Assert.NotNull(landed);
		Assert.Contains("\"docs\"", landed);
	}

	/// <summary>The conflict overview the removed dashboard drew is the API's, for Studio and other clients (bassia-de3319ff).</summary>
	[PostgresFact]
	public async Task TheConflictOverview_IsServedByTheApi()
	{
		await OkAsync(temp.Path, "clone", "-from", Url, "-path", "c", "-token", Token);
		var root = temp.Combine("c");
		await OkAsync(root, "agent", "start", "-name", "seed", "-land", "-run", WriteFileCommand("a.txt", "seed"));
		await OkAsync(root, "agent", "start", "-name", "late", "-run", WriteFileCommand("a.txt", "late"));
		await OkAsync(root, "agent", "start", "-name", "early", "-land", "-run", WriteFileCommand("a.txt", "early"));

		using var http = new HttpClient { BaseAddress = new Uri(Url) };
		http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
		var conflicts = await http.GetStringAsync("api/v1/conflicts");
		Assert.Contains("late", conflicts);
		Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/conflicts")).StatusCode);
	}
}
