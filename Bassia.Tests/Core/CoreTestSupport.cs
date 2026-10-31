namespace Bassia.Tests.Core;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Core.Workspaces;

/// <summary>A repository over the in-memory stores, with small chunks so multi-chunk files stay small in tests.</summary>
internal sealed class TestRepo
{
	public static readonly ChunkParameters SmallChunks = new(64, 256, 1024);

	public TestRepo(IObjectStore? objects = null)
	{
		Objects = objects ?? new MemoryObjectStore();
		Repo = new Repository(Objects, Views, Paths, Leases, SmallChunks);
		Landing = new LandingService(Repo);
		Workspaces = new WorkspaceService(Repo);
	}

	public IObjectStore Objects { get; }

	public MemoryViewStore Views { get; } = new();

	public MemoryPathIndex Paths { get; } = new();

	public MemoryLeaseStore Leases { get; } = new();

	public Repository Repo { get; }

	public LandingService Landing { get; }

	public WorkspaceService Workspaces { get; }

	public static async Task<TestRepo> CreateAsync()
	{
		var repo = new TestRepo();
		await repo.Repo.InitializeAsync("test");
		return repo;
	}

	public async Task<Leaf> FileAsync(string text) => await Repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes(text));

	/// <summary>A tree holding the given files.</summary>
	public async Task<ObjectId> TreeAsync(IReadOnlyDictionary<string, string> files, ObjectId? onto = null)
	{
		var changes = new List<PathChange>();
		foreach (var (path, text) in files)
		{
			changes.Add(new PathChange(path, text is null ? null : await FileAsync(text)));
		}

		return await Repo.Trees.EditAsync(onto ?? await Repo.Trees.EmptyTreeAsync(), changes);
	}

	public async Task<Dictionary<string, string>> FilesAsync(ObjectId tree)
	{
		var files = new Dictionary<string, string>(StringComparer.Ordinal);
		await foreach (var (path, leaf) in Repo.Trees.WalkAsync(tree))
		{
			files[path] = leaf.Kind == EntryKind.Conflict ? "<conflict>" : Encoding.UTF8.GetString(await Repo.Objects.ReadContentAsync(leaf));
		}

		return files;
	}

	public async Task<Dictionary<string, string>> MainFilesAsync() => await FilesAsync((await Repo.GetMainAsync()).Tree);

	/// <summary>Creates a workspace and sets its working tree by editing the base with <paramref name="edits"/> (null deletes).</summary>
	public async Task<WorkspaceState> WorkspaceAsync(string name, IReadOnlyDictionary<string, string?> edits, string? on = null, string intent = "change")
	{
		var state = await Workspaces.CreateAsync(name, "agent-" + name, on, intent);
		return await EditAsync(state, edits);
	}

	public async Task<WorkspaceState> EditAsync(WorkspaceState state, IReadOnlyDictionary<string, string?> edits)
	{
		var working = await Repo.Objects.ReadCommitAsync(state.Record.Working);
		var changes = new List<PathChange>();
		foreach (var (path, text) in edits)
		{
			changes.Add(new PathChange(path, text is null ? null : await FileAsync(text)));
		}

		var tree = await Repo.Trees.EditAsync(working.Tree, changes);
		return await Workspaces.SetTreeAsync(state.Id, tree, "edit", null, null);
	}

	public Task<LandResult> LandAsync(WorkspaceState state, bool dryRun = false) => Landing.LandAsync(new LandRequest(state.Id, DryRun: dryRun));

	/// <summary>Lands a set of files directly, through a throwaway workspace.</summary>
	public async Task<LandResult> CommitAsync(IReadOnlyDictionary<string, string?> edits, string name = "setup")
	{
		var result = await LandAsync(await WorkspaceAsync(name, edits));
		Assert.Equal(LandOutcome.Landed, result.Outcome);
		return result;
	}
}

/// <summary>A temporary directory, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
	public TempDir()
	{
		Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bassia-test-" + Guid.NewGuid().ToString("N")[..10]);
		Directory.CreateDirectory(Path);
	}

	public string Path { get; }

	public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

	public void Dispose()
	{
		try
		{
			Directory.Delete(Path, true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
