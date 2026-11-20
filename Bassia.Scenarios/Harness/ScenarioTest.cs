namespace Bassia.Scenarios.Harness;

using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Xunit.Abstractions;

/// <summary>
/// The base of every scenario: a temporary folder, a <c>bassia-server</c> on memory blobs and a fresh PostgreSQL
/// schema (started on first use), clients made with <c>bassia clone</c>, and a transcript of every command and its
/// result, written to the test's output (and to <c>BASSIA_SCENARIO_TRANSCRIPTS</c>).
/// </summary>
public abstract class ScenarioTest : IAsyncLifetime
{
	private readonly ITestOutputHelper output;
	private readonly StringBuilder transcript = new();
	private readonly List<ScenarioClient> clients = [];
	private readonly List<string> schemas = [];
	private ScenarioServer? server;

	protected ScenarioTest(ITestOutputHelper output)
	{
		this.output = output;
		Folder = Path.Combine(Path.GetTempPath(), "bassia-scenarios", $"{GetType().Name}-{Guid.NewGuid().ToString("N")[..8]}");
		Directory.CreateDirectory(Folder);
	}

	/// <summary>This test's own folder: clients, git repositories and the server's data are made in it.</summary>
	public string Folder { get; }

	/// <summary>Switches added to the server's command line (e.g. <c>-validate</c>).</summary>
	protected virtual IReadOnlyList<string> ServerArguments => [];

	/// <summary>The server, started on first use.</summary>
	protected async Task<ScenarioServer> ServerAsync()
	{
		if (server is null)
		{
			server = await ScenarioServer.StartAsync(Path.Combine(Folder, "server"), ServerArguments);
			schemas.Add(server.Schema);
			Note($"bassia-server at {server.Url} (blobs in memory, state in PostgreSQL schema {server.Schema})");
		}

		return server;
	}

	public Task InitializeAsync() => Task.CompletedTask;

	public async Task DisposeAsync()
	{
		foreach (var client in clients)
		{
			// Detached runs and lazy-folder servers may still be running in the client: stop them before cleaning up.
			await client.StopBackgroundAsync();
		}

		if (server is not null)
		{
			await server.DisposeAsync();
		}

		if (ScenarioEnvironment.TranscriptDirectory is { } directory)
		{
			Directory.CreateDirectory(directory);
			await File.WriteAllTextAsync(Path.Combine(directory, $"{GetType().Name}.{TestName}.md"), transcript.ToString());
		}

		if (ScenarioEnvironment.Keep)
		{
			output.WriteLine($"Kept {Folder} and PostgreSQL schemas {string.Join(", ", schemas.Concat(clients.Select(c => c.Schema).OfType<string>()))}.");
			return;
		}

		foreach (var schema in schemas.Concat(clients.Select(client => client.Schema).OfType<string>()))
		{
			await ScenarioPostgres.DropSchemaAsync(schema);
		}

		DeleteFolder(Folder);
	}

	/// <summary>The running test's name, for the transcript's file name.</summary>
	private string TestName => (output.GetType().GetField("test", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(output) as ITest)
		?.TestCase.TestMethod.Method.Name ?? "test";

	/// <summary>
	/// A client of the server: <c>bassia clone -from &lt;server&gt; -path &lt;folder&gt; -token &lt;t&gt;</c>, acting as
	/// <paramref name="user"/> (<c>BASSIA_USER</c>). Its trees are cached in a schema of the local PostgreSQL.
	/// </summary>
	protected async Task<ScenarioClient> CloneAsync(string name = "alice", string? user = null)
	{
		var running = await ServerAsync();
		var client = new ScenarioClient(this, Path.Combine(Folder, name), user ?? name);
		var cloned = await client.OkInAsync(Folder, "clone", "-from", running.Url, "-path", client.Root, "-token", running.Token);
		Assert.Equal(running.Url.TrimEnd('/'), cloned.Str("url").TrimEnd('/'));
		clients.Add(client);
		return client;
	}

	/// <summary>A local monorepo (<c>bassia init</c>): the core in-process over a local-disk blob store and PostgreSQL.</summary>
	protected async Task<ScenarioClient> InitLocalAsync(string name = "local", string? user = null)
	{
		var client = new ScenarioClient(this, Path.Combine(Folder, name), user ?? "alice");
		await client.OkInAsync(Folder, "init", "-path", client.Root);
		clients.Add(client);
		return client;
	}

	/// <summary>Environment every command of this test gets (besides the client's user).</summary>
	internal async Task<Dictionary<string, string?>> EnvironmentAsync(string user) => new()
	{
		["BASSIA_USER"] = user,
		// A client keeps its trees, and a local monorepo its state, in a schema of its own of this database.
		["BASSIA_POSTGRES"] = await ScenarioPostgres.ConnectionAsync(),
		["BASSIA_SERVER_TOKEN"] = null
	};

	/// <summary>Writes a line to the transcript.</summary>
	public void Note(string line)
	{
		transcript.AppendLine(line).AppendLine();
		output.WriteLine(line);
	}

	internal void Record(CliResult result, string? user)
	{
		var shown = new StringBuilder();
		shown.AppendLine($"```").AppendLine($"[{user}] {Relative(result.Directory)}$ {result.CommandLine}   -> exit {result.ExitCode}");
		var text = (result.ExitCode == 0 ? result.Stdout : result.Stderr + result.Stdout).TrimEnd();
		shown.AppendLine(text.Length > 6000 ? text[..6000] + "\n... (truncated)" : text).AppendLine("```");
		transcript.Append(shown).AppendLine();
		output.WriteLine(shown.ToString());
	}

	private string Relative(string directory) => directory.StartsWith(Folder, StringComparison.Ordinal) ? "." + directory[Folder.Length..].Replace('\\', '/') : directory;

	internal static void DeleteFolder(string path)
	{
		for (var attempt = 0; attempt < 5 && Directory.Exists(path); attempt++)
		{
			try
			{
				foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
				{
					File.SetAttributes(file, FileAttributes.Normal);
				}

				Directory.Delete(path, recursive: true);
			}
			catch (IOException)
			{
				Thread.Sleep(200);
			}
			catch (UnauthorizedAccessException)
			{
				Thread.Sleep(200);
			}
		}
	}
}

/// <summary>
/// A client folder (a clone of the server, or a local monorepo) and the person or agent using it: runs <c>bassia</c>
/// there and edits workspace folders the way an editor or agent would.
/// </summary>
public sealed class ScenarioClient(ScenarioTest test, string root, string user)
{
	public string Root { get; } = root;

	public string User { get; } = user;

	/// <summary>The PostgreSQL schema the client's <c>.bassia/repo.toml</c> names, if any.</summary>
	public string? Schema
	{
		get
		{
			var file = Path.Combine(Root, ".bassia", "repo.toml");
			return File.Exists(file) && TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file)) is { } settings
				&& settings.TryGetValue("postgres_schema", out var schema) ? schema as string : null;
		}
	}

	/// <summary>Where <c>workspace create &lt;name&gt;</c> puts a workspace's folder by default.</summary>
	public string WorkspacePath(string workspace) => Path.Combine(Root, "workspaces", workspace);

	public Task<CliResult> RunAsync(params string[] args) => RunInAsync(Root, args);

	public async Task<CliResult> RunInAsync(string directory, params string[] args)
	{
		var result = await Cli.RunAsync(directory, await test.EnvironmentAsync(User), args);
		test.Record(result, User);
		return result;
	}

	/// <summary>Runs bassia in the client folder and asserts it succeeded (exit code 0, <c>ok = true</c>).</summary>
	public Task<TomlTable> OkAsync(params string[] args) => OkInAsync(Root, args);

	public async Task<TomlTable> OkInAsync(string directory, params string[] args)
	{
		var result = await RunInAsync(directory, args);
		Assert.True(result.ExitCode == 0, $"Expected success: {result.CommandLine} exited {result.ExitCode}\n{result.Stderr}\n{result.Stdout}");
		Assert.True(result.HasResult, $"No TOML result: {result.CommandLine}\n{result.Stdout}");
		Assert.True(result.Result.Bool("ok"), $"ok = false: {result.CommandLine}\n{result.Stdout}");
		return result.Result;
	}

	/// <summary>Runs bassia and asserts it failed with a result (exit code 1, <c>ok = false</c> and an <c>error</c>).</summary>
	public async Task<TomlTable> FailAsync(params string[] args)
	{
		var result = await RunAsync(args);
		Assert.True(result.ExitCode == 1, $"Expected failure (exit 1): {result.CommandLine} exited {result.ExitCode}\n{result.Stdout}\n{result.Stderr}");
		Assert.False(result.Result.Bool("ok"));
		Assert.False(string.IsNullOrWhiteSpace(result.Result.Str("error")));
		return result.Result;
	}

	// ----- editing workspace folders, as an editor or agent would -----

	public string PathIn(string workspace, string relative) => Path.Combine(WorkspacePath(workspace), relative.Replace('/', Path.DirectorySeparatorChar));

	public void Write(string workspace, string relative, string content)
	{
		var path = PathIn(workspace, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	public string Read(string workspace, string relative) => File.ReadAllText(PathIn(workspace, relative));

	public bool Exists(string workspace, string relative) => File.Exists(PathIn(workspace, relative));

	public void Delete(string workspace, string relative) => File.Delete(PathIn(workspace, relative));

	/// <summary>
	/// The usual round trip in one call: a workspace with these files written (null deletes), landed on mainline.
	/// Returns the new seq.
	/// </summary>
	public async Task<long> LandFilesAsync(string workspace, string intent, IReadOnlyDictionary<string, string?> files)
	{
		await OkAsync("workspace", "create", workspace, "-intent", intent);
		foreach (var (path, content) in files)
		{
			if (content is null)
			{
				Delete(workspace, path);
			}
			else
			{
				Write(workspace, path, content);
			}
		}

		var landed = await OkAsync("workspace", "land", workspace);
		Assert.Equal("landed", landed.Str("outcome"));
		return landed.Long("seq");
	}

	/// <summary>Mainline's current seq, from <c>bassia status</c>.</summary>
	public async Task<long> MainSeqAsync() => (await OkAsync("status")).Long("seq");

	/// <summary>A file of mainline (<c>mainline cat</c>).</summary>
	public async Task<string> CatAsync(string path, long? seq = null) =>
		(await OkAsync(seq is null ? ["mainline", "cat", path] : ["mainline", "cat", path, "-seq", seq.Value.ToString()])).Str("content");

	/// <summary>Stops the runs this client started that are still going, so the test can clean up.</summary>
	internal async Task StopBackgroundAsync()
	{
		if (!Directory.Exists(Path.Combine(Root, ".bassia")))
		{
			return;
		}

		try
		{
			var runs = await Cli.RunAsync(Root, await test.EnvironmentAsync(User), ["agent", "list", "-status", "running"]);
			foreach (var run in runs.Result.Tables("run"))
			{
				if (run.StrOrNull("run_id") is { } id)
				{
					await Cli.RunAsync(Root, await test.EnvironmentAsync(User), ["agent", "stop", id]);
				}
			}

			var workspaces = await Cli.RunAsync(Root, await test.EnvironmentAsync(User), ["workspace", "list"]);
			foreach (var workspace in workspaces.Result.Tables("workspace"))
			{
				if (workspace.StrOrNull("materialize") == "lazy" && workspace.StrOrNull("name") is { } name)
				{
					await Cli.RunAsync(Root, await test.EnvironmentAsync(User), ["workspace", "unmount", name]);
				}
			}
		}
		catch (Exception)
		{
			// Best effort.
		}
	}
}
