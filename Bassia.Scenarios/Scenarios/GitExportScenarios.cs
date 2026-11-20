namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: mainline is published as a git repository - one git commit per landing, in seq order, with the intent as
/// message and Change-Id / Bassia-Seq trailers - and later exports continue where the last one stopped.
/// </summary>
[Trait("Scenario", "GitExport")]
public sealed class GitExportScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	[ScenarioFact(Needs.Git)]
	public async Task Export_WritesOneGitCommitPerLanding_WithTrailers_AndMainlinesTree()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("one", "first change", new Dictionary<string, string?> { ["a.txt"] = "a\n", ["dir/b.txt"] = "b\n" });
		await alice.LandFilesAsync("two", "second change", new Dictionary<string, string?> { ["a.txt"] = "a2\n", ["dir/b.txt"] = null });
		var changeId = (await alice.OkAsync("mainline", "show", "-seq", "2")).Str("change_id");

		var target = Path.Combine(Folder, "export");
		var exported = await alice.OkAsync("export", "-git", target);
		Assert.Equal(2L, exported.Long("to_seq"));
		var git = new GitRepo(target);

		var subjects = (await git.GitAsync("log", "--reverse", "--format=%s", "main")).Split('\n');
		Assert.Equal(["first change", "second change"], subjects[^2..]);
		var head = await git.GitAsync("log", "-1", "--format=%B", "main");
		Assert.Contains($"Change-Id: {changeId}", head);
		Assert.Contains("Bassia-Seq: 2", head);
		Assert.Equal(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["a.txt"] = GitBlobs.BlobId("a2\n"u8.ToArray()) }, await GitBlobs.OfCommitAsync(git, "main"));
	}

	[ScenarioFact(Needs.Git)]
	public async Task ASecondExport_ContinuesFromTheLastExportedSeq()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("one", "one", new Dictionary<string, string?> { ["a.txt"] = "1\n" });
		var target = Path.Combine(Folder, "export");
		await alice.OkAsync("export", "-git", target);
		var git = new GitRepo(target);
		var before = int.Parse(await git.GitAsync("rev-list", "--count", "main"));

		await alice.LandFilesAsync("two", "two", new Dictionary<string, string?> { ["a.txt"] = "2\n" });
		await alice.LandFilesAsync("three", "three", new Dictionary<string, string?> { ["b.txt"] = "3\n" });
		var again = await alice.OkAsync("export", "-git", target);
		Assert.Equal(1L, again.Long("from_seq"));
		Assert.Equal(3L, again.Long("to_seq"));
		Assert.Equal(2L, again.Long("commits"));
		Assert.Equal(before + 2, int.Parse(await git.GitAsync("rev-list", "--count", "main")));

		var nothing = await alice.OkAsync("export", "-git", target);
		Assert.Equal(0L, nothing.Long("commits"));
	}

	[ScenarioFact(Needs.Git)]
	public async Task ImportThenExport_RoundTripsAGitRepository_TreeForTree()
	{
		var alice = await CloneAsync();
		var source = await GitRepo.InitAsync(Path.Combine(Folder, "source"));
		await source.CommitAsync("one", new Dictionary<string, string?> { ["x/1.txt"] = "1\n" });
		await source.CommitAsync("two", new Dictionary<string, string?> { ["x/2.txt"] = "2\n" });
		await source.CommitAsync("three", new Dictionary<string, string?> { ["x/1.txt"] = null, ["y.txt"] = "y\n" });
		await alice.OkAsync("import", "-git", source.Path, "-history", "-prefix", "src");

		var target = Path.Combine(Folder, "export");
		await alice.OkAsync("export", "-git", target);
		var exported = new GitRepo(target);
		var revisions = (await source.GitAsync("rev-list", "--reverse", "HEAD")).Split('\n');
		var exportedRevisions = (await exported.GitAsync("rev-list", "--reverse", "main")).Split('\n')[^revisions.Length..];
		for (var i = 0; i < revisions.Length; i++)
		{
			var expected = (await GitBlobs.OfCommitAsync(source, revisions[i])).ToDictionary(entry => "src/" + entry.Key, entry => entry.Value);
			Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.Ordinal), (await GitBlobs.OfCommitAsync(exported, exportedRevisions[i])).OrderBy(e => e.Key, StringComparer.Ordinal));
		}
	}
}
