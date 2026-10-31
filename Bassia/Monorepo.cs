namespace Bassia;

using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Core.Workspaces;
using Bassia.Interop;
using Bassia.Platform;
using Bassia.Stores;
using Tomlyn;
using Tomlyn.Model;

/// <summary>A monorepo folder that cannot be opened, or a request it cannot serve.</summary>
internal sealed class MonorepoException(string message) : Exception(message);

/// <summary>
/// A monorepo folder on this machine: <c>.bassia/repo.toml</c> says whether it is a local monorepo (the version core
/// runs in-process over a local-disk blob store and SQLite) or a clone of a <c>bassia-server</c> (the same core over the
/// server's stores, with a local cache of objects). Both modes give commands the same <see cref="Repository"/>,
/// <see cref="ILanding"/> and <see cref="WorkspaceClient"/>, so every command behaves the same in both.
/// </summary>
internal sealed class Monorepo : IDisposable
{
	public const string DirName = ".bassia";
	public const string RepoFile = "repo.toml";

	private readonly ServerConnection? server;

	private Monorepo(string root, TomlTable settings)
	{
		Root = root;
		Config = new MonorepoConfig(Path.Combine(BassiaDir, MonorepoConfig.FileName));
		Kind = settings.TryGetValue("kind", out var kind) ? (string)kind : "local";
		var chunks = settings.TryGetValue("chunk_average", out var average)
			? new ChunkParameters((int)(long)settings["chunk_min"], (int)(long)average, (int)(long)settings["chunk_max"])
			: ChunkParameters.Default;
		if (IsRemote)
		{
			Url = Config.GetOrNull("server.url") ?? (settings.TryGetValue("url", out var url) ? (string)url : throw new MonorepoException("The clone has no server.url."));
			server = new ServerConnection(Url, Config.GetOrNull("server.token"));
			Objects = new CachingObjectStore(new DiskObjectStore(Path.Combine(BassiaDir, "cache")), new RemoteObjectStore(server));
			Repo = new Repository(Objects, new RemoteViewStore(server), new RemotePathIndex(server), new RemoteLeaseStore(server), chunks);
			Landing = new RemoteLanding(server);
		}
		else
		{
			var disk = new DiskObjectStore(Path.Combine(BassiaDir, "objects"));
			Objects = disk;
			Maintenance = disk;
			State = new SqliteState(Path.Combine(BassiaDir, "state.db"));
			Repo = new Repository(disk, State, State, State, chunks);
			var semantic = MergeDrivers.Semantic(Config.Get("merge.semantic_driver"));
			var validate = Config.GetOrNull("land.validate") is { } command ? new ShellValidationHook(Repo, command) : null;
			LocalLanding = new LandingService(Repo, semantic, validate);
			Landing = LocalLanding;
		}

		// On a Windows Dev Drive (block-cloning ReFS), workspace files are cloned from a local content cache.
		Clones = new CloneCache(Path.Combine(BassiaDir, "clones"), Repo, Cloner ?? FileCloners.Platform());
		var cloneSetting = Config.Get("workspace.clone");
		Workspaces = new WorkspaceClient(Repo, Landing, Path.Combine(BassiaDir, "workspaces"), User,
			(folder, _) => new CheckoutProvider(folder, Repo) { Materializer = Clones.For(folder, cloneSetting) }, monorepoRoot: Root);
	}

	public string Root { get; }

	public string BassiaDir => Path.Combine(Root, DirName);

	public string Kind { get; }

	public bool IsRemote => Kind == "remote";

	public string? Url { get; }

	public MonorepoConfig Config { get; }

	public IObjectStore Objects { get; }

	public IObjectStoreMaintenance? Maintenance { get; }

	public SqliteState? State { get; }

	public Repository Repo { get; }

	public ILanding Landing { get; }

	public LandingService? LocalLanding { get; }

	public WorkspaceClient Workspaces { get; }

	/// <summary>The content cache workspace files are block-cloned from on a Dev Drive.</summary>
	public CloneCache Clones { get; }

	/// <summary>Replaces the platform's file cloner (tests stand one in where the platform has none).</summary>
	internal static IFileCloner? Cloner { get; set; }

	public ServerConnection? Server => server;

	public string User => Config.User;

	public string RunsDir => Path.Combine(BassiaDir, "runs");

	/// <summary>The monorepo folder at or above <paramref name="start"/>, or the one a workspace there belongs to.</summary>
	public static string? FindRoot(string start)
	{
		for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
		{
			if (File.Exists(Path.Combine(dir.FullName, DirName, RepoFile)))
			{
				return dir.FullName;
			}

			if (WorkspaceClient.ReadMarker(dir.FullName) is { Monorepo: { } root } && File.Exists(Path.Combine(root, DirName, RepoFile)))
			{
				return root;
			}
		}

		return null;
	}

	public static Monorepo Open(string root)
	{
		var file = Path.Combine(root, DirName, RepoFile);
		if (!File.Exists(file))
		{
			throw new MonorepoException($"'{root}' is not a Bassia monorepo folder (no {DirName}/{RepoFile}).");
		}

		return new Monorepo(Path.GetFullPath(root), TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file)) ?? new TomlTable());
	}

	/// <summary>The monorepo of the current directory, or a clear error.</summary>
	public static Monorepo Current() =>
		Open(FindRoot(Environment.CurrentDirectory)
			?? throw new MonorepoException($"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia init' or 'bassia clone' first."));

	/// <summary>Creates a local monorepo: a local-disk blob store standing in for the cloud, SQLite for heads, path index and leases.</summary>
	public static async Task<Monorepo> InitAsync(string path, ChunkParameters? chunks = null, CancellationToken ct = default)
	{
		var root = Path.GetFullPath(path);
		Prepare(root);
		chunks ??= ChunkParameters.Default;
		chunks.Validate();
		var settings = new TomlTable
		{
			["kind"] = "local",
			["chunk_min"] = (long)chunks.Min,
			["chunk_average"] = (long)chunks.Average,
			["chunk_max"] = (long)chunks.Max
		};
		WriteSettings(root, settings);
		var monorepo = Open(root);
		await monorepo.Repo.InitializeAsync(monorepo.User, ct);
		return monorepo;
	}

	/// <summary>Creates a clone of a server's monorepo: settings and an empty cache; everything else is read on demand.</summary>
	public static async Task<Monorepo> CloneAsync(string url, string path, string? token, CancellationToken ct = default)
	{
		var root = Path.GetFullPath(path);
		using (var probe = new ServerConnection(url, token ?? Environment.GetEnvironmentVariable("BASSIA_SERVER_TOKEN")))
		{
			var info = await probe.GetAsync<Api.Info>("info", ct);
			Prepare(root);
			WriteSettings(root, new TomlTable
			{
				["kind"] = "remote",
				["url"] = url.TrimEnd('/'),
				["chunk_min"] = (long)info.ChunkMin,
				["chunk_average"] = (long)info.ChunkAverage,
				["chunk_max"] = (long)info.ChunkMax
			});
		}

		var config = new MonorepoConfig(Path.Combine(root, DirName, MonorepoConfig.FileName));
		config.Set("server.url", url.TrimEnd('/'));
		if (token is not null)
		{
			config.Set("server.token", token);
		}

		return Open(root);
	}

	private static void Prepare(string root)
	{
		if (FindRoot(root) is { } existing)
		{
			throw new MonorepoException($"'{root}' is already inside the Bassia monorepo at '{existing}'.");
		}

		if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
		{
			throw new MonorepoException($"'{root}' is not empty; choose a new or empty folder.");
		}

		Directory.CreateDirectory(Path.Combine(root, DirName));
	}

	private static void WriteSettings(string root, TomlTable settings) =>
		File.WriteAllText(Path.Combine(root, DirName, RepoFile),
			"# What this monorepo folder is: a local monorepo, or a clone of a bassia-server. Written by bassia init / clone.\n" + TomlSerializer.Serialize(settings));

	/// <summary>Where a new workspace's folder goes by default.</summary>
	public string DefaultWorkspacePath(string name) => Path.Combine(Root, Config.Get("workspace.dir"), name);

	/// <summary>The workspace the current directory is in, if any.</summary>
	public async Task<WorkspaceState?> CurrentWorkspaceAsync(CancellationToken ct = default)
	{
		if (WorkspaceClient.FindEnclosing(Environment.CurrentDirectory) is not { } found)
		{
			return null;
		}

		return await Repo.FindWorkspaceAsync(found.Marker.Workspace, ct);
	}

	/// <summary>A workspace by reference, or the one the current directory is in.</summary>
	public async Task<WorkspaceState> WorkspaceAsync(string? reference, CancellationToken ct = default)
	{
		if (reference is not null)
		{
			return await Repo.ResolveWorkspaceAsync(reference, ct);
		}

		return await CurrentWorkspaceAsync(ct)
			?? throw new MonorepoException("Name the workspace (-workspace <name>), or run the command inside a workspace's folder.");
	}

	public void Dispose() => server?.Dispose();
}
