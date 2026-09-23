namespace Bassia.Ui;

using Bassia.CliCommands.Agent;

/// <summary>
/// One run as the wallboard draws it: an immutable snapshot, so the renderer never reads fields a background run
/// is writing. A live run and a run read back from <c>.agentic-runs</c> produce the same shape.
/// </summary>
internal sealed record RunCard(
	string Key,
	string? RunId,
	AgentRunPhase Phase,
	string Select,
	IReadOnlyList<string> Components,
	string Command,
	string Message,
	DateTimeOffset Started,
	DateTimeOffset? Finished,
	bool IsLive,
	string? LastOutput)
{
	/// <summary>The short id once the run has one, otherwise the placeholder the frontend gave it while starting.</summary>
	public string Label => RunId is null ? Key : RunMetadata.ShortKey(RunId);

	public TimeSpan Elapsed => (Finished ?? DateTimeOffset.UtcNow) - Started;
}

/// <summary>
/// Owns the agentic runs the frontend started, each as a background task over the same
/// <see cref="AgentCommand.StartRunAsync"/> the CLI calls. Starting returns as soon as the task is scheduled,
/// which is what lets the frontend stay responsive and run several agents at once; every run keeps its latest
/// phase, progress line and a tail of the agent's output for its card, and can be cancelled individually.
/// </summary>
internal sealed class RunSupervisor : IDisposable
{
	/// <summary>Kept per run for its detail view; enough to see what the agent is doing, not a transcript.</summary>
	public const int OutputTailLength = 200;

	private readonly Func<string, string, AgentRunContext, Task<AgentRunOutcome>> start;
	private readonly List<LiveRun> runs = [];
	private readonly Lock gate = new();
	private int counter;
	private bool settled;

	public RunSupervisor(Func<string, string, AgentRunContext, Task<AgentRunOutcome>> start) => this.start = start;

	/// <summary>Starts a run in the background and returns its card key at once. The run never throws to the caller.</summary>
	public string Start(string select, string command)
	{
		lock (gate)
		{
			// The task is attached before the run is published, so a caller that sees the run in a snapshot can
			// always wait for it.
			var run = new LiveRun($"#{++counter}", select, command);
			run.Task = Task.Run(() => ExecuteAsync(run));
			runs.Add(run);
			return run.Key;
		}
	}

	private async Task ExecuteAsync(LiveRun run)
	{
		try
		{
			var context = new AgentRunContext
			{
				Cancellation = run.Cancellation.Token,
				OnStep = step => run.Step(step),
				OnOutput = line => run.Output(line)
			};

			var outcome = await start(run.Select, run.Command, context);
			run.Finish(AgentRunContext.PhaseOfStatus(outcome.Metadata.Status), outcome.Metadata.RunId, outcome.Message, outcome);
		}
		catch (OperationCanceledException)
		{
			run.Finish(AgentRunPhase.Cancelled, null, "Cancelled before the run was created; nothing was materialized.", null);
		}
		catch (Exception ex) when (ex is AgentException or MonorepoException or Git.GitException or IOException or UnauthorizedAccessException)
		{
			run.Finish(AgentRunPhase.Failed, null, ex.Message, null);
		}
		finally
		{
			lock (gate)
			{
				settled = true;
			}
		}
	}

	/// <summary>Every run this frontend started, newest first.</summary>
	public IReadOnlyList<RunCard> Cards()
	{
		lock (gate)
		{
			return runs.AsEnumerable().Reverse().Select(run => run.Snapshot()).ToList();
		}
	}

	public bool HasLive => Cards().Any(card => card.IsLive);

	public int LiveCount => Cards().Count(card => card.IsLive);

	/// <summary>
	/// True once (per call) after any run has finished. The frontend uses it to reload the stored run records
	/// exactly when they changed, instead of polling git on every repaint.
	/// </summary>
	public bool ConsumeSettled()
	{
		lock (gate)
		{
			var was = settled;
			settled = false;
			return was;
		}
	}

	/// <summary>Stops the agent process of a live run. Does nothing for a run that has already finished.</summary>
	public bool Cancel(string key)
	{
		LiveRun? run;
		lock (gate)
		{
			run = runs.FirstOrDefault(candidate => candidate.Key == key);
		}

		return run is not null && run.RequestCancel();
	}

	public void CancelAll()
	{
		foreach (var card in Cards().Where(card => card.IsLive))
		{
			Cancel(card.Key);
		}
	}

	/// <summary>The full output of a run, as far as it was kept.</summary>
	public IReadOnlyList<string> OutputOf(string key)
	{
		lock (gate)
		{
			return runs.FirstOrDefault(candidate => candidate.Key == key)?.OutputTail() ?? [];
		}
	}

	public AgentRunOutcome? OutcomeOf(string key)
	{
		lock (gate)
		{
			return runs.FirstOrDefault(candidate => candidate.Key == key)?.Outcome;
		}
	}

	/// <summary>Waits for every started run to reach a terminal phase.</summary>
	public Task WhenAllSettledAsync()
	{
		lock (gate)
		{
			return Task.WhenAll(runs.Select(run => run.Task ?? Task.CompletedTask));
		}
	}

	public void Dispose()
	{
		lock (gate)
		{
			foreach (var run in runs)
			{
				run.Dispose();
			}
		}
	}

	/// <summary>
	/// The mutable side of a run. Its fields are written by the run's own task and read by the render loop, so
	/// every access goes through its lock and the renderer only ever sees a <see cref="RunCard"/>.
	/// </summary>
	private sealed class LiveRun : IDisposable
	{
		private readonly Lock gate = new();
		private readonly Queue<string> output = new();
		private readonly List<string> components;
		private AgentRunPhase phase = AgentRunPhase.Queued;
		private string message = "Starting.";
		private string? runId;
		private string? lastOutput;
		private DateTimeOffset? finished;

		public LiveRun(string key, string select, string command)
		{
			Key = key;
			Select = select;
			Command = command;
			// The selection already names the components, so a card is complete before the run resolves anything.
			components = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(entry => entry.Split('@')[0]).ToList();
		}

		public string Key { get; }
		public string Select { get; }
		public string Command { get; }
		public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
		public CancellationTokenSource Cancellation { get; } = new();
		public Task? Task { get; set; }
		public AgentRunOutcome? Outcome { get; private set; }

		public void Step(AgentRunStep step)
		{
			lock (gate)
			{
				runId ??= step.RunId;
				phase = step.Phase;
				message = step.Message;
			}
		}

		public void Output(string line)
		{
			lock (gate)
			{
				lastOutput = line;
				output.Enqueue(line);
				while (output.Count > OutputTailLength)
				{
					output.Dequeue();
				}
			}
		}

		public void Finish(AgentRunPhase terminal, string? id, string text, AgentRunOutcome? outcome)
		{
			lock (gate)
			{
				phase = terminal;
				runId = id ?? runId;
				message = text;
				finished = DateTimeOffset.UtcNow;
				Outcome = outcome;
			}
		}

		public bool RequestCancel()
		{
			lock (gate)
			{
				if (finished is not null || Cancellation.IsCancellationRequested)
				{
					return false;
				}
			}

			Cancellation.Cancel();
			return true;
		}

		public IReadOnlyList<string> OutputTail()
		{
			lock (gate)
			{
				return output.ToList();
			}
		}

		public RunCard Snapshot()
		{
			lock (gate)
			{
				return new RunCard(Key, runId, phase, Select, components, Command, message, Started, finished,
					IsLive: finished is null, lastOutput);
			}
		}

		public void Dispose() => Cancellation.Dispose();
	}
}
