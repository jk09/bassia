namespace Bassia.Storage.Cloud;

using System.Globalization;
using Bassia.Git;

/// <summary>What one collection of a component did: the pack it wrote (or none), the packs it superseded and those it deleted.</summary>
internal sealed record CollectionResult(string Component, string? Pack, IReadOnlyList<string> Superseded, IReadOnlyList<string> Deleted);

/// <summary>
/// Garbage collection of a component of the <see cref="CloudComponentStorage"/>, in two steps a grace period apart: a
/// collection packs every object the refs reach into one new pack and marks the other packs superseded; a later one
/// deletes the packs superseded longer than the grace period ago - their records first, then their files. A write in
/// flight relies only on packs listed when it started, so one that takes less than the grace period never loses an
/// object; packs a failed write left unlisted are deleted once older than the grace period. A lease record keeps two
/// collections of one component from running at once.
/// </summary>
internal sealed class CloudGarbageCollector(CloudComponentStorage storage, TimeSpan? grace = null, Func<DateTimeOffset>? clock = null)
{
	public static readonly TimeSpan DefaultGrace = TimeSpan.FromDays(14);

	private static readonly TimeSpan LeaseTime = TimeSpan.FromHours(1);

	private readonly TimeSpan grace = grace ?? DefaultGrace;

	private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);

	private static string Seconds(DateTimeOffset time) => time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

	private static DateTimeOffset Time(string? seconds) =>
		DateTimeOffset.FromUnixTimeSeconds(long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0);

	public async Task<CollectionResult> CollectAsync(string name)
	{
		var partition = CloudComponentStorage.Partition(name);
		var lease = await TakeLeaseAsync(partition, name);
		try
		{
			var records = await storage.SyncAsync(name);
			var now = clock();
			var packs = records.Where(record => record.Sort.StartsWith(CloudComponentStorage.PackPrefix, StringComparison.Ordinal)).ToList();
			var live = packs.Where(record => record["superseded"] is null).ToList();
			var expired = packs.Where(record => record["superseded"] is { } superseded && Time(superseded) <= now - grace).ToList();

			// One live pack holds everything already (the last collection's, untouched since).
			string? written = null;
			var superseded = new List<CloudRecord>();
			var tips = CloudComponentStorage.Refs(records).Values.Distinct(StringComparer.Ordinal).ToList();
			if (live.Count > 1 && tips.Count > 0)
			{
				var git = GitClient.In(storage.LocationOf(name));
				var packDir = Path.Combine(storage.LocationOf(name), ".git", "objects", "pack");
				written = "pack-" + await git.RunOrThrowAsync(["pack-objects", "--revs", "--quiet", Path.Combine(packDir, "pack")], string.Concat(tips.Select(tip => tip + "\n")));
				if (live.All(record => record.Sort != CloudComponentStorage.PackPrefix + written))
				{
					await storage.Objects.PutFileAsync(CloudComponentStorage.PackObject(name, written, "pack"), Path.Combine(packDir, written + ".pack"));
					await storage.Objects.PutFileAsync(CloudComponentStorage.PackObject(name, written, "idx"), Path.Combine(packDir, written + ".idx"));
					await storage.Table.WriteAsync([CloudComponentStorage.PackRecord(name, written, new FileInfo(Path.Combine(packDir, written + ".pack")).Length)]);
				}

				superseded = live.Where(record => record.Sort != CloudComponentStorage.PackPrefix + written).ToList();
				foreach (var chunk in superseded.Chunk(IRecordTable.MaxWrites))
				{
					await storage.Table.WriteAsync(chunk.Select(record =>
						new CloudWrite(partition, record.Sort, new Dictionary<string, string>(record.Values, StringComparer.Ordinal) { ["superseded"] = Seconds(now) })).ToList());
				}
			}

			var deleted = new List<string>();
			foreach (var chunk in expired.Chunk(IRecordTable.MaxWrites))
			{
				await storage.Table.WriteAsync(chunk.Select(record => new CloudWrite(partition, record.Sort, null)).ToList());
				deleted.AddRange(chunk.Select(record => record.Sort[CloudComponentStorage.PackPrefix.Length..]));
			}

			// Files no record lists: packs of writes that failed, or whose records are deleted above.
			var listed = packs.Except(expired).Select(record => record.Sort[CloudComponentStorage.PackPrefix.Length..]).Append(written ?? "").ToHashSet(StringComparer.Ordinal);
			var prefix = $"components/{name}/packs/";
			var unlisted = (await storage.Objects.ListAsync(prefix))
				.Select(key => (Key: key, Pack: Path.GetFileNameWithoutExtension(key[prefix.Length..])))
				.Where(file => !listed.Contains(file.Pack))
				.ToList();
			foreach (var file in unlisted)
			{
				if (deleted.Contains(file.Pack) || await UnlistedLongEnoughAsync(name, file.Pack, now))
				{
					await storage.Objects.DeleteAsync(file.Key);
				}
			}

			// A pack seen unlisted that a write listed after all is no candidate any more.
			var stale = records.Where(record => record.Sort.StartsWith("unlisted#", StringComparison.Ordinal) && listed.Contains(record.Sort["unlisted#".Length..])).ToList();
			foreach (var chunk in stale.Chunk(IRecordTable.MaxWrites))
			{
				await storage.Table.WriteAsync(chunk.Select(record => new CloudWrite(partition, record.Sort, null)).ToList());
			}

			return new CollectionResult(name, written, superseded.Select(record => record.Sort[CloudComponentStorage.PackPrefix.Length..]).ToList(), deleted);
		}
		finally
		{
			await storage.Table.WriteAsync([new CloudWrite(partition, CloudComponentStorage.LeaseKey, null, lease)]);
		}
	}

	/// <summary>
	/// Whether a pack no record lists was first seen unlisted longer than the grace period ago: a write in flight uploads
	/// its pack before it lists it, so an unlisted pack is only remembered (in the lease record's partition) the first time.
	/// </summary>
	private async Task<bool> UnlistedLongEnoughAsync(string name, string pack, DateTimeOffset now)
	{
		var partition = CloudComponentStorage.Partition(name);
		var key = $"unlisted#{pack}";
		var seen = await storage.Table.GetAsync(partition, key);
		if (seen is null)
		{
			await storage.Table.WriteAsync([new CloudWrite(partition, key, new Dictionary<string, string> { ["seen"] = Seconds(now) })]);
			return false;
		}

		if (Time(seen["seen"]) > now - grace)
		{
			return false;
		}

		await storage.Table.WriteAsync([new CloudWrite(partition, key, null)]);
		return true;
	}

	/// <summary>Writes the lease record, unless another collection holds one that has not expired; returns its version.</summary>
	private async Task<long> TakeLeaseAsync(string partition, string name)
	{
		var current = await storage.Table.GetAsync(partition, CloudComponentStorage.LeaseKey);
		if (current is not null && Time(current["expires"]) > clock())
		{
			throw new StorageException($"A collection of component '{name}' is running (its lease expires {Time(current["expires"]):u}).");
		}

		try
		{
			await storage.Table.WriteAsync([new CloudWrite(partition, CloudComponentStorage.LeaseKey,
				new Dictionary<string, string> { ["owner"] = Environment.MachineName, ["expires"] = Seconds(clock() + LeaseTime) }, current?.Version ?? 0)]);
		}
		catch (CloudConflictException)
		{
			throw new StorageException($"A collection of component '{name}' started meanwhile.");
		}

		return (current?.Version ?? 0) + 1;
	}
}
