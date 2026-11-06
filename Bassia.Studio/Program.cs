namespace Bassia.Studio;

/// <summary>
/// <c>bassia-studio</c>: the web client of a <c>bassia-server</c>.
/// <code>
/// bassia-studio -server <url> [-token <t>] [-url http://0.0.0.0:8080] [-cache <folder>] [-label <text>]
///               [-user <name>] [-role reader|contributor|admin]
///               [-run-host none|local|aca] [-bassia <command>] [-runs <folder>] [-public-url <url>] [-job-server <url>]
///               [-aca-job <resource id>] [-aca-image <image>] [-aca-secrets NAME=secret,...]
///               [-auth none|entra -entra-tenant <t> -entra-client-id <id> -entra-client-secret <s>]
/// </code>
/// </summary>
internal static class Program
{
	private const string Usage = """
		Usage:
		  bassia-studio -server <bassia-server url> [-token <token>] [-url <listen url>] [-cache <folder>] [-label <text>]
		                [-user <name>] [-role reader|contributor|admin]
		                [-run-host none|local|aca] [-bassia <command>] [-runs <folder>] [-public-url <url>] [-job-server <url>]
		                [-aca-job <resource id>] [-aca-image <image>] [-aca-secrets NAME=secret,...]
		                [-auth none|entra -entra-tenant <t> -entra-client-id <id> -entra-client-secret <s>]
		Switches may also come from BASSIA_STUDIO_<NAME> environment variables (BASSIA_STUDIO_SERVER, BASSIA_STUDIO_TOKEN, ...).
		""";

	public static async Task<int> Main(string[] args)
	{
		if (args.Any(arg => arg is "help" or "-help" or "--help"))
		{
			Console.Out.WriteLine(Usage);
			return 0;
		}

		try
		{
			await using var studio = new StudioApp(StudioOptions.Parse(args));
			await studio.StartAsync();
			Console.Error.WriteLine($"bassia-studio: managing {studio.Options.Server} at {studio.Address}.");
			await studio.WaitForShutdownAsync();
			return 0;
		}
		catch (ArgumentException ex)
		{
			Console.Error.WriteLine($"bassia-studio: {ex.Message}");
			Console.Error.WriteLine(Usage);
			return 2;
		}
	}
}
