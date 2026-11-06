namespace Bassia.Tests.Core;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Core.Workspaces;

/// <summary>Load rules: a workspace folder that holds, fetches and caches only part of the monorepo.</summary>
public sealed class LoadRulesTests
{
	[Theory]
	[InlineData("api/", "api/src/a.cs", true)]
	[InlineData("api", "api/src/a.cs", true)]
	[InlineData("api", "api", true)]
	[InlineData("api/", "apix/a.cs", false)]
	[InlineData("api/", "web/a.cs", false)]
	[InlineData("*", "README.md", true)]
	[InlineData("*", "api/a.cs", false)]
	[InlineData("*.md", "README.md", true)]
	[InlineData("*.md", "docs/a.md", false)]
	[InlineData("**/*.md", "docs/deep/a.md", true)]
	[InlineData("**/*.md", "README.md", true)]
	[InlineData("libs/*/", "libs/core/x/y.cs", true)]
	[InlineData("libs/*/", "libs/top.cs", false)]
	[InlineData("libs/*", "libs/top.cs", true)]
	[InlineData("libs/*", "libs/core/y.cs", false)]
	[InlineData("api/,!api/testdata/", "api/testdata/big.bin", false)]
	[InlineData("api/,!api/testdata/", "api/src/a.cs", true)]
	[InlineData("!api/testdata/,api/", "api/testdata/big.bin", true)]
	[InlineData("web/", ".bassia.toml", true)]
	[InlineData("all", "anything/at/all.txt", true)]
	[InlineData("", "anything/at/all.txt", true)]
	public void Rules_LastMatchDecides(string rules, string path, bool loaded) =>
		Assert.Equal(loaded, LoadRules.Parse([rules]).IsLoaded(path));

	[Theory]
	[InlineData("api/", "api", true)]
	[InlineData("api/", "api/src", true)]
	[InlineData("api/", "web", false)]
	[InlineData("libs/shared/", "libs", true)]
	[InlineData("libs/shared/", "libs/other", false)]
	[InlineData("*", "api", false)]
	[InlineData("**/*.proto", "web/deep", true)]
	[InlineData("libs/*/", "libs/x/y", true)]
	public void Rules_PruneDirectoriesNothingCouldMatchIn(string rules, string dir, bool may) =>
		Assert.Equal(may, LoadRules.Parse([rules]).MayLoadUnder(dir));

	[Theory]
	[InlineData("/abs")]
	[InlineData("api/../web")]
	[InlineData("!")]
	public void Rules_RejectWhatIsNotAPathFromTheRoot(string rule)
	{
		// A leading slash is trimmed ("/abs" is "abs"): only '..', '.', and empty rules are refused.
		if (rule == "/abs")
		{
			Assert.True(LoadRules.Parse([rule]).IsLoaded("abs/x"));
			return;
		}

		Assert.Throws<VersionException>(() => LoadRules.Parse([rule]));
	}

	[Fact]
	public void Policy_ReadsTheDefaultAndProfiles()
	{
		var policy = RepoPolicy.Parse("""
			[load]
			default = ["*", "docs/"]
			[load.profiles]
			api = ["*", "api/", "!api/testdata/"]
			web = ["*", "web/"]
			""");
		Assert.Equal(["*", "docs/"], policy.LoadDefault);
		Assert.Equal(["*", "api/", "!api/testdata/"], policy.LoadProfiles["api"]);
		Assert.Equal(["*", "web/"], policy.LoadProfiles["web"]);
	}

	private static readonly Dictionary<string, string?> Monorepo = new()
	{
		["README.md"] = "# acme\n",
		["api/src/server.cs"] = "class Server {}\n",
		["api/testdata/fixture.txt"] = "fixture\n",
		["web/app.js"] = "app();\n",
		["web/lib/util.js"] = "util();\n",
		["docs/guide.md"] = "guide\n"
	};

	private static async Task<(TestRepo Repo, WorkspaceClient Client, TempDir Dir, CountingStore Store)> SetupAsync()
	{
		var store = new CountingStore();
		var repo = new TestRepo(store);
		await repo.Repo.InitializeAsync("test");
		await repo.CommitAsync(Monorepo);
		var dir = new TempDir();
		return (repo, new WorkspaceClient(repo.Repo, repo.Landing, dir.Combine(".state"), "tester"), dir, store);
	}

	[Fact]
	public async Task ASparseWorkspace_HoldsOnlyItsRules_AndShowsTheRestAsEmptyFolders()
	{
		var (_, client, dir, _) = await SetupAsync();
		using var _ = dir;
		var (_, local) = await client.CreateAsync("api-fix", dir.Combine("ws"), load: LoadRules.Parse(["*,api/,!api/testdata/"]));
		var root = dir.Combine("ws");
		Assert.True(File.Exists(Path.Combine(root, "README.md")));
		Assert.True(File.Exists(Path.Combine(root, "api", "src", "server.cs")));
		Assert.False(File.Exists(Path.Combine(root, "api", "testdata", "fixture.txt")));
		Assert.False(File.Exists(Path.Combine(root, "web", "app.js")));
		Assert.True(Directory.Exists(Path.Combine(root, "web")), "an unloaded directory shows, empty");
		Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "web")));
		Assert.Equal(["README.md", "api/src/server.cs"], local.State.Entries.Keys.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task ASparseCheckout_ReadsNoObjectOfWhatItDoesNotLoad()
	{
		var (repo, client, dir, store) = await SetupAsync();
		using var _ = dir;
		var webTree = (await repo.Repo.Trees.GetAsync((await repo.Repo.GetMainAsync()).Tree, "web"))!.Id;
		var webFile = (await repo.Repo.Trees.GetAsync((await repo.Repo.GetMainAsync()).Tree, "web/app.js"))!.Id;
		repo.Repo.Objects.ClearCache();
		store.Reads.Clear();
		await client.CreateAsync("api-only", dir.Combine("ws"), load: LoadRules.Parse(["api/"]));
		Assert.DoesNotContain(webTree, store.Reads);
		Assert.DoesNotContain(webFile, store.Reads);
	}

	[Fact]
	public async Task ASparseSnapshot_NeverDeletesWhatIsNotLoaded_AndLandsOnlyItsEdits()
	{
		var (repo, client, dir, _) = await SetupAsync();
		using var _ = dir;
		var (state, _) = await client.CreateAsync("api-fix", dir.Combine("ws"), load: LoadRules.Parse(["*,api/"]));
		var root = dir.Combine("ws");
		await File.WriteAllTextAsync(Path.Combine(root, "api", "src", "server.cs"), "class Server { int port; }\n");
		await File.WriteAllTextAsync(Path.Combine(root, "api", "src", "client.cs"), "class Client {}\n");
		// An agent writing into an unloaded folder adds that file; the folder's other files are untouched.
		await File.WriteAllTextAsync(Path.Combine(root, "web", "NOTES.md"), "notes\n");

		var snapshot = await client.SnapshotAsync(state);
		Assert.Equal(["api/src/client.cs", "api/src/server.cs", "web/NOTES.md"], snapshot.Changes.Select(change => change.Path).Order(StringComparer.Ordinal));
		Assert.All(snapshot.Changes, change => Assert.NotNull(change.Leaf));

		Assert.Equal(LandOutcome.Landed, (await client.LandAsync(snapshot.Workspace, new LandRequest(state.Id))).Outcome);
		var main = await repo.MainFilesAsync();
		Assert.Equal("app();\n", main["web/app.js"]);
		Assert.Equal("util();\n", main["web/lib/util.js"]);
		Assert.Equal("guide\n", main["docs/guide.md"]);
		Assert.Equal("class Server { int port; }\n", main["api/src/server.cs"]);
		Assert.Equal("notes\n", main["web/NOTES.md"]);
	}

	[Fact]
	public async Task Sync_UpdatesLoadedFiles_AndLeavesTheRestOnTheServer()
	{
		var (repo, client, dir, _) = await SetupAsync();
		using var _ = dir;
		var (state, _) = await client.CreateAsync("api-fix", dir.Combine("ws"), load: LoadRules.Parse(["api/"]));
		await repo.CommitAsync(new Dictionary<string, string?> { ["api/src/server.cs"] = "class Server2 {}\n", ["web/app.js"] = "app2();\n", ["web/new/x.js"] = "x\n" }, "other");

		await client.SyncAsync(state);
		var root = dir.Combine("ws");
		Assert.Equal("class Server2 {}\n", await File.ReadAllTextAsync(Path.Combine(root, "api", "src", "server.cs")));
		Assert.False(File.Exists(Path.Combine(root, "web", "app.js")));
		Assert.False(Directory.Exists(Path.Combine(root, "web", "new")));
	}

	[Fact]
	public async Task ChangingTheRules_LoadsWhatTheyAdd_AndKeepsEditsToWhatTheyDrop()
	{
		var (repo, client, dir, _) = await SetupAsync();
		using var _ = dir;
		var (state, _) = await client.CreateAsync("work", dir.Combine("ws"), load: LoadRules.Parse(["api/"]));
		var root = dir.Combine("ws");
		await File.WriteAllTextAsync(Path.Combine(root, "api", "src", "server.cs"), "edited\n");

		var (after, local, result) = await client.LoadAsync(state, LoadRules.Parse(["web/"]));
		Assert.Equal(2, result.Loaded);
		Assert.Equal(2, result.Unloaded);
		Assert.Equal("app();\n", await File.ReadAllTextAsync(Path.Combine(root, "web", "app.js")));
		Assert.False(File.Exists(Path.Combine(root, "api", "src", "server.cs")));
		Assert.Equal(["web/app.js", "web/lib/util.js"], local.State.Entries.Keys.Order(StringComparer.Ordinal));

		// The edit made before the rules changed is kept on the workspace, and survives a snapshot of the new folder.
		after = (await client.SnapshotAsync(after)).Workspace;
		var working = await repo.FilesAsync((await repo.Repo.Objects.ReadCommitAsync(after.Record.Working)).Tree);
		Assert.Equal("edited\n", working["api/src/server.cs"]);
		Assert.Equal("app();\n", working["web/app.js"]);

		// Back to everything: the edited file comes back with the edit.
		await client.LoadAsync(after, LoadRules.All);
		Assert.Equal("edited\n", await File.ReadAllTextAsync(Path.Combine(root, "api", "src", "server.cs")));
		Assert.Equal("fixture\n", await File.ReadAllTextAsync(Path.Combine(root, "api", "testdata", "fixture.txt")));
	}

	/// <summary>An in-memory store that records the ids it is asked to read.</summary>
	private sealed class CountingStore : IObjectStore
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
