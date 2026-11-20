namespace Bassia.Scenarios.Harness;

using Npgsql;
using Testcontainers.PostgreSql;

/// <summary>
/// The real PostgreSQL the scenarios keep state and trees in: <c>BASSIA_TEST_POSTGRES</c>, or else one throw-away
/// container (<c>postgres:16</c>) started on first use and removed when the test run ends. Every test gets schemas of
/// its own, dropped afterwards unless <c>BASSIA_SCENARIO_KEEP</c> is set.
/// </summary>
internal static class ScenarioPostgres
{
	private static readonly SemaphoreSlim Gate = new(1, 1);
	private static string? connection;
	private static PostgreSqlContainer? container;

	/// <summary>The connection string of the database (no schema chosen).</summary>
	public static async Task<string> ConnectionAsync()
	{
		if (connection is not null)
		{
			return connection;
		}

		await Gate.WaitAsync();
		try
		{
			if (connection is null)
			{
				if (ScenarioEnvironment.PostgresVariable is { } given)
				{
					connection = given;
				}
				else
				{
					container = new PostgreSqlBuilder("postgres:16").Build();
					await container.StartAsync();
					connection = container.GetConnectionString();
					AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().GetAwaiter().GetResult();
				}
			}

			return connection;
		}
		finally
		{
			Gate.Release();
		}
	}

	/// <summary>A connection string whose search path is a new, empty schema.</summary>
	public static async Task<(string Connection, string Schema)> FreshSchemaAsync(string prefix)
	{
		var baseConnection = await ConnectionAsync();
		var schema = prefix + "_" + Guid.NewGuid().ToString("N")[..12];
		await ExecuteAsync(baseConnection, $"CREATE SCHEMA {schema}");
		return (new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema }.ToString(), schema);
	}

	public static async Task DropSchemaAsync(string schema)
	{
		try
		{
			NpgsqlConnection.ClearAllPools();
			await ExecuteAsync(await ConnectionAsync(), $"DROP SCHEMA IF EXISTS {schema} CASCADE");
		}
		catch (NpgsqlException)
		{
			// Best effort: a schema still in use by a process that outlived the test is left behind.
		}
	}

	private static async Task ExecuteAsync(string connectionString, string sql)
	{
		await using var db = new NpgsqlConnection(connectionString);
		await db.OpenAsync();
		await using var command = new NpgsqlCommand(sql, db);
		await command.ExecuteNonQueryAsync();
	}
}
