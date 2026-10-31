namespace Bassia.Tests.Core;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

public class LandingTests
{
	private static Dictionary<string, string?> Files(params (string Path, string? Text)[] files) => files.ToDictionary(file => file.Path, file => file.Text);

	[Fact]
	public async Task DisjointWorkspaces_BothLand_WithLinearSeqsAndTheirChangeIds()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(Files(("src/a.cs", "a\n"), ("src/b.cs", "b\n")));
		var one = await repo.WorkspaceAsync("one", Files(("src/a.cs", "a1\n")));
		var two = await repo.WorkspaceAsync("two", Files(("src/b.cs", "b2\n"), ("docs/new.md", "new\n")));

		var first = await repo.LandAsync(one);
		var second = await repo.LandAsync(two);

		Assert.Equal((LandOutcome.Landed, 2L), (first.Outcome, first.Seq!.Value));
		Assert.Equal((LandOutcome.Landed, 3L), (second.Outcome, second.Seq!.Value));
		Assert.Equal(new Dictionary<string, string> { ["src/a.cs"] = "a1\n", ["src/b.cs"] = "b2\n", ["docs/new.md"] = "new\n" }.OrderBy(p => p.Key),
			(await repo.MainFilesAsync()).OrderBy(p => p.Key));

		var landed = await repo.Repo.Objects.ReadCommitAsync(second.Commit!.Value);
		var working = await repo.Repo.Objects.ReadCommitAsync(two.Record.Working);
		Assert.Equal(working.ChangeId, landed.ChangeId);
		Assert.NotEqual(two.Record.Working, second.Commit.Value);
		Assert.Equal([first.Commit!.Value], landed.Parents);
		Assert.Equal(WorkspaceStatus.Landed, (await repo.Repo.FindWorkspaceAsync(two.Id))!.Record.Status);

		var log = await repo.Repo.LogAsync(HeadName.Main).ToListAsync();
		Assert.Equal([3L, 2L, 1L, 0L], log.Select(entry => entry.Operation.Seq));
	}

	[Fact]
	public async Task BothRewroteAPath_NeedsReplay_AndTheWorkspaceRecordsIt()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(Files(("src/a.cs", "a\n")));
		var one = await repo.WorkspaceAsync("one", Files(("src/a.cs", "one\n")));
		var two = await repo.WorkspaceAsync("two", Files(("src/a.cs", "two\n")));
		Assert.Equal(LandOutcome.Landed, (await repo.LandAsync(one)).Outcome);

		var check = await repo.LandAsync(two, dryRun: true);
		Assert.Equal(LandOutcome.NeedsReplay, check.Outcome);
		var result = await repo.LandAsync(two);
		Assert.Equal(LandOutcome.NeedsReplay, result.Outcome);
		Assert.Equal("src/a.cs", Assert.Single(result.Conflicts).Path);
		var record = (await repo.Repo.FindWorkspaceAsync(two.Id))!.Record;
		Assert.Equal(WorkspaceStatus.NeedsReplay, record.Status);
		Assert.Equal(["src/a.cs"], record.LastLand!.Conflicts);
	}

	[Fact]
	public async Task SameResult_OrUnchangedMainlineSide_Lands()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(Files(("a.txt", "a\n"), ("b.txt", "b\n")));
		var one = await repo.WorkspaceAsync("one", Files(("a.txt", "same\n")));
		var two = await repo.WorkspaceAsync("two", Files(("a.txt", "same\n"), ("b.txt", "b2\n")));
		Assert.Equal(LandOutcome.Landed, (await repo.LandAsync(one)).Outcome);
		Assert.Equal(LandOutcome.Landed, (await repo.LandAsync(two)).Outcome);

		// Changed and changed back on mainline: the mainline version equals the base version.
		var three = await repo.WorkspaceAsync("three", Files(("a.txt", "three\n")));
		await repo.CommitAsync(Files(("a.txt", "temp\n")), "x");
		await repo.CommitAsync(Files(("a.txt", "same\n")), "y");
		Assert.Equal(LandOutcome.Landed, (await repo.LandAsync(three)).Outcome);
		Assert.Equal("three\n", (await repo.MainFilesAsync())["a.txt"]);
	}

	[Fact]
	public async Task TextMergePolicy_MergesDisjointHunks_ButReplayIsTheDefault()
	{
		var repo = await TestRepo.CreateAsync();
		var lines = string.Join("", Enumerable.Range(1, 30).Select(index => $"line {index}\n"));
		await repo.CommitAsync(Files(("docs/guide.md", lines), ("src/code.cs", lines),
			(RepoPolicy.FileName, "[conflicts]\ndefault = \"replay\"\n[[conflicts.rule]]\npaths = [\"docs/**\"]\npolicy = \"textmerge\"\n")));
		var one = await repo.WorkspaceAsync("one", Files(("docs/guide.md", lines.Replace("line 2\n", "line two\n")), ("src/code.cs", lines.Replace("line 2\n", "x\n"))));
		var two = await repo.WorkspaceAsync("two", Files(("docs/guide.md", lines.Replace("line 29\n", "line twenty-nine\n"))));
		var three = await repo.WorkspaceAsync("three", Files(("src/code.cs", lines.Replace("line 29\n", "y\n"))));
		Assert.Equal(LandOutcome.Landed, (await repo.LandAsync(one)).Outcome);

		var merged = await repo.LandAsync(two);
		Assert.Equal(LandOutcome.Landed, merged.Outcome);
		Assert.Single(merged.Merged);
		var guide = (await repo.MainFilesAsync())["docs/guide.md"];
		Assert.Contains("line two\n", guide);
		Assert.Contains("line twenty-nine\n", guide);

		Assert.Equal(LandOutcome.NeedsReplay, (await repo.LandAsync(three)).Outcome);
	}

	private sealed class ConcatDriver : IMergeDriver
	{
		public string Name => "concat";

		public Task<byte[]?> MergeAsync(string path, byte[] baseContent, byte[] ours, byte[] theirs, CancellationToken ct = default) =>
			Task.FromResult<byte[]?>([.. theirs, .. ours]);
	}

	private sealed class Validator(bool ok) : IValidationHook
	{
		public int Calls { get; private set; }

		public Task<(bool Ok, string Message)> ValidateAsync(ObjectId tree, CancellationToken ct = default)
		{
			Calls++;
			return Task.FromResult((ok, ok ? "ok" : "tests failed"));
		}
	}

	[Theory]
	[InlineData(true, LandOutcome.Landed)]
	[InlineData(false, LandOutcome.NeedsReplay)]
	public async Task SemanticPolicy_LandsMachineMergedOnlyWhenValidationPasses(bool valid, string expected)
	{
		var repo = await TestRepo.CreateAsync();
		var validator = new Validator(valid);
		var landing = new LandingService(repo.Repo, new ConcatDriver(), validator);
		await repo.CommitAsync(Files(("src/a.cs", "a\n"), (RepoPolicy.FileName, "[conflicts]\ndefault = \"semantic\"\n")));
		var one = await repo.WorkspaceAsync("one", Files(("src/a.cs", "one\n")));
		var two = await repo.WorkspaceAsync("two", Files(("src/a.cs", "two\n")));
		Assert.Equal(LandOutcome.Landed, (await landing.LandAsync(new LandRequest(one.Id))).Outcome);
		var result = await landing.LandAsync(new LandRequest(two.Id));
		Assert.Equal(expected, result.Outcome);
		Assert.Equal(1, validator.Calls);
		if (valid)
		{
			Assert.True((await repo.Repo.Objects.ReadCommitAsync(result.Commit!.Value)).MachineMerged);
			Assert.Equal("one\ntwo\n", (await repo.MainFilesAsync())["src/a.cs"]);
		}
	}

	[Fact]
	public async Task SemanticPolicy_WithoutAValidator_BehavesAsReplay()
	{
		var repo = await TestRepo.CreateAsync();
		var landing = new LandingService(repo.Repo, new ConcatDriver());
		await repo.CommitAsync(Files(("a.cs", "a\n"), (RepoPolicy.FileName, "[conflicts]\ndefault = \"semantic\"\n")));
		var one = await repo.WorkspaceAsync("one", Files(("a.cs", "one\n")));
		var two = await repo.WorkspaceAsync("two", Files(("a.cs", "two\n")));
		await landing.LandAsync(new LandRequest(one.Id));
		Assert.Equal(LandOutcome.NeedsReplay, (await landing.LandAsync(new LandRequest(two.Id))).Outcome);
	}

	[Fact]
	public async Task StrictMode_RejectsWhenTheReadSetMoved()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(Files(("api/contract.cs", "v1\n"), ("app/use.cs", "use v1\n")));
		var agent = await repo.WorkspaceAsync("agent", Files(("app/use.cs", "use v1 more\n")));
		await repo.CommitAsync(Files(("api/contract.cs", "v2\n")), "api-change");

		var strict = await repo.Landing.LandAsync(new LandRequest(agent.Id, Strict: true, ReadSet: ["api"], DryRun: true));
		Assert.Equal(LandOutcome.NeedsReplay, strict.Outcome);
		Assert.Equal("api/contract.cs", Assert.Single(strict.Conflicts).Path);
		Assert.Equal(LandOutcome.Landed, (await repo.Landing.LandAsync(new LandRequest(agent.Id))).Outcome);
	}

	[Fact]
	public async Task StackedWorkspace_WaitsForItsParent_ThenLandsOnTopOfIt()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(Files(("lib.cs", "v1\n")));
		var parent = await repo.WorkspaceAsync("parent", Files(("lib.cs", "v2\n")));
		var child = await repo.WorkspaceAsync("child", Files(("app.cs", "uses v2\n"), ("lib.cs", "v3\n")), on: parent.Id);
		Assert.Equal(parent.Id, child.Record.Parent);

		var early = await repo.LandAsync(child);
		Assert.Equal(LandOutcome.Rejected, early.Outcome);
		Assert.Contains("land 'parent' first", early.Message);

		Assert.Equal(LandOutcome.Landed, (await repo.LandAsync(parent)).Outcome);
		Assert.Equal(LandOutcome.Landed, (await repo.LandAsync(child)).Outcome);
		Assert.Equal("v3\n", (await repo.MainFilesAsync())["lib.cs"]);
		Assert.Equal("uses v2\n", (await repo.MainFilesAsync())["app.cs"]);
	}

	[Fact]
	public async Task Sync_RebasesOntoMainline_AndStoresConflictsWhichMainlineRefuses()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(Files(("a.txt", "a\n"), ("b.txt", "b\n")));
		var ws = await repo.WorkspaceAsync("ws", Files(("a.txt", "mine\n"), ("c.txt", "c\n")));
		await repo.CommitAsync(Files(("a.txt", "theirs\n"), ("b.txt", "b2\n")), "other");

		var sync = await repo.Workspaces.SyncAsync(ws.Id, null, null);
		Assert.Equal(["a.txt"], sync.Conflicts);
		Assert.Equal(2, sync.ToSeq);
		var files = await repo.FilesAsync(sync.NewTree);
		Assert.Equal("<conflict>", files["a.txt"]);
		Assert.Equal("b2\n", files["b.txt"]);
		Assert.Equal("c\n", files["c.txt"]);

		var refused = await repo.LandAsync(sync.Workspace);
		Assert.Equal(LandOutcome.Conflict, refused.Outcome);

		var resolved = await repo.EditAsync(sync.Workspace, Files(("a.txt", "resolved\n")));
		Assert.Equal(LandOutcome.Landed, (await repo.LandAsync(resolved)).Outcome);
		Assert.Equal("resolved\n", (await repo.MainFilesAsync())["a.txt"]);
	}

	[Fact]
	public async Task UndoAndRestore_ReturnEarlierViews()
	{
		var repo = await TestRepo.CreateAsync();
		var ws = await repo.WorkspaceAsync("ws", Files(("a.txt", "1\n")));
		var firstOp = ws.Head;
		ws = await repo.EditAsync(ws, Files(("a.txt", "2\n")));
		var (undone, _, newTree) = await repo.Workspaces.UndoAsync(ws.Id, null);
		Assert.Equal("1\n", (await repo.FilesAsync(newTree))["a.txt"]);
		var (redone, _, again) = await repo.Workspaces.UndoAsync(undone.Id, null);
		Assert.Equal("2\n", (await repo.FilesAsync(again))["a.txt"]);
		var (_, _, restored) = await repo.Workspaces.RestoreAsync(redone.Id, firstOp, null);
		Assert.Equal("1\n", (await repo.FilesAsync(restored))["a.txt"]);
	}

	[Fact]
	public async Task KeepAfterLanding_ContinuesWithAFreshChangeOnTheNewHead()
	{
		var repo = await TestRepo.CreateAsync();
		var ws = await repo.WorkspaceAsync("ws", Files(("a.txt", "1\n")));
		var change = (await repo.Repo.Objects.ReadCommitAsync(ws.Record.Working)).ChangeId;
		var result = await repo.Landing.LandAsync(new LandRequest(ws.Id, Keep: true));
		var after = (await repo.Repo.FindWorkspaceAsync(ws.Id))!;
		Assert.Equal(WorkspaceStatus.Open, after.Record.Status);
		Assert.Equal(result.Seq, after.Record.BaseSeq);
		Assert.NotEqual(change, (await repo.Repo.Objects.ReadCommitAsync(after.Record.Working)).ChangeId);
		Assert.Equal(LandOutcome.Nothing, (await repo.LandAsync(after)).Outcome);
	}

	[Fact]
	public async Task PathIndex_AnswersStatListAndChangedSince_AndRebuildsFromTheOperationLog()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(Files(("src/a.cs", "a\n"), ("src/b/c.cs", "c\n"), ("README.md", "r\n")));
		await repo.CommitAsync(Files(("src/a.cs", "a2\n"), ("README.md", null)), "two");
		var seq = (await repo.Repo.GetMainAsync()).Seq;
		Assert.Equal(2, (await repo.Paths.GetAsync("src/a.cs", seq))!.Seq);
		Assert.Null((await repo.Paths.GetAsync("README.md", seq))!.Leaf);
		Assert.NotNull((await repo.Paths.GetAsync("README.md", 1))!.Leaf);
		var listing = await repo.Paths.ListAsync("src", seq).ToListAsync();
		Assert.Equal(["src/b", "src/a.cs"], listing.Select(item => item.Path));
		Assert.Equal(["src/a.cs"], (await repo.Paths.ChangedSinceAsync("src", 1).ToListAsync()).Select(version => version.Path));

		var rebuilt = new MemoryPathIndex();
		var replica = new Repository(repo.Objects, repo.Views, rebuilt, repo.Leases, TestRepo.SmallChunks);
		await replica.EnsureIndexedAsync(await replica.GetMainAsync());
		Assert.Equal(seq, await rebuilt.IndexedSeqAsync());
		Assert.Equal((await repo.Paths.GetAsync("src/b/c.cs", seq))!.Leaf, (await rebuilt.GetAsync("src/b/c.cs", seq))!.Leaf);
		Assert.Null((await rebuilt.GetAsync("README.md", seq))!.Leaf);
	}

	[Fact]
	public async Task SwarmOfConcurrentAgents_LosesNoUpdate()
	{
		var repo = await TestRepo.CreateAsync();
		var shared = Enumerable.Range(0, 5).ToDictionary(index => $"shared/f{index}.txt", index => (string?)"0\n");
		await repo.CommitAsync(shared);
		// Several landing services over the same stores stand in for several server nodes.
		var nodes = Enumerable.Range(0, 3).Select(_ => new LandingService(repo.Repo)).ToList();
		const int Agents = 24;
		var outcomes = await Task.WhenAll(Enumerable.Range(0, Agents).Select(async index =>
		{
			var edits = new Dictionary<string, string?> { [$"own/agent{index}.txt"] = $"{index}\n", [$"shared/f{index % 5}.txt"] = $"agent {index}\n" };
			var ws = await repo.WorkspaceAsync($"agent{index}", edits);
			for (var attempt = 0; attempt < 50; attempt++)
			{
				var result = await nodes[index % nodes.Count].LandAsync(new LandRequest(ws.Id));
				if (result.Outcome == LandOutcome.Landed)
				{
					return (index, result.Seq!.Value);
				}

				Assert.Equal(LandOutcome.NeedsReplay, result.Outcome);
				// Replay: the same task on the new base, keeping the ChangeId.
				var change = (await repo.Repo.Objects.ReadCommitAsync(ws.Record.Working)).ChangeId;
				await repo.Workspaces.SetStatusAsync(ws.Id, WorkspaceStatus.Replaced, "replaced", null);
				var replay = await repo.Workspaces.CreateAsync($"agent{index}-replay{attempt}", "agent", changeId: change, replayOf: ws.Id);
				ws = await repo.EditAsync(replay, edits);
			}

			throw new InvalidOperationException("never landed");
		}));

		var main = await repo.Repo.GetMainAsync();
		Assert.Equal(1 + Agents, main.Seq);
		Assert.Equal(Agents, outcomes.Select(outcome => outcome.Item2).Distinct().Count());
		var files = await repo.MainFilesAsync();
		for (var index = 0; index < Agents; index++)
		{
			Assert.Equal($"{index}\n", files[$"own/agent{index}.txt"]);
		}

		// The last landing per shared file wins, and it is one of the agents that wrote it.
		foreach (var (path, text) in files.Where(pair => pair.Key.StartsWith("shared/", StringComparison.Ordinal)))
		{
			Assert.StartsWith("agent ", text);
		}
	}

	[Fact]
	public async Task AReplayedConflict_LandsWithTheOriginalChangeId()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.CommitAsync(Files(("a.txt", "a\n")));
		var first = await repo.WorkspaceAsync("first", Files(("a.txt", "first\n")));
		var second = await repo.WorkspaceAsync("second", Files(("a.txt", "second\n")));
		await repo.LandAsync(first);
		Assert.Equal(LandOutcome.NeedsReplay, (await repo.LandAsync(second)).Outcome);

		var change = (await repo.Repo.Objects.ReadCommitAsync(second.Record.Working)).ChangeId;
		var replay = await repo.Workspaces.CreateAsync("second-replay", "agent", changeId: change, replayOf: second.Id);
		replay = await repo.EditAsync(replay, Files(("a.txt", "first and second\n")));
		var result = await repo.LandAsync(replay);
		Assert.Equal(LandOutcome.Landed, result.Outcome);
		Assert.Equal(change, (await repo.Repo.Objects.ReadCommitAsync(result.Commit!.Value)).ChangeId);
	}

	[Fact]
	public async Task GarbageCollection_SweepsUnreachableOldBlobs_AndPrunesClosedWorkspaces()
	{
		var store = new MemoryObjectStore();
		var repo = new TestRepo(store);
		var clock = DateTimeOffset.UtcNow;
		repo.Repo.Clock = () => clock;
		store.Clock = () => clock;
		await repo.Repo.InitializeAsync("test");
		await repo.CommitAsync(Files(("kept.txt", "kept\n")));
		var abandoned = await repo.WorkspaceAsync("abandoned", Files(("dropped.txt", "only in an abandoned workspace\n")));
		await repo.Workspaces.CloseAsync(abandoned.Id, true, null);
		var orphan = await repo.Repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes("never referenced"));

		var gc = new GarbageCollector(repo.Repo, store);
		var young = await gc.CollectAsync(TimeSpan.FromHours(1), TimeSpan.FromDays(7));
		Assert.Equal(0, young.Swept);
		Assert.True(young.Young > 0);

		clock += TimeSpan.FromDays(8);
		var dry = await gc.CollectAsync(TimeSpan.FromHours(1), TimeSpan.FromDays(7), dryRun: true);
		Assert.True(dry.Swept > 0);
		Assert.NotNull(await store.ReadAsync(orphan.Id));

		var report = await gc.CollectAsync(TimeSpan.FromHours(1), TimeSpan.FromDays(7));
		// The landed setup workspace and the abandoned one closed more than the retention ago.
		Assert.Equal(2, report.PrunedWorkspaces);
		Assert.Null(await store.ReadAsync(orphan.Id));
		Assert.Null(await repo.Repo.FindWorkspaceAsync(abandoned.Id));
		Assert.Equal("kept\n", (await repo.MainFilesAsync())["kept.txt"]);

		var scrub = await gc.ScrubAsync();
		Assert.Empty(scrub.Corrupt);
		var victim = (await store.ListAsync().ToListAsync()).First().Id;
		store.Corrupt(victim, [1, 2, 3]);
		Assert.Equal([victim], (await gc.ScrubAsync()).Corrupt);
	}
}
