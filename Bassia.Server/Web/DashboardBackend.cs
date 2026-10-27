namespace Bassia.Server.Web;

using Bassia.Core;
using Bassia.Locks;
using Bassia.Server.Api;
using Bassia.Server.Auth;
using Bassia.Server.Data;
using Bassia.Split;
using Bassia.Storage;

/// <summary>
/// The server's monorepo as one caller may see it, for the dashboard (<see cref="IMonorepoBackend"/>): the registry
/// without the components they may not read and the references to those, the histories of the readable components
/// only, and the records, lock entries and jobs of which every component is readable - the same rule as the API's. The
/// dashboard only reads; every write through this backend is refused. One is made for each request, so it is the
/// caller's permissions and the registry as of that request.
/// </summary>
internal sealed class DashboardBackend : IMonorepoBackend
{
	private DashboardBackend(ServerState state, Caller caller, Readable readable, Snapshot meta, ConfigSnapshot config)
	{
		Root = Path.Combine(state.Config.State, "dashboard");
		Components = new ReadableStorage(state.Storage, readable);
		Meta = meta;
		Records = new ReadableRecords(state.Records, readable);
		Locks = new ReadableLocks(state.Locks, readable);
		Jobs = new ReadableJobs(state, readable);
		TaskCache = new NoTaskCache(state.TaskCache);
		Identity = new CallerIdentity(caller);
		Config = new FixedConfig(config, state.Config.PublicUrl ?? state.Config.Url);
	}

	public static async Task<DashboardBackend> OpenAsync(ServerState state, Caller caller)
	{
		var readable = await state.ReadableAsync(caller);
		var store = state.Meta.Store;
		var components = (await store.LoadAsync()).ToList();
		var visible = components.Where(component => readable.Contains(component.Name))
			.Select(component => component.References.All(reference => readable.Contains(reference.Name))
				? component
				: component with { References = component.References.Where(reference => readable.Contains(reference.Name)).ToList() })
			.ToList();

		// A rule naming a component the caller may not see would show its name; a pattern or a tag names none.
		bool Shown(IEnumerable<string> selectors) => selectors.All(selector => readable.Contains(selector) || components.All(component => component.Name != selector));
		var boundaries = (await store.LoadBoundariesAsync()).Where(rule => Shown([rule.Source, .. rule.Allow, .. rule.Deny])).ToList();
		var conformance = (await store.LoadConformanceAsync()).Where(rule => Shown(rule.Components.Split(',', StringSplitOptions.TrimEntries))).ToList();
		var meta = new Snapshot(visible, boundaries, conformance, await store.LoadUsersAsync());
		var config = (await state.Meta.ReadConfigAsync()).Limited(readable.Contains);
		return new DashboardBackend(state, caller, readable, meta, config);
	}

	public string Root { get; }

	public IComponentStorage Components { get; }

	public IMetaStore Meta { get; }

	public IRunRecordStore Records { get; }

	public ILockStore Locks { get; }

	public IJobScheduler Jobs { get; }

	public ITaskCache TaskCache { get; }

	public IIdentity Identity { get; }

	public IConfigStore Config { get; }

	private static Exception ReadOnly() => new StorageException("The dashboard is read-only; change the monorepo with the bassia command line.");

	/// <summary>The registry as of the request, limited to what the caller may read; it has no history to show.</summary>
	private sealed class Snapshot(IReadOnlyList<ComponentDefinition> components, IReadOnlyList<BoundaryRule> boundaries, IReadOnlyList<ConformanceRule> conformance,
		IReadOnlyList<UserDefinition> users) : IMetaStore
	{
		public string Location => "server registry";

		public Task<IReadOnlyList<ComponentDefinition>> LoadAsync() => Task.FromResult(components);

		public Task<IReadOnlyList<BoundaryRule>> LoadBoundariesAsync() => Task.FromResult(boundaries);

		public Task<IReadOnlyList<ConformanceRule>> LoadConformanceAsync() => Task.FromResult(conformance);

		public Task<IReadOnlyList<UserDefinition>> LoadUsersAsync() => Task.FromResult(users);

		public Task<IReadOnlyList<LogEntry>> LogAsync(LogQuery query) => Task.FromResult<IReadOnlyList<LogEntry>>([]);

		public Task<string?> CommitAsync(IReadOnlyList<RegistryChange> changes, string message) => throw ReadOnly();
	}

	/// <summary>The histories of the readable components; another does not exist.</summary>
	private sealed class ReadableStorage(IComponentStorage inner, Readable readable) : IComponentStorage
	{
		public bool Exists(string name) => readable.Contains(name) && inner.Exists(name);

		public string LocationOf(string name) => name;

		public IComponentStore Open(string name) => readable.Contains(name) ? inner.Open(name) : throw new StorageException($"No component '{name}'.");

		public Task<IComponentStore> CreateAsync(string name, string defaultBranch, string url, string message) => throw ReadOnly();

		public Task<IComponentStore> ImportAsync(string name, string url) => throw ReadOnly();

		public Task<IComponentStore> ImportHistoryAsync(string name, string url) => throw ReadOnly();

		public Task<IComponentStore> ForkAsync(IComponentStore source, string name, string branch, string commit, IReadOnlyList<string> tags, string url) => throw ReadOnly();

		public Task<IReadOnlyList<SplitPartResult>> SplitAsync(IComponentStore source, SplitHistoryRequest request) => throw ReadOnly();

		public Task DeleteAsync(string name) => throw ReadOnly();

		public IStagingArea BeginStaging() => throw ReadOnly();

		public IComponentWorkspace OpenWorkspace(string name, string directory) => throw ReadOnly();

		public Task MoveBranchesAsync(IReadOnlyList<BranchMove> moves) => throw ReadOnly();
	}

	/// <summary>The run and integration records whose components are all readable, as the API shows them.</summary>
	private sealed class ReadableRecords(DbRecordStore inner, Readable readable) : IRunRecordStore
	{
		private readonly Dictionary<string, bool> visible = new(StringComparer.Ordinal);

		public string Location => inner.Location;

		public Task EnsureRepositoryAsync() => Task.CompletedTask;

		private async Task<bool> VisibleAsync(string refBase)
		{
			if (!visible.TryGetValue(refBase, out var shown))
			{
				var latest = await inner.LatestIndexAsync(refBase);
				shown = latest >= 0 && ServerState.ComponentsOfRecord(await inner.QueryContentAsync(refBase, latest) ?? "").All(readable.Contains);
				visible[refBase] = shown;
			}

			return shown;
		}

		public async Task<int> LatestIndexAsync(string refBase) => await VisibleAsync(refBase) ? await inner.LatestIndexAsync(refBase) : -1;

		public async Task<IReadOnlyList<string>> ListKeysAsync(string refPrefix)
		{
			var keys = new List<string>();
			foreach (var key in await inner.ListKeysAsync(refPrefix))
			{
				if (await VisibleAsync(refPrefix + key))
				{
					keys.Add(key);
				}
			}

			return keys;
		}

		public async Task<(string Content, int Lineage)?> ReadLatestAsync(string refBase, string fileName) =>
			await inner.ReadLatestAsync(refBase, fileName) is { } found && ServerState.ComponentsOfRecord(found.Content).All(readable.Contains) ? found : null;

		public Task<string> WriteAsync(string refBase, int lineage, string fileName, string content, string message) => throw ReadOnly();

		public Task<IReadOnlyList<RunMetadata>> ListLatestAsync() => RunRecords.ListLatestAsync(this);

		public Task<RunMetadata?> LoadLatestAsync(string runId) => RunRecords.LoadLatestAsync(this, runId);

		public Task<string> CommitAsync(RunMetadata metadata) => throw ReadOnly();
	}

	/// <summary>The lock entries whose components are all readable.</summary>
	private sealed class ReadableLocks(DbLockStore inner, Readable readable) : ILockStore
	{
		public string Location => inner.Location;

		public TimeSpan Ttl => inner.Ttl;

		public async Task<bool> HasEntriesAsync() => (await ReadAllAsync()).Count > 0;

		public async Task<IReadOnlyList<LockEntry>> ReadAllAsync() =>
			(await inner.ReadAllAsync()).Where(entry => Bassia.Storage.Remote.Api.ComponentsOf(entry).All(readable.Contains)).ToList();

		public async Task<LockEntry?> FindAsync(string runId) =>
			await inner.FindAsync(runId) is { } entry && Bassia.Storage.Remote.Api.ComponentsOf(entry).All(readable.Contains) ? entry : null;

		public LockState StateOf(LockEntry entry, DateTimeOffset? now = null) => inner.StateOf(entry, now);

		public bool IsWaiting(LockEntry entry, LockWait wait) => inner.IsWaiting(entry, wait);

		public Task<T> UpdateAsync<T>(Func<LockSession, T> change) => throw ReadOnly();
	}

	/// <summary>The jobs whose components are all readable, and their output.</summary>
	private sealed class ReadableJobs(ServerState state, Readable readable) : IJobScheduler
	{
		public string Location => $"{state.Database.Provider}:jobs";

		private async Task<bool> VisibleAsync(string id, IReadOnlyList<string> components) =>
			(await StateEndpoints.ComponentsOfJobAsync(state, id, components)).All(readable.Contains);

		public IJobHandle Attach(string id, string kind, string? log = null, bool detached = false, bool hosted = false) => throw ReadOnly();

		public async Task<JobInfo?> FindAsync(string id) =>
			await state.Jobs.FindAsync(id) is { } found && await VisibleAsync(id, found.Components) ? found.Job : null;

		public async Task<IReadOnlyList<JobInfo>> ListAsync(string kind)
		{
			var jobs = new List<JobInfo>();
			foreach (var found in await state.Jobs.ListAsync(kind))
			{
				if (await VisibleAsync(found.Job.Id, found.Components))
				{
					jobs.Add(found.Job);
				}
			}

			return jobs;
		}

		public bool IsAlive(JobInfo? job) => job is not null && job.Finished is null;

		public Task<DetachedJob> StartDetachedAsync(string id, string workingDirectory, Func<string, IReadOnlyList<string>> arguments) => throw ReadOnly();

		/// <summary>The job's output from <paramref name="offset"/>; a negative offset is that many characters from the end.</summary>
		public async Task<JobLog?> ReadLogAsync(JobInfo job, long offset = 0)
		{
			if (await FindAsync(job.Id) is null || await state.Jobs.ReadLogAsync(job.Id, 0) is not { } whole)
			{
				return null;
			}

			var start = (int)Math.Clamp(offset < 0 ? whole.Text.Length + offset : offset, 0, whole.Text.Length);
			return new JobLog(whole.Text[start..], whole.Next);
		}

		public string ResultLocation(string id) => "";

		public Task RequestStopAsync(string id) => throw ReadOnly();

		public Task ClearStopAsync(string id) => throw ReadOnly();

		public Task<bool> KillAsync(JobInfo job) => throw ReadOnly();
	}

	private sealed class NoTaskCache(DbTaskCache inner) : ITaskCache
	{
		public string Location => "server task cache";

		public Task<TaskResult?> TryRestoreAsync(string key, PlannedTask task, string componentFolder, string logPath) => Task.FromResult<TaskResult?>(null);

		public Task StoreAsync(string key, PlannedTask task, TaskResult result, string componentFolder, IReadOnlyList<string> nested) => throw ReadOnly();

		public async Task<(int Entries, long Bytes)> SizeAsync()
		{
			var size = await inner.SizeAsync();
			return (size.Entries, size.Bytes);
		}

		public Task ClearAsync() => throw ReadOnly();
	}

	private sealed class CallerIdentity(Caller caller) : IIdentity
	{
		public Task<(UserDefinition? User, string Source)> CurrentAsync(UserDirectory users) => Task.FromResult<(UserDefinition?, string)>((caller.User, $"signed in with {caller.Provider}"));
	}

	/// <summary>The monorepo layer of the settings as of the request; the server has no user layer.</summary>
	private sealed class FixedConfig(ConfigSnapshot snapshot, string server) : IConfigStore
	{
		public string LocationOf(ConfigLayer layer) => layer == ConfigLayer.Monorepo ? $"{server} (the server's meta-repo)" : "(none on the server)";

		public ConfigSnapshot Read() => snapshot;

		public Task<string?> SetAsync(ConfigKey key, string value, ConfigLayer layer) => throw ReadOnly();

		public Task<(bool Removed, string? Revision)> UnsetAsync(ConfigKey key, ConfigLayer layer) => throw ReadOnly();
	}
}
