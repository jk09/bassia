namespace Bassia.Server;

using System.Threading.Channels;
using Bassia.Core.Version;

/// <summary>
/// The server's background reconciliation: a de-duplicated queue of workspaces to reconcile, fed by the events this
/// node sees - a workspace head it advanced (a snapshot, a sync, a recorded landing) and a landing it performed (the
/// workspaces that landing made stale). Every event reaches exactly one node, so each node reconciles what its own
/// events affect, and the candidates it stores (heads in PostgreSQL) serve every node. At start a node catches up on
/// open workspaces whose candidate is missing or stale.
/// </summary>
internal sealed class ReconcileQueue : IAsyncDisposable
{
	private const string CatchUp = "\0catch-up";
	private const string LandedPrefix = "\0landed:";

	private readonly Reconciler reconciler;
	private readonly Channel<string> channel = Channel.CreateUnbounded<string>();
	private readonly HashSet<string> queued = [];
	private readonly Lock gate = new();
	private readonly CancellationTokenSource stopping = new();
	private readonly List<Task> workers = [];
	private int busy;
	private int delayed;

	public ReconcileQueue(Reconciler reconciler) => this.reconciler = reconciler;

	/// <summary>How many workspaces this node reconciles at once.</summary>
	public int Workers { get; init; } = 4;

	/// <summary>
	/// Claims a workspace among all nodes for the length of its reconciliation; null when another node has it. Without
	/// it (one node) nothing is claimed.
	/// </summary>
	public Func<string, CancellationToken, Task<IAsyncDisposable?>>? Claim { get; init; }

	/// <summary>
	/// How often this node looks for open workspaces that are not reconciled, whoever's event made them stale: what
	/// picks up the work of a node that died with its queue. Null: only at start.
	/// </summary>
	public TimeSpan? CatchUpEvery { get; init; }

	/// <summary>Errors are reported here and the queue goes on (the next event reconciles again).</summary>
	public Action<string>? Log { get; init; }

	/// <summary>Whether nothing is queued or being reconciled (tests wait for it).</summary>
	public bool Idle
	{
		get
		{
			lock (gate)
			{
				return queued.Count == 0 && Volatile.Read(ref busy) == 0 && Volatile.Read(ref delayed) == 0;
			}
		}
	}

	public void Start()
	{
		if (workers.Count == 0)
		{
			for (var index = 0; index < Math.Max(1, Workers); index++)
			{
				workers.Add(Task.Run(RunAsync));
			}

			if (CatchUpEvery is { } interval)
			{
				workers.Add(Task.Run(() => CatchUpAsync(interval)));
			}
		}

		Enqueue(CatchUp);
	}

	/// <summary>Looks again for workspaces that are not reconciled (after a reconnect, or on the periodic check).</summary>
	public void LookAgain() => Enqueue(CatchUp);

	private async Task CatchUpAsync(TimeSpan interval)
	{
		try
		{
			while (true)
			{
				await Task.Delay(interval, stopping.Token);
				Enqueue(CatchUp);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	/// <summary>Another node is reconciling this workspace - perhaps as it was a moment ago: look at it again shortly.</summary>
	private async Task RetryAsync(string item)
	{
		Interlocked.Increment(ref delayed);
		try
		{
			await Task.Delay(200, stopping.Token);
			Enqueue(item);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			Interlocked.Decrement(ref delayed);
		}
	}

	/// <summary>Reconcile this workspace soon; a workspace already waiting is not queued twice.</summary>
	public void Workspace(string id) => Enqueue(id);

	/// <summary>Reconcile the workspaces the landing at <paramref name="seq"/> made stale, and the landed one (its candidate goes).</summary>
	public void Landed(long seq, string workspace)
	{
		Enqueue(workspace);
		Enqueue(LandedPrefix + seq);
	}

	private void Enqueue(string item)
	{
		lock (gate)
		{
			if (!queued.Add(item))
			{
				return;
			}
		}

		channel.Writer.TryWrite(item);
	}

	private async Task RunAsync()
	{
		var ct = stopping.Token;
		try
		{
			await foreach (var item in channel.Reader.ReadAllAsync(ct))
			{
				Interlocked.Increment(ref busy);
				lock (gate)
				{
					queued.Remove(item);
				}

				try
				{
					if (item == CatchUp)
					{
						var main = await reconciler.MainSeqAsync(ct);
						foreach (var id in await reconciler.AffectedAsync(main, ct))
						{
							Enqueue(id);
						}
					}
					else if (item.StartsWith(LandedPrefix, StringComparison.Ordinal))
					{
						foreach (var id in await reconciler.AffectedAsync(long.Parse(item[LandedPrefix.Length..]), ct))
						{
							Enqueue(id);
						}
					}
					else if (Claim is null)
					{
						await reconciler.ReconcileAsync(item, ct);
					}
					else
					{
						await using var claim = await Claim(item, ct);
						if (claim is null)
						{
							_ = RetryAsync(item);
						}
						else
						{
							await reconciler.ReconcileAsync(item, ct);
						}
					}
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex)
				{
					Log?.Invoke($"bassia-server: reconciling {(item.StartsWith('\0') ? item[1..] : $"workspace {item}")} failed: {ex.Message}");
				}
				finally
				{
					Interlocked.Decrement(ref busy);
				}
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
		}
	}

	public async ValueTask DisposeAsync()
	{
		await stopping.CancelAsync();
		channel.Writer.TryComplete();
		foreach (var worker in workers)
		{
			try
			{
				await worker;
			}
			catch (OperationCanceledException)
			{
			}
		}

		stopping.Dispose();
	}
}
