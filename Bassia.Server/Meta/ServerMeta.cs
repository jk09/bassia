namespace Bassia.Server.Meta;

using Bassia.Git;
using Bassia.Storage;
using Bassia.Storage.Git;

/// <summary>
/// The server's meta-repo: the registry (<see cref="IMetaStore"/>) and the monorepo layer of the settings, and the git
/// repository the gateway serves as <c>.bassia</c>. Every change is one revision; concurrent changes from several server
/// instances are serialized by compare-and-swap on the meta-repo's branch.
/// </summary>
internal interface IServerMeta
{
	IMetaStore Store { get; }

	/// <summary>The settings of the monorepo layer, as of now.</summary>
	Task<ConfigSnapshot> ReadConfigAsync();

	Task<string?> SetConfigAsync(ConfigKey key, string value);

	Task<(bool Removed, string? Revision)> UnsetConfigAsync(ConfigKey key);

	/// <summary>The meta-repo's git folder, up to date, and the commit its default branch is at.</summary>
	Task<(string Directory, string Tip)> GitAsync();
}

/// <summary>
/// The meta-repo of a monorepo on this machine (<c>[storage] kind = "git"</c>): <c>&lt;root&gt;/.bassia</c>, changed
/// through the git meta store, one change at a time.
/// </summary>
internal sealed class LocalServerMeta(string root) : IServerMeta
{
	private readonly SemaphoreSlim gate = new(1, 1);

	public IMetaStore Store => new SerializedMetaStore(new GitMetaStore(root), gate);

	public Task<ConfigSnapshot> ReadConfigAsync() => Task.FromResult(ConfigSnapshot.Load(root));

	public async Task<string?> SetConfigAsync(ConfigKey key, string value)
	{
		await gate.WaitAsync();
		try
		{
			return await new LocalConfigStore(root).SetAsync(key, value, ConfigLayer.Monorepo);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<(bool Removed, string? Revision)> UnsetConfigAsync(ConfigKey key)
	{
		await gate.WaitAsync();
		try
		{
			return await new LocalConfigStore(root).UnsetAsync(key, ConfigLayer.Monorepo);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<(string Directory, string Tip)> GitAsync()
	{
		var directory = Path.Combine(root, Monorepo.MetaRepoFolderName);
		return (directory, await GitClient.In(directory).RunOrThrowAsync(["rev-parse", "HEAD"]));
	}
}

/// <summary>A meta store whose commits wait for each other.</summary>
internal sealed class SerializedMetaStore(IMetaStore inner, SemaphoreSlim gate) : IMetaStore
{
	public string Location => inner.Location;

	public Task<IReadOnlyList<ComponentDefinition>> LoadAsync() => inner.LoadAsync();

	public Task<IReadOnlyList<Core.BoundaryRule>> LoadBoundariesAsync() => inner.LoadBoundariesAsync();

	public Task<IReadOnlyList<Core.ConformanceRule>> LoadConformanceAsync() => inner.LoadConformanceAsync();

	public Task<IReadOnlyList<Core.UserDefinition>> LoadUsersAsync() => inner.LoadUsersAsync();

	public Task<IReadOnlyList<LogEntry>> LogAsync(LogQuery query) => inner.LogAsync(query);

	public async Task<string?> CommitAsync(IReadOnlyList<RegistryChange> changes, string message)
	{
		await gate.WaitAsync();
		try
		{
			return await inner.CommitAsync(changes, message);
		}
		finally
		{
			gate.Release();
		}
	}
}

/// <summary>
/// The meta-repo kept as the history <c>.bassia</c> of the history storage (the cloud's, so every server instance shares
/// it): each instance keeps a working copy, applies a change there with the git meta store, and publishes the commit as a
/// fast-forward of the branch. When another instance moved the branch first, the working copy is brought up to it and
/// the change applied again.
/// </summary>
internal sealed class HistoryServerMeta : IServerMeta
{
	public const string Name = Monorepo.MetaRepoFolderName;
	private const string Branch = "main";
	private const int Attempts = 5;

	private readonly IComponentStorage storage;
	private readonly string root;
	private readonly SemaphoreSlim gate = new(1, 1);

	public HistoryServerMeta(IComponentStorage storage, string scratch)
	{
		this.storage = storage;
		root = Path.Combine(scratch, "meta");
	}

	private string WorkingCopy => Path.Combine(root, Monorepo.MetaRepoFolderName);

	public IMetaStore Store => new HistoryMetaStore(this);

	/// <summary>Creates the meta-repo history with the files <c>bassia init</c> writes, unless it exists.</summary>
	public async Task EnsureAsync()
	{
		if (storage.Exists(Name))
		{
			return;
		}

		var store = await storage.CreateAsync(Name, Branch, "", "Initialize Bassia meta-repo");
		var scratch = Path.Combine(root, $"init-{Guid.NewGuid():N}");
		try
		{
			var workspace = await store.CheckoutAsync((await store.BranchTipAsync(Branch))!, scratch, Branch);
			await File.WriteAllTextAsync(Path.Combine(scratch, "config.toml"), InitCommand.DefaultConfigToml);
			await File.WriteAllTextAsync(Path.Combine(scratch, "components.toml"), InitCommand.DefaultComponentsToml);
			await File.WriteAllTextAsync(Path.Combine(scratch, ".gitignore"), $"# Each user's own settings (bassia config set -user); never committed.\n/{ConfigFile.UserFileName}\n");
			await workspace.CommitAsync("Initialize Bassia meta-repo");
			await workspace.PublishAsync(Branch, []);
		}
		finally
		{
			GitComponentStorage.DeleteDirectory(scratch);
		}
	}

	/// <summary>Brings the working copy to the branch's tip; returns the working copy to publish from.</summary>
	private async Task<IComponentWorkspace> RefreshAsync()
	{
		var store = storage.Open(Name);
		var tip = (await store.BranchTipAsync(Branch))!;
		if (!Directory.Exists(WorkingCopy))
		{
			Directory.CreateDirectory(root);
			return await store.CheckoutAsync(tip, WorkingCopy, Branch);
		}

		var git = GitClient.In(WorkingCopy);
		if (await git.RunOrThrowAsync(["rev-parse", "HEAD"]) != tip)
		{
			await git.RunOrThrowAsync(["fetch", "--quiet", "--no-tags", store.Location, $"refs/heads/{Branch}"]);
			await git.RunOrThrowAsync(["reset", "--quiet", "--hard", tip]);
			await git.RunOrThrowAsync(["clean", "--quiet", "-fdx"]);
		}

		return storage.OpenWorkspace(Name, WorkingCopy);
	}

	/// <summary>Runs <paramref name="read"/> on the up-to-date working copy (its monorepo root).</summary>
	internal async Task<T> ReadAsync<T>(Func<string, Task<T>> read)
	{
		await gate.WaitAsync();
		try
		{
			await RefreshAsync();
			return await read(root);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Applies <paramref name="change"/> to the working copy (it commits there) and publishes the commit; on a lost race,
	/// does it again on the new tip.
	/// </summary>
	internal async Task<T> ChangeAsync<T>(Func<string, Task<T>> change)
	{
		await gate.WaitAsync();
		try
		{
			for (var attempt = 1; ; attempt++)
			{
				var workspace = await RefreshAsync();
				var result = await change(root);
				try
				{
					await workspace.PublishAsync(Branch, []);
					return result;
				}
				catch (StorageException) when (attempt < Attempts)
				{
					// Another instance changed the meta-repo first: start again from its revision.
				}
			}
		}
		finally
		{
			gate.Release();
		}
	}

	public Task<ConfigSnapshot> ReadConfigAsync() => ReadAsync(root => Task.FromResult(ConfigSnapshot.Load(root)));

	public Task<string?> SetConfigAsync(ConfigKey key, string value) => ChangeAsync(root => new LocalConfigStore(root).SetAsync(key, value, ConfigLayer.Monorepo));

	public Task<(bool Removed, string? Revision)> UnsetConfigAsync(ConfigKey key) => ChangeAsync(root => new LocalConfigStore(root).UnsetAsync(key, ConfigLayer.Monorepo));

	public async Task<(string Directory, string Tip)> GitAsync()
	{
		var store = storage.Open(Name);
		return (store.Location, (await store.BranchTipAsync(Branch))!);
	}

	private sealed class HistoryMetaStore(HistoryServerMeta meta) : IMetaStore
	{
		public string Location => $"{meta.storage.LocationOf(Name)} (history)";

		public Task<IReadOnlyList<ComponentDefinition>> LoadAsync() => meta.ReadAsync(root => new GitMetaStore(root).LoadAsync());

		public Task<IReadOnlyList<Core.BoundaryRule>> LoadBoundariesAsync() => meta.ReadAsync(root => new GitMetaStore(root).LoadBoundariesAsync());

		public Task<IReadOnlyList<Core.ConformanceRule>> LoadConformanceAsync() => meta.ReadAsync(root => new GitMetaStore(root).LoadConformanceAsync());

		public Task<IReadOnlyList<Core.UserDefinition>> LoadUsersAsync() => meta.ReadAsync(root => new GitMetaStore(root).LoadUsersAsync());

		public Task<IReadOnlyList<LogEntry>> LogAsync(LogQuery query) => meta.ReadAsync(root => new GitMetaStore(root).LogAsync(query));

		public Task<string?> CommitAsync(IReadOnlyList<RegistryChange> changes, string message) =>
			meta.ChangeAsync(root => new GitMetaStore(root).CommitAsync(changes, message));
	}
}
