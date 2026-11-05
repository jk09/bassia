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

	public async Task InitializeAsync()
	{
		var chunks = new ChunkParameters(1024, 4096, 16384);
		server = new ServerApp(new ServerOptions { Data = temp.Combine("server"), Url = "http://127.0.0.1:0", Token = Token, Chunks = chunks });
		await server.StartAsync();
		await SeedAsync(chunks);
		studio = new StudioApp(new StudioOptions { Server = server.Address!, Token = Token, Url = "http://127.0.0.1:0", Cache = temp.Combine("cache"), Label = "test" });
		await studio.StartAsync();
		http = new HttpClient { BaseAddress = new Uri(studio.Address!) };
	}

	public async Task DisposeAsync()
	{
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

	[Fact]
	public async Task Overview_ShowsMainlineAndRecentLandings()
	{
		var page = await PageAsync("/");
		Assert.Contains("Bassia Studio", page);
		Assert.Contains("mainline seq", page);
		Assert.Contains("Add the greeter", page);
		Assert.Contains("Greet the moon", page);
	}

	[Fact]
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

	[Fact]
	public async Task Depot_ShowsPathHistoryNewestFirst()
	{
		var page = await PageAsync("/depot?path=src/hello.txt&tab=history");
		var moon = page.IndexOf("Greet the moon", StringComparison.Ordinal);
		var greeter = page.IndexOf("Add the greeter", StringComparison.Ordinal);
		Assert.True(moon > 0 && greeter > moon, "history lists the newer landing first");
	}

	[Fact]
	public async Task Raw_DownloadsFileContent()
	{
		Assert.Equal([0, 1, 2, 0, 255], await http.GetByteArrayAsync("/raw?path=assets/logo.bin"));
		Assert.Equal("hello\nworld\n", await http.GetStringAsync("/raw?path=src/hello.txt&seq=1"));
		Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/raw?path=src")).StatusCode);
	}

	[Fact]
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

	[Fact]
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

	[Fact]
	public async Task UnreachableServer_IsAMessageNotACrash()
	{
		await using var orphan = new StudioApp(new StudioOptions { Server = "http://127.0.0.1:9", Url = "http://127.0.0.1:0", Cache = temp.Combine("cache2") });
		await orphan.StartAsync();
		using var client = new HttpClient { BaseAddress = new Uri(orphan.Address!) };
		var page = WebUtility.HtmlDecode(await client.GetStringAsync("/log"));
		Assert.Contains("unreachable", page);
	}

	[Fact]
	public void SplitDiff_PairsChangedLinesAndFoldsUnchangedRuns()
	{
		var old = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
		var updated = old.Replace("line 10\n", "line ten\n");
		var rows = SplitDiff.Build(old, updated, context: 2);
		var changed = Assert.Single(rows, row => row.Kind == SplitDiff.Replace);
		Assert.Equal(("line 10", "line ten", 10, 10), (changed.OldText, changed.NewText, changed.OldNumber, changed.NewNumber));
		Assert.Equal(2, rows.Count(row => row.Kind == SplitDiff.Fold));
	}
}
