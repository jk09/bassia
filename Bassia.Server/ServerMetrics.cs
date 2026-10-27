namespace Bassia.Server;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Bassia.Storage.Cloud;

/// <summary>
/// The server's metrics (meter <c>Bassia.Server</c>), exported over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set,
/// beside ASP.NET Core's own (<c>http.server.request.duration</c>):
/// <list type="bullet">
/// <item><c>bassia.server.request.duration</c> (s): every request by method, route and status - the rate and latency per endpoint;</item>
/// <item><c>bassia.merge_queue.depth</c>: landings on this instance, by state (<c>waiting</c> for a component's slot, <c>landing</c>);
/// summed over instances it is the merge queue; <c>bassia.merge_queue.wait</c> (s): how long a landing waited;</item>
/// <item><c>bassia.lock.decisions</c>: lock table decisions by result (<c>committed</c>, or <c>conflict</c>: the table moved and the
/// run decides again); <c>bassia.lock.waits</c> and <c>bassia.lock.conflicts</c>: waits for a held path and conflicts recorded;</item>
/// <item><c>bassia.git.requests</c> by service (<c>upload-pack</c> is a fetch, <c>receive-pack</c> a push) and status;
/// <c>bassia.git.bytes</c> (By) by service and direction (<c>in</c>, <c>out</c>);</item>
/// <item><c>bassia.db.duration</c> (s) by operation (<c>query</c>, <c>execute</c>, <c>transaction</c>);
/// <c>bassia.storage.duration</c> (s) by store (<c>objects</c>, <c>table</c>) and operation;</item>
/// <item><c>bassia.dashboard.streams</c>: browsers following live changes on this instance.</item>
/// </list>
/// </summary>
internal static class ServerMetrics
{
	public const string MeterName = "Bassia.Server";

	public static readonly Meter Meter = new(MeterName, "1.0");

	public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("bassia.server.request.duration", "s", "Requests by method, route and status.");

	public static readonly UpDownCounter<long> MergeQueueDepth = Meter.CreateUpDownCounter<long>("bassia.merge_queue.depth", description: "Landings on this instance, waiting for a slot or landing.");

	public static readonly Histogram<double> MergeQueueWait = Meter.CreateHistogram<double>("bassia.merge_queue.wait", "s", "How long a landing waited for its components' slots.");

	public static readonly Counter<long> LockDecisions = Meter.CreateCounter<long>("bassia.lock.decisions", description: "Lock table decisions, committed or in conflict with another.");

	public static readonly Counter<long> LockWaits = Meter.CreateCounter<long>("bassia.lock.waits", description: "Waits for a path another run holds.");

	public static readonly Counter<long> LockConflicts = Meter.CreateCounter<long>("bassia.lock.conflicts", description: "Lock conflicts recorded on runs' results.");

	public static readonly Counter<long> GitRequests = Meter.CreateCounter<long>("bassia.git.requests", description: "Git requests through the gateway.");

	public static readonly Counter<long> GitBytes = Meter.CreateCounter<long>("bassia.git.bytes", "By", "Bytes through the git gateway.");

	public static readonly Histogram<double> DatabaseDuration = Meter.CreateHistogram<double>("bassia.db.duration", "s", "Database operations.");

	public static readonly Histogram<double> StorageDuration = Meter.CreateHistogram<double>("bassia.storage.duration", "s", "History storage operations (S3, DynamoDB).");

	public static readonly UpDownCounter<long> DashboardStreams = Meter.CreateUpDownCounter<long>("bassia.dashboard.streams", description: "Browsers following live changes.");

	/// <summary>Times <paramref name="work"/> into <paramref name="histogram"/>, failed or not.</summary>
	public static async Task<T> TimeAsync<T>(Histogram<double> histogram, Func<Task<T>> work, params KeyValuePair<string, object?>[] tags)
	{
		var started = Stopwatch.GetTimestamp();
		try
		{
			return await work();
		}
		finally
		{
			histogram.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
		}
	}

	public static async Task TimeAsync(Histogram<double> histogram, Func<Task> work, params KeyValuePair<string, object?>[] tags) =>
		await TimeAsync(histogram, async () =>
		{
			await work();
			return true;
		}, tags);

	public static KeyValuePair<string, object?> Tag(string name, object? value) => new(name, value);

	/// <summary>The request's route (its endpoint's pattern), or <c>unmatched</c>: a bounded set of values, unlike paths.</summary>
	public static string RouteOf(HttpContext context) =>
		context.GetEndpoint() is RouteEndpoint endpoint ? endpoint.RoutePattern.RawText ?? "unmatched" : "unmatched";

	/// <summary>The git service of a gateway request: from its path, or from <c>info/refs</c>'s query.</summary>
	public static string GitServiceOf(HttpRequest request)
	{
		var path = request.Path.Value ?? "";
		return path.EndsWith("/git-upload-pack", StringComparison.Ordinal) ? "upload-pack"
			: path.EndsWith("/git-receive-pack", StringComparison.Ordinal) ? "receive-pack"
			: request.Query["service"].ToString() is { Length: > 4 } service && service.StartsWith("git-", StringComparison.Ordinal) ? service[4..] + " (refs)"
			: "other";
	}
}

/// <summary>A stream that counts what passes through it, in either direction.</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
	public long Count { get; private set; }

	public override bool CanRead => inner.CanRead;

	public override bool CanSeek => false;

	public override bool CanWrite => inner.CanWrite;

	public override long Length => throw new NotSupportedException();

	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	public override void Flush() => inner.Flush();

	public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

	public override int Read(byte[] buffer, int offset, int count)
	{
		var read = inner.Read(buffer, offset, count);
		Count += read;
		return read;
	}

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		var read = await inner.ReadAsync(buffer, cancellationToken);
		Count += read;
		return read;
	}

	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	public override void Write(byte[] buffer, int offset, int count)
	{
		inner.Write(buffer, offset, count);
		Count += count;
	}

	public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		await inner.WriteAsync(buffer, cancellationToken);
		Count += buffer.Length;
	}

	public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>An object store whose operations are timed (<c>bassia.storage.duration</c>, store <c>objects</c>).</summary>
internal sealed class TimedObjectStore(IObjectStore inner) : IObjectStore
{
	private static KeyValuePair<string, object?>[] Tags(string operation) => [ServerMetrics.Tag("store", "objects"), ServerMetrics.Tag("operation", operation)];

	public string Location => inner.Location;

	public Task PutFileAsync(string key, string path) => ServerMetrics.TimeAsync(ServerMetrics.StorageDuration, () => inner.PutFileAsync(key, path), Tags("put"));

	public Task GetFileAsync(string key, string path) => ServerMetrics.TimeAsync(ServerMetrics.StorageDuration, () => inner.GetFileAsync(key, path), Tags("get"));

	public Task CopyAsync(string fromKey, string toKey) => ServerMetrics.TimeAsync(ServerMetrics.StorageDuration, () => inner.CopyAsync(fromKey, toKey), Tags("copy"));

	public Task<IReadOnlyList<string>> ListAsync(string prefix) => ServerMetrics.TimeAsync(ServerMetrics.StorageDuration, () => inner.ListAsync(prefix), Tags("list"));

	public Task DeleteAsync(string key) => ServerMetrics.TimeAsync(ServerMetrics.StorageDuration, () => inner.DeleteAsync(key), Tags("delete"));
}

/// <summary>A record table whose operations are timed (<c>bassia.storage.duration</c>, store <c>table</c>).</summary>
internal sealed class TimedRecordTable(IRecordTable inner) : IRecordTable
{
	private static KeyValuePair<string, object?>[] Tags(string operation) => [ServerMetrics.Tag("store", "table"), ServerMetrics.Tag("operation", operation)];

	public string Location => inner.Location;

	public Task<CloudRecord?> GetAsync(string partition, string sort) => ServerMetrics.TimeAsync(ServerMetrics.StorageDuration, () => inner.GetAsync(partition, sort), Tags("get"));

	public Task<IReadOnlyList<CloudRecord>> QueryAsync(string partition, string sortPrefix = "") =>
		ServerMetrics.TimeAsync(ServerMetrics.StorageDuration, () => inner.QueryAsync(partition, sortPrefix), Tags("query"));

	public Task WriteAsync(IReadOnlyList<CloudWrite> writes) => ServerMetrics.TimeAsync(ServerMetrics.StorageDuration, () => inner.WriteAsync(writes), Tags("write"));
}
