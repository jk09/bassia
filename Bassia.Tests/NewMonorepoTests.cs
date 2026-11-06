namespace Bassia.Tests;

using System.Diagnostics;
using Npgsql;
using Tomlyn.Model;
using Xunit.Abstractions;
using static Bassia.Tests.TestEnvironment;

/// <summary>
/// A developer's first session, end to end, on a local monorepo (bassia-d397995f): <c>bassia init</c>, one landing
/// that lays out three projects with a <c>.bassia.toml</c> (a load profile per project, <c>textmerge</c> for Markdown,
/// <c>replay</c> for code), then seven agents started at once with <c>agent start -land -detach</c>, each in a
/// workspace of its own and followed with <c>agent wait</c>. One agent per project adds a file, loading only its
/// project. Two rewrite the same source file: one lands and the other lands after <c>agent replay</c> with its change id.
/// Two edit different lines of the root README: both land, text-merged.
///
/// The agents are a deterministic stand-in (a script, no model) that waits at a barrier until every agent has started,
/// so the runs provably overlap. Detached runs are background <c>bassia</c> processes, so the starts are child
/// processes of the built apphost too. Needs PostgreSQL (<c>BASSIA_TEST_POSTGRES</c>), like every local monorepo.
/// </summary>
public sealed class NewMonorepoTests(ITestOutputHelper output) : IDisposable
{
	private static readonly string[] Projects = ["api", "web", "cli"];

	private const string Readme = "# acme\n\nThree projects: api, web and cli.\n\nBuilt with Bassia.\n";

	private readonly TempDirectory temp = new();

	private string Root => temp.Combine("acme");

	private static string Apphost => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Bassia.exe" : "Bassia");

	public void Dispose()
	{
		NpgsqlConnection.ClearAllPools();
		temp.Dispose();
	}

	/// <summary>One agentic edit: its workspace, load rules (or none), the file it edits, the line, and how (write, append, first: replace line 1).</summary>
	private sealed record Edit(string Name, string? Load, string File, string Line, string Mode);

	[PostgresFact]
	public async Task ANewMonorepo_WithSeveralProjects_LandsAgentsRunningInParallelWorkspaces()
	{
		// ----- a new monorepo, and its projects -----

		var init = await OkAsync(temp.Path, "init", "-path", Root);
		Assert.Equal(0L, init["seq"]);
		var bootstrap = (string)(await OkAsync(Root, "workspace", "create", "bootstrap", "-intent", "lay out the projects"))["path"];
		foreach (var project in Projects)
		{
			Directory.CreateDirectory(Path.Combine(bootstrap, project, "src"));
			await File.WriteAllTextAsync(Path.Combine(bootstrap, project, "README.md"), $"# {project}\n");
			await File.WriteAllTextAsync(Path.Combine(bootstrap, project, "src", "Version.cs"), $"namespace {project};\n");
		}

		await File.WriteAllTextAsync(Path.Combine(bootstrap, "README.md"), Readme);
		await File.WriteAllTextAsync(Path.Combine(bootstrap, ".bassia.toml"), """
			[conflicts]
			default = "replay"

			[[conflicts.rule]]
			paths = ["*.md", "**/*.md"]
			policy = "textmerge"

			[load.profiles]
			api = ["*", "api/"]
			web = ["*", "web/"]
			cli = ["*", "cli/"]
			""");
		var laidOut = await OkAsync(Root, "workspace", "land", "bootstrap");
		Assert.Equal(1L, laidOut["seq"]);
		Assert.Equal(8L, laidOut["touched"]);

		// ----- seven agents at once, each in its own workspace -----

		// On sh the first line passes quotes, $ and a backslash as one argument, which must reach the agent unchanged.
		var tricky = OperatingSystem.IsWindows() ? "public static class Program {}" : "// it's \"cli\" for $USER, see \\docs and `man`";
		var edits = new List<Edit>
		{
			new("api-endpoints", "@api", "api/src/Endpoints.cs", "public static class Endpoints {}", "write"),
			new("web-app", "@web", "web/src/App.cs", "public static class App {}", "write"),
			new("cli-main", "@cli", "cli/src/Program.cs", tricky, "write"),
			new("api-version-a", "@api", "api/src/Version.cs", "// version 2, by agent a", "append"),
			new("api-version-b", "@api", "api/src/Version.cs", "// version 2, by agent b", "append"),
			new("readme-title", null, "README.md", "# acme monorepo", "first"),
			new("readme-footer", null, "README.md", "Agents work in parallel workspaces.", "append"),
		};
		var barrier = temp.Combine("barrier");
		Directory.CreateDirectory(barrier);
		var agent = WriteAgentScript();
		var clock = Stopwatch.StartNew();
		var starts = await Task.WhenAll(edits.Select(edit => BassiaAsync(Root,
			[
				"agent", "start", "-name", edit.Name, "-land", "-detach", .. edit.Load is { } load ? new[] { "-load", load } : [],
				"-run", .. AgentCommand(agent, barrier, edits.Count, edit)
			])));
		foreach (var (edit, start) in edits.Zip(starts))
		{
			Assert.True(start.Exit == 0, $"agent start {edit.Name} failed ({start.Exit}):\n{start.Text}");
		}

		var waits = new Dictionary<string, TomlTable>();
		foreach (var edit in edits)
		{
			var (exit, result, text) = await BassiaAsync(Root, "agent", "wait", edit.Name, "-timeout", "600");
			output.WriteLine($"--- {edit.Name} (exit {exit})\n{text}");
			Assert.True(exit == 0, $"run {edit.Name} did not succeed ({exit}):\n{text}\n{(await BassiaAsync(Root, "agent", "logs", edit.Name)).Text}");
			waits[edit.Name] = result;
		}

		output.WriteLine($"{edits.Count} detached agentic runs in {clock.Elapsed.TotalSeconds:F1} s.");

		// Every agent passed the barrier, so all of them were running at the same time.
		Assert.Equal(edits.Count, Directory.GetFiles(barrier).Length);

		string Outcome(string name) => (string)((TomlTable)waits[name]["last_land"])["outcome"];

		// Each project agent loaded only its own project, and landed.
		foreach (var (edit, start) in edits.Zip(starts).Where(pair => pair.First.Load is not null && pair.First.Mode == "write"))
		{
			Assert.Equal("landed", Outcome(edit.Name));
			var folder = (string)start.Result["path"];
			var project = edit.File.Split('/')[0];
			Assert.True(File.Exists(Path.Combine(folder, project, "src", "Version.cs")), $"{edit.Name} does not hold its own project");
			foreach (var other in Projects.Where(other => other != project))
			{
				Assert.False(File.Exists(Path.Combine(folder, other, "src", "Version.cs")), $"{edit.Name} loaded '{other}'");
			}

			Assert.True(File.Exists(Path.Combine(folder, "README.md")), $"{edit.Name} does not hold the top-level files");
		}

		// Both README edits land: different lines of a Markdown file are text-merged.
		Assert.Equal("landed", Outcome("readme-title"));
		Assert.Equal("landed", Outcome("readme-footer"));

		// The two edits of the same source file: one lands, the other needs a replay on the new mainline.
		var versions = new[] { "api-version-a", "api-version-b" };
		var first = versions.Single(name => Outcome(name) == "landed");
		var second = versions.Single(name => name != first);
		Assert.Equal("needs-replay", Outcome(second));
		var seqs = waits.Values.Where(result => (string)((TomlTable)result["last_land"])["outcome"] == "landed")
			.Select(result => (long)((TomlTable)result["last_land"])["seq"]).Order();
		Assert.Equal(Enumerable.Range(2, edits.Count - 1).Select(seq => (long)seq), seqs);

		// ----- the replay: the same task on the new mainline, with the same change id -----

		var replay = await OkAsync(Root, "agent", "replay", second, "-land");
		Assert.Equal(waits[second]["change_id"], replay["change_id"]);
		Assert.Equal("landed", ((TomlTable)replay["land"])["outcome"]);
		var head = (long)edits.Count + 1;
		Assert.Equal(head, ((TomlTable)replay["land"])["seq"]);
		Assert.Equal("replaced", (await OkAsync(Root, "workspace", "show", second))["status"]);

		// ----- mainline: linear, every landed run in its log, every edit in a fresh workspace -----

		var log = (TomlTableArray)(await OkAsync(Root, "mainline", "log", "-limit", "100"))["landing"];
		Assert.Equal(Enumerable.Range(0, (int)head + 1).Reverse().Select(seq => (long)seq), log.Select(landing => (long)landing["seq"]!));
		var landedRuns = waits.Where(pair => (string)((TomlTable)pair.Value["last_land"])["outcome"] == "landed")
			.Select(pair => (string)((TomlTable)pair.Value["run"])["run_id"]).Append((string)replay["run_id"]).ToHashSet();
		Assert.Equal(landedRuns, log.Where(landing => landing.ContainsKey("run")).Select(landing => (string)landing["run"]!).ToHashSet());

		var tree = (string)(await OkAsync(Root, "workspace", "create", "check"))["path"];
		string Read(string path) => File.ReadAllText(Path.Combine(tree, path)).Replace("\r\n", "\n");
		foreach (var edit in edits.Where(edit => edit.Mode == "write"))
		{
			Assert.Equal(edit.Line + "\n", Read(edit.File));
		}

		var firstLine = edits.Single(edit => edit.Name == first).Line;
		var secondLine = edits.Single(edit => edit.Name == second).Line;
		Assert.Equal($"namespace api;\n{firstLine}\n{secondLine}\n", Read("api/src/Version.cs"));
		Assert.Equal("# acme monorepo" + Readme[Readme.IndexOf('\n')..] + "Agents work in parallel workspaces.\n", Read("README.md"));
		foreach (var project in Projects)
		{
			Assert.Equal($"# {project}\n", Read($"{project}/README.md"));
		}

		var list = await OkAsync(Root, "workspace", "list");
		Assert.Equal(new[] { "check" }, ((TomlTableArray)list["workspace"]).Select(workspace => (string)workspace["name"]!));
	}

	/// <summary>
	/// The agent stand-in: marks its run in the barrier folder, waits until <c>count</c> runs have (a replay finds them all
	/// there already), then edits its file: <c>write</c> it, <c>append</c> a line, or replace the <c>first</c> line.
	/// </summary>
	private string WriteAgentScript()
	{
		if (OperatingSystem.IsWindows())
		{
			var script = temp.Combine("agent.ps1");
			File.WriteAllText(script, """
				param([string] $Barrier, [int] $Count, [string] $File, [string] $Line, [string] $Mode)
				$ErrorActionPreference = 'Stop'
				New-Item -ItemType File -Force -Path (Join-Path $Barrier $env:BASSIA_RUN_ID) | Out-Null
				$deadline = (Get-Date).AddMinutes(6)
				while (@(Get-ChildItem -File $Barrier).Count -lt $Count) {
					if ((Get-Date) -gt $deadline) { Write-Error "barrier timed out: $(@(Get-ChildItem -File $Barrier).Count) of $Count runs arrived in 6 minutes" }
					Start-Sleep -Milliseconds 100
				}
				$path = Join-Path $PWD $File
				switch ($Mode) {
					'append' { [IO.File]::AppendAllText($path, $Line + "`n") }
					'first' { $rest = [IO.File]::ReadAllText($path); [IO.File]::WriteAllText($path, $Line + $rest.Substring($rest.IndexOf("`n"))) }
					default { [IO.File]::WriteAllText($path, $Line + "`n") }
				}
				""");
			return script;
		}

		var shell = temp.Combine("agent.sh");
		File.WriteAllText(shell, """
			#!/bin/sh
			# agent.sh <barrier> <count> <file> <line> <write|append|first>
			set -e
			touch "$1/$BASSIA_RUN_ID"
			waited=0
			while [ "$(ls "$1" | wc -l)" -lt "$2" ]; do
				waited=$((waited + 1))
				if [ "$waited" -gt 3600 ]; then echo "barrier timed out: $(ls "$1" | wc -l) of $2 runs arrived in 6 minutes" >&2; exit 2; fi
				sleep 0.1
			done
			case "$5" in
				append) printf '%s\n' "$4" >> "$3" ;;
				first) { printf '%s\n' "$4"; tail -n +2 "$3"; } > "$3.new" && mv "$3.new" "$3" ;;
				*) printf '%s\n' "$4" > "$3" ;;
			esac
			""".Replace("\r\n", "\n"));
		return shell;
	}

	/// <summary>
	/// The words after <c>-run</c>. On sh they are separate arguments, as a shell hands them over, so bassia must quote
	/// them back into one command line; on Windows one command line, as cmd needs.
	/// </summary>
	private static string[] AgentCommand(string script, string barrier, int count, Edit edit) =>
		OperatingSystem.IsWindows()
			? [$"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\" \"{barrier}\" {count} \"{edit.File}\" \"{edit.Line}\" {edit.Mode}"]
			: ["sh", script, barrier, count.ToString(System.Globalization.CultureInfo.InvariantCulture), edit.File, edit.Line, edit.Mode];

	/// <summary>Runs the built bassia as a child process in <paramref name="directory"/>; started before the first await, so calls run at once.</summary>
	private static async Task<(int Exit, TomlTable Result, string Text)> BassiaAsync(string directory, params string[] args)
	{
		var startInfo = new ProcessStartInfo(Apphost) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in args)
		{
			startInfo.ArgumentList.Add(arg);
		}

		using var process = Process.Start(startInfo)!;
		var standardOutput = process.StandardOutput.ReadToEndAsync();
		var standardError = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		var (stdout, stderr) = (await standardOutput, await standardError);
		var resultText = stdout.Contains(TomlResult.Marker, StringComparison.Ordinal) ? stdout : stderr;
		return (process.ExitCode, Parse(resultText), stdout + stderr);
	}
}
