namespace Bassia.LoadTest;

using System.Diagnostics;
using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

/// <summary>What seeding left: mainline's head and how long each part took.</summary>
internal sealed record SeedResult(long MainSeq, ObjectId Tree, double Seconds, double FilesSeconds, double HistorySeconds, double WorkspacesSeconds);

/// <summary>
/// Builds the state a scenario starts from, through the version core alone - trees, commits, operations and landings
/// written to the stores, never through working folders: the monorepo's files in one landing, then the history as one
/// landing each, the closed workspaces that history leaves behind, and the open workspaces with unlanded work.
/// Workspace records are written directly instead of through <see cref="WorkspaceService.CreateAsync"/>, whose name
/// check lists every workspace: seeding must not cost what the run is there to measure.
/// </summary>
internal sealed class Seeder(Repository repo, ILanding landing, Scenario scenario, Action<string> log)
{
	private const string Owner = "seeder";
	private const int FileBatch = 20_000;
	private const int ContentPool = 64;

	private ObjectId mainCommit;
	private long mainSeq;
	private ObjectId tree;

	public async Task<SeedResult> SeedAsync(CancellationToken ct = default)
	{
		var total = Stopwatch.StartNew();
		var main = await repo.GetMainAsync(ct);
		if (main.Seq != 0)
		{
			throw new InvalidOperationException($"The monorepo is not empty (mainline is at seq {main.Seq}); seed a new one.");
		}

		(mainCommit, mainSeq, tree) = (main.CommitId, main.Seq, main.Tree);

		var watch = Stopwatch.StartNew();
		await SeedFilesAsync(ct);
		var files = watch.Elapsed.TotalSeconds;
		log($"seeded {scenario.Files:N0} files in {files:0.0} s");

		watch.Restart();
		var closed = await SeedHistoryAsync(ct);
		var history = watch.Elapsed.TotalSeconds;
		log($"seeded {scenario.History:N0} landings in {history:0.0} s");

		watch.Restart();
		await SeedClosedAsync(closed, ct);
		await SeedOpenAsync(ct);
		var workspaces = watch.Elapsed.TotalSeconds;
		log($"seeded {scenario.Closed:N0} closed and {scenario.Open:N0} open workspaces in {workspaces:0.0} s");

		return new SeedResult(mainSeq, tree, total.Elapsed.TotalSeconds, files, history, workspaces);
	}

	private static byte[] Content(string text) => Encoding.UTF8.GetBytes(text.PadRight(120, '.') + "\n");

	/// <summary>The id of a seeded workspace: like a generated one, but the same in every run.</summary>
	private static string Id(char kind, int index) => $"w{kind}{index:x8}";

	/// <summary>The monorepo's files, built a batch of paths at a time and landed as mainline's seq 1.</summary>
	private async Task SeedFilesAsync(CancellationToken ct)
	{
		// A small pool of contents: a million files do not need a million blobs, and directories still all differ.
		var pool = new Leaf[ContentPool];
		for (var index = 0; index < pool.Length; index++)
		{
			pool[index] = await repo.Objects.WriteContentAsync(Content($"seed {scenario.Seed} content {index}"), ct: ct);
		}

		for (var start = 0; start < scenario.Files; start += FileBatch)
		{
			var changes = new List<PathChange>(FileBatch);
			for (var index = start; index < Math.Min(scenario.Files, start + FileBatch); index++)
			{
				changes.Add(new PathChange(scenario.PathOf(index), pool[scenario.Random(1, index) % ContentPool]));
			}

			tree = await repo.Trees.EditAsync(tree, changes, ct);
		}

		var hot = new List<PathChange>();
		for (var index = 0; index < Scenario.HotFiles; index++)
		{
			hot.Add(new PathChange(Scenario.HotPath(index), await repo.Objects.WriteContentAsync(Content($"seed {scenario.Seed} hot {index}"), ct: ct)));
		}

		tree = await repo.Trees.EditAsync(tree, hot, ct);
		await LandAsync(await OpenAsync(Id('f', 0), "seed-files", tree, "seed the monorepo", ct), false, ct);
	}

	/// <summary>
	/// The history: one landing per step, each changing a few files. A step uses a workspace of its own while closed
	/// workspaces are still wanted, then one workspace that stays open across landings. Returns the closed workspaces made.
	/// </summary>
	private async Task<int> SeedHistoryAsync(CancellationToken ct)
	{
		var closed = 1; // the workspace that landed the files
		WorkspaceState? keeper = null;
		var service = new WorkspaceService(repo);
		for (var step = 0; step < scenario.History; step++)
		{
			var changes = new List<PathChange>(scenario.Changes);
			for (var change = 0; change < scenario.Changes; change++)
			{
				var file = (int)(scenario.Random(2, step, change) % (ulong)scenario.Files);
				changes.Add(new PathChange(scenario.PathOf(file), await repo.Objects.WriteContentAsync(Content($"seed {scenario.Seed} history {step} file {file}"), ct: ct)));
			}

			var next = await repo.Trees.EditAsync(tree, changes.DistinctBy(change => change.Path), ct);
			if (closed < scenario.Closed)
			{
				await LandAsync(await OpenAsync(Id('h', step), $"seed-history-{step}", next, $"history {step}", ct), false, ct);
				closed++;
			}
			else
			{
				keeper ??= await OpenAsync(Id('k', 0), "seed-history", tree, "history", ct);
				await service.SetTreeAsync(keeper.Id, next, "snapshot", Owner, null, ct);
				await LandAsync(keeper, true, ct);
			}

			tree = next;
			if ((step + 1) % 1000 == 0)
			{
				log($"  {step + 1:N0} landings");
			}
		}

		if (keeper is not null)
		{
			await service.CloseAsync(keeper.Id, true, Owner, ct);
			closed++;
		}

		return closed;
	}

	/// <summary>Closed workspaces beyond those the history landed: abandoned ones, without work.</summary>
	private async Task SeedClosedAsync(int already, CancellationToken ct)
	{
		var working = await repo.Objects.WriteAsync(new Commit(tree, [mainCommit], ChangeId.New(), new Author(Owner), "abandoned", repo.Clock()), ct);
		await Parallel.ForEachAsync(Enumerable.Range(0, Math.Max(0, scenario.Closed - already)), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
			async (index, token) =>
			{
				var record = new WorkspaceRecord(Id('c', index), $"seed-closed-{index}", null, mainCommit, mainSeq, working, WorkspaceStatus.Abandoned, repo.Clock(), Owner,
					Closed: repo.Clock());
				await repo.CommitWorkspaceAsync(null, record, "seed an abandoned workspace", Owner, null, token);
			});
	}

	/// <summary>Open workspaces, each with a few changed files, holding their names as created workspaces do.</summary>
	private async Task SeedOpenAsync(CancellationToken ct)
	{
		await Parallel.ForEachAsync(Enumerable.Range(0, scenario.Open), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
			async (index, token) =>
			{
				var changes = new List<PathChange>(scenario.Changes);
				for (var change = 0; change < scenario.Changes; change++)
				{
					var file = (int)(scenario.Random(3, index, change) % (ulong)scenario.Files);
					changes.Add(new PathChange(scenario.PathOf(file), await repo.Objects.WriteContentAsync(Content($"seed {scenario.Seed} open {index} file {file}"), ct: token)));
				}

				var edited = await repo.Trees.EditAsync(tree, changes.DistinctBy(change => change.Path), token);
				var state = await OpenAsync(Id('o', index), OpenName(index), edited, $"open work {index}", token);
				await repo.ClaimWorkspaceNameAsync(state, token);
			});
	}

	public static string OpenName(int index) => $"seed-open-{index}";

	/// <summary>An open workspace on mainline's head whose working commit holds <paramref name="working"/>.</summary>
	private async Task<WorkspaceState> OpenAsync(string id, string name, ObjectId working, string intent, CancellationToken ct)
	{
		var commit = await repo.Objects.WriteAsync(new Commit(working, [mainCommit], ChangeId.New(), new Author(Owner), intent, repo.Clock()), ct);
		var record = new WorkspaceRecord(id, name, null, mainCommit, mainSeq, commit, WorkspaceStatus.Open, repo.Clock(), Owner);
		return await repo.CommitWorkspaceAsync(null, record, $"create on seq {mainSeq}", Owner, null, ct);
	}

	private async Task LandAsync(WorkspaceState workspace, bool keep, CancellationToken ct)
	{
		var result = await landing.LandAsync(new LandRequest(workspace.Id, Keep: keep, Agent: Owner), ct);
		if (result.Outcome != LandOutcome.Landed)
		{
			throw new InvalidOperationException($"Seeding could not land '{workspace.Name}': {result.Outcome}: {result.Message}");
		}

		mainCommit = result.Commit!.Value;
		mainSeq = result.Seq!.Value;
	}
}
