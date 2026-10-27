namespace Bassia;

using Bassia.Core;
using Bassia.Git;
using Bassia.Storage;

/// <summary>
/// The monorepo on this machine's disk (<see cref="IMonorepoBackend"/>): component repositories next to the
/// <c>.bassia</c> meta-repo, run and integration records in <c>.agentic-runs</c>, the lock table in <c>.locks</c>, jobs
/// and the task cache in the workspace, the user's own word for who acts, and the settings in the meta-repo's
/// configuration files.
/// </summary>
internal sealed class LocalBackend : IMonorepoBackend
{
	private readonly Lazy<ConfigSnapshot> settings;

	public LocalBackend(string root)
	{
		Root = root;
		Config = new LocalConfigStore(root);
		settings = new Lazy<ConfigSnapshot>(Config.Read);
		Components = new Storage.Git.GitComponentStorage(root);
		Meta = new Storage.Git.GitMetaStore(root);
		Records = new RunMetadataStore(new GitClient(root), Path.Combine(root, Monorepo.RunsRepoFolderName, ".git"));
		Identity = new LocalIdentity(root, Config);
	}

	public string Root { get; }

	/// <summary>The workspace folder (<c>workspace.path</c>), where jobs and the task cache live.</summary>
	public string WorkspaceDir => Path.GetFullPath(settings.Value.Resolve(ConfigFile.Find("workspace.path")).Value, Root);

	public IComponentStorage Components { get; }

	public IMetaStore Meta { get; }

	public IRunRecordStore Records { get; }

	public ILockStore Locks => new Locks.FileLockStore(Path.Combine(Root, Bassia.Locks.FileLockStore.FolderName), Bassia.Locks.LockFilter.Ttl(settings.Value));

	public IJobScheduler Jobs => new LocalJobScheduler(WorkspaceDir);

	public ITaskCache TaskCache => Core.TaskCache.In(WorkspaceDir);

	public IIdentity Identity { get; }

	public IConfigStore Config { get; }
}

/// <summary>
/// The settings in the meta-repo's files (<see cref="IConfigStore"/>): <c>.bassia/config.toml</c>, edited line by line
/// (<see cref="ConfigFile"/>) and committed, and the git-ignored <c>.bassia/config.user.toml</c>.
/// </summary>
internal sealed class LocalConfigStore(string root) : IConfigStore
{
	public string LocationOf(ConfigLayer layer) => ConfigFile.PathOf(root, layer);

	public ConfigSnapshot Read() => ConfigSnapshot.Load(root);

	public async Task<string?> SetAsync(ConfigKey key, string value, ConfigLayer layer)
	{
		ConfigFile.Set(root, key, value, layer);
		return layer == ConfigLayer.Monorepo ? await Storage.Git.GitMetaStore.CommitAllAsync(root, $"Set {key.Key} = {value}") : null;
	}

	public async Task<(bool Removed, string? Revision)> UnsetAsync(ConfigKey key, ConfigLayer layer)
	{
		var removed = ConfigFile.Unset(root, key, layer);
		return (removed, removed && layer == ConfigLayer.Monorepo ? await Storage.Git.GitMetaStore.CommitAllAsync(root, $"Unset {key.Key}") : null);
	}
}

/// <summary>
/// Who acts, by the user's own word (<see cref="IIdentity"/>): <c>BASSIA_USER</c>, else <c>user.id</c> in the user's own
/// configuration layer, else the registered user whose email is the git email configured for the meta-repo.
/// </summary>
internal sealed class LocalIdentity(string root, IConfigStore config) : IIdentity
{
	/// <summary>The environment variable naming the current user; it wins over every other source.</summary>
	public const string Variable = "BASSIA_USER";

	public async Task<(UserDefinition? User, string Source)> CurrentAsync(UserDirectory users)
	{
		if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } fromEnvironment)
		{
			return (Known(users, fromEnvironment.Trim(), Variable), Variable);
		}

		if (config.Read().Raw(ConfigLayer.User, ConfigFile.Find("user.id")) is { Length: > 0 } configured)
		{
			return (Known(users, configured.Trim(), "user.id in config.user.toml"), "user.id in config.user.toml");
		}

		var email = await GitClient.In(Path.Combine(root, Monorepo.MetaRepoFolderName)).RunAsync(["config", "user.email"]);
		if (email.ExitCode == 0 && users.FindByEmail(email.Output.Trim()) is { } byEmail)
		{
			return (byEmail, $"git user.email {email.Output.Trim()}");
		}

		return (null, "none");
	}

	private static UserDefinition Known(UserDirectory users, string id, string source) =>
		users.Find(id) ?? throw new MonorepoException($"{source} names '{id}', which is not a registered user" +
			(users.IsEmpty ? "; register it with 'bassia user add'." : $"; registered: {string.Join(", ", users.Users.Select(user => user.Id))}."));
}
