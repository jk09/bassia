namespace Bassia.Core;

using System.Text.RegularExpressions;
using Bassia.Storage;

/// <summary>
/// Names for commits of a component, resolved through the storage contract alone: <c>HEAD</c> (the tip of the default
/// branch), a branch or tag name, a full commit id or an unambiguous prefix of one (6 to 64 hex digits), each optionally
/// followed by <c>~n</c> (the n-th first-parent ancestor) and <c>^n</c> (the n-th parent) steps.
/// </summary>
internal static partial class Revisions
{
	/// <summary>The commit <paramref name="name"/> names in <paramref name="store"/>, or null when it names none.</summary>
	public static async Task<string?> ResolveAsync(IComponentStore store, string name)
	{
		var match = Steps().Match(name);
		var start = match.Groups["base"].Value;
		var commit = start == "HEAD"
			? await store.DefaultBranchAsync() is { } branch ? await store.BranchTipAsync(branch) : null
			: (await store.ResolveRefAsync(start))?.Commit ?? await ResolveHashAsync(store, start);

		foreach (Capture step in match.Groups["step"].Captures)
		{
			if (commit is null)
			{
				return null;
			}

			var count = step.Value.Length > 1 ? int.Parse(step.Value[1..]) : 1;
			if (step.Value[0] == '^')
			{
				commit = count == 0 ? commit : (await store.GetCommitAsync(commit))?.Parents.ElementAtOrDefault(count - 1);
				continue;
			}

			for (var i = 0; i < count && commit is not null; i++)
			{
				commit = (await store.GetCommitAsync(commit))?.Parents.FirstOrDefault();
			}
		}

		return commit;
	}

	/// <summary>A commit id or a prefix that only one commit's id starts with.</summary>
	private static async Task<string?> ResolveHashAsync(IComponentStore store, string hash)
	{
		if (hash.Length is < 6 or > 64 || !hash.All(char.IsAsciiHexDigitLower))
		{
			return null;
		}

		var commits = (await store.FindObjectsAsync(hash)).Where(item => item.Kind == "commit").ToList();
		return commits.Count == 1 ? commits[0].Id : null;
	}

	[GeneratedRegex(@"^(?<base>.*?)(?<step>[~^]\d*)*$")]
	private static partial Regex Steps();
}
