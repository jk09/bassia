namespace Bassia.Stores;

using System.Data.Common;
using System.Runtime.CompilerServices;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Npgsql;
using NpgsqlTypes;

/// <summary>The server's mutable state: heads, mainline's path index and leases, in one store.</summary>
public interface IServerState : IViewStore, IPathIndex, ILeaseStore;

/// <summary>
/// The mutable state on PostgreSQL (Azure Database for PostgreSQL flexible server, or any PostgreSQL 13+): one row per
/// head advanced with <c>UPDATE ... WHERE head = @expected</c>, the path index as rows keyed by (path, seq) with paths in
/// byte order (<c>COLLATE "C"</c>) so prefix ranges are exact, and leases granted under a transaction-scoped advisory
/// lock with fencing tokens from a sequence. Every write is a compare-and-swap or idempotent, so any number of server
/// nodes can share one database.
/// </summary>
public sealed class PostgresState : IServerState, IAsyncDisposable
{
	/// <summary>The advisory lock that serializes lease grants (any constant unique to Bassia).</summary>
	private const long LeaseLock = 0x6261737369614c4c; // "bassiaLL"

	private readonly NpgsqlDataSource source;

	public PostgresState(string connectionString)
	{
		source = NpgsqlDataSource.Create(connectionString);
		using var connection = source.OpenConnection();
		Execute(connection, """
			CREATE TABLE IF NOT EXISTS bassia_heads (name text COLLATE "C" PRIMARY KEY, head text NOT NULL);
			CREATE TABLE IF NOT EXISTS bassia_paths (path text COLLATE "C" NOT NULL, seq bigint NOT NULL, kind integer, id text, size bigint NOT NULL,
				exec boolean NOT NULL, PRIMARY KEY (path, seq));
			CREATE INDEX IF NOT EXISTS bassia_paths_seq ON bassia_paths (seq);
			CREATE TABLE IF NOT EXISTS bassia_meta (key text PRIMARY KEY, value bigint NOT NULL);
			CREATE TABLE IF NOT EXISTS bassia_leases (id text PRIMARY KEY, prefix text COLLATE "C" NOT NULL, mode integer NOT NULL, owner text NOT NULL,
				run text, token bigint NOT NULL, granted timestamptz NOT NULL, expires timestamptz NOT NULL);
			CREATE TABLE IF NOT EXISTS bassia_lease_queue (ticket bigserial PRIMARY KEY, prefix text COLLATE "C" NOT NULL, mode integer NOT NULL,
				owner text NOT NULL, alive_until timestamptz NOT NULL);
			CREATE SEQUENCE IF NOT EXISTS bassia_fencing;
			""");
	}

	public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

	private static void Execute(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null, params (string Name, object? Value)[] parameters)
	{
		using var command = Command(connection, sql, transaction, parameters);
		command.ExecuteNonQuery();
	}

	private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction, CancellationToken ct, params (string Name, object? Value)[] parameters)
	{
		await using var command = Command(connection, sql, transaction, parameters);
		return await command.ExecuteNonQueryAsync(ct);
	}

	private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction, params (string Name, object? Value)[] parameters)
	{
		var command = new NpgsqlCommand(sql, connection, transaction);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value ?? DBNull.Value);
		}

		return command;
	}

	private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction, CancellationToken ct,
		params (string Name, object? Value)[] parameters)
	{
		await using var command = Command(connection, sql, transaction, parameters);
		var value = await command.ExecuteScalarAsync(ct);
		return value is DBNull ? null : value;
	}

	// ----- heads -----

	public async ValueTask<ObjectId?> GetHeadAsync(HeadName name, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		return await ScalarAsync(connection, "SELECT head FROM bassia_heads WHERE name = @name", null, ct, ("name", name.Value)) is string head
			? ObjectId.Parse(head)
			: null;
	}

	public async ValueTask<HeadUpdate> AdvanceHeadAsync(HeadName name, ObjectId? expected, ObjectId next, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		var changed = expected is { } value
			? await ExecuteAsync(connection, "UPDATE bassia_heads SET head = @next WHERE name = @name AND head = @expected", null, ct,
				("next", next.Hex), ("name", name.Value), ("expected", value.Hex))
			: await ExecuteAsync(connection, "INSERT INTO bassia_heads (name, head) VALUES (@name, @next) ON CONFLICT (name) DO NOTHING", null, ct,
				("name", name.Value), ("next", next.Hex));
		if (changed == 1)
		{
			return new HeadUpdate(true, next);
		}

		var current = await ScalarAsync(connection, "SELECT head FROM bassia_heads WHERE name = @name", null, ct, ("name", name.Value)) as string;
		return new HeadUpdate(false, current is null ? null : ObjectId.Parse(current));
	}

	public async IAsyncEnumerable<(HeadName Name, ObjectId Head)> ListHeadsAsync(string prefix = "", [EnumeratorCancellation] CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var command = Command(connection, "SELECT name, head FROM bassia_heads WHERE starts_with(name, @prefix) ORDER BY name", null, ("prefix", prefix));
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			yield return (new HeadName(reader.GetString(0)), ObjectId.Parse(reader.GetString(1)));
		}
	}

	public async ValueTask<bool> DeleteHeadAsync(HeadName name, ObjectId expected, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		return await ExecuteAsync(connection, "DELETE FROM bassia_heads WHERE name = @name AND head = @expected", null, ct,
			("name", name.Value), ("expected", expected.Hex)) == 1;
	}

	// ----- path index -----

	private const string VersionColumns = "path, seq, kind, id, size, exec";

	private static PathVersion ReadVersion(DbDataReader reader) => new(reader.GetString(0), reader.GetInt64(1),
		reader.IsDBNull(2) ? null : new Leaf((EntryKind)reader.GetInt32(2), ObjectId.Parse(reader.GetString(3)), reader.GetInt64(4), reader.GetBoolean(5)));

	private static (string Low, string High) Range(string prefix)
	{
		prefix = prefix.Trim('/');
		return prefix.Length == 0 ? ("", "￿") : (prefix + "/", prefix + "0");
	}

	public async ValueTask<PathVersion?> GetAsync(string path, long seq, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var command = Command(connection, $"SELECT {VersionColumns} FROM bassia_paths WHERE path = @path AND seq <= @seq ORDER BY seq DESC LIMIT 1", null,
			("path", path), ("seq", seq));
		await using var reader = await command.ExecuteReaderAsync(ct);
		return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
	}

	/// <summary>The paths in one statement per batch: each path's latest version at or before the seq, by the (path, seq) key.</summary>
	public const int LookupBatch = 10_000;

	public async ValueTask<IReadOnlyList<PathVersion?>> GetManyAsync(IReadOnlyList<string> paths, long seq, CancellationToken ct = default)
	{
		var versions = new PathVersion?[paths.Count];
		await using var connection = await source.OpenConnectionAsync(ct);
		for (var start = 0; start < paths.Count; start += LookupBatch)
		{
			var batch = paths.Skip(start).Take(LookupBatch).ToArray();
			await using var command = Command(connection,
				$"SELECT q.n, v.path, v.seq, v.kind, v.id, v.size, v.exec FROM unnest(@paths::text[]) WITH ORDINALITY AS q(path, n) " +
				"CROSS JOIN LATERAL (SELECT path, seq, kind, id, size, exec FROM bassia_paths p WHERE p.path = q.path COLLATE \"C\" AND p.seq <= @seq " +
				"ORDER BY p.seq DESC LIMIT 1) v", null, ("paths", batch), ("seq", seq));
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				var at = (int)reader.GetInt64(0) - 1;
				versions[start + at] = new PathVersion(reader.GetString(1), reader.GetInt64(2),
					reader.IsDBNull(3) ? null : new Leaf((EntryKind)reader.GetInt32(3), ObjectId.Parse(reader.GetString(4)), reader.GetInt64(5), reader.GetBoolean(6)));
			}
		}

		return versions;
	}

	public async IAsyncEnumerable<PathListing> ListAsync(string dir, long seq, [EnumeratorCancellation] CancellationToken ct = default)
	{
		var (low, high) = Range(dir);
		var versions = new List<PathVersion>();
		await using (var connection = await source.OpenConnectionAsync(ct))
		{
			await using var command = Command(connection,
				$"SELECT DISTINCT ON (path) {VersionColumns} FROM bassia_paths WHERE path >= @low AND path < @high AND seq <= @seq ORDER BY path, seq DESC", null,
				("low", low), ("high", high), ("seq", seq));
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				versions.Add(ReadVersion(reader));
			}
		}

		foreach (var item in PathIndexListing.Children(dir, versions))
		{
			yield return item;
		}
	}

	public async IAsyncEnumerable<PathVersion> ChangedSinceAsync(string prefix, long afterSeq, [EnumeratorCancellation] CancellationToken ct = default)
	{
		var (low, high) = Range(prefix);
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var command = Command(connection,
			// The latest version among those after the seq is the path's latest overall: only rows after it are read.
			$"SELECT DISTINCT ON (path) {VersionColumns} FROM bassia_paths WHERE ((path >= @low AND path < @high) OR path = @exact) AND seq > @after " +
			"ORDER BY path, seq DESC", null,
			("low", low), ("high", high), ("exact", prefix.Trim('/')), ("after", afterSeq));
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			yield return ReadVersion(reader);
		}
	}

	public async ValueTask ApplyAsync(long seq, IReadOnlyList<PathChange> changes, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var transaction = await connection.BeginTransactionAsync(ct);
		// Serialize appliers: the indexed seq is read and written under the row lock.
		await ExecuteAsync(connection, "INSERT INTO bassia_meta (key, value) VALUES ('indexed_seq', 0) ON CONFLICT (key) DO NOTHING", transaction, ct);
		var indexed = (long)(await ScalarAsync(connection, "SELECT value FROM bassia_meta WHERE key = 'indexed_seq' FOR UPDATE", transaction, ct))!;
		if (seq <= indexed)
		{
			return;
		}

		if (changes.Count > 0)
		{
			await using var writer = await connection.BeginBinaryImportAsync("COPY bassia_paths (path, seq, kind, id, size, exec) FROM STDIN (FORMAT BINARY)", ct);
			foreach (var change in changes.DistinctBy(change => change.Path))
			{
				await writer.StartRowAsync(ct);
				await writer.WriteAsync(change.Path, NpgsqlDbType.Text, ct);
				await writer.WriteAsync(seq, NpgsqlDbType.Bigint, ct);
				if (change.Leaf is { } leaf)
				{
					await writer.WriteAsync((int)leaf.Kind, NpgsqlDbType.Integer, ct);
					await writer.WriteAsync(leaf.Id.Hex, NpgsqlDbType.Text, ct);
					await writer.WriteAsync(leaf.Size, NpgsqlDbType.Bigint, ct);
					await writer.WriteAsync(leaf.Executable, NpgsqlDbType.Boolean, ct);
				}
				else
				{
					await writer.WriteNullAsync(ct);
					await writer.WriteNullAsync(ct);
					await writer.WriteAsync(0L, NpgsqlDbType.Bigint, ct);
					await writer.WriteAsync(false, NpgsqlDbType.Boolean, ct);
				}
			}

			await writer.CompleteAsync(ct);
		}

		await ExecuteAsync(connection, "UPDATE bassia_meta SET value = @seq WHERE key = 'indexed_seq'", transaction, ct, ("seq", seq));
		await transaction.CommitAsync(ct);
	}

	public async ValueTask<long> IndexedSeqAsync(CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		return await ScalarAsync(connection, "SELECT value FROM bassia_meta WHERE key = 'indexed_seq'", null, ct) is long value ? value : 0;
	}

	// ----- leases -----

	private const string LeaseColumns = "id, prefix, mode, owner, run, token, granted, expires";

	private static Lease ReadLease(DbDataReader reader) => new(reader.GetString(0), reader.GetString(1), (LeaseMode)reader.GetInt32(2), reader.GetString(3),
		reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7));

	private static async Task<List<Lease>> LiveLeasesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateTimeOffset now, CancellationToken ct)
	{
		await using var command = Command(connection, $"SELECT {LeaseColumns} FROM bassia_leases WHERE expires > @now ORDER BY prefix", transaction, ("now", now));
		await using var reader = await command.ExecuteReaderAsync(ct);
		var leases = new List<Lease>();
		while (await reader.ReadAsync(ct))
		{
			leases.Add(ReadLease(reader));
		}

		return leases;
	}

	public async ValueTask<LeaseResult> AcquireAsync(LeaseRequest request, CancellationToken ct = default)
	{
		var deadline = Clock() + request.Wait;
		long? ticket = null;
		try
		{
			while (true)
			{
				var now = Clock();
				await using (var connection = await source.OpenConnectionAsync(ct))
				await using (var transaction = await connection.BeginTransactionAsync(ct))
				{
					await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(@lock)", transaction, ct, ("lock", LeaseLock));
					await ExecuteAsync(connection, "DELETE FROM bassia_leases WHERE expires <= @now", transaction, ct, ("now", now));
					await ExecuteAsync(connection, "DELETE FROM bassia_lease_queue WHERE alive_until <= @now", transaction, ct, ("now", now));
					var blocking = LeaseRules.Blocking(request, await LiveLeasesAsync(connection, transaction, now, ct));
					var ahead = new List<LeaseRequest>();
					await using (var queue = Command(connection, "SELECT prefix, mode, owner FROM bassia_lease_queue WHERE @ticket::bigint IS NULL OR ticket < @ticket ORDER BY ticket",
						transaction, ("ticket", ticket)))
					await using (var reader = await queue.ExecuteReaderAsync(ct))
					{
						while (await reader.ReadAsync(ct))
						{
							ahead.Add(new LeaseRequest(reader.GetString(0), (LeaseMode)reader.GetInt32(1), reader.GetString(2), TimeSpan.Zero));
						}
					}

					if (blocking.Count == 0 && !ahead.Any(waiting => LeaseRules.Conflicts(request, waiting)))
					{
						var token = (long)(await ScalarAsync(connection, "SELECT nextval('bassia_fencing')", transaction, ct))!;
						var lease = new Lease(Guid.NewGuid().ToString("N"), PathNames.Prefix(request.Prefix), request.Mode, request.Owner, request.Run, token, now,
							now + request.Ttl);
						await ExecuteAsync(connection, $"INSERT INTO bassia_leases ({LeaseColumns}) VALUES (@id, @prefix, @mode, @owner, @run, @token, @granted, @expires)",
							transaction, ct, ("id", lease.Id), ("prefix", lease.Prefix), ("mode", (int)lease.Mode), ("owner", lease.Owner), ("run", lease.Run),
							("token", lease.Token), ("granted", lease.Granted), ("expires", lease.Expires));
						if (ticket is not null)
						{
							await ExecuteAsync(connection, "DELETE FROM bassia_lease_queue WHERE ticket = @ticket", transaction, ct, ("ticket", ticket));
							ticket = null;
						}

						await transaction.CommitAsync(ct);
						return new LeaseResult(lease, []);
					}

					if (now >= deadline)
					{
						await transaction.CommitAsync(ct);
						return new LeaseResult(null, blocking);
					}

					var aliveUntil = now + TimeSpan.FromSeconds(5);
					if (ticket is null)
					{
						ticket = (long)(await ScalarAsync(connection,
							"INSERT INTO bassia_lease_queue (prefix, mode, owner, alive_until) VALUES (@prefix, @mode, @owner, @alive) RETURNING ticket", transaction, ct,
							("prefix", PathNames.Prefix(request.Prefix)), ("mode", (int)request.Mode), ("owner", request.Owner), ("alive", aliveUntil)))!;
					}
					else
					{
						await ExecuteAsync(connection, "UPDATE bassia_lease_queue SET alive_until = @alive WHERE ticket = @ticket", transaction, ct,
							("alive", aliveUntil), ("ticket", ticket));
					}

					await transaction.CommitAsync(ct);
				}

				await Task.Delay(100, ct);
			}
		}
		finally
		{
			if (ticket is not null)
			{
				await using var connection = await source.OpenConnectionAsync(CancellationToken.None);
				await ExecuteAsync(connection, "DELETE FROM bassia_lease_queue WHERE ticket = @ticket", null, CancellationToken.None, ("ticket", ticket));
			}
		}
	}

	public async ValueTask<Lease?> RenewAsync(string id, long token, TimeSpan ttl, CancellationToken ct = default)
	{
		var now = Clock();
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var command = Command(connection,
			$"UPDATE bassia_leases SET expires = @expires WHERE id = @id AND token = @token AND expires > @now RETURNING {LeaseColumns}", null,
			("expires", now + ttl), ("id", id), ("token", token), ("now", now));
		await using var reader = await command.ExecuteReaderAsync(ct);
		return await reader.ReadAsync(ct) ? ReadLease(reader) : null;
	}

	public async ValueTask ReleaseAsync(string id, long token, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await ExecuteAsync(connection, "DELETE FROM bassia_leases WHERE id = @id AND token = @token", null, ct, ("id", id), ("token", token));
	}

	public async ValueTask<IReadOnlyList<Lease>> ListAsync(string? prefix = null, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		var leases = await LiveLeasesAsync(connection, null, Clock(), ct);
		return prefix is null ? leases : leases.Where(lease => PathNames.Overlap(lease.Prefix.TrimEnd('/'), prefix.Trim('/'))).ToList();
	}

	public async ValueTask<Lease?> GetAsync(string id, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var command = Command(connection, $"SELECT {LeaseColumns} FROM bassia_leases WHERE id = @id AND expires > @now", null, ("id", id), ("now", Clock()));
		await using var reader = await command.ExecuteReaderAsync(ct);
		return await reader.ReadAsync(ct) ? ReadLease(reader) : null;
	}

	public ValueTask DisposeAsync() => source.DisposeAsync();
}
