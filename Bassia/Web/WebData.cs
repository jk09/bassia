namespace Bassia.Web;

using Bassia.Storage;
using System.Globalization;
using Bassia.Integration;
using Bassia.Locks;

/// <summary>
/// A run some <c>bassia</c> process is executing right now (<c>bassia agent start</c>, detached or in a terminal), as the
/// job registry and the run store show it: its phase, how long it has been going, the latest line of its output, and
/// what it holds and waits for in the lock table.
/// </summary>
internal sealed record LiveRun(string RunId, string Phase, DateTimeOffset Started, RunMetadata? Record, IReadOnlyList<string> Tail, bool Detached,
	IReadOnlyList<string>? Holds = null, IReadOnlyList<string>? Waits = null)
{
	public string? LastLine => Tail.Count == 0 ? null : Tail[^1];
}

internal static class LiveRuns
{
	public const int TailLines = 40;

	/// <summary>Every live run, oldest first. A run whose process is gone is not live, whatever its record says.</summary>
	public static async Task<IReadOnlyList<LiveRun>> ReadAsync(Monorepo monorepo, IReadOnlyList<RunMetadata> recorded)
	{
		var live = new List<LiveRun>();
		var table = monorepo.Locks;
		var locks = (await table.ReadAllAsync()).ToDictionary(entry => entry.RunId, StringComparer.Ordinal);
		var jobs = monorepo.Jobs;
		foreach (var job in (await jobs.ListAsync(RunCommands.JobKind)).Where(jobs.IsAlive))
		{
			var record = recorded.FirstOrDefault(run => run.RunId == job.Id);
			if (record is not null && record.Status != "started")
			{
				continue;
			}

			var tail = (await RunCommands.OutputAsync(jobs, job, -TailWindow))?.Where(line => line.Trim().Length > 0).TakeLast(TailLines).ToList() ?? [];
			var started = Parse(record?.Created ?? job.Started) ?? DateTimeOffset.UtcNow;
			var entry = locks.GetValueOrDefault(job.Id);
			live.Add(new LiveRun(job.Id, record is null ? "preparing" : PhaseOf(tail), started, record, tail, job.Detached,
				entry?.Holds.Select(hold => hold.Key.Display).ToList(),
				entry?.Waits.Where(wait => table.IsWaiting(entry, wait)).Select(wait => wait.Key.Display).ToList()));
		}

		return live.OrderBy(run => run.Started).ToList();
	}

	/// <summary>
	/// Where a recorded, live run is, from the last progress line bassia wrote to its log: the agent works until the
	/// results are committed, tagged and pushed. Without a log (a run in a terminal) the agent is assumed working.
	/// </summary>
	internal static string PhaseOf(IReadOnlyList<string> tail)
	{
		var progress = tail.LastOrDefault(line => line.StartsWith("bassia: ", StringComparison.Ordinal)) ?? "";
		return progress.Contains("committing, tagging and pushing", StringComparison.Ordinal) || progress.Contains(" committed as ", StringComparison.Ordinal)
			|| progress.EndsWith(" unchanged.", StringComparison.Ordinal) || progress.Contains("' failed: ", StringComparison.Ordinal)
				? "finalizing"
				: "agent";
	}

	/// <summary>How much of the end of a live run's output is read for its tail.</summary>
	private const long TailWindow = 64 * 1024;

	public static DateTimeOffset? Parse(string? timestamp) =>
		DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
}

/// <summary>
/// The lock table as the dashboard shows it: every hold with where it stands and who waits for it, and every wait with
/// the runs it waits for.
/// </summary>
internal sealed record LockBoard(IReadOnlyList<LockBoard.Row> Holds, IReadOnlyList<LockBoard.Waiting> Waits, TimeSpan Ttl)
{
	internal sealed record Row(LockEntry Entry, LockHold Hold, LockState State, IReadOnlyList<string> Waiters);

	internal sealed record Waiting(LockEntry Entry, LockWait Wait, IReadOnlyList<string> HeldBy);

	public static LockBoard Empty { get; } = new([], [], TimeSpan.Zero);

	public static async Task<LockBoard> ReadAsync(Monorepo monorepo)
	{
		var table = monorepo.Locks;
		var entries = (await table.ReadAllAsync());
		var states = entries.ToDictionary(entry => entry.RunId, entry => table.StateOf(entry), StringComparer.Ordinal);
		var waits = entries.SelectMany(entry => entry.Waits.Where(wait => table.IsWaiting(entry, wait)).Select(wait => new Waiting(entry, wait,
				entries.Where(other => other.RunId != entry.RunId && states[other.RunId] == LockState.Live && other.Holds.Any(hold => hold.Key.Overlaps(wait.Key)))
					.Select(other => other.RunId).ToList())))
			.ToList();
		var holds = entries.SelectMany(entry => entry.Holds.Select(hold => new Row(entry, hold, states[entry.RunId],
				waits.Where(wait => wait.Entry.RunId != entry.RunId && wait.Wait.Key.Overlaps(hold.Key)).Select(wait => wait.Entry.RunId).Distinct().ToList())))
			.OrderBy(row => row.Hold.Key.Component, StringComparer.Ordinal).ThenBy(row => row.Hold.Key.Path, StringComparer.Ordinal)
			.ToList();
		return new LockBoard(holds, waits, table.Ttl);
	}

	public IEnumerable<Row> Live => Holds.Where(row => row.State == LockState.Live);

	public IEnumerable<Row> InComponent(string component) => Holds.Where(row => row.Hold.Key.Component == component);

	public IEnumerable<Row> OfRun(string runId) => Holds.Where(row => row.Entry.RunId == runId);
}

/// <summary>Where a run's result in one component stands on its way to the component's default branch.</summary>
internal enum QueueState
{
	/// <summary>Pushed and tagged, not yet in any integration.</summary>
	Waiting,

	/// <summary>Merged by an integration whose result has not reached the default branch (<c>integration advance</c>).</summary>
	Integrated,

	/// <summary>An integration left it for a human, or failed on it.</summary>
	NeedsAttention,

	/// <summary>Reachable from the default branch: done.</summary>
	Landed
}

/// <summary>One run's result in one component, and the latest integration step that dealt with it.</summary>
internal sealed record QueueItem(RunMetadata Run, string Component, string ResultTag, string ResultCommit, QueueState State,
	IntegrationRecord? Integration, IntegrationStep? Step);

/// <summary>
/// The merge queue: every result of every run that has not landed on its component's default branch yet, oldest run
/// first, with where it stands. A result counts as landed only when git says the default branch contains it, so the
/// queue is right however the result got there.
/// </summary>
internal sealed class MergeQueue
{
	private MergeQueue(IReadOnlyList<QueueItem> items, IReadOnlyDictionary<string, string> defaultBranches)
	{
		Items = items;
		DefaultBranches = defaultBranches;
	}

	/// <summary>Every result, landed ones included.</summary>
	public IReadOnlyList<QueueItem> Items { get; }

	/// <summary>Component -> its default branch (where results land).</summary>
	public IReadOnlyDictionary<string, string> DefaultBranches { get; }

	public IEnumerable<QueueItem> Pending => Items.Where(item => item.State != QueueState.Landed);

	public IEnumerable<QueueItem> Attention => Items.Where(item => item.State == QueueState.NeedsAttention);

	/// <summary>The runs the next integration would take: those with a result still waiting or left for a human.</summary>
	public IReadOnlyList<RunMetadata> RunsToIntegrate => Items.Where(item => item.State is QueueState.Waiting or QueueState.NeedsAttention)
		.Select(item => item.Run).DistinctBy(run => run.RunId).OrderBy(run => run.Created, StringComparer.Ordinal).ToList();

	public static async Task<MergeQueue> ReadAsync(Monorepo monorepo, IReadOnlyList<RunMetadata> runs, IReadOnlyList<IntegrationRecord> integrations)
	{
		var items = new List<QueueItem>();
		var branches = new Dictionary<string, string>(StringComparer.Ordinal);
		var heads = new Dictionary<string, string?>(StringComparer.Ordinal);
		var storage = monorepo.Backend.Components;
		foreach (var run in runs.Where(IntegrationPlanner.HasResults).OrderBy(run => run.Created, StringComparer.Ordinal))
		{
			foreach (var result in run.Components.Where(entry => entry.ResultStatus == ResultStatus.Pushed && entry.ResultCommit is not null && entry.ResultTag is not null))
			{
				if (monorepo.FindComponent(result.Name) is null || !storage.Exists(result.Name))
				{
					continue;
				}

				var store = storage.Open(result.Name);
				if (!heads.TryGetValue(result.Name, out var head))
				{
					var name = await store.DefaultBranchAsync();
					if (name is not null)
					{
						branches[result.Name] = name;
					}

					heads[result.Name] = head = name is null ? null : await store.BranchTipAsync(name);
				}

				// The latest integration that took this result decides, unless the default branch already has it.
				var (integration, step) = integrations
					.SelectMany(record => record.Components.Where(component => component.Name == result.Name)
						.SelectMany(component => component.Steps.Where(candidate => candidate.RunId == run.RunId).Select(candidate => (record, candidate))))
					.OrderByDescending(pair => pair.record.Created, StringComparer.Ordinal)
					.FirstOrDefault();

				var landed = head is not null && await IsAncestorAsync(store, result.ResultCommit!, head);
				var state = landed ? QueueState.Landed
					: step is null || step.Outcome is StepOutcome.Pending or StepOutcome.Skipped ? QueueState.Waiting
					: step.NeedsAttention ? QueueState.NeedsAttention
					: QueueState.Integrated;
				items.Add(new QueueItem(run, result.Name, result.ResultTag!, result.ResultCommit!, state, integration, step));
			}
		}

		return new MergeQueue(items, branches);
	}

	/// <summary>Whether a commit is in a branch's history; a commit the store does not have is not.</summary>
	private static async Task<bool> IsAncestorAsync(Storage.IComponentStore store, string ancestor, string descendant) =>
		await store.GetCommitAsync(ancestor) is not null && await store.IsAncestorAsync(ancestor, descendant);
}

/// <summary>
/// A tag across components as a unit of progress: the components and commits it marks, what made it (a run, an
/// integration, an unwind or split, or a person creating a baseline), the runs that started from it, and the
/// integrations that took it in.
/// </summary>
internal sealed record TagStory(
	MultiComponentTag Tag,
	string Kind,
	RunMetadata? MadeByRun,
	IntegrationRecord? MadeByIntegration,
	IReadOnlyList<RunMetadata> StartedRuns,
	IReadOnlyList<IntegrationRecord> IntegratedBy)
{
	public string Date => Tag.Components.Select(component => component.Date).DefaultIfEmpty("").Max(StringComparer.Ordinal) ?? "";

	/// <summary>The web's name for a tag's kind: a tag Bassia did not make is a baseline someone set.</summary>
	public static string KindOf(string name) => MultiComponentTag.KindOf(name) is "other" ? "baseline" : MultiComponentTag.KindOf(name);

	public static TagStory For(MultiComponentTag tag, IReadOnlyList<RunMetadata> runs, IReadOnlyList<IntegrationRecord> integrations)
	{
		var kind = KindOf(tag.Name);
		var parts = tag.Name.Split('/');
		var madeByRun = kind == "run" && parts.Length > 1 ? runs.FirstOrDefault(run => RunMetadata.Key(run.RunId) == parts[1]) : null;
		var madeByIntegration = kind == "integration" && parts.Length > 1 ? integrations.FirstOrDefault(record => IntegrationRecord.Key(record.IntegrationId) == parts[1]) : null;
		var started = runs.Where(run => run.Components.Any(component => component.CommitIsh == tag.Name)).OrderBy(run => run.Created, StringComparer.Ordinal).ToList();
		var integratedBy = integrations.Where(record => record.AllSteps.Any(step => step.SourceTag == tag.Name)).OrderBy(record => record.Created, StringComparer.Ordinal).ToList();
		return new TagStory(tag, kind, madeByRun, madeByIntegration, started, integratedBy);
	}
}
