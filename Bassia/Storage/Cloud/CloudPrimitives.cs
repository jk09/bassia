namespace Bassia.Storage.Cloud;

/// <summary>
/// Where the cloud storage keeps files - component packs - by key: S3 (<see cref="S3ObjectStore"/>), or memory for tests
/// (<see cref="MemoryObjectStore"/>). Objects are written once and never changed in place.
/// </summary>
internal interface IObjectStore
{
	/// <summary>Where the objects live, as the backend names it (<c>s3://bucket/prefix</c>); shown to users, never parsed.</summary>
	string Location { get; }

	Task PutFileAsync(string key, string path);

	/// <summary>Downloads the object to <paramref name="path"/>; throws <see cref="StorageException"/> when there is none.</summary>
	Task GetFileAsync(string key, string path);

	/// <summary>Copies an object within the store, without downloading it.</summary>
	Task CopyAsync(string fromKey, string toKey);

	/// <summary>The keys starting with <paramref name="prefix"/>.</summary>
	Task<IReadOnlyList<string>> ListAsync(string prefix);

	Task DeleteAsync(string key);
}

/// <summary>A record of the cloud table: its partition and sort key, its values, and its version (1 for a new record).</summary>
internal sealed record CloudRecord(string Partition, string Sort, IReadOnlyDictionary<string, string> Values, long Version)
{
	public string? this[string name] => Values.GetValueOrDefault(name);
}

/// <summary>
/// A write of <see cref="IRecordTable.WriteAsync"/>: the values of a record (null deletes it) under an expectation -
/// <paramref name="ExpectedVersion"/> null is none, 0 that the record does not exist, n that it is at version n.
/// </summary>
internal sealed record CloudWrite(string Partition, string Sort, IReadOnlyDictionary<string, string>? Values, long? ExpectedVersion = null);

/// <summary>A write's expectation did not hold: someone changed the record first. Nothing of the batch was written.</summary>
internal sealed class CloudConflictException(string message) : StorageException(message);

/// <summary>
/// Where the cloud storage keeps small records - refs, pack lists, leases - by partition and sort key, with versions:
/// DynamoDB (<see cref="DynamoRecordTable"/>), or memory for tests (<see cref="MemoryRecordTable"/>).
/// </summary>
internal interface IRecordTable
{
	/// <summary>How many writes one <see cref="WriteAsync"/> may hold (DynamoDB's transaction limit).</summary>
	public const int MaxWrites = 100;

	/// <summary>Where the records live, as the backend names it; shown to users, never parsed.</summary>
	string Location { get; }

	Task<CloudRecord?> GetAsync(string partition, string sort);

	/// <summary>The records of a partition whose sort key starts with <paramref name="sortPrefix"/>, in sort key order.</summary>
	Task<IReadOnlyList<CloudRecord>> QueryAsync(string partition, string sortPrefix = "");

	/// <summary>
	/// Applies every write or none: when one expectation does not hold, throws <see cref="CloudConflictException"/>.
	/// A put raises the record's version by one.
	/// </summary>
	Task WriteAsync(IReadOnlyList<CloudWrite> writes);
}

/// <summary>The reference <see cref="IObjectStore"/>: objects in memory.</summary>
internal sealed class MemoryObjectStore : IObjectStore
{
	private readonly Dictionary<string, byte[]> objects = new(StringComparer.Ordinal);

	public string Location => "memory:objects";

	/// <summary>How many objects were downloaded: what tests check caching by.</summary>
	public int Downloads { get; private set; }

	public async Task PutFileAsync(string key, string path)
	{
		var content = await File.ReadAllBytesAsync(path);
		lock (objects)
		{
			objects[key] = content;
		}
	}

	public async Task GetFileAsync(string key, string path)
	{
		byte[] content;
		lock (objects)
		{
			content = objects.TryGetValue(key, out var found) ? found : throw new StorageException($"No object '{key}' in {Location}.");
			Downloads++;
		}

		await File.WriteAllBytesAsync(path, content);
	}

	public Task CopyAsync(string fromKey, string toKey)
	{
		lock (objects)
		{
			objects[toKey] = objects.TryGetValue(fromKey, out var found) ? found : throw new StorageException($"No object '{fromKey}' in {Location}.");
		}

		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<string>> ListAsync(string prefix)
	{
		lock (objects)
		{
			return Task.FromResult<IReadOnlyList<string>>(objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList());
		}
	}

	public Task DeleteAsync(string key)
	{
		lock (objects)
		{
			objects.Remove(key);
		}

		return Task.CompletedTask;
	}
}

/// <summary>The reference <see cref="IRecordTable"/>: records in memory, a batch checked as a whole before any is applied.</summary>
internal sealed class MemoryRecordTable : IRecordTable
{
	private readonly SortedDictionary<(string Partition, string Sort), CloudRecord> records = new();

	public string Location => "memory:records";

	public Task<CloudRecord?> GetAsync(string partition, string sort)
	{
		lock (records)
		{
			return Task.FromResult(records.GetValueOrDefault((partition, sort)));
		}
	}

	public Task<IReadOnlyList<CloudRecord>> QueryAsync(string partition, string sortPrefix = "")
	{
		lock (records)
		{
			return Task.FromResult<IReadOnlyList<CloudRecord>>(records.Values
				.Where(record => record.Partition == partition && record.Sort.StartsWith(sortPrefix, StringComparison.Ordinal))
				.OrderBy(record => record.Sort, StringComparer.Ordinal).ToList());
		}
	}

	public Task WriteAsync(IReadOnlyList<CloudWrite> writes)
	{
		if (writes.Count > IRecordTable.MaxWrites)
		{
			throw new StorageException($"A batch of {writes.Count} writes is more than one transaction allows ({IRecordTable.MaxWrites}); nothing was written.");
		}

		lock (records)
		{
			foreach (var write in writes.Where(write => write.ExpectedVersion is not null))
			{
				var current = records.GetValueOrDefault((write.Partition, write.Sort))?.Version ?? 0;
				if (current != write.ExpectedVersion)
				{
					throw new CloudConflictException($"Record '{write.Partition}/{write.Sort}' changed: it is at version {current}, not {write.ExpectedVersion}; nothing was written.");
				}
			}

			foreach (var write in writes)
			{
				var key = (write.Partition, write.Sort);
				if (write.Values is null)
				{
					records.Remove(key);
				}
				else
				{
					var version = (records.GetValueOrDefault(key)?.Version ?? 0) + 1;
					records[key] = new CloudRecord(write.Partition, write.Sort, new Dictionary<string, string>(write.Values, StringComparer.Ordinal), version);
				}
			}
		}

		return Task.CompletedTask;
	}
}
