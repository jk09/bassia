namespace Bassia.Tests.Core;

using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Text;
using Bassia.Core.Version;

/// <summary>Property tests over the tree algebra: random trees and random edits, many seeds.</summary>
public class TreeAlgebraTests
{
	private static readonly string[] Names = ["a", "b", "c", "src", "docs", "x.txt", "y.md"];

	private static Dictionary<string, string> RandomFiles(Random random, int count)
	{
		var files = new Dictionary<string, string>(StringComparer.Ordinal);
		while (files.Count < count)
		{
			var depth = random.Next(1, 4);
			var path = string.Join('/', Enumerable.Range(0, depth).Select(_ => Names[random.Next(Names.Length)]));
			// Keep it a valid tree: no path may be a prefix directory of another file.
			if (files.Keys.Any(other => other.StartsWith(path + "/", StringComparison.Ordinal) || path.StartsWith(other + "/", StringComparison.Ordinal)))
			{
				continue;
			}

			files[path] = $"content {random.Next(5)}\n";
		}

		return files;
	}

	private static Dictionary<string, string?> RandomEdits(Random random, IReadOnlyDictionary<string, string> files)
	{
		var edits = new Dictionary<string, string?>(StringComparer.Ordinal);
		foreach (var path in files.Keys.Where(_ => random.Next(3) == 0))
		{
			edits[path] = random.Next(2) == 0 ? null : $"edited {random.Next(100)}\n";
		}

		var all = files.Keys.ToHashSet();
		for (var index = 0; index < random.Next(3); index++)
		{
			var path = "new/" + Names[random.Next(Names.Length)] + random.Next(10);
			if (!all.Any(other => other.StartsWith(path + "/", StringComparison.Ordinal) || path.StartsWith(other + "/", StringComparison.Ordinal)))
			{
				edits[path] = "new\n";
			}
		}

		return edits;
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	public async Task DiffThenApply_ReproducesTheNewTree_AndIdsAreDeterministic(int seed)
	{
		var random = new Random(seed);
		var repo = new TestRepo();
		for (var round = 0; round < 20; round++)
		{
			var files = RandomFiles(random, random.Next(0, 12));
			var oldTree = await repo.TreeAsync(files);
			Assert.Equal(oldTree, await repo.TreeAsync(files.Reverse().ToDictionary()));
			var edits = RandomEdits(random, files);
			var newTree = await repo.TreeAsync(edits!, oldTree);

			var diff = await repo.Repo.Trees.DiffAsync(oldTree, newTree);
			var applied = await repo.Repo.Trees.EditAsync(oldTree, diff.Select(change => new PathChange(change.Path, change.New)));
			Assert.Equal(newTree, applied);

			var expected = new Dictionary<string, string>(files);
			foreach (var (path, text) in edits)
			{
				if (text is null)
				{
					expected.Remove(path);
				}
				else
				{
					expected[path] = text;
				}
			}

			Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), (await repo.FilesAsync(newTree)).OrderBy(pair => pair.Key, StringComparer.Ordinal));
			Assert.Empty(await repo.Repo.Trees.DiffAsync(newTree, applied));
			Assert.Equal(diff.Count, (await repo.Repo.Trees.DiffAsync(newTree, oldTree)).Count);
		}
	}

	[Theory]
	[InlineData(11)]
	[InlineData(12)]
	[InlineData(13)]
	public async Task Merge_IdentitiesHold(int seed)
	{
		var random = new Random(seed);
		var repo = new TestRepo();
		static Task<MergeResolution> None(string path, Leaf? b, Leaf? o, Leaf? t) => Task.FromResult(MergeResolution.Unresolved);
		for (var round = 0; round < 15; round++)
		{
			var files = RandomFiles(random, random.Next(1, 10));
			var baseTree = await repo.TreeAsync(files);
			var ours = await repo.TreeAsync(RandomEdits(random, files)!, baseTree);
			var theirs = await repo.TreeAsync(RandomEdits(random, files)!, baseTree);

			Assert.Equal(theirs, (await repo.Repo.Trees.MergeAsync(baseTree, baseTree, theirs, None)).Tree);
			Assert.Equal(ours, (await repo.Repo.Trees.MergeAsync(baseTree, ours, baseTree, None)).Tree);
			Assert.Equal(ours, (await repo.Repo.Trees.MergeAsync(baseTree, ours, ours, None)).Tree);

			// Merging is symmetric in which paths conflict.
			var forward = await repo.Repo.Trees.MergeAsync(baseTree, ours, theirs, None);
			var backward = await repo.Repo.Trees.MergeAsync(baseTree, theirs, ours, None);
			Assert.Equal(forward.Conflicts.OrderBy(p => p), backward.Conflicts.OrderBy(p => p));

			// Paths only one side changed come from that side.
			var merged = await repo.FilesAsync(forward.Tree);
			var oursFiles = await repo.FilesAsync(ours);
			var theirsFiles = await repo.FilesAsync(theirs);
			foreach (var (path, text) in merged.Where(pair => pair.Value != "<conflict>"))
			{
				Assert.True(oursFiles.GetValueOrDefault(path) == text || theirsFiles.GetValueOrDefault(path) == text);
			}
		}
	}

	[Fact]
	public async Task Diff_PrunesEqualSubtrees()
	{
		var counting = new CountingObjectStore(new MemoryObjectStore());
		var repo = new TestRepo(counting);
		var files = Enumerable.Range(0, 200).ToDictionary(index => $"dir{index % 20}/sub/file{index}.txt", index => $"{index}\n");
		var oldTree = await repo.TreeAsync(files);
		var newTree = await repo.TreeAsync(new Dictionary<string, string> { ["dir3/sub/file3.txt"] = "changed\n" }, oldTree);
		var fresh = new Repository(counting, new MemoryViewStore(), new MemoryPathIndex(), new MemoryLeaseStore(), TestRepo.SmallChunks);
		var before = counting.Reads;
		var diff = await fresh.Trees.DiffAsync(oldTree, newTree);
		Assert.Single(diff);
		Assert.InRange(counting.Reads - before, 1, 8);
	}

	[Fact]
	public async Task Edit_RefusesAFileUnderAFile_AndReplacesAFileByADirectory()
	{
		var repo = new TestRepo();
		var tree = await repo.TreeAsync(new Dictionary<string, string> { ["a"] = "file\n" });
		var leaf = await repo.FileAsync("x");
		await Assert.ThrowsAsync<PathClashException>(() => repo.Repo.Trees.EditAsync(tree, [new PathChange("a/b", leaf)]));
		var replaced = await repo.Repo.Trees.EditAsync(tree, [new PathChange("a", null), new PathChange("a/b", leaf)]);
		Assert.Equal(["a/b"], (await repo.FilesAsync(replaced)).Keys);
		Assert.Equal(tree, await repo.Repo.Trees.EditAsync(tree, [new PathChange("missing/x", null)]));
	}

	[Fact]
	public async Task Diffs_DetectRenamesByIdAndChunkOverlap_AndMarkRewrites()
	{
		var repo = new TestRepo();
		var big = string.Join("\n", Enumerable.Range(0, 400).Select(index => $"line {index} of the big file"));
		var baseTree = await repo.TreeAsync(new Dictionary<string, string> { ["a.txt"] = "same\n", ["big.txt"] = big, ["notes.md"] = "one\ntwo\nthree\nfour\n" });
		var newTree = await repo.TreeAsync(new Dictionary<string, string?>
		{
			["a.txt"] = null, ["b.txt"] = "same\n",
			["big.txt"] = null, ["moved/big.txt"] = big + "\nappended",
			["notes.md"] = "completely\ndifferent\ncontent\nnow\n"
		}!, baseTree);
		var records = await repo.Repo.Diffs.DiffAsync(baseTree, newTree);
		Assert.Contains(records, record => record is { Path: "b.txt", Status: ChangeStatus.Renamed, OldPath: "a.txt" });
		Assert.Contains(records, record => record is { Path: "moved/big.txt", Status: ChangeStatus.Renamed, OldPath: "big.txt" });
		var notes = records.Single(record => record.Path == "notes.md");
		var patch = await repo.Repo.Diffs.PatchAsync(notes);
		Assert.True(patch.Rewritten);
		Assert.Null(patch.Hunks);

		var small = await repo.Repo.Diffs.PatchAsync(new DiffRecord("x", ChangeStatus.Modified,
			await repo.FileAsync("a\nb\nc\nd\ne\n"), await repo.FileAsync("a\nb\nC\nd\ne\n")));
		Assert.False(small.Rewritten);
		Assert.Contains("-c\n+C\n", small.Hunks);
		Assert.Equal((1, 1), (small.Added, small.Deleted));
	}

	[Fact]
	public void TextMerge_MergesDisjointEditsAndMarksOverlaps()
	{
		var baseLines = LineDiff.Lines("a\nb\nc\nd\ne\n");
		var clean = TextMerge.Merge(baseLines, LineDiff.Lines("A\nb\nc\nd\ne\n"), LineDiff.Lines("a\nb\nc\nd\nE\n"));
		Assert.True(clean.Clean);
		Assert.Equal("A\nb\nc\nd\nE\n", clean.Text);

		var conflict = TextMerge.Merge(baseLines, LineDiff.Lines("a\nB1\nc\nd\ne\n"), LineDiff.Lines("a\nB2\nc\nd\ne\n"));
		Assert.False(conflict.Clean);
		Assert.Contains("<<<<<<< workspace", conflict.Text);

		var same = TextMerge.Merge(baseLines, LineDiff.Lines("a\nX\nc\nd\ne\n"), LineDiff.Lines("a\nX\nc\nd\ne\n"));
		Assert.True(same.Clean);
	}

	[Fact]
	public void Unified_ShowsHunksWithContext()
	{
		var a = LineDiff.Lines(string.Join("\n", Enumerable.Range(1, 20)) + "\n");
		var b = a.ToArray();
		b[1] = "two";
		b[17] = "eighteen";
		var text = LineDiff.Unified("a/x", "b/x", a, b, 2);
		Assert.Equal(2, text.Split("@@ -").Length - 1);
		Assert.Contains("-2\n+two\n", text);
		Assert.Contains("-18\n+eighteen\n", text);
	}
}
