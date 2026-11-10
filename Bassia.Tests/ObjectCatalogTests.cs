namespace Bassia.Tests;

using System.Runtime.CompilerServices;
using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Stores;
using Bassia.Tests.Core;

/// <summary>
/// The object catalog (feat record bassia-2e6c8766): its contract in memory and on PostgreSQL, the blob store behind
/// it, and the property it exists for - an object a client was just told the store has survives the collection that
/// had already chosen it.
/// </summary>
public sealed class ObjectCatalogTests
{
	private static ObjectId Id(int index) => ObjectId.Of([(byte)index, (byte)(index >> 8), 77]);

	[Fact]
	public async Task TheCatalogInMemory_FollowsTheContract()
	{
		await ContractAsync(new MemoryObjectCatalog());
	}

	[PostgresFact]
	public async Task TheCatalogOnPostgres_FollowsTheContract()
	{
		await using var state = new PostgresState(Backends.RequiredPostgres());
		await ContractAsync(state);
	}

	private static async Task ContractAsync(IObjectCatalog catalog)
	{
		var (a, b, c, d) = (Id(1), Id(2), Id(3), Id(4));
		Assert.Equal([CatalogState.Absent, CatalogState.Absent], await catalog.TouchAsync([a, b]));
		Assert.False(await catalog.IsCompleteAsync());
		await catalog.MarkCompleteAsync();
		Assert.True(await catalog.IsCompleteAsync());

		// An announced upload is pending until it is confirmed; a stored object needs no upload.
		Assert.Empty(await catalog.BeginAsync([a, b]));
		Assert.Equal([CatalogState.Pending, CatalogState.Pending, CatalogState.Absent], await catalog.TouchAsync([a, b, c]));
		Assert.Equal(new HashSet<ObjectId> { a }, await catalog.ConfirmAsync([(a, 10)]));
		Assert.Equal([CatalogState.Stored, CatalogState.Pending], await catalog.TouchAsync([a, b]));
		Assert.Equal(new HashSet<ObjectId> { a }, await catalog.BeginAsync([a, b, c]));

		// Blobs found in the store are recorded with their own time, and never over a row that is there.
		var old = DateTimeOffset.UtcNow.AddDays(-30);
		await catalog.RecordAsync([new BlobInfo(d, 40, old), new BlobInfo(a, 999, old)]);
		var rows = await catalog.ListAsync().ToDictionaryAsync(blob => blob.Id);
		Assert.Equal(4, rows.Count);
		Assert.Equal(10, rows[a].Length);
		Assert.Equal(40, rows[d].Length);
		Assert.True(rows[d].Written < DateTimeOffset.UtcNow.AddDays(-29));
		Assert.True(rows[a].Written > DateTimeOffset.UtcNow.AddMinutes(-5));

		// A collection dooms only what was not used since its cutoff.
		var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
		await using (await catalog.SweepLockAsync(true))
		{
			Assert.Equal([d], await catalog.DoomAsync([a, b, d, Id(99)], cutoff));
			Assert.Equal([CatalogState.Stored, CatalogState.Doomed], await catalog.TouchAsync([a, d]));

			// A doomed object is not vouched for, and a confirmation does not bring it back.
			Assert.Empty(await catalog.ConfirmAsync([(d, 40)]));
			Assert.Equal([CatalogState.Doomed], await catalog.TouchAsync([d]));
			await catalog.RemoveAsync([d, a]); // only doomed rows go
		}

		Assert.Equal([CatalogState.Stored, CatalogState.Absent], await catalog.TouchAsync([a, d]));

		// Touching kept the stored object out of an earlier cutoff: "have" is use.
		await catalog.RecordAsync([new BlobInfo(c, 5, old)]); // c is pending: stays as it is
		Assert.Equal([CatalogState.Pending], await catalog.TouchAsync([c]));

		// An object left doomed by a collection that stopped half way is taken back by the next upload of it.
		await catalog.RecordAsync([new BlobInfo(d, 40, old)]);
		Assert.Equal([d], await catalog.DoomAsync([d], cutoff));
		Assert.Empty(await catalog.BeginAsync([d]));
		Assert.Equal([CatalogState.Pending], await catalog.TouchAsync([d]));
		Assert.Equal(new HashSet<ObjectId> { d }, await catalog.ConfirmAsync([(d, 40)]));
	}

	/// <summary>A blob store with the calls that would reach cloud storage counted.</summary>
	private sealed class CountingBlobs : IObjectStore, IObjectStoreMaintenance
	{
		public MemoryObjectStore Inner { get; } = new();

		public int Stats, Puts, Lists, Deletes;

		public ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
		{
			Stats += ids.Count;
			return Inner.HasAsync(ids, ct);
		}

		public ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default)
		{
			Interlocked.Increment(ref Stats);
			return Inner.StatAsync(id, ct);
		}

		public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default) => Inner.ReadAsync(id, range, ct);

		public ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
		{
			Interlocked.Increment(ref Puts);
			return Inner.PutAsync(id, bytes, ct);
		}

		public ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default) => Inner.BeginUploadAsync(id, length, ct);

		public IAsyncEnumerable<BlobInfo> ListAsync(CancellationToken ct = default)
		{
			Lists++;
			return Inner.ListAsync(ct);
		}

		public ValueTask DeleteAsync(ObjectId id, CancellationToken ct = default)
		{
			Interlocked.Increment(ref Deletes);
			return Inner.DeleteAsync(id, ct);
		}
	}

	private static (ObjectId Id, ReadOnlyMemory<byte> Bytes) Blob(string text)
	{
		var bytes = Encoding.UTF8.GetBytes(text);
		return (ObjectId.Of(bytes), bytes);
	}

	[Fact]
	public async Task BehindTheCatalog_HaveAsksNoBlobStore_AndAStoredObjectIsNotWrittenAgain()
	{
		var blobs = new CountingBlobs();
		var store = new CatalogedObjectStore(blobs, new MemoryObjectCatalog(), blobs);
		Assert.True(await store.CompleteIfEmptyAsync());
		var (one, two, three) = (Blob("one"), Blob("two"), Blob("three"));

		// A complete catalog answers for what it has never seen: nothing is asked of the blob store.
		blobs.Stats = 0;
		Assert.Equal([false, false], await store.HasAsync([one.Id, two.Id]));
		await store.PutManyAsync([one, two]);
		Assert.Equal([true, true, false], await store.HasAsync([one.Id, two.Id, three.Id]));
		Assert.Equal(0, blobs.Stats);
		Assert.Equal(2, blobs.Puts);

		// A put is the blob and one record of it; clients ask "have" first, so what is stored is not put again.
		await store.PutManyAsync([three]);
		Assert.Equal(3, blobs.Puts);
		Assert.Equal([true], await store.HasAsync([three.Id]));

		// A direct upload is announced; whether it arrived is asked once, then known.
		var direct = Blob("direct");
		await store.BeginUploadAsync(direct.Id, direct.Bytes.Length);
		Assert.Equal([false], await store.HasAsync([direct.Id]));
		Assert.Equal(1, blobs.Stats);
		await blobs.Inner.PutAsync(direct.Id, direct.Bytes);
		Assert.Equal([true], await store.HasAsync([direct.Id]));
		Assert.Equal([true], await store.HasAsync([direct.Id]));
		Assert.Equal(2, blobs.Stats);

		// Maintenance lists the catalog, not the bucket.
		blobs.Lists = 0;
		Assert.Equal(4, await store.ListAsync().CountAsync());
		Assert.Equal(0, blobs.Lists);
		Assert.Equal(direct.Bytes.Length, (await store.ListAsync().ToListAsync()).Single(blob => blob.Id == direct.Id).Length);
	}

	[Fact]
	public async Task AStoreFromBeforeTheCatalog_IsAskedForWhatIsUnknown_UntilItIsReconciled()
	{
		var blobs = new CountingBlobs();
		var (old, other, absent) = (Blob("old"), Blob("other"), Blob("absent"));
		await blobs.Inner.PutAsync(old.Id, old.Bytes);
		await blobs.Inner.PutAsync(other.Id, other.Bytes);

		var catalog = new MemoryObjectCatalog();
		var store = new CatalogedObjectStore(blobs, catalog, blobs);
		Assert.False(await store.CompleteIfEmptyAsync());

		// Nothing that exists is reported missing: the blob store is asked, and what is found is remembered.
		blobs.Stats = 0;
		Assert.Equal([true, false], await store.HasAsync([old.Id, absent.Id]));
		Assert.Equal(2, blobs.Stats);
		Assert.Equal([true, false], await store.HasAsync([old.Id, absent.Id]));
		Assert.Equal(3, blobs.Stats); // the known one is no longer asked about

		// One listing of the bucket makes the catalog complete.
		blobs.Lists = 0;
		Assert.Equal(2, await store.ReconcileAsync());
		Assert.Equal(1, blobs.Lists);
		blobs.Stats = 0;
		Assert.Equal([true, true, false], await store.HasAsync([old.Id, other.Id, absent.Id]));
		Assert.Equal(0, blobs.Stats);
		Assert.True(await new CatalogedObjectStore(blobs, catalog, blobs).CompleteIfEmptyAsync());
	}

	/// <summary>Maintenance that runs something after a collection has marked and before it sweeps: when it starts listing.</summary>
	private sealed class Interleaved(CatalogedObjectStore inner, Func<Task> betweenMarkAndSweep) : IObjectStoreMaintenance, IObjectSweeper
	{
		public async IAsyncEnumerable<BlobInfo> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
		{
			var blobs = await inner.ListAsync(ct).ToListAsync(ct);
			await betweenMarkAndSweep();
			foreach (var blob in blobs)
			{
				yield return blob;
			}
		}

		public ValueTask DeleteAsync(ObjectId id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);

		public Task<IReadOnlyList<ObjectId>> SweepAsync(IReadOnlyList<ObjectId> ids, DateTimeOffset olderThan, CancellationToken ct = default) => inner.SweepAsync(ids, olderThan, ct);
	}

	public static TheoryData<string> Catalogs => ["memory", "postgres"];

	[Theory]
	[MemberData(nameof(Catalogs))]
	public async Task AnObjectAClientWasToldIsThere_SurvivesTheCollectionThatChoseIt(string kind)
	{
		if (kind == "postgres" && Backends.Postgres is null)
		{
			return;
		}

		var connection = kind == "postgres" ? Backends.RequiredPostgres() : null;
		PostgresState? state = connection is null ? null : new PostgresState(connection);
		PostgresTreeStore? pgTrees = connection is null ? null : new PostgresTreeStore(connection);
		try
		{
			var memory = new MemoryObjectCatalog();
			IObjectCatalog catalog = (IObjectCatalog?)state ?? memory;
			var blobs = new MemoryObjectStore();
			var store = new CatalogedObjectStore(blobs, catalog, blobs);
			await store.CompleteIfEmptyAsync();
			var views = new MemoryViewStore();
			var paths = new MemoryPathIndex();
			ITreeStore trees = (ITreeStore?)pgTrees ?? new MemoryTreeStore();
			var repo = new Repository(store, views, paths, new MemoryLeaseStore(), TestRepo.SmallChunks, trees, new MemoryWorkspaceIndex(views, paths));
			await repo.InitializeAsync("test");
			var service = new WorkspaceService(repo);

			// Two files' content, uploaded long ago by a workspace that is gone: unreferenced, and older than the grace period.
			var kept = Encoding.UTF8.GetBytes("content a client is about to use again\n");
			var lost = Encoding.UTF8.GetBytes("content nobody wants\n");
			var keptLeaf = await repo.Objects.WriteContentAsync(kept);
			var lostLeaf = await repo.Objects.WriteContentAsync(lost);
			var longAgo = DateTimeOffset.UtcNow.AddDays(-3);
			var everything = await catalog.ListAsync().Select(blob => blob.Id).ToListAsync();
			await using (await catalog.SweepLockAsync(true))
			{
				// Age every row: doom and re-record with an old time is how a test reaches into the past.
				await catalog.DoomAsync(everything, DateTimeOffset.MaxValue);
				await catalog.RemoveAsync(everything);
			}

			await catalog.RecordAsync(await blobs.ListAsync().Select(blob => blob with { Written = longAgo }).ToListAsync());

			// The collection marks; then, before it sweeps, a client snapshots the same content: "have" says it is there,
			// the client uploads nothing, and its workspace's head moves to a tree that names the object.
			var client = new Repository(store, views, paths, new MemoryLeaseStore(), TestRepo.SmallChunks, trees, repo.Workspaces);
			WorkspaceState? workspace = null;
			var maintenance = new Interleaved(store, async () =>
			{
				var created = await new WorkspaceService(client).CreateAsync("late", "ann");
				var working = await client.Objects.ReadCommitAsync(created.Record.Working);
				var leaf = await client.Objects.WriteContentAsync(kept);
				Assert.Equal(keptLeaf, leaf);
				var tree = await client.Trees.EditAsync(working.Tree, [new PathChange("kept.txt", leaf)]);
				workspace = await new WorkspaceService(client).SetTreeAsync(created.Id, tree, "snapshot", "ann", null);
			});
			var report = await new GarbageCollector(repo, maintenance).CollectAsync(TimeSpan.FromHours(24), TimeSpan.FromDays(14));

			// What nobody asked about is gone; what the client was told is there is whole.
			Assert.NotNull(workspace);
			Assert.True(report.Swept >= 1);
			var fresh = new Repository(store, views, paths, new MemoryLeaseStore(), TestRepo.SmallChunks, trees, repo.Workspaces);
			var commit = await fresh.Objects.ReadCommitAsync(workspace!.Record.Working);
			var read = (await fresh.Trees.GetAsync(commit.Tree, "kept.txt"))!;
			Assert.Equal(kept, await fresh.Objects.ReadContentAsync(read));
			await Assert.ThrowsAsync<MissingObjectException>(async () => await fresh.Objects.ReadContentAsync(lostLeaf));

			// Nothing the open workspace reaches is missing, and a second collection finds nothing more to do.
			var again = await new GarbageCollector(repo, store).CollectAsync(TimeSpan.FromHours(24), TimeSpan.FromDays(14));
			Assert.Equal(0, again.Swept);
			var scrub = await new GarbageCollector(repo, store).ScrubAsync();
			Assert.Empty(scrub.Unreadable);
			Assert.Empty(scrub.Corrupt);
		}
		finally
		{
			if (state is not null)
			{
				await state.DisposeAsync();
				await pgTrees!.DisposeAsync();
			}
		}
	}

	[Theory]
	[MemberData(nameof(Catalogs))]
	public async Task ATreeAClientAskedAbout_SurvivesTheDeleteThatChoseIt(string kind)
	{
		if (kind == "postgres" && Backends.Postgres is null)
		{
			return;
		}

		PostgresTreeStore? postgres = kind == "postgres" ? new PostgresTreeStore(Backends.RequiredPostgres()) : null;
		try
		{
			ITreeStore trees = (ITreeStore?)postgres ?? new MemoryTreeStore();
			var maintenance = (ITreeStoreMaintenance)trees;
			Tree Make(string name) => new([new TreeEntry(name, EntryKind.Chunked, Id(1), 1, false)]);
			var (asked, put, idle) = (Make("asked"), Make("put"), Make("idle"));
			var ids = new[] { asked, put, idle }.Select(tree => ObjectId.Of(ObjectCodec.Encode(tree))).ToArray();
			await trees.PutManyAsync([(ids[0], asked), (ids[1], put), (ids[2], idle)]);

			// A collection chose all three (nothing reaches them); then one is asked about and one is put again.
			await Task.Delay(50);
			var cutoff = DateTimeOffset.UtcNow;
			await Task.Delay(50);
			Assert.Equal([true], await trees.HasAsync([ids[0]]));
			await trees.PutManyAsync([(ids[1], put)]);

			Assert.Equal(1, await maintenance.DeleteAsync(ids, cutoff));
			Assert.Equal([true, true, false], await trees.HasAsync(ids));
			Assert.Equal(asked.Entries, (await trees.GetManyAsync([ids[0]]))[ids[0]].Entries);
			Assert.Equal(2, await maintenance.DeleteAsync(ids)); // without a cutoff, as before
		}
		finally
		{
			if (postgres is not null)
			{
				await postgres.DisposeAsync();
			}
		}
	}
}

/// <summary>The catalog in a running server: what a snapshot asks of blob storage, and reconciling a store from before it.</summary>
public sealed class ObjectCatalogServerTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose() => temp.Dispose();

	[PostgresFact]
	public async Task ASnapshot_AsksBlobStorageOnlyToWrite_AndTheGcCommandReconcilesAnOlderStore()
	{
		var postgres = Backends.RequiredPostgres();
		var data = temp.Combine("server");
		var content = Encoding.UTF8.GetBytes("a file\n");
		ObjectId file;
		await using (var server = new Bassia.Server.ServerApp(new Bassia.Server.ServerOptions { Data = data, Postgres = postgres, Url = "http://127.0.0.1:0", Token = "t" }))
		{
			await server.StartAsync();
			Assert.True(await server.State.IsCompleteAsync()); // a new store: the catalog covers it from the start

			using var connection = new ServerConnection(server.Address!, "t");
			var remote = new RemoteObjectStore(connection);
			var repo = new Repository(remote, new RemoteViewStore(connection), new RemotePathIndex(connection), new RemoteLeaseStore(connection), null,
				new CachingTreeStore(new MemoryTreeStore(), new RemoteTreeStore(connection)), new RemoteWorkspaceIndex(connection));
			var leaf = await repo.Objects.WriteContentAsync(content);
			file = leaf.Id;
			Assert.Equal([true, false], await remote.HasAsync([file, ObjectId.Of([1, 2, 3])]));
			var rows = await ((IObjectCatalog)server.State).ListAsync().ToListAsync();
			Assert.Contains(rows, row => row.Id == file);
		}

		// As a deployment from before the catalog: the blobs, and no rows.
		using (var source = Npgsql.NpgsqlDataSource.Create(postgres))
		using (var command = source.CreateCommand("DELETE FROM bassia_objects; DELETE FROM bassia_meta WHERE key = 'objects_cataloged'"))
		{
			command.ExecuteNonQuery();
		}

		var output = new StringWriter();
		var original = Console.Out;
		Console.SetOut(output);
		try
		{
			Assert.Equal(0, await Bassia.Server.Program.Main(["gc", "-data", data, "-postgres", postgres, "-dry-run"]));
		}
		finally
		{
			Console.SetOut(original);
		}

		Assert.Contains("reconciled the object catalog with", output.ToString());
		await using var state = new PostgresState(postgres);
		Assert.True(await state.IsCompleteAsync());
		Assert.Equal([CatalogState.Stored], await state.TouchAsync([file]));
	}
}

/// <summary>An upload that meets a collection's deletes of the same object.</summary>
public sealed class ObjectCatalogSweepTests
{
	[Fact]
	public async Task AnUploadOfAnObjectBeingSwept_WaitsForTheDeletes_AndIsStoredAfterThem()
	{
		var blobs = new MemoryObjectStore();
		var catalog = new MemoryObjectCatalog();
		var store = new CatalogedObjectStore(blobs, catalog, blobs);
		await store.CompleteIfEmptyAsync();
		var bytes = System.Text.Encoding.UTF8.GetBytes("swept and uploaded at once");
		var id = ObjectId.Of(bytes);
		await store.PutAsync(id, bytes);

		// A collection has doomed the object and holds the sweep lock; its delete has not happened yet.
		var sweep = await catalog.SweepLockAsync(true);
		Assert.Equal([id], await catalog.DoomAsync([id], DateTimeOffset.MaxValue));
		Assert.Equal([false], await store.HasAsync([id])); // not vouched for: the client will upload

		var upload = Task.Run(() => store.PutAsync(id, bytes).AsTask());
		await Task.Delay(100);
		Assert.False(upload.IsCompleted); // it waits for the collection

		// The collection deletes the blob and its row, and lets go.
		await blobs.DeleteAsync(id);
		await catalog.RemoveAsync([id]);
		await sweep.DisposeAsync();

		await upload.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.Equal([true], await store.HasAsync([id]));
		Assert.True((await blobs.ReadAsync(id))!.Value.Span.SequenceEqual(bytes));
	}
}
