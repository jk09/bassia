namespace Bassia.Server.Data;

using System.Text.Json;
using Bassia.Locks;
using Bassia.Storage;
using Bassia.Storage.Cloud;
using Bassia.Storage.Remote;

/// <summary>
/// <see cref="IRunRecordStore"/> in the server's database: one row per version of a record. A version is written once:
/// writing one that exists, or skipping one, is refused, so two writers of the same record never both succeed.
/// </summary>
internal sealed class DbRecordStore(ServerDatabase database) : IRunRecordStore
{
	public string Location => $"{database.Provider}:records";

	public Task EnsureRepositoryAsync() => Task.CompletedTask;

	public async Task<int> LatestIndexAsync(string refBase) =>
		(await database.QueryAsync("SELECT MAX(lineage) FROM records WHERE ref_base = @ref;", reader => reader.IsDBNull(0) ? -1 : Convert.ToInt32(reader.GetValue(0)),
			("@ref", refBase)))[0];

	public async Task<IReadOnlyList<string>> ListKeysAsync(string refPrefix) =>
		(await database.QueryAsync("SELECT DISTINCT ref_base FROM records WHERE substr(ref_base, 1, @length) = @prefix ORDER BY ref_base;",
			reader => reader.GetString(0)[refPrefix.Length..], ("@length", refPrefix.Length), ("@prefix", refPrefix))).ToList();

	public async Task<(string Content, int Lineage)?> ReadLatestAsync(string refBase, string fileName)
	{
		var rows = await database.QueryAsync(
			"SELECT content, lineage, file_name FROM records WHERE ref_base = @ref ORDER BY lineage DESC LIMIT 1;",
			reader => (Content: reader.GetString(0), Lineage: Convert.ToInt32(reader.GetValue(1)), File: reader.GetString(2)), ("@ref", refBase));
		return rows.Count == 0 || rows[0].File != fileName ? null : (rows[0].Content, rows[0].Lineage);
	}

	/// <summary>The file of one version of a record, or null.</summary>
	public async Task<string?> QueryContentAsync(string refBase, int lineage) =>
		(await database.QueryAsync("SELECT content FROM records WHERE ref_base = @ref AND lineage = @lineage;", reader => reader.GetString(0),
			("@ref", refBase), ("@lineage", lineage))).FirstOrDefault();

	public Task<string> WriteAsync(string refBase, int lineage, string fileName, string content, string message) =>
		database.TransactAsync(async (connection, transaction) =>
		{
			var latest = (await ServerDatabase.QueryAsync(connection, transaction, "SELECT MAX(lineage) FROM records WHERE ref_base = @ref;",
				reader => reader.IsDBNull(0) ? -1 : Convert.ToInt32(reader.GetValue(0)), ("@ref", refBase)))[0];
			if (lineage != latest + 1)
			{
				throw new StorageException($"Version {lineage} of '{refBase}' cannot follow version {latest}.");
			}

			await ServerDatabase.ExecuteAsync(connection, transaction,
				"INSERT INTO records (ref_base, lineage, file_name, content, message, created) VALUES (@ref, @lineage, @file, @content, @message, @created);",
				("@ref", refBase), ("@lineage", lineage), ("@file", fileName), ("@content", content), ("@message", message), ("@created", ServerDatabase.Now()));
			await ChangeFeed.AddAsync(connection, transaction, refBase.StartsWith(Bassia.Integration.IntegrationRecord.RefPrefix, StringComparison.Ordinal) ? "integration" : "run",
				refBase, ServerState.ComponentsOfRecord(content));
			return $"{refBase}/{lineage}";
		});

	public Task<IReadOnlyList<RunMetadata>> ListLatestAsync() => RunRecords.ListLatestAsync(this);

	public Task<RunMetadata?> LoadLatestAsync(string runId) => RunRecords.LoadLatestAsync(this, runId);

	public Task<string> CommitAsync(RunMetadata metadata) => RunRecords.CommitAsync(this, metadata);
}

/// <summary>
/// <see cref="ILockStore"/> in the server's database. The table has one version, raised by every decision; a decision
/// reads the entries and the version, decides, and writes only while the version is unchanged - as one transaction on
/// the server, or from a client (<see cref="CommitAsync"/>, <c>POST /api/v1/locks</c>) as compare-and-swap. A decision
/// that loses is decided again on the new entries.
/// </summary>
internal sealed class DbLockStore(ServerDatabase database, TimeSpan ttl) : ILockStore
{
	public string Location => $"{database.Provider}:locks";

	public TimeSpan Ttl { get; } = ttl;

	public async Task<bool> HasEntriesAsync() =>
		(await database.QueryAsync("SELECT COUNT(*) FROM lock_entries;", reader => Convert.ToInt64(reader.GetValue(0))))[0] > 0;

	public async Task<IReadOnlyList<LockEntry>> ReadAllAsync() => (await ReadTableAsync()).Entries;

	public async Task<LockEntry?> FindAsync(string runId) =>
		(await database.QueryAsync("SELECT entry FROM lock_entries WHERE run_id = @run;", reader => LockEntry.FromToml(reader.GetString(0)), ("@run", runId)))
		.FirstOrDefault();

	public LockState StateOf(LockEntry entry, DateTimeOffset? now = null) => LockStores.ByLease(entry, Ttl, now);

	public bool IsWaiting(LockEntry entry, LockWait wait) => StateOf(entry) == LockState.Live;

	/// <summary>The table's version and every entry, oldest run first, read together.</summary>
	public Task<(long Version, IReadOnlyList<LockEntry> Entries)> ReadTableAsync() =>
		database.TransactAsync<(long, IReadOnlyList<LockEntry>)>(async (connection, transaction) =>
		{
			var version = (await ServerDatabase.QueryAsync(connection, transaction, "SELECT version FROM lock_version WHERE id = 1;", reader => Convert.ToInt64(reader.GetValue(0))))[0];
			var entries = await ServerDatabase.QueryAsync(connection, transaction, "SELECT entry FROM lock_entries ORDER BY started, run_id;",
				reader => LockEntry.FromToml(reader.GetString(0)));
			return (version, entries);
		});

	/// <summary>Writes a decision while the table is at <paramref name="expected"/>; false (nothing written) when it moved.</summary>
	public Task<bool> CommitAsync(long expected, IReadOnlyList<LockEntry> saved, IReadOnlyList<string> deleted) =>
		database.TransactAsync(async (connection, transaction) =>
		{
			if (await ServerDatabase.ExecuteAsync(connection, transaction, "UPDATE lock_version SET version = version + 1 WHERE id = 1 AND version = @expected;",
				("@expected", expected)) != 1)
			{
				ServerMetrics.LockDecisions.Add(1, ServerMetrics.Tag("result", "conflict"));
				return false;
			}

			// The waits and conflicts a decision adds, against what the entries had before it.
			var (waits, conflicts) = (0, 0);
			foreach (var entry in saved)
			{
				var before = (await ServerDatabase.QueryAsync(connection, transaction, "SELECT entry FROM lock_entries WHERE run_id = @run;",
					reader => LockEntry.FromToml(reader.GetString(0)), ("@run", entry.RunId))).FirstOrDefault();
				waits += entry.Waits.Count(wait => before?.Waits.Any(old => old.Key == wait.Key) != true);
				conflicts += Math.Max(0, entry.Conflicts.Count - (before?.Conflicts.Count ?? 0));
			}

			var components = saved.SelectMany(Bassia.Storage.Remote.Api.ComponentsOf).ToList();
			foreach (var runId in deleted)
			{
				foreach (var entry in await ServerDatabase.QueryAsync(connection, transaction, "SELECT entry FROM lock_entries WHERE run_id = @run;",
					reader => LockEntry.FromToml(reader.GetString(0)), ("@run", runId)))
				{
					components.AddRange(Bassia.Storage.Remote.Api.ComponentsOf(entry));
				}

				await ServerDatabase.ExecuteAsync(connection, transaction, "DELETE FROM lock_entries WHERE run_id = @run;", ("@run", runId));
			}

			await ChangeFeed.AddAsync(connection, transaction, "lock", "", components);
			ServerMetrics.LockDecisions.Add(1, ServerMetrics.Tag("result", "committed"));
			ServerMetrics.LockWaits.Add(waits);
			ServerMetrics.LockConflicts.Add(conflicts);

			foreach (var entry in saved)
			{
				await ServerDatabase.ExecuteAsync(connection, transaction,
					"INSERT INTO lock_entries (run_id, entry, started) VALUES (@run, @entry, @started) ON CONFLICT (run_id) DO UPDATE SET entry = excluded.entry;",
					("@run", entry.RunId), ("@entry", entry.ToToml()), ("@started", entry.Started));
			}

			return true;
		});

	public async Task<T> UpdateAsync<T>(Func<LockSession, T> change)
	{
		while (true)
		{
			var (version, entries) = await ReadTableAsync();
			var session = new LockSession(this, entries.ToList());
			var result = change(session);
			if (await CommitAsync(version, session.Saved.ToList(), session.Deleted.ToList()))
			{
				return result;
			}
		}
	}
}

/// <summary>
/// The jobs every client registers with the server (see <c>RemoteJobScheduler</c>): a job is live while its client
/// renews its heartbeat; its output is uploaded as it goes and read by anyone allowed to; a stop request is a flag the
/// client sees with its next heartbeat.
/// </summary>
internal sealed class DbJobStore(ServerDatabase database)
{
	/// <summary>How long a job stays live without a heartbeat.</summary>
	public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(60);

	public Task RegisterAsync(JobRegistration registration, string owner)
	{
		var info = new JobInfo(registration.Id, registration.Kind, registration.Pid, registration.ProcessStart, RunMetadata.Timestamp(), registration.Log,
			registration.Detached, registration.Hosted, null, registration.Host);
		return database.TransactAsync(async (connection, transaction) =>
		{
			await ServerDatabase.ExecuteAsync(connection, transaction,
				"""
				INSERT INTO jobs (id, kind, owner, info, components, heartbeat, stop, finished, log)
				VALUES (@id, @kind, @owner, @info, @components, @now, 0, NULL, '')
				ON CONFLICT (id) DO UPDATE SET kind = excluded.kind, owner = excluded.owner, info = excluded.info, components = excluded.components,
					heartbeat = excluded.heartbeat, stop = 0, finished = NULL, log = '';
				""",
				("@id", registration.Id), ("@kind", registration.Kind), ("@owner", owner), ("@info", JsonSerializer.Serialize(info, Api.Json)),
				("@components", string.Join(",", registration.Components ?? [])), ("@now", ServerDatabase.Now()));
			await ChangeFeed.AddAsync(connection, transaction, "job", registration.Id, registration.Components ?? []);
			return true;
		});
	}

	/// <summary>Adds the change of a job, with the components it registered with, in the transaction of the write.</summary>
	private static async Task ChangedAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, string kind, string id)
	{
		var components = (await ServerDatabase.QueryAsync(connection, transaction, "SELECT components FROM jobs WHERE id = @id;", reader => reader.GetString(0), ("@id", id)))
			.FirstOrDefault() ?? "";
		await ChangeFeed.AddAsync(connection, transaction, kind, id, components.Split(',', StringSplitOptions.RemoveEmptyEntries));
	}

	/// <summary>Renews the job's heartbeat; returns whether a stop was requested.</summary>
	public async Task<bool> HeartbeatAsync(string id)
	{
		await database.ExecuteAsync("UPDATE jobs SET heartbeat = @now WHERE id = @id AND finished IS NULL;", ("@now", ServerDatabase.Now()), ("@id", id));
		return (await database.QueryAsync("SELECT stop FROM jobs WHERE id = @id;", reader => Convert.ToInt64(reader.GetValue(0)) != 0, ("@id", id))).FirstOrDefault();
	}

	/// <summary>Ends the registration: a detached job's stays, finished, so its output can be read; another's goes.</summary>
	public async Task FinishAsync(string id)
	{
		var detached = (await FindAsync(id))?.Job is { Detached: true };
		await database.TransactAsync(async (connection, transaction) =>
		{
			await ChangedAsync(connection, transaction, "job", id);
			await ServerDatabase.ExecuteAsync(connection, transaction,
				detached ? "UPDATE jobs SET finished = @now WHERE id = @id;" : "DELETE FROM jobs WHERE id = @id;", ("@now", RunMetadata.Timestamp()), ("@id", id));
			return true;
		});
	}

	/// <summary>The job and the components it works on; a job whose heartbeat stopped reads as finished when it last beat.</summary>
	public async Task<(JobInfo Job, IReadOnlyList<string> Components, string Owner)?> FindAsync(string id) =>
		(await database.QueryAsync("SELECT info, heartbeat, finished, components, owner FROM jobs WHERE id = @id;", Read, ("@id", id)))
		.Cast<(JobInfo, IReadOnlyList<string>, string)?>().FirstOrDefault();

	public async Task<IReadOnlyList<(JobInfo Job, IReadOnlyList<string> Components, string Owner)>> ListAsync(string kind) =>
		(await database.QueryAsync("SELECT info, heartbeat, finished, components, owner FROM jobs WHERE kind = @kind ORDER BY id;", Read, ("@kind", kind))).ToList();

	private static (JobInfo, IReadOnlyList<string>, string) Read(System.Data.Common.DbDataReader reader)
	{
		var job = JsonSerializer.Deserialize<JobInfo>(reader.GetString(0), Api.Json)!;
		var heartbeat = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
		var finished = ServerDatabase.NullableText(reader, 2)
			?? (DateTimeOffset.UtcNow - heartbeat > HeartbeatTimeout ? heartbeat.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : null);
		return (job with { Finished = finished }, reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries), reader.GetString(4));
	}

	public Task AppendLogAsync(string id, string text) =>
		database.TransactAsync(async (connection, transaction) =>
		{
			await ServerDatabase.ExecuteAsync(connection, transaction, "UPDATE jobs SET log = log || @text WHERE id = @id;", ("@text", text), ("@id", id));
			await ChangedAsync(connection, transaction, "log", id);
			return true;
		});

	public async Task<JobLog?> ReadLogAsync(string id, long offset)
	{
		var log = (await database.QueryAsync("SELECT log FROM jobs WHERE id = @id;", reader => reader.GetString(0), ("@id", id))).FirstOrDefault();
		if (log is null)
		{
			return null;
		}

		var start = (int)Math.Clamp(offset, 0, log.Length);
		return new JobLog(log[start..], log.Length);
	}

	public Task SetStopAsync(string id, bool stop) =>
		database.ExecuteAsync("UPDATE jobs SET stop = @stop WHERE id = @id;", ("@stop", stop ? 1 : 0), ("@id", id));
}

/// <summary>
/// The task cache's index in the database, its entries (zip archives of a local task cache entry) in an object store,
/// shared by every client.
/// </summary>
internal sealed class DbTaskCache(ServerDatabase database, IObjectStore blobs)
{
	private static string Key(string key) => $"task-cache/{key}.zip";

	public async Task<bool> GetAsync(string key, string path)
	{
		if ((await database.QueryAsync("SELECT size FROM task_cache WHERE cache_key = @key;", reader => Convert.ToInt64(reader.GetValue(0)), ("@key", key))).Count == 0)
		{
			return false;
		}

		await blobs.GetFileAsync(Key(key), path);
		return true;
	}

	public async Task PutAsync(string key, string path)
	{
		await blobs.PutFileAsync(Key(key), path);
		await database.ExecuteAsync(
			"INSERT INTO task_cache (cache_key, size, created) VALUES (@key, @size, @now) ON CONFLICT (cache_key) DO UPDATE SET size = excluded.size, created = excluded.created;",
			("@key", key), ("@size", new FileInfo(path).Length), ("@now", ServerDatabase.Now()));
	}

	public async Task<CacheSize> SizeAsync() =>
		(await database.QueryAsync("SELECT COUNT(*), COALESCE(SUM(size), 0) FROM task_cache;",
			reader => new CacheSize(Convert.ToInt32(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)))))[0];

	public async Task ClearAsync()
	{
		foreach (var key in await database.QueryAsync("SELECT cache_key FROM task_cache;", reader => reader.GetString(0)))
		{
			await blobs.DeleteAsync(Key(key));
		}

		await database.ExecuteAsync("DELETE FROM task_cache;");
	}
}

/// <summary><see cref="IObjectStore"/> on a folder: the server's blobs when its histories are not in the cloud.</summary>
internal sealed class FileObjectStore(string root) : IObjectStore
{
	public string Location => root;

	private string PathOf(string key) => Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

	public async Task PutFileAsync(string key, string path)
	{
		var target = PathOf(key);
		Directory.CreateDirectory(Path.GetDirectoryName(target)!);
		var partial = target + $".{Guid.NewGuid():N}.tmp";
		await using (var source = File.OpenRead(path))
		await using (var destination = File.Create(partial))
		{
			await source.CopyToAsync(destination);
		}

		File.Move(partial, target, overwrite: true);
	}

	public async Task GetFileAsync(string key, string path)
	{
		if (!File.Exists(PathOf(key)))
		{
			throw new StorageException($"No object '{key}' in {Location}.");
		}

		await using var source = File.OpenRead(PathOf(key));
		await using var destination = File.Create(path);
		await source.CopyToAsync(destination);
	}

	public Task CopyAsync(string fromKey, string toKey)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(PathOf(toKey))!);
		File.Copy(PathOf(fromKey), PathOf(toKey), overwrite: true);
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<string>> ListAsync(string prefix) => Task.FromResult<IReadOnlyList<string>>(
		!Directory.Exists(root)
			? []
			: Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
				.Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
				.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && !key.EndsWith(".tmp", StringComparison.Ordinal))
				.Order(StringComparer.Ordinal)
				.ToList());

	public Task DeleteAsync(string key)
	{
		File.Delete(PathOf(key));
		return Task.CompletedTask;
	}
}
