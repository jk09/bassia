namespace Bassia.Tests;

using System.Globalization;
using Bassia.Locks;
using Bassia.Storage;
using Bassia.Storage.Git;
using Bassia.Storage.Memory;

/// <summary>
/// The contracts every backend implements beside the component histories - the lock store, the job scheduler - and the
/// history queries of the storage contract, each checked against the memory backend and the local (git and file) one,
/// so a new backend can be held to the same behavior.
/// </summary>
public class BackendContractTests
{
	// ----- lock stores -----

	public static TheoryData<string> LockStores => new() { "memory", "file" };

	private static ILockStore LockStore(string kind, TempDirectory temp) =>
		kind == "memory" ? new MemoryLockStore(TimeSpan.FromMinutes(2)) : new FileLockStore(Path.Combine(temp.Path, FileLockStore.FolderName), TimeSpan.FromMinutes(2));

	/// <summary>An entry of a process elsewhere, so only its lease decides whether it is live.</summary>
	private static LockEntry Entry(string runId, DateTimeOffset heartbeat) => new()
	{
		RunId = runId,
		Host = "another-machine",
		Pid = 1,
		ProcessStart = "2000-01-01T00:00:00Z",
		Started = heartbeat.ToString("O", CultureInfo.InvariantCulture),
		Heartbeat = heartbeat.ToString("O", CultureInfo.InvariantCulture)
	};

	[Theory]
	[MemberData(nameof(LockStores))]
	public async Task LockStore_JudgesLivenessByLease_AndAppliesADecisionAsAWhole(string kind)
	{
		using var temp = new TempDirectory();
		var store = LockStore(kind, temp);
		Assert.False(await store.HasEntriesAsync());

		var fresh = Entry("agent-run-a", DateTimeOffset.UtcNow);
		fresh.Holds.Add(new LockHold(new LockKey("lib", "a.txt", LockKind.File), LockHold.Changed, fresh.Heartbeat));
		fresh.Waits.Add(new LockWait(new LockKey("lib", "b.txt", LockKind.File), fresh.Heartbeat, 1, fresh.ProcessStart));
		var stale = Entry("agent-run-b", DateTimeOffset.UtcNow.AddMinutes(-10));
		var released = Entry("agent-run-c", DateTimeOffset.UtcNow.AddMinutes(-10));
		released.State = LockEntry.ReleasedState;
		await store.UpdateAsync(session =>
		{
			session.Save(fresh);
			session.Save(stale);
			session.Save(released);
		});

		var entries = (await store.ReadAllAsync()).ToDictionary(entry => entry.RunId);
		Assert.Equal(3, entries.Count);
		Assert.Equal(LockState.Live, store.StateOf(entries["agent-run-a"]));
		Assert.Equal(LockState.Expired, store.StateOf(entries["agent-run-b"]));
		Assert.Equal(LockState.Released, store.StateOf(entries["agent-run-c"]));
		Assert.True(store.IsWaiting(entries["agent-run-a"], entries["agent-run-a"].Waits.Single()));
		Assert.Equal("lib/a.txt", Assert.Single((await store.FindAsync("agent-run-a"))!.Holds).Key.Display);

		// A decision sees everyone's entries and changes them together.
		var blockers = await store.UpdateAsync(session =>
		{
			session.Delete("agent-run-b");
			return session.Blockers("agent-run-x", new LockKey("lib", "a.txt", LockKind.File)).Select(pair => pair.Entry.RunId).ToList();
		});
		Assert.Equal(["agent-run-a"], blockers);
		Assert.False(await store.HasAsync("agent-run-b"));
		Assert.Null(await store.FindAsync("agent-run-b"));
	}

	[Theory]
	[MemberData(nameof(LockStores))]
	public async Task LockStore_RunsOneDecisionAtATime(string kind)
	{
		using var temp = new TempDirectory();
		var store = LockStore(kind, temp);

		// Every decision reads the counter and writes it plus one; none may be lost.
		await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.UpdateAsync(session =>
		{
			var entry = session.Find("agent-run-counter") ?? Entry("agent-run-counter", DateTimeOffset.UtcNow);
			entry.Holds.Add(new LockHold(new LockKey("lib", $"file-{entry.Holds.Count}", LockKind.File), LockHold.Claimed, entry.Heartbeat));
			session.Save(entry);
		}))));

		Assert.Equal(20, (await store.FindAsync("agent-run-counter"))!.Holds.Count);
	}

	[Fact]
	public async Task FileLockStore_KnowsTheProcessesOfItsOwnMachine()
	{
		using var temp = new TempDirectory();
		var store = new FileLockStore(Path.Combine(temp.Path, FileLockStore.FolderName), TimeSpan.FromMinutes(2));
		var gone = RunLocks.NewEntry("agent-run-gone", RunMetadata.Timestamp(), Environment.ProcessId, "2000-01-01T00:00:00Z", RunMetadata.Timestamp());
		var mine = RunLocks.NewEntry("agent-run-mine", RunMetadata.Timestamp(), Environment.ProcessId, LocalJobScheduler.CurrentProcessStart(), RunMetadata.Timestamp());

		Assert.Equal(LockState.Expired, store.StateOf(gone));
		Assert.Equal(LockState.Live, store.StateOf(mine));
		Assert.Equal(LockState.Live, new MemoryLockStore(TimeSpan.FromMinutes(2)).StateOf(gone)); // the contract knows no processes
	}

	// ----- the job scheduler -----

	[Fact]
	public async Task LocalJobScheduler_RegistersJobs_StopsThemOnRequest_AndReadsTheirOutput()
	{
		using var temp = new TempDirectory();
		var jobs = new LocalJobScheduler(temp.Path);
		var log = jobs.LogPath("agent-run-quiet-fern-91ab22");
		Directory.CreateDirectory(Path.GetDirectoryName(log)!);
		await File.WriteAllTextAsync(log, "first\nsecond\n");

		using (var handle = jobs.Attach("agent-run-quiet-fern-91ab22", "run", log, detached: true))
		{
			var job = (await jobs.FindAsync("agent-run-quiet-fern-91ab22"))!;
			Assert.True(jobs.IsAlive(job));
			Assert.Equal(["agent-run-quiet-fern-91ab22"], (await jobs.ListAsync("run")).Select(item => item.Id));
			Assert.Empty(await jobs.ListAsync("integration"));

			var all = (await jobs.ReadLogAsync(job))!;
			Assert.Equal(["first", "second"], all.Lines);
			Assert.Equal("second\n", (await jobs.ReadLogAsync(job, -7))!.Text);
			Assert.Equal("", (await jobs.ReadLogAsync(job, all.Next))!.Text);

			await jobs.RequestStopAsync(job.Id);
			var clock = System.Diagnostics.Stopwatch.StartNew();
			while (!handle.Cancellation.IsCancellationRequested && clock.Elapsed < TimeSpan.FromSeconds(10))
			{
				await Task.Delay(50);
			}

			Assert.True(handle.Cancellation.IsCancellationRequested);
		}

		// A detached job stays registered, finished, so its output can still be read.
		var finished = (await jobs.FindAsync("agent-run-quiet-fern-91ab22"))!;
		Assert.False(jobs.IsAlive(finished));
		Assert.NotNull(finished.Finished);
		Assert.Equal(Environment.MachineName, finished.Host);
	}

	// ----- history -----

	public static TheoryData<string> Stores => new() { "memory", "git" };

	/// <summary>
	/// main: create (empty), a (a.txt), merge of topic; topic: t (t.txt) off a. Tagged v1 at a. Returns the store and the
	/// ids by subject.
	/// </summary>
	private static async Task<(IComponentStore Store, IReadOnlyDictionary<string, string> Ids)> HistoryAsync(string kind, TempDirectory temp)
	{
		IComponentStorage storage = kind == "memory" ? new MemoryComponentStorage() : new GitComponentStorage(temp.Path);
		var store = await storage.CreateAsync("lib", "main", "", "create");
		var ids = new Dictionary<string, string>(StringComparer.Ordinal) { ["create"] = (await store.BranchTipAsync("main"))! };

		async Task<string> CommitAsync(string branch, string from, string file, string content, string message)
		{
			var folder = Path.Combine(temp.Path, $"wc-{Guid.NewGuid():N}");
			var workspace = await store.CheckoutAsync(from, folder, branch);
			await File.WriteAllTextAsync(Path.Combine(folder, file), content);
			var commit = (await workspace.CommitAsync(message))!;
			await workspace.PublishAsync(branch, []);

			// Git's commit dates have one-second resolution: keep the order unambiguous.
			if (kind == "git")
			{
				await Task.Delay(1100);
			}
			return commit;
		}

		ids["a"] = await CommitAsync("main", ids["create"], "a.txt", "one\ntwo\n", "a");
		await store.CreateTagAsync("v1", ids["a"], "version 1\n\nthe first one");
		ids["t"] = await CommitAsync("topic", ids["a"], "t.txt", "t\n", "t for agent-run-x");
		var folder = Path.Combine(temp.Path, $"wc-{Guid.NewGuid():N}");
		var merging = await store.CheckoutAsync(ids["a"], folder, "main");
		await merging.MergeAsync(ids["t"], new WorkspaceMerge("merge topic"));
		await merging.PublishAsync("main", []);
		ids["merge topic"] = await merging.HeadAsync();
		return (store, ids);
	}

	[Theory]
	[MemberData(nameof(Stores))]
	public async Task Log_IsNewestFirst_WithRefs_AndPagesFiltersAndListsPaths(string kind)
	{
		using var temp = new TempDirectory();
		var (store, ids) = await HistoryAsync(kind, temp);

		var all = await store.LogAsync(new LogQuery());
		Assert.Equal(["merge topic", "t for agent-run-x", "a", "create"], all.Select(entry => entry.Commit.Subject));
		Assert.Equal([ids["a"], ids["t"]], all[0].Commit.Parents);
		Assert.Equal(["main"], all[0].Branches);
		Assert.Equal(["topic"], all[1].Branches);
		Assert.Equal(["v1"], all[2].Tags);
		Assert.True(all[0].Date >= all[2].Date);

		Assert.Equal(["t for agent-run-x", "a"], (await store.LogAsync(new LogQuery(Skip: 1, Limit: 2))).Select(entry => entry.Commit.Subject));
		Assert.Equal(["t for agent-run-x", "a", "create"], (await store.LogAsync(new LogQuery(["refs/heads/topic"]))).Select(entry => entry.Commit.Subject));
		Assert.Equal([ids["t"]], (await store.LogAsync(new LogQuery(Grep: ["agent-run-x"]))).Select(entry => entry.Commit.Id));
		var withPaths = await store.LogAsync(new LogQuery(["topic"], Paths: true));
		Assert.Equal(["t.txt"], withPaths[0].Paths);
		Assert.Equal(["a.txt"], withPaths[1].Paths);
	}

	[Theory]
	[MemberData(nameof(Stores))]
	public async Task DiffStat_CountsLinesPerFile_AndTagsKeepTheirWholeMessage(string kind)
	{
		using var temp = new TempDirectory();
		var (store, ids) = await HistoryAsync(kind, temp);

		Assert.Equal([new FileStat("a.txt", 2, 0)], await store.DiffStatAsync(ids["create"], ids["a"]));
		Assert.Equal([new FileStat("a.txt", 2, 0), new FileStat("t.txt", 1, 0)], await store.DiffStatAsync(null, ids["merge topic"]));
		Assert.Equal("version 1\n\nthe first one", await store.TagMessageAsync("v1"));
		Assert.Null(await store.TagMessageAsync("nope"));
	}

	[Fact]
	public void CommitGraph_DrawsMergesLikeGit()
	{
		static LogEntry Entry(string id, string subject, params string[] parents) =>
			new(new StoredCommit(id, parents, subject), DateTimeOffset.UnixEpoch, id == "m000000" ? ["main"] : [], id == "a000000" ? ["v1"] : []);

		var graph = Bassia.Graph.CommitGraph.Render(
			[Entry("m000000", "merge", "c000000", "t000000"), Entry("c000000", "c", "a000000"), Entry("t000000", "t", "a000000"), Entry("a000000", "a")], "main");

		Assert.Equal("""
			* m000000 (HEAD -> main) merge
			|\
			* | c000000 c
			| * t000000 t
			|/
			* a000000 (tag: v1) a
			""".Replace("\r", ""), graph);
	}

	[Fact]
	public void DiffStatText_DrawsGitsStat()
	{
		Assert.Equal(" a.txt   | 3 ++-\n bin.png | Bin\n 2 files changed, 2 insertions(+), 1 deletion(-)\n",
			Bassia.Cli.DiffStatText.Render([new FileStat("a.txt", 2, 1), new FileStat("bin.png", -1, -1)]));
	}
}
