namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: a team moves its git repositories into the monorepo - each under its own top-level folder, as one
/// baseline commit or with its first-parent history - and checks that what Bassia holds is exactly what git held.
/// Local repositories made on the fly, and real GitHub repositories when the network allows.
/// </summary>
[Trait("Scenario", "ImportRepo")]
public sealed class GitImportScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	/// <summary>A small repository with history: renames, deletions, a binary file, nested folders.</summary>
	private async Task<GitRepo> MakeRepositoryAsync(string name, int commits = 4)
	{
		var repo = await GitRepo.InitAsync(Path.Combine(Folder, "git", name));
		await repo.CommitAsync("initial commit", new Dictionary<string, string?> { ["README.md"] = $"# {name}\n", ["src/main.c"] = "int main() { return 0; }\n" },
			"Ada Lovelace <ada@example.com>");
		if (commits >= 2)
		{
			await repo.CommitAsync("add docs", new Dictionary<string, string?> { ["docs/guide.md"] = "guide\n", ["docs/deep/er/note.txt"] = "deep\n" });
		}

		if (commits >= 3)
		{
			File.WriteAllBytes(Path.Combine(repo.Path, "logo.bin"), [0, 159, 146, 150, 0, 255, 10, 13, 10]);
			await repo.CommitAsync("add a binary logo, rename main", new Dictionary<string, string?> { ["src/main.c"] = null, ["src/app.c"] = "int main() { return 1; }\n" });
		}

		for (var i = 4; i <= commits; i++)
		{
			await repo.CommitAsync($"change {i}", new Dictionary<string, string?> { ["CHANGELOG.md"] = string.Concat(Enumerable.Range(4, i - 3).Select(n => $"- change {n}\n")) });
		}

		return repo;
	}

	/// <summary>Mainline's files under a folder, as a workspace folder shows them, by git blob id.</summary>
	private static async Task<SortedDictionary<string, string>> MainlineFilesAsync(ScenarioClient client, string folder)
	{
		var name = "inspect-" + Guid.NewGuid().ToString("N")[..6];
		await client.OkAsync("workspace", "create", name, "-context", folder + "/");
		var files = GitBlobs.OfFolder(client.WorkspacePath(name), folder);
		await client.OkAsync("workspace", "close", name, "-abandon", "-remove");
		return files;
	}

	[ScenarioFact(Needs.Git)]
	public async Task ABaselineImport_HoldsExactlyTheFilesOfTheDefaultBranch()
	{
		var alice = await CloneAsync();
		var repo = await MakeRepositoryAsync("service");

		var imported = await alice.OkAsync("import", "-git", repo.Path);
		var report = Assert.Single(imported.Tables("repository"));
		Assert.Equal("service", report.Str("prefix"));
		Assert.Equal(1L, report.Long("commits"));
		Assert.Equal(1L, await alice.MainSeqAsync());

		var expected = await GitBlobs.OfCommitAsync(repo);
		Assert.Equal(expected.Count, report.Long("files"));
		Assert.Equal(expected, await MainlineFilesAsync(alice, "service"));
	}

	[ScenarioFact(Needs.Git)]
	public async Task AHistoryImport_LandsEveryFirstParentCommit_AsItsOwnSeq_WithItsMessageAndAuthor()
	{
		var alice = await CloneAsync();
		var repo = await MakeRepositoryAsync("history", commits: 6);

		var imported = await alice.OkAsync("import", "-git", repo.Path, "-history");
		var commits = await repo.FirstParentCountAsync();
		Assert.Equal(commits, Assert.Single(imported.Tables("repository")).Long("commits"));
		Assert.Equal(commits, await alice.MainSeqAsync());

		var landings = (await alice.OkAsync("mainline", "log")).Tables("landing").Where(landing => landing.Long("seq") > 0).OrderBy(landing => landing.Long("seq")).ToList();
		var subjects = (await repo.GitAsync("log", "--first-parent", "--reverse", "--format=%s")).Split('\n');
		Assert.Equal(subjects, landings.Select(landing => landing.Str("intent").Split('\n')[0].Trim()));
		Assert.Contains("Ada Lovelace", landings[0].Str("author"));

		// Each seq holds the tree of its commit.
		var revisions = (await repo.GitAsync("rev-list", "--first-parent", "--reverse", "HEAD")).Split('\n');
		Assert.Equal("int main() { return 0; }\n", await alice.CatAsync("history/src/main.c", 1));
		await alice.FailAsync("mainline", "cat", "history/src/main.c");
		Assert.Equal(await GitBlobs.OfCommitAsync(repo, revisions[^1]), await MainlineFilesAsync(alice, "history"));
	}

	[ScenarioFact(Needs.Git)]
	public async Task HistoryLimit_ImportsOnlyTheNewestCommits()
	{
		var alice = await CloneAsync();
		var repo = await MakeRepositoryAsync("limited", commits: 6);
		await alice.OkAsync("import", "-git", repo.Path, "-history", "-limit", "2");
		Assert.Equal(2L, await alice.MainSeqAsync());
		Assert.Equal(await GitBlobs.OfCommitAsync(repo), await MainlineFilesAsync(alice, "limited"));
	}

	[ScenarioFact(Needs.Git)]
	public async Task SeveralRepositories_EachGetTheirOwnFolder_UnderTheirNameOrAPrefix()
	{
		var alice = await CloneAsync();
		var api = await MakeRepositoryAsync("api", commits: 2);
		var web = await MakeRepositoryAsync("web", commits: 3);

		var imported = await alice.OkAsync("import", "-git", $"{api.Path},{web.Path}", "-prefix", "services/api,apps/web");
		Assert.Equal(["services/api", "apps/web"], imported.Tables("repository").Select(report => report.Str("prefix")));
		Assert.Equal(await GitBlobs.OfCommitAsync(api), await MainlineFilesAsync(alice, "services/api"));
		Assert.Equal(await GitBlobs.OfCommitAsync(web), await MainlineFilesAsync(alice, "apps/web"));
	}

	[ScenarioFact(Needs.Git)]
	public async Task ImportedCode_IsWorkedOnLikeAnyOther()
	{
		var alice = await CloneAsync();
		var repo = await MakeRepositoryAsync("lib", commits: 2);
		await alice.OkAsync("import", "-git", repo.Path);

		await alice.OkAsync("workspace", "create", "fix");
		alice.Write("fix", "lib/README.md", "# lib, fixed\n");
		Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "fix")).Str("outcome"));
		Assert.Equal("# lib, fixed\n", await alice.CatAsync("lib/README.md"));
	}

	[ScenarioFact(Needs.Git)]
	public async Task ARepositoryWithASubmodule_IsImported_AndTheSubmoduleSkippedAndListed()
	{
		var alice = await CloneAsync();
		var inner = await MakeRepositoryAsync("inner", commits: 1);
		var outer = await MakeRepositoryAsync("outer", commits: 1);
		await outer.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "-q", inner.Path, "vendor/inner");
		await outer.GitAsync("commit", "-q", "-m", "add a submodule");

		var imported = await alice.OkAsync("import", "-git", outer.Path);
		var report = Assert.Single(imported.Tables("repository"));
		Assert.Contains(report.Strings("skipped"), skipped => skipped.Contains("vendor/inner"));
		Assert.Equal($"# outer\n", await alice.CatAsync("outer/README.md"));
	}

	[ScenarioFact(Needs.Network)]
	public async Task RealGitHubRepositories_ImportAsTheirDefaultBranch_FileForFile()
	{
		var alice = await CloneAsync();
		var urls = ScenarioEnvironment.Repositories;

		var imported = await alice.OkAsync("import", "-git", string.Join(',', urls));
		var reports = imported.Tables("repository");
		Assert.Equal(urls.Count, reports.Count);
		for (var i = 0; i < urls.Count; i++)
		{
			var prefix = reports[i].Str("prefix");
			var reference = await GitRepo.CloneAsync(urls[i], Path.Combine(Folder, "reference", prefix.Replace('/', '_')));
			var expected = await GitBlobs.OfCommitAsync(reference);
			Note($"{urls[i]}: {expected.Count} files at {await reference.GitAsync("rev-parse", "--short", "HEAD")}");
			Assert.Equal(expected, await MainlineFilesAsync(alice, prefix));
		}
	}

	[ScenarioFact(Needs.Network)]
	public async Task ARealGitHubRepository_ImportsWithHistory_AndExportsBackToTheSameTree()
	{
		var alice = await CloneAsync();
		var url = ScenarioEnvironment.Repositories[^1];
		const int limit = 25;
		var reference = await GitRepo.CloneAsync(url, Path.Combine(Folder, "reference"));
		var commits = Math.Min(limit, await reference.FirstParentCountAsync());

		var imported = await alice.OkAsync("import", "-git", url, "-history", "-limit", limit.ToString(), "-prefix", "repo");
		Assert.Equal(commits, Assert.Single(imported.Tables("repository")).Long("commits"));
		Assert.Equal(commits, await alice.MainSeqAsync());

		var exportDir = Path.Combine(Folder, "exported");
		await alice.OkAsync("export", "-git", exportDir);
		var exported = new GitRepo(exportDir);
		var expected = (await GitBlobs.OfCommitAsync(reference)).ToDictionary(entry => "repo/" + entry.Key, entry => entry.Value);
		Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.Ordinal), (await GitBlobs.OfCommitAsync(exported, "main")).OrderBy(e => e.Key, StringComparer.Ordinal));
	}
}
