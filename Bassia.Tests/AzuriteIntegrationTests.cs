namespace Bassia.Tests;

using System.Diagnostics;
using Azure.Storage.Blobs;
using Bassia.Core.Version;
using Bassia.Server;
using Npgsql;
using Tomlyn.Model;
using Xunit.Abstractions;
using static Bassia.Tests.TestEnvironment;

/// <summary>
/// The Azure backend end to end (bassia-07d97c3d). A <c>bassia-server</c> keeps its objects in Azure Blob Storage on
/// Azurite (and its state in PostgreSQL when <c>BASSIA_TEST_POSTGRES</c> is set). A client imports a git repository
/// (<c>BASSIA_TEST_GIT_URL</c>, by default jk09/example on GitHub), and three clients run agentic edits in parallel, each
/// in its own workspace and as its own <c>bassia</c> process. Disjoint edits land, a conflicting one needs a replay and
/// then lands with its change id, and mainline, a fresh workspace and the blob container agree.
///
/// The agent is a deterministic stand-in (a script, no model): it waits at a barrier until every agent has started, which
/// proves the runs overlap, then makes its one edit. The CLI tests drive <see cref="ProgramCli"/> in-process through
/// the process-wide current directory and console, so the parallel runs are child processes of the built apphost.
///
/// Without Azurite (<c>BASSIA_TEST_AZURE_BLOB</c>) it checks nothing, like the other cloud tests, unless
/// <c>BASSIA_TEST_REQUIRE_AZURITE</c> is set; CI sets it.
/// </summary>
[Trait("Category", "Azurite")]
public sealed class AzuriteIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
	private const string Folder = "example";

	private static readonly string[] Clients = ["alice", "bob", "carol"];

	private readonly TempDirectory temp = new();

	private static string GitUrl => Environment.GetEnvironmentVariable("BASSIA_TEST_GIT_URL") is { Length: > 0 } url ? url : "https://github.com/jk09/example.git";

	private static string Apphost => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Bassia.exe" : "Bassia");

	public Task InitializeAsync() => Task.CompletedTask;

	public Task DisposeAsync()
	{
		NpgsqlConnection.ClearAllPools();
		temp.Dispose();
		return Task.CompletedTask;
	}

	/// <summary>One agentic edit: which client runs it, its workspace, and what it writes (appends, when <c>Append</c>).</summary>
	private sealed record Edit(string Client, string Name, string File, string Line, bool Append);

	[Fact]
	public async Task AMonorepoOnAzurite_ImportsFromGit_AndLandsParallelAgenticEditsFromSeveralClients()
	{
		if (Backends.AzureBlob is not { } azure)
		{
			Assert.True(Environment.GetEnvironmentVariable("BASSIA_TEST_REQUIRE_AZURITE") is not { Length: > 0 },
				"BASSIA_TEST_REQUIRE_AZURITE is set but BASSIA_TEST_AZURE_BLOB is not: there is no Azurite to test against.");
			output.WriteLine("BASSIA_TEST_AZURE_BLOB is not set: nothing to test against.");
			return;
		}

		Assert.True(File.Exists(Apphost), $"'{Apphost}' is missing; build the solution first.");

		// ----- the server on Azurite, in a container of its own -----

		var container = "it" + Guid.NewGuid().ToString("N")[..12];
		var postgres = Backends.FreshPostgres();
		var options = new ServerOptions
		{
			Data = temp.Combine("server"),
			Url = "http://127.0.0.1:0",
			Token = "secret",
			Storage = "azure",
			AzureConnection = azure,
			AzureContainer = container,
			State = postgres is null ? "sqlite" : "postgres",
			Postgres = postgres
		};
		await using var server = new ServerApp(options);
		await server.StartAsync();
		output.WriteLine($"Server {server.Address}: blobs in Azurite container '{container}', state in {options.State}.");

		// ----- several clients, each a folder connected to the server -----

		foreach (var client in Clients)
		{
			await OkAsync(temp.Path, "clone", "-from", server.Address!, "-path", client, "-token", "secret");
		}

		// ----- the git repository, imported through one client -----

		var source = temp.Combine("source");
		await GitAsync(temp.Path, "clone", "--quiet", "--depth", "1", GitUrl, source);
		var expected = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
			.Where(file => !Path.GetRelativePath(source, file).Split(Path.DirectorySeparatorChar).Contains(".git"))
			.ToDictionary(file => Path.GetRelativePath(source, file).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
		Assert.NotEmpty(expected);
		Assert.Contains("README.md", expected.Keys);

		var import = await OkAsync(temp.Combine("alice"), "import", "-git", GitUrl, "-prefix", Folder);
		Assert.Equal(1L, import["seq"]);
		var repository = (TomlTable)((TomlTableArray)import["repository"])[0];
		Assert.Equal((long)expected.Count, repository["files"]);
		output.WriteLine($"Imported {GitUrl}: {expected.Count} file(s) at seq 1.");

		// ----- agentic edits, all at once: two new files per client, and two appends to the same README -----

		var edits = Clients.SelectMany(client => Enumerable.Range(1, 2).Select(index =>
				new Edit(client, $"{client}-{index}", $"{Folder}/{client}-{index}.txt", $"written by agent {index} of {client}", false)))
			.Append(new Edit("alice", "readme-alice", $"{Folder}/README.md", "appended by the agent of alice", true))
			.Append(new Edit("bob", "readme-bob", $"{Folder}/README.md", "appended by the agent of bob", true))
			.ToList();
		var barrier = temp.Combine("barrier");
		Directory.CreateDirectory(barrier);
		var agent = WriteAgentScript();
		var clock = Stopwatch.StartNew();
		var runs = await Task.WhenAll(edits.Select(edit => BassiaAsync(temp.Combine(edit.Client),
			"agent", "start", "-name", edit.Name, "-land", "-run", AgentCommand(agent, barrier, edits.Count, edit))));
		output.WriteLine($"{edits.Count} agentic runs in {clock.Elapsed.TotalSeconds:F1} s.");

		foreach (var (edit, run) in edits.Zip(runs))
		{
			output.WriteLine($"--- {edit.Name} (exit {run.Exit})\n{run.Text}");
		}

		// Every agent passed the barrier, so all of them were running at the same time.
		Assert.Equal(edits.Count, Directory.GetFiles(barrier).Length);

		var results = edits.Zip(runs).ToDictionary(pair => pair.First.Name, pair => pair.Second);
		foreach (var edit in edits.Where(edit => !edit.Append))
		{
			var (exit, result, text) = results[edit.Name];
			Assert.True(exit == 0, $"run {edit.Name} failed ({exit}):\n{text}");
			Assert.Equal("landed", ((TomlTable)result["land"])["outcome"]);
			Assert.Equal(new[] { edit.File }, ((TomlArray)result["changed"]).Cast<string>());
		}

		// The two README edits rewrote the same path: one lands, the other needs a replay on the new mainline.
		var readme = edits.Where(edit => edit.Append).ToList();
		var landed = readme.Where(edit => results[edit.Name].Exit == 0).ToList();
		Assert.True(landed.Count == 1, string.Join("\n", readme.Select(edit => $"{edit.Name}: {results[edit.Name].Text}")));
		var first = landed[0];
		var second = readme.Single(edit => edit != first);
		Assert.Equal("needs-replay", ((TomlTable)results[second.Name].Result["land"])["outcome"]);

		var seqs = runs.Where(run => run.Exit == 0).Select(run => (long)((TomlTable)run.Result["land"])["seq"]).Order().ToList();
		Assert.Equal(Enumerable.Range(2, edits.Count - 1).Select(seq => (long)seq), seqs);

		// ----- the replay: the same task on the new mainline, with the same change id -----

		var replay = await OkAsync(temp.Combine(second.Client), "agent", "replay", second.Name, "-land");
		Assert.Equal(results[second.Name].Result["change_id"], replay["change_id"]);
		Assert.Equal("landed", ((TomlTable)replay["land"])["outcome"]);
		Assert.Equal((long)edits.Count + 1, ((TomlTable)replay["land"])["seq"]);

		// ----- what every client sees: mainline's log, and the tree in a fresh workspace -----

		var log = (TomlTableArray)(await OkAsync(temp.Combine("carol"), "mainline", "log"))["landing"];
		Assert.Equal(edits.Count + 2, log.Count);
		Assert.Equal(Enumerable.Range(0, edits.Count + 2).Reverse().Select(seq => (long)seq), log.Select(landing => (long)landing["seq"]!));
		var runIds = runs.Where(run => run.Exit == 0).Select(run => (string)run.Result["run_id"]).Append((string)replay["run_id"]).ToHashSet();
		Assert.Equal(runIds, log.Where(landing => landing.ContainsKey("run")).Select(landing => (string)landing["run"]!).ToHashSet());

		var check = await OkAsync(temp.Combine("carol"), "workspace", "create", "check");
		var tree = (string)check["path"];
		foreach (var (path, content) in expected.Where(pair => pair.Key != "README.md"))
		{
			Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(tree, Folder, path)));
		}

		foreach (var edit in edits.Where(edit => !edit.Append))
		{
			Assert.Equal(edit.Line, (await File.ReadAllTextAsync(Path.Combine(tree, edit.File))).TrimEnd('\r', '\n'));
		}

		var readmeText = (await File.ReadAllTextAsync(Path.Combine(tree, Folder, "README.md"))).Replace("\r\n", "\n");
		var original = System.Text.Encoding.UTF8.GetString(expected["README.md"]).Replace("\r\n", "\n");
		Assert.StartsWith(original, readmeText);
		Assert.Equal($"{first.Line}\n{second.Line}\n", readmeText[original.Length..]);

		// ----- the objects are in Azurite, intact -----

		var blobs = new List<string>();
		await foreach (var blob in new BlobContainerClient(azure, container).GetBlobsAsync())
		{
			blobs.Add(blob.Name);
		}

		Assert.NotEmpty(blobs);
		Assert.All(blobs, name => Assert.StartsWith("objects/", name));
		var scrub = await new GarbageCollector(server.Repo, server.Maintenance).ScrubAsync();
		Assert.Equal(blobs.Count, scrub.Checked);
		Assert.Empty(scrub.Corrupt);
		Assert.Empty(scrub.Unreadable);
		output.WriteLine($"Mainline at seq {edits.Count + 1}; {blobs.Count} object(s) in Azurite, all intact.");
	}

	/// <summary>
	/// The agent stand-in: marks its run in the barrier folder, waits until <c>count</c> runs have (a replay finds them all
	/// there already), then writes or appends its line.
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
				$deadline = (Get-Date).AddMinutes(2)
				while (@(Get-ChildItem -File $Barrier).Count -lt $Count) {
					if ((Get-Date) -gt $deadline) { Write-Error 'barrier timed out' }
					Start-Sleep -Milliseconds 100
				}
				$text = $Line + "`n"
				if ($Mode -eq 'append') { [IO.File]::AppendAllText((Join-Path $PWD $File), $text) } else { [IO.File]::WriteAllText((Join-Path $PWD $File), $text) }
				""");
			return script;
		}

		var shell = temp.Combine("agent.sh");
		File.WriteAllText(shell, """
			#!/bin/sh
			# agent.sh <barrier> <count> <file> <line> <write|append>
			set -e
			touch "$1/$BASSIA_RUN_ID"
			waited=0
			while [ "$(ls "$1" | wc -l)" -lt "$2" ]; do
				waited=$((waited + 1))
				if [ "$waited" -gt 1200 ]; then echo 'barrier timed out' >&2; exit 2; fi
				sleep 0.1
			done
			if [ "$5" = append ]; then printf '%s\n' "$4" >> "$3"; else printf '%s\n' "$4" > "$3"; fi
			""".Replace("\r\n", "\n"));
		return shell;
	}

	private static string AgentCommand(string script, string barrier, int count, Edit edit)
	{
		var mode = edit.Append ? "append" : "write";
		return OperatingSystem.IsWindows()
			? $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\" \"{barrier}\" {count} \"{edit.File}\" \"{edit.Line}\" {mode}"
			: $"sh '{script}' '{barrier}' {count} '{edit.File}' '{edit.Line}' {mode}";
	}

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
		// A failure's result may go to either stream; the agent's own output never carries the result marker.
		var resultText = stdout.Contains(TomlResult.Marker, StringComparison.Ordinal) ? stdout : stderr;
		return (process.ExitCode, Parse(resultText), stdout + stderr);
	}
}
