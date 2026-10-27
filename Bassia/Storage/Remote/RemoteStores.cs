namespace Bassia.Storage.Remote;

using System.IO.Compression;
using System.Net;
using Bassia.Core;
using Bassia.Git;
using Bassia.Locks;
using Bassia.Storage.Git;

/// <summary><see cref="IRunRecordStore"/> on the server: every record (runs, integrations) is the server's.</summary>
internal sealed class RemoteRecordStore(ServerClient client) : IRunRecordStore
{
	public string Location => $"{client.Server} (records)";

	public Task EnsureRepositoryAsync() => Task.CompletedTask;

	public async Task<int> LatestIndexAsync(string refBase) =>
		(await client.GetAsync<IndexResponse>($"/records/index?ref={Uri.EscapeDataString(refBase)}")).Index;

	public async Task<IReadOnlyList<string>> ListKeysAsync(string refPrefix) =>
		(await client.GetAsync<KeysResponse>($"/records/keys?prefix={Uri.EscapeDataString(refPrefix)}")).Keys;

	public async Task<(string Content, int Lineage)?> ReadLatestAsync(string refBase, string fileName) =>
		await client.FindAsync<RecordContent>($"/records/latest?ref={Uri.EscapeDataString(refBase)}&file={Uri.EscapeDataString(fileName)}") is { } found
			? (found.Content, found.Lineage)
			: null;

	public async Task<string> WriteAsync(string refBase, int lineage, string fileName, string content, string message)
	{
		try
		{
			return (await client.PostAsync<NameResponse>("/records", new RecordWrite(refBase, lineage, fileName, content, message))).Name;
		}
		catch (ServerException ex) when (ex.Status == HttpStatusCode.Conflict)
		{
			throw new StorageException(ex.Message);
		}
	}

	/// <summary>One request for every run, rather than one per run.</summary>
	public async Task<IReadOnlyList<RunMetadata>> ListLatestAsync() =>
		(await client.GetAsync<List<RecordContent>>("/runs")).Select(found =>
		{
			var metadata = RunMetadata.FromToml(found.Content);
			metadata.Lineage = found.Lineage;
			return metadata;
		}).ToList();

	public Task<RunMetadata?> LoadLatestAsync(string runId) => RunRecords.LoadLatestAsync(this, runId);

	public Task<string> CommitAsync(RunMetadata metadata) => RunRecords.CommitAsync(this, metadata);
}

/// <summary>
/// <see cref="ILockStore"/> on the server. A lock decision is made here, as on every backend, on the entries the server
/// shows (those about components the user may read - the only ones a decision about the user's runs can concern), and
/// written back only while the table is at the version it was read at; when another decision came first, it is made
/// again on the new entries.
/// </summary>
internal sealed class RemoteLockStore(ServerClient client, TimeSpan ttl) : ILockStore
{
	public string Location => $"{client.Server} (locks)";

	public TimeSpan Ttl { get; } = ttl;

	private async Task<(long Version, List<LockEntry> Entries)> ReadTableAsync()
	{
		var table = await client.GetAsync<LockTableResponse>("/locks");
		return (table.Version, table.Entries.Select(LockEntry.FromToml).ToList());
	}

	public async Task<bool> HasEntriesAsync() => (await ReadTableAsync()).Entries.Count > 0;

	public async Task<IReadOnlyList<LockEntry>> ReadAllAsync() => (await ReadTableAsync()).Entries;

	public async Task<LockEntry?> FindAsync(string runId) => (await ReadTableAsync()).Entries.FirstOrDefault(entry => entry.RunId == runId);

	public LockState StateOf(LockEntry entry, DateTimeOffset? now = null) => LockStores.ByLease(entry, Ttl, now);

	public bool IsWaiting(LockEntry entry, LockWait wait) => StateOf(entry) == LockState.Live;

	public async Task<T> UpdateAsync<T>(Func<LockSession, T> change)
	{
		for (var attempt = 0; ; attempt++)
		{
			var (version, entries) = await ReadTableAsync();
			var session = new LockSession(this, entries);
			var result = change(session);
			try
			{
				await client.PostAsync("/locks", new LockCommit(version, session.Saved.Select(entry => entry.ToToml()).ToList(), session.Deleted.ToList()));
				return result;
			}
			catch (ServerException ex) when (ex.Status == HttpStatusCode.Conflict && attempt < 50)
			{
				await Task.Delay(Random.Shared.Next(10, 50 + attempt * 20));
			}
		}
	}
}

/// <summary>
/// <see cref="IJobScheduler"/> on the server: every job of every client is registered there, so anyone allowed sees it,
/// follows its output and stops it. A job's process runs here, on the client that started it: its registration is
/// renewed every few seconds (which also brings a stop request), and its log - for a detached job - is uploaded as it
/// grows.
/// </summary>
internal sealed class RemoteJobScheduler(ServerClient client, LocalJobScheduler local) : IJobScheduler
{
	public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

	private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(30);

	public string Location => $"{client.Server} (jobs)";

	public IJobHandle Attach(string id, string kind, string? log = null, bool detached = false, bool hosted = false)
	{
		client.PostAsync("/jobs", new JobRegistration(id, kind, Environment.ProcessId, LocalJobScheduler.CurrentProcessStart(), log, detached, hosted, Environment.MachineName))
			.GetAwaiter().GetResult();
		return new Handle(client, id, log);
	}

	public async Task<JobInfo?> FindAsync(string id) => await client.FindAsync<JobInfo>($"/jobs/{Uri.EscapeDataString(id)}");

	public async Task<IReadOnlyList<JobInfo>> ListAsync(string kind) => await client.GetAsync<List<JobInfo>>($"/jobs?kind={Uri.EscapeDataString(kind)}");

	/// <summary>The server marks a job finished once it ended or stopped renewing its registration.</summary>
	public bool IsAlive(JobInfo? job) => job is not null && job.Finished is null;

	/// <summary>Starts the background bassia here, as the local scheduler does, and waits until it registered with the server.</summary>
	public async Task<DetachedJob> StartDetachedAsync(string id, string workingDirectory, Func<string, IReadOnlyList<string>> arguments)
	{
		Directory.CreateDirectory(local.Dir);
		var log = local.LogPath(id);
		await File.WriteAllTextAsync(log, "");
		using var process = DetachedProcess.Start(workingDirectory, arguments(log));
		JobInfo? job = null;
		var clock = System.Diagnostics.Stopwatch.StartNew();
		while (clock.Elapsed < RegistrationTimeout && (job = await FindAsync(id)) is null && !process.HasExited)
		{
			await Task.Delay(100);
		}

		job ??= await FindAsync(id);
		var failure = job is null && File.Exists(log)
			? string.Join(" | ", (await File.ReadAllLinesAsync(log)).Where(line => line.Trim().Length > 0).TakeLast(20))
			: "";
		return new DetachedJob(job, log, ResultLocation(id), failure);
	}

	public async Task<JobLog?> ReadLogAsync(JobInfo job, long offset = 0)
	{
		if (offset < 0 && await client.FindAsync<JobLog>($"/jobs/{Uri.EscapeDataString(job.Id)}/log?offset=0") is { } whole)
		{
			var start = (int)Math.Max(0, whole.Text.Length + offset);
			return new JobLog(whole.Text[start..], whole.Next);
		}

		return await client.FindAsync<JobLog>($"/jobs/{Uri.EscapeDataString(job.Id)}/log?offset={offset}");
	}

	public string ResultLocation(string id) => local.ResultLocation(id);

	public Task RequestStopAsync(string id) => client.PostAsync($"/jobs/{Uri.EscapeDataString(id)}/stop", null);

	public Task ClearStopAsync(string id) => client.DeleteAsync($"/jobs/{Uri.EscapeDataString(id)}/stop");

	/// <summary>Only a job of this machine can be killed from here; another machine's is asked to stop.</summary>
	public Task<bool> KillAsync(JobInfo job) => job.Host == Environment.MachineName ? local.KillAsync(job) : Task.FromResult(false);

	private sealed class Handle : IJobHandle
	{
		private readonly ServerClient client;
		private readonly string id;
		private readonly string? log;
		private readonly CancellationTokenSource stop = new();
		private readonly CancellationTokenSource done = new();
		private readonly Task beating;
		private long uploaded;

		public Handle(ServerClient client, string id, string? log)
		{
			this.client = client;
			this.id = id;
			this.log = log;
			beating = Task.Run(BeatAsync);
		}

		public CancellationToken Cancellation => stop.Token;

		public void Cancel() => stop.Cancel();

		private async Task BeatAsync()
		{
			while (!done.IsCancellationRequested)
			{
				try
				{
					await UploadLogAsync();
					var reply = await client.PostAsync<System.Text.Json.JsonElement>($"/jobs/{Uri.EscapeDataString(id)}/heartbeat", null);
					if (reply.TryGetProperty("stop", out var requested) && requested.GetBoolean())
					{
						stop.Cancel();
					}
				}
				catch (MonorepoException)
				{
					// The server is away for a moment; the next beat tries again.
				}

				try
				{
					await Task.Delay(HeartbeatInterval, done.Token);
				}
				catch (OperationCanceledException)
				{
				}
			}
		}

		/// <summary>The log's new text since the last upload.</summary>
		private async Task UploadLogAsync()
		{
			if (log is null || !File.Exists(log))
			{
				return;
			}

			string text;
			await using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				if (stream.Length <= uploaded)
				{
					return;
				}

				stream.Seek(uploaded, SeekOrigin.Begin);
				using var reader = new StreamReader(stream);
				text = await reader.ReadToEndAsync();
				uploaded = stream.Position;
			}

			await client.PostAsync($"/jobs/{Uri.EscapeDataString(id)}/log", new LogChunk(text));
		}

		public void Dispose()
		{
			done.Cancel();
			beating.GetAwaiter().GetResult();
			try
			{
				UploadLogAsync().GetAwaiter().GetResult();
				client.PostAsync($"/jobs/{Uri.EscapeDataString(id)}/finish", null).GetAwaiter().GetResult();
			}
			catch (MonorepoException)
			{
				// The server marks the job finished once its heartbeat stops.
			}

			stop.Dispose();
			done.Dispose();
		}
	}
}

/// <summary>
/// <see cref="ITaskCache"/> on the server: an entry travels as a zip of what the local task cache keeps for it, so
/// restoring and storing are the local cache's, on a scratch copy of the one entry.
/// </summary>
internal sealed class RemoteTaskCache(ServerClient client, string scratchDir) : ITaskCache
{
	public string Location => $"{client.Server} (task cache)";

	private string Scratch() => Path.Combine(scratchDir, $".task-cache-remote-{Guid.NewGuid():N}");

	public async Task<TaskResult?> TryRestoreAsync(string key, PlannedTask task, string componentFolder, string logPath)
	{
		var scratch = Scratch();
		try
		{
			Directory.CreateDirectory(scratch);
			var archive = Path.Combine(scratch, "entry.zip");
			if (!await client.GetFileAsync($"/task-cache/{key}", archive))
			{
				return null;
			}

			var cache = new TaskCache(Path.Combine(scratch, "cache"));
			ZipFile.ExtractToDirectory(archive, Path.Combine(cache.Folder, key));
			return await cache.TryRestoreAsync(key, task, componentFolder, logPath);
		}
		finally
		{
			GitComponentStorage.DeleteDirectory(scratch);
		}
	}

	public async Task StoreAsync(string key, PlannedTask task, TaskResult result, string componentFolder, IReadOnlyList<string> nested)
	{
		var scratch = Scratch();
		try
		{
			var cache = new TaskCache(Path.Combine(scratch, "cache"));
			await cache.StoreAsync(key, task, result, componentFolder, nested);
			var archive = Path.Combine(scratch, "entry.zip");
			ZipFile.CreateFromDirectory(Path.Combine(cache.Folder, key), archive);
			await client.PutFileAsync($"/task-cache/{key}", archive);
		}
		finally
		{
			GitComponentStorage.DeleteDirectory(scratch);
		}
	}

	public async Task<(int Entries, long Bytes)> SizeAsync()
	{
		var size = await client.GetAsync<CacheSize>("/task-cache");
		return (size.Entries, size.Bytes);
	}

	public Task ClearAsync() => client.DeleteAsync("/task-cache");
}

/// <summary><see cref="IIdentity"/> of a clone of a server: who the credential says the user is.</summary>
internal sealed class RemoteIdentity(ServerClient client) : IIdentity
{
	private Me? me;

	public async Task<(UserDefinition? User, string Source)> CurrentAsync(UserDirectory users)
	{
		me ??= await client.GetAsync<Me>("/me");
		var source = $"the login to {client.Server}";
		return (users.Find(me.User) ?? throw new MonorepoException($"{source} is '{me.User}', which this clone's registry does not list; run 'bassia fetch'."), source);
	}
}

/// <summary>
/// The registry of a clone of a server: read from the clone's meta-repo (the server's view of it), changed on the server
/// - which checks and records the change - after which the clone's meta-repo is brought up to the server's.
/// </summary>
internal sealed class RemoteMetaStore(string root, ServerClient client) : IMetaStore
{
	private readonly GitMetaStore local = new(root);

	public string Location => $"{client.Server} (meta-repo)";

	public Task<IReadOnlyList<ComponentDefinition>> LoadAsync() => local.LoadAsync();

	public Task<IReadOnlyList<BoundaryRule>> LoadBoundariesAsync() => local.LoadBoundariesAsync();

	public Task<IReadOnlyList<ConformanceRule>> LoadConformanceAsync() => local.LoadConformanceAsync();

	public Task<IReadOnlyList<UserDefinition>> LoadUsersAsync() => local.LoadUsersAsync();

	public Task<IReadOnlyList<LogEntry>> LogAsync(LogQuery query) => local.LogAsync(query);

	public async Task<string?> CommitAsync(IReadOnlyList<RegistryChange> changes, string message)
	{
		var revision = (await client.PostAsync<RevisionResponse>("/meta/commit", new MetaCommitRequest(changes, message))).Revision;
		await RefreshAsync(root);
		return revision;
	}

	/// <summary>
	/// Brings a clone's meta-repo to the server's: fetched and reset, since the view a user gets is a commit of its own
	/// for each revision, not a continuation of the last one.
	/// </summary>
	public static async Task RefreshAsync(string root)
	{
		var git = GitClient.In(Path.Combine(root, Monorepo.MetaRepoFolderName));
		await git.RunOrThrowAsync(["fetch", "--quiet", "--force", "origin"]);
		await git.RunOrThrowAsync(["reset", "--quiet", "--hard", "origin/HEAD"]);
	}
}

/// <summary>
/// The settings of a clone of a server: the monorepo layer is the server's (read from the clone's meta-repo, changed on
/// the server), the user layer the clone's own file.
/// </summary>
internal sealed class RemoteConfigStore(string root, ServerClient client) : IConfigStore
{
	private readonly LocalConfigStore local = new(root);

	public string LocationOf(ConfigLayer layer) => layer == ConfigLayer.Monorepo ? $"{client.Server} (config.toml)" : local.LocationOf(layer);

	public ConfigSnapshot Read() => local.Read();

	public async Task<string?> SetAsync(ConfigKey key, string value, ConfigLayer layer)
	{
		if (layer == ConfigLayer.User)
		{
			return await local.SetAsync(key, value, layer);
		}

		var revision = (await client.PostAsync<RevisionResponse>("/config", new ConfigChange(key.Key, value))).Revision;
		await RemoteMetaStore.RefreshAsync(root);
		return revision;
	}

	public async Task<(bool Removed, string? Revision)> UnsetAsync(ConfigKey key, ConfigLayer layer)
	{
		if (layer == ConfigLayer.User)
		{
			return await local.UnsetAsync(key, layer);
		}

		var reply = await client.PostAsync<RevisionResponse>("/config", new ConfigChange(key.Key, null));
		await RemoteMetaStore.RefreshAsync(root);
		return (reply.Removed, reply.Revision);
	}
}
