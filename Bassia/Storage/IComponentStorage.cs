namespace Bassia.Storage;

using Bassia.Split;

/// <summary>
/// A backend for the components of one monorepo: it creates, imports, opens, forks, splits and deletes component stores
/// by component name. The component graph (<see cref="Core.MonorepoGraph"/>) uses nothing else to reach the history of
/// a component, so replacing git means implementing this interface, <see cref="IComponentStore"/>,
/// <see cref="IComponentWorkspace"/> and <see cref="IStagingArea"/>.
/// </summary>
internal interface IComponentStorage
{
	/// <summary>True when a store (or anything else in its place) exists for <paramref name="name"/>, registered or not.</summary>
	bool Exists(string name);

	/// <summary>Where the store of <paramref name="name"/> lives or would live (see <see cref="IComponentStore.Location"/>).</summary>
	string LocationOf(string name);

	/// <summary>The existing store of <paramref name="name"/>; throws <see cref="StorageException"/> when there is none.</summary>
	IComponentStore Open(string name);

	/// <summary>
	/// A new store whose default branch <paramref name="defaultBranch"/> holds one root commit without files, described by
	/// <paramref name="message"/>; <paramref name="url"/>, when not empty, is its upstream.
	/// </summary>
	Task<IComponentStore> CreateAsync(string name, string defaultBranch, string url, string message);

	/// <summary>A new store with the complete history, branches and tags of the repository at <paramref name="url"/>.</summary>
	Task<IComponentStore> ImportAsync(string name, string url);

	/// <summary>
	/// A new store with the history of the repository at <paramref name="url"/> - commits, branches, tags and file
	/// listings - but none of the files' content: a cache that fetches content from <paramref name="url"/>, its upstream,
	/// when it is needed (see <see cref="IComponentStore.HydrateAsync"/>).
	/// </summary>
	Task<IComponentStore> ImportHistoryAsync(string name, string url);

	/// <summary>
	/// A new store holding <paramref name="source"/>'s history up to <paramref name="commit"/> on the single branch
	/// <paramref name="branch"/> (pointing at <paramref name="commit"/>), the named <paramref name="tags"/> and nothing else.
	/// Commit ids are preserved. <paramref name="url"/>, when not empty, is its upstream.
	/// </summary>
	Task<IComponentStore> ForkAsync(IComponentStore source, string name, string branch, string commit, IReadOnlyList<string> tags, string url);

	/// <summary>
	/// Rewrites the history of <paramref name="source"/>'s branch into one new store per part of the split (see
	/// <see cref="SplitHistoryRequest"/>), each ending in exactly its allocated files. The source is not changed. On
	/// failure the stores created so far are left for the caller to delete.
	/// </summary>
	Task<IReadOnlyList<SplitPartResult>> SplitAsync(IComponentStore source, SplitHistoryRequest request);

	/// <summary>Deletes the store of <paramref name="name"/> with all its history.</summary>
	Task DeleteAsync(string name);

	/// <summary>A place to import stores into before they are known to be wanted (see <see cref="IStagingArea"/>).</summary>
	IStagingArea BeginStaging();

	/// <summary>Reopens the working copy an earlier <see cref="IComponentStore.CheckoutAsync"/> of <paramref name="name"/> made in <paramref name="directory"/>.</summary>
	IComponentWorkspace OpenWorkspace(string name, string directory);

	/// <summary>
	/// Moves branches of several components, each only while it still points at its expected commit: all of them, or -
	/// when one has moved - none, with <see cref="BranchesMovedException"/> naming those that moved. A backend that cannot
	/// move them in one step checks every expectation before it moves anything.
	/// </summary>
	Task MoveBranchesAsync(IReadOnlyList<BranchMove> moves);
}

/// <summary>A branch to move: the component, the branch, the commit it is to point at and the one it must point at now.</summary>
internal sealed record BranchMove(string Component, string Branch, string Commit, string Expected);

/// <summary>Branches that no longer point where <see cref="IComponentStorage.MoveBranchesAsync"/> expected; no branch was moved.</summary>
internal sealed class BranchesMovedException(IReadOnlyList<BranchMove> moved)
	: StorageException($"{string.Join(", ", moved.Select(move => $"'{move.Branch}' of component '{move.Component}'"))} moved meanwhile; no branch was moved.")
{
	public IReadOnlyList<BranchMove> Moved { get; } = moved;
}

internal static class BranchMoves
{
	/// <summary>
	/// The move for a storage that changes one store at a time: every expectation is checked first; a branch that moves
	/// in between is caught by its compare-and-swap, and the branches moved before it are put back.
	/// </summary>
	public static async Task InSequenceAsync(IComponentStorage storage, IReadOnlyList<BranchMove> moves)
	{
		var moved = new List<BranchMove>();
		foreach (var move in moves)
		{
			if (!storage.Exists(move.Component) || await storage.Open(move.Component).BranchTipAsync(move.Branch) != move.Expected)
			{
				moved.Add(move);
			}
		}

		if (moved.Count > 0)
		{
			throw new BranchesMovedException(moved);
		}

		var done = new List<BranchMove>();
		foreach (var move in moves)
		{
			try
			{
				await storage.Open(move.Component).SetBranchAsync(move.Branch, move.Commit, move.Expected);
				done.Add(move);
			}
			catch (StorageException)
			{
				foreach (var back in done)
				{
					await storage.Open(back.Component).SetBranchAsync(back.Branch, back.Expected, back.Commit);
				}

				throw new BranchesMovedException([move]);
			}
		}
	}
}

/// <summary>
/// Stores imported on approval: they are invisible to <see cref="IComponentStorage.Exists"/> and are dropped when the
/// area is disposed, unless promoted into the storage first. Planning an unwrap imports into one, so a plan that fails
/// leaves nothing behind.
/// </summary>
internal interface IStagingArea : IDisposable
{
	Task<IComponentStore> ImportAsync(string name, string url);

	/// <summary>Moves a staged store into the storage under its name; returns it as the storage now holds it.</summary>
	Task<IComponentStore> PromoteAsync(IComponentStore staged);
}

/// <summary>
/// What a backend needs to split a history: the checked allocation (the plan, the source's branch and tip, and the
/// files per part), the tag that will mark the source, and each part's record (the message of its final commit).
/// </summary>
internal sealed record SplitHistoryRequest(SplitAllocation Allocation, string SplitId, string Tag, Func<SplitPart, string> RecordMessage);
