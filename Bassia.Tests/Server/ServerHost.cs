namespace Bassia.Tests.Server;

using System.Net;
using System.Net.Sockets;
using Bassia.Server;
using Bassia.Storage.Remote;
using Microsoft.AspNetCore.Builder;

/// <summary>
/// A Bassia server for tests, in this process on a local port: SQLite, and a monorepo on disk (the depot fixture) with
/// the components lib and app (app references lib) and the users alice (an admin), bob and carol. Clients run the CLI in
/// this process; <see cref="As"/> picks whose token they use.
/// </summary>
internal sealed class ServerHost : IAsyncDisposable
{
	private readonly TempDirectory temp = new();

	private WebApplication app = null!;

	public MonorepoFixture Depot { get; private set; } = null!;

	public ServerState State { get; private set; } = null!;

	public string Url { get; private set; } = "";

	public string Temp => temp.Path;

	/// <summary>
	/// A server on the depot monorepo; with <paramref name="cloud"/>, on the cloud storage (in-memory object store and
	/// table) instead, starting empty - not even users, so only the configured admin alice can act. <paramref name="auth"/>
	/// is added to the configuration's <c>[auth]</c> section (identity providers).
	/// </summary>
	public static async Task<ServerHost> StartAsync(string defaultPermission = "none", string auth = "", bool cloud = false)
	{
		var host = new ServerHost();
		host.Depot = await MonorepoFixture.CreateAsync();
		if (!cloud)
		{
			await host.SetUpDepotAsync();
		}

		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		host.Url = $"http://127.0.0.1:{port}";

		var storage = cloud
			? "kind = \"cloud\"\nbucket = \"test\"\ntable = \"test\""
			: $"kind = \"git\"\npath = \"{host.Depot.Root.Replace("\\", "\\\\")}\"";
		var (provider, connection) = await TestDatabase.CreateAsync(host.temp.Path);
		var config = ServerConfig.Parse($"""
			url = "{host.Url}"
			admins = ["alice"]
			default_permission = "{defaultPermission}"
			state = "state"
			request_log = false
			[database]
			provider = "{provider}"
			connection = "{connection.Replace("\\", "\\\\")}"
			[storage]
			{storage}
			[auth]
			signing_key = "a-test-signing-key-of-at-least-32-characters"
			{auth}
			""", host.temp.Path);
		host.State = await ServerState.CreateAsync(config, cloud: cloud ? (new Bassia.Storage.Cloud.MemoryObjectStore(), new Bassia.Storage.Cloud.MemoryRecordTable()) : null);
		host.app = ServerApp.Build(host.State);
		await host.app.StartAsync();

		// Logins of the tests are files of their own; nothing lands in the developer's keychain.
		Environment.SetEnvironmentVariable("BASSIA_CREDENTIAL_STORE", "file");
		Environment.SetEnvironmentVariable("BASSIA_CREDENTIALS_DIR", Path.Combine(host.temp.Path, "credentials"));
		return host;
	}

	/// <summary>The depot's components (lib, and app referencing lib), lock settings and users.</summary>
	private async Task SetUpDepotAsync()
	{
		var host = this;
		await host.Depot.AddComponentAsync("lib", files: new Dictionary<string, string> { ["include/lib.h"] = "int f();\n", ["src/lib.c"] = "int f() { return 1; }\n" });
		await host.Depot.AddComponentAsync("app", files: new Dictionary<string, string> { ["src/main.c"] = "int main;\n", ["docs/guide.md"] = "guide\n" });
		Assert.Equal(0, (await host.Depot.BassiaAsync("component", "set", "app", "-references", "lib")).ExitCode);
		Assert.Equal(0, (await host.Depot.BassiaAsync("config", "set", "-key", "lock.files", "-value", "**")).ExitCode);
		foreach (var user in new[] { "alice", "bob", "carol" })
		{
			var (exit, _, error) = await host.Depot.BassiaAsync("user", "add", "-id", user, "-name", user, "-email", $"{user}@example.com");
			Assert.True(exit == 0, error);
		}
	}

	public string TokenFor(string user, IReadOnlyList<string>? scope = null) => State.Tokens.Issue(user, TimeSpan.FromHours(1), scope);

	/// <summary>Until disposed, clients act with this token (<c>BASSIA_TOKEN</c>).</summary>
	public static IDisposable As(string token)
	{
		var previous = Environment.GetEnvironmentVariable(ServerCredentials.TokenVariable);
		Environment.SetEnvironmentVariable(ServerCredentials.TokenVariable, token);
		return new Restore(() => Environment.SetEnvironmentVariable(ServerCredentials.TokenVariable, previous));
	}

	public IDisposable AsUser(string user) => As(TokenFor(user));

	/// <summary>A clone of the server, made by <paramref name="user"/>.</summary>
	public async Task<string> CloneAsync(string user, string name)
	{
		using var _ = AsUser(user);
		var clone = Path.Combine(temp.Path, name);
		var (exit, _, error) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "clone", "-from", Url, "-path", clone);
		Assert.True(exit == 0, error);
		return clone;
	}

	public async Task<(int ExitCode, string Output, string Error)> RunAsync(string user, string directory, params string[] args)
	{
		using var _ = AsUser(user);
		return await TestEnvironment.RunInDirectoryAsync(directory, args);
	}

	public async ValueTask DisposeAsync()
	{
		await app.StopAsync();
		await app.DisposeAsync();
		await Depot.DisposeAsync();
		temp.Dispose();
	}

	private sealed class Restore(Action restore) : IDisposable
	{
		public void Dispose() => restore();
	}
}
