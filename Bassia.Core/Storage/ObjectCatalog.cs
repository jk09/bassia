namespace Bassia.Core.Storage;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Bassia.Core.Model;

/// <summary>What the catalog says about an object.</summary>
public enum CatalogState
{
	/// <summary>No row: not stored, when the catalog is complete; otherwise the blob store has to be asked.</summary>
	Absent,

	/// <summary>An upload was announced (a put in progress, or a direct upload on a signed URL) and is not confirmed yet.</summary>
	Pending,

	/// <summary>The blob is stored.</summary>
	Stored,

	/// <summary>A garbage collection chose it: its blob is being deleted, or was when a collection stopped half way.</summary>
	Doomed
}

/// <summary>
/// The object catalog: which blobs the store holds, with their size and when each was last put or asked about, so the
/// server answers "have" from its database instead of asking blob storage one object at a time, and garbage collection
/// needs no listing of the bucket. It is derived - the blobs are the truth, and it is rebuilt from a listing of them -
/// and it orders a collection's deletes against concurrent uploads of the same object.
/// </summary>
public interface IObjectCatalog
{
	/// <summary>The state of each id, in order; a stored object is touched (its last-use time becomes now).</summary>
	Task<IReadOnlyList<CatalogState>> TouchAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default);

	/// <summary>
	/// Announces uploads: a stored object is touched and reported (it needs no upload); any other gets a pending row and
	/// is left out. An object a collection doomed is taken back only once that collection's deletes are over.
	/// </summary>
	Task<IReadOnlySet<ObjectId>> BeginAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default);

	/// <summary>Records blobs as stored, touched now; a doomed one is left doomed. Returns those recorded.</summary>
	Task<IReadOnlySet<ObjectId>> ConfirmAsync(IReadOnlyList<(ObjectId Id, long Size)> objects, CancellationToken ct = default);

	/// <summary>Records blobs found in the store with the time they were written, unless the catalog already has them (reconciliation).</summary>
	Task RecordAsync(IReadOnlyList<BlobInfo> blobs, CancellationToken ct = default);

	/// <summary>Marks as doomed those of <paramref name="ids"/> not touched since <paramref name="olderThan"/>, and returns them. Called under <see cref="SweepLockAsync"/>.</summary>
	Task<IReadOnlyList<ObjectId>> DoomAsync(IReadOnlyList<ObjectId> ids, DateTimeOffset olderThan, CancellationToken ct = default);

	/// <summary>Drops the rows of doomed objects whose blobs were deleted.</summary>
	Task RemoveAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default);

	/// <summary>
	/// The lock between a collection's deletes (<paramref name="exclusive"/>) and whoever is about to rely on a blob the
	/// catalog does not vouch for (shared): held while a batch is doomed, deleted and removed.
	/// </summary>
	Task<IAsyncDisposable> SweepLockAsync(bool exclusive, CancellationToken ct = default);

	/// <summary>Every row: id, size and when it was last touched.</summary>
	IAsyncEnumerable<BlobInfo> ListAsync(CancellationToken ct = default);

	/// <summary>Whether every blob of the store has a row, so an absent id is an object the store does not hold.</summary>
	ValueTask<bool> IsCompleteAsync(CancellationToken ct = default);

	ValueTask MarkCompleteAsync(CancellationToken ct = default);
}

/// <summary>A store that deletes a batch of objects only if none was used since a moment, and says which it deleted.</summary>
public interface IObjectSweeper
{
	Task<IReadOnlyList<ObjectId>> SweepAsync(IReadOnlyList<ObjectId> ids, DateTimeOffset olderThan, CancellationToken ct = default);
}

/// <summary>The catalog in memory.</summary>
public sealed class MemoryObjectCatalog : IObjectCatalog
{
	private sealed record Row(long Size, CatalogState State, DateTimeOffset Touched);

	private readonly Dictionary<ObjectId, Row> rows = [];
	private readonly Lock gate = new();
	private readonly SemaphoreSlim sweeping = new(1, 1);
	private bool complete;

	public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

	public Task<IReadOnlyList<CatalogState>> TouchAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		lock (gate)
		{
			var states = new List<CatalogState>(ids.Count);
			foreach (var id in ids)
			{
				if (!rows.TryGetValue(id, out var row))
				{
					states.Add(CatalogState.Absent);
					continue;
				}

				if (row.State == CatalogState.Stored)
				{
					rows[id] = row with { Touched = Clock() };
				}

				states.Add(row.State);
			}

			return Task.FromResult<IReadOnlyList<CatalogState>>(states);
		}
	}

	public async Task<IReadOnlySet<ObjectId>> BeginAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		bool doomed;
		lock (gate)
		{
			doomed = ids.Any(id => rows.TryGetValue(id, out var row) && row.State == CatalogState.Doomed);
		}

		if (doomed)
		{
			// Wait for the collection that doomed them to finish deleting.
			await using var _ = await SweepLockAsync(false, ct);
		}

		lock (gate)
		{
			var stored = new HashSet<ObjectId>();
			foreach (var id in ids)
			{
				if (rows.TryGetValue(id, out var row) && row.State == CatalogState.Stored)
				{
					rows[id] = row with { Touched = Clock() };
					stored.Add(id);
				}
				else
				{
					rows[id] = new Row(row?.Size ?? 0, CatalogState.Pending, Clock());
				}
			}

			return stored;
		}
	}

	public Task<IReadOnlySet<ObjectId>> ConfirmAsync(IReadOnlyList<(ObjectId Id, long Size)> objects, CancellationToken ct = default)
	{
		lock (gate)
		{
			var confirmed = new HashSet<ObjectId>();
			foreach (var (id, size) in objects)
			{
				if (rows.TryGetValue(id, out var row) && row.State == CatalogState.Doomed)
				{
					continue;
				}

				rows[id] = new Row(size, CatalogState.Stored, Clock());
				confirmed.Add(id);
			}

			return Task.FromResult<IReadOnlySet<ObjectId>>(confirmed);
		}
	}

	public Task RecordAsync(IReadOnlyList<BlobInfo> blobs, CancellationToken ct = default)
	{
		lock (gate)
		{
			foreach (var blob in blobs)
			{
				rows.TryAdd(blob.Id, new Row(blob.Length, CatalogState.Stored, blob.Written));
			}
		}

		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<ObjectId>> DoomAsync(IReadOnlyList<ObjectId> ids, DateTimeOffset olderThan, CancellationToken ct = default)
	{
		lock (gate)
		{
			var doomed = new List<ObjectId>();
			foreach (var id in ids)
			{
				if (rows.TryGetValue(id, out var row) && (row.State == CatalogState.Doomed || row.Touched < olderThan))
				{
					rows[id] = row with { State = CatalogState.Doomed };
					doomed.Add(id);
				}
			}

			return Task.FromResult<IReadOnlyList<ObjectId>>(doomed);
		}
	}

	public Task RemoveAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		lock (gate)
		{
			foreach (var id in ids)
			{
				if (rows.TryGetValue(id, out var row) && row.State == CatalogState.Doomed)
				{
					rows.Remove(id);
				}
			}
		}

		return Task.CompletedTask;
	}

	private sealed class Release(SemaphoreSlim semaphore) : IAsyncDisposable
	{
		public ValueTask DisposeAsync()
		{
			semaphore.Release();
			return ValueTask.CompletedTask;
		}
	}

	public async Task<IAsyncDisposable> SweepLockAsync(bool exclusive, CancellationToken ct = default)
	{
		await sweeping.WaitAsync(ct);
		return new Release(sweeping);
	}

	public async IAsyncEnumerable<BlobInfo> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
	{
		List<BlobInfo> all;
		lock (gate)
		{
			all = rows.Select(pair => new BlobInfo(pair.Key, pair.Value.Size, pair.Value.Touched)).ToList();
		}

		foreach (var blob in all)
		{
			yield return blob;
		}

		await Task.CompletedTask;
	}

	public ValueTask<bool> IsCompleteAsync(CancellationToken ct = default) => ValueTask.FromResult(complete);

	public ValueTask MarkCompleteAsync(CancellationToken ct = default)
	{
		complete = true;
		return ValueTask.CompletedTask;
	}
}

/// <summary>
/// A blob store behind the object catalog. "Have" is answered from the catalog (and touches what it reports, so a
/// collection leaves alone what a client was just told is there); a put writes the blob and records it;
/// maintenance lists the catalog instead of the bucket, and deletes in batches that a concurrent upload of the same
/// object cannot cross. While the catalog is not complete (a store from before it), ids it does not know are asked
/// of the blob store and recorded.
/// </summary>
public sealed class CatalogedObjectStore(IObjectStore inner, IObjectCatalog catalog, IObjectStoreMaintenance maintenance)
	: IObjectStore, IObjectBatchWriter, IObjectStoreMaintenance, IObjectSweeper
{
	private const int Parallel = 16;

	private bool complete;

	public IObjectCatalog Catalog => catalog;

	private async ValueTask<bool> CompleteAsync(CancellationToken ct) => complete || (complete = await catalog.IsCompleteAsync(ct));

	/// <summary>
	/// Makes the catalog complete: at once for an empty store, else by listing every blob and recording those it lacks.
	/// Returns the number of blobs listed.
	/// </summary>
	public async Task<long> ReconcileAsync(CancellationToken ct = default)
	{
		long listed = 0;
		var batch = new List<BlobInfo>();
		await foreach (var blob in maintenance.ListAsync(ct))
		{
			listed++;
			batch.Add(blob);
			if (batch.Count >= 5000)
			{
				await catalog.RecordAsync(batch, ct);
				batch.Clear();
			}
		}

		await catalog.RecordAsync(batch, ct);
		await catalog.MarkCompleteAsync(ct);
		complete = true;
		return listed;
	}

	/// <summary>Marks the catalog complete when the store holds nothing yet (a new server): from then on an unknown id is a missing object.</summary>
	public async Task<bool> CompleteIfEmptyAsync(CancellationToken ct = default)
	{
		if (await CompleteAsync(ct))
		{
			return true;
		}

		await foreach (var _ in maintenance.ListAsync(ct))
		{
			return false;
		}

		await catalog.MarkCompleteAsync(ct);
		return complete = true;
	}

	public async ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var states = await catalog.TouchAsync(ids, ct);
		var have = states.Select(state => state == CatalogState.Stored).ToArray();
		var known = await CompleteAsync(ct);

		// What the catalog does not vouch for: an announced upload (did it arrive?) and, while the catalog is being
		// built, ids it has never seen. The blob store is asked, under the lock a collection's deletes take.
		var asking = Enumerable.Range(0, ids.Count).Where(index => states[index] == CatalogState.Pending || (states[index] == CatalogState.Absent && !known)).ToList();
		if (asking.Count > 0)
		{
			await using var _ = await catalog.SweepLockAsync(false, ct);
			var found = new ConcurrentBag<(int Index, long Size)>();
			await System.Threading.Tasks.Parallel.ForEachAsync(asking, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct }, async (index, token) =>
			{
				if (await inner.StatAsync(ids[index], token) is { } stat)
				{
					found.Add((index, stat.Length));
				}
			});
			var confirmed = await catalog.ConfirmAsync(found.Select(item => (ids[item.Index], item.Size)).ToList(), ct);
			foreach (var (index, _) in found)
			{
				have[index] = confirmed.Contains(ids[index]);
			}
		}

		return have;
	}

	public ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default) => inner.StatAsync(id, ct);

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default) => inner.ReadAsync(id, range, ct);

	public async ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => await PutManyAsync([(id, bytes)], ct);

	public async Task PutManyAsync(IReadOnlyList<(ObjectId Id, ReadOnlyMemory<byte> Bytes)> objects, CancellationToken ct = default)
	{
		var distinct = objects.DistinctBy(item => item.Id).ToList();
		if (distinct.Count == 0)
		{
			return;
		}

		// Write, then record: one statement for the batch. The catalog refuses to record an object a collection has
		// doomed - its blob is being deleted, perhaps after this write - and only those take the careful way.
		await WriteAsync(distinct, ct);
		var confirmed = await catalog.ConfirmAsync(distinct.Select(item => (item.Id, (long)item.Bytes.Length)).ToList(), ct);
		var doomed = distinct.Where(item => !confirmed.Contains(item.Id)).ToList();
		if (doomed.Count > 0)
		{
			// Announcing waits until the collection's deletes are over and takes the rows back; then the blobs are written again.
			var stored = await catalog.BeginAsync(doomed.Select(item => item.Id).ToList(), ct);
			var writing = doomed.Where(item => !stored.Contains(item.Id)).ToList();
			await WriteAsync(writing, ct);
			await catalog.ConfirmAsync(writing.Select(item => (item.Id, (long)item.Bytes.Length)).ToList(), ct);
		}
	}

	private Task WriteAsync(IReadOnlyList<(ObjectId Id, ReadOnlyMemory<byte> Bytes)> objects, CancellationToken ct) =>
		System.Threading.Tasks.Parallel.ForEachAsync(objects, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct },
			async (item, token) => await inner.PutAsync(item.Id, item.Bytes, token));

	/// <summary>A direct upload is announced as pending; the first "have" for it asks the blob store whether it arrived.</summary>
	public async ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default)
	{
		await catalog.BeginAsync([id], ct);
		return await inner.BeginUploadAsync(id, length, ct);
	}

	/// <summary>The catalog's rows once it is complete; before that, the store's own listing.</summary>
	public async IAsyncEnumerable<BlobInfo> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var blob in (await CompleteAsync(ct) ? catalog.ListAsync(ct) : maintenance.ListAsync(ct)).WithCancellation(ct))
		{
			yield return blob;
		}
	}

	public async ValueTask DeleteAsync(ObjectId id, CancellationToken ct = default) => await SweepAsync([id], DateTimeOffset.MaxValue, ct);

	public async Task<IReadOnlyList<ObjectId>> SweepAsync(IReadOnlyList<ObjectId> ids, DateTimeOffset olderThan, CancellationToken ct = default)
	{
		await using var _ = await catalog.SweepLockAsync(true, ct);
		var doomed = await catalog.DoomAsync(ids, olderThan, ct);
		await System.Threading.Tasks.Parallel.ForEachAsync(doomed, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct },
			async (id, token) => await maintenance.DeleteAsync(id, token));
		await catalog.RemoveAsync(doomed, ct);
		return doomed;
	}
}
