namespace Bassia.Server;

using Bassia.Core.Version;
using Bassia.Stores;

/// <summary>
/// <c>bassia-server</c>: runs the server (a Web API for Bassia Studio, <c>bassia</c> clients and other clients), or a maintenance job over its data.
/// <code>
/// bassia-server -data <folder> [-url http://0.0.0.0:8080] [-public-url <url>] [-token <t> | -no-auth] [-cors-origin <origins>]
///             -postgres <connection string> [-storage disk|s3|azure ...] [-semantic-driver <cmd>|off] [-validate <cmd>]
/// bassia-server gc -data <folder> -postgres <connection string> [-grace-hours 24] [-retention-days 14] [-dry-run]
/// bassia-server scrub -data <folder> -postgres <connection string> [-sample <n>]
/// </code>
/// Every switch can also come from a <c>BASSIA_SERVER_*</c> environment variable (containers).
/// </summary>
internal static class Program
{
	private const string Usage = """
		Usage:
		  bassia-server -data <folder> -postgres <connection string> [-url <url>] [-public-url <url>] [-token <token> | -no-auth]
		                [-storage disk | -storage s3 -bucket <b> [-region <r>] | -storage azure (-azure-connection <string> | -azure-url <container url>)]
		                [-cors-origin <origin>[,<origin>...]] [-semantic-driver <command>|off] [-validate <command>]        run the server
		  bassia-server gc -data <folder> -postgres <connection string> [-grace-hours <n>] [-retention-days <n>] [-dry-run]   collect unreferenced blobs
		  bassia-server scrub -data <folder> -postgres <connection string> [-sample <n>]                                      re-hash stored blobs and trees
		The state (heads, path index, leases) lives in PostgreSQL; -data holds the signing key and the local-disk blobs.
		The server is a Web API under /api/v1 (Bassia Studio is its web UI). It needs a bearer token: -token, or without it
		one generated into <data>/token. -no-auth serves everyone. -cors-origin lets browser apps on those origins call it.
		Switches may also come from BASSIA_SERVER_<NAME> environment variables (BASSIA_SERVER_DATA, BASSIA_SERVER_TOKEN, ...).
		""";

	public static async Task<int> Main(string[] args)
	{
		var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "serve";
		var rest = command == "serve" ? args : args[1..];
		if (command is "help" or "-help" or "--help" || args.Contains("-help") || args.Contains("--help"))
		{
			Console.Out.WriteLine(Usage);
			return 0;
		}

		try
		{
			var options = ServerOptions.Parse(rest);
			await using var server = new ServerApp(options);
			switch (command)
			{
				case "serve":
					await server.StartAsync();
					Console.Error.WriteLine($"bassia-server: serving '{options.Data}' ({options.Storage} blobs) at {server.Address}.");
					if (options.NoAuth)
					{
						Console.Error.WriteLine("bassia-server: WARNING: -no-auth: anyone who can reach this port can read and change the monorepo.");
					}
					else if (options.Token is null)
					{
						Console.Error.WriteLine($"bassia-server: no -token given; the token is in '{server.TokenFile}'. Clients: bassia clone -from {server.Address} -token <token>, or Studio's -token.");
					}

					await server.WaitForShutdownAsync();
					return 0;
				case "gc":
				{
					await server.Repo.InitializeAsync("bassia-server");
					var value = Switch(rest, "grace-hours");
					var grace = TimeSpan.FromHours(value is null ? 24 : double.Parse(value));
					var retention = TimeSpan.FromDays(Switch(rest, "retention-days") is { } days ? double.Parse(days) : 14);
					var report = await new GarbageCollector(server.Repo, server.Maintenance).CollectAsync(grace, retention, rest.Contains("-dry-run"));
					Console.Out.WriteLine($"{(report.DryRun ? "would sweep" : "swept")} {report.Swept} of {report.Blobs} blobs ({report.SweptBytes} bytes) and {report.TreesSwept} of {report.Trees + report.TreesSwept} trees; " +
						$"{report.Live} live; {report.Young} younger than the grace period; {report.PrunedWorkspaces} closed workspaces pruned");
					return 0;
				}

				case "scrub":
				{
					var sample = Switch(rest, "sample") is { } text ? int.Parse(text) : (int?)null;
					var report = await new GarbageCollector(server.Repo, server.Maintenance).ScrubAsync(sample);
					Console.Out.WriteLine($"checked {report.Checked} blobs and {report.TreesChecked} trees: {report.Corrupt.Count} corrupt, {report.Unreadable.Count} unreadable");
					foreach (var id in report.Corrupt)
					{
						Console.Out.WriteLine($"corrupt {id}");
					}

					return report.Corrupt.Count == 0 && report.Unreadable.Count == 0 ? 0 : 1;
				}

				default:
					Console.Error.WriteLine($"Unknown command '{command}'.\n{Usage}");
					return 2;
			}
		}
		catch (ArgumentException ex)
		{
			Console.Error.WriteLine($"bassia-server: {ex.Message}\n{Usage}");
			return 2;
		}
	}

	private static string? Switch(IReadOnlyList<string> args, string name)
	{
		for (var index = 0; index + 1 < args.Count; index++)
		{
			if (args[index].TrimStart('-').Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				return args[index + 1];
			}
		}

		return null;
	}
}
