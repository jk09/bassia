namespace Bassia.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Server;
using Bassia.Stores;

/// <summary>
/// The bounds on what the server holds in memory (feat record bassia-817ac979): batched fetches by a byte budget,
/// request bodies by a limit, and the cache of decoded objects by size, evicting what was used least recently.
/// </summary>
public sealed class ObjectCacheTests
{
	private static Tree TreeOf(int seed, int entries) =>
		new(Enumerable.Range(0, entries).Select(index => new TreeEntry($"f{seed}-{index:d5}", EntryKind.Chunked, ObjectId.Of([(byte)seed, (byte)index, (byte)(index >> 8)]), 1, false)).ToList());

	[Fact]
	public void TheCache_StaysWithinItsBound_AndKeepsWhatIsUsed()
	{
		var one = ObjectCache.SizeOf(TreeOf(0, 100));
		var cache = new ObjectCache(20 * one);
		var hot = ObjectId.Of([1, 2, 3]);
		cache.Add(hot, TreeOf(255, 100));

		// A burst of other objects, many times the bound; the hot one is read now and then, as a server's root tree is.
		for (var index = 0; index < 500; index++)
		{
			cache.Add(ObjectId.Of([(byte)index, (byte)(index >> 8), 7]), TreeOf(index % 250, 100));
			if (index % 5 == 0)
			{
				Assert.True(cache.TryGet(hot, out _), $"the hot object was evicted at {index}");
			}

			Assert.True(cache.Bytes <= 20 * one + one, $"{cache.Bytes} bytes held at {index}");
		}

		Assert.InRange(cache.Count, 10, 21);
		Assert.True(cache.TryGet(hot, out _));
		Assert.False(cache.Contains(ObjectId.Of([0, 0, 7]))); // the oldest of the burst went

		cache.Clear();
		Assert.Equal(0, cache.Bytes);
		Assert.Equal(0, cache.Count);
	}

	[Fact]
	public void LargeObjects_CountForWhatTheyHold()
	{
		Assert.True(ObjectCache.SizeOf(TreeOf(0, 10_000)) > 100 * ObjectCache.SizeOf(TreeOf(0, 50)));
		var cache = new ObjectCache(ObjectCache.SizeOf(TreeOf(0, 1_000)) * 3);
		for (var index = 0; index < 10; index++)
		{
			cache.Add(ObjectId.Of([(byte)index]), TreeOf(index, 1_000));
		}

		Assert.InRange(cache.Count, 1, 3);
	}

	[Fact]
	public async Task ARepository_ReadsAHotObjectOnce_ThroughABurstOfOthers()
	{
		var counting = new CountingObjectStore(new MemoryObjectStore());
		var objects = new ObjectAccess(counting) { CacheLimitBytes = 64 * 1024 };
		var hot = await objects.WriteAsync(new Commit(ObjectId.Of([1]), [], ChangeId.New(), new Author("ann"), "hot", DateTimeOffset.UnixEpoch));
		var others = new List<ObjectId>();
		for (var index = 0; index < 400; index++)
		{
			others.Add(await objects.WriteAsync(new Commit(ObjectId.Of([2]), [], ChangeId.New(), new Author("ann"), $"other {index}", DateTimeOffset.UnixEpoch)));
		}

		objects.ClearCache();
		var before = counting.Reads;
		await objects.ReadCommitAsync(hot);
		foreach (var id in others)
		{
			await objects.ReadCommitAsync(id);
			await objects.ReadCommitAsync(hot);
		}

		// Every other object once, the hot one once: the burst evicted among itself.
		Assert.Equal(401, counting.Reads - before);
		Assert.True(objects.CacheBytes <= 64 * 1024 + 1024);
	}
}

public sealed class ServerMemoryTests : IAsyncLifetime
{
	private readonly TempDirectory temp = new();
	private ServerApp server = null!;
	private const string Token = "s3cret";

	/// <summary>A budget of 1 MiB per batched reply, and bodies of at most 9 MiB: room for one client batch (8 MiB), not for two.</summary>
	public async Task InitializeAsync()
	{
		server = new ServerApp(new ServerOptions
		{
			Data = temp.Combine("server"),
			Postgres = Backends.RequiredPostgres(),
			Url = "http://127.0.0.1:0",
			Token = Token,
			FetchBudget = 1024 * 1024,
			PutLimit = 9 * 1024 * 1024
		});
		await server.StartAsync();
	}

	public async Task DisposeAsync()
	{
		await server.DisposeAsync();
		temp.Dispose();
	}

	private static byte[] Content(int index, int size)
	{
		var bytes = new byte[size];
		new Random(index).NextBytes(bytes);
		return bytes;
	}

	[PostgresFact]
	public async Task ABatchedFetch_StopsAtItsBudget_AndTheClientAsksForTheRest()
	{
		using var connection = new ServerConnection(server.Address!, Token);
		var store = new RemoteObjectStore(connection);

		// 300 objects of 60 KiB: 17.6 MiB, many times the reply's budget.
		var objects = Enumerable.Range(0, 300).Select(index => Content(index, 60 * 1024)).Select(bytes => (Id: ObjectId.Of(bytes), Bytes: (ReadOnlyMemory<byte>)bytes)).ToList();
		await store.PutManyAsync(objects);
		var ids = objects.Select(item => item.Id).ToList();
		var missing = ObjectId.Of([42]);

		// One request: no more than the budget plus one group of objects, and a count of what was left.
		using var response = await connection.Http.PostAsJsonAsync(ServerConnection.Path("objects/fetch"), new Api.IdsRequest(ids), Api.Json);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var first = Api.Frames.Read(await response.Content.ReadAsByteArrayAsync()).ToList();
		var left = int.Parse(response.Headers.GetValues(Api.TruncatedHeader).Single());
		Assert.InRange(first.Sum(item => (long)item.Bytes.Length), 1, 1024 * 1024 + 64 * 60 * 1024);
		Assert.Equal(ids.Count - first.Count, left);
		Assert.Equal(ids.Take(first.Count).ToHashSet(), first.Select(item => item.Id).ToHashSet());

		// The client gets everything that exists, in several requests, each object whole.
		var all = await store.ReadManyAsync([.. ids, missing, ids[0]]);
		Assert.Equal(300, all.Count);
		Assert.All(objects, item => Assert.True(all[item.Id].Span.SequenceEqual(item.Bytes.Span)));
		Assert.False(all.ContainsKey(missing));

		// A request that fits says nothing about a rest.
		using var small = await connection.Http.PostAsJsonAsync(ServerConnection.Path("objects/fetch"), new Api.IdsRequest(ids.Take(5).ToList()), Api.Json);
		Assert.False(small.Headers.Contains(Api.TruncatedHeader));

		// A client that knows nothing of the budget (an older one) reads what a reply left out one by one, as it always did.
		var caching = new CachingObjectStore(new MemoryObjectStore(), new OneReplyStore(connection));
		await caching.PrefetchAsync(ids);
		Assert.All(objects.Take(40), item => Assert.True(caching.ReadAsync(item.Id).AsTask().Result!.Value.Span.SequenceEqual(item.Bytes.Span)));
	}

	/// <summary>The remote store as an older client has it: one request per batch, whatever the reply left out.</summary>
	private sealed class OneReplyStore(ServerConnection connection) : IObjectStore, IObjectBatchReader
	{
		private readonly RemoteObjectStore inner = new(connection);

		public async Task<IReadOnlyDictionary<ObjectId, ReadOnlyMemory<byte>>> ReadManyAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
		{
			using var response = await connection.Http.PostAsJsonAsync(ServerConnection.Path("objects/fetch"), new Api.IdsRequest(ids), Api.Json, ct);
			return Api.Frames.Read(await response.Content.ReadAsByteArrayAsync(ct)).ToDictionary(item => item.Id, item => item.Bytes);
		}

		public ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default) => inner.HasAsync(ids, ct);

		public ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default) => inner.StatAsync(id, ct);

		public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default) => inner.ReadAsync(id, range, ct);

		public ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => inner.PutAsync(id, bytes, ct);

		public ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default) => inner.BeginUploadAsync(id, length, ct);
	}

	[PostgresFact]
	public async Task ABodyOverTheLimit_IsRefusedWithA413_AndClientBatchesStayBelowTheDefault()
	{
		using var connection = new ServerConnection(server.Address!, Token);
		var frames = Api.Frames.Write(Enumerable.Range(0, 130).Select(index => Content(index, 100 * 1024)).Select(bytes => (ObjectId.Of(bytes), (ReadOnlyMemory<byte>)bytes)));
		Assert.True(frames.Length > 9 * 1024 * 1024);

		// The server answers before it reads the body; a client that waits for the go-ahead (Expect: 100-continue) hears the reason.
		using var request = new HttpRequestMessage(HttpMethod.Post, ServerConnection.Path("objects/put")) { Content = new ByteArrayContent(frames) };
		request.Content.Headers.ContentType = new MediaTypeHeaderValue(Api.Frames.MediaType);
		request.Headers.ExpectContinue = true;
		using var response = await connection.Http.SendAsync(request);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		Assert.Contains("-put-limit", await response.Content.ReadAsStringAsync());

		// Nothing of the refused batch was stored.
		var have = await new RemoteObjectStore(connection).HasAsync([ObjectId.Of(Content(0, 100 * 1024))]);
		Assert.False(have[0]);

		// The default limit leaves room for what a client sends in one batch (8 MiB of small objects).
		Assert.True(new ServerOptions { Data = temp.Path }.PutLimit >= 3 * 8L * 1024 * 1024);
		Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["-data", temp.Path, "-postgres", "Host=x", "-fetch-budget", "lots"]));
		Assert.Equal(64L * 1024 * 1024, ServerOptions.Parse(["-data", temp.Path, "-postgres", "Host=x", "-object-cache", "64"]).ObjectCache);
	}
}
