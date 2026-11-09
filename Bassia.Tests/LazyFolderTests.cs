namespace Bassia.Tests;

using System.Diagnostics;
using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Core.Workspaces;
using Bassia.Lazy;
using Bassia.Tests.Core;
using Tomlyn.Model;
using static Bassia.Tests.TestEnvironment;

/// <summary>
/// Lazy workspace folders: placeholders written without fetching any content, hydrated on first use - by the helper
/// on every platform, and through a real FUSE mount on Linux (BASSIA_TEST_REQUIRE_FUSE=1 makes a missing FUSE fail).
/// </summary>
[Collection("fuse")]
public sealed class LazyFolderTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose() => temp.Dispose();

	private static readonly Dictionary<string, string?> Files = new()
	{
		[".bassia.toml"] = "[ignore]\npaths = [\"*.log\"]\n",
		["README.md"] = "# acme\n",
		["api/src/server.cs"] = "class Server {}\n",
		["api/src/big.cs"] = string.Concat(Enumerable.Repeat("// a line of a large file\n", 400)),
		["api/tool.sh"] = "echo hi\n",
		["web/app.js"] = "app();\n"
	};

	private async Task<(TestRepo Repo, CountingStore Store, WorkspaceClient Client, FakeLazyFolders Lazy)> SetupAsync(Func<LocalWorkspace, Task>? mount = null)
	{
		var store = new CountingStore();
		var repo = new TestRepo(store);
		await repo.Repo.InitializeAsync("test");
		await repo.CommitAsync(Files);
		var lazy = new FakeLazyFolders(mount);
		return (repo, store, new WorkspaceClient(repo.Repo, repo.Landing, temp.Combine(".state"), "tester", lazyFolders: lazy), lazy);
	}

	private static async Task<HashSet<ObjectId>> ContentIdsAsync(Repository repo, string path)
	{
		var leaf = (await repo.Trees.GetAsync((await repo.GetMainAsync()).Tree, path))!;
		var ids = new HashSet<ObjectId> { leaf.Id };
		ids.UnionWith((await repo.Objects.ChunksAsync(leaf)).Select(chunk => chunk.Id));
		return ids;
	}

	[Fact]
	public async Task ALazyCheckout_WritesPlaceholders_AndFetchesNoContent()
	{
		var (repo, store, client, lazy) = await SetupAsync();
		var big = await ContentIdsAsync(repo.Repo, "api/src/big.cs");
		repo.Repo.Objects.ClearCache();
		store.Reads.Clear();

		var (state, local) = await client.CreateAsync("lazy", temp.Combine("ws"), context: ContextSpec.Parse(["*,api/"]), lazy: true);
		Assert.True(local.IsLazy);
		Assert.Equal(1, lazy.Mounts);
		Assert.Empty(store.Reads.Intersect(big));
		var files = local.Files;
		Assert.NotEqual(local.Root, files);
		Assert.Equal(Files["api/src/big.cs"]!.Length, new FileInfo(Path.Combine(files, "api", "src", "big.cs")).Length);
		Assert.True(local.State.Entries["api/src/big.cs"].Placeholder);
		// .bassia.toml is written in full: a snapshot reads its ignore rules.
		Assert.False(local.State.Entries[".bassia.toml"].Placeholder);
		Assert.Equal(Files[".bassia.toml"], await File.ReadAllTextAsync(Path.Combine(files, ".bassia.toml")));
		Assert.True(Directory.Exists(Path.Combine(files, "web")));
		Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(files, "web")));
		if (!OperatingSystem.IsWindows())
		{
			Assert.True((File.GetUnixFileMode(Path.Combine(files, "api", "tool.sh")) & UnixFileMode.UserExecute) == 0);
		}

		// Untouched placeholders are no change.
		Assert.False((await client.SnapshotAsync(state)).Changed);
		Assert.Empty(store.Reads.Intersect(big));

		// Hydrating one fetches that file only, keeps its size and time, and is still no change.
		var reloaded = client.Local(state)!;
		Assert.True(await Placeholders.HydrateAsync(repo.Repo, files, "api/src/big.cs", reloaded.State.Entries["api/src/big.cs"]));
		Assert.Equal(Files["api/src/big.cs"], await File.ReadAllTextAsync(Path.Combine(files, "api", "src", "big.cs")));
		Assert.NotEmpty(store.Reads.Intersect(big));
		Assert.False(await Placeholders.HydrateAsync(repo.Repo, files, "api/src/big.cs", reloaded.State.Entries["api/src/big.cs"] with { Size = 1 }));
		Assert.False((await client.SnapshotAsync(state)).Changed);

		// Edits and deletions in the backing directory are snapshotted as usual.
		await File.AppendAllTextAsync(Path.Combine(files, "api", "src", "big.cs"), "// more\n");
		File.Delete(Path.Combine(files, "api", "src", "server.cs"));
		var snapshot = await client.SnapshotAsync(state);
		Assert.Equal(["api/src/big.cs", "api/src/server.cs"], snapshot.Changes.Select(change => change.Path).Order(StringComparer.Ordinal));
		Assert.Null(snapshot.Changes.Single(change => change.Path == "api/src/server.cs").Leaf);

		// Removing the folder unmounts it and deletes the backing directory.
		await client.RemoveDirectoryAsync(snapshot.Workspace);
		Assert.Equal(1, lazy.Unmounts);
		Assert.False(Directory.Exists(files));
	}

	[Fact]
	public async Task ALazyFolder_NeedsAVirtualFileSystem()
	{
		var store = new CountingStore();
		var repo = new TestRepo(store);
		await repo.Repo.InitializeAsync("test");
		var client = new WorkspaceClient(repo.Repo, repo.Landing, temp.Combine(".state"), "tester");
		var refused = await Assert.ThrowsAsync<VersionException>(() => client.CreateAsync("lazy", temp.Combine("ws"), lazy: true));
		Assert.Contains("lazy", refused.Message);
		Assert.Null(await repo.Repo.OpenWorkspaceNamedAsync("lazy"));
	}

	private static bool FuseHere()
	{
		var why = FuseServer.Unavailable();
		if (why is not null && Environment.GetEnvironmentVariable("BASSIA_TEST_REQUIRE_FUSE") == "1")
		{
			Assert.Fail($"FUSE is required here but unavailable: {why}");
		}

		return why is null;
	}

	[Fact]
	public async Task ThroughFuse_ReadingAFileHydratesOnlyIt_AndWritesAreSnapshotted()
	{
		if (!FuseHere())
		{
			return;
		}

		Thread? thread = null;
		FuseServer? server = null;
		LocalWorkspace? mounted = null;
		Repository? repository = null;
		var (repo, store, client, _) = await SetupAsync(async local =>
		{
			mounted = local;
			var hydrator = new Hydrator(repository!, local);
			server = new FuseServer(local.Files, hydrator.Hydrate);
			thread = new Thread(() => server.Run(local.Root)) { IsBackground = true };
			thread.Start();
			await WaitAsync(() => IsMountPoint(local.Root));
		});
		repository = repo.Repo;
		var big = await ContentIdsAsync(repo.Repo, "api/src/big.cs");
		var server_ = await ContentIdsAsync(repo.Repo, "api/src/server.cs");
		repo.Repo.Objects.ClearCache();
		store.Reads.Clear();

		var (state, local) = await client.CreateAsync("fuse", temp.Combine("ws"), context: ContextSpec.Parse(["*,api/"]), lazy: true);
		try
		{
			var root = local.Root;
			Assert.True(File.Exists(Path.Combine(root, CheckoutProvider.MarkerName)));
			Assert.Equal(["api", "web"], Directory.GetDirectories(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
			Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "web")));
			Assert.Equal(Files["api/src/big.cs"]!.Length, new FileInfo(Path.Combine(root, "api", "src", "big.cs")).Length);
			Assert.Empty(store.Reads.Intersect(big));

			// Reading one file fetches exactly its content.
			Assert.Equal(Files["api/src/server.cs"], await File.ReadAllTextAsync(Path.Combine(root, "api", "src", "server.cs")));
			Assert.NotEmpty(store.Reads.Intersect(server_));
			Assert.Empty(store.Reads.Intersect(big));
			Assert.Equal(1, server!.Hydrated);
			Assert.False((await client.SnapshotAsync(state)).Changed);

			// Writing through the mount: an edit of a placeholder keeps the rest of its content, a new file is added.
			await File.AppendAllTextAsync(Path.Combine(root, "api", "src", "big.cs"), "// more\n");
			await File.WriteAllTextAsync(Path.Combine(root, "api", "new.cs"), "class New {}\n");
			File.Move(Path.Combine(root, "README.md"), Path.Combine(root, "README.txt"));
			var snapshot = await client.SnapshotAsync(state);
			Assert.Equal(["README.md", "README.txt", "api/new.cs", "api/src/big.cs"], snapshot.Changes.Select(change => change.Path).Order(StringComparer.Ordinal));
			var working = await repo.FilesAsync((await repo.Repo.Objects.ReadCommitAsync(snapshot.Workspace.Record.Working)).Tree);
			Assert.Equal(Files["api/src/big.cs"] + "// more\n", working["api/src/big.cs"]);
			Assert.Equal("# acme\n", working["README.txt"]);
			Assert.Equal("app();\n", working["web/app.js"]);

			// A change outside the context is refused, as in a full folder.
			await File.WriteAllTextAsync(Path.Combine(root, "web", "x.js"), "x\n");
			await Assert.ThrowsAsync<ContextBoundaryException>(() => client.SnapshotAsync(snapshot.Workspace));
		}
		finally
		{
			Unmount(local.Root);
			thread?.Join(TimeSpan.FromSeconds(10));
		}

		Assert.False(IsMountPoint(local.Root));
		Assert.NotNull(mounted);
	}

	[Fact]
	public async Task TheCli_ServesALazyFolderInTheBackground_AndRemovesIt()
	{
		if (!FuseHere() || Environment.GetEnvironmentVariable("BASSIA_TEST_POSTGRES") is not { Length: > 0 })
		{
			return;
		}

		var root = temp.Combine("repo");
		await OkAsync(temp.Path, "init", "-path", root);
		await OkAsync(root, "workspace", "create", "base");
		Directory.CreateDirectory(Path.Combine(root, "workspaces", "base", "api"));
		await File.WriteAllTextAsync(Path.Combine(root, "workspaces", "base", "api", "a.cs"), "a\n");
		await File.WriteAllTextAsync(Path.Combine(root, "workspaces", "base", "api", "b.cs"), "b\n");
		await OkAsync(root, "workspace", "land", "base");

		var created = await OkAsync(root, "workspace", "create", "lazy", "-materialize", "lazy");
		var folder = Path.Combine(root, "workspaces", "lazy");
		try
		{
			Assert.Equal("lazy", created["materialize"]);
			Assert.Equal(2L, created["files_placeholders"]);
			Assert.True(IsMountPoint(folder));
			Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(folder, "api", "a.cs")));
			await File.WriteAllTextAsync(Path.Combine(folder, "api", "c.cs"), "c\n");
			var status = await OkAsync(folder, "workspace", "status");
			Assert.Contains("1 added", (string)status["message"]);

			await OkAsync(root, "workspace", "unmount", "lazy");
			Assert.False(IsMountPoint(folder));
			await OkAsync(root, "workspace", "mount", "lazy");
			Assert.True(IsMountPoint(folder));
			Assert.Equal("b\n", await File.ReadAllTextAsync(Path.Combine(folder, "api", "b.cs")));
			Assert.Contains("hydrated api/b.cs", await File.ReadAllTextAsync(Path.Combine(root, ".bassia", "workspaces", (string)created["id"] + ".mount.log")));

			await OkAsync(root, "workspace", "close", "lazy", "-remove");
			Assert.False(IsMountPoint(folder));
			Assert.False(Directory.Exists(folder));
		}
		finally
		{
			Unmount(folder);
		}
	}

	private static bool IsMountPoint(string path)
	{
		var full = Path.GetFullPath(path).TrimEnd('/');
		return File.ReadLines("/proc/self/mountinfo").Any(line => line.Split(' ') is { Length: > 4 } fields && fields[4] == full);
	}

	private static void Unmount(string path)
	{
		if (OperatingSystem.IsLinux() && IsMountPoint(path))
		{
			using var process = Process.Start(new ProcessStartInfo("fusermount3", ["-u", "-z", path]) { UseShellExecute = false })!;
			process.WaitForExit();
		}
	}

	private static async Task WaitAsync(Func<bool> condition)
	{
		for (var waited = 0; !condition(); waited++)
		{
			Assert.True(waited < 200, "timed out");
			await Task.Delay(50);
		}
	}

	internal sealed class FakeLazyFolders(Func<LocalWorkspace, Task>? mount) : ILazyFolders
	{
		public int Mounts { get; private set; }

		public int Unmounts { get; private set; }

		public string? Unavailable => null;

		public async Task MountAsync(LocalWorkspace local, CancellationToken ct = default)
		{
			Mounts++;
			if (mount is not null)
			{
				await mount(local);
			}
		}

		public Task UnmountAsync(LocalWorkspace local, CancellationToken ct = default)
		{
			Unmounts++;
			return Task.CompletedTask;
		}

		public bool IsMounted(LocalWorkspace local) => false;
	}

	/// <summary>An in-memory store that records the ids it is asked to read.</summary>
	internal sealed class CountingStore : IObjectStore
	{
		private readonly MemoryObjectStore inner = new();

		public HashSet<ObjectId> Reads { get; } = [];

		public ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default) => inner.HasAsync(ids, ct);

		public ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default) => inner.StatAsync(id, ct);

		public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default)
		{
			lock (Reads)
			{
				Reads.Add(id);
			}

			return inner.ReadAsync(id, range, ct);
		}

		public ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => inner.PutAsync(id, bytes, ct);

		public ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default) => inner.BeginUploadAsync(id, length, ct);
	}
}
