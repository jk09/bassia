namespace Bassia.Core.Version;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Text;

/// <summary>
/// A language-aware merge driver (for example weave's tree-sitter based <c>weave-driver</c>): merges three versions of
/// one file, or returns null when it cannot merge them cleanly.
/// </summary>
public interface IMergeDriver
{
	string Name { get; }

	Task<byte[]?> MergeAsync(string path, byte[] baseContent, byte[] ours, byte[] theirs, CancellationToken ct = default);
}

/// <summary>Validates a machine-merged tree (build and tests) before it may land.</summary>
public interface IValidationHook
{
	Task<(bool Ok, string Message)> ValidateAsync(ObjectId tree, CancellationToken ct = default);
}

/// <summary>
/// Applies the conflict policy of a path both sides rewrote: <c>textmerge</c> and <c>semantic</c> try to merge
/// (semantic results are machine-merged), <c>replay</c> and <c>conflict</c> leave the path unresolved.
/// </summary>
public sealed class ConflictResolver(ObjectAccess objects, RepoPolicy policy, IMergeDriver? semantic = null)
{
	public RepoPolicy Policy => policy;

	/// <summary>Whether any path was merged by the semantic driver (the result needs validation).</summary>
	public bool MachineMerged { get; private set; }

	public async Task<MergeResolution> ResolveAsync(string path, Leaf? baseLeaf, Leaf? ours, Leaf? theirs, CancellationToken ct = default)
	{
		var kind = policy.PolicyFor(path);
		if (kind is ConflictPolicy.Replay or ConflictPolicy.Conflict)
		{
			return MergeResolution.Unresolved;
		}

		// Only edits of a file on both sides can be merged; a deletion against an edit cannot.
		if (ours is not { IsFile: true } || theirs is not { IsFile: true } || baseLeaf is not null && !baseLeaf.IsFile)
		{
			return MergeResolution.Unresolved;
		}

		var baseBytes = baseLeaf is null ? [] : await objects.ReadContentAsync(baseLeaf, ct);
		var oursBytes = await objects.ReadContentAsync(ours, ct);
		var theirsBytes = await objects.ReadContentAsync(theirs, ct);
		if (kind == ConflictPolicy.TextMerge)
		{
			if (!LineDiff.IsText(baseBytes) || !LineDiff.IsText(oursBytes) || !LineDiff.IsText(theirsBytes))
			{
				return MergeResolution.Unresolved;
			}

			var baseLines = LineDiff.Lines(baseBytes);
			var oursLines = LineDiff.Lines(oursBytes);
			var theirsLines = LineDiff.Lines(theirsBytes);
			if (LineDiff.Similarity(baseLines, oursLines) < policy.TextMergeMinSimilarity
				|| LineDiff.Similarity(baseLines, theirsLines) < policy.TextMergeMinSimilarity)
			{
				return MergeResolution.Unresolved;
			}

			var merged = TextMerge.Merge(baseLines, oursLines, theirsLines);
			if (!merged.Clean)
			{
				return MergeResolution.Unresolved;
			}

			var leaf = await objects.WriteContentAsync(Encoding.UTF8.GetBytes(merged.Text), ours.Executable, ct);
			return MergeResolution.With(leaf, ConflictPolicy.TextMerge);
		}

		if (kind == ConflictPolicy.Semantic && semantic is not null)
		{
			var result = await semantic.MergeAsync(path, baseBytes, oursBytes, theirsBytes, ct);
			if (result is null)
			{
				return MergeResolution.Unresolved;
			}

			MachineMerged = true;
			var leaf = await objects.WriteContentAsync(result, ours.Executable, ct);
			return MergeResolution.With(leaf, $"{ConflictPolicy.Semantic} ({semantic.Name})");
		}

		return MergeResolution.Unresolved;
	}
}
