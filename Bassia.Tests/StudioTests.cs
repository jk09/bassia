namespace Bassia.Tests;

using System.Net;
using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Server;
using Bassia.Stores;
using Bassia.Studio;
using Bassia.Studio.Components.Shared;
using static Bassia.Tests.TestEnvironment;

/// <summary>
/// Bassia Studio against an in-process bassia-server: pages are prerendered on the server, so plain HTTP requests see
/// what a browser first shows.
/// </summary>
public sealed class StudioTests : IAsyncLifetime
{
	private const string Token = "studio-token";
	private readonly TempDirectory temp = new();
	private ServerApp server = null!;
	private StudioApp studio = null!;
	private HttpClient http = null!;
	private string workspaceId = "";
	private readonly List<MonorepoClient> clients = [];

	/// <summary>Studio's actions for a user, over their own connection to the server.</summary>
	private StudioActions As(string user, StudioRole role = StudioRole.Contributor)
	{
		var client = new MonorepoClient(new StudioOptions { Server = server.Address!, Token = Token, Cache = temp.Combine("cache-" + user) });
		clients.Add(client);
		return new StudioActions(client, new StudioUser(user, role));
	}

	private async Task<string?> MainlineTextAsync(string path)
	{
		var main = await server.Repo.GetMainAsync();
		return await server.Repo.Trees.GetAsync(main.Tree, path) is { } leaf ? Encoding.UTF8.GetString(await server.Repo.Objects.ReadContentAsync(leaf)) : null;
	}

	private static async Task<Leaf?> LeafAsync(Repository repo, WorkspaceState state, string path) =>
		await repo.Trees.GetAsync((await repo.Objects.ReadCommitAsync(state.Record.Working)).Tree, path);

	public async Task InitializeAsync()
	{
		var chunks = new ChunkParameters(1024, 4096, 16384);
		server = new ServerApp(new ServerOptions { Data = temp.Combine("server"), Postgres = Backends.RequiredPostgres(), Url = "http://127.0.0.1:0", Token = Token, Chunks = chunks });
		await server.StartAsync();
		await SeedAsync(chunks);
		studio = new StudioApp(new StudioOptions { Server = server.Address!, Token = Token, Url = "http://127.0.0.1:0", Cache = temp.Combine("cache"), Label = "test" });
		await studio.StartAsync();
		http = new HttpClient { BaseAddress = new Uri(studio.Address!) };
	}

	public async Task DisposeAsync()
	{
		foreach (var client in clients)
		{
			client.Dispose();
		}

		http.Dispose();
		await studio.DisposeAsync();
		await server.DisposeAsync();
		temp.Dispose();
	}

	/// <summary>Two landings (add, then edit and add a binary file) and an open workspace with an unlanded edit.</summary>
	private async Task SeedAsync(ChunkParameters chunks)
	{
		using var connection = new ServerConnection(server.Address!, Token);
		var repo = new Repository(new RemoteObjectStore(connection), new RemoteViewStore(connection), new RemotePathIndex(connection), new RemoteLeaseStore(connection), chunks);
		var landing = new RemoteLanding(connection);
		var service = new WorkspaceService(repo);

		async Task<WorkspaceState> EditAsync(WorkspaceState ws, string intent, params (string Path, byte[]? Content)[] files)
		{
			var tree = (await repo.Objects.ReadCommitAsync(ws.Record.Working)).Tree;
			var changes = new List<PathChange>();
			foreach (var (path, content) in files)
			{
				changes.Add(new PathChange(path, content is null ? null : await repo.Objects.WriteContentAsync(content)));
			}

			await service.DescribeAsync(ws.Id, intent, "tester");
			return await service.SetTreeAsync(ws.Id, await repo.Trees.EditAsync(tree, changes), intent, "tester", null);
		}

		var first = await service.CreateAsync("first", "alice");
		await EditAsync(first, "Add the greeter", ("src/hello.txt", Encoding.UTF8.GetBytes("hello\nworld\n")), ("README.md", Encoding.UTF8.GetBytes("# Demo\n")));
		Assert.Equal(LandOutcome.Landed, (await landing.LandAsync(new LandRequest(first.Id))).Outcome);

		var second = await service.CreateAsync("second", "bob");
		await EditAsync(second, "Greet the moon", ("src/hello.txt", Encoding.UTF8.GetBytes("hello\nmoon\n")), ("assets/logo.bin", [0, 1, 2, 0, 255]));
		Assert.Equal(LandOutcome.Landed, (await landing.LandAsync(new LandRequest(second.Id))).Outcome);

		var open = await service.CreateAsync("open-work", "carol");
		await EditAsync(open, "Document the greeter", ("docs/greeter.md", Encoding.UTF8.GetBytes("Says hello.\n")));
		workspaceId = open.Id;
	}

	private async Task<string> PageAsync(string path)
	{
		using var response = await http.GetAsync(path);
		var body = await response.Content.ReadAsStringAsync();
		Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {body}");
		return WebUtility.HtmlDecode(body);
	}

	[PostgresFact]
	public async Task Overview_ShowsMainlineAndRecentLandings()
	{
		var page = await PageAsync("/");
		Assert.Contains("Bassia Studio", page);
		Assert.Contains("mainline seq", page);
		Assert.Contains("Add the greeter", page);
		Assert.Contains("Greet the moon", page);
	}

	[PostgresFact]
	public async Task Depot_ListsFoldersAndShowsFileContentAtAnySeq()
	{
		var root = await PageAsync("/depot");
		Assert.Contains("src/", root);
		Assert.Contains("assets/", root);

		var latest = await PageAsync("/depot?path=src/hello.txt");
		Assert.Contains("moon", latest);

		var older = await PageAsync("/depot?path=src/hello.txt&seq=1");
		Assert.Contains("world", older);
		Assert.DoesNotContain("moon", older);

		var binary = await PageAsync("/depot?path=assets/logo.bin");
		Assert.Contains("binary content", binary);

		var missing = await PageAsync("/depot?path=nope.txt");
		Assert.Contains("'nope.txt' does not exist", missing);
	}

	[PostgresFact]
	public async Task Depot_ShowsPathHistoryNewestFirst()
	{
		var page = await PageAsync("/depot?path=src/hello.txt&tab=history");
		var moon = page.IndexOf("Greet the moon", StringComparison.Ordinal);
		var greeter = page.IndexOf("Add the greeter", StringComparison.Ordinal);
		Assert.True(moon > 0 && greeter > moon, "history lists the newer landing first");
	}

	[PostgresFact]
	public async Task Raw_DownloadsFileContent()
	{
		Assert.Equal([0, 1, 2, 0, 255], await http.GetByteArrayAsync("/raw?path=assets/logo.bin"));
		Assert.Equal("hello\nworld\n", await http.GetStringAsync("/raw?path=src/hello.txt&seq=1"));
		Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/raw?path=src")).StatusCode);
	}

	[PostgresFact]
	public async Task Log_ListsLandingsAndALandingShowsItsDiff()
	{
		var log = await PageAsync("/log");
		Assert.Contains("Greet the moon", log);
		Assert.Contains("Add the greeter", log);

		var unified = await PageAsync("/log/2");
		Assert.Contains("Greet the moon", unified);
		Assert.Contains("src/hello.txt", unified);
		Assert.Contains("-world", unified);
		Assert.Contains("+moon", unified);
		Assert.Contains("assets/logo.bin", unified);

		var split = await PageAsync("/log/2?view=split");
		Assert.Contains("class=\"split\"", split);
		Assert.Contains("world", split);
		Assert.Contains("moon", split);
	}

	[PostgresFact]
	public async Task Workspaces_ListOpenWorkAndShowItsChanges()
	{
		var list = await PageAsync("/workspaces");
		Assert.Contains("open-work", list);
		Assert.DoesNotContain(">first<", list);

		var detail = await PageAsync("/workspaces/" + workspaceId);
		Assert.Contains("docs/greeter.md", detail);
		Assert.Contains("+Says hello.", detail);
		Assert.Contains("carol", detail);
	}

	[PostgresFact]
	public async Task UnreachableServer_IsAMessageNotACrash()
	{
		await using var orphan = new StudioApp(new StudioOptions { Server = "http://127.0.0.1:9", Url = "http://127.0.0.1:0", Cache = temp.Combine("cache2") });
		await orphan.StartAsync();
		using var client = new HttpClient { BaseAddress = new Uri(orphan.Address!) };
		var page = WebUtility.HtmlDecode(await client.GetStringAsync("/log"));
		Assert.Contains("unreachable", page);
	}

	[PostgresFact]
	public void SplitDiff_PairsChangedLinesAndFoldsUnchangedRuns()
	{
		var old = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
		var updated = old.Replace("line 10\n", "line ten\n");
		var rows = SplitDiff.Build(old, updated, context: 2);
		var changed = Assert.Single(rows, row => row.Kind == SplitDiff.Replace);
		Assert.Equal(("line 10", "line ten", 10, 10), (changed.OldText, changed.NewText, changed.OldNumber, changed.NewNumber));
		Assert.Equal(2, rows.Count(row => row.Kind == SplitDiff.Fold));
	}

	// ----- slice (b): workspaces, editing, landing, conflicts, leases -----

	[PostgresFact]
	public async Task Actions_CreateEditRenameDeleteAndLand()
	{
		var dana = As("dana");
		var ws = await dana.CreateAsync("feature", "Add the farewell");
		Assert.Equal("dana", ws.Record.Owner);
		await dana.SaveTextAsync(ws.Id, "src/bye.txt", "bye\n", null);
		await dana.SaveTextAsync(ws.Id, "notes/tmp.txt", "scratch\n", null);
		var repo = server.Repo;
		var tmp = (await LeafAsync(repo, (await repo.FindWorkspaceAsync(ws.Id))!, "notes/tmp.txt"))!;
		await dana.RenameAsync(ws.Id, "src/bye.txt", "src/farewell.txt");
		await dana.DeleteAsync(ws.Id, "notes/tmp.txt", tmp.Id);

		Assert.Equal(LandOutcome.WouldLand, (await dana.CheckAsync(ws.Id)).Outcome);
		var landed = await dana.LandAsync(ws.Id);
		Assert.Equal(LandOutcome.Landed, landed.Outcome);
		Assert.Equal("bye\n", await MainlineTextAsync("src/farewell.txt"));
		Assert.Null(await MainlineTextAsync("src/bye.txt"));
		Assert.Null(await MainlineTextAsync("notes/tmp.txt"));
		var main = await repo.GetMainAsync();
		Assert.Equal("Add the farewell", main.Commit.Intent);
		Assert.Equal("dana", main.Commit.Author.Agent);

		// Each edit was an operation of its own, so the log shows them and undo works.
		var operations = new List<string>();
		await foreach (var entry in repo.LogAsync(HeadName.Workspace(ws.Id)))
		{
			operations.Add(entry.Operation.Description);
		}

		Assert.Contains("add src/bye.txt", operations);
		Assert.Contains("rename src/bye.txt -> src/farewell.txt", operations);
		Assert.Contains("delete notes/tmp.txt", operations);
	}

	[PostgresFact]
	public async Task Actions_StaleEditIsRefusedAndLosesNothing()
	{
		var erin = As("erin");
		var ws = await erin.CreateAsync("stale");
		await erin.SaveTextAsync(ws.Id, "a.txt", "one\n", null);
		var seen = (await LeafAsync(server.Repo, (await server.Repo.FindWorkspaceAsync(ws.Id))!, "a.txt"))!.Id;

		// Someone else (another tab, or the CLI) saves first.
		await erin.SaveTextAsync(ws.Id, "a.txt", "two\n", seen);
		await Assert.ThrowsAsync<StaleEditException>(() => erin.SaveTextAsync(ws.Id, "a.txt", "three\n", seen));
		await Assert.ThrowsAsync<StaleEditException>(() => erin.SaveTextAsync(ws.Id, "a.txt", "new\n", null));
		var leaf = await LeafAsync(server.Repo, (await server.Repo.FindWorkspaceAsync(ws.Id))!, "a.txt");
		Assert.Equal("two\n", Encoding.UTF8.GetString(await server.Repo.Objects.ReadContentAsync(leaf!)));
	}

	[PostgresFact]
	public async Task Actions_SyncStoresConflictsThatResolveToLand()
	{
		var fred = As("fred");
		var mine = await fred.CreateAsync("mine", "Greet the sun");
		await fred.SaveTextAsync(mine.Id, "src/hello.txt", "hello\nsun\n", (await LeafAsync(server.Repo, mine, "src/hello.txt"))!.Id);

		var gina = As("gina");
		var theirs = await gina.CreateAsync("theirs", "Greet the stars");
		await gina.SaveTextAsync(theirs.Id, "src/hello.txt", "hello\nstars\n", (await LeafAsync(server.Repo, theirs, "src/hello.txt"))!.Id);
		Assert.Equal(LandOutcome.Landed, (await gina.LandAsync(theirs.Id)).Outcome);

		// The default policy is replay: landing refuses; a sync stores the path as a conflict.
		var refused = await fred.LandAsync(mine.Id);
		Assert.Equal(LandOutcome.NeedsReplay, refused.Outcome);
		Assert.Equal("src/hello.txt", Assert.Single(refused.Conflicts).Path);
		var sync = await fred.SyncAsync(mine.Id);
		Assert.Equal(["src/hello.txt"], sync.Conflicts);
		var conflict = Assert.Single(await fred.ConflictsAsync(mine.Id));
		var markers = await fred.ConflictTextAsync(conflict);
		var detail = await PageAsync($"/workspaces/{mine.Id}");
		Assert.Contains("unresolved conflict", detail);
		Assert.Contains("Edit merged", detail);
		Assert.Contains("sun", markers);
		Assert.Contains("stars", markers);
		Assert.Equal(LandOutcome.Conflict, (await fred.CheckAsync(mine.Id)).Outcome);

		await fred.ResolveAsync(mine.Id, "src/hello.txt", ConflictChoice.Merged, "hello\nsun\nstars\n");
		Assert.Empty(await fred.ConflictsAsync(mine.Id));
		Assert.Equal(LandOutcome.Landed, (await fred.LandAsync(mine.Id)).Outcome);
		Assert.Equal("hello\nsun\nstars\n", await MainlineTextAsync("src/hello.txt"));
	}

	[PostgresFact]
	public async Task Actions_UndoRestoreAndAbandon()
	{
		var hal = As("hal");
		var ws = await hal.CreateAsync("history");
		await hal.SaveTextAsync(ws.Id, "x.txt", "1\n", null);
		var afterFirst = (await server.Repo.FindWorkspaceAsync(ws.Id))!.Head;
		await hal.SaveTextAsync(ws.Id, "y.txt", "2\n", null);
		var undone = await hal.UndoAsync(ws.Id);
		Assert.Null(await LeafAsync(server.Repo, undone, "y.txt"));
		Assert.NotNull(await LeafAsync(server.Repo, undone, "x.txt"));
		var redone = await hal.UndoAsync(ws.Id);
		Assert.NotNull(await LeafAsync(server.Repo, redone, "y.txt"));
		var restored = await hal.RestoreAsync(ws.Id, afterFirst);
		Assert.Null(await LeafAsync(server.Repo, restored, "y.txt"));
		Assert.Equal(WorkspaceStatus.Abandoned, (await hal.CloseAsync(ws.Id, abandon: true)).Record.Status);
		await Assert.ThrowsAsync<VersionException>(() => hal.SaveTextAsync(ws.Id, "z.txt", "3\n", null));
	}

	[PostgresFact]
	public async Task Actions_StackAndClone()
	{
		var ivy = As("ivy");
		var parent = await ivy.CreateAsync("parent");
		await ivy.SaveTextAsync(parent.Id, "p.txt", "parent\n", null);
		var child = await ivy.CreateAsync("child", on: "parent");
		Assert.Equal(parent.Id, child.Record.Parent);
		Assert.NotNull(await LeafAsync(server.Repo, child, "p.txt"));
		Assert.Equal(LandOutcome.Rejected, (await ivy.CheckAsync(child.Id)).Outcome);

		var clone = await ivy.CloneAsync("parent", "copy");
		Assert.NotNull(await LeafAsync(server.Repo, clone, "p.txt"));
		var parentCommit = await server.Repo.Objects.ReadCommitAsync((await server.Repo.FindWorkspaceAsync(parent.Id))!.Record.Working);
		var cloneCommit = await server.Repo.Objects.ReadCommitAsync(clone.Record.Working);
		Assert.Equal(parentCommit.Tree, cloneCommit.Tree);
		Assert.NotEqual(parentCommit.ChangeId, cloneCommit.ChangeId);
	}

	[PostgresFact]
	public async Task Roles_ReadersReadContributorsOwnAdminsAll()
	{
		var reader = As("rita", StudioRole.Reader);
		await Assert.ThrowsAsync<StudioForbiddenException>(() => reader.CreateAsync("nope"));
		Assert.Equal(LandOutcome.WouldLand, (await reader.CheckAsync(workspaceId)).Outcome);

		// open-work belongs to carol.
		var other = As("sam");
		await Assert.ThrowsAsync<StudioForbiddenException>(() => other.SaveTextAsync(workspaceId, "x.txt", "x\n", null));
		await Assert.ThrowsAsync<StudioForbiddenException>(() => other.LandAsync(workspaceId));
		var carol = As("carol");
		var lease = await carol.AcquireLeaseAsync("docs", LeaseMode.Exclusive, workspaceId, TimeSpan.FromMinutes(5));
		Assert.True(lease.Granted);
		await Assert.ThrowsAsync<StudioForbiddenException>(() => other.ReleaseLeaseAsync(lease.Lease!.Id));

		var admin = As("root", StudioRole.Admin);
		await admin.ReleaseLeaseAsync(lease.Lease!.Id);
		Assert.Empty(await server.Repo.Leases.ListAsync());
		Assert.Equal(LandOutcome.Landed, (await admin.LandAsync(workspaceId)).Outcome);
	}

	[PostgresFact]
	public async Task Leases_ExclusiveLeaseBlocksOthersAndLandsWithItsHolder()
	{
		var uma = As("uma");
		var ws = await uma.CreateAsync("locked");
		var granted = await uma.AcquireLeaseAsync("assets", LeaseMode.Exclusive, ws.Id, TimeSpan.FromMinutes(5));
		Assert.True(granted.Granted);
		var vic = As("vic");
		var other = await vic.CreateAsync("other");
		var blocked = await vic.AcquireLeaseAsync("assets/logo.bin", LeaseMode.Exclusive, other.Id, TimeSpan.FromMinutes(5));
		Assert.False(blocked.Granted);
		Assert.Single(blocked.Blocking);

		await uma.SaveFileAsync(ws.Id, "assets/logo.bin", [9, 9], (await LeafAsync(server.Repo, ws, "assets/logo.bin"))!.Id);
		Assert.Equal(LandOutcome.Landed, (await uma.LandAsync(ws.Id)).Outcome);
		var page = await PageAsync("/leases");
		Assert.Contains("assets", page);
		Assert.Contains("locked", page);
	}

	[PostgresFact]
	public async Task Parity_StudioAndCliMakeTheSameChange()
	{
		var clone = temp.Combine("clone");
		await OkAsync(temp.Path, "clone", "-from", server.Address!, "-path", "clone", "-token", Token);
		await OkAsync(clone, "workspace", "create", "cli");
		Directory.CreateDirectory(Path.Combine(clone, "workspaces", "cli", "docs"));
		await File.WriteAllTextAsync(Path.Combine(clone, "workspaces", "cli", "docs", "same.md"), "identical\n");
		await File.WriteAllTextAsync(Path.Combine(clone, "workspaces", "cli", "src", "hello.txt"), "hello\nplanet\n");
		await OkAsync(clone, "workspace", "snapshot", "-workspace", "cli");

		var wes = As("wes");
		var studio = await wes.CreateAsync("studio");
		await wes.SaveTextAsync(studio.Id, "docs/same.md", "identical\n", null);
		await wes.SaveTextAsync(studio.Id, "src/hello.txt", "hello\nplanet\n", (await LeafAsync(server.Repo, studio, "src/hello.txt"))!.Id);

		var cliState = await server.Repo.ResolveWorkspaceAsync("cli");
		var studioState = await server.Repo.ResolveWorkspaceAsync(studio.Id);
		Assert.Equal((await server.Repo.Objects.ReadCommitAsync(cliState.Record.Working)).Tree, (await server.Repo.Objects.ReadCommitAsync(studioState.Record.Working)).Tree);

		// Both see the same outcome when a third change rewrites a path they touched.
		var xena = As("xena");
		var third = await xena.CreateAsync("third");
		await xena.SaveTextAsync(third.Id, "src/hello.txt", "hello\ncomet\n", (await LeafAsync(server.Repo, third, "src/hello.txt"))!.Id);
		Assert.Equal(LandOutcome.Landed, (await xena.LandAsync(third.Id)).Outcome);
		var cliCheck = await OkAsync(clone, "workspace", "check", "cli");
		var studioCheck = await wes.CheckAsync(studio.Id);
		Assert.Equal(LandOutcome.NeedsReplay, studioCheck.Outcome);
		Assert.Equal(studioCheck.Outcome, cliCheck["outcome"]);
	}

	[PostgresFact]
	public async Task Pages_ForWorkspaceManagementRender()
	{
		var newPage = await PageAsync("/workspaces/new?on=open-work");
		Assert.Contains("stacked on a workspace", newPage);
		Assert.Contains("open-work", newPage);

		var files = await PageAsync($"/workspaces/{workspaceId}/files?path=docs/greeter.md");
		Assert.Contains("Says hello.", files);
		Assert.Contains("Edit", files);

		var detail = await PageAsync($"/workspaces/{workspaceId}");
		Assert.Contains("Land", detail);
		Assert.Contains("Sync", detail);
		Assert.Contains("Restore", detail);

		var fred = As("fred");
		var mine = await fred.CreateAsync("blocked");
		await fred.SaveTextAsync(mine.Id, "src/hello.txt", "x\n", (await LeafAsync(server.Repo, mine, "src/hello.txt"))!.Id);
		var gina = As("gina");
		var theirs = await gina.CreateAsync("first-in");
		await gina.SaveTextAsync(theirs.Id, "src/hello.txt", "y\n", (await LeafAsync(server.Repo, theirs, "src/hello.txt"))!.Id);
		await gina.LandAsync(theirs.Id);

		// The conflicts page reads the server's live overview, which the server reconciles in the background after the landing.
		var clock = System.Diagnostics.Stopwatch.StartNew();
		while (!server.Reconciling.Idle)
		{
			Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "the server's reconciliation did not settle");
			await Task.Delay(20);
		}

		var conflicts = await PageAsync("/conflicts");
		Assert.Contains("blocked", conflicts);
		Assert.Contains("needs-replay", conflicts);
		Assert.Contains("src/hello.txt", conflicts);
	}

	[PostgresFact]
	public async Task Watch_TellsStudioAboutLandingsAsTheyHappen()
	{
		var seen = new TaskCompletionSource<Bassia.Stores.Api.WatchEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		studio.Watch.Changed += change =>
		{
			if (change.Kind == "landed")
			{
				seen.TrySetResult(change);
			}
		};
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (!studio.Watch.Connected && DateTime.UtcNow < deadline)
		{
			await Task.Delay(50);
		}

		Assert.True(studio.Watch.Connected, "Studio follows the server's change stream");
		var zoe = As("zoe");
		var ws = await zoe.CreateAsync("watched");
		await zoe.SaveTextAsync(ws.Id, "w.txt", "w\n", null);
		var landed = await zoe.LandAsync(ws.Id);
		var change = await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.Equal(landed.Seq, change.Seq);
	}
}
