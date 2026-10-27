namespace Bassia.Server.Data;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;

/// <summary>
/// The server's SQL database: PostgreSQL for deployments, SQLite for a single instance and tests. Plain SQL that both
/// understand (upserts with <c>ON CONFLICT</c>, compare-and-swap as a conditional <c>UPDATE</c>); the schema is versioned
/// in <c>schema_version</c> and brought up to date by <see cref="MigrateAsync"/> at start.
/// </summary>
internal sealed class ServerDatabase
{
	private readonly Func<DbConnection> connect;

	private ServerDatabase(string provider, Func<DbConnection> connect)
	{
		Provider = provider;
		this.connect = connect;
	}

	public string Provider { get; }

	public bool IsPostgres => Provider == "postgres";

	public static ServerDatabase Open(string provider, string connection)
	{
		if (provider == "postgres")
		{
			var source = NpgsqlDataSource.Create(connection);
			return new ServerDatabase(provider, source.CreateConnection);
		}

		var builder = new SqliteConnectionStringBuilder(connection) { DefaultTimeout = 30 };
		if (builder.DataSource is { Length: > 0 } file && file != ":memory:")
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
		}

		return new ServerDatabase(provider, () => new SqliteConnection(builder.ToString()));
	}

	/// <summary>Now, as the database stores times: UTC, sortable as text.</summary>
	public static string Now(DateTimeOffset? time = null) => (time ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

	public async Task<DbConnection> ConnectAsync()
	{
		var connection = connect();
		await connection.OpenAsync();
		if (!IsPostgres)
		{
			await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;");
		}

		return connection;
	}

	/// <summary>Runs <paramref name="work"/> in one transaction, committed when it returns.</summary>
	public async Task<T> TransactAsync<T>(Func<DbConnection, DbTransaction, Task<T>> work)
	{
		var started = System.Diagnostics.Stopwatch.GetTimestamp();
		try
		{
			await using var connection = await ConnectAsync();
			await using var transaction = await connection.BeginTransactionAsync();
			var result = await work(connection, transaction);
			await transaction.CommitAsync();
			return result;
		}
		finally
		{
			ServerMetrics.DatabaseDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, ServerMetrics.Tag("operation", "transaction"));
		}
	}

	public async Task<int> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
	{
		await using var connection = await ConnectAsync();
		return await ExecuteAsync(connection, null, sql, parameters);
	}

	public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<DbDataReader, T> map, params (string Name, object? Value)[] parameters)
	{
		await using var connection = await ConnectAsync();
		return await QueryAsync(connection, null, sql, map, parameters);
	}

	public static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
	{
		await using var command = Command(connection, transaction, sql, parameters);
		return await ServerMetrics.TimeAsync(ServerMetrics.DatabaseDuration, () => command.ExecuteNonQueryAsync(), ServerMetrics.Tag("operation", "execute"));
	}

	public static async Task<IReadOnlyList<T>> QueryAsync<T>(DbConnection connection, DbTransaction? transaction, string sql, Func<DbDataReader, T> map,
		params (string Name, object? Value)[] parameters)
	{
		await using var command = Command(connection, transaction, sql, parameters);
		return await ServerMetrics.TimeAsync<IReadOnlyList<T>>(ServerMetrics.DatabaseDuration, async () =>
		{
			await using var reader = await command.ExecuteReaderAsync();
			var rows = new List<T>();
			while (await reader.ReadAsync())
			{
				rows.Add(map(reader));
			}

			return rows;
		}, ServerMetrics.Tag("operation", "query"));
	}

	private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
	{
		var command = connection.CreateCommand();
		command.CommandText = sql;
		command.Transaction = transaction;
		foreach (var (name, value) in parameters)
		{
			var parameter = command.CreateParameter();
			parameter.ParameterName = name;
			parameter.Value = value ?? DBNull.Value;
			command.Parameters.Add(parameter);
		}

		return command;
	}

	public static string? NullableText(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

	// ----- schema -----

	/// <summary>The schema, one migration per version; never edit one that shipped, add the next.</summary>
	private IReadOnlyList<string> Migrations =>
	[
		$"""
		CREATE TABLE records (ref_base TEXT NOT NULL, lineage INTEGER NOT NULL, file_name TEXT NOT NULL, content TEXT NOT NULL,
			message TEXT NOT NULL, created TEXT NOT NULL, PRIMARY KEY (ref_base, lineage));
		CREATE TABLE lock_entries (run_id TEXT PRIMARY KEY, entry TEXT NOT NULL, started TEXT NOT NULL);
		CREATE TABLE lock_version (id INTEGER PRIMARY KEY, version {BigInt} NOT NULL);
		INSERT INTO lock_version (id, version) VALUES (1, 0);
		CREATE TABLE jobs (id TEXT PRIMARY KEY, kind TEXT NOT NULL, owner TEXT NOT NULL, info TEXT NOT NULL, components TEXT NOT NULL,
			heartbeat TEXT NOT NULL, stop INTEGER NOT NULL, finished TEXT, log TEXT NOT NULL);
		CREATE TABLE task_cache (cache_key TEXT PRIMARY KEY, size {BigInt} NOT NULL, created TEXT NOT NULL);
		CREATE TABLE identities (provider TEXT NOT NULL, subject TEXT NOT NULL, user_id TEXT NOT NULL, PRIMARY KEY (provider, subject));
		CREATE TABLE grants (principal TEXT NOT NULL, component TEXT NOT NULL, permission TEXT NOT NULL, PRIMARY KEY (principal, component));
		CREATE TABLE group_members (group_name TEXT NOT NULL, user_id TEXT NOT NULL, PRIMARY KEY (group_name, user_id));
		CREATE TABLE refresh_tokens (token_hash TEXT PRIMARY KEY, user_id TEXT NOT NULL, expires TEXT NOT NULL);
		CREATE TABLE idempotency (idem_key TEXT NOT NULL, user_id TEXT NOT NULL, status INTEGER NOT NULL, body TEXT NOT NULL, created TEXT NOT NULL,
			PRIMARY KEY (idem_key, user_id));
		CREATE TABLE audit (seq {Serial}, time TEXT NOT NULL, user_id TEXT NOT NULL, action TEXT NOT NULL, target TEXT NOT NULL,
			detail TEXT NOT NULL, remote TEXT NOT NULL);
		CREATE TABLE land_queue (component TEXT PRIMARY KEY, holder TEXT NOT NULL, expires TEXT NOT NULL);
		""",
		$"""
		CREATE TABLE changes (seq {Serial}, kind TEXT NOT NULL, subject TEXT NOT NULL, components TEXT NOT NULL, created TEXT NOT NULL);
		CREATE INDEX changes_created ON changes (created);
		"""
	];

	private string BigInt => IsPostgres ? "BIGINT" : "INTEGER";

	private string Serial => IsPostgres ? "BIGSERIAL PRIMARY KEY" : "INTEGER PRIMARY KEY AUTOINCREMENT";

	/// <summary>
	/// Applies the migrations the database does not have yet, each in its own transaction. On PostgreSQL an advisory lock
	/// holds back the other tasks of a deployment that start at the same moment, so each migration runs once.
	/// </summary>
	public async Task MigrateAsync()
	{
		if (!IsPostgres)
		{
			await ApplyMigrationsAsync();
			return;
		}

		await using var holder = await ConnectAsync();
		await ExecuteAsync(holder, null, $"SELECT pg_advisory_lock({MigrationLock});");
		try
		{
			await ApplyMigrationsAsync();
		}
		finally
		{
			await ExecuteAsync(holder, null, $"SELECT pg_advisory_unlock({MigrationLock});");
		}
	}

	/// <summary>The advisory lock migrations take on PostgreSQL ("bassia" in ASCII).</summary>
	private const long MigrationLock = 0x626173736961;

	private async Task ApplyMigrationsAsync()
	{
		await ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");
		var current = (await QueryAsync("SELECT MAX(version) FROM schema_version;", reader => reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)))[0];
		for (var version = current + 1; version <= Migrations.Count; version++)
		{
			var migration = Migrations[version - 1];
			await TransactAsync(async (connection, transaction) =>
			{
				foreach (var statement in migration.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					await ExecuteAsync(connection, transaction, statement + ";");
				}

				await ExecuteAsync(connection, transaction, "INSERT INTO schema_version (version) VALUES (@version);", ("@version", version));
				return true;
			});
		}
	}
}
