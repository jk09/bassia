namespace Bassia.Storage.Remote;

using Bassia.Storage.Git;

/// <summary>
/// The backend of a clone of a Bassia server (<c>server.url</c>, set by <c>bassia clone -from https://&lt;server&gt;</c>):
/// component histories are git partial clones of the server's gateway, as in any clone; the registry and the
/// monorepo's settings are read from the clone's meta-repo and changed on the server; records, locks, jobs, the task
/// cache and who the user is are the server's.
/// </summary>
internal sealed class RemoteBackend : IMonorepoBackend
{
	private readonly Lazy<ConfigSnapshot> settings;
	private readonly ServerClient client;

	public RemoteBackend(string root, string server)
	{
		Root = root;
		client = new ServerClient(server);
		GitCredentials.UseFor(client.Server);
		Config = new RemoteConfigStore(root, client);
		settings = new Lazy<ConfigSnapshot>(Config.Read);
		Components = new RemoteComponentStorage(new GitComponentStorage(root), client, new Core.Depot(GatewayUrl(server)));
		Meta = new RemoteMetaStore(root, client);
		Records = new RemoteRecordStore(client);
		Identity = new RemoteIdentity(client);
	}

	/// <summary>Where a server's git gateway is: the depot URL of its clones.</summary>
	public static string GatewayUrl(string server) => ServerCredentials.Normalize(server) + "/git";

	public string Root { get; }

	public string Server => client.Server;

	private string WorkspaceDir => Path.GetFullPath(settings.Value.Resolve(ConfigFile.Find("workspace.path")).Value, Root);

	public IComponentStorage Components { get; }

	public IMetaStore Meta { get; }

	public IRunRecordStore Records { get; }

	public ILockStore Locks => new RemoteLockStore(client, Bassia.Locks.LockFilter.Ttl(settings.Value));

	public IJobScheduler Jobs => new RemoteJobScheduler(client, new LocalJobScheduler(WorkspaceDir));

	public ITaskCache TaskCache => new RemoteTaskCache(client, WorkspaceDir);

	public IIdentity Identity { get; }

	public IConfigStore Config { get; }
}

/// <summary>
/// How git gets the credential for a server: <c>bassia credential</c> as its credential helper for the server's URL,
/// given to every git process this one starts (<c>GIT_CONFIG_*</c>), so clones, fetches and pushes through the gateway
/// use the login - refreshed when it expires - without it ever being written into a repository's configuration.
/// </summary>
internal static class GitCredentials
{
	/// <summary>Adds the helper for <paramref name="server"/> to this process's git configuration, once.</summary>
	public static void UseFor(string server)
	{
		var key = $"credential.{ServerCredentials.Normalize(server)}.helper";
		var count = int.TryParse(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"), out var existing) ? existing : 0;
		for (var index = 0; index < count; index++)
		{
			if (Environment.GetEnvironmentVariable($"GIT_CONFIG_KEY_{index}") == key)
			{
				return;
			}
		}

		Environment.SetEnvironmentVariable($"GIT_CONFIG_KEY_{count}", key);
		Environment.SetEnvironmentVariable($"GIT_CONFIG_VALUE_{count}", HelperCommand());
		Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", (count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	/// <summary>
	/// The helper as git runs it (a shell command after <c>!</c>): this bassia - <c>BASSIA_EXE</c>, the running
	/// executable, or the one next to this assembly - with <c>credential</c>.
	/// </summary>
	public static string HelperCommand()
	{
		static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";
		if (Environment.GetEnvironmentVariable("BASSIA_EXE") is { Length: > 0 } configured)
		{
			return $"!{Quote(configured)} credential";
		}

		if (Environment.ProcessPath is { } process && Path.GetFileNameWithoutExtension(process).Equals("bassia", StringComparison.OrdinalIgnoreCase))
		{
			return $"!{Quote(process)} credential";
		}

		var apphost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Bassia.exe" : "Bassia");
		return File.Exists(apphost)
			? $"!{Quote(apphost)} credential"
			: $"!dotnet {Quote(Path.Combine(AppContext.BaseDirectory, "Bassia.dll"))} credential";
	}
}
