namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Storage;
using static MemoryMonorepo;

/// <summary>
/// Agentic runs on the component graph with in-memory storage: the agent is a delegate that edits the run folder, so the
/// whole sequence - selection, working copies, nesting, commit, tag, publish, records, retry and abandon - runs without
/// git or a shell.
/// </summary>
public class AgentRunsTests
{
	/// <summary>lib (tagged v1) and app (tagged v1, referencing lib at vendor/lib).</summary>
	private static async Task<MemoryMonorepo> TwoComponentsAsync()
	{
		var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib", null, ("lib.c", "int lib;"));
		await monorepo.AddAsync("app", Refs("lib:vendor/lib"), ("main.c", "int main;"));
		return monorepo;
	}

	private static void Write(RunMetadata metadata, string path, string content)
	{
		var file = Path.Combine(metadata.WorkspacePath, path);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, content);
	}

	[Fact]
	public async Task Start_CommitsTagsAndPublishesWhatTheAgentChanged()
	{
		using var monorepo = await TwoComponentsAsync();
		var locks = new RecordingLocks();
		var prepared = false;
		var runs = monorepo.Runs(metadata =>
		{
			// The referenced component is nested in its referrer: both paths reach the same working copy.
			Assert.Equal("int lib;", File.ReadAllText(Path.Combine(metadata.WorkspacePath, "app", "vendor", "lib", "lib.c")));
			Write(metadata, "app/feature.c", "int feature;");
			return 0;
		}, (_, _) => locks, _ => prepared = true);

		var outcome = await runs.StartAsync("app@v1,lib@v1", "agent \"add a feature\"", Quiet());

		Assert.True(outcome.Ok, outcome.Message);
		Assert.Equal("completed", outcome.Metadata.Status);
		Assert.True(prepared);
		Assert.Equal(["stop", "check-stale", "finish", "dispose"], locks.Calls);

		var app = outcome.Metadata.Components.Single(component => component.Name == "app");
		var lib = outcome.Metadata.Components.Single(component => component.Name == "lib");
		Assert.Equal(ResultStatus.Pushed, app.ResultStatus);
		Assert.Equal(ResultStatus.Unchanged, lib.ResultStatus);
		Assert.Equal(RunMetadata.TagName(outcome.Metadata.RunId, 0), app.ResultTag);
		Assert.Equal("lib", app.Junctions["vendor/lib"]);

		// Published: the run branch and the result tag point at the result commit, which holds the change but not the nested lib.
		var store = monorepo.Storage["app"];
		Assert.Equal(app.ResultCommit, await store.BranchTipAsync(RunMetadata.RefBase(outcome.Metadata.RunId)));
		Assert.Equal(app.ResultCommit, (await store.GetTagAsync(app.ResultTag!))!.Commit);
		Assert.Equal(["feature.c", "main.c"], await FilesAsync(store, app.ResultCommit!));
		var commit = (await store.GetCommitAsync(app.ResultCommit!))!;
		Assert.Equal([app.Commit], commit.Parents);
		Assert.Contains("add a feature", commit.Subject);

		// The baseline did not move, and the record went from started to completed.
		Assert.Equal(app.Commit, await store.BranchTipAsync("main"));
		Assert.Equal(["started", "completed"], monorepo.Records.History(outcome.Metadata.RunId).Select(record => record.Status));
	}

	[Fact]
	public async Task Start_FromBareNames_ResolvesTheDefaultBranches()
	{
		using var monorepo = await TwoComponentsAsync();
		var tip = monorepo.Storage["lib"].Commit("main", "Newer lib", new Dictionary<string, string?> { ["new.c"] = "new" });

		var outcome = await monorepo.Runs(_ => 0).StartAsync("app,lib", "agent", Quiet());

		var lib = outcome.Metadata.Components.Single(component => component.Name == "lib");
		Assert.Equal(("main", tip), (lib.CommitIsh, lib.Commit));
		Assert.Contains("changed no component", outcome.Message);
	}

	[Fact]
	public async Task Start_ByCommitHashPrefix_SelectsThatCommit()
	{
		using var monorepo = await TwoComponentsAsync();
		var first = (await monorepo.Storage["lib"].BranchTipAsync("main"))!;
		monorepo.Storage["lib"].Commit("main", "Newer lib", new Dictionary<string, string?> { ["new.c"] = "new" });

		var (_, resolved) = await monorepo.Runs(_ => 0).ResolveSelectionAsync($"app@v1,lib@{first[..10]}");

		Assert.Equal(first, resolved.Single(entry => entry.Component.Name == "lib").Commit);
	}

	[Theory]
	[InlineData("app@v1", "must cover the full component closure; missing: 'lib' (referenced by 'app')")]
	[InlineData("app@v1,lib@main", "is a commit, not an annotated tag")]
	[InlineData("app@v1,lib@v9", "does not exist")]
	[InlineData("app@v1,nope@v1", "Component 'nope' is not registered")]
	[InlineData("", "-select names no component")]
	public async Task Start_InvalidSelection_FailsBeforeAnythingIsWritten(string select, string expected)
	{
		using var monorepo = await TwoComponentsAsync();

		var error = await Assert.ThrowsAsync<AgentException>(() => monorepo.Runs(_ => throw new InvalidOperationException("the agent must not run")).StartAsync(select, "agent", Quiet()));

		Assert.Contains(expected, error.Message);
		Assert.False(Directory.Exists(monorepo.WorkspaceDir) && Directory.EnumerateFileSystemEntries(monorepo.WorkspaceDir).Any());
		Assert.Empty(await monorepo.Records.ListLatestAsync());
	}

	[Fact]
	public async Task Start_AgentFails_RecordsFailedAndPublishesNothing()
	{
		using var monorepo = await TwoComponentsAsync();

		var outcome = await monorepo.Runs(metadata =>
		{
			Write(metadata, "app/half-done.c", "x");
			return 3;
		}).StartAsync("app@v1,lib@v1", "agent", Quiet());

		Assert.False(outcome.Ok);
		Assert.Equal(("failed", 3), (outcome.Metadata.Status, outcome.Metadata.AgentExitCode));
		Assert.Empty(await monorepo.Storage["app"].ListTagsAsync(RunMetadata.RefPrefix));
		Assert.True(File.Exists(Path.Combine(outcome.Metadata.WorkspacePath, "app", "half-done.c")));
	}

	[Fact]
	public async Task Start_Cancelled_RecordsCancelledAndKeepsTheFolder()
	{
		using var monorepo = await TwoComponentsAsync();
		var locks = new RecordingLocks();

		var outcome = await monorepo.Runs(_ => null, (_, _) => locks).StartAsync("app@v1,lib@v1", "agent", Quiet());

		Assert.Equal("cancelled", outcome.Metadata.Status);
		Assert.Contains("finish", locks.Calls);
		Assert.True(Directory.Exists(outcome.Metadata.WorkspacePath));
	}

	[Fact]
	public async Task Start_CancelledBeforeMaterializing_LeavesNothing()
	{
		using var monorepo = await TwoComponentsAsync();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			monorepo.Runs(_ => 0).StartAsync("app@v1,lib@v1", "agent", new AgentRunContext { Cancellation = cancellation.Token, OnStep = _ => { } }));

		Assert.Empty(await monorepo.Records.ListLatestAsync());
	}

	[Fact]
	public async Task Retry_AfterAFailedPublish_FinishesTheRun()
	{
		using var monorepo = await TwoComponentsAsync();
		var app = monorepo.Storage["app"];
		var stray = app.Commit("elsewhere", "Not related to the run", new Dictionary<string, string?> { ["x"] = "x" });

		// The agent's change cannot be published: someone moved the run branch to an unrelated commit meanwhile.
		var runs = monorepo.Runs(metadata =>
		{
			Write(metadata, "app/feature.c", "int feature;");
			app.SetBranchAsync(RunMetadata.RefBase(metadata.RunId), stray).Wait();
			return 0;
		});
		var partial = await runs.StartAsync("app@v1,lib@v1", "agent", Quiet());

		Assert.Equal("partial", partial.Metadata.Status);
		var failed = partial.Metadata.Components.Single(component => component.Name == "app");
		Assert.Equal(ResultStatus.Failed, failed.ResultStatus);
		Assert.Contains("not a fast-forward", failed.ResultError);

		// Once the branch is back where the run started, the retry publishes the commit and tag recorded the first time.
		await app.SetBranchAsync(RunMetadata.RefBase(partial.Metadata.RunId), failed.Commit);
		var retried = await runs.RetryAsync(partial.Metadata);

		Assert.True(retried.Ok, retried.Message);
		var pushed = retried.Metadata.Components.Single(component => component.Name == "app");
		Assert.Equal((ResultStatus.Pushed, failed.ResultCommit, failed.ResultTag), (pushed.ResultStatus, pushed.ResultCommit, pushed.ResultTag));
		Assert.Equal(pushed.ResultCommit, await app.BranchTipAsync(RunMetadata.RefBase(partial.Metadata.RunId)));
		Assert.Equal(["started", "partial", "completed"], monorepo.Records.History(partial.Metadata.RunId).Select(record => record.Status));
	}

	[Fact]
	public async Task Retry_OfARunThatDidNotFailPublishing_IsRefused()
	{
		using var monorepo = await TwoComponentsAsync();
		var runs = monorepo.Runs(_ => 1);
		var failed = await runs.StartAsync("app@v1,lib@v1", "agent", Quiet());

		var error = await Assert.ThrowsAsync<AgentException>(() => runs.RetryAsync(failed.Metadata));

		Assert.Contains("has status 'failed'", error.Message);
	}

	[Fact]
	public async Task Abandon_DiscardsTheFolder_KeepsPublishedResults_AndOnlyOnce()
	{
		using var monorepo = await TwoComponentsAsync();
		var runs = monorepo.Runs(metadata =>
		{
			Write(metadata, "lib/fix.c", "fixed");
			return 0;
		});
		var outcome = await runs.StartAsync("app@v1,lib@v1", "agent", Quiet());

		var abandoned = await runs.AbandonAsync(outcome.Metadata);

		Assert.Equal("abandoned", abandoned.Metadata.Status);
		Assert.False(Directory.Exists(outcome.Metadata.WorkspacePath));
		Assert.NotEmpty(await monorepo.Storage["lib"].ListTagsAsync(RunMetadata.RefBase(outcome.Metadata.RunId)));
		await Assert.ThrowsAsync<AgentException>(() => runs.AbandonAsync(abandoned.Metadata));
	}

	[Fact]
	public async Task ResultTags_ContinueTheRunsSequence()
	{
		using var monorepo = await TwoComponentsAsync();
		var store = monorepo.Storage["lib"];
		var runId = RunMetadata.NewRunId();
		var workspace = await store.CheckoutAsync((await store.BranchTipAsync("main"))!, Path.Combine(monorepo.WorkspaceDir, "x"), RunMetadata.RefBase(runId));
		await workspace.TagAsync(RunMetadata.TagName(runId, 0), (await store.BranchTipAsync("main"))!, "earlier");
		await workspace.TagAsync(RunMetadata.TagName(runId, 4), (await store.BranchTipAsync("main"))!, "earlier");

		Assert.Equal(RunMetadata.TagName(runId, 5), await AgentRuns.NextResultTagAsync(workspace, runId));
	}

	[Fact]
	public async Task Revisions_ResolveNamesThroughTheContract()
	{
		using var monorepo = await TwoComponentsAsync();
		var store = monorepo.Storage["lib"];
		var first = (await store.BranchTipAsync("main"))!;
		var second = store.Commit("main", "Second", new Dictionary<string, string?> { ["b"] = "b" });

		Assert.Equal(second, await Revisions.ResolveAsync(store, "HEAD"));
		Assert.Equal(second, await Revisions.ResolveAsync(store, "main"));
		Assert.Equal(first, await Revisions.ResolveAsync(store, "main~1"));
		Assert.Equal(first, await Revisions.ResolveAsync(store, "HEAD^"));
		Assert.Equal(first, await Revisions.ResolveAsync(store, "v1"));
		Assert.Equal(first, await Revisions.ResolveAsync(store, "refs/tags/v1"));
		Assert.Equal(second, await Revisions.ResolveAsync(store, second[..7]));
		Assert.Null(await Revisions.ResolveAsync(store, "main~5"));
		Assert.Null(await Revisions.ResolveAsync(store, "missing"));
	}
}
