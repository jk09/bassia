namespace Bassia.Core.Model;

/// <summary>What a tree entry names.</summary>
public enum EntryKind
{
	/// <summary>A file stored as one chunk: the entry's id is the chunk's id.</summary>
	File = 0,

	/// <summary>A file of several chunks: the entry's id is a <see cref="FileObject"/>.</summary>
	Chunked = 1,

	/// <summary>A symbolic link: the entry's id is a chunk holding the link target.</summary>
	Symlink = 2,

	/// <summary>A directory: the entry's id is a <see cref="Tree"/>.</summary>
	Tree = 3,

	/// <summary>A conflicted path: the entry's id is a <see cref="ConflictObject"/>.</summary>
	Conflict = 4
}

/// <summary>A tree entry without its name: what a path holds. Sides of a conflict and diff records are leaves.</summary>
public sealed record Leaf(EntryKind Kind, ObjectId Id, long Size, bool Executable = false)
{
	public bool IsFile => Kind is EntryKind.File or EntryKind.Chunked;
}

/// <summary>One named entry of a <see cref="Tree"/>. Size and kind make listing, status and diff statistics cheap.</summary>
public sealed record TreeEntry(string Name, EntryKind Kind, ObjectId Id, long Size, bool Executable = false)
{
	public Leaf Leaf => new(Kind, Id, Size, Executable);

	public static TreeEntry Of(string name, Leaf leaf) => new(name, leaf.Kind, leaf.Id, leaf.Size, leaf.Executable);
}

/// <summary>Any object stored with a canonical encoding (everything but a raw chunk).</summary>
public abstract record StoredObject;

/// <summary>A directory: entries sorted by the ordinal order of their names, names unique.</summary>
public sealed record Tree(IReadOnlyList<TreeEntry> Entries) : StoredObject
{
	public static readonly Tree Empty = new([]);

	public TreeEntry? Find(string name)
	{
		var low = 0;
		var high = Entries.Count - 1;
		while (low <= high)
		{
			var middle = (low + high) / 2;
			var order = string.CompareOrdinal(Entries[middle].Name, name);
			if (order == 0)
			{
				return Entries[middle];
			}

			if (order < 0)
			{
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}

		return null;
	}

	/// <summary>A tree of <paramref name="entries"/> in canonical order; a duplicate name is refused.</summary>
	public static Tree Of(IEnumerable<TreeEntry> entries)
	{
		var sorted = entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();
		for (var index = 1; index < sorted.Count; index++)
		{
			if (sorted[index - 1].Name == sorted[index].Name)
			{
				throw new InvalidDataException($"A tree cannot hold two entries named '{sorted[index].Name}'.");
			}
		}

		return new Tree(sorted);
	}

	public long TotalSize => Entries.Sum(entry => entry.Size);
}

/// <summary>One chunk of a file: its id and length.</summary>
public sealed record ChunkRef(ObjectId Id, long Length);

/// <summary>A file of several chunks: their ids in order and the total size.</summary>
public sealed record FileObject(long Size, IReadOnlyList<ChunkRef> Chunks) : StoredObject;

/// <summary>
/// A conflicted path, in Jujutsu's removes-and-adds form: one base (or more, after repeated merges) and two or more
/// sides. A null leaf is an absent path (deleted on that side).
/// </summary>
public sealed record ConflictObject(IReadOnlyList<Leaf?> Bases, IReadOnlyList<Leaf?> Sides) : StoredObject;

/// <summary>Who made a commit: the agent (or person) and the agentic run, when there is one.</summary>
public sealed record Author(string Agent, string? Run = null)
{
	public override string ToString() => Run is null ? Agent : $"{Agent} ({Run})";
}

/// <summary>
/// How to make a commit again on a new base: the task (a prompt, or a command), the agent and its configuration. The
/// orchestrator re-runs it when a landing needs a replay; the replay keeps the commit's ChangeId.
/// </summary>
public sealed record ReplayRecipe(string? Prompt, string? Command, string? Agent, string? Model, ObjectId? TaskSpec = null);

/// <summary>
/// A snapshot of the whole tree with its history. The CommitId (hash of the encoding) changes on every snapshot,
/// rebase and landing; the <see cref="ChangeId"/> does not.
/// </summary>
public sealed record Commit(
	ObjectId Tree,
	IReadOnlyList<ObjectId> Parents,
	ChangeId ChangeId,
	Author Author,
	string Intent,
	DateTimeOffset Time,
	ReplayRecipe? Recipe = null,
	bool MachineMerged = false) : StoredObject;

/// <summary>
/// One mutation: the view it produced, why, by whom and when. Operations form one linear chain per head; on
/// <c>main</c> the <see cref="Seq"/> numbers the landings, on a workspace head its operations.
/// </summary>
public sealed record Operation(
	IReadOnlyList<ObjectId> Parents,
	ObjectId View,
	string Description,
	string? Workspace,
	string? Agent,
	string? Run,
	DateTimeOffset Time,
	long Seq) : StoredObject;

/// <summary>
/// What a head shows after an operation: named refs (<c>main</c> on mainline) and, on a workspace head, the
/// workspace's record.
/// </summary>
public sealed record View(IReadOnlyDictionary<string, ObjectId> Refs, WorkspaceRecord? Workspace = null) : StoredObject;

/// <summary>Where a workspace is in its life.</summary>
public static class WorkspaceStatus
{
	public const string Open = "open";
	public const string Landed = "landed";
	public const string NeedsReplay = "needs-replay";
	public const string Replaced = "replaced";
	public const string Closed = "closed";
	public const string Abandoned = "abandoned";

	public static bool IsOpen(string status) => status is Open or NeedsReplay;
}

/// <summary>
/// A workspace: its base (a mainline commit at <see cref="BaseSeq"/>, or the working commit of the parent workspace
/// it is stacked on), its working commit, and what happened to it: the agentic run working in it and its last landing.
/// </summary>
public sealed record WorkspaceRecord(
	string Id,
	string Name,
	string? Parent,
	ObjectId Base,
	long BaseSeq,
	ObjectId Working,
	string Status,
	DateTimeOffset Created,
	string Owner,
	string? Path = null,
	string? Host = null,
	RunRecord? Run = null,
	LandRecord? LastLand = null,
	string? ReplayOf = null,
	DateTimeOffset? Closed = null);

/// <summary>An agentic run working in a workspace.</summary>
public sealed record RunRecord(
	string RunId,
	string Command,
	string? Prompt,
	string Agent,
	string? Model,
	string Status,
	DateTimeOffset Started,
	DateTimeOffset? Finished = null,
	int? ExitCode = null,
	ObjectId? Log = null,
	string? Host = null,
	int? Pid = null,
	string? LastLine = null);

/// <summary>The status of an agentic run.</summary>
public static class RunStatus
{
	public const string Running = "running";
	public const string Succeeded = "succeeded";
	public const string Failed = "failed";
	public const string Stopped = "stopped";
}

/// <summary>The outcome of a workspace's last landing attempt.</summary>
public sealed record LandRecord(
	string Outcome,
	DateTimeOffset Time,
	long? Seq,
	ObjectId? Commit,
	IReadOnlyList<string> Conflicts,
	string Message);
