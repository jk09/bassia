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
/// runs in-process over a local-disk blob store and a local PostgreSQL, which holds heads, path index, leases and trees)
/// or a clone of a <c>bassia-server</c> (the same core over the server's stores, with a local cache of objects and, in
/// the local PostgreSQL, of the trees its work touched). Both modes give commands the same <see cref="Repository"/>,
/// <see cref="ILanding"/> and <see cref="WorkspaceClient"/>, so every command behaves the same in both.
/// </summary>
internal sealed class Monorepo : IDisposable
{
	public const string DirName = ".bassia";
	public const string RepoFile = "repo.toml";

	private readonly ServerConnection? server;
	private readonly List<IAsyncDisposable> postgres = [];

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

			// The trees a clone's work touches are kept in its own schema of the local PostgreSQL when one is configured; a
			// clone without one (a job container) keeps them for the length of the process. The rest stays on the server.
			ITreeStore localTrees;
			if (Config.GetOrNull(LocalPostgres.ConfigKey) is not null)
			{
				var persistent = new PostgresTreeStore(LocalPostgres.Connect(Config, settings), cache: true);
				postgres.Add(persistent);
				localTrees = persistent;
			}
			else
			{
				localTrees = new MemoryTreeStore();
			}

			Repo = new Repository(Objects, new RemoteViewStore(server), new RemotePathIndex(server), new RemoteLeaseStore(server), chunks,
				new CachingTreeStore(localTrees, new RemoteTreeStore(server)), new RemoteWorkspaceIndex(server));
			Landing = new RemoteLanding(server);
			Overview = new RemoteConflictOverview(server);
		}
		else
		{
			var disk = new DiskObjectStore(Path.Combine(BassiaDir, "objects"));
			Objects = disk;
			Maintenance = disk;
			var connection = LocalPostgres.Connect(Config, settings);
			State = new PostgresState(connection);
			var trees = new PostgresTreeStore(connection);
			postgres.Add(State);
			postgres.Add(trees);
			Repo = new Repository(disk, State, State, State, chunks, trees, State);
			if (State.MainlineNeedsBackfill)
			{
				// A monorepo made before the mainline index: its landings are recorded once, from the operation log.
				Repo.BackfillMainlineIndexAsync().GetAwaiter().GetResult();
			}

			if (State.WorkspacesNeedBackfill)
			{
				// A monorepo made before the workspace registry: its workspaces are recorded once, from their heads.
				Repo.SyncWorkspaceIndexAsync().GetAwaiter().GetResult();
			}
			var semantic = MergeDrivers.Semantic(Config.Get("merge.semantic_driver"));
			var validate = Config.GetOrNull("land.validate") is { } command ? new ShellValidationHook(Repo, command) : null;
			LocalLanding = new LandingService(Repo, semantic, validate);
			Landing = LocalLanding;

			// No process runs in the background of a local monorepo: stale candidates are reconciled when the overview is read.
			LocalLanding.Reconciler.ReconcileOnRead = true;
			Overview = LocalLanding.Reconciler;
		}

		// On a Windows Dev Drive (block-cloning ReFS), workspace files are cloned from a local content cache.
		Clones = new CloneCache(Path.Combine(BassiaDir, "clones"), Repo, Cloner ?? FileCloners.Platform());
		var cloneSetting = Config.Get("workspace.clone");
		Workspaces = new WorkspaceClient(Repo, Landing, Path.Combine(BassiaDir, "workspaces"), User,
			(folder, _, lazy) => new CheckoutProvider(folder, Repo) { Materializer = lazy ? null : Clones.For(folder, cloneSetting), Lazy = lazy }, monorepoRoot: Root,
			lazyFolders: Lazy.LazyFolders.Platform(this));
	}

	public string Root { get; }

	public string BassiaDir => Path.Combine(Root, DirName);

	public string Kind { get; }

	public bool IsRemote => Kind == "remote";

	public string? Url { get; }

	public MonorepoConfig Config { get; }

	public IObjectStore Objects { get; }

	public IObjectStoreMaintenance? Maintenance { get; }

	public PostgresState? State { get; }

	public Repository Repo { get; }

	public ILanding Landing { get; }

	public LandingService? LocalLanding { get; }

	/// <summary>The live conflict overview: the server's on a clone, reconciled on read in a local monorepo.</summary>
	public IConflictOverviewSource Overview { get; }

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

	/// <summary>
	/// Creates a local monorepo: a local-disk blob store standing in for the cloud, and a schema of the local PostgreSQL
	/// (<paramref name="postgres"/>, else BASSIA_POSTGRES) for heads, path index, leases and trees.
	/// </summary>
	public static async Task<Monorepo> InitAsync(string path, ChunkParameters? chunks = null, string? postgres = null, CancellationToken ct = default)
	{
		var root = Path.GetFullPath(path);
		LocalPostgres.Require(postgres);
		Prepare(root);
		if (postgres is not null)
		{
			new MonorepoConfig(Path.Combine(root, DirName, MonorepoConfig.FileName)).Set("postgres.connection", postgres);
		}

		chunks ??= ChunkParameters.Default;
		chunks.Validate();
		var settings = new TomlTable
		{
			["kind"] = "local",
			["postgres_schema"] = LocalPostgres.NewSchema(),
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
	public static async Task<Monorepo> CloneAsync(string url, string path, string? token, string? postgres = null, CancellationToken ct = default)
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
				["postgres_schema"] = LocalPostgres.NewSchema(),
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

		if (postgres is not null)
		{
			config.Set("postgres.connection", postgres);
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
	/// <summary>
	/// The context-spec for a new workspace: the rules given (null: none given), else <paramref name="inherited"/> (the
	/// source's, for a stacked or cloned workspace or a replay), else the <c>[context] default</c> of mainline's
	/// <c>.bassia.toml</c>, else everything. <c>@name</c> stands for that profile of <c>[context.profiles]</c>; "all" for
	/// everything. Profiles are expanded: the workspace records the rules themselves.
	/// </summary>
	public async Task<ContextSpec?> ResolveContextAsync(IReadOnlyList<string>? given, ContextSpec? inherited = null, CancellationToken ct = default)
	{
		if (given is null && inherited is not null)
		{
			return inherited;
		}

		RepoPolicy? policy = null;
		async Task<RepoPolicy> Policy() => policy ??= await RepoPolicy.LoadAsync(Repo.Trees, (await Repo.GetMainAsync(ct)).Tree, ct);
		if (given is null)
		{
			var main = await Policy();
			if (main.HasLoadTable && main.ContextDefault.Count == 0 && main.ContextProfiles.Count == 0)
			{
				throw new MonorepoException($"{RepoPolicy.FileName} on mainline still has a [load] table: load rules were replaced by context-specs. " +
					"Rename [load] to [context] (and [load.profiles] to [context.profiles]) and land it, or give -context <rules>.");
			}

			if (main.ContextDefault.Count == 0)
			{
				return null;
			}

			given = main.ContextDefault;
		}

		return await ExpandContextAsync(given, Policy, ct);
	}

	/// <summary>
	/// Whether a new workspace folder is lazy: <c>-materialize</c> if given (<paramref name="given"/>), else this
	/// machine's <c>workspace.materialize</c> setting.
	/// </summary>
	public bool IsLazy(string? given)
	{
		try
		{
			return Materialize.IsLazy(given ?? Config.Get("workspace.materialize"));
		}
		catch (VersionException ex)
		{
			throw new MonorepoException(ex.Message);
		}
	}

	/// <summary>Expands <c>@profile</c> references (from mainline's <c>.bassia.toml</c>) and parses the rules.</summary>
	public async Task<ContextSpec> ExpandContextAsync(IReadOnlyList<string> given, Func<Task<RepoPolicy>>? policy = null, CancellationToken ct = default)
	{
		if (!ContextSpec.NamesProfile(given))
		{
			return ContextSpec.Parse(given);
		}

		policy ??= async () => await RepoPolicy.LoadAsync(Repo.Trees, (await Repo.GetMainAsync(ct)).Tree, ct);
		try
		{
			return ContextSpec.Resolve(given, await policy());
		}
		catch (VersionException ex)
		{
			throw new MonorepoException(ex.Message);
		}
	}

	public async Task<WorkspaceState> WorkspaceAsync(string? reference, CancellationToken ct = default)
	{
		if (reference is not null)
		{
			return await Repo.ResolveWorkspaceAsync(reference, ct);
		}

		return await CurrentWorkspaceAsync(ct)
			?? throw new MonorepoException("Name the workspace (-workspace <name>), or run the command inside a workspace's folder.");
	}

	public void Dispose()
	{
		server?.Dispose();
		foreach (var store in postgres)
		{
			store.DisposeAsync().AsTask().GetAwaiter().GetResult();
		}
	}
}
