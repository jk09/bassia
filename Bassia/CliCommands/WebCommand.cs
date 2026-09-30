namespace Bassia;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Web;
using Microsoft.Extensions.Hosting;

/// <summary>
/// <c>bassia web</c>: serves the web dashboard over the monorepo the current directory belongs to, on the loopback
/// interface only, until Ctrl-C. Runs started from the dashboard are stopped (and recorded as cancelled) on the way out.
/// </summary>
internal static class WebCommand
{
	public const int DefaultPort = 8080;

	/// <summary>How many ports after the requested one are tried when it is taken.</summary>
	private const int PortAttempts = 20;

	public static async Task<int> RunAsync(Invocation invocation)
	{
		var port = invocation.Int("port", DefaultPort, 1, 65535);
		var open = !invocation.Has("no-open");

		var root = Monorepo.FindRoot(Environment.CurrentDirectory);
		if (root is null)
		{
			return ProgramCli.WriteResult(false, "web", $"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia init' first.");
		}

		Monorepo monorepo;
		try
		{
			monorepo = Monorepo.Load(root);
		}
		catch (MonorepoException ex)
		{
			return ProgramCli.WriteResult(false, "web", ex.Message);
		}

		if (FreePort(port) is not { } free)
		{
			return ProgramCli.WriteResult(false, "web", $"No free port in {port}-{port + PortAttempts - 1} on 127.0.0.1; choose one with -port.");
		}

		var git = new GitClient(root);
		using var supervisor = new RunSupervisor((select, command, context) => RunCommands.StartHostedAsync(git, monorepo, select, command, context));
		var url = $"http://127.0.0.1:{free}/";
		await using var app = new Dashboard(monorepo, supervisor).Build(url);
		await app.StartAsync();

		Console.Error.WriteLine($"bassia: dashboard for '{root}' at {url} (Ctrl-C stops it).");
		if (open)
		{
			OpenBrowser(url);
		}

		await app.WaitForShutdownAsync();

		// The runs are tasks of this process: stop them so none is orphaned without its final record.
		if (supervisor.HasLive)
		{
			Console.Error.WriteLine("bassia: stopping the runs started from the dashboard.");
			supervisor.CancelAll();
		}

		await supervisor.WhenAllSettledAsync();
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
				// Taken; try the next one.
			}
		}

		return null;
	}

	/// <summary>Best effort: a machine without a browser (or a display) still gets the URL on stderr.</summary>
	private static void OpenBrowser(string url)
	{
		try
		{
			var startInfo = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
				: OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", url)
				: new ProcessStartInfo("xdg-open", url);
			startInfo.RedirectStandardError = !startInfo.UseShellExecute;
			startInfo.RedirectStandardOutput = !startInfo.UseShellExecute;
			Process.Start(startInfo)?.Dispose();
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
		{
			// No browser to open; the URL is printed.
		}
	}
}
