namespace Bassia.Stores;

using System.Text.RegularExpressions;
using Npgsql;
using Tomlyn.Model;

/// <summary>
/// The local PostgreSQL a monorepo folder keeps its state and trees in. Several monorepos share one database; each has a
/// schema of its own (named in <c>.bassia/repo.toml</c>), created on first use.
/// </summary>
internal static partial class LocalPostgres
{
	public const string ConfigKey = "postgres.connection";

	/// <summary>A new schema name for a monorepo folder.</summary>
	public static string NewSchema() => "bassia_" + Guid.NewGuid().ToString("N")[..16];

	/// <summary>Fails before anything is created when there is no PostgreSQL to use.</summary>
	public static void Require(string? given)
	{
		if (given is null && Environment.GetEnvironmentVariable("BASSIA_POSTGRES") is not { Length: > 0 })
		{
			throw new MonorepoException("Bassia keeps a monorepo's state in a local PostgreSQL: pass -postgres <connection string> " +
				"(e.g. \"Host=localhost;Username=postgres;Password=...;Database=bassia\") or set BASSIA_POSTGRES.");
		}
	}

	/// <summary>
	/// The connection string for this monorepo's schema (created if missing). <paramref name="legacyState"/> is the SQLite
	/// file an earlier version kept state in; a monorepo that has one and no schema cannot be opened.
	/// </summary>
	public static string Connect(MonorepoConfig config, TomlTable settings, string? legacyState = null)
	{
		if (!settings.TryGetValue("postgres_schema", out var value) || value is not string schema)
		{
			throw new MonorepoException(legacyState is not null && File.Exists(legacyState)
				? $"This monorepo keeps its state in SQLite ('{legacyState}'), which Bassia no longer reads and does not migrate to PostgreSQL. Create a new one with 'bassia init'."
				: "This monorepo folder has no PostgreSQL schema (postgres_schema in .bassia/repo.toml).");
		}

		if (!SchemaName().IsMatch(schema))
		{
			throw new MonorepoException($"'{schema}' is not a valid postgres_schema name.");
		}

		var connection = config.GetOrNull(ConfigKey)
			?? throw new MonorepoException("No local PostgreSQL is configured: set postgres.connection ('bassia config set -key postgres.connection -value <connection string>') or BASSIA_POSTGRES.");
		try
		{
			using (var admin = new NpgsqlConnection(connection))
			{
				admin.Open();
				using var create = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {schema}", admin);
				create.ExecuteNonQuery();
			}
		}
		catch (NpgsqlException ex)
		{
			throw new MonorepoException($"Cannot reach the local PostgreSQL: {ex.Message}");
		}

		return new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema, MaxPoolSize = 20 }.ToString();
	}

	[GeneratedRegex("^[a-z_][a-z0-9_]*$")]
	private static partial Regex SchemaName();
}
