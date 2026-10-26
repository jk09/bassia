namespace Bassia.Cli;

/// <summary>
/// What an agent driving the command line needs to know about a command beyond its switches: whether it only reads,
/// and whether it is meant for a person. Help reports both (<see cref="CommandSpec.ReadOnly"/>,
/// <see cref="CommandSpec.Interactive"/>), so an agent can explore freely with the read-only commands that are not interactive.
/// </summary>
internal static class CommandCatalog
{
	/// <summary>
	/// Commands that only read: they change nothing in the monorepo (<c>graph -out</c> writes just the file it is given).
	/// Anything else - including every command added later - changes something, so an agent
	/// should run it only when that change is what it was asked for.
	/// </summary>
	public static readonly IReadOnlySet<string> ReadOnlyCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"status", "config list", "config get", "version",
		"component list", "component show", "component survey",
		"tag list", "tag show", "graph", "log", "boundary list", "boundary check", "task list", "affected", "conformance list", "conformance check", "template list", "user list", "user show", "whoami",
		"agent list", "agent show", "agent logs", "agent wait", "agent diff",
		"lock list", "lock check",
		"integration plan", "integration list", "integration show", "integration logs", "integration wait",
		"skill list", "skill show", "help", "web",
		"view list", "view show", "view ls"
	};

	/// <summary>
	/// Commands meant for a person: the guide reads answers from stdin, feedback asks before anything leaves the machine
	/// (unless -yes), and the dashboard serves until Ctrl-C.
	/// </summary>
	public static readonly IReadOnlySet<string> InteractiveCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"tutorial", "feedback send", "web"
	};

	/// <summary>The command line as a person would type it, quoting words with spaces.</summary>
	public static string Display(IReadOnlyList<string> args) =>
		"bassia " + string.Join(' ', args.Select(word => word.Length == 0 || word.Any(char.IsWhiteSpace) || word.Contains('"')
			? $"\"{word.Replace("\"", "\\\"")}\""
			: word));
}
