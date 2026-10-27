namespace Bassia.Tests;

using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.S3;
using Bassia.Storage;
using Bassia.Storage.Cloud;
using Bassia.Storage.Git;
using Bassia.Storage.Memory;

/// <summary>
/// The storage contract (<see cref="IComponentStorage"/>, <see cref="IComponentStore"/>, <see cref="IComponentWorkspace"/>)
/// held against every backend: memory, git, and the cloud storage - on the in-memory object store and record table, or
/// on emulators when <c>BASSIA_CLOUD_TEST_ENDPOINT</c> (DynamoDB, e.g. DynamoDB Local) and optionally
/// <c>BASSIA_CLOUD_TEST_S3_ENDPOINT</c> (S3, e.g. MinIO) are set.
/// </summary>
[Trait("Category", "StorageContract")]
public class StorageContractTests
{
	public static TheoryData<string> Backends => new() { "memory", "git", "cloud" };

	/// <summary>A storage of the kind; for the cloud, a second node (its own cache) on the same object store and table is <c>Second</c>.</summary>
	internal static IComponentStorage Storage(string kind, TempDirectory temp) => kind switch
	{
		"memory" => new MemoryComponentStorage(),
		"git" => new GitComponentStorage(temp.Path),
		_ => CloudTest.Nodes(temp).First
	};

	/// <summary>A folder holding one commit on <c>main</c> with <paramref name="files"/>: an upstream to import.</summary>
	private static async Task<string> UpstreamAsync(TempDirectory temp, params (string Path, string Content)[] files)
	{
		var folder = Path.Combine(temp.Path, $"upstream-{Guid.NewGuid():N}");
		Directory.CreateDirectory(folder);
		var git = Bassia.Git.GitClient.In(folder);
		await git.RunOrThrowAsync(["init", "--quiet", "--initial-branch=main"]);
		foreach (var (path, content) in files)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, path))!);
			await File.WriteAllTextAsync(Path.Combine(folder, path), content);
		}

		await git.RunOrThrowAsync(["add", "--all"]);
		await git.RunOrThrowAsync(["commit", "--quiet", "-m", "upstream"]);
		await git.RunOrThrowAsync(["tag", "-a", "v1", "-m", "v1"]);
		return folder;
	}

	private static async Task<string> CommitAsync(IComponentStore store, TempDirectory temp, string from, string branch, string file, string content)
	{
		var folder = Path.Combine(temp.Path, $"wc-{Guid.NewGuid():N}");
		var workspace = await store.CheckoutAsync(from, folder, branch);
		await File.WriteAllTextAsync(Path.Combine(folder, file), content);
		var commit = (await workspace.CommitAsync($"change {file}"))!;
		await workspace.PublishAsync(branch, []);
		return commit;
	}

	[Theory]
	[MemberData(nameof(Backends))]
	public async Task Create_OpensWithItsDefaultBranch_AndUpstream(string kind)
	{
		using var temp = new TempDirectory();
		var storage = Storage(kind, temp);

		var store = await storage.CreateAsync("lib", "trunk", "https://example.com/lib.git", "first");

		Assert.True(storage.Exists("lib"));
		Assert.False(storage.Exists("app"));
		var opened = storage.Open("lib");
		Assert.Equal("trunk", await opened.DefaultBranchAsync());
		Assert.Equal("first", (await opened.GetCommitAsync((await opened.BranchTipAsync("trunk"))!))!.Subject);
		Assert.Equal("https://example.com/lib.git", await opened.UpstreamAsync());
		Assert.Empty(await opened.ListFilesAsync((await store.BranchTipAsync("trunk"))!));
		Assert.Throws<StorageException>(() => storage.Open("app"));
	}

	[Theory]
	[MemberData(nameof(Backends))]
	public async Task SetBranch_IsACompareAndSwap(string kind)
	{
		using var temp = new TempDirectory();
		var store = await Storage(kind, temp).CreateAsync("lib", "main", "", "first");
		var first = (await store.BranchTipAsync("main"))!;
		var second = await CommitAsync(store, temp, first, "main", "a.txt", "a");

		await store.SetBranchAsync("topic", first);
		Assert.Equal(first, await store.BranchTipAsync("topic"));
		await Assert.ThrowsAnyAsync<Exception>(() => store.SetBranchAsync("topic", second, expected: second));
		Assert.Equal(first, await store.BranchTipAsync("topic"));
		await store.SetBranchAsync("topic", second, expected: first);
		Assert.Equal(second, await store.BranchTipAsync("topic"));
		Assert.Equal(["main", "topic"], (await store.ListBranchesAsync()).Select(branch => branch.Name).Order(StringComparer.Ordinal));
	}

	[Theory]
	[MemberData(nameof(Backends))]
	public async Task Tags_AreCreatedOnce_ListedByPrefix_AndDeleted(string kind)
	{
		using var temp = new TempDirectory();
		var store = await Storage(kind, temp).CreateAsync("lib", "main", "", "first");
		var tip = (await store.BranchTipAsync("main"))!;

		await store.CreateTagAsync("run/1", tip, "one");
		await store.CreateTagAsync("v1", tip, "version");
		await Assert.ThrowsAnyAsync<Exception>(() => store.CreateTagAsync("v1", tip, "again"));

		Assert.Equal(["run/1"], (await store.ListTagsAsync("run/")).Select(tag => tag.Name));
		Assert.Equal(tip, (await store.GetTagAsync("v1"))!.Commit);
		Assert.Equal("version", await store.TagMessageAsync("v1"));
		await store.DeleteTagAsync("v1");
		Assert.Null(await store.GetTagAsync("v1"));
	}

	[Theory]
	[MemberData(nameof(Backends))]
	public async Task Publish_FastForwardsTheBranch_AndRefusesToLoseCommits(string kind)
	{
		using var temp = new TempDirectory();
		var store = await Storage(kind, temp).CreateAsync("lib", "main", "", "first");
		var first = (await store.BranchTipAsync("main"))!;

		var ahead = await CommitAsync(store, temp, first, "main", "a.txt", "a");
		Assert.Equal(ahead, await store.BranchTipAsync("main"));
		Assert.Equal("a"u8.ToArray(), await store.ReadFileAsync(ahead, "a.txt"));

		// A working copy of the old tip publishes a commit that does not contain the new one.
		await Assert.ThrowsAnyAsync<Exception>(() => CommitAsync(store, temp, first, "main", "b.txt", "b"));
		Assert.Equal(ahead, await store.BranchTipAsync("main"));
	}

	[Theory]
	[MemberData(nameof(Backends))]
	public async Task MoveBranches_MovesAllOrNone(string kind)
	{
		using var temp = new TempDirectory();
		var storage = Storage(kind, temp);
		var lib = await storage.CreateAsync("lib", "main", "", "lib");
		var app = await storage.CreateAsync("app", "main", "", "app");
		var (libBase, appBase) = ((await lib.BranchTipAsync("main"))!, (await app.BranchTipAsync("main"))!);
		var libNext = await CommitAsync(lib, temp, libBase, "next", "l.txt", "l");
		var appNext = await CommitAsync(app, temp, appBase, "next", "a.txt", "a");

		var moved = await Assert.ThrowsAsync<BranchesMovedException>(() => storage.MoveBranchesAsync(
			[new BranchMove("lib", "main", libNext, libBase), new BranchMove("app", "main", appNext, libBase)]));
		Assert.Equal("app", Assert.Single(moved.Moved).Component);
		Assert.Equal(libBase, await storage.Open("lib").BranchTipAsync("main"));

		await storage.MoveBranchesAsync([new BranchMove("lib", "main", libNext, libBase), new BranchMove("app", "main", appNext, appBase)]);
		Assert.Equal(libNext, await storage.Open("lib").BranchTipAsync("main"));
		Assert.Equal(appNext, await storage.Open("app").BranchTipAsync("main"));
	}

	[Theory]
	[MemberData(nameof(Backends))]
	public async Task Import_BringsEveryBranchAndTag_AndFork_TakesTheBranchUpToItsPoint(string kind)
	{
		if (kind == "memory")
		{
			return; // The memory storage imports from its own fake upstreams (see MemoryMonorepo).
		}

		using var temp = new TempDirectory();
		var storage = Storage(kind, temp);
		var url = await UpstreamAsync(temp, ("src/a.c", "int a;"));

		var app = await storage.ImportAsync("app", url);
		var point = (await app.BranchTipAsync("main"))!;
		Assert.Equal(["src/a.c"], (await app.ListFilesAsync(point)).Select(file => file.Path));
		Assert.Equal(point, (await app.GetTagAsync("v1"))!.Commit);
		Assert.Equal(url, await app.UpstreamAsync());
		await CommitAsync(app, temp, point, "main", "later.txt", "later");

		var fork = await storage.ForkAsync(storage.Open("app"), "app-next", "main", point, ["v1"], "");
		Assert.Equal(point, await fork.BranchTipAsync("main"));
		Assert.Equal(point, (await fork.GetTagAsync("v1"))!.Commit);
		Assert.Equal(["main"], (await fork.ListBranchesAsync()).Select(branch => branch.Name));
		Assert.Equal("int a;"u8.ToArray(), await fork.ReadFileAsync(point, "src/a.c"));
	}

	[Theory]
	[MemberData(nameof(Backends))]
	public async Task Delete_RemovesTheStore(string kind)
	{
		using var temp = new TempDirectory();
		var storage = Storage(kind, temp);
		await storage.CreateAsync("lib", "main", "", "first");

		await storage.DeleteAsync("lib");

		Assert.False(storage.Exists("lib"));
		var again = await storage.CreateAsync("lib", "main", "", "second");
		Assert.Equal("second", (await again.GetCommitAsync((await again.BranchTipAsync("main"))!))!.Subject);
	}

	[Theory]
	[MemberData(nameof(Backends))]
	public async Task Staging_IsInvisibleUntilPromoted(string kind)
	{
		if (kind == "memory")
		{
			return;
		}

		using var temp = new TempDirectory();
		var storage = Storage(kind, temp);
		var url = await UpstreamAsync(temp, ("x.txt", "x"));
		using (var staging = storage.BeginStaging())
		{
			var staged = await staging.ImportAsync("dropped", url);
			Assert.False(storage.Exists("dropped"));
			var kept = await staging.ImportAsync("kept", url);
			var promoted = await staging.PromoteAsync(kept);
			Assert.NotNull(await promoted.BranchTipAsync("main"));
		}

		Assert.True(storage.Exists("kept"));
		Assert.False(storage.Exists("dropped"));
		Assert.Equal(url, await storage.Open("kept").UpstreamAsync());
	}

	// ----- equivalence -----

	/// <summary>One sequence of operations: create, two branches of commits, a tree-level merge with a conflict-free result, a tag.</summary>
	private static async Task<IReadOnlyList<string>> SequenceAsync(IComponentStorage storage, TempDirectory temp)
	{
		var store = await storage.CreateAsync("lib", "main", "", "first");
		var root = (await store.BranchTipAsync("main"))!;
		var ours = await CommitAsync(store, temp, root, "main", "a.txt", "one\ntwo\n");
		var theirs = await CommitAsync(store, temp, root, "topic", "b.txt", "three\n");
		var merge = await store.MergeAsync(ours, theirs);
		var merged = await store.CommitSnapshotAsync(merge.Snapshot!, [ours, theirs], "merge topic");
		await store.SetBranchAsync("main", merged, expected: ours);
		await store.CreateTagAsync("v1", merged, "version 1");
		return
		[
			root, ours, theirs, merged, (await store.GetTagAsync("v1"))!.Commit,
			string.Join(",", (await store.ListFilesAsync(merged)).Select(file => $"{file.Path}:{file.Object}")),
			await store.DiffAsync(root, merged),
			string.Join(",", await store.ChangedPathsAsync(ours, merged)),
			(await store.MergeBaseAsync(ours, theirs))!,
			string.Join(",", (await store.HistoryAsync(merged)).Select(commit => commit.Id))
		];
	}

	[Fact]
	public async Task Equivalence_GitAndCloud_GiveTheSameIdsTreesMergesAndDiffs()
	{
		var dates = new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = "1700000000 +0000", ["GIT_COMMITTER_DATE"] = "1700000000 +0000" };
		var saved = dates.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
		try
		{
			foreach (var (key, value) in dates)
			{
				Environment.SetEnvironmentVariable(key, value);
			}

			using var gitTemp = new TempDirectory();
			using var cloudTemp = new TempDirectory();
			var git = await SequenceAsync(Storage("git", gitTemp), gitTemp);
			var cloud = await SequenceAsync(Storage("cloud", cloudTemp), cloudTemp);

			Assert.Equal(git, cloud);
		}
		finally
		{
			foreach (var (key, value) in saved)
			{
				Environment.SetEnvironmentVariable(key, value);
			}
		}
	}

	// ----- the cloud storage across nodes -----

	[Fact]
	public async Task Cloud_ANodeSeesWhatAnotherPublished_DownloadingEachPackOnce()
	{
		using var temp = new TempDirectory();
		var (first, second, objects) = CloudTest.Nodes(temp);
		var store = await first.CreateAsync("lib", "main", "", "first");
		var tip = await CommitAsync(store, temp, (await store.BranchTipAsync("main"))!, "main", "a.txt", "a");
		await store.CreateTagAsync("v1", tip, "v1");

		var seen = second.Open("lib");
		Assert.Equal(tip, await seen.BranchTipAsync("main"));
		Assert.Equal("a"u8.ToArray(), await seen.ReadFileAsync(tip, "a.txt"));
		Assert.Equal(tip, (await seen.GetTagAsync("v1"))!.Commit);
		Assert.Equal(["change a.txt", "first"], (await seen.LogAsync(new LogQuery())).Select(entry => entry.Commit.Subject));

		if (objects is MemoryObjectStore memory)
		{
			var downloads = memory.Downloads;
			Assert.Equal(tip, await second.Open("lib").BranchTipAsync("main"));
			Assert.Equal(downloads, memory.Downloads);
		}
	}

	[Fact]
	public async Task Cloud_TwoNodesRacingForABranch_OneWins_TheOtherIsRefusedWhole()
	{
		using var temp = new TempDirectory();
		var (first, second, _) = CloudTest.Nodes(temp);
		var created = await first.CreateAsync("lib", "main", "", "first");
		var root = (await created.BranchTipAsync("main"))!;
		var mine = first.Open("lib");
		var theirs = second.Open("lib");
		Assert.Equal(root, await theirs.BranchTipAsync("main"));

		var winner = await CommitAsync(mine, temp, root, "main", "a.txt", "mine");
		var loser = Assert.ThrowsAnyAsync<StorageException>(() => CommitAsync(theirs, temp, root, "main", "a.txt", "theirs"));
		Assert.Contains("does not contain", (await loser).Message);

		Assert.Equal(winner, await second.Open("lib").BranchTipAsync("main"));
		await Assert.ThrowsAsync<CloudConflictException>(() => theirs.SetBranchAsync("main", root, expected: root));
	}

	[Fact]
	public async Task Cloud_Collection_RepacksIntoOnePack_AndDeletesWhatItSupersededAfterTheGrace()
	{
		using var temp = new TempDirectory();
		var (storage, _, objects) = CloudTest.Nodes(temp);
		var store = await storage.CreateAsync("lib", "main", "", "first");
		var tip = (await store.BranchTipAsync("main"))!;
		foreach (var file in new[] { "a.txt", "b.txt", "c.txt" })
		{
			tip = await CommitAsync(store, temp, tip, "main", file, file);
		}

		var dead = Path.Combine(temp.Path, "dead.pack");
		await File.WriteAllTextAsync(dead, "a pack a failed write left behind");
		await objects.PutFileAsync("components/lib/packs/pack-dead.pack", dead);
		var now = DateTimeOffset.UtcNow;
		var collector = new CloudGarbageCollector(storage, TimeSpan.FromDays(14), () => now);

		var first = await collector.CollectAsync("lib");

		Assert.NotNull(first.Pack);
		Assert.Equal(4, first.Superseded.Count);
		Assert.Empty(first.Deleted);
		Assert.Contains("components/lib/packs/pack-dead.pack", await objects.ListAsync("components/lib/"));

		now += TimeSpan.FromDays(15);
		var second = await collector.CollectAsync("lib");

		Assert.Null(second.Pack);
		Assert.Equal(first.Superseded.Order(StringComparer.Ordinal), second.Deleted.Order(StringComparer.Ordinal));
		Assert.Equal([$"components/lib/packs/{first.Pack}.idx", $"components/lib/packs/{first.Pack}.pack"], await objects.ListAsync("components/lib/"));

		// A node with an empty cache has everything from the one pack.
		var fresh = new CloudComponentStorage(storage.Objects, storage.Table, Path.Combine(temp.Path, "node-3")).Open("lib");
		Assert.Equal(tip, await fresh.BranchTipAsync("main"));
		Assert.Equal(4, (await fresh.LogAsync(new LogQuery())).Count);
		Assert.Equal("a.txt"u8.ToArray(), await fresh.ReadFileAsync(tip, "a.txt"));
	}

	[Fact]
	public async Task Cloud_Collection_WaitsForARunningOne()
	{
		using var temp = new TempDirectory();
		var (storage, _, _) = CloudTest.Nodes(temp);
		await storage.CreateAsync("lib", "main", "", "first");
		await storage.Table.WriteAsync([new CloudWrite(CloudComponentStorage.Partition("lib"), CloudComponentStorage.LeaseKey,
			new Dictionary<string, string> { ["owner"] = "elsewhere", ["expires"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) })]);

		Assert.Contains("is running", (await Assert.ThrowsAsync<StorageException>(() => new CloudGarbageCollector(storage).CollectAsync("lib"))).Message);
		Assert.NotNull(await new CloudGarbageCollector(storage, clock: () => DateTimeOffset.UtcNow.AddHours(1)).CollectAsync("lib"));
	}

	[Fact]
	public async Task Cloud_RefusesSha256Repositories()
	{
		using var temp = new TempDirectory();
		var folder = Path.Combine(temp.Path, "sha256");
		var git = Bassia.Git.GitClient.In(temp.Path);
		if ((await git.RunAsync(["init", "--quiet", "--object-format=sha256", folder])).ExitCode != 0)
		{
			return; // A git without sha256 support cannot make one.
		}

		await File.WriteAllTextAsync(Path.Combine(folder, "x.txt"), "x");
		await Bassia.Git.GitClient.In(folder).RunOrThrowAsync(["add", "--all"]);
		await Bassia.Git.GitClient.In(folder).RunOrThrowAsync(["commit", "--quiet", "-m", "x"]);
		var (storage, _, _) = CloudTest.Nodes(temp);

		Assert.Contains("sha256", (await Assert.ThrowsAnyAsync<StorageException>(() => storage.ImportAsync("x", folder))).Message);
		Assert.False(storage.Exists("x"));
	}
}

/// <summary>Cloud storages for tests: on memory, or on emulators named by environment variables (see <see cref="StorageContractTests"/>).</summary>
internal static class CloudTest
{
	public static string? DynamoEndpoint => Environment.GetEnvironmentVariable("BASSIA_CLOUD_TEST_ENDPOINT") is { Length: > 0 } url ? url : null;

	public static string? S3Endpoint => Environment.GetEnvironmentVariable("BASSIA_CLOUD_TEST_S3_ENDPOINT") is { Length: > 0 } url ? url : null;

	/// <summary>A fresh object store and record table: in memory, or a new bucket and table on the emulators.</summary>
	public static (IObjectStore Objects, IRecordTable Table) Primitives()
	{
		var credentials = new BasicAWSCredentials("test", "test");
		IObjectStore objects = new MemoryObjectStore();
		if (S3Endpoint is { } s3Url)
		{
			var s3 = new AmazonS3Client(credentials, new AmazonS3Config { ServiceURL = s3Url, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
			var bucket = $"bassia-test-{Guid.NewGuid():N}"[..30];
			s3.PutBucketAsync(bucket).GetAwaiter().GetResult();
			objects = new S3ObjectStore(s3, bucket, "test/");
		}

		IRecordTable table = new MemoryRecordTable();
		if (DynamoEndpoint is { } dynamoUrl)
		{
			var dynamo = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig { ServiceURL = dynamoUrl, AuthenticationRegion = "us-east-1" });
			var records = new DynamoRecordTable(dynamo, $"bassia-test-{Guid.NewGuid():N}");
			records.EnsureTableAsync().GetAwaiter().GetResult();
			table = records;
		}

		return (objects, table);
	}

	/// <summary>Two nodes - each its own cache - on one object store and table.</summary>
	public static (CloudComponentStorage First, CloudComponentStorage Second, IObjectStore Objects) Nodes(TempDirectory temp)
	{
		var (objects, table) = Primitives();
		return (new CloudComponentStorage(objects, table, Path.Combine(temp.Path, "node-1")), new CloudComponentStorage(objects, table, Path.Combine(temp.Path, "node-2")), objects);
	}
}
