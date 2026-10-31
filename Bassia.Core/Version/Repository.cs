namespace Bassia.Core.Version;

using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>Mainline as of one operation: its head operation, seq, view, commit and the commit's tree.</summary>
public sealed record MainState(ObjectId Head, Operation Operation, ObjectId CommitId, Commit Commit)
{
	public long Seq => Operation.Seq;

	public ObjectId Tree => Commit.Tree;
}

/// <summary>A workspace as of its head operation.</summary>
public sealed record WorkspaceState(ObjectId Head, Operation Operation, WorkspaceRecord Record)
{
	public string Id => Record.Id;

	public string Name => Record.Name;

	public bool IsOpen => WorkspaceStatus.IsOpen(Record.Status);
}

/// <summary>An operation with its id, as an operation log lists it.</summary>
public sealed record OperationEntry(ObjectId Id, Operation Operation);

/// <summary>Raised when a head moved underneath an operation; the caller re-reads and retries.</summary>
public sealed class ConcurrentUpdateException(HeadName head) : VersionException($"'{head}' moved during the operation; try again.")
{
	public HeadName Head { get; } = head;
}

/// <summary>
/// The version core bound to its four stores. Every operation reads objects, writes new objects and finishes by
/// advancing one head with compare-and-swap; nothing here knows whether it runs in a client or on the server.
/// </summary>
public sealed class Repository
{
	public Repository(IObjectStore objects, IViewStore views, IPathIndex paths, ILeaseStore leases, ChunkParameters? chunks = null)
	{
		Store = objects;
		Views = views;
		Paths = paths;
		Leases = leases;
		Objects = new ObjectAccess(objects, chunks);
		Trees = new Trees(Objects);
		Diffs = new Diffs(Trees);
	}

	public IObjectStore Store { get; }

	public IViewStore Views { get; }

	public IPathIndex Paths { get; }

	public ILeaseStore Leases { get; }

	public ObjectAccess Objects { get; }

	public Trees Trees { get; }

	public Diffs Diffs { get; }

	public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

	/// <summary>Creates mainline with an empty root commit at seq 0, unless it exists.</summary>
	public async Task<MainState> InitializeAsync(string agent, CancellationToken ct = default)
	{
		if (await Views.GetHeadAsync(HeadName.Main, ct) is not null)
		{
			return await GetMainAsync(ct);
		}

		var tree = await Trees.EmptyTreeAsync(ct);
		var commit = new Commit(tree, [], ChangeId.New(), new Author(agent), "initialize the monorepo", Clock());
		var commitId = await Objects.WriteAsync(commit, ct);
		var view = await Objects.WriteAsync(new View(new Dictionary<string, ObjectId> { ["main"] = commitId }), ct);
		var op = await Objects.WriteAsync(new Operation([], view, "initialize", null, agent, null, Clock(), 0), ct);
		await Views.AdvanceHeadAsync(HeadName.Main, null, op, ct);
		return await GetMainAsync(ct);
	}

	public async Task<MainState> GetMainAsync(CancellationToken ct = default)
	{
		var head = await Views.GetHeadAsync(HeadName.Main, ct) ?? throw new VersionException("The monorepo has no mainline yet; it was not initialized.");
		return await MainAtAsync(head, ct);
	}

	public async Task<MainState> MainAtAsync(ObjectId head, CancellationToken ct = default)
	{
		var op = await Objects.ReadOperationAsync(head, ct);
		var view = await Objects.ReadViewAsync(op.View, ct);
		var commitId = view.Refs.TryGetValue("main", out var id) ? id : throw new VersionException($"Operation {head.Short} has no mainline commit.");
		return new MainState(head, op, commitId, await Objects.ReadCommitAsync(commitId, ct));
	}

	/// <summary>Mainline as of <paramref name="seq"/>, walking back from the head.</summary>
	public async Task<MainState> MainAtSeqAsync(long seq, CancellationToken ct = default)
	{
		var main = await GetMainAsync(ct);
		if (seq > main.Seq || seq < 0)
		{
			throw new VersionException($"Mainline has no seq {seq}; its head is at seq {main.Seq}.");
		}

		var head = main.Head;
		var op = main.Operation;
		while (op.Seq > seq)
		{
			head = op.Parents[0];
			op = await Objects.ReadOperationAsync(head, ct);
		}

		return await MainAtAsync(head, ct);
	}

	/// <summary>The operations of a head, newest first.</summary>
	public async IAsyncEnumerable<OperationEntry> LogAsync(HeadName name, int limit = int.MaxValue, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
	{
		var head = await Views.GetHeadAsync(name, ct);
		for (var count = 0; head is { } id && count < limit; count++)
		{
			var op = await Objects.ReadOperationAsync(id, ct);
			yield return new OperationEntry(id, op);
			head = op.Parents.Count > 0 ? op.Parents[0] : null;
		}
	}

	// ----- workspaces -----

	public async Task<WorkspaceState?> FindWorkspaceAsync(string id, CancellationToken ct = default)
	{
		var head = await Views.GetHeadAsync(HeadName.Workspace(id), ct);
		return head is { } value ? await WorkspaceAtAsync(value, ct) : null;
	}

	public async Task<WorkspaceState> WorkspaceAtAsync(ObjectId head, CancellationToken ct = default)
	{
		var op = await Objects.ReadOperationAsync(head, ct);
		var view = await Objects.ReadViewAsync(op.View, ct);
		return new WorkspaceState(head, op, view.Workspace ?? throw new VersionException($"Operation {head.Short} has no workspace."));
	}

	public async Task<IReadOnlyList<WorkspaceState>> ListWorkspacesAsync(bool includeClosed = false, CancellationToken ct = default)
	{
		var result = new List<WorkspaceState>();
		await foreach (var (_, head) in Views.ListHeadsAsync(HeadName.WorkspacePrefix, ct))
		{
			var state = await WorkspaceAtAsync(head, ct);
			if (includeClosed || state.IsOpen)
			{
				result.Add(state);
			}
		}

		return result.OrderBy(state => state.Record.Created).ToList();
	}

	/// <summary>
	/// A workspace by id, name, or a unique prefix of either (at least 4 characters), open ones first; closed ones are
	/// found by exact id or name.
	/// </summary>
	public async Task<WorkspaceState> ResolveWorkspaceAsync(string reference, CancellationToken ct = default)
	{
		if (await FindWorkspaceAsync(reference, ct) is { } exact)
		{
			return exact;
		}

		var all = await ListWorkspacesAsync(true, ct);
		var named = all.Where(state => state.Name == reference).ToList();
		var open = named.Where(state => state.IsOpen).ToList();
		if (open.Count == 1 || (open.Count == 0 && named.Count >= 1))
		{
			return open.Count == 1 ? open[0] : named[^1];
		}

		if (open.Count > 1)
		{
			throw new VersionException($"Several open workspaces are named '{reference}'; use the id ({string.Join(", ", open.Select(state => state.Id))}).");
		}

		var prefixed = reference.Length >= 4
			? all.Where(state => state.IsOpen && (state.Id.StartsWith(reference, StringComparison.Ordinal) || state.Name.StartsWith(reference, StringComparison.Ordinal))).ToList()
			: [];
		return prefixed.Count switch
		{
			1 => prefixed[0],
			0 => throw new VersionException($"No workspace '{reference}'. Run 'bassia workspace list'."),
			_ => throw new VersionException($"'{reference}' matches several workspaces: {string.Join(", ", prefixed.Select(state => state.Name))}.")
		};
	}

	/// <summary>Writes a workspace's new record as an operation on its head; throws <see cref="ConcurrentUpdateException"/> if the head moved.</summary>
	public async Task<WorkspaceState> CommitWorkspaceAsync(WorkspaceState? current, WorkspaceRecord record, string description, string? agent,
		string? run, CancellationToken ct = default)
	{
		// The view and the operation pointing at it go up together, in one round trip for a remote store.
		var viewObject = new View(new Dictionary<string, ObjectId> { ["base"] = record.Base, ["working"] = record.Working }, record);
		var view = ObjectId.Of(ObjectCodec.Encode(viewObject));
		var op = new Operation(current is null ? [] : [current.Head], view, description, record.Id, agent, run, Clock(), (current?.Operation.Seq ?? -1) + 1);
		var opId = (await Objects.WriteManyAsync([viewObject, op], ct))[1];
		// Inside a write batch, everything written so far goes up now: the head may only point at stored objects.
		await Objects.FlushAsync(ct);
		var name = HeadName.Workspace(record.Id);
		var update = await Views.AdvanceHeadAsync(name, current?.Head, opId, ct);
		if (!update.Success)
		{
			throw new ConcurrentUpdateException(name);
		}

		return new WorkspaceState(opId, op, record);
	}

	/// <summary>Re-reads a workspace and applies <paramref name="change"/> until its compare-and-swap succeeds.</summary>
	public async Task<WorkspaceState> UpdateWorkspaceAsync(string id, Func<WorkspaceState, Task<(WorkspaceRecord Record, string Description)?>> change,
		string? agent, string? run, CancellationToken ct = default)
	{
		for (var attempt = 0; ; attempt++)
		{
			var current = await FindWorkspaceAsync(id, ct) ?? throw new VersionException($"No workspace '{id}'.");
			var next = await change(current);
			if (next is not { } value)
			{
				return current;
			}

			try
			{
				return await CommitWorkspaceAsync(current, value.Record, value.Description, agent, run, ct);
			}
			catch (ConcurrentUpdateException) when (attempt < 20)
			{
				await Task.Delay(10 * (attempt + 1), ct);
			}
		}
	}

	/// <summary>A new commit for a workspace's working copy: same change, parents and intent, the given tree.</summary>
	public async Task<ObjectId> AmendAsync(Commit working, ObjectId tree, CancellationToken ct = default) =>
		await Objects.WriteAsync(working with { Tree = tree, Time = Clock() }, ct);

	// ----- path index -----

	/// <summary>Brings the path index up to mainline's head from the operation log, when it is behind.</summary>
	public async Task EnsureIndexedAsync(MainState main, CancellationToken ct = default)
	{
		var indexed = await Paths.IndexedSeqAsync(ct);
		if (indexed >= main.Seq)
		{
			return;
		}

		// Seq 0 is the empty root commit, so an index at 0 holds nothing yet and needs nothing from it.
		var pending = new Stack<MainState>();
		var state = main;
		while (state.Seq > indexed)
		{
			pending.Push(state);
			state = await MainAtAsync(state.Operation.Parents[0], ct);
		}

		ObjectId? previous = state.Tree;
		while (pending.TryPop(out var next))
		{
			var changes = await Trees.DiffAsync(previous, next.Tree, "", ct);
			await Paths.ApplyAsync(next.Seq, changes.Select(change => new PathChange(change.Path, change.New)).ToList(), ct);
			previous = next.Tree;
		}
	}
}
