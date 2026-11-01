namespace Bassia.Tests;

using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Stores;

/// <summary>The same contract over every implementation of the mutable stores: memory (reference) and SQLite.</summary>
public sealed class StoreContractTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose() => temp.Dispose();

	public static TheoryData<string> Kinds => ["memory", "sqlite"];

	private (IViewStore Views, IPathIndex Paths, ILeaseStore Leases) Create(string kind)
	{
		if (kind == "memory")
		{
			return (new MemoryViewStore(), new MemoryPathIndex(), new MemoryLeaseStore());
		}

		var state = new SqliteState(temp.Combine($"state-{Guid.NewGuid():N}.db"));
		return (state, state, state);
	}

	private static readonly ObjectId A = ObjectId.Of([1]);
	private static readonly ObjectId B = ObjectId.Of([2]);
	private static readonly ObjectId C = ObjectId.Of([3]);

	[Theory]
	[MemberData(nameof(Kinds))]
	public async Task PathIndex_BatchedLookup_MatchesSingleLookups_AndChangedSinceReadsOnlyLaterHistory(string kind) =>
		await PathIndexContract.BatchedLookupAndChangedSinceAsync(Create(kind).Paths);

	[Theory]
	[MemberData(nameof(Kinds))]
	public async Task PathIndex_BatchedLookup_KeepsOrderAcrossBatches(string kind) =>
		await PathIndexContract.ManyPathsAsync(Create(kind).Paths, 12_345);

	[Theory]
	[MemberData(nameof(Kinds))]
	public async Task Heads_MoveOnlyByCompareAndSwap(string kind)
	{
		var (views, _, _) = Create(kind);
		var main = HeadName.Main;
		Assert.Null(await views.GetHeadAsync(main));
		Assert.True((await views.AdvanceHeadAsync(main, null, A)).Success);
		var stale = await views.AdvanceHeadAsync(main, null, B);
		Assert.False(stale.Success);
		Assert.Equal(A, stale.Current);
		Assert.True((await views.AdvanceHeadAsync(main, A, B)).Success);
		Assert.False((await views.AdvanceHeadAsync(main, A, C)).Success);
		Assert.Equal(B, await views.GetHeadAsync(main));

		await views.AdvanceHeadAsync(HeadName.Workspace("w1"), null, C);
		Assert.Equal(["ws/w1"], (await views.ListHeadsAsync("ws/").ToListAsync()).Select(head => head.Name.Value));
		Assert.False(await views.DeleteHeadAsync(HeadName.Workspace("w1"), A));
		Assert.True(await views.DeleteHeadAsync(HeadName.Workspace("w1"), C));
		Assert.Empty(await views.ListHeadsAsync("ws/").ToListAsync());
	}

	[Theory]
	[MemberData(nameof(Kinds))]
	public async Task ConcurrentSwaps_ExactlyOneWinsEachRound(string kind)
	{
		var (views, _, _) = Create(kind);
		await views.AdvanceHeadAsync(HeadName.Main, null, A);
		var current = A;
		for (var round = 0; round < 5; round++)
		{
			var expected = current;
			var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
				views.AdvanceHeadAsync(HeadName.Main, expected, ObjectId.Of([(byte)round, (byte)index])).AsTask()));
			Assert.Equal(1, results.Count(result => result.Success));
			current = (await views.GetHeadAsync(HeadName.Main))!.Value;
		}
	}

	[Theory]
	[MemberData(nameof(Kinds))]
	public async Task PathIndex_KeepsVersionsBySeq(string kind)
	{
		var (_, paths, _) = Create(kind);
		var leaf = new Leaf(EntryKind.File, A, 3);
		await paths.ApplyAsync(1, [new PathChange("src/a.cs", leaf), new PathChange("src/sub/b.cs", leaf), new PathChange("README.md", leaf)]);
		await paths.ApplyAsync(2, [new PathChange("src/a.cs", new Leaf(EntryKind.File, B, 4)), new PathChange("README.md", null)]);
		await paths.ApplyAsync(2, [new PathChange("src/a.cs", null)]);
		Assert.Equal(2, await paths.IndexedSeqAsync());
		Assert.Equal(B, (await paths.GetAsync("src/a.cs", 2))!.Leaf!.Id);
		Assert.Equal(A, (await paths.GetAsync("src/a.cs", 1))!.Leaf!.Id);
		Assert.Null(await paths.GetAsync("src/a.cs", 0));
		Assert.Null((await paths.GetAsync("README.md", 2))!.Leaf);
		Assert.Equal(["src", "README.md"], (await paths.ListAsync("", 1).ToListAsync()).Select(item => item.Path));
		Assert.Equal(["src"], (await paths.ListAsync("", 2).ToListAsync()).Select(item => item.Path));
		Assert.Equal(["src/sub", "src/a.cs"], (await paths.ListAsync("src", 2).ToListAsync()).Select(item => item.Path));
		Assert.Equal(["src/a.cs"], (await paths.ChangedSinceAsync("src", 1).ToListAsync()).Select(version => version.Path));
		Assert.Equal(["README.md", "src/a.cs"], (await paths.ChangedSinceAsync("", 1).ToListAsync()).Select(version => version.Path).Order(StringComparer.Ordinal));
	}

	[Theory]
	[MemberData(nameof(Kinds))]
	public async Task Leases_ConflictByHierarchy_WithGrowingTokens(string kind)
	{
		var (_, _, leases) = Create(kind);
		var ttl = TimeSpan.FromMinutes(1);
		var billing = (await leases.AcquireAsync(new LeaseRequest("src/Billing", LeaseMode.Exclusive, "w1", ttl))).Lease!;
		var api = (await leases.AcquireAsync(new LeaseRequest("src/Api", LeaseMode.Exclusive, "w2", ttl))).Lease!;
		Assert.True(api.Token > billing.Token);
		var refused = await leases.AcquireAsync(new LeaseRequest("src", LeaseMode.Exclusive, "w3", ttl));
		Assert.False(refused.Granted);
		Assert.Equal(2, refused.Blocking.Count);
		Assert.NotNull(await leases.RenewAsync(billing.Id, billing.Token, ttl));
		Assert.Null(await leases.RenewAsync(billing.Id, billing.Token + 100, ttl));
		await leases.ReleaseAsync(billing.Id, billing.Token);
		await leases.ReleaseAsync(api.Id, api.Token);
		Assert.True((await leases.AcquireAsync(new LeaseRequest("src", LeaseMode.Exclusive, "w3", ttl))).Granted);
		Assert.Single(await leases.ListAsync("src/Api"));
		Assert.Empty(await leases.ListAsync("docs"));
	}

	[Theory]
	[MemberData(nameof(Kinds))]
	public async Task Leases_WaitInLine(string kind)
	{
		var (_, _, leases) = Create(kind);
		var held = (await leases.AcquireAsync(new LeaseRequest("hot", LeaseMode.Exclusive, "a", TimeSpan.FromMinutes(1)))).Lease!;
		var waiting = leases.AcquireAsync(new LeaseRequest("hot", LeaseMode.Exclusive, "b", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10))).AsTask();
		await Task.Delay(150);
		Assert.False(waiting.IsCompleted);
		await leases.ReleaseAsync(held.Id, held.Token);
		Assert.True((await waiting).Granted);
		Assert.False((await leases.AcquireAsync(new LeaseRequest("hot", LeaseMode.Exclusive, "c", TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(100)))).Granted);
	}
}
