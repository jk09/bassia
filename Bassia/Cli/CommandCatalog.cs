namespace Bassia.Cli;

/// <summary>
/// What an agent driving the command line needs to know about a command beyond its switches: whether it only reads,
/// and whether it is meant for a person. Help reports both, so an agent can explore freely with the read-only commands
/// that are not interactive.
/// </summary>
internal static class CommandCatalog
{
	/// <summary>
	/// Commands that change nothing in the monorepo. The workspace readers snapshot first, which only records what is
	/// already in the workspace's folder, so they count as reading.
	/// </summary>
	public static readonly IReadOnlySet<string> ReadOnlyCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"status", "version", "help", "config list", "config get",
		"workspace list", "workspace show", "workspace status", "workspace diff", "workspace check", "workspace log",
		"mainline log", "mainline show", "mainline ls", "mainline cat", "mainline changed",
		"lease list", "agent list", "agent show", "agent logs", "agent wait", "scrub", "web"
	};

	/// <summary>Commands meant for a person: feedback asks before anything leaves the machine, the dashboard serves until Ctrl-C.</summary>
	public static readonly IReadOnlySet<string> InteractiveCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"feedback send", "web"
	};

	/// <summary>The command line as a person would type it, quoting words with spaces.</summary>
	public static string Display(IReadOnlyList<string> args) =>
		"bassia " + string.Join(' ', args.Select(word => word.Length == 0 || word.Any(char.IsWhiteSpace) || word.Contains('"')
			? $"\"{word.Replace("\"", "\\\"")}\""
			: word));
}
