namespace Bassia.Stores;

using System.Data.Common;
using System.Runtime.CompilerServices;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Microsoft.Data.Sqlite;

/// <summary>
/// The reference SQL implementation of the mutable state: one row per head (advanced with
/// <c>UPDATE ... WHERE head = @expected</c>), the path index as rows keyed by (path, seq), and leases with a monotonic
/// fencing counter and a FIFO queue of waiters. One SQLite file serves a local monorepo (shared by every process on the
/// machine) and a single-node server.
/// </summary>
public sealed class SqliteState : IViewStore, IPathIndex, ILeaseStore
{
	private readonly string connectionString;

	public SqliteState(string file)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
		connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = true, DefaultTimeout = 30 }.ToString();
		using var connection = Open();
		Execute(connection, """
			PRAGMA journal_mode = WAL;
			CREATE TABLE IF NOT EXISTS heads (name TEXT PRIMARY KEY, head TEXT NOT NULL);
			CREATE TABLE IF NOT EXISTS paths (path TEXT NOT NULL, seq INTEGER NOT NULL, kind INTEGER, id TEXT, size INTEGER NOT NULL, exec INTEGER NOT NULL,
				PRIMARY KEY (path, seq));
			CREATE INDEX IF NOT EXISTS paths_seq ON paths (seq);
			CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
			CREATE TABLE IF NOT EXISTS leases (id TEXT PRIMARY KEY, prefix TEXT NOT NULL, mode INTEGER NOT NULL, owner TEXT NOT NULL, run TEXT,
				token INTEGER NOT NULL, granted INTEGER NOT NULL, expires INTEGER NOT NULL);
			CREATE TABLE IF NOT EXISTS lease_queue (ticket INTEGER PRIMARY KEY AUTOINCREMENT, prefix TEXT NOT NULL, mode INTEGER NOT NULL, owner TEXT NOT NULL,
				alive_until INTEGER NOT NULL);
			""");
	}

	public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

	private SqliteConnection Open()
	{
		var connection = new SqliteConnection(connectionString);
		connection.Open();
		Execute(connection, "PRAGMA busy_timeout = 30000;");
		return connection;
	}

	private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
	{
		using var command = Command(connection, sql, parameters);
		command.ExecuteNonQuery();
	}

	private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
	{
		var command = connection.CreateCommand();
		command.CommandText = sql;
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value ?? DBNull.Value);
		}

		return command;
	}

	private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
	{
		using var command = Command(connection, sql, parameters);
		var value = command.ExecuteScalar();
		return value is DBNull ? null : value;
	}

	/// <summary>A write transaction that takes the database's write lock up front, so check-then-write cannot interleave.</summary>
	private static SqliteTransaction BeginWrite(SqliteConnection connection)
	{
		var transaction = connection.BeginTransaction(deferred: false);
		return transaction;
	}

	// ----- heads -----

	public ValueTask<ObjectId?> GetHeadAsync(HeadName name, CancellationToken ct = default)
	{
		using var connection = Open();
		return ValueTask.FromResult(Scalar(connection, "SELECT head FROM heads WHERE name = $name", ("$name", name.Value)) is string head ? ObjectId.Parse(head) : (ObjectId?)null);
	}

	public ValueTask<HeadUpdate> AdvanceHeadAsync(HeadName name, ObjectId? expected, ObjectId next, CancellationToken ct = default)
	{
		using var connection = Open();
		using var transaction = BeginWrite(connection);
		int changed;
		if (expected is { } value)
		{
			using var update = Command(connection, "UPDATE heads SET head = $next WHERE name = $name AND head = $expected",
				("$next", next.Hex), ("$name", name.Value), ("$expected", value.Hex));
			changed = update.ExecuteNonQuery();
		}
		else
		{
			using var insert = Command(connection, "INSERT OR IGNORE INTO heads (name, head) VALUES ($name, $next)", ("$name", name.Value), ("$next", next.Hex));
			changed = insert.ExecuteNonQuery();
		}

		var current = Scalar(connection, "SELECT head FROM heads WHERE name = $name", ("$name", name.Value)) as string;
		transaction.Commit();
		return ValueTask.FromResult(new HeadUpdate(changed == 1, current is null ? null : ObjectId.Parse(current)));
	}

	public async IAsyncEnumerable<(HeadName Name, ObjectId Head)> ListHeadsAsync(string prefix = "", [EnumeratorCancellation] CancellationToken ct = default)
	{
		var result = new List<(HeadName, ObjectId)>();
		using (var connection = Open())
		{
			using var command = Command(connection, "SELECT name, head FROM heads WHERE substr(name, 1, length($prefix)) = $prefix ORDER BY name", ("$prefix", prefix));
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				result.Add((new HeadName(reader.GetString(0)), ObjectId.Parse(reader.GetString(1))));
			}
		}

		foreach (var item in result)
		{
			yield return item;
		}

		await Task.CompletedTask;
	}

	public ValueTask<bool> DeleteHeadAsync(HeadName name, ObjectId expected, CancellationToken ct = default)
	{
		using var connection = Open();
		using var command = Command(connection, "DELETE FROM heads WHERE name = $name AND head = $expected", ("$name", name.Value), ("$expected", expected.Hex));
		return ValueTask.FromResult(command.ExecuteNonQuery() == 1);
	}

	// ----- path index -----

	private const string LatestAtOrBefore = "seq = (SELECT MAX(q.seq) FROM paths q WHERE q.path = p.path AND q.seq <= $seq)";

	private static PathVersion ReadVersion(DbDataReader reader) => new(reader.GetString(0), reader.GetInt64(1),
		reader.IsDBNull(2) ? null : new Leaf((EntryKind)reader.GetInt32(2), ObjectId.Parse(reader.GetString(3)), reader.GetInt64(4), reader.GetInt64(5) != 0));

	public ValueTask<PathVersion?> GetAsync(string path, long seq, CancellationToken ct = default)
	{
		using var connection = Open();
		using var command = Command(connection, "SELECT path, seq, kind, id, size, exec FROM paths WHERE path = $path AND seq <= $seq ORDER BY seq DESC LIMIT 1",
			("$path", path), ("$seq", seq));
		using var reader = command.ExecuteReader();
		return ValueTask.FromResult(reader.Read() ? ReadVersion(reader) : null);
	}

	private static (string Low, string High) Range(string prefix)
	{
		prefix = prefix.Trim('/');
		return prefix.Length == 0 ? ("", "￿") : (prefix + "/", prefix + "0");
	}

	public async IAsyncEnumerable<PathListing> ListAsync(string dir, long seq, [EnumeratorCancellation] CancellationToken ct = default)
	{
		var (low, high) = Range(dir);
		var versions = new List<PathVersion>();
		using (var connection = Open())
		{
			using var command = Command(connection,
				$"SELECT path, seq, kind, id, size, exec FROM paths p WHERE path >= $low AND path < $high AND {LatestAtOrBefore}",
				("$low", low), ("$high", high), ("$seq", seq));
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				versions.Add(ReadVersion(reader));
			}
		}

		foreach (var item in PathIndexListing.Children(dir, versions))
		{
			yield return item;
		}

		await Task.CompletedTask;
	}

	public async IAsyncEnumerable<PathVersion> ChangedSinceAsync(string prefix, long afterSeq, [EnumeratorCancellation] CancellationToken ct = default)
	{
		var (low, high) = Range(prefix);
		var versions = new List<PathVersion>();
		using (var connection = Open())
		{
			using var command = Command(connection,
				"SELECT path, seq, kind, id, size, exec FROM paths p WHERE ((path >= $low AND path < $high) OR path = $exact) AND seq > $after " +
				"AND seq = (SELECT MAX(q.seq) FROM paths q WHERE q.path = p.path) ORDER BY path",
				("$low", low), ("$high", high), ("$exact", prefix.Trim('/')), ("$after", afterSeq));
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				versions.Add(ReadVersion(reader));
			}
		}

		foreach (var version in versions)
		{
			yield return version;
		}

		await Task.CompletedTask;
	}

	public ValueTask ApplyAsync(long seq, IReadOnlyList<PathChange> changes, CancellationToken ct = default)
	{
		using var connection = Open();
		using var transaction = BeginWrite(connection);
		var indexed = Scalar(connection, "SELECT value FROM meta WHERE key = 'indexed_seq'") is string text ? long.Parse(text) : 0;
		if (seq <= indexed)
		{
			return ValueTask.CompletedTask;
		}

		using (var insert = Command(connection, "INSERT OR REPLACE INTO paths (path, seq, kind, id, size, exec) VALUES ($path, $seq, $kind, $id, $size, $exec)"))
		{
			var path = insert.Parameters.Add("$path", SqliteType.Text);
			insert.Parameters.AddWithValue("$seq", seq);
			var kind = insert.Parameters.Add("$kind", SqliteType.Integer);
			var id = insert.Parameters.Add("$id", SqliteType.Text);
			var size = insert.Parameters.Add("$size", SqliteType.Integer);
			var exec = insert.Parameters.Add("$exec", SqliteType.Integer);
			foreach (var change in changes)
			{
				path.Value = change.Path;
				kind.Value = change.Leaf is { } leaf ? (int)leaf.Kind : DBNull.Value;
				id.Value = change.Leaf?.Id.Hex ?? (object)DBNull.Value;
				size.Value = change.Leaf?.Size ?? 0;
				exec.Value = change.Leaf?.Executable == true ? 1 : 0;
				insert.ExecuteNonQuery();
			}
		}

		Execute(connection, "INSERT OR REPLACE INTO meta (key, value) VALUES ('indexed_seq', $seq)", ("$seq", seq.ToString()));
		transaction.Commit();
		return ValueTask.CompletedTask;
	}

	public ValueTask<long> IndexedSeqAsync(CancellationToken ct = default)
	{
		using var connection = Open();
		return ValueTask.FromResult(Scalar(connection, "SELECT value FROM meta WHERE key = 'indexed_seq'") is string text ? long.Parse(text) : 0L);
	}

	// ----- leases -----

	private static Lease ReadLease(DbDataReader reader) => new(reader.GetString(0), reader.GetString(1), (LeaseMode)reader.GetInt32(2), reader.GetString(3),
		reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
		DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)));

	private const string LeaseColumns = "id, prefix, mode, owner, run, token, granted, expires";

	private static List<Lease> LiveLeases(SqliteConnection connection, long now)
	{
		using var command = Command(connection, $"SELECT {LeaseColumns} FROM leases WHERE expires > $now ORDER BY prefix", ("$now", now));
		using var reader = command.ExecuteReader();
		var leases = new List<Lease>();
		while (reader.Read())
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
				var nowMs = now.ToUnixTimeMilliseconds();
				using (var connection = Open())
				using (var transaction = BeginWrite(connection))
				{
					Execute(connection, "DELETE FROM leases WHERE expires <= $now", ("$now", nowMs));
					Execute(connection, "DELETE FROM lease_queue WHERE alive_until <= $now", ("$now", nowMs));
					var blocking = LeaseRules.Blocking(request, LiveLeases(connection, nowMs));
					var ahead = new List<LeaseRequest>();
					using (var queue = Command(connection, "SELECT prefix, mode, owner FROM lease_queue WHERE $ticket IS NULL OR ticket < $ticket ORDER BY ticket",
						("$ticket", ticket)))
					using (var reader = queue.ExecuteReader())
					{
						while (reader.Read())
						{
							ahead.Add(new LeaseRequest(reader.GetString(0), (LeaseMode)reader.GetInt32(1), reader.GetString(2), TimeSpan.Zero));
						}
					}

					if (blocking.Count == 0 && !ahead.Any(waiting => LeaseRules.Conflicts(request, waiting)))
					{
						var token = (Scalar(connection, "SELECT value FROM meta WHERE key = 'fencing'") is string text ? long.Parse(text) : 0) + 1;
						Execute(connection, "INSERT OR REPLACE INTO meta (key, value) VALUES ('fencing', $value)", ("$value", token.ToString()));
						var lease = new Lease(Guid.NewGuid().ToString("N"), PathNames.Prefix(request.Prefix), request.Mode, request.Owner, request.Run, token, now,
							now + request.Ttl);
						Execute(connection, $"INSERT INTO leases ({LeaseColumns}) VALUES ($id, $prefix, $mode, $owner, $run, $token, $granted, $expires)",
							("$id", lease.Id), ("$prefix", lease.Prefix), ("$mode", (int)lease.Mode), ("$owner", lease.Owner), ("$run", lease.Run),
							("$token", lease.Token), ("$granted", nowMs), ("$expires", lease.Expires.ToUnixTimeMilliseconds()));
						if (ticket is not null)
						{
							Execute(connection, "DELETE FROM lease_queue WHERE ticket = $ticket", ("$ticket", ticket));
							ticket = null;
						}

						transaction.Commit();
						return new LeaseResult(lease, []);
					}

					if (now >= deadline)
					{
						transaction.Commit();
						return new LeaseResult(null, blocking);
					}

					var aliveUntil = (now + TimeSpan.FromSeconds(5)).ToUnixTimeMilliseconds();
					if (ticket is null)
					{
						Execute(connection, "INSERT INTO lease_queue (prefix, mode, owner, alive_until) VALUES ($prefix, $mode, $owner, $alive)",
							("$prefix", PathNames.Prefix(request.Prefix)), ("$mode", (int)request.Mode), ("$owner", request.Owner), ("$alive", aliveUntil));
						ticket = (long)Scalar(connection, "SELECT last_insert_rowid()")!;
					}
					else
					{
						Execute(connection, "UPDATE lease_queue SET alive_until = $alive WHERE ticket = $ticket", ("$alive", aliveUntil), ("$ticket", ticket));
					}

					transaction.Commit();
				}

				await Task.Delay(50, ct);
			}
		}
		finally
		{
			if (ticket is not null)
			{
				using var connection = Open();
				Execute(connection, "DELETE FROM lease_queue WHERE ticket = $ticket", ("$ticket", ticket));
			}
		}
	}

	public ValueTask<Lease?> RenewAsync(string id, long token, TimeSpan ttl, CancellationToken ct = default)
	{
		var now = Clock();
		using var connection = Open();
		using var transaction = BeginWrite(connection);
		using var update = Command(connection, "UPDATE leases SET expires = $expires WHERE id = $id AND token = $token AND expires > $now",
			("$expires", (now + ttl).ToUnixTimeMilliseconds()), ("$id", id), ("$token", token), ("$now", now.ToUnixTimeMilliseconds()));
		var changed = update.ExecuteNonQuery();
		transaction.Commit();
		return changed == 1 ? GetAsync(id, ct) : ValueTask.FromResult<Lease?>(null);
	}

	public ValueTask ReleaseAsync(string id, long token, CancellationToken ct = default)
	{
		using var connection = Open();
		Execute(connection, "DELETE FROM leases WHERE id = $id AND token = $token", ("$id", id), ("$token", token));
		return ValueTask.CompletedTask;
	}

	public ValueTask<IReadOnlyList<Lease>> ListAsync(string? prefix = null, CancellationToken ct = default)
	{
		using var connection = Open();
		var leases = LiveLeases(connection, Clock().ToUnixTimeMilliseconds());
		return ValueTask.FromResult<IReadOnlyList<Lease>>(prefix is null
			? leases
			: leases.Where(lease => PathNames.Overlap(lease.Prefix.TrimEnd('/'), prefix.Trim('/'))).ToList());
	}

	public ValueTask<Lease?> GetAsync(string id, CancellationToken ct = default)
	{
		using var connection = Open();
		using var command = Command(connection, $"SELECT {LeaseColumns} FROM leases WHERE id = $id AND expires > $now",
			("$id", id), ("$now", Clock().ToUnixTimeMilliseconds()));
		using var reader = command.ExecuteReader();
		return ValueTask.FromResult(reader.Read() ? ReadLease(reader) : null);
	}
}
