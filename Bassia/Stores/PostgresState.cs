namespace Bassia.Stores;

using System.Data.Common;
using System.Runtime.CompilerServices;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Npgsql;
using NpgsqlTypes;

/// <summary>The server's mutable state: heads, mainline's path index and leases, in one store.</summary>
public interface IServerState : IViewStore, IPathIndex, ILeaseStore, IWorkspaceIndex;

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

	/// <summary>
	/// The schema of heads, path index and leases. Migration 1 is the schema as it was created before versioning, kept
	/// verbatim and idempotent (it is also what existing databases are stamped against). Add a step for any later change;
	/// never edit an applied one. Heads and the fencing sequence are primary data: no step may recreate them.
	/// </summary>
	public static readonly SchemaComponent Schema = new("state",
	[
		new SchemaMigration(1, "heads, path index, meta, leases, lease queue and fencing sequence", """
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
			"""),
		// The workspace registry (bassia-c7ba795f): derived from the ws/ and rc/ heads, filled by a backfill on start. Adds only.
		new SchemaMigration(2, "workspace registry: workspaces and the paths their candidates touch", """
			CREATE TABLE bassia_workspaces (id text COLLATE "C" PRIMARY KEY, name text COLLATE "C" NOT NULL, status text NOT NULL, open boolean NOT NULL,
				owner text NOT NULL, parent text, base_seq bigint NOT NULL, created timestamptz NOT NULL, closed timestamptz, head text NOT NULL,
				base text NOT NULL, working text NOT NULL, candidate text, candidate_base text, candidate_working text, candidate_seq bigint);
			CREATE INDEX bassia_workspaces_open ON bassia_workspaces (created, id) WHERE open;
			CREATE INDEX bassia_workspaces_closed ON bassia_workspaces (created DESC, id DESC) WHERE NOT open;
			CREATE INDEX bassia_workspaces_name ON bassia_workspaces (name);
			CREATE TABLE bassia_workspace_paths (path text COLLATE "C" NOT NULL, workspace text COLLATE "C" NOT NULL, PRIMARY KEY (path, workspace));
			CREATE INDEX bassia_workspace_paths_workspace ON bassia_workspace_paths (workspace);
			"""),
	]);

	/// <param name="schema">Migrate the schema to this build's version (the default), or only check that it is usable (<c>bassia-server -migrate verify</c>).</param>
	/// <exception cref="SchemaException">The schema is not usable by this build.</exception>
	public PostgresState(string connectionString, SchemaMode schema = SchemaMode.Migrate)
	{
		source = NpgsqlDataSource.Create(connectionString);
		try
		{
			SchemaStatus = SchemaMigrator.Run(source, Schema, schema);
		}
		catch
		{
			source.Dispose();
			throw;
		}
	}

	/// <summary>What opening the schema found and did.</summary>
	public SchemaStatus SchemaStatus { get; }

	/// <summary>Whether workspaces exist that the registry has never recorded: a database from before it, not backfilled yet.</summary>
	public bool WorkspacesNeedBackfill
	{
		get
		{
			using var connection = source.OpenConnection();
			using var command = new NpgsqlCommand(
				"SELECT EXISTS (SELECT 1 FROM bassia_heads WHERE starts_with(name, 'ws/')) AND NOT EXISTS (SELECT 1 FROM bassia_workspaces)", connection);
			return (bool)command.ExecuteScalar()!;
		}
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

	/// <summary>
	/// The children of a directory as of a seq, by a skip scan rather than reading every version below it. A recursive
	/// query hops along the (path, seq) key from one child to the next: past a file's other versions, and past a whole
	/// subdirectory (to its name + '0', the byte after '/'). A file child is kept when its latest version at the seq is
	/// live; a directory child when some descendant's is, found by an EXISTS that stops at the first. The cost follows
	/// the number of children (plus the history of subtrees deleted since), not the size of the history below.
	/// </summary>
	public async IAsyncEnumerable<PathListing> ListAsync(string dir, long seq, [EnumeratorCancellation] CancellationToken ct = default)
	{
		var (low, high) = Range(dir);
		var directories = new List<PathListing>();
		var files = new List<PathListing>();
		await using (var connection = await source.OpenConnectionAsync(ct))
		{
			await using var command = Command(connection, ListSql, null, ("low", low), ("high", high), ("seq", seq));
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				var child = PathNames.Join(dir.Trim('/'), reader.GetString(0));
				if (reader.GetBoolean(1))
				{
					directories.Add(new PathListing(child, true, null));
				}
				else
				{
					files.Add(new PathListing(child, false, new PathVersion(child, reader.GetInt64(2),
						new Leaf((EntryKind)reader.GetInt32(3), ObjectId.Parse(reader.GetString(4)), reader.GetInt64(5), reader.GetBoolean(6)))));
				}
			}
		}

		foreach (var item in directories.OrderBy(item => item.Path, StringComparer.Ordinal).Concat(files.OrderBy(item => item.Path, StringComparer.Ordinal)))
		{
			yield return item;
		}
	}

	private const string ListSql = """
		WITH RECURSIVE child(path, name, is_dir) AS (
			SELECT f.path, split_part(substr(f.path, length(@low) + 1), '/', 1), strpos(substr(f.path, length(@low) + 1), '/') > 0
			FROM (SELECT path FROM bassia_paths WHERE path >= @low COLLATE "C" AND path < @high COLLATE "C" ORDER BY path LIMIT 1) f
			UNION ALL
			SELECT n.path, split_part(substr(n.path, length(@low) + 1), '/', 1), strpos(substr(n.path, length(@low) + 1), '/') > 0
			FROM child c CROSS JOIN LATERAL (
				SELECT p.path FROM bassia_paths p
				WHERE p.path > c.path AND p.path < @high COLLATE "C"
					AND p.path >= (CASE WHEN c.is_dir THEN @low || c.name || '0' ELSE c.path END) COLLATE "C"
				ORDER BY p.path LIMIT 1) n
		)
		SELECT c.name, c.is_dir, v.seq, v.kind, v.id, v.size, v.exec
		FROM child c
		LEFT JOIN LATERAL (
			SELECT seq, kind, id, size, exec FROM bassia_paths
			WHERE NOT c.is_dir AND path = c.path AND seq <= @seq ORDER BY seq DESC LIMIT 1) v ON true
		WHERE (NOT c.is_dir AND v.kind IS NOT NULL)
			OR (c.is_dir AND EXISTS (
				SELECT 1 FROM bassia_paths d
				WHERE d.path >= (@low || c.name || '/') COLLATE "C" AND d.path < (@low || c.name || '0') COLLATE "C"
					AND d.seq <= @seq AND d.kind IS NOT NULL
					AND NOT EXISTS (SELECT 1 FROM bassia_paths t WHERE t.path = d.path AND t.seq > d.seq AND t.seq <= @seq)))
		""";

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

	// ----- workspace registry -----

	private const string WorkspaceColumns = "id, name, status, owner, parent, base_seq, created, closed, head, base, working, candidate";

	private static WorkspaceSummary ReadWorkspace(DbDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
		reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6),
		reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), ObjectId.Parse(reader.GetString(8)), ObjectId.Parse(reader.GetString(9)),
		ObjectId.Parse(reader.GetString(10)), reader.IsDBNull(11) ? null : ObjectId.Parse(reader.GetString(11)));

	private static async Task<IReadOnlyList<WorkspaceSummary>> WorkspacesAsync(NpgsqlCommand command, CancellationToken ct)
	{
		await using var reader = await command.ExecuteReaderAsync(ct);
		var list = new List<WorkspaceSummary>();
		while (await reader.ReadAsync(ct))
		{
			list.Add(ReadWorkspace(reader));
		}

		return list;
	}

	/// <summary>The compare-and-swap of a head inside a transaction; true when it moved.</summary>
	private static async Task<bool> SwapAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name, ObjectId? expected, ObjectId next, CancellationToken ct) =>
		(expected is { } value
			? await ExecuteAsync(connection, "UPDATE bassia_heads SET head = @next WHERE name = @name AND head = @expected", transaction, ct,
				("next", next.Hex), ("name", name), ("expected", value.Hex))
			: await ExecuteAsync(connection, "INSERT INTO bassia_heads (name, head) VALUES (@name, @next) ON CONFLICT (name) DO NOTHING", transaction, ct,
				("name", name), ("next", next.Hex))) == 1;

	private static async Task<ObjectId?> HeadAsync(NpgsqlConnection connection, string name, CancellationToken ct) =>
		await ScalarAsync(connection, "SELECT head FROM bassia_heads WHERE name = @name", null, ct, ("name", name)) is string head ? ObjectId.Parse(head) : null;

	private static Task<int> UpsertWorkspaceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, WorkspaceSummary summary, CancellationToken ct) =>
		ExecuteAsync(connection, """
			INSERT INTO bassia_workspaces (id, name, status, open, owner, parent, base_seq, created, closed, head, base, working)
			VALUES (@id, @name, @status, @open, @owner, @parent, @base_seq, @created, @closed, @head, @base, @working)
			ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, status = EXCLUDED.status, open = EXCLUDED.open, owner = EXCLUDED.owner, parent = EXCLUDED.parent,
				base_seq = EXCLUDED.base_seq, created = EXCLUDED.created, closed = EXCLUDED.closed, head = EXCLUDED.head, base = EXCLUDED.base, working = EXCLUDED.working
			""", transaction, ct,
			("id", summary.Id), ("name", summary.Name), ("status", summary.Status), ("open", summary.IsOpen), ("owner", summary.Owner), ("parent", summary.Parent),
			("base_seq", summary.BaseSeq), ("created", summary.Created.ToUniversalTime()), ("closed", summary.Closed?.ToUniversalTime()), ("head", summary.Head.Hex),
			("base", summary.Base.Hex), ("working", summary.Working.Hex));

	/// <summary>Sets a workspace's candidate columns and replaces its touched paths (none for a workspace that is not open).</summary>
	private static async Task SetCandidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string workspace, CandidateSummary? candidate, CancellationToken ct)
	{
		var open = await ScalarAsync(connection,
			"UPDATE bassia_workspaces SET candidate = @candidate, candidate_base = @base, candidate_working = @working, candidate_seq = @seq WHERE id = @id RETURNING open",
			transaction, ct, ("candidate", candidate?.Id.Hex), ("base", candidate?.Base.Hex), ("working", candidate?.Working.Hex), ("seq", candidate?.MainSeq), ("id", workspace)) as bool?;
		await ExecuteAsync(connection, "DELETE FROM bassia_workspace_paths WHERE workspace = @id", transaction, ct, ("id", workspace));
		if (open == true && candidate is { Touched.Count: > 0 })
		{
			await ExecuteAsync(connection, "INSERT INTO bassia_workspace_paths (path, workspace) SELECT DISTINCT p, @id FROM unnest(@paths::text[]) AS p", transaction, ct,
				("id", workspace), ("paths", candidate.Touched.ToArray()));
		}
	}

	public async ValueTask<HeadUpdate> AdvanceAsync(ObjectId? expected, WorkspaceSummary next, CancellationToken ct = default)
	{
		var name = HeadName.Workspace(next.Id).Value;
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var transaction = await connection.BeginTransactionAsync(ct);
		if (!await SwapAsync(connection, transaction, name, expected, next.Head, ct))
		{
			await transaction.RollbackAsync(ct);
			return new HeadUpdate(false, await HeadAsync(connection, name, ct));
		}

		await UpsertWorkspaceAsync(connection, transaction, next, ct);
		if (!next.IsOpen)
		{
			await ExecuteAsync(connection, "DELETE FROM bassia_workspace_paths WHERE workspace = @id", transaction, ct, ("id", next.Id));
		}

		await transaction.CommitAsync(ct);
		return new HeadUpdate(true, next.Head);
	}

	public async ValueTask<HeadUpdate> AdvanceCandidateAsync(string workspace, ObjectId? expected, CandidateSummary? next, CancellationToken ct = default)
	{
		var name = HeadName.Candidate(workspace).Value;
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var transaction = await connection.BeginTransactionAsync(ct);
		var moved = next is not null
			? await SwapAsync(connection, transaction, name, expected, next.Id, ct)
			: expected is { } head && await ExecuteAsync(connection, "DELETE FROM bassia_heads WHERE name = @name AND head = @expected", transaction, ct,
				("name", name), ("expected", head.Hex)) == 1;
		if (!moved)
		{
			await transaction.RollbackAsync(ct);
			return new HeadUpdate(false, await HeadAsync(connection, name, ct));
		}

		await SetCandidateAsync(connection, transaction, workspace, next, ct);
		await transaction.CommitAsync(ct);
		return new HeadUpdate(true, next?.Id);
	}

	public async ValueTask<bool> RemoveAsync(string workspace, ObjectId expected, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var transaction = await connection.BeginTransactionAsync(ct);
		if (await ExecuteAsync(connection, "DELETE FROM bassia_heads WHERE name = @name AND head = @expected", transaction, ct,
			("name", HeadName.Workspace(workspace).Value), ("expected", expected.Hex)) != 1)
		{
			await transaction.RollbackAsync(ct);
			return false;
		}

		await ExecuteAsync(connection, "DELETE FROM bassia_workspace_paths WHERE workspace = @id", transaction, ct, ("id", workspace));
		await ExecuteAsync(connection, "DELETE FROM bassia_workspaces WHERE id = @id", transaction, ct, ("id", workspace));
		await transaction.CommitAsync(ct);
		return true;
	}

	public async Task<IReadOnlyList<WorkspaceSummary>> ListAsync(bool open, int limit = int.MaxValue, DateTimeOffset? before = null, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var command = Command(connection, open
			? $"SELECT {WorkspaceColumns} FROM bassia_workspaces WHERE open AND (@before::timestamptz IS NULL OR created < @before) ORDER BY created, id LIMIT @limit"
			: $"SELECT {WorkspaceColumns} FROM bassia_workspaces WHERE NOT open AND (@before::timestamptz IS NULL OR created < @before) ORDER BY created DESC, id DESC LIMIT @limit",
			null, ("before", before?.ToUniversalTime()), ("limit", (long)limit));
		return await WorkspacesAsync(command, ct);
	}

	public async Task<IReadOnlyList<WorkspaceSummary>> GetAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var command = Command(connection, $"SELECT {WorkspaceColumns} FROM bassia_workspaces WHERE id = ANY(@ids::text[])", null, ("ids", ids.Distinct().ToArray()));
		return await WorkspacesAsync(command, ct);
	}

	public async Task<IReadOnlyList<WorkspaceSummary>> FindAsync(string reference, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var command = Command(connection,
			$"SELECT {WorkspaceColumns} FROM bassia_workspaces WHERE name = @reference UNION " +
			$"SELECT {WorkspaceColumns} FROM bassia_workspaces WHERE open AND (starts_with(id, @reference) OR starts_with(name, @reference)) ORDER BY created, id",
			null, ("reference", reference));
		return await WorkspacesAsync(command, ct);
	}

	public async Task<IReadOnlyList<string>> StaleAsync(long seq, string policyPath, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var command = Command(connection, """
			SELECT w.id FROM bassia_workspaces w
			WHERE w.open AND (w.candidate IS NULL OR w.candidate_base <> w.base OR w.candidate_working <> w.working
				OR (w.candidate_seq < @seq AND (
					EXISTS (SELECT 1 FROM bassia_paths p WHERE p.path = @policy AND p.seq > w.candidate_seq AND p.seq <= @seq)
					OR EXISTS (SELECT 1 FROM bassia_workspace_paths t JOIN bassia_paths p ON p.path = t.path
						WHERE t.workspace = w.id AND p.seq > w.candidate_seq AND p.seq <= @seq))))
			ORDER BY w.created, w.id
			""", null, ("seq", seq), ("policy", policyPath));
		await using var reader = await command.ExecuteReaderAsync(ct);
		var stale = new List<string>();
		while (await reader.ReadAsync(ct))
		{
			stale.Add(reader.GetString(0));
		}

		return stale;
	}

	public async Task<IReadOnlyList<(HeadName Name, ObjectId Head)>> UnindexedAsync(CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var command = Command(connection, """
			SELECT h.name, h.head FROM bassia_heads h
			WHERE (starts_with(h.name, 'ws/') AND NOT EXISTS (SELECT 1 FROM bassia_workspaces w WHERE w.id = substr(h.name, 4) AND w.head = h.head))
				OR (starts_with(h.name, 'rc/') AND NOT EXISTS (SELECT 1 FROM bassia_workspaces w WHERE w.id = substr(h.name, 4) AND w.candidate = h.head))
			ORDER BY h.name
			""", null);
		await using var reader = await command.ExecuteReaderAsync(ct);
		var unindexed = new List<(HeadName, ObjectId)>();
		while (await reader.ReadAsync(ct))
		{
			unindexed.Add((new HeadName(reader.GetString(0)), ObjectId.Parse(reader.GetString(1))));
		}

		return unindexed;
	}

	public async ValueTask RecordAsync(WorkspaceSummary summary, CandidateSummary? candidate, CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await using var transaction = await connection.BeginTransactionAsync(ct);
		await UpsertWorkspaceAsync(connection, transaction, summary, ct);
		await SetCandidateAsync(connection, transaction, summary.Id, candidate, ct);
		await transaction.CommitAsync(ct);
	}

	public async ValueTask PruneAsync(CancellationToken ct = default)
	{
		await using var connection = await source.OpenConnectionAsync(ct);
		await ExecuteAsync(connection, """
			WITH gone AS (DELETE FROM bassia_workspaces w WHERE NOT EXISTS (SELECT 1 FROM bassia_heads h WHERE h.name = 'ws/' || w.id) RETURNING id)
			DELETE FROM bassia_workspace_paths t USING gone WHERE t.workspace = gone.id
			""", null, ct);

		// A candidate whose head went without the registry (an older build dropped it) is forgotten, with its paths.
		await ExecuteAsync(connection, """
			WITH gone AS (UPDATE bassia_workspaces w SET candidate = NULL, candidate_base = NULL, candidate_working = NULL, candidate_seq = NULL
				WHERE w.candidate IS NOT NULL AND NOT EXISTS (SELECT 1 FROM bassia_heads h WHERE h.name = 'rc/' || w.id) RETURNING id)
			DELETE FROM bassia_workspace_paths t USING gone WHERE t.workspace = gone.id
			""", null, ct);
	}

	public ValueTask DisposeAsync() => source.DisposeAsync();
}
