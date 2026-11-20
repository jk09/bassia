namespace Bassia.Scenarios.Harness;

using System.Runtime.CompilerServices;

/// <summary>Process-wide set-up before any scenario runs.</summary>
internal static class Setup
{
	[ModuleInitializer]
	internal static void Initialize()
	{
		// The in-process CLI must never think a person is at a terminal (a bare 'bassia' would print the short help).
		ProgramCli.Interactive = () => false;

		// Imports and exports run git, which needs an identity.
		foreach (var (name, fallback) in new[] { ("GIT_AUTHOR_NAME", "Bassia Scenarios"), ("GIT_COMMITTER_NAME", "Bassia Scenarios"),
			("GIT_AUTHOR_EMAIL", "scenarios@bassia.invalid"), ("GIT_COMMITTER_EMAIL", "scenarios@bassia.invalid") })
		{
			Environment.SetEnvironmentVariable(name, Environment.GetEnvironmentVariable(name) ?? fallback);
		}

		// Per-user state (the crash-report choice, pending reports) of a test run stays out of the user's own.
		Environment.SetEnvironmentVariable("BASSIA_HOME", Path.Combine(Path.GetTempPath(), "bassia-scenarios", "home"));
	}
}
