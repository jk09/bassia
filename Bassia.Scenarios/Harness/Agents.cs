namespace Bassia.Scenarios.Harness;

/// <summary>The agents the agent scenarios configure as <c>agent.command</c>.</summary>
public static class Agents
{
	/// <summary>
	/// The deterministic stand-in for Claude Code (project Bassia.Scenarios.Agent): it gets the prompt as its last
	/// argument, like <c>claude -p</c>, and carries out the scenarios' plain-English instructions.
	/// </summary>
	public static string StandIn
	{
		get
		{
			var directory = AppContext.BaseDirectory;
			var apphost = Path.Combine(directory, OperatingSystem.IsWindows() ? "bassia-standin-agent.exe" : "bassia-standin-agent");
			return File.Exists(apphost) ? $"\"{apphost}\"" : $"dotnet \"{Path.Combine(directory, "bassia-standin-agent.dll")}\"";
		}
	}

	/// <summary>The real Claude Code, as Bassia runs it by default.</summary>
	public const string Claude = "claude -p --permission-mode acceptEdits";
}
