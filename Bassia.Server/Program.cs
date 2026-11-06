namespace Bassia.Server;

using Bassia.Core.Version;
using Bassia.Stores;

/// <summary>
/// <c>bassia-server</c>: runs the server, or a maintenance job over its data.
/// <code>
/// bassia-server -data <folder> [-url http://0.0.0.0:8080] [-public-url <url>] [-token <t>]
///               -postgres <connection string> [-storage disk|s3|azure ...] [-semantic-driver <cmd>|off] [-validate <cmd>]
/// bassia-server gc -data <folder> -postgres <connection string> [-grace-hours 24] [-retention-days 14] [-dry-run]
/// bassia-server scrub -data <folder> -postgres <connection string> [-sample <n>]
/// </code>
/// Every switch can also come from a <c>BASSIA_SERVER_*</c> environment variable (containers).
/// </summary>
internal static class Program
{
	private const string Usage = """
		Usage:
		  bassia-server -data <folder> -postgres <connection string> [-url <url>] [-public-url <url>] [-token <token>]
		                [-storage disk | -storage s3 -bucket <b> [-region <r>] | -storage azure (-azure-connection <string> | -azure-url <container url>)]
		                [-semantic-driver <command>|off] [-validate <command>]        run the server
		  bassia-server gc -data <folder> -postgres <connection string> [-grace-hours <n>] [-retention-days <n>] [-dry-run]   collect unreferenced blobs
		  bassia-server scrub -data <folder> -postgres <connection string> [-sample <n>]                                      re-hash stored blobs and trees
		  bassia-server rebuild-trees -data <folder> -postgres <connection string> -tree-store blob [-delete-blobs]           move an earlier server's trees from blobs into PostgreSQL
		The state (heads, path index, leases) lives in PostgreSQL; -data holds the signing key and the local-disk blobs.
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
					Console.Out.WriteLine($"checked {report.Checked} blobs: {report.Corrupt.Count} corrupt, {report.Unreadable.Count} unreadable");
					foreach (var id in report.Corrupt)
					{
						Console.Out.WriteLine($"corrupt {id}");
					}

					return report.Corrupt.Count == 0 && report.Unreadable.Count == 0 ? 0 : 1;
				}

				case "rebuild-trees":
				{
					// The explicit step of moving an existing server's trees from blobs into PostgreSQL. The server runs here with
					// trees as blobs (-tree-store blob), and the relational store is filled from every tree the heads reach.
					if (!options.TreeStore.Equals("blob", StringComparison.OrdinalIgnoreCase))
					{
						throw new ArgumentException("rebuild-trees reads the old trees from blobs: pass -tree-store blob.");
					}

					await using var destination = new PostgresTreeStore(options.Postgres!);
					var report = await new GarbageCollector(server.Repo, server.Maintenance).MigrateTreesAsync(destination, rest.Contains("-delete-blobs"));
					await destination.MarkReadyAsync();
					Console.Out.WriteLine($"moved {report.Trees} trees into PostgreSQL; {report.BlobsDeleted} tree blobs deleted. Start the server without -tree-store blob.");
					return 0;
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
