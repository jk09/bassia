namespace Bassia.Studio;

using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Stores;

/// <summary>One landing on mainline, as the log lists it.</summary>
public sealed record LogEntry(long Seq, ObjectId CommitId, Commit Commit, Operation Operation);

/// <summary>One version of a path in mainline's history: the landing that set it, and its content (null: deleted).</summary>
public sealed record PathHistoryEntry(long Seq, Leaf? Leaf, LogEntry Landing);

/// <summary>A diff to show: the records between two trees, with the trees' labels.</summary>
public sealed record DiffSet(ObjectId? OldTree, ObjectId NewTree, IReadOnlyList<DiffRecord> Records);

/// <summary>
/// Studio's view of the monorepo: the version core over the server's remote stores (as a clone has it), with a local
/// object cache. Opened on first use, so Studio starts before its server is reachable.
/// </summary>
public sealed class MonorepoClient(StudioOptions options) : IDisposable
{
	private readonly SemaphoreSlim gate = new(1, 1);
	private ServerConnection? connection;
	private Repository? repo;
	private ILanding? landing;
	private IConflictOverviewSource? overview;

	public string Server => options.Server;

	public string Label => options.Label ?? options.Server;

	public Repository Repo => repo ?? throw new InvalidOperationException("Open the monorepo first.");

	public ILanding Landing => landing ?? throw new InvalidOperationException("Open the monorepo first.");

	/// <summary>The server's live conflict overview (bassia-f162a7fd).</summary>
	public IConflictOverviewSource Overview => overview ?? throw new InvalidOperationException("Open the monorepo first.");

	/// <summary>Connects to the server (once): its chunk parameters, the remote stores and the landing service.</summary>
	public async Task<Repository> OpenAsync(CancellationToken ct = default)
	{
		if (repo is not null)
		{
			return repo;
		}

		await gate.WaitAsync(ct);
		try
		{
			if (repo is null)
			{
				var server = new ServerConnection(options.Server, options.Token);
				try
				{
					var info = await server.GetAsync<Api.Info>("info", ct);
					var chunks = new ChunkParameters(info.ChunkMin, info.ChunkAverage, info.ChunkMax);
					Directory.CreateDirectory(options.Cache);
					var objects = new CachingObjectStore(new DiskObjectStore(options.Cache), new RemoteObjectStore(server));
					landing = new RemoteLanding(server);
					overview = new RemoteConflictOverview(server);
					connection = server;
					repo = new Repository(objects, new RemoteViewStore(server), new RemotePathIndex(server), new RemoteLeaseStore(server), chunks);
				}
				catch
				{
					server.Dispose();
					throw;
				}
			}

			return repo;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>The tree of a workspace's working commit, of mainline at a seq, or of mainline now.</summary>
	public async Task<ObjectId> TreeAsync(long? seq, string? workspace, CancellationToken ct = default)
	{
		var repository = await OpenAsync(ct);
		if (!string.IsNullOrEmpty(workspace))
		{
			var state = await repository.ResolveWorkspaceAsync(workspace, ct);
			return (await repository.Objects.ReadCommitAsync(state.Record.Working, ct)).Tree;
		}

		return (seq is { } at ? await repository.MainAtSeqAsync(at, ct) : await repository.GetMainAsync(ct)).Tree;
	}

	/// <summary>Mainline's landings, newest first, from a seq (default: now).</summary>
	public async Task<IReadOnlyList<LogEntry>> LogAsync(long? from, int limit, CancellationToken ct = default)
	{
		var repository = await OpenAsync(ct);
		var state = from is { } seq ? await repository.MainAtSeqAsync(seq, ct) : await repository.GetMainAsync(ct);
		var entries = new List<LogEntry>();
		while (entries.Count < limit)
		{
			entries.Add(Entry(state));
			if (state.Operation.Parents.Count == 0)
			{
				break;
			}

			state = await repository.MainAtAsync(state.Operation.Parents[0], ct);
		}

		return entries;
	}

	/// <summary>One landing, and what it changed against the landing before it.</summary>
	public async Task<(LogEntry Entry, DiffSet Diff)> LandingAsync(long seq, CancellationToken ct = default)
	{
		var repository = await OpenAsync(ct);
		var state = await repository.MainAtSeqAsync(seq, ct);
		var parent = state.Operation.Parents.Count > 0 ? await repository.MainAtAsync(state.Operation.Parents[0], ct) : null;
		return (Entry(state), new DiffSet(parent?.Tree, state.Tree, await repository.Diffs.DiffAsync(parent?.Tree, state.Tree, ct: ct)));
	}

	/// <summary>The landings that changed a file, newest first, as of a seq (from mainline's path index).</summary>
	public async Task<IReadOnlyList<PathHistoryEntry>> PathHistoryAsync(string path, long? seq, int limit, CancellationToken ct = default)
	{
		var repository = await OpenAsync(ct);
		var at = seq ?? (await repository.GetMainAsync(ct)).Seq;
		var history = new List<PathHistoryEntry>();
		while (history.Count < limit && at >= 0 && await repository.Paths.GetAsync(path, at, ct) is { } version)
		{
			history.Add(new PathHistoryEntry(version.Seq, version.Leaf, Entry(await repository.MainAtSeqAsync(version.Seq, ct))));
			at = version.Seq - 1;
		}

		return history;
	}

	/// <summary>A workspace's changes: its working tree against its base on mainline.</summary>
	public async Task<(WorkspaceState State, DiffSet Diff)> WorkspaceAsync(string reference, CancellationToken ct = default)
	{
		var repository = await OpenAsync(ct);
		var state = await repository.ResolveWorkspaceAsync(reference, ct);
		var working = (await repository.Objects.ReadCommitAsync(state.Record.Working, ct)).Tree;
		var baseTree = (await repository.Objects.ReadCommitAsync(state.Record.Base, ct)).Tree;
		return (state, new DiffSet(baseTree, working, await repository.Diffs.DiffAsync(baseTree, working, ct: ct)));
	}

	/// <summary>The text of a leaf (null for binary or larger than <paramref name="max"/> bytes).</summary>
	public async Task<string?> TextAsync(Leaf? leaf, long max = 2 * 1024 * 1024, CancellationToken ct = default)
	{
		if (leaf is null || !leaf.IsFile && leaf.Kind != EntryKind.Symlink || leaf.Size > max)
		{
			return leaf is null ? "" : null;
		}

		var bytes = await Repo.Objects.ReadContentAsync(leaf, ct);
		return Core.Text.LineDiff.IsText(bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
	}

	private static LogEntry Entry(MainState state) => new(state.Seq, state.CommitId, state.Commit, state.Operation);

	public void Dispose()
	{
		connection?.Dispose();
		gate.Dispose();
	}
}
