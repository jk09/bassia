namespace Bassia.Tests;

using System.Diagnostics;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Stores;
using Bassia.Tests.Core;

/// <summary>
/// The workspace registry (feat record bassia-c7ba795f): its contract over memory and PostgreSQL, that it is derived
/// from the heads and rebuilds from them, and that finding workspaces no longer costs the workspaces ever kept.
/// </summary>
public sealed class WorkspaceIndexTests
{
	private static readonly ObjectId H1 = ObjectId.Of([1]);
	private static readonly ObjectId H2 = ObjectId.Of([2]);
	private static readonly ObjectId H3 = ObjectId.Of([3]);
	private static readonly ObjectId B1 = ObjectId.Of([11]);
	private static readonly ObjectId W1 = ObjectId.Of([21]);
	private static readonly ObjectId W2 = ObjectId.Of([22]);
	private static readonly ObjectId C1 = ObjectId.Of([31]);
	private static readonly ObjectId C2 = ObjectId.Of([32]);
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static WorkspaceSummary Summary(string id, string name, ObjectId head, int minute, string status = WorkspaceStatus.Open, ObjectId? working = null) =>
		new(id, name, status, "ann", null, 0, T0.AddMinutes(minute), WorkspaceStatus.IsOpen(status) ? null : T0.AddMinutes(minute + 1), head, B1, working ?? W1);

	private static Leaf Leaf(byte seed) => new(EntryKind.Chunked, ObjectId.Of([seed, 99]), 1, false);

	[Fact]
	public async Task TheRegistryInMemory_FollowsTheContract()
	{
		var views = new MemoryViewStore();
		var paths = new MemoryPathIndex();
		await ContractAsync(new MemoryWorkspaceIndex(views, paths), views, paths);
	}

	[PostgresFact]
	public async Task TheRegistryOnPostgres_FollowsTheContract()
	{
		await using var state = new PostgresState(Backends.RequiredPostgres());
		await ContractAsync(state, state, state);
	}

	private static async Task ContractAsync(IWorkspaceIndex index, IViewStore views, IPathIndex paths)
	{
		// A workspace head moves by compare-and-swap, and its row with it.
		Assert.True((await index.AdvanceAsync(null, Summary("wa000001", "alpha", H1, 1))).Success);
		Assert.Equal(H1, await views.GetHeadAsync(HeadName.Workspace("wa000001")));
		var lost = await index.AdvanceAsync(null, Summary("wa000001", "other", H2, 1));
		Assert.False(lost.Success);
		Assert.Equal(H1, lost.Current);
		Assert.Equal("alpha", Assert.Single(await index.ListAsync(true)).Name);
		Assert.True((await index.AdvanceAsync(H1, Summary("wa000001", "alpha", H2, 1))).Success);
		Assert.Equal(H2, Assert.Single(await index.ListAsync(true)).Head);

		// Open workspaces oldest first; closed ones newest first, in pages.
		await index.AdvanceAsync(null, Summary("wb000002", "beta", H1, 2));
		await index.AdvanceAsync(null, Summary("wc000003", "alpine", H1, 3));
		for (var closed = 0; closed < 5; closed++)
		{
			await index.AdvanceAsync(null, Summary($"wz00000{closed}", "old", H1, 10 + closed, WorkspaceStatus.Landed));
		}

		Assert.Equal(["alpha", "beta", "alpine"], (await index.ListAsync(true)).Select(summary => summary.Name));
		Assert.Equal(["wa000001", "wb000002"], (await index.ListAsync(true, 2)).Select(summary => summary.Id));
		var page = await index.ListAsync(false, 2);
		Assert.Equal(["wz000004", "wz000003"], page.Select(summary => summary.Id));
		Assert.Equal(["wz000002", "wz000001"], (await index.ListAsync(false, 2, page[^1].Created)).Select(summary => summary.Id));
		Assert.Equal(5, (await index.ListAsync(false)).Count);
		Assert.All(await index.ListAsync(false), summary => Assert.False(summary.IsOpen));

		// By id; by exact name (closed ones too); by prefix of an id or a name (open ones only).
		Assert.Equal(["wb000002", "wz000000"], (await index.GetAsync(["wb000002", "wz000000", "nope"])).Select(summary => summary.Id).Order());
		Assert.Equal(5, (await index.FindAsync("old")).Count);
		Assert.Equal(["alpha", "alpine"], (await index.FindAsync("alp")).Select(summary => summary.Name));
		Assert.Equal(["wb000002"], (await index.FindAsync("wb00")).Select(summary => summary.Id));
		Assert.Empty(await index.FindAsync("wz00"));
		Assert.Empty(await index.FindAsync("nothing"));

		// Without a candidate every open workspace is stale; a candidate for its commits makes it current.
		Assert.Equal(["wa000001", "wb000002", "wc000003"], await index.StaleAsync(0, ".bassia.toml"));
		Assert.True((await index.AdvanceCandidateAsync("wa000001", null, new CandidateSummary(C1, B1, W1, 0, ["src/a.txt", "src/b.txt"]))).Success);
		Assert.True((await index.AdvanceCandidateAsync("wb000002", null, new CandidateSummary(C1, B1, W1, 0, ["docs/readme.md"]))).Success);
		Assert.Equal(C1, await views.GetHeadAsync(HeadName.Candidate("wa000001")));
		Assert.Equal(C1, (await index.GetAsync(["wa000001"]))[0].Candidate);
		Assert.Equal(["wc000003"], await index.StaleAsync(0, ".bassia.toml"));
		Assert.False((await index.AdvanceCandidateAsync("wa000001", null, new CandidateSummary(C2, B1, W1, 0, []))).Success);

		// A landing that changes a touched path overtakes that workspace's candidate, and no other.
		await paths.ApplyAsync(1, [new PathChange("src/b.txt", Leaf(1)), new PathChange("lib/x.txt", Leaf(2))]);
		Assert.Equal(["wa000001", "wc000003"], await index.StaleAsync(1, ".bassia.toml"));
		Assert.Equal(["wc000003"], await index.StaleAsync(0, ".bassia.toml"));

		// A candidate at the newer seq is current again; one for other commits is not.
		Assert.True((await index.AdvanceCandidateAsync("wa000001", C1, new CandidateSummary(C2, B1, W1, 1, ["src/a.txt", "src/b.txt"]))).Success);
		Assert.Equal(["wc000003"], await index.StaleAsync(1, ".bassia.toml"));
		await index.AdvanceAsync(H2, Summary("wa000001", "alpha", H3, 1, working: W2));
		Assert.Equal(["wa000001", "wc000003"], await index.StaleAsync(1, ".bassia.toml"));
		Assert.True((await index.AdvanceCandidateAsync("wa000001", C2, new CandidateSummary(C1, B1, W2, 1, ["src/a.txt"]))).Success);

		// The policy file overtakes every candidate.
		await paths.ApplyAsync(2, [new PathChange(".bassia.toml", Leaf(3))]);
		Assert.Equal(["wa000001", "wb000002", "wc000003"], await index.StaleAsync(2, ".bassia.toml"));
		Assert.Equal(["wc000003"], await index.StaleAsync(1, ".bassia.toml"));

		// A closed workspace is no longer stale, and its paths are gone: a later landing on them concerns no one.
		await index.AdvanceAsync(H1, Summary("wb000002", "beta", H2, 2, WorkspaceStatus.Abandoned));
		Assert.DoesNotContain("wb000002", await index.StaleAsync(2, ".bassia.toml"));
		Assert.Equal(["alpha", "alpine"], (await index.ListAsync(true)).Select(summary => summary.Name));

		// A candidate is removed by compare-and-swap too.
		Assert.False((await index.AdvanceCandidateAsync("wa000001", C2, null)).Success);
		Assert.True((await index.AdvanceCandidateAsync("wa000001", C1, null)).Success);
		Assert.Null(await views.GetHeadAsync(HeadName.Candidate("wa000001")));
		Assert.Null((await index.GetAsync(["wa000001"]))[0].Candidate);

		// Retention removes head and row together, only at the expected head.
		Assert.False(await index.RemoveAsync("wz000000", H2));
		Assert.True(await index.RemoveAsync("wz000000", H1));
		Assert.Null(await views.GetHeadAsync(HeadName.Workspace("wz000000")));
		Assert.Empty(await index.GetAsync(["wz000000"]));

		// Heads moved without the registry are reported, recorded, and rows without a head pruned.
		Assert.Empty(await index.UnindexedAsync());
		await views.AdvanceHeadAsync(HeadName.Workspace("wn000009"), null, H1);
		await views.AdvanceHeadAsync(HeadName.Workspace("wc000003"), H1, H2);
		await views.AdvanceHeadAsync(HeadName.Candidate("wc000003"), null, C1);
		Assert.Equal(["rc/wc000003", "ws/wc000003", "ws/wn000009"], (await index.UnindexedAsync()).Select(item => item.Name.Value).Order(StringComparer.Ordinal));
		await index.RecordAsync(Summary("wn000009", "newcomer", H1, 30), null);
		await index.RecordAsync(Summary("wc000003", "alpine", H2, 3), new CandidateSummary(C1, B1, W1, 2, ["src/c.txt"]));
		Assert.Empty(await index.UnindexedAsync());
		Assert.DoesNotContain("wc000003", await index.StaleAsync(2, ".bassia.toml"));
		Assert.True(await views.DeleteHeadAsync(HeadName.Workspace("wn000009"), H1));
		await index.PruneAsync();
		Assert.Empty(await index.GetAsync(["wn000009"]));
	}

	/// <summary>A landed history with open, landed and abandoned workspaces, some reconciled.</summary>
	private static async Task<(Repository Repo, LandingService Landing)> PopulateAsync(Repository repo, int closed)
	{
		await repo.InitializeAsync("test");
		var landing = new LandingService(repo);
		var service = new WorkspaceService(repo);
		async Task<WorkspaceState> WorkAsync(string name, string path)
		{
			var workspace = await service.CreateAsync(name, "ann");
			var working = await repo.Objects.ReadCommitAsync(workspace.Record.Working);
			var tree = await repo.Trees.EditAsync(working.Tree, [new PathChange(path, await repo.Objects.WriteContentAsync(System.Text.Encoding.UTF8.GetBytes(name)))]);
			return await service.SetTreeAsync(workspace.Id, tree, "work", "ann", null);
		}

		for (var index = 0; index < closed; index++)
		{
			var workspace = await WorkAsync($"done-{index}", $"done/f{index}.txt");
			if (index % 2 == 0)
			{
				Assert.Equal(LandOutcome.Landed, (await landing.LandAsync(new LandRequest(workspace.Id))).Outcome);
			}
			else
			{
				await service.CloseAsync(workspace.Id, true, "ann");
			}
		}

		foreach (var (name, path) in new[] { ("feature-one", "src/one.txt"), ("feature-two", "src/two.txt"), ("fix-three", "src/three.txt") })
		{
			var workspace = await WorkAsync(name, path);
			await landing.Reconciler.ReconcileAsync(workspace.Id);
		}

		// A landing on a path feature-two touches: its candidate is overtaken.
		Assert.Equal(LandOutcome.Landed, (await landing.LandAsync(new LandRequest((await WorkAsync("lands-two", "src/two.txt")).Id))).Outcome);
		await WorkAsync("unreconciled", "src/four.txt");
		return (repo, landing);
	}

	private static async Task<string> DescribeAsync(Repository repo, LandingService landing)
	{
		var main = await repo.GetMainAsync();
		var open = (await repo.ListWorkspacesAsync()).Select(state => $"{state.Name}:{state.Record.Status}");
		var all = (await repo.ListWorkspacesAsync(true, closedLimit: 1000)).Select(state => state.Name);
		var affected = new List<string>();
		foreach (var id in await landing.Reconciler.AffectedAsync(main.Seq))
		{
			affected.Add((await repo.FindWorkspaceAsync(id))!.Name);
		}

		var overview = await landing.Reconciler.OverviewAsync();
		string Resolve(string reference)
		{
			try
			{
				return repo.ResolveWorkspaceAsync(reference).GetAwaiter().GetResult().Name;
			}
			catch (VersionException ex)
			{
				return ex.Message;
			}
		}

		return string.Join("\n",
		[
			"open: " + string.Join(",", open.Order(StringComparer.Ordinal)),
			"all: " + string.Join(",", all.Order(StringComparer.Ordinal)),
			"affected: " + string.Join(",", affected.Order(StringComparer.Ordinal)), // workspaces made in the same millisecond have no order of their own
			"overview: " + string.Join(",", overview.Workspaces.Select(row => $"{row.Name}={row.Outcome}/{row.Current}/{row.Touched}").Order(StringComparer.Ordinal)),
			"overlaps: " + string.Join(",", overview.Overlaps.Select(overlap => overlap.Path)),
			.. new[] { "feature-one", "feat", "fix-", "done-3", "done", "nope", "fea" }.Select(reference => $"{reference} -> {Resolve(reference)}")
		]);
	}

	[Fact]
	public async Task ARepositoryOnTheRegistry_AnswersWhatReadingEveryHeadAnswers()
	{
		var scanning = new TestRepo();
		var scanned = new Repository(scanning.Objects, scanning.Views, scanning.Paths, scanning.Leases, TestRepo.SmallChunks, scanning.TreeStore);
		Assert.IsType<ScanningWorkspaceIndex>(scanned.Workspaces);
		var (_, scannedLanding) = await PopulateAsync(scanned, 6);

		var indexed = new TestRepo();
		Assert.IsType<MemoryWorkspaceIndex>(indexed.Repo.Workspaces);
		var (_, indexedLanding) = await PopulateAsync(indexed.Repo, 6);

		var expected = await DescribeAsync(scanned, scannedLanding);
		Assert.Equal(expected, await DescribeAsync(indexed.Repo, indexedLanding));
		Assert.Contains("affected: feature-two,unreconciled", expected);
		Assert.Contains("feat -> 'feat' matches several workspaces", expected);
		Assert.Contains("fix- -> fix-three", expected);
		Assert.Contains("done-3 -> done-3", expected);
		Assert.Contains("fea -> No workspace 'fea'", expected);
	}

	private static async Task<List<(string, string)>> RowsAsync(string connection)
	{
		await using var source = Npgsql.NpgsqlDataSource.Create(connection);
		var rows = new List<(string, string)>();
		foreach (var sql in new[]
		{
			"SELECT id, concat_ws('|', name, status, open, owner, parent, base_seq, created, closed, head, base, working, candidate, candidate_base, candidate_working, candidate_seq) FROM bassia_workspaces ORDER BY id",
			"SELECT workspace, path FROM bassia_workspace_paths ORDER BY workspace, path"
		})
		{
			await using var command = source.CreateCommand(sql);
			await using var reader = await command.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				rows.Add((reader.GetString(0), reader.GetString(1)));
			}
		}

		return rows;
	}

	private static void Execute(string connection, string sql)
	{
		using var source = Npgsql.NpgsqlDataSource.Create(connection);
		using var command = source.CreateCommand(sql);
		command.ExecuteNonQuery();
	}

	[PostgresFact]
	public async Task TheRegistry_IsRebuiltFromTheHeads_IdenticalToTheOneKeptIncrementally()
	{
		var connection = Backends.RequiredPostgres();
		var objects = new MemoryObjectStore();
		List<(string, string)> kept;
		await using (var state = new PostgresState(connection))
		await using (var trees = new PostgresTreeStore(connection))
		{
			Assert.False(state.WorkspacesNeedBackfill);
			await PopulateAsync(new Repository(objects, state, state, state, TestRepo.SmallChunks, trees, state), 6);
			kept = await RowsAsync(connection);
			Assert.Equal(11, kept.Count(row => !row.Item2.Contains('/') || row.Item2.Contains('|')));
		}

		// As a database from before the registry looks right after the migration: the heads, and empty tables.
		Execute(connection, "DELETE FROM bassia_workspaces; DELETE FROM bassia_workspace_paths");
		await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
		{
			await using var state = new PostgresState(connection);
			await using var trees = new PostgresTreeStore(connection);
			Assert.True(await new Repository(objects, state, state, state, TestRepo.SmallChunks, trees, state).SyncWorkspaceIndexAsync() >= 0);
		})));
		Assert.Equal(kept, await RowsAsync(connection));

		await using (var state = new PostgresState(connection))
		await using (var trees = new PostgresTreeStore(connection))
		{
			var repo = new Repository(objects, state, state, state, TestRepo.SmallChunks, trees, state);
			Assert.Equal(0, await repo.SyncWorkspaceIndexAsync());

			// A head moved without its row (an older build, or a crash in a store that does not move both at once) is repaired.
			var workspace = await repo.ResolveWorkspaceAsync("feature-one");
			var old = new Repository(objects, state, state, state, TestRepo.SmallChunks, trees);
			await new WorkspaceService(old).CloseAsync(workspace.Id, true, "ann");
			Assert.Contains(await repo.ListWorkspacesAsync(), state => state.Name == "feature-one"); // the row still says open
			Assert.Equal(1, await repo.SyncWorkspaceIndexAsync());
			Assert.DoesNotContain(await repo.ListWorkspacesAsync(), state => state.Name == "feature-one");

			// Garbage collection prunes a closed workspace's row with its head.
			var before = (await state.ListAsync(false)).Count;
			var report = await new GarbageCollector(repo, objects).CollectAsync(TimeSpan.Zero, TimeSpan.Zero);
			Assert.Equal(before, report.PrunedWorkspaces);
			Assert.Empty(await state.ListAsync(false));
			Assert.Empty(await state.UnindexedAsync());
			Assert.Equal(3, (await repo.ListWorkspacesAsync()).Count);
		}
	}

	/// <summary>Counts the PostgreSQL commands issued while it lives.</summary>
	private sealed class StatementCounter : IDisposable
	{
		private readonly ActivityListener listener;
		private int count;

		public StatementCounter()
		{
			listener = new ActivityListener
			{
				ShouldListenTo = source => source.Name == "Npgsql",
				Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
				ActivityStopped = activity =>
				{
					if (activity.TagObjects.Any(tag => tag.Key is "db.query.text" or "db.statement" or "db.operation.name"))
					{
						Interlocked.Increment(ref count);
					}
				}
			};
			ActivitySource.AddActivityListener(listener);
		}

		public int Count => Volatile.Read(ref count);

		public void Dispose() => listener.Dispose();
	}

	[PostgresFact]
	public async Task FindingWorkspaces_CostsTheSame_HoweverManyAreClosed()
	{
		// None of this reads a closed workspace: the same statements and object reads with none and with sixty.
		var none = await CostsAsync(0);
		Assert.Equal(none, await CostsAsync(60));
		Assert.All(none, cost =>
		{
			Assert.InRange(cost.Statements, 1, 6);
			Assert.InRange(cost.Reads, 0, 14);
		});
		Assert.Equal(0, none.Single(cost => cost.Operation == "free name").Reads);
	}

	private static async Task<List<(string Operation, int Statements, long Reads)>> CostsAsync(int closed)
	{
		var connection = Backends.RequiredPostgres();
		var memory = new MemoryObjectStore();
		await using (var seedState = new PostgresState(connection))
		await using (var seedTrees = new PostgresTreeStore(connection))
		{
			await PopulateAsync(new Repository(memory, seedState, seedState, seedState, TestRepo.SmallChunks, seedTrees, seedState), closed);
		}

		var counting = new CountingObjectStore(memory);
		await using var state = new PostgresState(connection);
		await using var trees = new PostgresTreeStore(connection);
		var repo = new Repository(counting, state, state, state, TestRepo.SmallChunks, trees, state);
		var landing = new LandingService(repo);
		var main = await repo.GetMainAsync();
		await repo.EnsureIndexedAsync(main);

		var costs = new List<(string, int, long)>();
		async Task CostAsync(string operation, Func<Task> work)
		{
			// A fresh process each time: nothing decoded, nothing cached.
			repo.Objects.ClearCache();
			var reads = counting.Reads;
			using var statements = new StatementCounter();
			await work();
			costs.Add((operation, statements.Count, counting.Reads - reads));
		}

		await CostAsync("stale after a landing", async () => Assert.Equal(["feature-two", "unreconciled"], await NamesAsync(repo, await landing.Reconciler.AffectedAsync(main.Seq))));
		await CostAsync("list open", async () => Assert.Equal(4, (await repo.ListWorkspacesAsync()).Count));
		await CostAsync("resolve by prefix", async () => Assert.Equal("fix-three", (await repo.ResolveWorkspaceAsync("fix-")).Name));
		await CostAsync("resolve by name", async () => Assert.Equal("feature-one", (await repo.ResolveWorkspaceAsync("feature-one")).Name));
		await CostAsync("free name", async () => Assert.Null(await repo.OpenWorkspaceNamedAsync("a-free-name")));
		await CostAsync("overview", async () => Assert.Equal(4, (await landing.Reconciler.OverviewAsync()).Workspaces.Count));
		return costs;
	}

	private static async Task<List<string>> NamesAsync(Repository repo, IReadOnlyList<string> ids) =>
		(await repo.Workspaces.GetAsync(ids)).OrderBy(summary => summary.Created).Select(summary => summary.Name).ToList();
}

/// <summary>Clients of a server with the registry: one that asks it, and an older one that still lists heads.</summary>
public sealed class WorkspaceRegistryServerTests : IAsyncLifetime
{
	private readonly TempDirectory temp = new();
	private Bassia.Server.ServerApp server = null!;
	private const string Token = "s3cret";

	public async Task InitializeAsync()
	{
		server = new Bassia.Server.ServerApp(new Bassia.Server.ServerOptions { Data = temp.Combine("server"), Postgres = Backends.RequiredPostgres(), Url = "http://127.0.0.1:0", Token = Token });
		await server.StartAsync();
	}

	public async Task DisposeAsync()
	{
		await server.DisposeAsync();
		temp.Dispose();
	}

	private sealed class CountingHandler() : DelegatingHandler(new HttpClientHandler())
	{
		public int Requests;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			Interlocked.Increment(ref Requests);
			return base.SendAsync(request, ct);
		}
	}

	private static Repository Client(ServerConnection connection, bool registry) =>
		new(new CachingObjectStore(new MemoryObjectStore(), new RemoteObjectStore(connection)), new RemoteViewStore(connection), new RemotePathIndex(connection),
			new RemoteLeaseStore(connection), null, new CachingTreeStore(new MemoryTreeStore(), new RemoteTreeStore(connection)),
			registry ? new RemoteWorkspaceIndex(connection) : null);

	[PostgresFact]
	public async Task AClone_ListsAndResolvesWorkspaces_InRequestsThatDoNotGrowWithClosedWorkspaces()
	{
		var handler = new CountingHandler();
		using var connection = new ServerConnection(server.Address!, Token, handler);
		var service = new WorkspaceService(Client(connection, true));
		foreach (var name in new[] { "feature-one", "feature-two", "fix-three" })
		{
			await service.CreateAsync(name, "ann");
		}

		async Task<(int List, int Resolve, int Create)> RequestsAsync()
		{
			var repo = Client(connection, true); // a client that has cached nothing
			var before = handler.Requests;
			Assert.Equal(["feature-one", "feature-two", "fix-three"], (await repo.ListWorkspacesAsync()).Select(state => state.Name).Order());
			var listed = handler.Requests;
			Assert.Equal("fix-three", (await Client(connection, true).ResolveWorkspaceAsync("fix-")).Name);
			var resolved = handler.Requests;
			var created = await new WorkspaceService(Client(connection, true)).CreateAsync($"probe-{before}", "ann");
			var made = handler.Requests;
			await new WorkspaceService(Client(connection, true)).CloseAsync(created.Id, true, "ann");
			return (listed - before, resolved - listed, made - resolved);
		}

		var few = await RequestsAsync();
		for (var index = 0; index < 40; index++)
		{
			await service.CloseAsync((await service.CreateAsync($"done-{index}", "ann")).Id, true, "ann");
		}

		Assert.Equal(few, await RequestsAsync());
		Assert.InRange(few.List, 1, 3); // the list, then operations and views in one batch each
		Assert.InRange(few.Resolve, 1, 5);

		// Closed workspaces come in pages, newest first.
		var all = await Client(connection, true).ListWorkspacesAsync(true, closedLimit: 10);
		Assert.Equal(13, all.Count);
		Assert.Equal(10, all.Count(state => !state.IsOpen));
		var oldest = all.Where(state => !state.IsOpen).Min(state => state.Record.Created);
		var next = await Client(connection, true).ListWorkspacesAsync(true, closedLimit: 10, closedBefore: oldest);
		Assert.Empty(all.Where(state => !state.IsOpen).Select(state => state.Id).Intersect(next.Select(state => state.Id)));
		Assert.Equal(10, next.Count(state => !state.IsOpen));
	}

	[PostgresFact]
	public async Task AnOlderClient_ThatListsHeads_StillWorks_AndTheServerRecordsWhatItMoves()
	{
		using var connection = new ServerConnection(server.Address!, Token);
		var old = Client(connection, false);
		Assert.IsType<ScanningWorkspaceIndex>(old.Workspaces);
		var service = new WorkspaceService(old);
		var kept = await service.CreateAsync("old-kept", "bob");
		var closed = await service.CreateAsync("old-closed", "bob");
		await service.CloseAsync(closed.Id, true, "bob");
		Assert.Equal(["old-kept"], (await old.ListWorkspacesAsync()).Select(state => state.Name));

		// The registry knows them, though the client never told it: the server read the operations itself.
		Assert.Equal(["old-kept"], (await server.State.ListAsync(true)).Select(summary => summary.Name));
		Assert.Equal(["old-closed"], (await server.State.ListAsync(false)).Select(summary => summary.Name));
		Assert.Empty(await server.State.UnindexedAsync());
		Assert.Equal(kept.Id, (await Client(connection, true).ResolveWorkspaceAsync("old-k")).Id);

		// A head that claims to be another workspace's is refused.
		var other = await service.CreateAsync("old-other", "bob");
		await Assert.ThrowsAnyAsync<Exception>(async () => await old.Views.AdvanceHeadAsync(HeadName.Workspace(kept.Id), kept.Head, other.Head));

		// Deleting a workspace head through the API drops its row.
		Assert.True(await old.Views.DeleteHeadAsync(HeadName.Workspace(closed.Id), (await old.FindWorkspaceAsync(closed.Id))!.Head));
		Assert.Empty(await server.State.ListAsync(false));
	}
}
