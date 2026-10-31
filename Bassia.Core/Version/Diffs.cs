namespace Bassia.Core.Version;

using Bassia.Core.Model;
using Bassia.Core.Text;

/// <summary>How a path changed.</summary>
public static class ChangeStatus
{
	public const string Added = "added";
	public const string Deleted = "deleted";
	public const string Modified = "modified";
	public const string Renamed = "renamed";
}

/// <summary>
/// One record of a derived diff: added, deleted, modified (old and new id) or renamed (from <see cref="OldPath"/>).
/// <see cref="Similarity"/> is how much of the old content the new one keeps, when it was computed.
/// </summary>
public sealed record DiffRecord(string Path, string Status, Leaf? Old, Leaf? New, string? OldPath = null, double? Similarity = null)
{
	public long SizeDelta => (New?.Size ?? 0) - (Old?.Size ?? 0);
}

/// <summary>
/// Derived diffs: records from tree comparison (renames by equal id, then by chunk overlap), diff statistics, and
/// lazily the review form of a modified file - unified hunks, or a rewrite (old and new side by side) when the new
/// version shares little with the old.
/// </summary>
public sealed class Diffs(Trees trees)
{
	/// <summary>Below this share of common content a modified file is reviewed as rewritten.</summary>
	public const double RewriteThreshold = 0.5;

	private ObjectAccess Objects => trees.Objects;

	public async Task<IReadOnlyList<DiffRecord>> DiffAsync(ObjectId? oldTree, ObjectId? newTree, bool detectRenames = true, CancellationToken ct = default)
	{
		var changes = await trees.DiffAsync(oldTree, newTree, "", ct);
		var records = new List<DiffRecord>();
		var deleted = changes.Where(change => change.New is null && change.Old is not null).ToList();
		var added = changes.Where(change => change.Old is null && change.New is not null).ToList();
		var renamedFrom = new HashSet<string>(StringComparer.Ordinal);
		var renamedTo = new Dictionary<string, (string From, double Similarity)>(StringComparer.Ordinal);
		if (detectRenames && deleted.Count > 0 && added.Count > 0)
		{
			// Exact renames: the same content id.
			var byId = deleted.GroupBy(change => (change.Old!.Kind, change.Old.Id)).ToDictionary(group => group.Key, group => new Queue<TreeChange>(group));
			foreach (var change in added)
			{
				if (byId.TryGetValue((change.New!.Kind, change.New.Id), out var queue) && queue.Count > 0)
				{
					var from = queue.Dequeue();
					renamedFrom.Add(from.Path);
					renamedTo[change.Path] = (from.Path, 1.0);
				}
			}

			// Similar renames: chunked files sharing at least half of their chunks.
			var oldChunked = deleted.Where(change => !renamedFrom.Contains(change.Path) && change.Old!.Kind == EntryKind.Chunked).ToList();
			foreach (var change in added.Where(change => !renamedTo.ContainsKey(change.Path) && change.New!.Kind == EntryKind.Chunked))
			{
				var best = (Path: (string?)null, Share: 0.0);
				foreach (var candidate in oldChunked.Where(candidate => !renamedFrom.Contains(candidate.Path)))
				{
					var share = await ChunkShareAsync(candidate.Old!, change.New!, ct);
					if (share > best.Share)
					{
						best = (candidate.Path, share);
					}
				}

				if (best.Path is not null && best.Share >= RewriteThreshold)
				{
					renamedFrom.Add(best.Path);
					renamedTo[change.Path] = (best.Path, best.Share);
				}
			}
		}

		var oldByPath = deleted.ToDictionary(change => change.Path, change => change.Old, StringComparer.Ordinal);
		foreach (var change in changes)
		{
			if (change.Old is not null && change.New is not null)
			{
				records.Add(new DiffRecord(change.Path, ChangeStatus.Modified, change.Old, change.New));
			}
			else if (change.New is not null)
			{
				records.Add(renamedTo.TryGetValue(change.Path, out var from)
					? new DiffRecord(change.Path, ChangeStatus.Renamed, oldByPath[from.From], change.New, from.From, from.Similarity)
					: new DiffRecord(change.Path, ChangeStatus.Added, null, change.New));
			}
			else if (!renamedFrom.Contains(change.Path))
			{
				records.Add(new DiffRecord(change.Path, ChangeStatus.Deleted, change.Old, null));
			}
		}

		return records.OrderBy(record => record.Path, StringComparer.Ordinal).ToList();
	}

	/// <summary>The share of the new file's bytes that are in chunks the old file also has.</summary>
	public async Task<double> ChunkShareAsync(Leaf oldLeaf, Leaf newLeaf, CancellationToken ct = default)
	{
		if (!oldLeaf.IsFile || !newLeaf.IsFile || newLeaf.Size == 0)
		{
			return oldLeaf.Id == newLeaf.Id ? 1 : 0;
		}

		var oldChunks = (await Objects.ChunksAsync(oldLeaf, ct)).Select(chunk => chunk.Id).ToHashSet();
		var newChunks = await Objects.ChunksAsync(newLeaf, ct);
		return (double)newChunks.Where(chunk => oldChunks.Contains(chunk.Id)).Sum(chunk => chunk.Length) / newLeaf.Size;
	}

	/// <summary>
	/// The review form of one record: a unified diff for an ordinary text edit; for a rewrite (new and old share less
	/// than <see cref="RewriteThreshold"/>) or binary content, a summary instead of a wall of hunks.
	/// </summary>
	public async Task<FilePatch> PatchAsync(DiffRecord record, int context = 3, CancellationToken ct = default)
	{
		var oldBytes = record.Old is { IsFile: true } or { Kind: EntryKind.Symlink } ? await Objects.ReadContentAsync(record.Old, ct) : [];
		var newBytes = record.New is { IsFile: true } or { Kind: EntryKind.Symlink } ? await Objects.ReadContentAsync(record.New, ct) : [];
		if (!LineDiff.IsText(oldBytes) || !LineDiff.IsText(newBytes))
		{
			return new FilePatch(record, false, true, 0, 0, null, "binary content");
		}

		var oldLines = LineDiff.Lines(oldBytes);
		var newLines = LineDiff.Lines(newBytes);
		var (added, deleted) = LineDiff.Stat(oldLines, newLines);
		var similarity = record.Status == ChangeStatus.Modified || record.Status == ChangeStatus.Renamed
			? LineDiff.Similarity(oldLines, newLines)
			: 1;
		var rewritten = record.Status is ChangeStatus.Modified or ChangeStatus.Renamed && oldLines.Length > 0 && newLines.Length > 0 && similarity < RewriteThreshold;
		var oldPath = record.Old is null ? "/dev/null" : "a/" + (record.OldPath ?? record.Path);
		var newPath = record.New is null ? "/dev/null" : "b/" + record.Path;
		var summary = rewritten
			? $"rewritten: {Math.Round(similarity * 100)}% of lines kept, {oldLines.Length} -> {newLines.Length} lines"
			: null;
		return new FilePatch(record, rewritten, false, added, deleted, rewritten ? null : LineDiff.Unified(oldPath, newPath, oldLines, newLines, context), summary);
	}
}

/// <summary>A file's review form: line statistics and either hunks or (rewritten, binary) a summary.</summary>
public sealed record FilePatch(DiffRecord Record, bool Rewritten, bool Binary, int Added, int Deleted, string? Hunks, string? Summary);
