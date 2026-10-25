namespace Bassia.Storage;

/// <summary>
/// A failure of a component backend: an object or ref that does not exist, a ref that already does, a store that cannot
/// be reached. Backends throw it (or a subclass, like <see cref="Bassia.Git.GitException"/>) for every expected failure.
/// </summary>
internal class StorageException(string message) : Exception(message);

/// <summary>A node of a component's history DAG: its id, its parents (none for a root commit) and its message.</summary>
internal sealed record StoredCommit(string Id, IReadOnlyList<string> Parents, string Message)
{
	public string Subject => Message.Split('\n')[0].Trim();
}

/// <summary>A branch: a movable name for a commit.</summary>
internal sealed record StoredBranch(string Name, string Commit, string Subject);

/// <summary>
/// A tag: an immutable name for a commit. Bassia creates annotated tags (with a message and a date); a backend may also
/// report lightweight ones it imported.
/// </summary>
internal sealed record StoredTag(string Name, string Commit, bool Annotated, string Subject, string Date);

/// <summary>A file of a commit: path (with <c>/</c>), mode, content object id and size in bytes.</summary>
internal sealed record StoredFile(string Path, string Mode, string Object, long Size);

/// <summary>
/// A unit nested in a commit at <paramref name="Path"/> and pinned at <paramref name="Commit"/> of another history - a
/// git submodule. <paramref name="Name"/> and <paramref name="Url"/> come from the commit's own description of it
/// (<c>.gitmodules</c>), when there is one.
/// </summary>
internal sealed record NestedUnit(string Path, string Commit, string? Name, string? Url);

/// <summary>An object found by id prefix, with its kind (<c>commit</c>, or a backend's own kinds such as <c>tree</c> or <c>blob</c>).</summary>
internal sealed record StoredObject(string Id, string Kind);

/// <summary>What resolving a name in a store found: a branch or a tag (annotated or not) and the commit it points at.</summary>
internal enum RefKind { Branch, AnnotatedTag, LightweightTag }

internal sealed record ResolvedRef(string FullName, RefKind Kind, string Commit);

/// <summary>
/// A file-level merge driver - a program that merges one file three ways, like weave's entity-level merge. It is run as
/// <paramref name="Command"/> with <c>%O</c>, <c>%A</c> and <c>%B</c> replaced by the paths of the base, ours and theirs
/// versions (<c>%L</c> marker size, <c>%P</c> the file's path in the component); it writes the result over ours and exits
/// non-zero when the file still conflicts. It handles the files with one of <paramref name="Extensions"/>; stderr lines
/// starting with <paramref name="WarningPrefix"/> are warnings about a merge it completed.
/// </summary>
internal sealed record FileMergeDriver(string Name, string Command, IReadOnlyList<string> Extensions, string? WarningPrefix = null)
{
	public bool Handles(string path) => Extensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

	/// <summary>The warnings in a driver's stderr.</summary>
	public IReadOnlyList<string> Warnings(string error) =>
		WarningPrefix is null ? [] : error.Replace("\r", "").Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.StartsWith(WarningPrefix, StringComparison.Ordinal))
			.Select(line => line[WarningPrefix.Length..].Trim())
			.Distinct(StringComparer.Ordinal)
			.ToList();
}

/// <summary>
/// What a merge of two commits came to: clean or not, the paths left conflicted, and the warnings of a file merge
/// driver. <paramref name="Snapshot"/> names the merged content (conflicts included, as markers) for
/// <see cref="IComponentStore.CommitSnapshotAsync"/>.
/// </summary>
internal sealed record MergeResult(bool Clean, string Snapshot, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Warnings);

/// <summary>How a working copy merges a commit (see <see cref="IComponentWorkspace.MergeAsync"/>).</summary>
/// <param name="Commit">Conclude a clean merge with a commit; false leaves even a clean merge open.</param>
/// <param name="FastForward">Move the head without a merge commit when it is an ancestor of the merged commit.</param>
/// <param name="Diff3">Conflict markers include the common ancestor's version.</param>
internal sealed record WorkspaceMerge(string Message, bool Commit = true, bool FastForward = false, bool Diff3 = false, FileMergeDriver? Driver = null);

/// <summary>
/// What a working-copy merge did: <see cref="MergeStatus.Merged"/> (committed, fast-forwarded or left open clean),
/// <see cref="MergeStatus.Conflicted"/> (left open with <paramref name="Conflicts"/>; none when it stopped for another
/// reason) or <see cref="MergeStatus.Failed"/> (nothing changed). <paramref name="Error"/> is the backend's account of
/// anything but a clean merge; <paramref name="Warnings"/> are a file merge driver's.
/// </summary>
internal sealed record MergeOutcome(MergeStatus Status, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Warnings, string? Error = null);

internal enum MergeStatus { Merged, Conflicted, Failed }
