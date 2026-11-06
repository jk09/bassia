namespace Bassia.Tests;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Server;
using Bassia.Stores;
using Bassia.Tests.Core;

/// <summary>
/// Trees as rows: the tree store contract over every implementation (memory, trees as blobs, PostgreSQL), and what the
/// relational store is for - walks, lookups, diffs and garbage collection that read no tree blob.
/// </summary>
public sealed class TreeStoreTests
{
	public static TheoryData<string> Kinds => ["memory", "blobs", "postgres"];

	private static ITreeStore? Create(string kind) => kind switch
	{
		"memory" => new MemoryTreeStore(),
		"blobs" => new ObjectBackedTreeStore(new MemoryObjectStore()),
		"postgres" => Backends.FreshPostgres() is { } connection ? new PostgresTreeStore(connection) : null,
		_ => throw new ArgumentException(kind)
	};

	private static (ObjectId Id, Tree Tree) Make(params TreeEntry[] entries)
	{
		var tree = Tree.Of(entries);
		return (ObjectId.Of(ObjectCodec.Encode(tree)), tree);
	}

	private static TreeEntry File(string name, string content) => new(name, EntryKind.File, ObjectId.Of(Encoding.UTF8.GetBytes(content)), content.Length);

	private static TreeEntry Dir(string name, ObjectId id) => new(name, EntryKind.Tree, id, 0);

	[Theory]
	[MemberData(nameof(Kinds))]
	public async Task Trees_RoundTrip_InCanonicalOrder_AndPutsAreIdempotent(string kind)
	{
		if (Create(kind) is not { } store)
		{
			return;
		}

		// Ordinal order differs from a collation's and from UTF-8 byte order for characters beyond the BMP.
		var tree = Make(File("b", "1"), File("B", "2"), File("é", "3"), File("\U0001F600", "4"), File("｡", "5"), new TreeEntry("run.sh", EntryKind.File, ObjectId.Of([9]), 3, true));
		var empty = Make();
		await store.PutManyAsync([(tree.Id, tree.Tree), (empty.Id, empty.Tree)]);
		await store.PutManyAsync([(tree.Id, tree.Tree)]);

		var read = await store.GetManyAsync([tree.Id, empty.Id, ObjectId.Of([0xAB])]);
		Assert.Equal(2, read.Count);
		Assert.Equal(tree.Tree.Entries, read[tree.Id].Entries);
		Assert.Empty(read[empty.Id].Entries);
		Assert.Equal(tree.Id, ObjectId.Of(ObjectCodec.Encode(read[tree.Id])));
		Assert.Equal([true, true, false], await store.HasAsync([tree.Id, empty.Id, ObjectId.Of([0xAB])]));
	}

	[Theory]
	[MemberData(nameof(Kinds))]
	public async Task PathAndSubtreeReads_AreOneCall_AndStopWhereThePathEnds(string kind)
	{
		if (Create(kind) is not { } store)
		{
			return;
		}

		var leaf = Make(File("x.txt", "x"));
		var c = Make(Dir("leaf", leaf.Id), File("z", "z"));
		var b = Make(Dir("c", c.Id), File("shared", "s"));
		var a = Make(Dir("b", b.Id), Dir("b2", b.Id));
		var root = Make(Dir("a", a.Id), File("top", "t"));
		await store.PutManyAsync([root, a, b, c, leaf]);

		var path = await store.GetPathAsync(root.Id, ["a", "b", "c"]);
		Assert.Equal(new[] { root.Id, a.Id, b.Id, c.Id }.OrderBy(id => id.Hex), path.Keys.OrderBy(id => id.Hex));

		// A file in the way, an absent name: the walk ends, nothing fails.
		Assert.Equal([root.Id], (await store.GetPathAsync(root.Id, ["top", "x"])).Keys);
		Assert.Equal([root.Id], (await store.GetPathAsync(root.Id, ["nope"])).Keys);
		Assert.Empty(await store.GetPathAsync(ObjectId.Of([7]), ["a"]));
		Assert.Single(await store.GetPathAsync(root.Id, []));

		// b is reachable twice and returned once; depth and limit bound the answer.
		Assert.Equal(5, (await store.GetSubtreesAsync(root.Id, 100, 100)).Count);
		Assert.Equal([root.Id], (await store.GetSubtreesAsync(root.Id, 0, 100)).Keys);
		Assert.Equal(2, (await store.GetSubtreesAsync(root.Id, 1, 100)).Count);
		Assert.Equal(3, (await store.GetSubtreesAsync(root.Id, 2, 100)).Count);
		Assert.Equal(2, (await store.GetSubtreesAsync(root.Id, 100, 2)).Count);
	}

	[Theory]
	[InlineData("memory")]
	[InlineData("postgres")]
	public async Task Maintenance_ReachesTreesAndLeaves_AndDeletes(string kind)
	{
		if (Create(kind) is not ITreeStore store)
		{
			return;
		}

		var maintenance = (ITreeStoreMaintenance)store;
		var kept = Make(File("k", "k"));
		var root = Make(Dir("d", kept.Id), File("f", "f"));
		var stray = Make(File("s", "s"));
		await store.PutManyAsync([root, kept, stray]);

		var reach = await maintenance.ReachAsync([root.Id]);
		Assert.Equal(new[] { root.Id, kept.Id }.OrderBy(id => id.Hex), reach.Trees.OrderBy(id => id.Hex));
		Assert.Equal(2, reach.Leaves.Count);
		Assert.Equal(3, await maintenance.ListAsync().CountAsync());

		await maintenance.DeleteAsync([stray.Id]);
		Assert.Equal([true, false], await store.HasAsync([kept.Id, stray.Id]));
		Assert.Equal(2, (await maintenance.ListAsync().ToListAsync()).Count);
		Assert.Empty((await store.GetManyAsync([stray.Id])));
	}

	public static TheoryData<string> Relational => ["memory", "postgres"];

	[Theory]
	[MemberData(nameof(Relational))]
	public async Task RelationalTrees_AreNotBlobs_AndLookupsWalksAndDiffsReadNoBlob(string kind)
	{
		if (Create(kind) is not { } trees)
		{
			return;
		}

		var blobs = new MemoryObjectStore();
		var counting = new CountingObjectStore(blobs);
		var repo = new TestRepo(counting, trees);
		await repo.Repo.InitializeAsync("test");
		var first = await repo.CommitAsync(new Dictionary<string, string?> { ["a/b/c/d/e/deep.txt"] = "deep\n", ["a/b/side.txt"] = "side\n", ["top.txt"] = "top\n" });
		var second = await repo.CommitAsync(new Dictionary<string, string?> { ["a/b/c/d/e/deep.txt"] = "deeper\n" }, "second");

		// No tree object is a blob.
		await foreach (var blob in blobs.ListAsync())
		{
			Assert.NotEqual("tree", ObjectCodec.TypeOf((await blobs.ReadAsync(blob.Id))!.Value));
		}

		var main = await repo.Repo.GetMainAsync();
		var previous = (await repo.Repo.Objects.ReadCommitAsync(main.Commit.Parents[0])).Tree;
		repo.Repo.Objects.ClearCache();
		var before = counting.Reads;

		Assert.NotNull(await repo.Repo.Trees.GetAsync(main.Tree, "a/b/c/d/e/deep.txt"));
		Assert.Null(await repo.Repo.Trees.GetAsync(main.Tree, "a/b/missing/x"));
		Assert.Equal(3, await repo.Repo.Trees.WalkAsync(main.Tree).CountAsync());
		var changes = await repo.Repo.Trees.DiffAsync(previous, main.Tree);
		Assert.Equal(["a/b/c/d/e/deep.txt"], changes.Select(change => change.Path));
		Assert.Equal(before, counting.Reads);

		Assert.Equal("deeper\n", (await repo.MainFilesAsync())["a/b/c/d/e/deep.txt"]);
		Assert.True(first.Seq < second.Seq);
	}

	[Theory]
	[MemberData(nameof(Relational))]
	public async Task GarbageCollection_MarksTreesInTheStore_AndSweepsTheUnreachableOnes(string kind)
	{
		if (Create(kind) is not { } trees)
		{
			return;
		}

		var blobs = new MemoryObjectStore();
		var repo = new TestRepo(blobs, trees);
		var clock = DateTimeOffset.UtcNow;
		repo.Repo.Clock = () => clock;
		blobs.Clock = () => clock;
		if (trees is MemoryTreeStore memory)
		{
			memory.Clock = () => clock;
		}

		await repo.Repo.InitializeAsync("test");
		await repo.CommitAsync(new Dictionary<string, string?> { ["kept/file.txt"] = "kept\n" });
		var abandoned = await repo.WorkspaceAsync("abandoned", new Dictionary<string, string?> { ["dropped/only.txt"] = "only here\n" });
		await repo.Workspaces.CloseAsync(abandoned.Id, true, null);
		var maintenance = (ITreeStoreMaintenance)trees;
		var before = await maintenance.ListAsync().CountAsync();

		var gc = new GarbageCollector(repo.Repo, blobs);
		Assert.Equal(0, (await gc.CollectAsync(TimeSpan.FromHours(1), TimeSpan.FromDays(7))).TreesSwept);

		clock += TimeSpan.FromDays(8);
		if (trees is PostgresTreeStore)
		{
			// Rows carry the database's clock; age them instead.
			clock = DateTimeOffset.UtcNow + TimeSpan.FromDays(8);
			repo.Repo.Clock = () => clock;
		}

		var dry = await gc.CollectAsync(TimeSpan.FromHours(1), TimeSpan.FromDays(7), dryRun: true);
		Assert.True(dry.TreesSwept > 0);
		Assert.Equal(before, await maintenance.ListAsync().CountAsync());

		var report = await gc.CollectAsync(TimeSpan.FromHours(1), TimeSpan.FromDays(7));
		Assert.True(report.TreesSwept > 0);
		Assert.Equal(before - report.TreesSwept, await maintenance.ListAsync().CountAsync());
		Assert.Equal("kept\n", (await repo.MainFilesAsync())["kept/file.txt"]);
		Assert.Empty((await gc.ScrubAsync()).Corrupt);
		Assert.Empty((await gc.ScrubAsync()).Unreadable);
	}

	[Fact]
	public async Task AClone_KeepsWhatItFetched_AndAsksTheServerOnlyForTheRest()
	{
		var server = new CountingTreeStore(new MemoryTreeStore());
		var leaf = Make(File("x", "x"));
		var root = Make(Dir("d", leaf.Id));
		await server.PutManyAsync([root, leaf]);
		var local = new MemoryTreeStore();
		var clone = new CachingTreeStore(local, server);

		Assert.Equal(2, (await clone.GetSubtreesAsync(root.Id, 10, 100)).Count);
		Assert.Equal(2, local.Count);
		server.Calls = 0;
		Assert.Single(await clone.GetManyAsync([leaf.Id]));
		Assert.Equal(2, (await clone.GetPathAsync(root.Id, ["d"])).Count);
		Assert.Equal(0, server.Calls);
	}
}

/// <summary>A clone needs no database of its own: a job container clones a server and runs with trees cached in memory.</summary>
public sealed class CloneWithoutPostgresTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose() => temp.Dispose();

	[PostgresFact]
	public async Task ACloneWithoutALocalPostgres_ReadsAndLandsThroughTheServersTrees()
	{
		await using var server = new ServerApp(new ServerOptions { Data = temp.Combine("server"), Postgres = Backends.RequiredPostgres(), Url = "http://127.0.0.1:0", NoAuth = true });
		await server.StartAsync();
		var saved = Environment.GetEnvironmentVariable("BASSIA_POSTGRES");
		Environment.SetEnvironmentVariable("BASSIA_POSTGRES", null);
		try
		{
			var clone = temp.Combine("clone");
			await TestEnvironment.OkAsync(temp.Path, "clone", "-from", server.Address!, "-path", clone);
			await TestEnvironment.OkAsync(clone, "workspace", "create", "w");
			await File.WriteAllTextAsync(Path.Combine(clone, "workspaces", "w", "a.txt"), "a\n");
			Assert.Equal("landed", (string)(await TestEnvironment.OkAsync(clone, "workspace", "land", "w"))["outcome"]);
			Assert.Equal("a\n", (string)(await TestEnvironment.OkAsync(clone, "mainline", "cat", "a.txt"))["content"]);
		}
		finally
		{
			Environment.SetEnvironmentVariable("BASSIA_POSTGRES", saved);
		}
	}
}
