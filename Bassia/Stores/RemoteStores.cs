namespace Bassia.Stores;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

/// <summary>A connection to a <c>bassia-server</c>: its base URL and the optional shared token.</summary>
public sealed class ServerConnection : IDisposable
{
	/// <summary>Chunks at least this large go to blob storage directly on a signed URL.</summary>
	public const int DirectTransferThreshold = 256 * 1024;

	public ServerConnection(string url, string? token = null, HttpMessageHandler? handler = null)
	{
		BaseUrl = url.TrimEnd('/');
		// Replies are compressed by the server when asked (structural objects and JSON compress well).
		Http = handler is null
			? new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
			: new HttpClient(handler, false);
		Http.BaseAddress = new Uri(BaseUrl + "/");
		Http.Timeout = TimeSpan.FromMinutes(10);
		if (token is not null)
		{
			Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}

		// Signed blob URLs carry their own authorization and may point at another host (S3).
		Blobs = handler is null ? new HttpClient() : new HttpClient(handler, false);
		Blobs.Timeout = TimeSpan.FromMinutes(10);
	}

	public string BaseUrl { get; }

	public HttpClient Http { get; }

	public HttpClient Blobs { get; }

	public static string Path(string relative) => Api.Prefix.TrimStart('/') + "/" + relative;

	public async Task<T> GetAsync<T>(string relative, CancellationToken ct = default)
	{
		using var response = await Http.GetAsync(Path(relative), ct);
		await EnsureAsync(response, ct);
		return (await response.Content.ReadFromJsonAsync<T>(Api.Json, ct))!;
	}

	public async Task<T> PostAsync<T>(string relative, object body, CancellationToken ct = default)
	{
		using var response = await Http.PostAsJsonAsync(Path(relative), body, Api.Json, ct);
		await EnsureAsync(response, ct);
		return (await response.Content.ReadFromJsonAsync<T>(Api.Json, ct))!;
	}

	public async Task PostAsync(string relative, object body, CancellationToken ct = default)
	{
		using var response = await Http.PostAsJsonAsync(Path(relative), body, Api.Json, ct);
		await EnsureAsync(response, ct);
	}

	public static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
	{
		if (response.IsSuccessStatusCode)
		{
			return;
		}

		string message;
		try
		{
			message = (await response.Content.ReadFromJsonAsync<Api.ErrorReply>(Api.Json, ct))?.Error ?? response.ReasonPhrase ?? "";
		}
		catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
		{
			message = response.ReasonPhrase ?? "";
		}

		throw new VersionException(response.StatusCode == HttpStatusCode.Unauthorized
			? "The server refused the request: set server.token (bassia config set -key server.token -value <token>) or BASSIA_SERVER_TOKEN."
			: $"The server answered {(int)response.StatusCode}: {message}");
	}

	public void Dispose()
	{
		Http.Dispose();
		Blobs.Dispose();
	}
}

/// <summary>
/// Objects on the server. Structural objects go through the server, which validates them; large chunks are uploaded
/// directly to blob storage on signed URLs, and large reads are redirected there, so the server never proxies file
/// bytes. Reads of whole objects are verified against their ids.
/// </summary>
public sealed class RemoteObjectStore(ServerConnection server) : IObjectStore, IObjectBatchReader, IObjectBatchWriter
{
	/// <summary>
	/// Many small objects in one request (<c>/objects/fetch</c>); the server leaves out what it lacks and what is too
	/// large to batch, which is then read on its direct URL.
	/// </summary>
	public async Task<IReadOnlyDictionary<ObjectId, ReadOnlyMemory<byte>>> ReadManyAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var result = new Dictionary<ObjectId, ReadOnlyMemory<byte>>();
		foreach (var part in ids.Chunk(1000))
		{
			using var response = await server.Http.PostAsJsonAsync(ServerConnection.Path("objects/fetch"), new Api.IdsRequest(part), Api.Json, ct);
			await ServerConnection.EnsureAsync(response, ct);
			foreach (var (id, bytes) in Api.Frames.Read(await response.Content.ReadAsByteArrayAsync(ct)))
			{
				result[id] = bytes;
			}
		}

		return result;
	}

	/// <summary>Many small objects in one request (<c>/objects/put</c>); large ones still go on their direct URLs.</summary>
	public async Task PutManyAsync(IReadOnlyList<(ObjectId Id, ReadOnlyMemory<byte> Bytes)> objects, CancellationToken ct = default)
	{
		var small = objects.Where(item => item.Bytes.Length < ServerConnection.DirectTransferThreshold).ToList();
		const int BatchBytes = 8 * 1024 * 1024;
		var batch = new List<(ObjectId, ReadOnlyMemory<byte>)>();
		var size = 0;
		async Task FlushAsync()
		{
			if (batch.Count == 0)
			{
				return;
			}

			using var content = new ByteArrayContent(Api.Frames.Write(batch));
			content.Headers.ContentType = new MediaTypeHeaderValue(Api.Frames.MediaType);
			using var response = await server.Http.PostAsync(ServerConnection.Path("objects/put"), content, ct);
			await ServerConnection.EnsureAsync(response, ct);
			batch.Clear();
			size = 0;
		}

		foreach (var item in small)
		{
			if (size + item.Bytes.Length > BatchBytes)
			{
				await FlushAsync();
			}

			batch.Add(item);
			size += item.Bytes.Length;
		}

		await FlushAsync();
		foreach (var (id, bytes) in objects.Where(item => item.Bytes.Length >= ServerConnection.DirectTransferThreshold))
		{
			await PutAsync(id, bytes, ct);
		}
	}

	public async ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var result = new List<bool>(ids.Count);
		foreach (var batch in ids.Chunk(1000))
		{
			result.AddRange((await server.PostAsync<Api.HaveReply>("objects/have", new Api.IdsRequest(batch), ct)).Have);
		}

		return result;
	}

	public async ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default)
	{
		using var response = await server.Http.GetAsync(ServerConnection.Path($"objects/{id.Hex}/stat"), ct);
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			return null;
		}

		await ServerConnection.EnsureAsync(response, ct);
		return new ObjectStat(id, (await response.Content.ReadFromJsonAsync<Api.StatReply>(Api.Json, ct))!.Length);
	}

	public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, ByteRange? range = null, CancellationToken ct = default)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, ServerConnection.Path($"objects/{id.Hex}"));
		if (range is { Length: > 0 } r)
		{
			request.Headers.Range = new RangeHeaderValue(r.Offset, r.Offset + r.Length - 1);
		}

		using var response = await server.Http.SendAsync(request, ct);
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			return null;
		}

		await ServerConnection.EnsureAsync(response, ct);
		return await response.Content.ReadAsByteArrayAsync(ct);
	}

	public async ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
	{
		if (bytes.Length >= ServerConnection.DirectTransferThreshold)
		{
			var ticket = await BeginUploadAsync(id, bytes.Length, ct);
			if (ticket.Url is { } url)
			{
				using var direct = new ByteArrayContent(bytes.ToArray());
				using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = direct };
				// Azure Blob Storage needs the blob type on a SAS upload; S3 and the server's own blob URLs ignore it.
				request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
				using var put = await server.Blobs.SendAsync(request, ct);
				await ServerConnection.EnsureAsync(put, ct);
				return;
			}
		}

		using var content = new ByteArrayContent(bytes.ToArray());
		content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		using var response = await server.Http.PutAsync(ServerConnection.Path($"objects/{id.Hex}"), content, ct);
		await ServerConnection.EnsureAsync(response, ct);
	}

	public async ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default)
	{
		var reply = await server.PostAsync<Api.UrlsReply>("objects/upload-urls", new Api.UploadRequest([id], [length]), ct);
		var entry = reply.Urls.Single();
		return new UploadTicket(id, entry.Url is null ? null : new Uri(entry.Url), entry.Expires);
	}
}

/// <summary>Heads on the server: every compare-and-swap is decided there.</summary>
public sealed class RemoteViewStore(ServerConnection server) : IViewStore
{
	public async ValueTask<ObjectId?> GetHeadAsync(HeadName name, CancellationToken ct = default) =>
		(await server.GetAsync<Api.HeadReply>($"heads/{Uri.EscapeDataString(name.Value)}", ct)).Head;

	public async ValueTask<HeadUpdate> AdvanceHeadAsync(HeadName name, ObjectId? expected, ObjectId next, CancellationToken ct = default)
	{
		var reply = await server.PostAsync<Api.AdvanceReply>("heads/advance", new Api.AdvanceRequest(name.Value, expected, next), ct);
		return new HeadUpdate(reply.Success, reply.Current);
	}

	public async IAsyncEnumerable<(HeadName Name, ObjectId Head)> ListHeadsAsync(string prefix = "", [EnumeratorCancellation] CancellationToken ct = default)
	{
		foreach (var entry in await server.GetAsync<List<Api.HeadEntry>>($"heads?prefix={Uri.EscapeDataString(prefix)}", ct))
		{
			yield return (new HeadName(entry.Name), entry.Head);
		}
	}

	public async ValueTask<bool> DeleteHeadAsync(HeadName name, ObjectId expected, CancellationToken ct = default) =>
		(await server.PostAsync<Api.AdvanceReply>("heads/delete", new Api.DeleteHeadRequest(name.Value, expected), ct)).Success;
}

/// <summary>Mainline's path index on the server (StatPaths, ChangedSince); only the server's landing service applies to it.</summary>
public sealed class RemotePathIndex(ServerConnection server) : IPathIndex
{
	public async ValueTask<PathVersion?> GetAsync(string path, long seq, CancellationToken ct = default) =>
		(await StatAsync([path], seq, ct))[0];

	/// <summary>Several paths in one call.</summary>
	public async Task<IReadOnlyList<PathVersion?>> StatAsync(IReadOnlyList<string> paths, long seq, CancellationToken ct = default) =>
		(await server.PostAsync<Api.StatPathsReply>("paths/stat", new Api.StatPathsRequest(paths, seq), ct)).Versions.Select(version => version?.ToVersion()).ToList();

	public async IAsyncEnumerable<PathListing> ListAsync(string dir, long seq, [EnumeratorCancellation] CancellationToken ct = default)
	{
		foreach (var item in await server.GetAsync<List<Api.ListingDto>>($"paths/list?dir={Uri.EscapeDataString(dir)}&seq={seq}", ct))
		{
			yield return new PathListing(item.Path, item.IsDirectory, item.Version?.ToVersion());
		}
	}

	public async IAsyncEnumerable<PathVersion> ChangedSinceAsync(string prefix, long afterSeq, [EnumeratorCancellation] CancellationToken ct = default)
	{
		foreach (var item in await server.GetAsync<List<Api.VersionDto>>($"paths/changed?prefix={Uri.EscapeDataString(prefix)}&after={afterSeq}", ct))
		{
			yield return item.ToVersion();
		}
	}

	public ValueTask ApplyAsync(long seq, IReadOnlyList<PathChange> changes, CancellationToken ct = default) =>
		throw new VersionException("Only the server's landing service changes the path index.");

	public async ValueTask<long> IndexedSeqAsync(CancellationToken ct = default) => (await server.GetAsync<Api.SeqReply>("paths/indexed", ct)).Seq;
}

/// <summary>Leases on the server; waiting happens there.</summary>
public sealed class RemoteLeaseStore(ServerConnection server) : ILeaseStore
{
	public async ValueTask<LeaseResult> AcquireAsync(LeaseRequest request, CancellationToken ct = default)
	{
		var reply = await server.PostAsync<Api.LeaseReply>("leases/acquire",
			new Api.AcquireRequest(request.Prefix, request.Mode, request.Owner, request.Ttl.TotalSeconds, request.Wait.TotalSeconds, request.Run), ct);
		return new LeaseResult(reply.Lease, reply.Blocking);
	}

	public async ValueTask<Lease?> RenewAsync(string id, long token, TimeSpan ttl, CancellationToken ct = default) =>
		(await server.PostAsync<Api.LeaseReply>("leases/renew", new Api.RenewRequest(id, token, ttl.TotalSeconds), ct)).Lease;

	public async ValueTask ReleaseAsync(string id, long token, CancellationToken ct = default) =>
		await server.PostAsync("leases/release", new Api.ReleaseRequest(id, token), ct);

	public async ValueTask<IReadOnlyList<Lease>> ListAsync(string? prefix = null, CancellationToken ct = default) =>
		await server.GetAsync<List<Lease>>(prefix is null ? "leases" : $"leases?prefix={Uri.EscapeDataString(prefix)}", ct);

	public async ValueTask<Lease?> GetAsync(string id, CancellationToken ct = default) =>
		(await server.GetAsync<Api.LeaseReply>($"leases/{Uri.EscapeDataString(id)}", ct)).Lease;
}

/// <summary>Landing on the server: the only place mainline advances for a clone.</summary>
public sealed class RemoteLanding(ServerConnection server) : ILanding
{
	public Task<LandResult> LandAsync(LandRequest request, CancellationToken ct = default) =>
		server.PostAsync<LandResult>("land", Api.LandRequestDto.From(request), ct);
}
