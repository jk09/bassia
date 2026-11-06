namespace Bassia.Agents;

/// <summary>
/// Snapshots a workspace folder while an agent works in it, once its files have been quiet for a while: every
/// snapshot moves the workspace head, which is how the server (bassia-f162a7fd) learns of the edits and reconciles the
/// workspace with mainline before it lands. Changes under <c>.bassia</c> folders and markers are ignored; snapshots
/// never overlap, and a failed one is reported and retried at the next change.
/// </summary>
internal sealed class AutoSnapshot : IAsyncDisposable
{
	private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

	private readonly Func<CancellationToken, Task> snapshot;
	private readonly TimeSpan quiet;
	private readonly Action<string>? log;
	private readonly FileSystemWatcher watcher;
	private readonly CancellationTokenSource stopping = new();
	private readonly Task loop;
	private long lastChange;
	private int dirty;

	private AutoSnapshot(string root, TimeSpan quiet, Func<CancellationToken, Task> snapshot, Action<string>? log)
	{
		this.snapshot = snapshot;
		this.quiet = quiet;
		this.log = log;
		watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
		watcher.Changed += OnChange;
		watcher.Created += OnChange;
		watcher.Deleted += OnChange;
		watcher.Renamed += OnChange;
		watcher.EnableRaisingEvents = true;
		loop = RunAsync();
	}

	/// <summary>How many snapshots it took.</summary>
	public int Snapshots { get; private set; }

	/// <summary>Starts watching <paramref name="root"/>; null when <paramref name="quiet"/> is zero (turned off).</summary>
	public static AutoSnapshot? Start(string root, TimeSpan quiet, Func<CancellationToken, Task> snapshot, Action<string>? log = null) =>
		quiet <= TimeSpan.Zero || !Directory.Exists(root) ? null : new AutoSnapshot(root, quiet, snapshot, log);

	private void OnChange(object sender, FileSystemEventArgs change)
	{
		var relative = change.Name ?? "";
		if (relative.Split('/', '\\').Any(part => part.StartsWith(".bassia", StringComparison.Ordinal)))
		{
			return;
		}

		Interlocked.Exchange(ref lastChange, Environment.TickCount64);
		Interlocked.Exchange(ref dirty, 1);
	}

	private async Task RunAsync()
	{
		var ct = stopping.Token;
		try
		{
			while (!ct.IsCancellationRequested)
			{
				await Task.Delay(Tick, ct);
				if (Volatile.Read(ref dirty) == 0 || Environment.TickCount64 - Interlocked.Read(ref lastChange) < quiet.TotalMilliseconds)
				{
					continue;
				}

				Interlocked.Exchange(ref dirty, 0);
				try
				{
					await snapshot(ct);
					Snapshots++;
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					log?.Invoke($"bassia: automatic snapshot failed: {ex.Message}");
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	/// <summary>Stops watching and waits for a snapshot in progress; the run takes its own final snapshot afterwards.</summary>
	public async ValueTask DisposeAsync()
	{
		watcher.EnableRaisingEvents = false;
		await stopping.CancelAsync();
		await loop;
		watcher.Dispose();
		stopping.Dispose();
	}
}
