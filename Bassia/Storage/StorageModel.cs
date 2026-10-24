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
