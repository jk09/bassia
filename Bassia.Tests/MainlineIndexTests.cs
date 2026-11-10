namespace Bassia.Tests;

using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Stores;
using Bassia.Tests.Core;

/// <summary>
/// The mainline index (feat record bassia-ff9140e9): the landing at every seq, kept with the path index, so mainline
/// at a past seq is one lookup; derived from the operation log, backfilled for histories from before it.
/// </summary>
public sealed class MainlineIndexTests
{
	private static MainlineEntry Entry(long seq, byte salt = 0) => new(seq, ObjectId.Of([(byte)seq, 1, salt]), ObjectId.Of([(byte)seq, 2, salt]), ObjectId.Of([(byte)seq, 3, salt]),
		ObjectId.Of([(byte)seq, 4, salt]), seq == 0 ? null : $"w{seq}", new DateTimeOffset(2026, 1, 1, 0, 0, (int)seq, TimeSpan.Zero), seq % 2 == 0 ? null : ObjectId.Of([(byte)seq, 5]));

	[Fact]
	public async Task TheIndexInMemory_FollowsTheContract() => await ContractAsync(new MemoryPathIndex());

	[PostgresFact]
	public async Task TheIndexOnPostgres_FollowsTheContract()
	{
		await using var state = new PostgresState(Backends.RequiredPostgres());
		await ContractAsync(state);
	}

	private static async Task ContractAsync(IPathIndex index)
	{
		var leaf = new Leaf(EntryKind.Chunked, ObjectId.Of([9]), 1, false);
		Assert.Null(await index.GetEntryAsync(1));
		Assert.Equal(0, await index.EntryCountAsync());

		// A landing is recorded with its paths; applying the seq again changes neither.
		await index.ApplyAsync(1, [new PathChange("a.txt", leaf)], Entry(1));
		await index.ApplyAsync(2, [new PathChange("b.txt", leaf)], Entry(2));
		await index.ApplyAsync(2, [new PathChange("c.txt", leaf)], Entry(2, salt: 7));
		Assert.Equal(Entry(1), await index.GetEntryAsync(1));
		Assert.Equal(Entry(2), await index.GetEntryAsync(2));
		Assert.Null(await index.GetAsync("c.txt", 2));
		Assert.Equal(2, await index.IndexedSeqAsync());

		// A seq applied without its landing (an older build) leaves a gap the backfill fills; held entries stay.
		await index.ApplyAsync(3, [new PathChange("d.txt", leaf)]);
		Assert.Null(await index.GetEntryAsync(3));
		await index.RecordEntriesAsync([Entry(3), Entry(0), Entry(1, salt: 7)]);
		Assert.Equal(Entry(1), await index.GetEntryAsync(1));
		Assert.Equal(4, await index.EntryCountAsync());

		// Pages, newest first, from any seq.
		Assert.Equal([3L, 2, 1, 0], (await index.ListEntriesAsync(10, 10)).Select(entry => entry.Seq));
		Assert.Equal([2L, 1], (await index.ListEntriesAsync(2, 2)).Select(entry => entry.Seq));
		Assert.Empty(await index.ListEntriesAsync(-1, 5));
		Assert.Equal(Entry(0), (await index.ListEntriesAsync(0, 5)).Single());
	}

	/// <summary>Lands <paramref name="count"/> changes, one file each.</summary>
	private static async Task LandAsync(Repository repo, LandingService landing, int count, string prefix = "f")
	{
		var service = new WorkspaceService(repo);
		for (var index = 0; index < count; index++)
		{
			var workspace = await service.CreateAsync($"{prefix}-{index}", "ann", recipe: index % 3 == 0 ? await RecipeAsync(repo, index) : null);
			var working = await repo.Objects.ReadCommitAsync(workspace.Record.Working);
			var tree = await repo.Trees.EditAsync(working.Tree, [new PathChange($"{prefix}/{index}.txt", await repo.Objects.WriteContentAsync(System.Text.Encoding.UTF8.GetBytes($"{prefix} {index}")))]);
			await service.SetTreeAsync(workspace.Id, tree, "work", "ann", null);
			Assert.Equal(LandOutcome.Landed, (await landing.LandAsync(new LandRequest(workspace.Id))).Outcome);
		}
	}

	/// <summary>A recipe whose task spec is a stored chunk: something only the commit names, which garbage collection must keep.</summary>
	private static async Task<ReplayRecipe> RecipeAsync(Repository repo, int index)
	{
		var bytes = System.Text.Encoding.UTF8.GetBytes($"task {index}");
		var id = ObjectId.Of(bytes);
		await repo.Store.PutAsync(id, bytes);
		return new ReplayRecipe($"task {index}", "agent", null, null, id);
	}

	[Theory]
	[InlineData(8)]
	[InlineData(120)]
	public async Task MainlineAtAPastSeq_ReadsTheSame_HoweverLongTheHistory(int landings)
	{
		var counting = new CountingObjectStore(new MemoryObjectStore());
		var repo = new TestRepo(counting);
		await repo.Repo.InitializeAsync("test");
		await LandAsync(repo.Repo, repo.Landing, landings);
		Assert.Equal(landings + 1, await repo.Paths.EntryCountAsync() + 1); // seq 0 is backfilled, not applied

		foreach (var seq in new long[] { 1, landings / 2, landings - 1 })
		{
			var fresh = new Repository(counting, repo.Views, repo.Paths, repo.Leases, TestRepo.SmallChunks, repo.TreeStore, repo.Index);
			var before = counting.Reads;
			var state = await fresh.MainAtSeqAsync(seq);
			Assert.Equal(seq, state.Seq);
			// Mainline's head (operation, view, commit) and the same three at the seq: six reads at any distance.
			Assert.Equal(6, counting.Reads - before);
		}

		// What the index says is what the walk finds.
		var walked = new Repository(counting, repo.Views, new MemoryPathIndex(), repo.Leases, TestRepo.SmallChunks, repo.TreeStore, repo.Index);
		for (long seq = 0; seq <= landings; seq += Math.Max(1, landings / 5))
		{
			var (expected, actual) = (await walked.MainAtSeqAsync(seq), await repo.Repo.MainAtSeqAsync(seq));
			Assert.Equal((expected.Seq, expected.Head, expected.CommitId, expected.Tree), (actual.Seq, actual.Head, actual.CommitId, actual.Tree));
		}

		await Assert.ThrowsAsync<VersionException>(async () => await repo.Repo.MainAtSeqAsync(landings + 1));
		await Assert.ThrowsAsync<VersionException>(async () => await repo.Repo.MainAtSeqAsync(-1));
	}

	[Fact]
	public async Task TheBackfill_RecordsWhatLandingsWouldHave_AndResumes()
	{
		var repo = await TestRepo.CreateAsync();
		await LandAsync(repo.Repo, repo.Landing, 12);
		var kept = await repo.Paths.ListEntriesAsync(long.MaxValue, 100);
		Assert.Equal(Enumerable.Range(1, 12).Reverse().Select(seq => (long)seq), kept.Select(entry => entry.Seq));
		Assert.Equal(1, await repo.Repo.BackfillMainlineIndexAsync()); // seq 0
		Assert.Equal(0, await repo.Repo.BackfillMainlineIndexAsync());

		// A store that held the paths but no landings (a database from before the index): everything is backfilled, identically.
		var bare = new MemoryPathIndex();
		var replica = new Repository(repo.Objects, repo.Views, bare, repo.Leases, TestRepo.SmallChunks, repo.TreeStore, repo.Index);
		Assert.Equal(13, await replica.BackfillMainlineIndexAsync());
		Assert.Equal(await repo.Paths.ListEntriesAsync(long.MaxValue, 100), await bare.ListEntriesAsync(long.MaxValue, 100));

		// A backfill that stopped part of the way resumes below what it recorded.
		var partial = new MemoryPathIndex();
		await partial.RecordEntriesAsync(kept.Where(entry => entry.Seq >= 7).ToList());
		var resumed = new Repository(repo.Objects, repo.Views, partial, repo.Leases, TestRepo.SmallChunks, repo.TreeStore, repo.Index);
		Assert.Equal(7, await resumed.BackfillMainlineIndexAsync());
		Assert.Equal(await repo.Paths.ListEntriesAsync(long.MaxValue, 100), await partial.ListEntriesAsync(long.MaxValue, 100));
	}

	[Fact]
	public async Task GarbageCollection_MarksTheSame_FromTheIndexAndFromTheLog()
	{
		var objects = new MemoryObjectStore();
		var counting = new CountingObjectStore(objects);
		var repo = new TestRepo(counting);
		await repo.Repo.InitializeAsync("test");
		await LandAsync(repo.Repo, repo.Landing, 15);
		await repo.Repo.BackfillMainlineIndexAsync();

		// Something unreachable, so the sweep has work.
		await repo.Repo.Objects.WriteContentAsync(System.Text.Encoding.UTF8.GetBytes("garbage"));

		var fromLog = new Repository(counting, repo.Views, new MemoryPathIndex(), repo.Leases, TestRepo.SmallChunks, repo.TreeStore, repo.Index);
		var before = counting.Reads;
		var walked = await new GarbageCollector(fromLog, objects).CollectAsync(TimeSpan.Zero, TimeSpan.FromDays(1), dryRun: true);
		var walkReads = counting.Reads - before;

		var fromIndex = new Repository(counting, repo.Views, repo.Paths, repo.Leases, TestRepo.SmallChunks, repo.TreeStore, repo.Index);
		before = counting.Reads;
		var indexed = await new GarbageCollector(fromIndex, objects).CollectAsync(TimeSpan.Zero, TimeSpan.FromDays(1), dryRun: true);
		var indexReads = counting.Reads - before;

		Assert.Equal(walked, indexed);
		Assert.True(walked.Swept > 0);
		// The log walk reads every landing's operation, view and commit; the index reads none of them.
		Assert.True(walkReads - indexReads >= 3 * 15, $"walk {walkReads}, index {indexReads}");
	}

	[PostgresFact]
	public async Task OnPostgres_TheIndexIsKeptWithThePathIndex_AndRebuiltFromTheLog()
	{
		var connection = Backends.RequiredPostgres();
		var objects = new MemoryObjectStore();
		await using var state = new PostgresState(connection);
		await using var trees = new PostgresTreeStore(connection);
		var repo = new Repository(objects, state, state, state, TestRepo.SmallChunks, trees, state);
		await repo.InitializeAsync("test");
		Assert.True(state.MainlineNeedsBackfill);
		Assert.Equal(1, await repo.BackfillMainlineIndexAsync());
		Assert.False(state.MainlineNeedsBackfill);

		// Several landing services at once (as server nodes): no seq is left without its landing.
		var services = Enumerable.Range(0, 3).Select(_ => new LandingService(new Repository(objects, state, state, state, TestRepo.SmallChunks, trees, state))).ToList();
		await Task.WhenAll(services.Select((landing, index) => Task.Run(() => LandAsync(repo, landing, 6, $"n{index}"))));
		var main = await repo.GetMainAsync();
		Assert.Equal(18, main.Seq);
		Assert.Equal(await state.IndexedSeqAsync() + 1, await state.EntryCountAsync());
		var kept = await state.ListEntriesAsync(long.MaxValue, 100);
		Assert.Equal(Enumerable.Range(0, 19).Reverse().Select(seq => (long)seq), kept.Select(entry => entry.Seq));

		// Dropped, lookups still answer (by the walk), and the backfill rebuilds it identically.
		using (var source = Npgsql.NpgsqlDataSource.Create(connection))
		using (var command = source.CreateCommand("DELETE FROM bassia_mainline"))
		{
			command.ExecuteNonQuery();
		}

		Assert.Equal(5, (await repo.MainAtSeqAsync(5)).Seq);
		Assert.Equal(19, await repo.BackfillMainlineIndexAsync());
		Assert.Equal(kept, await state.ListEntriesAsync(long.MaxValue, 100));
		Assert.Equal(kept.Single(entry => entry.Seq == 5).Operation, (await repo.MainAtSeqAsync(5)).Head);
	}
}
