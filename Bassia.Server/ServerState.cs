namespace Bassia.Server;

using Bassia.Core;
using Bassia.Server.Auth;
using Bassia.Server.Data;
using Bassia.Server.Meta;
using Bassia.Storage;
using Bassia.Storage.Cloud;
using Bassia.Storage.Git;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// Everything a server instance works with, built from its configuration: the database and its stores, the history
/// storage with its git host and meta-repo, the meta-repo views, tokens, authentication and permissions.
/// </summary>
internal sealed class ServerState
{
	private ServerState(ServerConfig config, ServerDatabase database, IComponentStorage storage, IGitHost gitHost, IServerMeta meta, IObjectStore blobs, TimeSpan lockTtl,
		HttpClient http)
	{
		Config = config;
		Database = database;
		Access = new AccessStore(database);
		Records = new DbRecordStore(database);
		Locks = new DbLockStore(database, lockTtl);
		Jobs = new DbJobStore(database);
		TaskCache = new DbTaskCache(database, blobs);
		Changes = new ChangeFeed(database);
		Storage = storage;
		GitHost = gitHost;
		Meta = meta;
		Views = new MetaViews(Path.Combine(config.State, "meta-views"));
		Tokens = new ServerTokens(config);
		Permissions = new Permissions(Access, config);
		Http = http;
		Authenticator = new Authenticator(config, Tokens, Access, () => Meta.Store.LoadUsersAsync(), config.Oidc.Select(provider => new OidcValidator(provider, http)).ToList());
	}

	public ServerConfig Config { get; }

	public ServerDatabase Database { get; }

	public AccessStore Access { get; }

	public DbRecordStore Records { get; }

	public DbLockStore Locks { get; }

	public DbJobStore Jobs { get; }

	public DbTaskCache TaskCache { get; }

	/// <summary>What changed, for the dashboards' live updates.</summary>
	public ChangeFeed Changes { get; }

	public IComponentStorage Storage { get; }

	public IGitHost GitHost { get; }

	public IServerMeta Meta { get; }

	public MetaViews Views { get; }

	public ServerTokens Tokens { get; }

	public Permissions Permissions { get; }

	public Authenticator Authenticator { get; }

	/// <summary>For the identity providers: discovery documents, GitHub's API.</summary>
	public HttpClient Http { get; }

	/// <summary>
	/// Opens the database (migrating it), the history storage and the meta-repo. <paramref name="cloud"/> replaces the AWS
	/// object store and table of a cloud storage (tests use the in-memory ones).
	/// </summary>
	public static async Task<ServerState> CreateAsync(ServerConfig config, HttpClient? http = null, (IObjectStore Objects, IRecordTable Table)? cloud = null)
	{
		Directory.CreateDirectory(config.State);
		var database = ServerDatabase.Open(config.DatabaseProvider, config.DatabaseConnection);
		await database.MigrateAsync();

		IComponentStorage storage;
		IGitHost gitHost;
		IServerMeta meta;
		IObjectStore blobs;
		if (config.StorageKind == "git")
		{
			var root = config.StoragePath!;
			if (!Directory.Exists(Path.Combine(root, Monorepo.MetaRepoFolderName)))
			{
				throw new ServerConfigException($"'{root}' is not a Bassia monorepo (no .bassia folder); create one with 'bassia init' first.");
			}

			var git = new GitComponentStorage(root);
			storage = git;
			gitHost = new LocalGitHost(git, Path.Combine(config.State, "scratch"));
			meta = new LocalServerMeta(root);
			blobs = new FileObjectStore(Path.Combine(config.State, "blobs"));
		}
		else
		{
			var options = new AwsCloudOptions(config.Bucket!, config.Table!, config.Region, config.Prefix, config.S3Url, config.DynamoUrl);
			var (rawObjects, rawTable) = cloud ?? (new S3ObjectStore(options.S3Client(), options.Bucket, options.Prefix), new DynamoRecordTable(options.DynamoClient(), options.Table));
			var (objects, table) = ((IObjectStore)new TimedObjectStore(rawObjects), (IRecordTable)new TimedRecordTable(rawTable));
			var cloudStorage = new CloudComponentStorage(objects, table, Path.Combine(config.State, "cache"));
			storage = cloudStorage;
			gitHost = new CloudGitHost(cloudStorage);
			var history = new HistoryServerMeta(cloudStorage, config.State);
			await history.EnsureAsync();
			meta = history;
			blobs = new PrefixedObjectStore(objects, "server/");
		}

		var settings = await meta.ReadConfigAsync();
		return new ServerState(config, database, storage, gitHost, meta, blobs, Bassia.Locks.LockFilter.Ttl(settings), http ?? new HttpClient());
	}

	/// <summary>The registered components, as of now.</summary>
	public Task<IReadOnlyList<ComponentDefinition>> ComponentsAsync() => Meta.Store.LoadAsync();

	/// <summary>The caller's permission on every registered component.</summary>
	public async Task<IReadOnlyDictionary<string, Permission>> PermissionsOfAsync(Caller caller) => await Permissions.OfAsync(caller, await ComponentsAsync());

	/// <summary>The caller's permission on one component: an admin's is admin, registered or not.</summary>
	public async Task<Permission> LevelAsync(Caller caller, string component) =>
		caller.Admin ? Permission.Admin : (await PermissionsOfAsync(caller)).GetValueOrDefault(component);

	/// <summary>The components the caller may read: an admin every one, registered or not.</summary>
	public async Task<Readable> ReadableAsync(Caller caller) => new(caller.Admin,
		(await PermissionsOfAsync(caller)).Where(pair => pair.Value >= Permission.Read).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal));

	/// <summary>Throws <see cref="ForbiddenException"/> unless the caller holds <paramref name="needed"/> on each component.</summary>
	public async Task RequireAsync(Caller caller, IEnumerable<string> components, Permission needed)
	{
		if (caller.Admin)
		{
			return;
		}

		var levels = await PermissionsOfAsync(caller);
		foreach (var component in components.Distinct(StringComparer.Ordinal))
		{
			var level = levels.GetValueOrDefault(component);
			if (level < needed)
			{
				throw level == Permission.None
					? new NotFoundException($"No component '{component}'.")
					: new ForbiddenException($"'{caller.Id}' may not {Permissions.Name(needed)} component '{component}' (holds {Permissions.Name(level)}).");
			}
		}
	}

	public async Task RequireGlobalAsync(Caller caller, Permission needed)
	{
		if (await Permissions.GlobalAsync(caller) < needed)
		{
			throw new ForbiddenException($"'{caller.Id}' may not do this: it needs {Permissions.Name(needed)} on every component ('*').");
		}
	}

	/// <summary>The components a record (a run's or an integration's, TOML with <c>[[component]] name</c>) is about.</summary>
	public static IReadOnlyList<string> ComponentsOfRecord(string content)
	{
		try
		{
			return TomlSerializer.Deserialize<TomlTable>(content) is { } table && table.TryGetValue("component", out var components) && components is TomlTableArray tables
				? tables.Select(component => component.TryGetValue("name", out var name) ? name as string : null).OfType<string>().ToList()
				: [];
		}
		catch (TomlException)
		{
			return [];
		}
	}
}

/// <summary>An object store under a key prefix of another: the server's blobs beside the component packs.</summary>
internal sealed class PrefixedObjectStore(IObjectStore inner, string prefix) : IObjectStore
{
	public string Location => inner.Location + prefix;

	public Task PutFileAsync(string key, string path) => inner.PutFileAsync(prefix + key, path);

	public Task GetFileAsync(string key, string path) => inner.GetFileAsync(prefix + key, path);

	public Task CopyAsync(string fromKey, string toKey) => inner.CopyAsync(prefix + fromKey, prefix + toKey);

	public async Task<IReadOnlyList<string>> ListAsync(string keyPrefix) => (await inner.ListAsync(prefix + keyPrefix)).Select(key => key[prefix.Length..]).ToList();

	public Task DeleteAsync(string key) => inner.DeleteAsync(prefix + key);
}

/// <summary>What a caller may read: every component (an admin), or the named ones.</summary>
internal sealed class Readable(bool all, IReadOnlySet<string> names)
{
	public bool Contains(string component) => all || names.Contains(component);
}

internal sealed class ForbiddenException(string message) : Exception(message);

internal sealed class NotFoundException(string message) : Exception(message);
