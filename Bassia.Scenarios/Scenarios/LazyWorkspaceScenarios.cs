namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: an agent works on a large monorepo without downloading it - a lazy workspace folder lists every file in
/// context as a placeholder and fetches a file's content when it is first read or changed (FUSE on Linux, ProjFS on
/// Windows). Edits are snapshotted and land like any others.
/// </summary>
[Trait("Scenario", "LazyFolders")]
public sealed class LazyWorkspaceScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	[ScenarioFact(Needs.Lazy)]
	public async Task ALazyFolder_ListsEveryFile_ReadsThemOnDemand_AndLandsEdits()
	{
		var alice = await CloneAsync();
		var files = Enumerable.Range(1, 20).ToDictionary(i => $"big/part{i:D2}.txt", i => (string?)$"part {i}\n");
		files["README.md"] = "readme\n";
		await alice.LandFilesAsync("seed", "seed", files);

		var created = await alice.OkAsync("workspace", "create", "lazy", "-materialize", "lazy");
		Assert.Equal("lazy", created.Str("materialize"));
		try
		{
			// Every file is listed with its size; reading one fetches it.
			Assert.Equal(20, Directory.GetFiles(alice.PathIn("lazy", "big")).Length);
			Assert.Equal("part 7\n", alice.Read("lazy", "big/part07.txt"));

			alice.Write("lazy", "big/part03.txt", "part 3, edited\n");
			alice.Write("lazy", "new.txt", "new\n");
			var status = await alice.OkAsync("workspace", "status", "lazy");
			Assert.Equal(["big/part03.txt", "new.txt"], status.Tables("change").Select(change => change.Str("path")).Order(StringComparer.Ordinal));

			Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "lazy", "-keep")).Str("outcome"));
			Assert.Equal("part 3, edited\n", await alice.CatAsync("big/part03.txt"));
			Assert.Equal("part 4\n", await alice.CatAsync("big/part04.txt"));
		}
		finally
		{
			await alice.RunAsync("workspace", "unmount", "lazy");
		}
	}
}
