namespace Bassia.Scenarios.Harness;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Bassia.Server;

/// <summary>
/// A <c>bassia-server</c> for one test: blobs in its memory (<c>-storage memory</c>, the mock of cloud blob storage),
/// heads, path index, leases, the workspace registry and trees in a fresh schema of the real PostgreSQL. In-process by
/// default, so the server's code can be debugged from a test; <c>BASSIA_SCENARIO_SERVER</c> runs an installed one.
/// Either way it is started from its command line, and clients reach it over HTTP like any client.
/// </summary>
public sealed class ScenarioServer : IAsyncDisposable
{
	private ServerApp? inProcess;
	private Process? process;

	private ScenarioServer(string url, string token, string schema)
	{
		Url = url;
		Token = token;
		Schema = schema;
	}

	public string Url { get; }

	public string Token { get; }

	/// <summary>The PostgreSQL schema holding the server's state.</summary>
	public string Schema { get; }

	public static async Task<ScenarioServer> StartAsync(string data, IReadOnlyList<string> extraArguments)
	{
		var (postgres, schema) = await ScenarioPostgres.FreshSchemaAsync("scenario_server");
		var token = "scenario-" + Guid.NewGuid().ToString("N")[..8];
		if (ScenarioEnvironment.ExternalServer is { } executable)
		{
			var url = $"http://127.0.0.1:{FreePort()}";
			var server = new ScenarioServer(url, token, schema);
			var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
			foreach (var argument in Arguments(data, postgres, url, token, extraArguments))
			{
				start.ArgumentList.Add(argument);
			}

			server.process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
			_ = server.process.StandardOutput.ReadToEndAsync();
			_ = server.process.StandardError.ReadToEndAsync();
			using var http = new HttpClient();
			for (var attempt = 0; ; attempt++)
			{
				try
				{
					if ((await http.GetAsync(url + "/healthz")).IsSuccessStatusCode)
					{
						return server;
					}
				}
				catch (HttpRequestException) when (attempt < 100)
				{
				}

				if (server.process.HasExited || attempt >= 100)
				{
					throw new InvalidOperationException($"{executable} did not start at {url}.");
				}

				await Task.Delay(200);
			}
		}

		var app = new ServerApp(ServerOptions.Parse(Arguments(data, postgres, "http://127.0.0.1:0", token, extraArguments)));
		await app.StartAsync();
		return new ScenarioServer(app.Address!, token, schema) { inProcess = app };
	}

	/// <summary>The server's command line: what a person would type to run it on memory blobs.</summary>
	private static List<string> Arguments(string data, string postgres, string url, string token, IReadOnlyList<string> extra) =>
		["-data", data, "-postgres", postgres, "-storage", "memory", "-url", url, "-token", token, "-semantic-driver", "off", .. extra];

	private static int FreePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}

	public async ValueTask DisposeAsync()
	{
		if (inProcess is not null)
		{
			await inProcess.DisposeAsync();
		}

		if (process is not null)
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync();
			}

			process.Dispose();
		}
	}
}
