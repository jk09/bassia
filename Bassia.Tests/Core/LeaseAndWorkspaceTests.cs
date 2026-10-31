namespace Bassia.Tests.Core;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Core.Workspaces;

public class LeaseTests
{
	private static LeaseRequest Request(string prefix, LeaseMode mode, string owner, double ttlSeconds = 60, double waitSeconds = 0) =>
		new(prefix, mode, owner, TimeSpan.FromSeconds(ttlSeconds), TimeSpan.FromSeconds(waitSeconds));

	[Fact]
	public async Task ExclusiveLeases_FollowTheHierarchy()
	{
		var store = new MemoryLeaseStore();
		Assert.True((await store.AcquireAsync(Request("src/Billing", LeaseMode.Exclusive, "a"))).Granted);
		Assert.True((await store.AcquireAsync(Request("src/Api", LeaseMode.Exclusive, "b"))).Granted);
		Assert.True((await store.AcquireAsync(Request("src", LeaseMode.Intent, "c"))).Granted);
		Assert.False((await store.AcquireAsync(Request("src", LeaseMode.Exclusive, "d"))).Granted);
		Assert.False((await store.AcquireAsync(Request("src/Billing/Invoices", LeaseMode.Exclusive, "d"))).Granted);
		Assert.False((await store.AcquireAsync(Request("src/Billing/Invoices", LeaseMode.Intent, "d"))).Granted);
		Assert.True((await store.AcquireAsync(Request("src/Billing/Invoices", LeaseMode.Exclusive, "a"))).Granted);
		Assert.True((await store.AcquireAsync(Request("docs", LeaseMode.Exclusive, "d"))).Granted);
	}

	[Fact]
	public async Task FencingTokens_Grow_AndAnExpiredHolderCannotRenewOrLand()
	{
		var clock = DateTimeOffset.UtcNow;
		var repo = await TestRepo.CreateAsync();
		repo.Leases.Clock = () => clock;
		var ws = await repo.WorkspaceAsync("slow", new Dictionary<string, string?> { ["src/x.cs"] = "x\n" });
		var first = (await repo.Leases.AcquireAsync(Request("src", LeaseMode.Exclusive, ws.Id, ttlSeconds: 10))).Lease!;
		clock += TimeSpan.FromSeconds(11);
		var second = (await repo.Leases.AcquireAsync(Request("src", LeaseMode.Exclusive, "successor"))).Lease!;
		Assert.True(second.Token > first.Token);
		Assert.Null(await repo.Leases.RenewAsync(first.Id, first.Token, TimeSpan.FromSeconds(10)));

		var result = await repo.Landing.LandAsync(new LandRequest(ws.Id, Leases: [new LeaseToken(first.Id, first.Token)]));
		Assert.Equal(LandOutcome.Rejected, result.Outcome);
	}

	[Fact]
	public async Task LeaseRequiredPrefixes_RefuseALandingWithoutAValidLease()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(new Dictionary<string, string?> { [RepoPolicy.FileName] = "[leases]\nrequired = [\"src/Billing\"]\n" });
		var ws = await repo.WorkspaceAsync("billing", new Dictionary<string, string?> { ["src/Billing/a.cs"] = "a\n" });
		var refused = await repo.LandAsync(ws);
		Assert.Equal(LandOutcome.Rejected, refused.Outcome);
		Assert.Equal("lease", refused.Conflicts.Single().Case);

		var lease = (await repo.Leases.AcquireAsync(Request("src/Billing", LeaseMode.Exclusive, ws.Id))).Lease!;
		var landed = await repo.Landing.LandAsync(new LandRequest(ws.Id, Leases: [new LeaseToken(lease.Id, lease.Token)]));
		Assert.Equal(LandOutcome.Landed, landed.Outcome);
	}

	[Fact]
	public async Task Waiters_AreServedFirstInFirstOut()
	{
		var store = new MemoryLeaseStore();
		var held = (await store.AcquireAsync(Request("hot", LeaseMode.Exclusive, "holder"))).Lease!;
		var order = new List<string>();
		var first = Task.Run(async () =>
		{
			var lease = (await store.AcquireAsync(Request("hot", LeaseMode.Exclusive, "first", waitSeconds: 10))).Lease!;
			lock (order)
			{
				order.Add("first");
			}

			await Task.Delay(100);
			await store.ReleaseAsync(lease.Id, lease.Token);
		});
		await Task.Delay(100);
		var second = Task.Run(async () =>
		{
			var lease = (await store.AcquireAsync(Request("hot", LeaseMode.Exclusive, "second", waitSeconds: 10))).Lease!;
			lock (order)
			{
				order.Add("second");
			}

			await store.ReleaseAsync(lease.Id, lease.Token);
		});
		await Task.Delay(100);
		await store.ReleaseAsync(held.Id, held.Token);
		await Task.WhenAll(first, second);
		Assert.Equal(["first", "second"], order);
	}
}

public class WorkspaceDiskTests
{
	private static async Task<(TestRepo Repo, WorkspaceClient Client, TempDir Dir)> SetupAsync()
	{
		var repo = await TestRepo.CreateAsync();
		var dir = new TempDir();
		var client = new WorkspaceClient(repo.Repo, repo.Landing, dir.Combine(".state"), "tester");
		return (repo, client, dir);
	}

	[Fact]
	public async Task Snapshot_HashesOnlyChangedFiles_AndRespectsIgnoreRules()
	{
		var (repo, client, dir) = await SetupAsync();
		using var _ = dir;
		await repo.CommitAsync(Enumerable.Range(0, 50).ToDictionary(index => $"src/f{index}.txt", index => (string?)$"{index}\n"));
		var (state, local) = await client.CreateAsync("ws", dir.Combine("ws"));
		Assert.True(File.Exists(dir.Combine("ws", "src", "f7.txt")));
		Assert.NotNull(WorkspaceClient.ReadMarker(dir.Combine("ws")));

		var quiet = await client.SnapshotAsync(state);
		Assert.False(quiet.Changed);

		await File.WriteAllTextAsync(dir.Combine("ws", "src", "f3.txt"), "three\n");
		Directory.CreateDirectory(dir.Combine("ws", "bin"));
		await File.WriteAllTextAsync(dir.Combine("ws", "bin", "out.dll"), "binary");
		await File.WriteAllTextAsync(dir.Combine("ws", "new.md"), "new\n");
		File.Delete(dir.Combine("ws", "src", "f9.txt"));

		var snapshot = await client.SnapshotAsync(state);
		Assert.Equal(["new.md", "src/f3.txt", "src/f9.txt"], snapshot.Changes.Select(change => change.Path).Order(StringComparer.Ordinal));
		var diff = await repo.Repo.Diffs.DiffAsync((await repo.Repo.Objects.ReadCommitAsync(snapshot.Workspace.Record.Base)).Tree,
			(await repo.Repo.Objects.ReadCommitAsync(snapshot.Workspace.Record.Working)).Tree);
		Assert.Equal(3, diff.Count);
		Assert.Equal(local.Root, snapshot.Workspace.Record.Path);
	}

	[Fact]
	public async Task Snapshot_RefusesPathsThatDifferOnlyByCase()
	{
		if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
		{
			return;
		}

		var (_, client, dir) = await SetupAsync();
		using var _ = dir;
		var (state, _) = await client.CreateAsync("ws", dir.Combine("ws"));
		await File.WriteAllTextAsync(dir.Combine("ws", "Readme.md"), "a");
		await File.WriteAllTextAsync(dir.Combine("ws", "README.md"), "b");
		var error = await Assert.ThrowsAsync<VersionException>(() => client.SnapshotAsync(state));
		Assert.Contains("differ only by case", error.Message);
	}

	[Fact]
	public async Task SyncWithAConflict_MaterializesMarkers_AndEditingResolvesIt()
	{
		var (repo, client, dir) = await SetupAsync();
		using var _ = dir;
		await repo.CommitAsync(new Dictionary<string, string?> { ["a.txt"] = "base\n" });
		var (state, _) = await client.CreateAsync("ws", dir.Combine("ws"));
		await File.WriteAllTextAsync(dir.Combine("ws", "a.txt"), "mine\n");
		await repo.CommitAsync(new Dictionary<string, string?> { ["a.txt"] = "theirs\n" }, "other");

		var sync = await client.SyncAsync(state);
		Assert.Equal(["a.txt"], sync.Conflicts);
		var text = await File.ReadAllTextAsync(dir.Combine("ws", "a.txt"));
		Assert.Contains("<<<<<<< workspace\nmine\n", text);
		Assert.Contains("=======\ntheirs\n>>>>>>> mainline", text);

		// Untouched, the conflict stays a conflict.
		var again = await client.SnapshotAsync(sync.Workspace);
		Assert.False(again.Changed);
		Assert.Equal(LandOutcome.Conflict, (await client.LandAsync(again.Workspace, new LandRequest(""))).Outcome);

		await Task.Delay(10);
		await File.WriteAllTextAsync(dir.Combine("ws", "a.txt"), "mine and theirs\n");
		var landed = await client.LandAsync(again.Workspace, new LandRequest(""));
		Assert.Equal(LandOutcome.Landed, landed.Outcome);
		Assert.Equal("mine and theirs\n", (await repo.MainFilesAsync())["a.txt"]);
	}

	[Fact]
	public async Task Undo_PutsTheDirectoryBack()
	{
		var (repo, client, dir) = await SetupAsync();
		using var _ = dir;
		await repo.CommitAsync(new Dictionary<string, string?> { ["a.txt"] = "1\n" });
		var (state, _) = await client.CreateAsync("ws", dir.Combine("ws"));
		await File.WriteAllTextAsync(dir.Combine("ws", "a.txt"), "2\n");
		await File.WriteAllTextAsync(dir.Combine("ws", "b.txt"), "b\n");
		await client.UndoAsync(state);
		Assert.Equal("1\n", await File.ReadAllTextAsync(dir.Combine("ws", "a.txt")));
		Assert.False(File.Exists(dir.Combine("ws", "b.txt")));
	}

	[Fact]
	public async Task StatusOfALargeTree_ReadsNoFileBytesFromTheStore()
	{
		var counting = new CountingObjectStore(new MemoryObjectStore());
		var repo = new TestRepo(counting);
		await repo.Repo.InitializeAsync("t");
		var big = new string('x', 5000);
		await repo.CommitAsync(Enumerable.Range(0, 300).ToDictionary(index => $"d{index % 10}/f{index}.txt", index => (string?)(big + index)));
		using var dir = new TempDir();
		var client = new WorkspaceClient(repo.Repo, repo.Landing, dir.Combine(".state"), "tester");
		var (state, _) = await client.CreateAsync("ws", dir.Combine("ws"));
		File.WriteAllText(dir.Combine("ws", "d1", "f1.txt"), "changed");

		// A fresh client over the same store, as a second process would be: only tree objects are read.
		var fresh = new Repository(counting, repo.Views, repo.Paths, repo.Leases, TestRepo.SmallChunks);
		var freshClient = new WorkspaceClient(fresh, new LandingService(fresh), dir.Combine(".state"), "tester");
		var before = counting.BytesRead;
		var reads = counting.Reads;
		var snapshot = await freshClient.SnapshotAsync((await fresh.FindWorkspaceAsync(state.Id))!);
		var record = snapshot.Workspace.Record;
		var diff = await fresh.Diffs.DiffAsync((await fresh.Objects.ReadCommitAsync(record.Base)).Tree, (await fresh.Objects.ReadCommitAsync(record.Working)).Tree);
		Assert.Single(diff);
		Assert.True(counting.BytesRead - before < 5000 * 3, $"status read {counting.BytesRead - before} bytes in {counting.Reads - reads} reads");
	}

	[Fact]
	public async Task StackedAndClonedWorkspaces_ShareTheirBases()
	{
		var (repo, client, dir) = await SetupAsync();
		using var _ = dir;
		var (parent, _) = await client.CreateAsync("parent", dir.Combine("parent"));
		await File.WriteAllTextAsync(dir.Combine("parent", "p.txt"), "p\n");
		parent = (await client.SnapshotAsync(parent)).Workspace;
		var (child, _) = await client.CreateAsync("child", dir.Combine("child"), on: parent.Id);
		Assert.Equal("p\n", await File.ReadAllTextAsync(dir.Combine("child", "p.txt")));
		var (clone, _) = await client.CloneAsync(parent.Id, "clone", dir.Combine("clone"));
		Assert.Equal("p\n", await File.ReadAllTextAsync(dir.Combine("clone", "p.txt")));
		Assert.Equal(parent.Record.Base, clone.Record.Base);
		Assert.NotEqual((await repo.Repo.Objects.ReadCommitAsync(parent.Record.Working)).ChangeId,
			(await repo.Repo.Objects.ReadCommitAsync(clone.Record.Working)).ChangeId);
		Assert.Equal(parent.Id, child.Record.Parent);
	}
}
