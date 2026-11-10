namespace Bassia.LoadTest;

using System.Collections.Concurrent;
using System.Diagnostics;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Microsoft.AspNetCore.Http;

/// <summary>What some work asked of the server's stores: PostgreSQL statements and blob-store calls.</summary>
internal sealed class Cost
{
	public long Statements;

	public long BlobCalls;
}

/// <summary>
/// Counts, inside the server the harness hosts, what each request costs: every PostgreSQL command (Npgsql reports each
/// as an activity) and every blob-store call made while the request runs are charged to it, and the totals go back to
/// the client in two response headers. Work outside a request (the reconcile queue) is charged to <see cref="Background"/>.
/// </summary>
internal sealed class ServerProbe : IDisposable
{
	public const string StatementsHeader = "X-Bassia-Statements";
	public const string BlobCallsHeader = "X-Bassia-Blob-Calls";

	private static readonly AsyncLocal<Cost?> Current = new();

	private readonly ActivityListener listener;

	public ServerProbe()
	{
		listener = new ActivityListener
		{
			ShouldListenTo = source => source.Name == "Npgsql",
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
			// Stopped, not started: the continuation of a command runs in the context of the request that issued it.
			ActivityStopped = activity =>
			{
				if (IsStatement(activity))
				{
					Interlocked.Increment(ref (Current.Value ?? Background).Statements);
				}
			}
		};
		ActivitySource.AddActivityListener(listener);
	}

	/// <summary>Work no request asked for: the server's background reconciliation.</summary>
	public Cost Background { get; } = new();

	/// <summary>A command or a COPY, not the opening of a physical connection.</summary>
	private static bool IsStatement(Activity activity)
	{
		foreach (var tag in activity.TagObjects)
		{
			if (tag.Key is "db.query.text" or "db.statement" or "db.operation.name")
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The middleware: charges the request's work to it and reports the totals in the response headers.</summary>
	public async Task InvokeAsync(HttpContext context, RequestDelegate next)
	{
		var cost = new Cost();
		Current.Value = cost;
		context.Response.OnStarting(() =>
		{
			context.Response.Headers[StatementsHeader] = Interlocked.Read(ref cost.Statements).ToString();
			context.Response.Headers[BlobCallsHeader] = Interlocked.Read(ref cost.BlobCalls).ToString();
			return Task.CompletedTask;
		});
		await next(context);
	}

	/// <summary>Charges work done in this process, outside a request, to a cost of its own.</summary>
	public static async Task<(T Result, Cost Cost)> MeasureAsync<T>(Func<Task<T>> work)
	{
		var cost = new Cost();
		Current.Value = cost;
		try
		{
			return (await work(), cost);
		}
		finally
		{
			Current.Value = null;
		}
	}

	internal static void BlobCalls(ServerProbe probe, int count) => Interlocked.Add(ref (Current.Value ?? probe.Background).BlobCalls, count);

	public void Dispose() => listener.Dispose();
}

/// <summary>
/// The server's blob store with its calls counted as a cloud blob store would be called: one per object asked about,
/// read, written or signed.
/// </summary>
internal sealed class ProbedObjectStore(IObjectStore inner, ServerProbe probe) : IObjectStore
{
	public ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		ServerProbe.BlobCalls(probe, ids.Count);
		return inner.HasAsync(ids, ct);
	}

	public ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default)
	{
		ServerProbe.BlobCalls(probe, 1);
		return inner.StatAsync(id, ct);
	}

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default)
	{
		ServerProbe.BlobCalls(probe, 1);
		return inner.ReadAsync(id, range, ct);
	}

	public ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
	{
		ServerProbe.BlobCalls(probe, 1);
		return inner.PutAsync(id, bytes, ct);
	}

	public ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default) => inner.BeginUploadAsync(id, length, ct);
}

/// <summary>One operation as it is being measured on the client: the requests it sent and what the server reported for them.</summary>
internal sealed class OperationScope
{
	public long Requests;

	public long Statements;

	public long BlobCalls;

	public bool Counted;
}

/// <summary>The client's HTTP handler: adds each reply's reported cost to the operation in flight.</summary>
internal sealed class ClientProbe(HttpMessageHandler inner) : DelegatingHandler(inner)
{
	internal static readonly AsyncLocal<OperationScope?> Scope = new();

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
	{
		var response = await base.SendAsync(request, ct);
		if (Scope.Value is { } scope)
		{
			Interlocked.Increment(ref scope.Requests);
			if (Header(response, ServerProbe.StatementsHeader) is { } statements)
			{
				scope.Counted = true;
				Interlocked.Add(ref scope.Statements, statements);
				Interlocked.Add(ref scope.BlobCalls, Header(response, ServerProbe.BlobCallsHeader) ?? 0);
			}
		}

		return response;
	}

	private static long? Header(HttpResponseMessage response, string name) =>
		response.Headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), out var value) ? value : null;
}

/// <summary>The samples of one operation.</summary>
internal sealed class OperationSamples(string name)
{
	private readonly Lock gate = new();
	private readonly List<double> milliseconds = [];
	private long first = long.MaxValue;
	private long last;

	public string Name => name;

	public long Requests;

	public long Statements;

	public long BlobCalls;

	public long Errors;

	/// <summary>Whether the server reported costs for it (a hosted server does; a remote one does not).</summary>
	public bool Counted;

	public void Add(long started, long ended, OperationScope? scope)
	{
		lock (gate)
		{
			milliseconds.Add(Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds);
			first = Math.Min(first, started);
			last = Math.Max(last, ended);
			if (scope is not null)
			{
				Requests += scope.Requests;
				Statements += scope.Statements;
				BlobCalls += scope.BlobCalls;
				Counted |= scope.Counted;
			}
		}
	}

	public void Add(double elapsedMilliseconds, Cost? cost = null)
	{
		var ended = Stopwatch.GetTimestamp();
		lock (gate)
		{
			milliseconds.Add(elapsedMilliseconds);
			first = Math.Min(first, ended - (long)(elapsedMilliseconds / 1000 * Stopwatch.Frequency));
			last = Math.Max(last, ended);
			if (cost is not null)
			{
				Statements += cost.Statements;
				BlobCalls += cost.BlobCalls;
				Counted = true;
			}
		}
	}

	public OperationResult Result()
	{
		lock (gate)
		{
			var sorted = milliseconds.Order().ToList();
			var count = sorted.Count;
			double At(double quantile) => count == 0 ? 0 : sorted[Math.Min(count - 1, (int)Math.Ceiling(quantile * count) - 1)];
			var seconds = count == 0 ? 0 : Stopwatch.GetElapsedTime(first, last).TotalSeconds;
			return new OperationResult(name, count, Errors, seconds > 0 ? count / seconds : 0, At(0.50), At(0.95), At(0.99), count == 0 ? 0 : sorted[^1],
				count == 0 ? 0 : (double)Requests / count,
				Counted && count > 0 ? (double)Statements / count : null,
				Counted && count > 0 ? (double)BlobCalls / count : null);
		}
	}
}

/// <summary>Times operations and keeps their samples by name.</summary>
internal sealed class Recorder
{
	private readonly ConcurrentDictionary<string, OperationSamples> operations = new(StringComparer.Ordinal);

	public OperationSamples Of(string name) => operations.GetOrAdd(name, key => new OperationSamples(key));

	/// <summary>
	/// Runs and times <paramref name="work"/> as one sample of <paramref name="name"/>, or of the name
	/// <paramref name="outcome"/> gives its result (a landing is recorded by its outcome). A failure is counted and rethrown.
	/// </summary>
	public async Task<T> TimeAsync<T>(string name, Func<Task<T>> work, Func<T, string>? outcome = null)
	{
		var scope = new OperationScope();
		ClientProbe.Scope.Value = scope;
		var started = Stopwatch.GetTimestamp();
		try
		{
			var result = await work();
			Of(outcome?.Invoke(result) ?? name).Add(started, Stopwatch.GetTimestamp(), scope);
			return result;
		}
		catch
		{
			Interlocked.Increment(ref Of(name).Errors);
			throw;
		}
		finally
		{
			ClientProbe.Scope.Value = null;
		}
	}

	public IReadOnlyList<OperationResult> Results() => operations.Values.Select(samples => samples.Result()).ToList();
}
