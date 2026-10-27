namespace Bassia.Tests.Server;

using Npgsql;

/// <summary>
/// The database a test server uses: SQLite in its folder, or - when <c>BASSIA_SERVER_TEST_POSTGRES</c> is a PostgreSQL
/// connection string - a new database of its own on that server, so the server tests run on both.
/// </summary>
internal static class TestDatabase
{
	public static string? Postgres => Environment.GetEnvironmentVariable("BASSIA_SERVER_TEST_POSTGRES") is { Length: > 0 } connection ? connection : null;

	/// <summary>The provider and connection string of a new, empty database.</summary>
	public static async Task<(string Provider, string Connection)> CreateAsync(string folder)
	{
		if (Postgres is not { } connection)
		{
			return ("sqlite", $"Data Source={Path.Combine(folder, "server.db")}");
		}

		var name = $"bassia_test_{Guid.NewGuid():N}";
		await using (var admin = new NpgsqlConnection(connection))
		{
			await admin.OpenAsync();
			await using var create = new NpgsqlCommand($"CREATE DATABASE {name};", admin);
			await create.ExecuteNonQueryAsync();
		}

		return ("postgres", new NpgsqlConnectionStringBuilder(connection) { Database = name }.ToString());
	}
}
