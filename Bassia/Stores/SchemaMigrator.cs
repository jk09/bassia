namespace Bassia.Stores;

using Npgsql;

/// <summary>What a store does about its schema when it opens.</summary>
public enum SchemaMode
{
	/// <summary>Bring the schema up to this build's version (creating, baselining or migrating it), under a lock.</summary>
	Migrate,

	/// <summary>Change nothing: refuse to open unless the schema is at this build's version or a newer one this build can use.</summary>
	Verify
}

/// <summary>What opening a component's schema found and did.</summary>
public enum SchemaOutcome
{
	/// <summary>Already at this build's version: nothing was done.</summary>
	Current,

	/// <summary>The schema was empty and was created from migration 1.</summary>
	Created,

	/// <summary>Tables made before versioning (by <c>CREATE TABLE IF NOT EXISTS</c>) matched migration 1 and were stamped as version 1.</summary>
	Baselined,

	/// <summary>Migrations after the stored version were applied.</summary>
	Migrated,

	/// <summary>The schema is newer than this build, but still declares itself usable by it (an expand step during a rolling deploy).</summary>
	NewerCompatible,

	/// <summary>A cache whose schema this build cannot use was dropped and created again.</summary>
	Rebuilt
}

/// <summary>
/// One forward step of a component's schema. Steps are numbered 1, 2, 3... and run in order, each once, in its own
/// transaction together with the version bump (PostgreSQL DDL is transactional), so a step that fails leaves the
/// schema at the version before it.
/// </summary>
/// <param name="Version">The schema version this step brings the component to.</param>
/// <param name="Description">One line for the history table and the log.</param>
/// <param name="Sql">The statements. Migration 1 must be idempotent (<c>IF NOT EXISTS</c>): it is also run over tables made before versioning.</param>
public sealed record SchemaMigration(int Version, string Description, string Sql)
{
	/// <summary>
	/// The oldest build (by the latest schema version it knows) that can still run once this step is applied. Leave it
	/// unset for an <em>expand</em> step - one that only adds what older builds ignore (a table, a nullable column or one
	/// with a default, an index) - so older nodes keep running during a rolling deploy. Set it on a <em>contract</em>
	/// step that removes or changes what an older build still uses: those builds then refuse to start.
	/// </summary>
	public int? MinimumCode { get; init; }

	/// <summary>
	/// False for a statement PostgreSQL refuses inside a transaction (<c>CREATE INDEX CONCURRENTLY</c>). Such a step
	/// must be idempotent: a crash between the statement and the version bump runs it again.
	/// </summary>
	public bool Transactional { get; init; } = true;
}

/// <summary>
/// A store's part of a PostgreSQL schema, versioned on its own: <c>state</c> (heads, path index, leases) and <c>trees</c>
/// share a schema on a server and in a local monorepo, while a clone's schema holds only <c>trees</c>.
/// </summary>
public sealed record SchemaComponent(string Name, IReadOnlyList<SchemaMigration> Migrations)
{
	/// <summary>
	/// Statements that drop every object of the component. Set only for a cache - data that can be fetched again - which
	/// is then dropped and created afresh whenever this build cannot use what it finds, instead of refusing to open.
	/// </summary>
	public string? DropSql { get; init; }

	/// <summary>The latest version this build knows.</summary>
	public int Latest => Migrations[^1].Version;

	public bool IsCache => DropSql is not null;
}

/// <summary>A schema this build will not open, with what to do about it.</summary>
public sealed class SchemaException(string message) : Exception(message);

/// <param name="Version">The component's version in the database once the call returns.</param>
/// <param name="CompatibleFrom">The oldest build (by its latest known version) that the stored schema accepts.</param>
/// <param name="Applied">The migration versions this call ran (1 for a baseline), in order.</param>
public sealed record SchemaStatus(string Component, int Version, int CompatibleFrom, SchemaOutcome Outcome, IReadOnlyList<int> Applied);

/// <summary>
/// Versions and migrates the PostgreSQL schema a store keeps, in the stores layer (the version core never sees it).
/// <list type="bullet">
/// <item>The version of each component is a row of <c>bassia_schema</c> in the connection's schema (its
/// <c>search_path</c>), beside the history of applied steps in <c>bassia_schema_history</c>.</item>
/// <item>A schema already at this build's version costs one query and takes no lock - every server start and every
/// CLI command opens the stores.</item>
/// <item>Otherwise the runner takes a session advisory lock keyed by the schema name, so concurrent starters (server
/// nodes, CLI processes) wait for one another, then re-reads the version and applies what is still missing.</item>
/// <item>Tables made before versioning are detected and stamped as version 1 when they match what migration 1 would
/// create; a mismatch is reported, never repaired.</item>
/// <item>A database newer than the build opens when its <c>compatible_from</c> is at most the build's latest version
/// (expand/contract), and is refused otherwise.</item>
/// </list>
/// </summary>
public static class SchemaMigrator
{
	/// <summary>The first key of the migration advisory lock ("bass"); the second is the hash of the schema name.</summary>
	private const int LockClass = 0x62617373;

	/// <summary>How long a starter waits for another one's migration before giving up.</summary>
	public static TimeSpan DefaultLockTimeout { get; set; } = TimeSpan.FromMinutes(5);

	private const string Bootstrap = """
		CREATE TABLE IF NOT EXISTS bassia_schema (component text PRIMARY KEY, version integer NOT NULL, compatible_from integer NOT NULL,
			updated timestamptz NOT NULL DEFAULT now());
		CREATE TABLE IF NOT EXISTS bassia_schema_history (component text NOT NULL, version integer NOT NULL, description text NOT NULL,
			applied timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (component, version));
		""";

	/// <summary>The components of a server's (and a local monorepo's) schema, in the order they are migrated.</summary>
	public static IReadOnlyList<SchemaComponent> ServerComponents => [PostgresState.Schema, PostgresTreeStore.Schema];

	/// <summary>Runs every component of <paramref name="components"/> on one connection string (<c>bassia-server migrate</c>).</summary>
	public static IReadOnlyList<SchemaStatus> RunAll(string connectionString, IEnumerable<SchemaComponent> components, SchemaMode mode = SchemaMode.Migrate,
		TimeSpan? lockTimeout = null)
	{
		using var source = NpgsqlDataSource.Create(connectionString);
		return components.Select(component => Run(source, component, mode, lockTimeout)).ToList();
	}

	/// <summary>Opens <paramref name="component"/>'s schema on <paramref name="source"/> as <paramref name="mode"/> says.</summary>
	/// <exception cref="SchemaException">The schema cannot be used by this build (and is not a cache), or the lock was not granted in time.</exception>
	public static SchemaStatus Run(NpgsqlDataSource source, SchemaComponent component, SchemaMode mode = SchemaMode.Migrate, TimeSpan? lockTimeout = null)
	{
		Validate(component);
		using var connection = source.OpenConnection();

		// The fast path: one query, no lock, nothing written.
		var stored = ReadVersion(connection, null, component.Name);
		if (stored is { } fast && (fast.Version == component.Latest || (fast.Version > component.Latest && fast.CompatibleFrom <= component.Latest)))
		{
			return new SchemaStatus(component.Name, fast.Version, fast.CompatibleFrom,
				fast.Version == component.Latest ? SchemaOutcome.Current : SchemaOutcome.NewerCompatible, []);
		}

		if (mode == SchemaMode.Verify && !component.IsCache)
		{
			throw Refusal(connection, component, stored);
		}

		Lock(connection, lockTimeout ?? DefaultLockTimeout);
		try
		{
			Execute(connection, null, Bootstrap);
			return MigrateLocked(connection, component);
		}
		finally
		{
			Unlock(connection);
		}
	}

	private static void Validate(SchemaComponent component)
	{
		if (component.Migrations.Count == 0)
		{
			throw new ArgumentException($"Schema component '{component.Name}' has no migrations.");
		}

		for (var index = 0; index < component.Migrations.Count; index++)
		{
			if (component.Migrations[index].Version != index + 1)
			{
				throw new ArgumentException($"Schema component '{component.Name}': migration {index + 1} is numbered {component.Migrations[index].Version}; versions run 1, 2, 3...");
			}
		}
	}

	private static SchemaStatus MigrateLocked(NpgsqlConnection connection, SchemaComponent component)
	{
		var stored = ReadVersion(connection, null, component.Name);
		var outcome = SchemaOutcome.Migrated;
		var applied = new List<int>();
		if (stored is null)
		{
			var existing = BaselineTables(connection, component);
			if (existing.Count == 0)
			{
				outcome = SchemaOutcome.Created;
			}
			else if (Drift(connection, component) is { Count: > 0 } drift)
			{
				if (!component.IsCache)
				{
					throw new SchemaException($"The {component.Name} tables in schema '{CurrentSchema(connection)}' predate schema versioning but do not match " +
						$"version 1, so they were left as they are: {string.Join("; ", drift)}. Restore them to the version-1 shape, or move the data to a new schema.");
				}

				Rebuild(connection, component);
				outcome = SchemaOutcome.Rebuilt;
			}
			else
			{
				// Migration 1 is idempotent: over tables that match it, it only adds what is missing (an index, a sequence).
				Apply(connection, component, component.Migrations[0], compatibleFrom: 1, baseline: true);
				applied.Add(1);
				stored = (1, 1);
				outcome = SchemaOutcome.Baselined;
			}
		}
		else if (stored.Value.Version > component.Latest)
		{
			if (stored.Value.CompatibleFrom <= component.Latest)
			{
				return new SchemaStatus(component.Name, stored.Value.Version, stored.Value.CompatibleFrom, SchemaOutcome.NewerCompatible, []);
			}

			if (!component.IsCache)
			{
				throw Refusal(connection, component, stored);
			}

			Rebuild(connection, component);
			outcome = SchemaOutcome.Rebuilt;
			stored = null;
		}
		else if (stored.Value.Version == component.Latest)
		{
			// Another starter migrated while this one waited for the lock.
			return new SchemaStatus(component.Name, stored.Value.Version, stored.Value.CompatibleFrom, SchemaOutcome.Current, []);
		}

		var version = stored?.Version ?? 0;
		var compatibleFrom = stored?.CompatibleFrom ?? 1;
		foreach (var migration in component.Migrations.Where(migration => migration.Version > version))
		{
			compatibleFrom = Math.Max(compatibleFrom, migration.MinimumCode ?? 1);
			Apply(connection, component, migration, compatibleFrom, baseline: false);
			applied.Add(migration.Version);
		}

		return new SchemaStatus(component.Name, component.Latest, compatibleFrom, outcome, applied);
	}

	private static void Apply(NpgsqlConnection connection, SchemaComponent component, SchemaMigration migration, int compatibleFrom, bool baseline)
	{
		const string Stamp = """
			INSERT INTO bassia_schema (component, version, compatible_from) VALUES (@component, @version, @compatible)
			ON CONFLICT (component) DO UPDATE SET version = EXCLUDED.version, compatible_from = EXCLUDED.compatible_from, updated = now();
			INSERT INTO bassia_schema_history (component, version, description) VALUES (@component, @version, @description)
			ON CONFLICT (component, version) DO UPDATE SET description = EXCLUDED.description, applied = now();
			""";
		var parameters = new (string, object)[]
		{
			("component", component.Name), ("version", migration.Version), ("compatible", compatibleFrom),
			("description", baseline ? "baseline (tables made before versioning): " + migration.Description : migration.Description)
		};
		try
		{
			if (migration.Transactional)
			{
				using var transaction = connection.BeginTransaction();
				Execute(connection, transaction, migration.Sql);
				Execute(connection, transaction, Stamp, parameters);
				transaction.Commit();
			}
			else
			{
				Execute(connection, null, migration.Sql);
				Execute(connection, null, Stamp, parameters);
			}
		}
		catch (PostgresException ex)
		{
			throw new SchemaException($"Migration {migration.Version} of the {component.Name} schema ({migration.Description}) failed and was rolled back: {ex.MessageText}");
		}
	}

	private static void Rebuild(NpgsqlConnection connection, SchemaComponent component)
	{
		using var transaction = connection.BeginTransaction();
		Execute(connection, transaction, component.DropSql!);
		Execute(connection, transaction, "DELETE FROM bassia_schema WHERE component = @component; DELETE FROM bassia_schema_history WHERE component = @component",
			("component", component.Name));
		transaction.Commit();
	}

	private static SchemaException Refusal(NpgsqlConnection connection, SchemaComponent component, (int Version, int CompatibleFrom)? stored)
	{
		var schema = CurrentSchema(connection);
		return stored switch
		{
			null => new SchemaException($"The {component.Name} schema in '{schema}' is not versioned yet; this build needs version {component.Latest}. " +
				"Run 'bassia-server migrate', or start without '-migrate verify'."),
			{ } value when value.Version < component.Latest => new SchemaException($"The {component.Name} schema in '{schema}' is at version {value.Version}; " +
				$"this build needs version {component.Latest}. Run 'bassia-server migrate', or start without '-migrate verify'."),
			{ } value => new SchemaException($"The {component.Name} schema in '{schema}' is at version {value.Version} and needs a build that knows version " +
				$"{value.CompatibleFrom} or later; this build knows up to version {component.Latest}. Upgrade Bassia.")
		};
	}

	// ----- baseline detection -----

	/// <summary>Which of the tables migration 1 creates exist already, by name, in the current schema.</summary>
	private static List<string> BaselineTables(NpgsqlConnection connection, SchemaComponent component)
	{
		var expected = BaselineShape(connection, component).Select(column => column.Table).Distinct().ToArray();
		using var command = new NpgsqlCommand(
			"SELECT table_name::text FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = ANY(@names) ORDER BY 1", connection);
		command.Parameters.AddWithValue("names", expected);
		using var reader = command.ExecuteReader();
		var found = new List<string>();
		while (reader.Read())
		{
			found.Add(reader.GetString(0));
		}

		return found;
	}

	private sealed record Column(string Table, string Name, string Type, string? Collation, bool Nullable)
	{
		public override string ToString() => $"{Type}{(Collation is null ? "" : $" COLLATE \"{Collation}\"")}{(Nullable ? "" : " NOT NULL")}";
	}

	private const string ColumnsSql = """
		SELECT c.relname::text, a.attname::text, format_type(a.atttypid, a.atttypmod),
			CASE WHEN a.attcollation <> t.typcollation THEN co.collname::text END, NOT a.attnotnull
		FROM pg_attribute a
		JOIN pg_class c ON c.oid = a.attrelid
		JOIN pg_type t ON t.oid = a.atttypid
		LEFT JOIN pg_collation co ON co.oid = a.attcollation
		WHERE c.relnamespace = {0} AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped AND c.relname::text = ANY(@tables)
		""";

	/// <summary>
	/// The columns migration 1 creates, found by running it in this session's temporary schema inside a transaction
	/// that is rolled back: the shape is never written down twice, so it cannot drift from the migration itself.
	/// </summary>
	private static List<Column> BaselineShape(NpgsqlConnection connection, SchemaComponent component)
	{
		using var transaction = connection.BeginTransaction();
		Execute(connection, transaction, "SET LOCAL search_path = pg_temp");
		Execute(connection, transaction, component.Migrations[0].Sql);
		var tables = new List<string>();
		using (var list = new NpgsqlCommand("SELECT relname::text FROM pg_class WHERE relnamespace = pg_my_temp_schema() AND relkind = 'r'", connection, transaction))
		using (var reader = list.ExecuteReader())
		{
			while (reader.Read())
			{
				tables.Add(reader.GetString(0));
			}
		}

		var shape = Columns(connection, transaction, "pg_my_temp_schema()", tables);
		transaction.Rollback();
		return shape;
	}

	private static List<Column> Columns(NpgsqlConnection connection, NpgsqlTransaction? transaction, string schemaOid, IReadOnlyList<string> tables)
	{
		using var command = new NpgsqlCommand(string.Format(ColumnsSql, schemaOid), connection, transaction);
		command.Parameters.AddWithValue("tables", tables.ToArray());
		using var reader = command.ExecuteReader();
		var columns = new List<Column>();
		while (reader.Read())
		{
			columns.Add(new Column(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4)));
		}

		return columns;
	}

	/// <summary>How the component's existing tables differ from migration 1's: missing, extra or changed columns (empty when they match).</summary>
	private static List<string> Drift(NpgsqlConnection connection, SchemaComponent component)
	{
		var expected = BaselineShape(connection, component);
		var present = BaselineTables(connection, component);
		var actual = Columns(connection, null, "current_schema()::regnamespace", present);
		var differences = new List<string>();
		foreach (var table in present)
		{
			var want = expected.Where(column => column.Table == table).ToDictionary(column => column.Name);
			var have = actual.Where(column => column.Table == table).ToDictionary(column => column.Name);
			foreach (var (name, column) in want)
			{
				if (!have.TryGetValue(name, out var found))
				{
					differences.Add($"{table}.{name} is missing (expected {column})");
				}
				else if (found != column)
				{
					differences.Add($"{table}.{name} is {found} (expected {column})");
				}
			}

			differences.AddRange(have.Keys.Where(name => !want.ContainsKey(name)).Select(name => $"{table}.{name} is not in version 1"));
		}

		return differences;
	}

	// ----- plumbing -----

	private static (int Version, int CompatibleFrom)? ReadVersion(NpgsqlConnection connection, NpgsqlTransaction? transaction, string component)
	{
		// to_regclass answers null for a missing table instead of failing, so a fresh schema needs no exception handling.
		// Qualified with the current schema, so a bassia_schema further along the search_path (public) is not mistaken for this one.
		using var command = new NpgsqlCommand("SELECT to_regclass(quote_ident(current_schema()) || '.bassia_schema') IS NOT NULL", connection, transaction);
		if (command.ExecuteScalar() is not true)
		{
			return null;
		}

		using var read = new NpgsqlCommand("SELECT version, compatible_from FROM bassia_schema WHERE component = @component", connection, transaction);
		read.Parameters.AddWithValue("component", component);
		using var reader = read.ExecuteReader();
		return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : null;
	}

	private static string CurrentSchema(NpgsqlConnection connection)
	{
		using var command = new NpgsqlCommand("SELECT current_schema()", connection);
		return command.ExecuteScalar() as string ?? "(none)";
	}

	private static void Lock(NpgsqlConnection connection, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (true)
		{
			using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@class, hashtext(current_schema()))", connection);
			command.Parameters.AddWithValue("class", LockClass);
			if (command.ExecuteScalar() is true)
			{
				return;
			}

			if (DateTime.UtcNow >= deadline)
			{
				throw new SchemaException($"Another process has been migrating schema '{CurrentSchema(connection)}' for more than {timeout.TotalSeconds:0} s; " +
					"try again once it has finished.");
			}

			Thread.Sleep(100);
		}
	}

	private static void Unlock(NpgsqlConnection connection)
	{
		// A broken connection drops its session locks; Npgsql's reset on return to the pool (DISCARD ALL) releases them too.
		if (connection.State == System.Data.ConnectionState.Open)
		{
			using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@class, hashtext(current_schema()))", connection);
			command.Parameters.AddWithValue("class", LockClass);
			command.ExecuteNonQuery();
		}
	}

	private static void Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
	{
		using var command = new NpgsqlCommand(sql, connection, transaction);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		command.ExecuteNonQuery();
	}
}
