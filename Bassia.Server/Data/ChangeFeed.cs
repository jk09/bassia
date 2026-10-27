namespace Bassia.Server.Data;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading.Channels;

/// <summary>A change the dashboards follow: its kind, what it is about (a record, a job, a component) and the components it touches.</summary>
internal sealed record Change(long Seq, string Kind, string Subject, IReadOnlyList<string> Components);

/// <summary>
/// What changed, for the dashboards' live updates, across server instances: the stores add a row to the <c>changes</c>
/// table in the transaction of the write - a record's new version (<c>run</c>, <c>integration</c>), a lock decision
/// (<c>lock</c>), a job registered or finished (<c>job</c>) and its output (<c>log</c>), a landing (<c>land</c>) - and
/// every instance polls the table and hands the new rows to the browsers connected to it. A row of a transaction that
/// commits after a later one was read is still seen: each poll reads back a window behind the newest row. Rows older
/// than <see cref="Retention"/> are pruned.
/// </summary>
internal sealed class ChangeFeed(ServerDatabase database)
{
	public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

	public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

	/// <summary>How many rows behind the newest seen one each poll reads again, for those that committed late.</summary>
	private const long Window = 1000;

	private readonly ConcurrentDictionary<Channel<Change>, bool> subscribers = new();
	private readonly HashSet<long> seen = [];
	private long newest = -1;
	private DateTimeOffset pruned = DateTimeOffset.MinValue;

	/// <summary>Adds a change in the transaction of the write it describes.</summary>
	public static Task AddAsync(DbConnection connection, DbTransaction? transaction, string kind, string subject, IEnumerable<string> components) =>
		ServerDatabase.ExecuteAsync(connection, transaction, "INSERT INTO changes (kind, subject, components, created) VALUES (@kind, @subject, @components, @created);",
			("@kind", kind), ("@subject", subject), ("@components", string.Join(",", components.Distinct(StringComparer.Ordinal))), ("@created", ServerDatabase.Now()));

	public async Task AddAsync(string kind, string subject, IEnumerable<string> components)
	{
		await using var connection = await database.ConnectAsync();
		await AddAsync(connection, null, kind, subject, components);
	}

	/// <summary>
	/// The changes from now on, until the subscription is disposed. A subscriber that falls behind loses the oldest of
	/// the changes it has not read: a dashboard reloads what changed, so the latest changes are what count.
	/// </summary>
	public Subscription Subscribe()
	{
		var channel = Channel.CreateBounded<Change>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
		subscribers[channel] = true;
		return new Subscription(channel, () =>
		{
			subscribers.TryRemove(channel, out _);
			channel.Writer.TryComplete();
		});
	}

	public int Subscribers => subscribers.Count;

	/// <summary>Polls the table until <paramref name="stopping"/>, handing new rows to the subscribers.</summary>
	public async Task RunAsync(CancellationToken stopping)
	{
		while (!stopping.IsCancellationRequested)
		{
			try
			{
				await PollAsync();
			}
			catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
			{
				// The database is away for a moment; the next poll catches up.
			}

			try
			{
				await Task.Delay(PollInterval, stopping);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	/// <summary>One poll: the rows not seen yet, to every subscriber; the first poll only learns where the table is.</summary>
	public async Task<IReadOnlyList<Change>> PollAsync()
	{
		var first = newest < 0;
		if (first)
		{
			newest = (await database.QueryAsync("SELECT COALESCE(MAX(seq), 0) FROM changes;", reader => Convert.ToInt64(reader.GetValue(0))))[0];
		}

		var floor = Math.Max(0, newest - Window);
		var rows = await database.QueryAsync("SELECT seq, kind, subject, components FROM changes WHERE seq > @floor ORDER BY seq;",
			reader => new Change(Convert.ToInt64(reader.GetValue(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries)),
			("@floor", floor));
		var fresh = rows.Where(row => seen.Add(row.Seq) && !first).ToList();
		if (rows.Count > 0)
		{
			newest = Math.Max(newest, rows[^1].Seq);
			seen.RemoveWhere(seq => seq <= newest - Window);
		}

		foreach (var change in fresh)
		{
			foreach (var subscriber in subscribers.Keys)
			{
				subscriber.Writer.TryWrite(change);
			}
		}

		if (DateTimeOffset.UtcNow - pruned > TimeSpan.FromMinutes(1))
		{
			pruned = DateTimeOffset.UtcNow;
			await database.ExecuteAsync("DELETE FROM changes WHERE created < @before;", ("@before", ServerDatabase.Now(DateTimeOffset.UtcNow - Retention)));
		}

		return fresh;
	}

	internal sealed class Subscription(Channel<Change> channel, Action dispose) : IDisposable
	{
		public ChannelReader<Change> Reader => channel.Reader;

		public void Dispose() => dispose();
	}
}
