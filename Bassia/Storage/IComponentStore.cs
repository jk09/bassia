namespace Bassia.Storage;

/// <summary>
/// The backing store of one component: a DAG of commits with branches and annotated tags pointing into it. This is
/// everything the component graph needs from a component's history; <see cref="Git.GitComponentStore"/> keeps it in a
/// bare git repository, <see cref="Memory.MemoryComponentStore"/> in memory, and a future backend could keep blobs in
/// cloud storage and the DAG in a database.
/// </summary>
internal interface IComponentStore
{
	/// <summary>The component's name in the monorepo.</summary>
	string Name { get; }

	/// <summary>Where the store lives, as the backend names it (a folder for git); shown to users, never parsed.</summary>
	string Location { get; }

	// ----- branches -----

	/// <summary>The default (main) branch's name, or null when the store has none (a detached head). It may have no commit yet.</summary>
	Task<string?> DefaultBranchAsync();

	Task<IReadOnlyList<StoredBranch>> ListBranchesAsync();

	/// <summary>The commit <paramref name="branch"/> points at, or null when there is no such branch (or it has no commit yet).</summary>
	Task<string?> BranchTipAsync(string branch);

	/// <summary>
	/// Points <paramref name="branch"/> at <paramref name="commit"/>, creating it when needed. With <paramref name="expected"/>
	/// the move only happens while the branch still points there (compare-and-swap); otherwise it throws.
	/// </summary>
	Task SetBranchAsync(string branch, string commit, string? expected = null);

	// ----- tags -----

	/// <summary>Every tag, or those whose name starts with <paramref name="prefix"/>, sorted by name.</summary>
	Task<IReadOnlyList<StoredTag>> ListTagsAsync(string? prefix = null);

	Task<StoredTag?> GetTagAsync(string name);

	/// <summary>Creates the annotated tag <paramref name="name"/> on <paramref name="commit"/>; throws when it already exists.</summary>
	Task CreateTagAsync(string name, string commit, string message);

	Task DeleteTagAsync(string name);

	// ----- the commit DAG -----

	/// <summary>The commit with this full id, or null when the store has none.</summary>
	Task<StoredCommit?> GetCommitAsync(string id);

	/// <summary>The objects whose id starts with <paramref name="prefix"/>: none, one, or several (an ambiguous prefix).</summary>
	Task<IReadOnlyList<StoredObject>> FindObjectsAsync(string prefix);

	/// <summary>Resolves a branch or tag name (short or full, e.g. <c>main</c>, <c>v1</c>, <c>refs/tags/v1</c>); null when nothing has that name.</summary>
	Task<ResolvedRef?> ResolveRefAsync(string name);

	/// <summary>True when <paramref name="ancestor"/> is <paramref name="descendant"/> or one of its ancestors.</summary>
	Task<bool> IsAncestorAsync(string ancestor, string descendant);

	/// <summary><paramref name="tip"/> and its ancestors, parents after children, newest first.</summary>
	Task<IReadOnlyList<StoredCommit>> HistoryAsync(string tip);

	// ----- content -----

	/// <summary>The files of <paramref name="commit"/>, nested units excluded, in path order.</summary>
	Task<IReadOnlyList<StoredFile>> ListFilesAsync(string commit);

	/// <summary>The units nested in <paramref name="commit"/> (git submodules), in path order.</summary>
	Task<IReadOnlyList<NestedUnit>> ListNestedAsync(string commit);

	/// <summary>
	/// A new commit on top of <paramref name="commit"/> with the same content minus the units nested at
	/// <paramref name="paths"/> (and their descriptions). No branch moves; returns the new commit's id.
	/// </summary>
	Task<string> CommitWithoutNestedAsync(string commit, IReadOnlyList<string> paths, string message);

	/// <summary>
	/// Makes sure <paramref name="commit"/> is in the store, fetching it from <paramref name="url"/> when it is not.
	/// Returns false when it still is not there.
	/// </summary>
	Task<bool> EnsureCommitAsync(string commit, string url);

	/// <summary>The upstream the store was imported from (git's <c>origin</c>), or null.</summary>
	Task<string?> UpstreamAsync();

	// ----- working copies -----

	/// <summary>
	/// Materializes <paramref name="commit"/> into the empty or missing folder <paramref name="directory"/> as a working copy
	/// on the new branch <paramref name="branch"/>: an agent edits it, and the working copy commits, tags and publishes what
	/// it changed back to this store.
	/// </summary>
	Task<IComponentWorkspace> CheckoutAsync(string commit, string directory, string branch);
}

/// <summary>
/// A working copy of a component: plain files in <see cref="Directory"/> that anything (an agent) may change, and the
/// operations that turn those changes into a commit of the component's history.
/// </summary>
internal interface IComponentWorkspace
{
	string Directory { get; }

	/// <summary>Leaves the file or folder at <paramref name="path"/> (relative, with <c>/</c>) out of every commit: a nested component lives there.</summary>
	Task HideAsync(string path);

	/// <summary>
	/// Commits every change in the working copy on its branch and returns the commit, or null when nothing changed.
	/// The commit is local to the working copy until it is published.
	/// </summary>
	Task<string?> CommitAsync(string message);

	/// <summary>Tags visible from the working copy (its own, and the store's as of the checkout) whose name starts with <paramref name="prefix"/>.</summary>
	Task<IReadOnlyList<string>> ListTagsAsync(string prefix);

	/// <summary>The commit the tag points at in the working copy, or null.</summary>
	Task<string?> TagCommitAsync(string name);

	/// <summary>Creates an annotated tag in the working copy.</summary>
	Task TagAsync(string name, string commit, string message);

	/// <summary>Publishes the working copy's branch and the named tags to the store it was checked out from.</summary>
	Task PublishAsync(string branch, IReadOnlyList<string> tags);
}
