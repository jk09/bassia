namespace Bassia.Tests;

using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>The path index's batched lookup and ChangedSince over history, the same on every store.</summary>
internal static class PathIndexContract
{
	public static async Task BatchedLookupAndChangedSinceAsync(IPathIndex paths)
	{
		var one = new Leaf(EntryKind.File, ObjectId.Of([1]), 1);
		var two = new Leaf(EntryKind.File, ObjectId.Of([2]), 2);
		// A history: src/a.cs changes at 1, 3 and 5 (deleted at 5); src/b.cs only at 1; src-x/c.cs at 2 and 4; lib/d.cs at 4.
		await paths.ApplyAsync(1, [new PathChange("src/a.cs", one), new PathChange("src/b.cs", one)]);
		await paths.ApplyAsync(2, [new PathChange("src-x/c.cs", one)]);
		await paths.ApplyAsync(3, [new PathChange("src/a.cs", two)]);
		await paths.ApplyAsync(4, [new PathChange("src-x/c.cs", two), new PathChange("lib/d.cs", two)]);
		await paths.ApplyAsync(5, [new PathChange("src/a.cs", null)]);

		string[] asked = ["src/a.cs", "missing.txt", "src/b.cs", "src/a.cs", "lib/d.cs", "src-x/c.cs"];
		foreach (var seq in new long[] { 0, 1, 2, 3, 4, 5, 9 })
		{
			var batched = await paths.GetManyAsync(asked, seq);
			Assert.Equal(asked.Length, batched.Count);
			for (var index = 0; index < asked.Length; index++)
			{
				Assert.Equal(await paths.GetAsync(asked[index], seq), batched[index]);
			}
		}

		var atThree = await paths.GetManyAsync(asked, 3);
		Assert.Equal(3, atThree[0]!.Seq);
		Assert.Equal(two, atThree[0]!.Leaf);
		Assert.Null(atThree[1]);
		Assert.Null((await paths.GetManyAsync(["src/a.cs"], 5))[0]!.Leaf);
		Assert.Empty(await paths.GetManyAsync([], 5));

		// The latest version of each path changed after the seq; older history under the prefix does not matter.
		Assert.Equal(["src/a.cs@5"], await Changed(paths, "src", 3));
		Assert.Equal(["src/a.cs@5"], await Changed(paths, "src", 1));
		Assert.Equal(["src/a.cs@5", "src/b.cs@1"], await Changed(paths, "src", 0));
		Assert.Equal(["lib/d.cs@4", "src-x/c.cs@4", "src/a.cs@5"], await Changed(paths, "", 3));
		Assert.Equal(["src-x/c.cs@4"], await Changed(paths, "src-x", 2));
		Assert.Equal(["src/a.cs@5"], await Changed(paths, "src/a.cs", 0));
		Assert.Empty(await Changed(paths, "src", 5));
	}

	private static async Task<List<string>> Changed(IPathIndex paths, string prefix, long after) =>
		(await paths.ChangedSinceAsync(prefix, after).ToListAsync()).Select(version => $"{version.Path}@{version.Seq}").Order(StringComparer.Ordinal).ToList();

	/// <summary>Many paths in one call: the batching across the store's batch size keeps order.</summary>
	public static async Task ManyPathsAsync(IPathIndex paths, int count)
	{
		var leaf = new Leaf(EntryKind.File, ObjectId.Of([7]), 7);
		var changes = Enumerable.Range(0, count).Select(index => new PathChange($"d{index % 97}/f{index}.txt", leaf)).ToList();
		await paths.ApplyAsync(1, changes);
		await paths.ApplyAsync(2, changes.Where((_, index) => index % 3 == 0).Select(change => change with { Leaf = null }).ToList());
		var asked = changes.Select(change => change.Path).Append("absent").Reverse().ToList();
		var versions = await paths.GetManyAsync(asked, 2);
		Assert.Null(versions[0]);
		for (var index = 1; index < asked.Count; index++)
		{
			var number = int.Parse(asked[index].Split('/')[1][1..^4]);
			Assert.Equal(asked[index], versions[index]!.Path);
			Assert.Equal(number % 3 == 0 ? 2 : 1, versions[index]!.Seq);
		}
	}
}
