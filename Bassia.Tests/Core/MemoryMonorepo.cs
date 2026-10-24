namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Storage;
using Bassia.Storage.Memory;

/// <summary>
/// A monorepo whose meta-repo, component stores and run records all live in memory: the component graph under test with
/// no git involved. Working copies of agentic runs go to a temporary folder that is removed on dispose.
/// </summary>
internal sealed class MemoryMonorepo : IDisposable
{
	public MemoryMonorepo()
	{
		Graph = new MonorepoGraph(MetaRepo.Open(MetaStore, ComponentRegistry.Empty), Storage);
		WorkspaceDir = Path.Combine(Path.GetTempPath(), $"bassia-core-{Guid.NewGuid():N}");
	}

	public MemoryUpstreams Upstreams => Storage.Upstreams;

	public MemoryComponentStorage Storage { get; } = new();

	public MemoryMetaStore MetaStore { get; } = new();

	public MemoryRunRecordStore Records { get; } = new();

	public MonorepoGraph Graph { get; }

	public string WorkspaceDir { get; }

	/// <summary>An upstream repository at <c>https://example.com/&lt;name&gt;.git</c> with one commit holding <paramref name="files"/>.</summary>
	public string Upstream(string name, params (string Path, string Content)[] files)
	{
		var url = $"https://example.com/{name}.git";
		Upstreams.At(url).Commit("main", $"Initial commit of {name}", files.ToDictionary(file => file.Path, file => (string?)file.Content));
		return url;
	}

	/// <summary>Imports an upstream made by <see cref="Upstream"/> as a registered component, tagged <c>v1</c> at its tip.</summary>
	public async Task<MemoryComponentStore> AddAsync(string name, IReadOnlyList<ComponentReference>? references = null, params (string Path, string Content)[] files)
	{
		var url = Upstream(name, files.Length == 0 ? [("README.md", $"# {name}\n")] : files);
		await Graph.ImportAsync(name, url, references ?? []);
		await Graph.TagAsync(name, "v1");
		return Storage[name];
	}

	public static IReadOnlyList<ComponentReference> Refs(params string[] names) =>
		names.Select(name => name.Split(':') is [var component, var path] ? new ComponentReference(component, path) : new ComponentReference(name, name)).ToList();

	/// <summary>The paths of the files of <paramref name="commit"/> in a store.</summary>
	public static async Task<string[]> FilesAsync(IComponentStore store, string commit) =>
		(await store.ListFilesAsync(commit)).Select(file => file.Path).ToArray();

	/// <summary>The run engine with <paramref name="agent"/> as the agent: it gets the run's folder and returns an exit code (null: cancelled).</summary>
	public AgentRuns Runs(Func<RunMetadata, int?> agent, Func<RunMetadata, Action<string>, IAgentRunLocks>? locks = null, Action<RunMetadata>? prepared = null) =>
		Graph.AgentRuns(Records, new AgentRunOptions
		{
			WorkspaceDir = WorkspaceDir,
			RunAgent = (metadata, _) => Task.FromResult(agent(metadata)),
			StartLocks = locks ?? ((_, _) => new RecordingLocks()),
			Prepared = prepared
		});

	/// <summary>A context that collects the progress steps instead of writing them to stderr.</summary>
	public static AgentRunContext Quiet(List<AgentRunStep>? steps = null) => new() { OnStep = step => steps?.Add(step) };

	public void Dispose()
	{
		if (!Directory.Exists(WorkspaceDir))
		{
			return;
		}

		// Links first, so the delete never follows one into another working copy.
		foreach (var link in Directory.EnumerateFileSystemEntries(WorkspaceDir, "*", SearchOption.AllDirectories).Where(DirectoryLinks.IsLink).ToList())
		{
			DirectoryLinks.Delete(link);
		}

		Directory.Delete(WorkspaceDir, recursive: true);
	}
}

/// <summary>An <see cref="IAgentRunLocks"/> that records the calls the engine makes.</summary>
internal sealed class RecordingLocks : IAgentRunLocks
{
	public List<string> Calls { get; } = [];

	public Task StopAsync()
	{
		Calls.Add("stop");
		return Task.CompletedTask;
	}

	public Task CheckStaleAsync()
	{
		Calls.Add("check-stale");
		return Task.CompletedTask;
	}

	public Task FinishAsync()
	{
		Calls.Add("finish");
		return Task.CompletedTask;
	}

	public ValueTask DisposeAsync()
	{
		Calls.Add("dispose");
		return ValueTask.CompletedTask;
	}
}
