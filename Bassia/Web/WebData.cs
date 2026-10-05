namespace Bassia.Web;

using System.Globalization;
using Bassia.Git;
using Bassia.Integration;

/// <summary>
/// A run some <c>bassia</c> process is executing right now (<c>bassia run start</c>, detached or in a terminal), as the
/// job registry and the run store show it: its phase, how long it has been going, and the latest line of its output.
/// </summary>
internal sealed record LiveRun(string RunId, string Phase, DateTimeOffset Started, RunMetadata? Record, IReadOnlyList<string> Tail, bool Detached)
{
	public string? LastLine => Tail.Count == 0 ? null : Tail[^1];
}

internal static class LiveRuns
{
	public const int TailLines = 40;

	/// <summary>Every live run, oldest first. A run whose process is gone is not live, whatever its record says.</summary>
	public static IReadOnlyList<LiveRun> Read(Monorepo monorepo, IReadOnlyList<RunMetadata> recorded)
	{
		var live = new List<LiveRun>();
		foreach (var job in new JobRegistry(monorepo).List(RunCommands.JobKind).Where(JobRegistry.IsAlive))
		{
			var record = recorded.FirstOrDefault(run => run.RunId == job.Id);
			if (record is not null && record.Status != "started")
			{
				continue;
			}

			var tail = job.Log is null ? [] : Tail(job.Log, TailLines);
			var started = Parse(record?.Created ?? job.Started) ?? DateTimeOffset.UtcNow;
			live.Add(new LiveRun(job.Id, record is null ? "preparing" : PhaseOf(tail), started, record, tail, job.Detached));
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

	/// <summary>The last non-empty lines of a log another process is still writing.</summary>
	internal static IReadOnlyList<string> Tail(string path, int count)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			const int window = 64 * 1024;
			if (stream.Length > window)
			{
				stream.Seek(-window, SeekOrigin.End);
			}

			using var reader = new StreamReader(stream);
			return reader.ReadToEnd().Replace("\r", "").Split('\n').Where(line => line.Trim().Length > 0).TakeLast(count).ToList();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	public static DateTimeOffset? Parse(string? timestamp) =>
		DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
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
		foreach (var run in runs.Where(IntegrationPlanner.HasResults).OrderBy(run => run.Created, StringComparer.Ordinal))
		{
			foreach (var result in run.Components.Where(entry => entry.ResultStatus == ResultStatus.Pushed && entry.ResultCommit is not null && entry.ResultTag is not null))
			{
				var sourceDir = monorepo.SourceRepoDir(result.Name);
				if (monorepo.FindComponent(result.Name) is null || !Directory.Exists(sourceDir))
				{
					continue;
				}

				var git = GitClient.In(sourceDir);
				if (!heads.TryGetValue(result.Name, out var head))
				{
					var branch = await git.RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"]);
					var name = branch.ExitCode == 0 ? branch.Output.Trim() : null;
					if (name is not null)
					{
						branches[result.Name] = name;
					}

					heads[result.Name] = head = name is null ? null : await IntegrationPlanner.ResolveAsync(git, name);
				}

				// The latest integration that took this result decides, unless the default branch already has it.
				var (integration, step) = integrations
					.SelectMany(record => record.Components.Where(component => component.Name == result.Name)
						.SelectMany(component => component.Steps.Where(candidate => candidate.RunId == run.RunId).Select(candidate => (record, candidate))))
					.OrderByDescending(pair => pair.record.Created, StringComparer.Ordinal)
					.FirstOrDefault();

				var landed = head is not null && await IsAncestorAsync(git, result.ResultCommit!, head);
				var state = landed ? QueueState.Landed
					: step is null || step.Outcome is StepOutcome.Pending or StepOutcome.Skipped ? QueueState.Waiting
					: step.NeedsAttention ? QueueState.NeedsAttention
					: QueueState.Integrated;
				items.Add(new QueueItem(run, result.Name, result.ResultTag!, result.ResultCommit!, state, integration, step));
			}
		}

		return new MergeQueue(items, branches);
	}

	private static async Task<bool> IsAncestorAsync(GitClient git, string ancestor, string descendant) =>
		(await git.RunAsync(["merge-base", "--is-ancestor", ancestor, descendant])).ExitCode == 0;
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
