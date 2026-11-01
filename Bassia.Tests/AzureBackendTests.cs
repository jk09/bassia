namespace Bassia.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Storage.Blobs;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Core.Workspaces;
using Bassia.Server;
using Bassia.Stores;
using Npgsql;
using Tomlyn.Model;
using static Bassia.Tests.TestEnvironment;

/// <summary>
/// The cloud backends under test: a PostgreSQL connection string in BASSIA_TEST_POSTGRES and an Azure Blob Storage
/// connection string (Azurite) in BASSIA_TEST_AZURE_BLOB. CI starts both as service containers; elsewhere the tests that
/// need them have nothing to run on and pass without checking anything.
/// </summary>
internal static class Backends
{
	public static string? Postgres => Environment.GetEnvironmentVariable("BASSIA_TEST_POSTGRES") is { Length: > 0 } value ? value : null;

	public static string? AzureBlob => Environment.GetEnvironmentVariable("BASSIA_TEST_AZURE_BLOB") is { Length: > 0 } value ? value : null;

	/// <summary>A connection string to a new, empty schema, so every test has a database of its own.</summary>
	public static string? FreshPostgres()
	{
		if (Postgres is not { } connection)
		{
			return null;
		}

		var schema = "t" + Guid.NewGuid().ToString("N")[..12];
		using (var admin = new NpgsqlConnection(connection))
		{
			admin.Open();
			using var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin);
			create.ExecuteNonQuery();
		}

		return new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ToString();
	}

	/// <summary>An object store in a new container.</summary>
	public static AzureBlobObjectStore? FreshAzure(out string container)
	{
		container = "t" + Guid.NewGuid().ToString("N")[..12];
		if (AzureBlob is not { } connection)
		{
			return null;
		}

		var store = AzureBlobObjectStore.FromConnectionString(connection, container);
		store.CreateIfMissingAsync().GetAwaiter().GetResult();
		return store;
	}
}

/// <summary>The mutable stores' contract on PostgreSQL (the same cases as memory and SQLite, plus several nodes).</summary>
public sealed class PostgresStateTests
{
	[Fact]
	public async Task PathIndex_BatchedLookup_AndChangedSince_FollowTheContract()
	{
		if (Backends.FreshPostgres() is not { } connection)
		{
			return;
		}

		await using (var state = new PostgresState(connection))
		{
			await PathIndexContract.BatchedLookupAndChangedSinceAsync(state);
		}

		if (Backends.FreshPostgres() is { } listing)
		{
			await using var state = new PostgresState(listing);
			await PathIndexContract.ListingAsync(state);
		}

		// More paths than one statement takes (PostgresState.LookupBatch), on a fresh schema.
		if (Backends.FreshPostgres() is { } another)
		{
			await using var state = new PostgresState(another);
			await PathIndexContract.ManyPathsAsync(state, PostgresState.LookupBatch * 2 + 17);
		}
	}

	private static readonly ObjectId A = ObjectId.Of([1]);
	private static readonly ObjectId B = ObjectId.Of([2]);

	[Fact]
	public async Task HeadsPathIndexAndLeases_FollowTheContract_AcrossTwoNodes()
	{
		if (Backends.FreshPostgres() is not { } connection)
		{
			return;
		}

		await using var one = new PostgresState(connection);
		await using var two = new PostgresState(connection);

		Assert.True((await one.AdvanceHeadAsync(HeadName.Main, null, A)).Success);
		Assert.False((await two.AdvanceHeadAsync(HeadName.Main, null, B)).Success);
		var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(index =>
			(index % 2 == 0 ? one : two).AdvanceHeadAsync(HeadName.Main, A, ObjectId.Of([(byte)index, 9])).AsTask()));
		Assert.Equal(1, results.Count(result => result.Success));
		await one.AdvanceHeadAsync(HeadName.Workspace("w1"), null, B);
		Assert.Equal(["ws/w1"], (await two.ListHeadsAsync("ws/").ToListAsync()).Select(head => head.Name.Value));
		Assert.True(await two.DeleteHeadAsync(HeadName.Workspace("w1"), B));

		var leaf = new Leaf(EntryKind.File, A, 3);
		await one.ApplyAsync(1, [new PathChange("src/a.cs", leaf), new PathChange("src/sub/b.cs", leaf), new PathChange("src-other/c.cs", leaf)]);
		await two.ApplyAsync(1, [new PathChange("src/a.cs", null)]);
		await two.ApplyAsync(2, [new PathChange("src/a.cs", null)]);
		Assert.Equal(2, await one.IndexedSeqAsync());
		Assert.Null((await one.GetAsync("src/a.cs", 2))!.Leaf);
		Assert.NotNull((await one.GetAsync("src/a.cs", 1))!.Leaf);
		// Byte order: "src/" sorts before "src-other" in C collation only; the range must not leak into it.
		Assert.Equal(["src/sub"], (await one.ListAsync("src", 2).ToListAsync()).Select(item => item.Path));
		Assert.Equal(["src/a.cs"], (await two.ChangedSinceAsync("src", 1).ToListAsync()).Select(version => version.Path));

		var ttl = TimeSpan.FromMinutes(1);
		var held = (await one.AcquireAsync(new LeaseRequest("src/Billing", LeaseMode.Exclusive, "w1", ttl))).Lease!;
		Assert.False((await two.AcquireAsync(new LeaseRequest("src", LeaseMode.Exclusive, "w2", ttl))).Granted);
		var waiting = two.AcquireAsync(new LeaseRequest("src/Billing/x", LeaseMode.Exclusive, "w3", ttl, TimeSpan.FromSeconds(10))).AsTask();
		await Task.Delay(300);
		Assert.False(waiting.IsCompleted);
		await one.ReleaseAsync(held.Id, held.Token);
		var granted = (await waiting).Lease!;
		Assert.True(granted.Token > held.Token);
		Assert.NotNull(await two.RenewAsync(granted.Id, granted.Token, ttl));
		Assert.Single(await one.ListAsync("src"));
	}
}

/// <summary>The object store's contract on Azure Blob Storage (Azurite), against memory and disk.</summary>
public sealed class ObjectStoreContractTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose() => temp.Dispose();

	public static TheoryData<string> Kinds => ["memory", "disk", "azure"];

	private IObjectStore? Create(string kind) => kind switch
	{
		"memory" => new MemoryObjectStore(),
		"disk" => new DiskObjectStore(temp.Combine("blobs")),
		_ => Backends.FreshAzure(out _)
	};

	[Theory]
	[MemberData(nameof(Kinds))]
	public async Task Objects_AreStoredByIdIdempotentlyAndReadWholeOrByRange(string kind)
	{
		if (Create(kind) is not { } store)
		{
			return;
		}

		var bytes = Encoding.UTF8.GetBytes("hello, content-addressed world");
		var id = ObjectId.Of(bytes);
		Assert.Equal([false], await store.HasAsync([id]));
		Assert.Null(await store.StatAsync(id));
		Assert.Null(await store.ReadAsync(id));
		await store.PutAsync(id, bytes);
		await store.PutAsync(id, bytes);
		Assert.Equal([true, false], await store.HasAsync([id, ObjectId.Of([9])]));
		Assert.Equal(bytes.Length, (await store.StatAsync(id))!.Length);
		Assert.Equal(bytes, (await store.ReadAsync(id))!.Value.ToArray());
		Assert.Equal("content", Encoding.UTF8.GetString((await store.ReadAsync(id, new ByteRange(7, 7)))!.Value.Span));

		var maintenance = (IObjectStoreMaintenance)store;
		Assert.Contains(id, await maintenance.ListAsync().Select(blob => blob.Id).ToListAsync());
		await maintenance.DeleteAsync(id);
		Assert.Null(await store.StatAsync(id));
	}

	[Fact]
	public async Task AzureSasUrls_UploadAndDownloadDirectly()
	{
		if (Backends.FreshAzure(out _) is not { } store)
		{
			return;
		}

		var bytes = new byte[300_000];
		new Random(3).NextBytes(bytes);
		var id = ObjectId.Of(bytes);
		var ticket = await store.BeginUploadAsync(id, bytes.Length);
		using var http = new HttpClient();
		using var put = new HttpRequestMessage(HttpMethod.Put, ticket.Url) { Content = new ByteArrayContent(bytes) };
		put.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
		Assert.Equal(HttpStatusCode.Created, (await http.SendAsync(put)).StatusCode);
		Assert.Equal(bytes, await http.GetByteArrayAsync(await store.ReadUrlAsync(id)));
		Assert.Equal(bytes, (await store.ReadAsync(id))!.Value.ToArray());
	}
}

/// <summary>Counts the requests a client sends, by path.</summary>
internal sealed class CountingHandler() : DelegatingHandler(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
{
	private readonly List<string> paths = [];

	public int Count(string fragment)
	{
		lock (paths)
		{
			return paths.Count(path => path.Contains(fragment, StringComparison.Ordinal));
		}
	}

	public string Paths()
	{
		lock (paths)
		{
			return string.Join(", ", paths);
		}
	}

	public void Reset()
	{
		lock (paths)
		{
			paths.Clear();
		}
	}

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		lock (paths)
		{
			paths.Add(request.RequestUri!.AbsolutePath);
		}

		return base.SendAsync(request, cancellationToken);
	}
}

/// <summary>Client sync against a server: how many round trips a checkout costs, on the disk and on the Azure + PostgreSQL backends.</summary>
public sealed class SyncEfficiencyTests : IAsyncLifetime
{
	private readonly TempDirectory temp = new();

	public Task InitializeAsync() => Task.CompletedTask;

	public Task DisposeAsync()
	{
		NpgsqlConnection.ClearAllPools();
		temp.Dispose();
		return Task.CompletedTask;
	}

	public static TheoryData<string> Backend => ["disk", "azure"];

	private ServerOptions? Options(string backend)
	{
		var options = new ServerOptions { Data = temp.Combine("server-" + backend), Url = "http://127.0.0.1:0", Chunks = new ChunkParameters(1024, 4096, 16384) };
		if (backend == "disk")
		{
			return options;
		}

		if (Backends.FreshPostgres() is not { } postgres || Backends.AzureBlob is not { } azure)
		{
			return null;
		}

		return options with
		{
			Storage = "azure",
			AzureConnection = azure,
			AzureContainer = "t" + Guid.NewGuid().ToString("N")[..12],
			State = "postgres",
			Postgres = postgres
		};
	}

	private static (Repository Repo, ILanding Landing, ServerConnection Connection) Client(string url, string cache, CountingHandler? handler = null)
	{
		var connection = new ServerConnection(url, null, handler);
		var objects = new CachingObjectStore(new DiskObjectStore(cache), new RemoteObjectStore(connection));
		var repo = new Repository(objects, new RemoteViewStore(connection), new RemotePathIndex(connection), new RemoteLeaseStore(connection), new ChunkParameters(1024, 4096, 16384));
		return (repo, new RemoteLanding(connection), connection);
	}

	[Theory]
	[MemberData(nameof(Backend))]
	public async Task ACheckout_CostsAFewRoundTripsPerTreeLevel_AndASecondOneNone(string backend)
	{
		if (Options(backend) is not { } options)
		{
			return;
		}

		await using var server = new ServerApp(options);
		await server.StartAsync();

		// 20 folders of 10 small files and one large file: 221 files, 22 trees.
		var (setup, landing, setupConnection) = Client(server.Address!, temp.Combine("setup-cache"));
		using (setupConnection)
		{
			var service = new WorkspaceService(setup);
			var seed = await service.CreateAsync("seed", "setup");
			var changes = new List<PathChange>();
			for (var dir = 0; dir < 20; dir++)
			{
				for (var file = 0; file < 10; file++)
				{
					changes.Add(new PathChange($"d{dir}/f{file}.txt", await setup.Objects.WriteContentAsync(Encoding.UTF8.GetBytes($"file {dir}/{file}\n"))));
				}
			}

			var big = new byte[600_000];
			new Random(1).NextBytes(big);
			changes.Add(new PathChange("assets/big.bin", await setup.Objects.WriteContentAsync(big)));
			var tree = await setup.Trees.EditAsync((await setup.Objects.ReadCommitAsync(seed.Record.Working)).Tree, changes);
			await service.SetTreeAsync(seed.Id, tree, "seed", null, null);
			Assert.Equal(LandOutcome.Landed, (await landing.LandAsync(new LandRequest(seed.Id))).Outcome);
		}

		var handler = new CountingHandler();
		var (repo, clientLanding, connection) = Client(server.Address!, temp.Combine("cache"), handler);
		ObjectId mainTree;
		using (connection)
		{
			var workspaces = new WorkspaceClient(repo, clientLanding, temp.Combine("state"), "tester");
			handler.Reset();
			var (_, local) = await workspaces.CreateAsync("one", temp.Combine("one"));
			Assert.Equal("file 7/3\n", await File.ReadAllTextAsync(Path.Combine(local.Root, "d7", "f3.txt")));
			var singleReads = handler.Count("/api/v1/objects/") - handler.Count("/objects/fetch") - handler.Count("/objects/put") - handler.Count("/objects/have")
				- handler.Count("/objects/upload-urls");
			var fetches = handler.Count("/objects/fetch");
			// One by one this checkout would cost ~260 requests (22 trees, a file object, 221+ chunks).
			Assert.True(fetches + singleReads <= 16, $"checkout used {fetches} batched and {singleReads} single object requests");

			handler.Reset();
			var (_, second) = await workspaces.CreateAsync("two", temp.Combine("two"));
			Assert.Equal(new FileInfo(Path.Combine(local.Root, "assets", "big.bin")).Length, new FileInfo(Path.Combine(second.Root, "assets", "big.bin")).Length);
			Assert.Equal(0, handler.Count("/objects/fetch"));
			Assert.True(handler.Count("/api/v1/objects/") - handler.Count("/objects/put") - handler.Count("/objects/have") <= 1,
				"a second workspace reads nothing the first one fetched");

			// A snapshot uploads its trees, the view and the operation in batches.
			File.WriteAllText(Path.Combine(second.Root, "d3", "new.txt"), "new\n");
			File.WriteAllText(Path.Combine(second.Root, "d9", "f1.txt"), "changed\n");
			var two = await repo.ResolveWorkspaceAsync("two");
			handler.Reset();
			await workspaces.SnapshotAsync(two);
			Assert.True(handler.Count("/objects/put") <= 4, $"snapshot used {handler.Count("/objects/put")} batch puts");
			// Besides the batches only the amended commit goes up on its own; nothing is read.
			Assert.True(handler.Count("/objects/") - handler.Count("/objects/put") - handler.Count("/objects/have") <= 1, handler.Paths());
			mainTree = (await repo.GetMainAsync()).Tree;
		}

		// Replies are compressed for a client that accepts it.
		using var raw = new HttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{server.Address}/api/v1/objects/fetch")
		{
			Content = JsonContent(new Api.IdsRequest([mainTree]))
		};
		request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
		var reply = await raw.SendAsync(request);
		Assert.Contains("gzip", reply.Content.Headers.ContentEncoding);
	}

	private static HttpContent JsonContent(object value) =>
		new StringContent(System.Text.Json.JsonSerializer.Serialize(value, Api.Json), Encoding.UTF8, "application/json");

	[Fact]
	public async Task AServerOnAzureAndPostgres_ServesTwoClonesEndToEnd()
	{
		if (Options("azure") is not { } options)
		{
			return;
		}

		await using var server = new ServerApp(options with { Token = "secret" });
		await server.StartAsync();
		await OkAsync(temp.Path, "clone", "-from", server.Address!, "-path", "a", "-token", "secret");
		await OkAsync(temp.Path, "clone", "-from", server.Address!, "-path", "b", "-token", "secret");
		var a = temp.Combine("a");
		var b = temp.Combine("b");
		await OkAsync(a, "workspace", "create", "w");
		var big = new byte[700_000];
		new Random(8).NextBytes(big);
		await File.WriteAllBytesAsync(Path.Combine(a, "workspaces", "w", "big.bin"), big);
		await File.WriteAllTextAsync(Path.Combine(a, "workspaces", "w", "readme.md"), "azure\n");
		await OkAsync(a, "workspace", "land", "w");

		await OkAsync(b, "workspace", "create", "v");
		Assert.Equal(big, await File.ReadAllBytesAsync(Path.Combine(b, "workspaces", "v", "big.bin")));
		var log = (TomlTableArray)(await OkAsync(b, "mainline", "log"))["landing"];
		Assert.Equal(1L, log[0]["seq"]);
		var changed = (TomlTableArray)(await OkAsync(b, "mainline", "changed", "-since", "0"))["path"];
		Assert.Equal(2, changed.Count);

		var gc = await new GarbageCollector(server.Repo, server.Maintenance).CollectAsync(TimeSpan.Zero, TimeSpan.FromDays(1), dryRun: true);
		Assert.True(gc.Live > 0);
		Assert.Empty((await new GarbageCollector(server.Repo, server.Maintenance).ScrubAsync()).Corrupt);
	}
}
