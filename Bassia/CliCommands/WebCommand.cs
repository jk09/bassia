namespace Bassia.CliCommands;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Bassia.Cli;
using Bassia.Web;
using Microsoft.Extensions.Hosting;

/// <summary>
/// <c>bassia web</c>: serves the read-only dashboard over the monorepo the current folder belongs to (a local monorepo or
/// a clone of a server), on the loopback interface only, until Ctrl-C.
/// </summary>
internal static class WebCommand
{
	public const int DefaultPort = 8080;

	private const int PortAttempts = 20;

	public static async Task<int> RunAsync(Invocation invocation)
	{
		var port = invocation.Int("port", DefaultPort, 1, 65535);
		using var monorepo = Monorepo.Current();
		if (FreePort(port) is not { } free)
		{
			return ProgramCli.WriteResult(false, "web", $"No free port in {port}-{port + PortAttempts - 1} on 127.0.0.1; choose one with -port.");
		}

		var url = $"http://127.0.0.1:{free}/";
		var runner = new Agents.AgentRunner(monorepo);
		var settings = MonorepoConfig.Keys.Select(key =>
		{
			var (value, source) = monorepo.Config.Resolve(key.Key);
			return (key.Key, key.Key == "server.token" && value.Length > 0 ? "(set)" : value, source);
		}).ToList();
		var scope = new DashboardScope(monorepo.Repo, monorepo.Landing, monorepo.IsRemote ? $"{monorepo.Root} (clone of {monorepo.Url})" : monorepo.Root,
			run => runner.ReadLogAsync(run), Settings: settings);
		await using var app = new Dashboard(_ => Task.FromResult(scope)).Build(url);
		await app.StartAsync();
		Console.Error.WriteLine($"bassia: dashboard for '{monorepo.Root}' at {url} (Ctrl-C stops it).");
		if (!invocation.Has("no-open"))
		{
			OpenBrowser(url);
		}

		await app.WaitForShutdownAsync();
		return ProgramCli.WriteResult(true, "web", $"Dashboard at {url} stopped.", new Dictionary<string, object?> { ["url"] = url });
	}

	private static int? FreePort(int first)
	{
		for (var port = first; port < first + PortAttempts && port < 65536; port++)
		{
			try
			{
				using var probe = new TcpListener(IPAddress.Loopback, port);
				probe.Start();
				probe.Stop();
				return port;
			}
			catch (SocketException)
			{
			}
		}

		return null;
	}

	private static void OpenBrowser(string url)
	{
		try
		{
			if (OperatingSystem.IsWindows())
			{
				Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
			}
			else
			{
				Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url);
			}
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			// No browser here; the URL is printed.
		}
	}
}
