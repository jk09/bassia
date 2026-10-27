namespace Bassia.Tests;

using System.Net;
using System.Net.Sockets;
using Bassia.Git;
using Bassia.Storage;
using Bassia.Storage.Cloud;
using Microsoft.AspNetCore.Builder;

/// <summary>
/// The git smart-HTTP gateway (<see cref="GitGateway"/>) on a local port, over a cloud storage on the in-memory object
/// store and record table: real git clones, fetches and pushes through it, and <c>bassia clone</c> takes it as a depot.
/// </summary>
public sealed class GitGatewayTests : IAsyncLifetime
{
	private readonly TempDirectory temp = new();
	private readonly RacingTable table = new(new MemoryRecordTable());
	private readonly MemoryObjectStore objects = new();
	private MonorepoFixture depot = null!;
	private CloudComponentStorage storage = null!;
	private WebApplication gateway = null!;
	private string url = "";

	public async Task InitializeAsync()
	{
		depot = await MonorepoFixture.CreateAsync();
		await depot.AddComponentAsync("lib", files: new Dictionary<string, string> { ["include/lib.h"] = "int f();\n", ["src/lib.c"] = "int f() { return 1; }\n" });
		await depot.AddComponentAsync("app", files: new Dictionary<string, string> { ["src/main.c"] = "int main;\n", ["docs/guide.md"] = "guide\n" });
		Assert.Equal(0, (await depot.BassiaAsync("component", "set", "app", "-references", "lib")).ExitCode);

		// The cloud holds the depot's components; the gateway serves them, and the depot's meta-repo beside them.
		storage = new CloudComponentStorage(objects, table, Path.Combine(temp.Path, "node-1"));
		await storage.ImportAsync("lib", depot.SourceRepo("lib"));
		await storage.ImportAsync("app", depot.SourceRepo("app"));
		var port = FreePort();
		url = $"http://127.0.0.1:{port}/git";
		gateway = new GitGateway(storage, Path.Combine(depot.Root, ".bassia")).Build($"http://127.0.0.1:{port}");
		await gateway.StartAsync();
	}

	public async Task DisposeAsync()
	{
		await gateway.StopAsync();
		await gateway.DisposeAsync();
		await depot.DisposeAsync();
		temp.Dispose();
	}

	private static int FreePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}

	private static Task<GitResult> GitAsync(string directory, params string[] arguments) => GitClient.In(directory).RunAsync(arguments);

	private async Task<string> CloneAsync(string component, params string[] options)
	{
		var folder = Path.Combine(temp.Path, $"clone-{Guid.NewGuid():N}");
		var clone = await GitAsync(temp.Path, ["clone", "--quiet", .. options, $"{url}/{component}", folder]);
		Assert.True(clone.ExitCode == 0, clone.Error);
		return folder;
	}

	private static async Task<string> CommitAsync(string clone, string file, string content)
	{
		await File.WriteAllTextAsync(Path.Combine(clone, file), content);
		await TestEnvironment.GitAsync(clone, "add", "--all");
		await TestEnvironment.GitAsync(clone, "commit", "--quiet", "-m", $"change {file}");
		return await TestEnvironment.GitAsync(clone, "rev-parse", "HEAD");
	}

	[Theory]
	[InlineData("2")]
	[InlineData("0")]
	public async Task Clone_InEitherProtocol_HasTheStoredHistory(string version)
	{
		var clone = await CloneAsync("lib", "-c", $"protocol.version={version}");

		var main = (await storage.Open("lib").BranchTipAsync("main"))!;
		Assert.Equal(main, await TestEnvironment.GitAsync(clone, "rev-parse", "HEAD"));
		Assert.Equal(await TestEnvironment.GitAsync(depot.SourceRepo("lib"), "rev-parse", "v0"), await TestEnvironment.GitAsync(clone, "rev-parse", "v0"));
		Assert.Equal("int f();", (await File.ReadAllTextAsync(Path.Combine(clone, "include", "lib.h"))).Trim());
	}

	[Fact]
	public async Task PartialClone_FetchesContentByIdOnDemand()
	{
		var clone = await CloneAsync("app", "--filter=blob:none", "--no-checkout");
		var missing = await TestEnvironment.GitAsync(clone, "rev-list", "--objects", "--missing=print", "HEAD");
		Assert.Contains('?', missing);

		Assert.Equal("guide", await TestEnvironment.GitAsync(clone, "show", "HEAD:docs/guide.md"));
	}

	[Fact]
	public async Task Push_PublishesTheBranchAndTag_AndFetch_BringsAnotherNodesChanges()
	{
		var clone = await CloneAsync("lib");
		var commit = await CommitAsync(clone, "new.txt", "new");
		await TestEnvironment.GitAsync(clone, "tag", "-a", "v1", "-m", "v1");

		var push = await GitAsync(clone, "push", "--quiet", "origin", "main", "v1");

		Assert.True(push.ExitCode == 0, push.Error);
		var second = new CloudComponentStorage(objects, table, Path.Combine(temp.Path, "node-2"));
		Assert.Equal(commit, await second.Open("lib").BranchTipAsync("main"));
		Assert.Equal(commit, (await second.Open("lib").GetTagAsync("v1"))!.Commit);

		// Another node moves the branch; a fetch through this one sees it.
		var other = await CloneAsync("lib");
		var later = await CommitAsync(other, "later.txt", "later");
		await TestEnvironment.GitAsync(other, "push", "--quiet", "origin", "main");
		Assert.Equal(later, await second.Open("lib").BranchTipAsync("main"));

		Assert.Equal(0, (await GitAsync(clone, "pull", "--quiet", "--ff-only")).ExitCode);
		Assert.Equal(later, await TestEnvironment.GitAsync(clone, "rev-parse", "HEAD"));
	}

	[Fact]
	public async Task Push_ThatLosesARace_IsRefusedWhole()
	{
		var clone = await CloneAsync("lib");
		var root = await TestEnvironment.GitAsync(clone, "rev-parse", "HEAD");
		await CommitAsync(clone, "mine.txt", "mine");
		await TestEnvironment.GitAsync(clone, "branch", "topic");

		// Between receive-pack and the publish, another node writes the branch.
		var second = new CloudComponentStorage(objects, table, Path.Combine(temp.Path, "node-2"));
		table.BeforeNextWrite = () => second.Open("lib").SetBranchAsync("main", root);

		var push = await GitAsync(clone, "push", "origin", "main", "topic");

		Assert.NotEqual(0, push.ExitCode);
		Assert.Contains("moved meanwhile", push.Error);
		Assert.Equal(root, await second.Open("lib").BranchTipAsync("main"));
		Assert.Null(await second.Open("lib").BranchTipAsync("topic"));
		Assert.Null(await storage.Open("lib").BranchTipAsync("topic"));
	}

	[Fact]
	public async Task Push_ToTheMetaRepo_OrAnUnknownComponent_IsRefused()
	{
		var meta = await CloneAsync(".bassia");
		await CommitAsync(meta, "x.txt", "x");

		Assert.NotEqual(0, (await GitAsync(meta, "push", "--quiet", "origin", "HEAD")).ExitCode);
		Assert.NotEqual(0, (await GitAsync(temp.Path, "clone", "--quiet", $"{url}/nothing", Path.Combine(temp.Path, "nothing"))).ExitCode);
	}

	[Fact]
	public async Task BassiaClone_OfTheGateway_RunsSparse_AndWritesItsResultsThrough()
	{
		var clone = Path.Combine(temp.Path, "clone");
		var (exit, _, error) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "clone", "-from", url, "-path", clone);
		Assert.True(exit == 0, error);
		var specFile = Path.Combine(temp.Path, "parser.spec");
		await File.WriteAllTextAsync(specFile, "map app\nselect lib v0\nload app/src\nload lib/include\n");

		var (runExit, output, runError) = await TestEnvironment.RunInDirectoryAsync(clone, "agent", "start", "-spec", specFile, "-run",
			TestEnvironment.WriteFileCommand("app/src/new.c", "int added;"));

		Assert.True(runExit == 0, runError);
		var runId = TestEnvironment.RunIdOf(output);
		var tag = $"agent-run/{runId["agent-run-".Length..]}/0";
		var app = new CloudComponentStorage(objects, table, Path.Combine(temp.Path, "node-2")).Open("app");
		var result = (await app.GetTagAsync(tag))!.Commit;
		Assert.Equal(["README.md", "docs/guide.md", "src/main.c", "src/new.c"], (await app.ListFilesAsync(result)).Select(file => file.Path));
		Assert.Equal("guide\n"u8.ToArray(), await app.ReadFileAsync(result, "docs/guide.md"));
	}

	/// <summary>A record table that runs <see cref="BeforeNextWrite"/> once, just before the next batch is written: a race on demand.</summary>
	private sealed class RacingTable(IRecordTable inner) : IRecordTable
	{
		public Func<Task>? BeforeNextWrite { get; set; }

		public string Location => inner.Location;

		public Task<CloudRecord?> GetAsync(string partition, string sort) => inner.GetAsync(partition, sort);

		public Task<IReadOnlyList<CloudRecord>> QueryAsync(string partition, string sortPrefix = "") => inner.QueryAsync(partition, sortPrefix);

		public async Task WriteAsync(IReadOnlyList<CloudWrite> writes)
		{
			if (BeforeNextWrite is { } race)
			{
				BeforeNextWrite = null;
				await race();
			}

			await inner.WriteAsync(writes);
		}
	}
}
