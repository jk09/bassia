namespace Bassia.Ui;

using Bassia.Integration;

/// <summary>
/// The integration the frontend is running, as the control panel draws it: an immutable snapshot, so the renderer
/// never reads a record the background task is still writing. <see cref="Snapshot"/> is null until the integration
/// has recorded itself; the panel keeps drawing the plan until then.
/// </summary>
internal sealed record IntegrationCard(
	IntegrationRecord? Snapshot,
	string Message,
	string? ActiveComponent,
	string? ActiveRunId,
	bool IsLive,
	DateTimeOffset Started,
	DateTimeOffset? Finished,
	string? LastOutput,
	IntegrationOutcome? Outcome)
{
	public TimeSpan Elapsed => (Finished ?? DateTimeOffset.UtcNow) - Started;
}

/// <summary>
/// Runs one integration at a time in the background over the same <see cref="IntegrationRunner.RunAsync"/> the CLI
/// calls, so a slow semantic merge never blocks the frontend. One at a time because two integrations over the same
/// components would only race each other for the same conflicts.
/// </summary>
internal sealed class IntegrationSupervisor : IDisposable
{
	private readonly Func<IReadOnlyList<RunMetadata>, IReadOnlyList<ComponentIntegration>, string, IntegrationContext, Task<IntegrationOutcome>> start;
	private readonly Lock gate = new();
	private readonly Queue<string> output = new();
	private IntegrationCard? card;
	private CancellationTokenSource? cancellation;
	private Task task = Task.CompletedTask;
	private bool settled;

	public IntegrationSupervisor(Func<IReadOnlyList<RunMetadata>, IReadOnlyList<ComponentIntegration>, string, IntegrationContext, Task<IntegrationOutcome>> start) =>
		this.start = start;

	public IntegrationCard? Current
	{
		get
		{
			lock (gate)
			{
				return card;
			}
		}
	}

	public bool IsLive => Current?.IsLive ?? false;

	/// <summary>Starts the integration in the background; false when one is already running.</summary>
	public bool Start(IReadOnlyList<RunMetadata> runs, IReadOnlyList<ComponentIntegration> plan, string resolver)
	{
		lock (gate)
		{
			if (card?.IsLive == true)
			{
				return false;
			}

			cancellation?.Dispose();
			cancellation = new CancellationTokenSource();
			output.Clear();
			card = new IntegrationCard(null, "Starting.", null, null, IsLive: true, DateTimeOffset.UtcNow, null, null, null);
			var token = cancellation.Token;
			task = Task.Run(() => ExecuteAsync(runs, plan, resolver, token));
			return true;
		}
	}

	private async Task ExecuteAsync(IReadOnlyList<RunMetadata> runs, IReadOnlyList<ComponentIntegration> plan, string resolver, CancellationToken token)
	{
		try
		{
			var context = new IntegrationContext
			{
				Cancellation = token,
				OnProgress = progress => Update(current => current with
				{
					Snapshot = progress.Snapshot,
					Message = progress.Message,
					ActiveComponent = progress.Component,
					ActiveRunId = progress.RunId
				}),
				OnOutput = Output
			};

			var outcome = await start(runs, plan, resolver, context);
			Update(current => current with
			{
				Snapshot = outcome.Record,
				Message = outcome.Message,
				ActiveComponent = null,
				ActiveRunId = null,
				IsLive = false,
				Finished = DateTimeOffset.UtcNow,
				Outcome = outcome
			});
		}
		catch (OperationCanceledException)
		{
			Update(current => current with { Message = "Cancelled before the integration was recorded; nothing changed.", IsLive = false, Finished = DateTimeOffset.UtcNow });
		}
		catch (Exception ex)
		{
			// Whatever the failure, the card must end: a card left live would refuse every later integration and make
			// quitting wait on it, and a task that faults would throw into whoever awaits it.
			Update(current => current with { Message = ex.Message, IsLive = false, Finished = DateTimeOffset.UtcNow });
		}
		finally
		{
			lock (gate)
			{
				settled = true;
			}
		}
	}

	private void Update(Func<IntegrationCard, IntegrationCard> change)
	{
		lock (gate)
		{
			if (card is not null)
			{
				card = change(card);
			}
		}
	}

	private void Output(string line)
	{
		lock (gate)
		{
			output.Enqueue(line);
			while (output.Count > RunSupervisor.OutputTailLength)
			{
				output.Dequeue();
			}

			if (card is not null)
			{
				card = card with { LastOutput = line };
			}
		}
	}

	/// <summary>The resolver's output so far, as far as it was kept.</summary>
	public IReadOnlyList<string> OutputTail()
	{
		lock (gate)
		{
			return output.ToList();
		}
	}

	/// <summary>Stops the running integration; a resolver at work is killed with its process tree.</summary>
	public bool Cancel()
	{
		lock (gate)
		{
			if (card?.IsLive != true || cancellation is null || cancellation.IsCancellationRequested)
			{
				return false;
			}

			cancellation.Cancel();
			return true;
		}
	}

	/// <summary>True once (per call) after the integration ended, so the frontend reloads the records exactly then.</summary>
	public bool ConsumeSettled()
	{
		lock (gate)
		{
			var was = settled;
			settled = false;
			return was;
		}
	}

	public Task WhenSettledAsync()
	{
		lock (gate)
		{
			return task;
		}
	}

	public void Dispose()
	{
		lock (gate)
		{
			cancellation?.Dispose();
		}
	}
}
