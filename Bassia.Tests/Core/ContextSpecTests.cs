namespace Bassia.Tests.Core;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Core.Workspaces;

/// <summary>Context-specs: a workspace that contains only part of the monorepo, holds and fetches only that, and changes nothing else.</summary>
public sealed class ContextSpecTests
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
	public void Rules_LastMatchDecides(string rules, string path, bool inside) =>
		Assert.Equal(inside, ContextSpec.Parse([rules]).Contains(path));

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
		Assert.Equal(may, ContextSpec.Parse([rules]).MayContainUnder(dir));

	[Theory]
	[InlineData("/abs")]
	[InlineData("api/../web")]
	[InlineData("!")]
	public void Rules_RejectWhatIsNotAPathFromTheRoot(string rule)
	{
		// A leading slash is trimmed ("/abs" is "abs"): only '..', '.', and empty rules are refused.
		if (rule == "/abs")
		{
			Assert.True(ContextSpec.Parse([rule]).Contains("abs/x"));
			return;
		}

		Assert.Throws<VersionException>(() => ContextSpec.Parse([rule]));
	}

	[Fact]
	public void Policy_ReadsTheDefaultAndProfiles()
	{
		var policy = RepoPolicy.Parse("""
			[context]
			default = ["*", "docs/"]
			[context.profiles]
			api = ["*", "api/", "!api/testdata/"]
			web = ["*", "web/"]
			""");
		Assert.Equal(["*", "docs/"], policy.ContextDefault);
		Assert.Equal(["*", "api/", "!api/testdata/"], policy.ContextProfiles["api"]);
		Assert.Equal(["*", "web/"], policy.ContextProfiles["web"]);
		Assert.False(policy.HasLoadTable);
		Assert.True(RepoPolicy.Parse("[load]\ndefault = [\"*\"]\n").HasLoadTable);
	}

	[Fact]
	public void Spec_AddsRemovesAndCompares()
	{
		var spec = ContextSpec.Parse(["api/,*"]);
		Assert.Equal("api/,*,docs/", spec.Add(["docs/"]).ToString());
		Assert.Equal("*", spec.Remove(["api/"]).ToString());
		Assert.Equal("all", spec.Remove(["api/", "*"]).ToString());
		Assert.Throws<VersionException>(() => spec.Remove(["web/"]));
		Assert.Equal(ContextSpec.Parse(["api/", "*"]), spec);
		Assert.NotEqual(ContextSpec.Parse(["*,api/"]), spec);
		Assert.Equal(ContextSpec.All, ContextSpec.Of(null));
		Assert.Null(ContextSpec.All.ToStored());
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
	public async Task AWorkspace_HoldsOnlyItsContext_AndShowsTheRestAsEmptyFolders()
	{
		var (_, client, dir, _) = await SetupAsync();
		using var cleanup = dir;
		var (_, local) = await client.CreateAsync("api-fix", dir.Combine("ws"), context: ContextSpec.Parse(["*,api/,!api/testdata/"]));
		var root = dir.Combine("ws");
		Assert.True(File.Exists(Path.Combine(root, "README.md")));
		Assert.True(File.Exists(Path.Combine(root, "api", "src", "server.cs")));
		Assert.False(File.Exists(Path.Combine(root, "api", "testdata", "fixture.txt")));
		Assert.False(File.Exists(Path.Combine(root, "web", "app.js")));
		Assert.True(Directory.Exists(Path.Combine(root, "web")), "a directory outside the context shows, empty");
		Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "web")));
		Assert.Equal(["README.md", "api/src/server.cs"], local.State.Entries.Keys.Order(StringComparer.Ordinal));
		Assert.Equal(["*", "api/", "!api/testdata/"], local.State.Context.Patterns);
	}

	[Fact]
	public async Task ACheckout_ReadsNoObjectOutsideItsContext()
	{
		var (repo, client, dir, store) = await SetupAsync();
		using var cleanup = dir;
		var webTree = (await repo.Repo.Trees.GetAsync((await repo.Repo.GetMainAsync()).Tree, "web"))!.Id;
		var webFile = (await repo.Repo.Trees.GetAsync((await repo.Repo.GetMainAsync()).Tree, "web/app.js"))!.Id;
		repo.Repo.Objects.ClearCache();
		store.Reads.Clear();
		await client.CreateAsync("api-only", dir.Combine("ws"), context: ContextSpec.Parse(["api/"]));
		Assert.DoesNotContain(webTree, store.Reads);
		Assert.DoesNotContain(webFile, store.Reads);
	}

	[Fact]
	public async Task ASnapshot_NeverDeletesWhatIsOutsideTheContext_AndLandsOnlyItsEdits()
	{
		var (repo, client, dir, _) = await SetupAsync();
		using var cleanup = dir;
		var (state, _) = await client.CreateAsync("api-fix", dir.Combine("ws"), context: ContextSpec.Parse(["*,api/"]));
		var root = dir.Combine("ws");
		await File.WriteAllTextAsync(Path.Combine(root, "api", "src", "server.cs"), "class Server { int port; }\n");
		await File.WriteAllTextAsync(Path.Combine(root, "api", "src", "client.cs"), "class Client {}\n");
		File.Delete(Path.Combine(root, "README.md"));

		var snapshot = await client.SnapshotAsync(state);
		Assert.Equal(["README.md", "api/src/client.cs", "api/src/server.cs"], snapshot.Changes.Select(change => change.Path).Order(StringComparer.Ordinal));

		Assert.Equal(LandOutcome.Landed, (await client.LandAsync(snapshot.Workspace, new LandRequest(state.Id))).Outcome);
		var main = await repo.MainFilesAsync();
		Assert.Equal("app();\n", main["web/app.js"]);
		Assert.Equal("util();\n", main["web/lib/util.js"]);
		Assert.Equal("guide\n", main["docs/guide.md"]);
		Assert.Equal("fixture\n", main["api/testdata/fixture.txt"]);
		Assert.Equal("class Server { int port; }\n", main["api/src/server.cs"]);
		Assert.False(main.ContainsKey("README.md"));
	}

	[Theory]
	[InlineData("web/NOTES.md")]
	[InlineData("docs/new.md")]
	[InlineData("api/testdata/fixture.txt")]
	public async Task ASnapshot_RefusesAChangeOutsideTheContext_AndStoresNothing(string path)
	{
		var (repo, client, dir, _) = await SetupAsync();
		using var cleanup = dir;
		var (state, _) = await client.CreateAsync("api-fix", dir.Combine("ws"), context: ContextSpec.Parse(["*,api/,!api/testdata/"]));
		var root = dir.Combine("ws");
		await File.WriteAllTextAsync(Path.Combine(root, "api", "src", "server.cs"), "edited\n");
		var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		await File.WriteAllTextAsync(full, "outside\n");

		var refused = await Assert.ThrowsAsync<ContextBoundaryException>(() => client.SnapshotAsync(state));
		Assert.Equal([path], refused.Paths);
		Assert.Contains("workspace context -add", refused.Message);
		var after = (await repo.Repo.FindWorkspaceAsync(state.Id))!;
		Assert.Equal(state.Record.Working, after.Record.Working);
		await Assert.ThrowsAsync<ContextBoundaryException>(() => client.LandAsync(after, new LandRequest(state.Id)));

		// Widening the context admits the change (the edit inside was snapshotted first, when the context changed).
		File.Delete(full);
		(state, _) = await client.SetContextAsync(after, ContextSpec.Parse(["*,api/,!api/testdata/"]).Add([path]));
		await File.WriteAllTextAsync(full, "outside\n");
		var snapshot = await client.SnapshotAsync(state);
		Assert.Equal([path], snapshot.Changes.Select(change => change.Path));
		var working = await repo.FilesAsync((await repo.Repo.Objects.ReadCommitAsync(snapshot.Workspace.Record.Working)).Tree);
		Assert.Equal("edited\n", working["api/src/server.cs"]);
		Assert.Equal("outside\n", working[path]);
	}

	[Fact]
	public async Task Sync_UpdatesFilesInContext_AndLeavesTheRestOnTheServer()
	{
		var (repo, client, dir, _) = await SetupAsync();
		using var cleanup = dir;
		var (state, _) = await client.CreateAsync("api-fix", dir.Combine("ws"), context: ContextSpec.Parse(["api/"]));
		await repo.CommitAsync(new Dictionary<string, string?> { ["api/src/server.cs"] = "class Server2 {}\n", ["web/app.js"] = "app2();\n", ["web/new/x.js"] = "x\n" }, "other");

		await client.SyncAsync(state);
		var root = dir.Combine("ws");
		Assert.Equal("class Server2 {}\n", await File.ReadAllTextAsync(Path.Combine(root, "api", "src", "server.cs")));
		Assert.False(File.Exists(Path.Combine(root, "web", "app.js")));
		Assert.False(Directory.Exists(Path.Combine(root, "web", "new")));
	}

	[Fact]
	public async Task ChangingTheContext_IsAnOperation_WritesWhatItAdds_AndUndoRevertsIt()
	{
		var (repo, client, dir, _) = await SetupAsync();
		using var cleanup = dir;
		var (state, _) = await client.CreateAsync("work", dir.Combine("ws"), context: ContextSpec.Parse(["api/"]));
		var root = dir.Combine("ws");
		Assert.Equal(["api/"], state.Record.Context);

		(state, var result) = await client.SetContextAsync(state, ContextSpec.Parse(["api/,web/"]));
		Assert.Equal(["api/", "web/"], state.Record.Context);
		Assert.Equal(new ContextResult(2, 0), result);
		Assert.Equal("app();\n", await File.ReadAllTextAsync(Path.Combine(root, "web", "app.js")));
		Assert.StartsWith("context:", state.Operation.Description);

		(state, _, _) = await client.UndoAsync(state);
		Assert.Equal(["api/"], state.Record.Context);
		Assert.False(File.Exists(Path.Combine(root, "web", "app.js")));
		Assert.Equal(["api/src/server.cs", "api/testdata/fixture.txt"], client.Local(state)!.State.Entries.Keys.Order(StringComparer.Ordinal));

		// Everything: the rest of the tree appears.
		(state, _) = await client.SetContextAsync(state, ContextSpec.All);
		Assert.Null(state.Record.Context);
		Assert.Equal("guide\n", await File.ReadAllTextAsync(Path.Combine(root, "docs", "guide.md")));
	}

	[Fact]
	public async Task NarrowingTheContext_PastAChangedPath_IsRefused()
	{
		var (repo, client, dir, _) = await SetupAsync();
		using var cleanup = dir;
		var (state, _) = await client.CreateAsync("work", dir.Combine("ws"), context: ContextSpec.Parse(["api/,web/"]));
		var root = dir.Combine("ws");
		await File.WriteAllTextAsync(Path.Combine(root, "web", "app.js"), "edited\n");

		var refused = await Assert.ThrowsAsync<ContextBoundaryException>(() => client.SetContextAsync(state, ContextSpec.Parse(["api/"])));
		Assert.Equal(["web/app.js"], refused.Paths);
		state = (await repo.Repo.FindWorkspaceAsync(state.Id))!;
		Assert.Equal(["api/", "web/"], state.Record.Context);
		// The edit was snapshotted before the refusal, and stays in the folder.
		Assert.Equal("edited\n", await File.ReadAllTextAsync(Path.Combine(root, "web", "app.js")));

		// Narrowing to what does not drop it is fine: the dropped files leave the folder.
		(state, var result) = await client.SetContextAsync(state, ContextSpec.Parse(["web/"]));
		Assert.Equal(new ContextResult(0, 2), result);
		Assert.False(Directory.EnumerateFiles(Path.Combine(root, "api"), "*", SearchOption.AllDirectories).Any());
	}

	[Fact]
	public async Task StackedClonedAndCheckedOutWorkspaces_ShareTheContextOfTheirSource()
	{
		var (repo, client, dir, _) = await SetupAsync();
		using var cleanup = dir;
		var (parent, _) = await client.CreateAsync("parent", dir.Combine("p"), context: ContextSpec.Parse(["api/"]));
		var (stacked, _) = await client.CreateAsync("stacked", dir.Combine("s"), on: parent.Id);
		Assert.Equal(["api/"], stacked.Record.Context);
		var (own, _) = await client.CreateAsync("own", dir.Combine("o"), on: parent.Id, context: ContextSpec.Parse(["web/"]));
		Assert.Equal(["web/"], own.Record.Context);
		var (cloned, _) = await client.CloneAsync(parent.Id, "cloned", dir.Combine("c"));
		Assert.Equal(["api/"], cloned.Record.Context);

		// Another machine checking out the workspace gets the same context: it is on the record, not in the folder.
		var elsewhere = new WorkspaceClient(repo.Repo, repo.Landing, dir.Combine(".state2"), "tester");
		var local = await elsewhere.CheckOutAsync((await repo.Repo.FindWorkspaceAsync(parent.Id))!, dir.Combine("p2"));
		Assert.Equal(["api/src/server.cs", "api/testdata/fixture.txt"], local.State.Entries.Keys.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task ARecordWithoutAContext_KeepsItsEncoding()
	{
		var (repo, client, dir, _) = await SetupAsync();
		using var cleanup = dir;
		var (state, _) = await client.CreateAsync("all", dir.Combine("ws"));
		Assert.Null(state.Record.Context);
		var view = new View(new Dictionary<string, ObjectId>(), state.Record);
		var withContext = new View(new Dictionary<string, ObjectId>(), state.Record with { Context = ["api/"] });
		var bytes = ObjectCodec.Encode(view);
		Assert.Null(((View)ObjectCodec.Decode(bytes)).Workspace!.Context);
		Assert.Equal(bytes, ObjectCodec.Encode((View)ObjectCodec.Decode(bytes)));
		Assert.Equal(["api/"], ((View)ObjectCodec.Decode(ObjectCodec.Encode(withContext))).Workspace!.Context);
		Assert.True(ObjectCodec.Encode(withContext).Length > bytes.Length);
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
