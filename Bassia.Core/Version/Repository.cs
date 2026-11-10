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

/// <summary>Raised when a workspace is created or cloned under the name of an open workspace.</summary>
public sealed class WorkspaceNameTakenException(string name, string holder)
	: VersionException($"A workspace named '{name}' is already open ({holder}).")
{
	public string Name => name;

	public string Holder => holder;
}

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
	/// <param name="workspaces">The workspace registry; without one, workspaces are found by reading every head (<see cref="ScanningWorkspaceIndex"/>).</param>
	public Repository(IObjectStore objects, IViewStore views, IPathIndex paths, ILeaseStore leases, ChunkParameters? chunks = null, ITreeStore? trees = null,
		IWorkspaceIndex? workspaces = null)
	{
		Store = objects;
		Views = views;
		Paths = paths;
		Leases = leases;
		Objects = new ObjectAccess(objects, chunks, trees);
		Trees = new Trees(Objects);
		Diffs = new Diffs(Trees);
		Workspaces = workspaces ?? new ScanningWorkspaceIndex(views, Objects, paths);
	}

	/// <summary>The workspace registry: it moves the workspace and candidate heads and answers every question about workspaces.</summary>
	public IWorkspaceIndex Workspaces { get; }

	/// <summary>How many closed workspaces a listing that includes them returns unless it asks for more.</summary>
	public const int ClosedPage = 100;

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

	/// <summary>
	/// The open workspaces and, with <paramref name="includeClosed"/>, the newest <paramref name="closedLimit"/> closed
	/// ones (created before <paramref name="closedBefore"/>, to page further back), in the order they were created.
	/// </summary>
	public async Task<IReadOnlyList<WorkspaceState>> ListWorkspacesAsync(bool includeClosed = false, CancellationToken ct = default, int closedLimit = ClosedPage,
		DateTimeOffset? closedBefore = null)
	{
		var summaries = (await Workspaces.ListAsync(true, ct: ct)).ToList();
		if (includeClosed)
		{
			summaries.AddRange(await Workspaces.ListAsync(false, closedLimit, closedBefore, ct));
		}

		return (await StatesAsync(summaries, ct)).OrderBy(state => state.Record.Created).ToList();
	}

	/// <summary>The states behind summaries, their operations and views fetched together where the store batches reads.</summary>
	public async Task<IReadOnlyList<WorkspaceState>> StatesAsync(IReadOnlyList<WorkspaceSummary> summaries, CancellationToken ct = default)
	{
		await Objects.PrefetchAsync(summaries.Select(summary => summary.Head), ct);
		var operations = new List<(ObjectId Head, Operation Operation)>();
		foreach (var summary in summaries)
		{
			operations.Add((summary.Head, await Objects.ReadOperationAsync(summary.Head, ct)));
		}

		await Objects.PrefetchAsync(operations.Select(item => item.Operation.View), ct);
		var states = new List<WorkspaceState>();
		foreach (var (head, operation) in operations)
		{
			var view = await Objects.ReadViewAsync(operation.View, ct);
			states.Add(new WorkspaceState(head, operation, view.Workspace ?? throw new VersionException($"Operation {head.Short} has no workspace.")));
		}

		return states;
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

		var found = await Workspaces.FindAsync(reference, ct);
		var named = found.Where(summary => summary.Name == reference).ToList();
		var open = named.Where(summary => summary.IsOpen).ToList();
		if (open.Count == 1 || (open.Count == 0 && named.Count >= 1))
		{
			return await StateAsync(open.Count == 1 ? open[0] : named[^1], ct);
		}

		if (open.Count > 1)
		{
			throw new VersionException($"Several open workspaces are named '{reference}'; use the id ({string.Join(", ", open.Select(summary => summary.Id))}).");
		}

		var prefixed = reference.Length >= 4 ? found.Where(summary => summary.IsOpen).ToList() : [];
		return prefixed.Count switch
		{
			1 => await StateAsync(prefixed[0], ct),
			0 => throw new VersionException($"No workspace '{reference}'. Run 'bassia workspace list'."),
			_ => throw new VersionException($"'{reference}' matches several workspaces: {string.Join(", ", prefixed.Select(summary => summary.Name))}.")
		};
	}

	/// <summary>The workspace a summary names, as it is now.</summary>
	private async Task<WorkspaceState> StateAsync(WorkspaceSummary summary, CancellationToken ct) =>
		await FindWorkspaceAsync(summary.Id, ct) ?? await WorkspaceAtAsync(summary.Head, ct);

	/// <summary>
	/// The open workspace named <paramref name="name"/>, or null when the name is free. Names are case-sensitive and
	/// unique among open workspaces; a landed or abandoned workspace frees its name.
	/// </summary>
	public async Task<WorkspaceState?> OpenWorkspaceNamedAsync(string name, CancellationToken ct = default)
	{
		if (await Views.GetHeadAsync(HeadName.WorkspaceName(name), ct) is { } head && await NameHolderAsync(head, name, ct) is { } holder)
		{
			return holder;
		}

		// Workspaces opened before names were reserved hold no reservation; they still keep their name.
		return (await Workspaces.FindAsync(name, ct)).FirstOrDefault(summary => summary.IsOpen && summary.Name == name) is { } holding ? await StateAsync(holding, ct) : null;
	}

	/// <summary><paramref name="name"/> if no open workspace holds it, else the first free <c>name-2</c>, <c>name-3</c>, ...</summary>
	public async Task<string> FreeWorkspaceNameAsync(string name, CancellationToken ct = default)
	{
		for (var suffix = 1; ; suffix++)
		{
			var candidate = suffix == 1 ? name : $"{name}-{suffix}";
			if (await OpenWorkspaceNamedAsync(candidate, ct) is null)
			{
				return candidate;
			}
		}
	}

	/// <summary>
	/// Reserves <paramref name="state"/>'s name for it by compare-and-swap on <c>name/&lt;name&gt;</c>: from empty, or
	/// from a reservation whose workspace is no longer open. Throws <see cref="WorkspaceNameTakenException"/> when
	/// another open workspace holds the name.
	/// </summary>
	public async Task ClaimWorkspaceNameAsync(WorkspaceState state, CancellationToken ct = default)
	{
		var head = HeadName.WorkspaceName(state.Name);
		var current = await Views.GetHeadAsync(head, ct);
		for (var attempt = 0; ; attempt++)
		{
			if (current is { } held && await NameHolderAsync(held, state.Name, ct) is { } holder && holder.Id != state.Id)
			{
				throw new WorkspaceNameTakenException(state.Name, holder.Id);
			}

			var update = await Views.AdvanceHeadAsync(head, current, state.Head, ct);
			if (update.Success)
			{
				return;
			}

			if (attempt >= 20)
			{
				throw new ConcurrentUpdateException(head);
			}

			current = update.Current;
		}
	}

	/// <summary>The workspace a name reservation points at, if it is open and still has that name.</summary>
	public async Task<WorkspaceState?> NameHolderAsync(ObjectId reservation, string name, CancellationToken ct = default)
	{
		Operation op;
		try
		{
			op = await Objects.ReadOperationAsync(reservation, ct);
		}
		catch (MissingObjectException)
		{
			return null; // a reservation outliving its workspace's objects holds nothing
		}

		return op.Workspace is { } id && await FindWorkspaceAsync(id, ct) is { IsOpen: true } state && state.Name == name ? state : null;
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
		var update = await Workspaces.AdvanceAsync(current?.Head, WorkspaceSummary.Of(opId, record), ct);
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

	/// <summary>
	/// Brings the workspace registry in step with the heads: records every workspace and candidate head it does not
	/// describe as it is now (the first backfill, a head an older build moved) and drops rows whose head is gone. Returns
	/// the number of heads recorded. With the registry in step this is one question to the store.
	/// </summary>
	public async Task<int> SyncWorkspaceIndexAsync(CancellationToken ct = default)
	{
		var unindexed = await Workspaces.UnindexedAsync(ct);
		foreach (var id in unindexed.Select(item => item.Name.WorkspaceId).Distinct())
		{
			if (await Views.GetHeadAsync(HeadName.Workspace(id), ct) is not { } head)
			{
				continue; // a candidate without its workspace: the reconciler or garbage collection drops it
			}

			var state = await WorkspaceAtAsync(head, ct);
			CandidateSummary? candidate = null;
			if (await Views.GetHeadAsync(HeadName.Candidate(id), ct) is { } candidateHead)
			{
				var stored = await Objects.ReadCandidateAsync(candidateHead, ct);
				candidate = new CandidateSummary(candidateHead, stored.Base, stored.Working, stored.MainSeq, stored.Touched);
			}

			await Workspaces.RecordAsync(WorkspaceSummary.Of(head, state.Record), candidate, ct);
		}

		await Workspaces.PruneAsync(ct);
		return unindexed.Count;
	}

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
