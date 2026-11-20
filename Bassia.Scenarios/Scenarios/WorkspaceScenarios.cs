namespace Bassia.Scenarios.Scenarios;

using Bassia.Scenarios.Harness;
using Xunit.Abstractions;

/// <summary>
/// Scenario: a person (or agent) works in a workspace instead of a branch - creates it, edits its folder with any tool,
/// and inspects, describes, undoes, restores, stacks, clones and closes it. Nobody stages or commits: every command that
/// reads a workspace snapshots its folder.
/// </summary>
[Trait("Scenario", "Workspaces")]
public sealed class WorkspaceScenarios(ITestOutputHelper output) : ScenarioTest(output)
{
	[ScenarioFact]
	public async Task ANewWorkspace_HoldsMainlinesNewestTree_InItsOwnFolder()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("seed", "seed", new Dictionary<string, string?> { ["src/app.txt"] = "app\n", ["README.md"] = "readme\n" });

		var created = await alice.OkAsync("workspace", "create", "feature", "-intent", "build the feature");
		Assert.Equal("feature", created.Str("name"));
		Assert.Equal("open", created.Str("status"));
		Assert.Equal(1L, created.Long("base_seq"));
		Assert.Equal("build the feature", created.Str("intent"));
		Assert.Equal(Path.GetFullPath(alice.WorkspacePath("feature")), Path.GetFullPath(created.Str("path")));
		Assert.Equal("app\n", alice.Read("feature", "src/app.txt"));
		Assert.Equal("readme\n", alice.Read("feature", "README.md"));

		var listed = await alice.OkAsync("workspace", "list");
		Assert.Contains(listed.Tables("workspace"), workspace => workspace.Str("name") == "feature" && workspace.Str("status") == "open");
	}

	[ScenarioFact]
	public async Task EditsInTheFolder_AreSnapshottedImplicitly_AndListedAsAddedModifiedDeletedRenamed()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("seed", "seed", new Dictionary<string, string?>
		{
			["keep.txt"] = "keep\n", ["edit.txt"] = "one\ntwo\nbefore\nfour\nfive\n", ["gone.txt"] = "gone\n", ["old-name.txt"] = "a file that will be renamed, long enough to be recognized\n"
		});
		await alice.OkAsync("workspace", "create", "edits");

		alice.Write("edits", "new.txt", "new\n");
		alice.Write("edits", "edit.txt", "one\ntwo\nafter\nfour\nfive\n");
		alice.Delete("edits", "gone.txt");
		File.Move(alice.PathIn("edits", "old-name.txt"), alice.PathIn("edits", "new-name.txt"));

		var status = await alice.OkAsync("workspace", "status", "edits");
		var changes = status.Tables("change").ToDictionary(change => change.Str("path"), change => change.Str("status"));
		Assert.Equal("added", changes["new.txt"]);
		Assert.Equal("modified", changes["edit.txt"]);
		Assert.Equal("deleted", changes["gone.txt"]);
		Assert.Equal("renamed", changes["new-name.txt"]);
		Assert.DoesNotContain("keep.txt", changes.Keys);

		// The same changes, as a diff with hunks.
		var diff = await alice.OkAsync("workspace", "diff", "edits", "-patch");
		Assert.Contains("+after", diff.Str("patch"));
		Assert.Contains("-before", diff.Str("patch"));
		var underPath = await alice.OkAsync("workspace", "diff", "edits", "-path", "edit.txt");
		Assert.All(underPath.Tables("change"), change => Assert.Equal("edit.txt", change.Str("path")));
	}

	[ScenarioFact(Needs.Unix)]
	public async Task OnlyTheExecutableBitChanged_IsAModeChange()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("seed", "seed", new Dictionary<string, string?> { ["run.sh"] = "#!/bin/sh\necho hi\n" });
		await alice.OkAsync("workspace", "create", "chmod");
		File.SetUnixFileMode(alice.PathIn("chmod", "run.sh"), File.GetUnixFileMode(alice.PathIn("chmod", "run.sh")) | UnixFileMode.UserExecute);

		var status = await alice.OkAsync("workspace", "status", "chmod");
		Assert.Equal("mode", Assert.Single(status.Tables("change")).Str("status"));
		await alice.OkAsync("workspace", "land", "chmod");

		await alice.OkAsync("workspace", "create", "reader");
		Assert.True(File.GetUnixFileMode(alice.PathIn("reader", "run.sh")).HasFlag(UnixFileMode.UserExecute));
	}

	[ScenarioFact]
	public async Task BuildAndVersionControlFolders_AreNeverSnapshotted_NorWhatBassiaTomlIgnores()
	{
		var alice = await CloneAsync();
		await alice.LandFilesAsync("policy", "ignore logs", new Dictionary<string, string?> { [".bassia.toml"] = "[ignore]\npaths = [\"*.log\"]\n" });
		await alice.OkAsync("workspace", "create", "build");
		alice.Write("build", "src/code.cs", "class C {}\n");
		alice.Write("build", "src/bin/Debug/code.dll", "binary");
		alice.Write("build", "src/obj/project.assets.json", "{}");
		alice.Write("build", "web/node_modules/left-pad/index.js", "module.exports = 1;\n");
		alice.Write("build", ".git/HEAD", "ref: refs/heads/main\n");
		alice.Write("build", "trace.log", "noise\n");

		var status = await alice.OkAsync("workspace", "status", "build");
		Assert.Equal(["src/code.cs"], status.Tables("change").Select(change => change.Str("path")));
	}

	[ScenarioFact]
	public async Task Describe_SetsTheIntent_ThatTheLandingRecords()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "docs");
		alice.Write("docs", "guide.md", "# Guide\n");
		await alice.OkAsync("workspace", "describe", "-workspace", "docs", "-intent", "write the guide");
		Assert.Equal("write the guide", (await alice.OkAsync("workspace", "show", "docs")).Str("intent"));

		var landed = await alice.OkAsync("workspace", "land", "docs");
		var shown = await alice.OkAsync("mainline", "show", "-seq", landed.Long("seq").ToString());
		Assert.Equal("write the guide", shown.Str("intent"));
	}

	[ScenarioFact]
	public async Task Undo_RevertsTheLastOperation_AndTheFolderFollows_UndoAgainRedoes()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "undo");
		alice.Write("undo", "a.txt", "one\n");
		await alice.OkAsync("workspace", "snapshot", "undo");
		alice.Write("undo", "a.txt", "two\n");
		await alice.OkAsync("workspace", "snapshot", "undo");

		await alice.OkAsync("workspace", "undo", "undo");
		Assert.Equal("one\n", alice.Read("undo", "a.txt"));

		await alice.OkAsync("workspace", "undo", "undo");
		Assert.Equal("two\n", alice.Read("undo", "a.txt"));
	}

	[ScenarioFact]
	public async Task Restore_ReturnsToAnyOperationOfTheLog()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "history");
		foreach (var version in new[] { "v1", "v2", "v3" })
		{
			alice.Write("history", "file.txt", version + "\n");
			await alice.OkAsync("workspace", "snapshot", "history");
		}

		var log = await alice.OkAsync("workspace", "log", "history");
		var operations = log.Tables("operation");
		Assert.True(operations.Count >= 4, "create plus three snapshots");
		var firstSnapshot = operations.Single(operation => operation.Long("seq") == 1);

		await alice.OkAsync("workspace", "restore", "history", "-op", firstSnapshot.Str("id")[..8]);
		Assert.Equal("v1\n", alice.Read("history", "file.txt"));

		// The restore is itself an operation: the log only grows.
		Assert.True((await alice.OkAsync("workspace", "log", "history")).Tables("operation").Count > operations.Count);
	}

	[ScenarioFact]
	public async Task WorkspaceNames_AreUniqueAmongOpenWorkspaces_AndFreedWhenClosed()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "dup");
		await alice.FailAsync("workspace", "create", "dup", "-path", Path.Combine(Folder, "elsewhere"));

		await alice.OkAsync("workspace", "close", "dup", "-abandon", "-remove");
		Assert.False(Directory.Exists(alice.WorkspacePath("dup")));
		await alice.OkAsync("workspace", "create", "dup");
	}

	[ScenarioFact]
	public async Task ClosedAndAbandonedWorkspaces_LeaveTheOpenList_ButStayListedWithAll()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "closed-one");
		await alice.OkAsync("workspace", "create", "abandoned-one");
		await alice.OkAsync("workspace", "close", "closed-one");
		await alice.OkAsync("workspace", "close", "abandoned-one", "-abandon");

		Assert.Empty((await alice.OkAsync("workspace", "list")).Tables("workspace"));
		var all = (await alice.OkAsync("workspace", "list", "-all")).Tables("workspace").ToDictionary(w => w.Str("name"), w => w.Str("status"));
		Assert.Equal("closed", all["closed-one"]);
		Assert.Equal("abandoned", all["abandoned-one"]);
	}

	[ScenarioFact]
	public async Task AWorkspace_IsNamedByNameIdOrUniquePrefix_AndFromInsideItsFolder()
	{
		var alice = await CloneAsync();
		var created = await alice.OkAsync("workspace", "create", "refer-to-me");
		var id = created.Str("id");

		Assert.Equal("refer-to-me", (await alice.OkAsync("workspace", "show", id)).Str("name"));
		Assert.Equal("refer-to-me", (await alice.OkAsync("workspace", "show", "refer-to")).Str("name"));
		Assert.Equal("refer-to-me", (await alice.OkInAsync(alice.WorkspacePath("refer-to-me"), "workspace", "show")).Str("name"));
	}

	[ScenarioFact]
	public async Task AStackedWorkspace_StartsFromItsParentsWork_AndLandsAfterIt()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "parent");
		alice.Write("parent", "api.txt", "api v2\n");

		var stacked = await alice.OkAsync("workspace", "create", "child", "-on", "parent");
		Assert.Equal("api v2\n", alice.Read("child", "api.txt"));
		Assert.False(string.IsNullOrEmpty(stacked.StrOrNull("parent") ?? stacked.StrOrNull("on")));
		alice.Write("child", "client.txt", "uses api v2\n");

		// The child cannot land before its parent.
		var early = await alice.OkAsync("workspace", "check", "child");
		Assert.Equal("rejected", early.Str("outcome"));
		Assert.False(early.Bool("can_land"));

		Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "parent")).Str("outcome"));
		await alice.OkAsync("workspace", "sync", "child");
		var landed = await alice.OkAsync("workspace", "land", "child");
		Assert.Equal("landed", landed.Str("outcome"));
		Assert.Equal("uses api v2\n", await alice.CatAsync("client.txt"));
		Assert.Equal("api v2\n", await alice.CatAsync("api.txt"));
	}

	[ScenarioFact]
	public async Task CloningAWorkspace_BranchesOffItsWork_AsANewChange()
	{
		var alice = await CloneAsync();
		var original = await alice.OkAsync("workspace", "create", "original");
		alice.Write("original", "idea.txt", "first idea\n");

		var cloned = await alice.OkAsync("workspace", "clone", "-from", "original", "-name", "variant");
		Assert.NotEqual(original.Str("change_id"), cloned.Str("change_id"));
		Assert.Equal("first idea\n", alice.Read("variant", "idea.txt"));

		alice.Write("variant", "idea.txt", "better idea\n");
		Assert.Equal("first idea\n", alice.Read("original", "idea.txt"));
		Assert.Equal("landed", (await alice.OkAsync("workspace", "land", "variant")).Str("outcome"));
		await alice.OkAsync("workspace", "close", "original", "-abandon");
		Assert.Equal("better idea\n", await alice.CatAsync("idea.txt"));
	}

	[ScenarioFact]
	public async Task Checkout_GivesAWorkspaceAFolderAgain_AfterItWasRemoved()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "portable");
		alice.Write("portable", "work.txt", "in progress\n");
		await alice.OkAsync("workspace", "snapshot", "portable");
		ScenarioTest.DeleteFolder(alice.WorkspacePath("portable"));

		var target = Path.Combine(Folder, "portable-again");
		await alice.OkAsync("workspace", "checkout", "portable", "-path", target);
		Assert.Equal("in progress\n", File.ReadAllText(Path.Combine(target, "work.txt")));
	}

	[ScenarioFact(Needs.Unix)]
	public async Task TwoPathsDifferingOnlyByCase_AreRefused()
	{
		var alice = await CloneAsync();
		await alice.OkAsync("workspace", "create", "case");
		alice.Write("case", "Readme.md", "a\n");
		alice.Write("case", "README.md", "b\n");

		var refused = await alice.FailAsync("workspace", "status", "case");
		Assert.Contains("README.md", refused.Str("error"), StringComparison.OrdinalIgnoreCase);
	}
}
