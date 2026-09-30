namespace Bassia.Split;

using System.Collections;

/// <summary>A file at the tip of the split branch.</summary>
internal sealed record TipFile(string Path, string Mode, string Object, long Size);

/// <summary>A rename <c>old -&gt; new</c> made by the commit at stream index <c>Commit</c>.</summary>
internal readonly record struct Rename(int Commit, string Old, string New);

/// <summary>
/// Which parts a path belongs to, at a given commit of the source's history. A path belongs to a part
/// <list type="bullet">
/// <item>when the plan allocates it: a file at the tip goes to the part it was allocated to, any other path to the
/// first part whose patterns match it; a <c>shared</c> path goes to every part, a <c>drop</c> path to none; or</item>
/// <item>when it is an earlier name of one of the part's files (<c>follow_renames</c>): walking the history newest
/// first, a rename <c>old -&gt; new</c> where <c>new</c> belongs to the part claims <c>old</c> for the part too. If
/// <c>old</c> is used again at the tip by another file, the claim only holds in the commits up to the rename (its
/// ancestors), and the part that owns the later file yields the path in those commits: each part gets its own
/// file's timeline and not the other's.</item>
/// </list>
/// </summary>
internal sealed class Ownership
{
	private const int SharedOwner = -1;
	private const int DropOwner = -2;

	private readonly SplitPlan plan;
	private readonly Dictionary<string, int> tipOwner;
	private readonly Dictionary<string, int> staticOwner = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Claim>[] claims;

	/// <summary>Tip paths that belonged to another file up to these renames: their owner does not own them there.</summary>
	private readonly Dictionary<string, List<int>> yields = new(StringComparer.Ordinal);
	private readonly IReadOnlyList<ExportedCommit> commits;
	private readonly Dictionary<int, BitArray> ancestors = [];

	public Ownership(SplitPlan plan, IReadOnlyDictionary<string, int> tipAllocation, IReadOnlyList<ExportedCommit> commits, IEnumerable<Rename> renames)
	{
		this.plan = plan;
		this.commits = commits;
		tipOwner = new Dictionary<string, int>(tipAllocation, StringComparer.Ordinal);
		claims = plan.Parts.Select(_ => new Dictionary<string, Claim>(StringComparer.Ordinal)).ToArray();
		if (plan.FollowRenames)
		{
			// Descendants before ancestors: a rename's new name has its own claims before its old name is looked at.
			foreach (var rename in renames.OrderByDescending(rename => rename.Commit))
			{
				for (var part = 0; part < claims.Length; part++)
				{
					if (!Belongs(part, rename.New, rename.Commit) || StaticallyOwns(part, rename.Old))
					{
						continue;
					}

					var claim = claims[part].TryGetValue(rename.Old, out var existing) ? existing : claims[part][rename.Old] = new Claim();
					if (tipOwner.ContainsKey(rename.Old))
					{
						claim.UpTo.Add(rename.Commit);
						(yields.TryGetValue(rename.Old, out var list) ? list : yields[rename.Old] = []).Add(rename.Commit);
					}
					else
					{
						claim.Always = true;
					}
				}
			}
		}
	}

	/// <summary>The paths claimed through renames, per part: the earlier names its history follows.</summary>
	public int ClaimedPaths(int part) => claims[part].Count;

	public bool Belongs(int part, string path, int commit)
	{
		if (claims[part].TryGetValue(path, out var claim) && (claim.Always || claim.UpTo.Any(rename => UpTo(rename, commit))))
		{
			return true;
		}

		return StaticallyOwns(part, path) && !(yields.TryGetValue(path, out var renames) && renames.Any(rename => UpTo(rename, commit)));
	}

	/// <summary>Whether <paramref name="commit"/> is <paramref name="rename"/> or one of its ancestors.</summary>
	private bool UpTo(int rename, int commit) => rename == commit || AncestorsOf(rename)[commit];

	private bool StaticallyOwns(int part, string path)
	{
		var owner = OwnerOf(path);
		return owner == part || owner == SharedOwner;
	}

	private int OwnerOf(string path)
	{
		if (tipOwner.TryGetValue(path, out var owner))
		{
			return owner;
		}

		if (staticOwner.TryGetValue(path, out owner))
		{
			return owner;
		}

		var matching = plan.Parts.FindIndex(part => part.Paths.Matches(path));
		owner = plan.Shared.Matches(path) ? SharedOwner
			: plan.Drop.Matches(path) || matching < 0 ? DropOwner
			: matching;
		return staticOwner[path] = owner;
	}

	/// <summary>The strict ancestors of a commit, as a bit per stream index.</summary>
	private BitArray AncestorsOf(int commit)
	{
		if (ancestors.TryGetValue(commit, out var set))
		{
			return set;
		}

		set = new BitArray(commits.Count);
		var pending = new Stack<int>(commits[commit].Parents);
		while (pending.Count > 0)
		{
			var next = pending.Pop();
			if (set[next])
			{
				continue;
			}

			set[next] = true;
			foreach (var parent in commits[next].Parents)
			{
				pending.Push(parent);
			}
		}

		return ancestors[commit] = set;
	}

	private sealed class Claim
	{
		public bool Always { get; set; }
		public List<int> UpTo { get; } = [];
	}
}
