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

	// ----- merging and comparing -----

	/// <summary>
	/// The three-way merge of <paramref name="ours"/> and <paramref name="theirs"/> on their merge base, computed without a
	/// working copy and without moving any ref. With <paramref name="driver"/>, the files it handles are merged by it.
	/// </summary>
	Task<MergeResult> MergeAsync(string ours, string theirs, FileMergeDriver? driver = null);

	/// <summary>A commit (referenced by nothing) with the content of <paramref name="snapshot"/> (see <see cref="MergeResult.Snapshot"/>) and these parents.</summary>
	Task<string> CommitSnapshotAsync(string snapshot, IReadOnlyList<string> parents, string message);

	/// <summary>The best common ancestor of two commits, or null when they share none.</summary>
	Task<string?> MergeBaseAsync(string first, string second);

	/// <summary>The paths whose content differs between two commits, in path order.</summary>
	Task<IReadOnlyList<string>> ChangedPathsAsync(string from, string to);

	/// <summary>A unified diff (patch text) from <paramref name="from"/> to <paramref name="to"/>, for people and agents to read.</summary>
	Task<string> DiffAsync(string from, string to);

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
	/// With a merge open, it concludes it: the commit has the merged commit as its second parent, changed or not.
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

	/// <summary>The commit the working copy's branch is at.</summary>
	Task<string> HeadAsync();

	/// <summary>
	/// The files changed in the working copy and not committed (modified, added, deleted, untracked), relative to its
	/// root, hidden paths excluded.
	/// </summary>
	Task<IReadOnlyList<string>> ChangedPathsAsync();

	/// <summary>Whether <paramref name="commit"/> is in the working copy's history: it has the commit and the head contains it.</summary>
	Task<bool> ContainsAsync(string commit);

	/// <summary>Brings the store's tag <paramref name="tag"/> and its history into the working copy; false when the store has no such tag.</summary>
	Task<bool> FetchTagAsync(string tag);

	/// <summary>
	/// Merges <paramref name="commit"/> into the head. <see cref="WorkspaceMerge.Commit"/> concludes a clean merge with a
	/// commit described by <see cref="WorkspaceMerge.Message"/>; otherwise (and on a conflict) the merge is left open in
	/// the working copy - conflicts marked in the files - for <see cref="CommitAsync"/> to conclude or
	/// <see cref="ResetAsync"/> to abandon. A merge that would overwrite uncommitted changes is refused.
	/// </summary>
	Task<MergeOutcome> MergeAsync(string commit, WorkspaceMerge options);

	/// <summary>Whether a merge is open (started by <see cref="MergeAsync"/> and neither concluded nor reset).</summary>
	Task<bool> MergeInProgressAsync();

	/// <summary>The paths an open merge left conflicted.</summary>
	Task<IReadOnlyList<string>> ConflictedPathsAsync();

	/// <summary>Puts the working copy back exactly at <paramref name="commit"/>: no open merge, no changed and no untracked files.</summary>
	Task ResetAsync(string commit);

	/// <summary>Abandons an open merge, keeping the uncommitted changes that were there before it started.</summary>
	Task AbortMergeAsync();

	/// <summary>The paths whose content differs between two commits the working copy has.</summary>
	Task<IReadOnlyList<string>> ChangedPathsAsync(string from, string to);

	/// <summary>The best common ancestor of two commits the working copy has, or null.</summary>
	Task<string?> MergeBaseAsync(string first, string second);

	/// <summary>The commits reachable from <paramref name="to"/> but not from <paramref name="from"/>, newest first.</summary>
	Task<IReadOnlyList<StoredCommit>> LogAsync(string from, string to);

	/// <summary>A unified diff from <paramref name="from"/> to <paramref name="to"/>, two commits the working copy has.</summary>
	Task<string> DiffAsync(string from, string to);
}
