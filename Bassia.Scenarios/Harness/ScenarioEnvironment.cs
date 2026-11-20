namespace Bassia.Scenarios.Harness;

using System.Diagnostics;

/// <summary>
/// What the scenarios run on, from the environment, decided once per test run: PostgreSQL, the CLI and server to drive,
/// the network, Claude Code. Every variable is documented in README.md.
/// </summary>
internal static class ScenarioEnvironment
{
	/// <summary>A PostgreSQL connection string to use instead of a container (the same variable as Bassia.Tests).</summary>
	public static string? PostgresVariable => Variable("BASSIA_TEST_POSTGRES");

	/// <summary>An installed <c>bassia</c> to run as a process instead of the in-process CLI.</summary>
	public static string? ExternalCli => Variable("BASSIA_SCENARIO_CLI");

	/// <summary>An installed <c>bassia-server</c> to run as a process instead of the in-process server.</summary>
	public static string? ExternalServer => Variable("BASSIA_SCENARIO_SERVER");

	/// <summary>Keep every test's folders and database schemas, for looking at them after a run.</summary>
	public static bool Keep => Variable("BASSIA_SCENARIO_KEEP") is "1" or "true";

	/// <summary>A folder to write each test's transcript into (Markdown, one file per test).</summary>
	public static string? TranscriptDirectory => Variable("BASSIA_SCENARIO_TRANSCRIPTS");

	/// <summary>The GitHub (or any git) repositories the import scenarios use.</summary>
	public static IReadOnlyList<string> Repositories => Variable("BASSIA_SCENARIO_REPOS") is { } list
		? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		: ["https://github.com/octocat/Hello-World.git", "https://github.com/octocat/Spoon-Knife.git", "https://github.com/sindresorhus/slugify.git"];

	/// <summary>Why a scenario that needs PostgreSQL cannot run, or null when it can.</summary>
	public static string? PostgresSkip => postgresSkip.Value;

	/// <summary>Why a scenario that needs git cannot run, or null when it can.</summary>
	public static string? GitSkip => gitSkip.Value;

	/// <summary>Why a scenario that needs GitHub cannot run, or null when it can.</summary>
	public static string? NetworkSkip => networkSkip.Value;

	/// <summary>Why a scenario that runs the real Claude Code cannot run, or null when it can.</summary>
	public static string? ClaudeSkip => claudeSkip.Value;

	/// <summary>Why a scenario that needs a lazy (FUSE/ProjFS) folder cannot run, or null when it can.</summary>
	public static string? LazySkip => lazySkip.Value;

	private static readonly Lazy<string?> postgresSkip = new(() =>
		PostgresVariable is not null ? null
		: Variable("BASSIA_SCENARIO_DOCKER") is "0" or "false" ? "Needs PostgreSQL: set BASSIA_TEST_POSTGRES (Docker is turned off by BASSIA_SCENARIO_DOCKER=0)."
		: Succeeds("docker", ["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(20)) ? null
		: "Needs PostgreSQL: set BASSIA_TEST_POSTGRES to a connection string, or start Docker so the scenarios can run PostgreSQL in a container.");

	private static readonly Lazy<string?> gitSkip = new(() =>
		Succeeds("git", ["--version"], TimeSpan.FromSeconds(20)) ? null : "Needs git on PATH.");

	private static readonly Lazy<string?> networkSkip = new(() =>
		Variable("BASSIA_SCENARIO_OFFLINE") is "1" or "true" ? "Offline: BASSIA_SCENARIO_OFFLINE is set."
		: GitSkip ?? (Succeeds("git", ["ls-remote", "--heads", Repositories[0]], TimeSpan.FromSeconds(30)) ? null
		: $"Cannot reach {Repositories[0]} (no network?); set BASSIA_SCENARIO_OFFLINE=1 to skip quietly."));

	private static readonly Lazy<string?> claudeSkip = new(() =>
		Variable("BASSIA_SCENARIO_CLAUDE") is not ("1" or "true") ? "Runs the real Claude Code: opt in with BASSIA_SCENARIO_CLAUDE=1."
		: Succeeds("claude", ["--version"], TimeSpan.FromSeconds(30)) ? null
		: "BASSIA_SCENARIO_CLAUDE=1, but 'claude' is not on PATH.");

	private static readonly Lazy<string?> lazySkip = new(() =>
		OperatingSystem.IsLinux() ? (File.Exists("/dev/fuse") && (Succeeds("fusermount3", ["--version"], TimeSpan.FromSeconds(10)) || Succeeds("fusermount", ["--version"], TimeSpan.FromSeconds(10)))
			? null : "Needs FUSE 3 (/dev/fuse and fusermount3).")
		: OperatingSystem.IsWindows() ? null
		: "Lazy folders are exercised on Linux and Windows only.");

	/// <summary>Why a scenario that needs weave's semantic merge driver cannot run, or null when it can.</summary>
	public static string? SemanticSkip => semanticSkip.Value;

	private static readonly Lazy<string?> semanticSkip = new(() =>
		Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Any(directory =>
			File.Exists(Path.Combine(directory, "weave-driver")) || File.Exists(Path.Combine(directory, "weave-driver.exe"))) == true
			? null : "Needs weave's weave-driver on PATH (packaging/Get-Weave.ps1 fetches the pinned build).");

	public static string? Variable(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

	/// <summary>Whether a program runs and exits 0 within the time given.</summary>
	public static bool Succeeds(string program, IReadOnlyList<string> args, TimeSpan timeout)
	{
		try
		{
			var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
			foreach (var arg in args)
			{
				start.ArgumentList.Add(arg);
			}

			using var process = Process.Start(start)!;
			_ = process.StandardOutput.ReadToEndAsync();
			_ = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(timeout))
			{
				process.Kill(entireProcessTree: true);
				return false;
			}

			return process.ExitCode == 0;
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return false;
		}
	}
}

/// <summary>What a scenario needs besides PostgreSQL, which every scenario needs.</summary>
[Flags]
public enum Needs
{
	None = 0,
	Git = 1,
	/// <summary>GitHub (implies git).</summary>
	Network = 2,
	/// <summary>The real Claude Code, opted in.</summary>
	Claude = 4,
	/// <summary>Lazy folders (FUSE on Linux, ProjFS on Windows).</summary>
	Lazy = 8,
	/// <summary>A Unix file system (executable bits, case-sensitive names).</summary>
	Unix = 16,
	/// <summary>weave's semantic merge driver, <c>weave-driver</c>, on PATH.</summary>
	Semantic = 32
}

/// <summary>A scenario test: skipped, with the reason, when what it needs is missing.</summary>
public sealed class ScenarioFactAttribute : FactAttribute
{
	public ScenarioFactAttribute(Needs needs = Needs.None)
	{
		Skip = SkipReason(needs);
	}

	internal static string? SkipReason(Needs needs) =>
		ScenarioEnvironment.PostgresSkip
		?? (needs.HasFlag(Needs.Git) ? ScenarioEnvironment.GitSkip : null)
		?? (needs.HasFlag(Needs.Network) ? ScenarioEnvironment.NetworkSkip : null)
		?? (needs.HasFlag(Needs.Claude) ? ScenarioEnvironment.ClaudeSkip : null)
		?? (needs.HasFlag(Needs.Lazy) ? ScenarioEnvironment.LazySkip : null)
		?? (needs.HasFlag(Needs.Unix) && OperatingSystem.IsWindows() ? "Needs a Unix file system." : null)
		?? (needs.HasFlag(Needs.Semantic) ? ScenarioEnvironment.SemanticSkip : null);
}

/// <summary>A scenario theory; see <see cref="ScenarioFactAttribute"/>.</summary>
public sealed class ScenarioTheoryAttribute : TheoryAttribute
{
	public ScenarioTheoryAttribute(Needs needs = Needs.None)
	{
		Skip = ScenarioFactAttribute.SkipReason(needs);
	}
}
