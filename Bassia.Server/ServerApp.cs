namespace Bassia.Server;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Amazon;
using Amazon.S3;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Interop;
using Bassia.Stores;
using Bassia.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenTelemetry.Metrics;

/// <summary>
/// The single-node server: the version core over the server's stores (a blob container - local disk or S3 - and
/// SQLite for heads, path index and leases), the RPC surface clients' remote stores call, signed URLs so large chunks
/// never pass through it, the landing service (the only writer of mainline), a watch stream, and the dashboard.
/// </summary>
internal sealed class ServerApp : IAsyncDisposable
{
	public static readonly Meter Meter = new("Bassia.Server");
	private static readonly Counter<long> Landings = Meter.CreateCounter<long>("bassia.landings", description: "Landing attempts by outcome");
	private static readonly Counter<long> ObjectsStored = Meter.CreateCounter<long>("bassia.objects.stored", description: "Objects stored through the server");
	private static readonly Counter<long> BytesServed = Meter.CreateCounter<long>("bassia.bytes.served", description: "Object bytes served by the server itself");
	private static readonly Counter<long> Redirects = Meter.CreateCounter<long>("bassia.reads.redirected", description: "Reads sent to blob storage on a signed URL");

	private readonly ServerOptions options;
	private readonly byte[] signingKey;
	private readonly List<Channel<Api.WatchEvent>> watchers = [];
	private readonly Lock watchGate = new();
	private WebApplication? app;

	public ServerApp(ServerOptions options)
	{
		this.options = options;
		Directory.CreateDirectory(options.Data);
		signingKey = LoadKey(Path.Combine(options.Data, "blob.key"));
		State = options.State.ToLowerInvariant() switch
		{
			"sqlite" => new SqliteState(Path.Combine(options.Data, "state.db")),
			"postgres" => new PostgresState(options.Postgres ?? throw new ArgumentException("-postgres <connection string> is required with -state postgres.")),
			var other => throw new ArgumentException($"-state is sqlite or postgres, not '{other}'.")
		};
		if (options.Storage.Equals("azure", StringComparison.OrdinalIgnoreCase))
		{
			var azure = options.AzureConnection is { } connection
				? AzureBlobObjectStore.FromConnectionString(connection, options.AzureContainer, options.BucketPrefix)
				: options.AzureUrl is { } url
					? AzureBlobObjectStore.FromContainerUrl(new Uri(url), options.BucketPrefix)
					: throw new ArgumentException("-storage azure needs -azure-connection <string> or -azure-url <container url>.");
			Blobs = azure;
			Maintenance = azure;
			Azure = azure;
		}
		else if (options.Storage.Equals("s3", StringComparison.OrdinalIgnoreCase))
		{
			var client = options.Region is null ? new AmazonS3Client() : new AmazonS3Client(RegionEndpoint.GetBySystemName(options.Region));
			var s3 = new S3ObjectStore(client, options.Bucket ?? throw new ArgumentException("-bucket is required with -storage s3."), options.BucketPrefix);
			Blobs = s3;
			Maintenance = s3;
			S3 = s3;
		}
		else
		{
			var disk = new DiskObjectStore(Path.Combine(options.Data, "objects"));
			disk.UploadUrls = (id, _) => new UploadTicket(id, SignedUrl(id, "put"), DateTimeOffset.UtcNow + UrlLifetime);
			Blobs = disk;
			Maintenance = disk;
			Disk = disk;
		}

		Repo = new Repository(Blobs, new NotifyingViewStore(State, this), State, State, options.Chunks);
		var validate = options.Validate is { } command ? new ShellValidationHook(Repo, command) : null;
		Landing = new LandingService(Repo, MergeDrivers.Semantic(options.SemanticDriver), validate);
		Landing.Landed += landed => Publish(new Api.WatchEvent("landed", landed.Seq, landed.Prefixes, landed.Workspace, landed.Time));
	}

	private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(30);

	public IServerState State { get; }

	public AzureBlobObjectStore? Azure { get; }

	public IObjectStore Blobs { get; }

	public IObjectStoreMaintenance Maintenance { get; }

	public DiskObjectStore? Disk { get; }

	public S3ObjectStore? S3 { get; }

	public Repository Repo { get; }

	public LandingService Landing { get; }

	/// <summary>Where the server listens once started (the actual port when the options asked for port 0).</summary>
	public string? Address { get; private set; }

	private string PublicUrl => (options.PublicUrl ?? Address ?? options.Url).TrimEnd('/');

	public async Task StartAsync()
	{
		if (Azure is not null && options.AzureConnection is not null)
		{
			await Azure.CreateIfMissingAsync();
		}

		await Repo.InitializeAsync("bassia-server");
		var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls(options.Url);
		if (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 })
		{
			builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(Meter.Name).AddOtlpExporter());
		}

		builder.Services.AddResponseCompression(compression =>
		{
			compression.EnableForHttps = true;
			compression.MimeTypes = ["application/json", "application/octet-stream", "text/html", "text/css", "text/javascript"];
			compression.ExcludedMimeTypes = ["text/event-stream"];
		});
		builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
		builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
		app = builder.Build();
		app.UseResponseCompression();
		var dashboard = new Dashboard(context => Task.FromResult(new DashboardScope(Repo, Landing, $"bassia-server · {options.Data}", ReadLogAsync,
			Events: "/events")));
		app.Use(AuthorizeAsync);
		app.UseWhen(context => Dashboard.IsDashboardPath(context.Request.Path), branch => branch.Use(dashboard.ServeAsync));
		dashboard.Map(app);
		MapApi(app);
		await app.StartAsync();
		Address = app.Urls.First();
	}

	public Task WaitForShutdownAsync() => app!.WaitForShutdownAsync();

	private async Task<string> ReadLogAsync(RunRecord run) =>
		run.Log is { } id && await Blobs.ReadAsync(id) is { } bytes ? Encoding.UTF8.GetString(bytes.Span) : run.LastLine ?? "";

	// ----- authorization -----

	private async Task AuthorizeAsync(HttpContext context, RequestDelegate next)
	{
		var path = context.Request.Path;
		if (options.Token is null || path.StartsWithSegments("/blobs") || path == "/healthz" || Dashboard.IsAsset(path))
		{
			await next(context);
			return;
		}

		var presented = context.Request.Headers.Authorization.ToString() is { } header && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..]
			: context.Request.Query["token"].FirstOrDefault() ?? context.Request.Cookies["bassia_token"];
		if (presented is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(options.Token)))
		{
			if (context.Request.Query.ContainsKey("token"))
			{
				context.Response.Cookies.Append("bassia_token", presented, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict });
			}

			await next(context);
			return;
		}

		context.Response.StatusCode = StatusCodes.Status401Unauthorized;
		await context.Response.WriteAsJsonAsync(new Api.ErrorReply("A bearer token is required (or ?token=<token> once for the dashboard)."), Api.Json);
	}

	// ----- signed blob URLs -----

	private static byte[] LoadKey(string file)
	{
		if (File.Exists(file))
		{
			return File.ReadAllBytes(file);
		}

		var key = RandomNumberGenerator.GetBytes(32);
		File.WriteAllBytes(file, key);
		return key;
	}

	private string Signature(ObjectId id, string operation, long expires) =>
		Convert.ToHexStringLower(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes($"{operation}:{id.Hex}:{expires}")));

	/// <summary>A URL that lets its holder read or write one blob until it expires, without the server's token: the disk store's presigned URL.</summary>
	public Uri SignedUrl(ObjectId id, string operation)
	{
		var expires = (DateTimeOffset.UtcNow + UrlLifetime).ToUnixTimeSeconds();
		return new Uri($"{PublicUrl}/blobs/{id.Hex}?op={operation}&exp={expires}&sig={Signature(id, operation, expires)}");
	}

	private bool Verify(ObjectId id, string operation, HttpRequest request) =>
		request.Query["op"] == operation && long.TryParse(request.Query["exp"], out var expires) && expires >= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
		&& CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Signature(id, operation, expires)), Encoding.ASCII.GetBytes(request.Query["sig"].ToString()));

	private async Task<Uri> ReadUrlAsync(ObjectId id) =>
		S3 is not null ? await S3.ReadUrlAsync(id) : Azure is not null ? await Azure.ReadUrlAsync(id) : SignedUrl(id, "get");

	// ----- watch -----

	private void Publish(Api.WatchEvent change)
	{
		lock (watchGate)
		{
			foreach (var watcher in watchers)
			{
				watcher.Writer.TryWrite(change);
			}
		}
	}

	/// <summary>Publishes a moved workspace head to watchers (the dashboard refreshes on it).</summary>
	private sealed class NotifyingViewStore(IViewStore inner, ServerApp server) : IViewStore
	{
		public ValueTask<ObjectId?> GetHeadAsync(HeadName name, CancellationToken ct = default) => inner.GetHeadAsync(name, ct);

		public async ValueTask<HeadUpdate> AdvanceHeadAsync(HeadName name, ObjectId? expected, ObjectId next, CancellationToken ct = default)
		{
			var update = await inner.AdvanceHeadAsync(name, expected, next, ct);
			if (update.Success && name.IsWorkspace)
			{
				server.Publish(new Api.WatchEvent("workspace", Workspace: name.WorkspaceId, Time: DateTimeOffset.UtcNow));
			}

			return update;
		}

		public IAsyncEnumerable<(HeadName Name, ObjectId Head)> ListHeadsAsync(string prefix = "", CancellationToken ct = default) => inner.ListHeadsAsync(prefix, ct);

		public ValueTask<bool> DeleteHeadAsync(HeadName name, ObjectId expected, CancellationToken ct = default) => inner.DeleteHeadAsync(name, expected, ct);
	}

	private async Task WatchAsync(HttpContext context)
	{
		var channel = Channel.CreateBounded<Api.WatchEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
		lock (watchGate)
		{
			watchers.Add(channel);
		}

		try
		{
			context.Response.ContentType = "text/event-stream";
			context.Response.Headers.CacheControl = "no-cache";
			await context.Response.WriteAsync(": watching\n\n", context.RequestAborted);
			await context.Response.Body.FlushAsync(context.RequestAborted);
			await foreach (var change in channel.Reader.ReadAllAsync(context.RequestAborted))
			{
				var json = System.Text.Json.JsonSerializer.Serialize(change, Api.Json);
				await context.Response.WriteAsync($"event: change\ndata: {json}\n\n", context.RequestAborted);
				await context.Response.Body.FlushAsync(context.RequestAborted);
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			lock (watchGate)
			{
				watchers.Remove(channel);
			}
		}
	}

	// ----- API -----

	private static IResult Json(object value) => Results.Json(value, Api.Json);

	private static IResult Error(int status, string message) => Results.Json(new Api.ErrorReply(message), Api.Json, statusCode: status);

	private static async Task<T> BodyAsync<T>(HttpRequest request) =>
		await request.ReadFromJsonAsync<T>(Api.Json) ?? throw new BadHttpRequestException("A JSON body is required.");

	private static bool TryId(string text, out ObjectId id) => ObjectId.TryParse(text, out id);

	private void MapApi(WebApplication web)
	{
		web.MapGet("/healthz", () => Results.Text("ok"));
		web.MapGet("/events", WatchAsync);
		var api = web.MapGroup(Api.Prefix);
		api.AddEndpointFilter(async (context, next) =>
		{
			try
			{
				return await next(context);
			}
			catch (Exception ex) when (ex is VersionException or MonorepoException or InvalidDataException or BadHttpRequestException or System.Text.Json.JsonException)
			{
				return Error(StatusCodes.Status400BadRequest, ex.Message);
			}
		});

		api.MapGet("/info", () => Json(new Api.Info("bassia-server", typeof(ServerApp).Assembly.GetName().Version?.ToString() ?? "0", options.Chunks.Min, options.Chunks.Average,
			options.Chunks.Max, options.Storage)));
		api.MapGet("/watch", WatchAsync);

		// GetHead: mainline's head and seq.
		api.MapGet("/main", async () =>
		{
			var main = await Repo.GetMainAsync();
			return Json(new Api.MainReply(main.Head, main.Seq, main.CommitId, main.Tree));
		});

		// Heads (the view store).
		api.MapGet("/heads/{*name}", async (string name) => Json(new Api.HeadReply(await State.GetHeadAsync(new HeadName(Uri.UnescapeDataString(name))))));
		api.MapGet("/heads", async (string? prefix) =>
		{
			var list = new List<Api.HeadEntry>();
			await foreach (var (name, head) in State.ListHeadsAsync(prefix ?? ""))
			{
				list.Add(new Api.HeadEntry(name.Value, head));
			}

			return Json(list);
		});
		api.MapPost("/heads/advance", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.AdvanceRequest>(request);
			var name = new HeadName(body.Name);
			if (name == HeadName.Main)
			{
				return Error(StatusCodes.Status403Forbidden, "Only the landing service advances mainline; call land.");
			}

			if (!name.IsWorkspace && !name.Value.StartsWith("pin/", StringComparison.Ordinal))
			{
				return Error(StatusCodes.Status400BadRequest, $"'{name}' is not a head a client may move.");
			}

			// The operation and its view must be stored and well formed before a head may point at them.
			var op = await Repo.Objects.ReadOperationAsync(body.Next);
			await Repo.Objects.ReadViewAsync(op.View);
			var update = await Repo.Views.AdvanceHeadAsync(name, body.Expected, body.Next);
			return Json(new Api.AdvanceReply(update.Success, update.Current));
		});
		api.MapPost("/heads/delete", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.DeleteHeadRequest>(request);
			var name = new HeadName(body.Name);
			return name == HeadName.Main ? Error(StatusCodes.Status403Forbidden, "Mainline cannot be deleted.") : Json(new Api.AdvanceReply(await State.DeleteHeadAsync(name, body.Expected), null));
		});

		// Objects.
		api.MapPost("/objects/have", async (HttpRequest request) => Json(new Api.HaveReply(await Blobs.HasAsync((await BodyAsync<Api.IdsRequest>(request)).Ids))));
		api.MapGet("/objects/{id}/stat", async (string id) =>
			TryId(id, out var objectId) && await Blobs.StatAsync(objectId) is { } stat ? Json(new Api.StatReply(stat.Length)) : Error(404, $"No object {id}."));
		api.MapGet("/objects/{id}", ReadObjectAsync);
		api.MapPut("/objects/{id}", PutObjectAsync);
		api.MapPost("/objects/batch", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.BatchRequest>(request);
			var result = new List<Api.BatchObject>();
			var pending = new Queue<(ObjectId Id, int Depth)>(body.Ids.Select(id => (id, 0)));
			var seen = new HashSet<ObjectId>();
			while (pending.TryDequeue(out var item) && result.Count < 5000)
			{
				if (!seen.Add(item.Id) || await Blobs.ReadAsync(item.Id) is not { } bytes || ObjectCodec.TypeOf(bytes) is null)
				{
					continue;
				}

				result.Add(new Api.BatchObject(item.Id, Convert.ToBase64String(bytes.Span)));
				if (item.Depth < body.Depth && ObjectCodec.Decode(bytes) is Tree tree)
				{
					foreach (var entry in tree.Entries.Where(entry => entry.Kind == EntryKind.Tree))
					{
						pending.Enqueue((entry.Id, item.Depth + 1));
					}
				}
			}

			return Json(new Api.BatchReply(result));
		});
		// Many small objects in one reply; those missing or large enough for a direct URL are left out.
		api.MapPost("/objects/fetch", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.IdsRequest>(request);
			var found = new ConcurrentBag<(ObjectId, ReadOnlyMemory<byte>)>();
			await Parallel.ForEachAsync(body.Ids.Distinct().Take(5000), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = request.HttpContext.RequestAborted },
				async (id, ct) =>
				{
					if (await Blobs.StatAsync(id, ct) is { Length: < ServerConnection.DirectTransferThreshold } && await Blobs.ReadAsync(id, null, ct) is { } bytes)
					{
						found.Add((id, bytes));
					}
				});
			var reply = Api.Frames.Write(found);
			BytesServed.Add(reply.Length);
			return Results.Bytes(reply, "application/octet-stream");
		});

		// Many small objects in one request, each checked like a single put.
		api.MapPost("/objects/put", async (HttpRequest request) =>
		{
			var objects = Api.Frames.Read(await ReadBodyAsync(request));
			foreach (var (id, bytes) in objects)
			{
				if (ObjectId.Of(bytes.Span) != id)
				{
					return Error(StatusCodes.Status400BadRequest, $"Object {id.Short}: the bytes do not hash to its id.");
				}

				if (ObjectCodec.TypeOf(bytes) is not null)
				{
					try
					{
						ObjectCodec.Decode(bytes);
					}
					catch (InvalidDataException ex)
					{
						return Error(StatusCodes.Status400BadRequest, $"Refused a malformed object {id.Short}: {ex.Message}");
					}
				}
			}

			await Parallel.ForEachAsync(objects, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = request.HttpContext.RequestAborted },
				async (item, ct) => await Blobs.PutAsync(item.Id, item.Bytes, ct));
			ObjectsStored.Add(objects.Count);
			return Results.NoContent();
		});

		api.MapPost("/objects/upload-urls", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.UploadRequest>(request);
			var urls = new List<Api.UrlEntry>();
			for (var index = 0; index < body.Ids.Count; index++)
			{
				var ticket = await Blobs.BeginUploadAsync(body.Ids[index], index < body.Lengths.Count ? body.Lengths[index] : 0);
				urls.Add(new Api.UrlEntry(ticket.Id, ticket.Url?.ToString(), ticket.Expires));
			}

			return Json(new Api.UrlsReply(urls));
		});
		api.MapPost("/objects/read-urls", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.IdsRequest>(request);
			var urls = new List<Api.UrlEntry>();
			foreach (var id in body.Ids)
			{
				urls.Add(new Api.UrlEntry(id, (await ReadUrlAsync(id)).ToString(), DateTimeOffset.UtcNow + UrlLifetime));
			}

			return Json(new Api.UrlsReply(urls));
		});

		// Path index (StatPaths, ChangedSince).
		api.MapPost("/paths/stat", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.StatPathsRequest>(request);
			await IndexAsync();
			var versions = await State.GetManyAsync(body.Paths, body.Seq);
			return Json(new Api.StatPathsReply(versions.Select(version => version is null ? null : Api.VersionDto.From(version)).ToList()));
		});
		api.MapGet("/paths/list", async (string? dir, long seq) =>
		{
			await IndexAsync();
			var list = new List<Api.ListingDto>();
			await foreach (var item in State.ListAsync(dir ?? "", seq))
			{
				list.Add(new Api.ListingDto(item.Path, item.IsDirectory, item.Version is null ? null : Api.VersionDto.From(item.Version)));
			}

			return Json(list);
		});
		api.MapGet("/paths/changed", async (string? prefix, long after) =>
		{
			await IndexAsync();
			var list = new List<Api.VersionDto>();
			await foreach (var version in State.ChangedSinceAsync(prefix ?? "", after))
			{
				list.Add(Api.VersionDto.From(version));
			}

			return Json(list);
		});
		api.MapGet("/paths/indexed", async () =>
		{
			await IndexAsync();
			return Json(new Api.SeqReply(await State.IndexedSeqAsync()));
		});

		// Leases.
		api.MapPost("/leases/acquire", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.AcquireRequest>(request);
			var wait = TimeSpan.FromSeconds(Math.Clamp(body.WaitSeconds, 0, 3600));
			var result = await State.AcquireAsync(new LeaseRequest(body.Prefix, body.Mode, body.Owner, TimeSpan.FromSeconds(Math.Max(1, body.TtlSeconds)), wait, body.Run),
				request.HttpContext.RequestAborted);
			return Json(new Api.LeaseReply(result.Lease, result.Blocking));
		});
		api.MapPost("/leases/renew", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.RenewRequest>(request);
			return Json(new Api.LeaseReply(await State.RenewAsync(body.Id, body.Token, TimeSpan.FromSeconds(Math.Max(1, body.TtlSeconds))), []));
		});
		api.MapPost("/leases/release", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.ReleaseRequest>(request);
			await State.ReleaseAsync(body.Id, body.Token);
			return Json(new Api.LeaseReply(null, []));
		});
		api.MapGet("/leases", async (string? prefix) => Json(await State.ListAsync(prefix)));
		api.MapGet("/leases/{id}", async (string id) => Json(new Api.LeaseReply(await State.GetAsync(id), [])));

		// Land.
		api.MapPost("/land", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.LandRequestDto>(request);
			var result = await Landing.LandAsync(body.ToRequest());
			Landings.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome));
			return Json(result);
		});

		// Signed blob access: the disk store's stand-in for presigned cloud URLs.
		web.MapGet("/blobs/{id}", async (string id, HttpRequest request, HttpResponse response) =>
		{
			if (Disk is null || !TryId(id, out var objectId) || !Verify(objectId, "get", request))
			{
				return Error(StatusCodes.Status403Forbidden, "The signature is invalid or expired.");
			}

			var path = Disk.PathOf(objectId);
			return File.Exists(path) ? Results.File(path, "application/octet-stream", enableRangeProcessing: true) : Error(404, $"No object {id}.");
		});
		web.MapPut("/blobs/{id}", async (string id, HttpRequest request) =>
		{
			if (Disk is null || !TryId(id, out var objectId) || !Verify(objectId, "put", request))
			{
				return Error(StatusCodes.Status403Forbidden, "The signature is invalid or expired.");
			}

			var bytes = await ReadBodyAsync(request);
			if (ObjectId.Of(bytes) != objectId)
			{
				return Error(StatusCodes.Status400BadRequest, "The bytes do not hash to the object's id.");
			}

			await Disk.PutAsync(objectId, bytes);
			ObjectsStored.Add(1);
			return Results.NoContent();
		});
	}

	private async Task IndexAsync() => await Repo.EnsureIndexedAsync(await Repo.GetMainAsync());

	private static async Task<byte[]> ReadBodyAsync(HttpRequest request)
	{
		using var buffer = new MemoryStream();
		await request.Body.CopyToAsync(buffer, request.HttpContext.RequestAborted);
		return buffer.ToArray();
	}

	/// <summary>Serves an object: small ones and structural objects directly, large chunks by redirect to a signed URL.</summary>
	private async Task<IResult> ReadObjectAsync(string id, HttpRequest request)
	{
		if (!TryId(id, out var objectId) || await Blobs.StatAsync(objectId) is not { } stat)
		{
			return Error(404, $"No object {id}.");
		}

		if (stat.Length >= ServerConnection.DirectTransferThreshold)
		{
			Redirects.Add(1);
			return Results.Redirect((await ReadUrlAsync(objectId)).ToString(), permanent: false, preserveMethod: true);
		}

		ByteRange? range = null;
		if (RangeHeaderValue.TryParse(request.Headers.Range.ToString(), out var header) && header.Ranges.FirstOrDefault() is { From: { } from } item)
		{
			range = new ByteRange(from, (item.To ?? stat.Length - 1) - from + 1);
		}

		var bytes = await Blobs.ReadAsync(objectId, range);
		if (bytes is null)
		{
			return Error(404, $"No object {id}.");
		}

		BytesServed.Add(bytes.Value.Length);
		return Results.Bytes(bytes.Value.ToArray(), "application/octet-stream");
	}

	/// <summary>
	/// Stores an object sent through the server. Structural objects (tree, file, conflict, commit, operation, view) are
	/// decoded and so validated synchronously; chunks need only hash to their id.
	/// </summary>
	private async Task<IResult> PutObjectAsync(string id, HttpRequest request)
	{
		if (!TryId(id, out var objectId))
		{
			return Error(StatusCodes.Status400BadRequest, $"'{id}' is not an object id.");
		}

		var bytes = await ReadBodyAsync(request);
		if (ObjectId.Of(bytes) != objectId)
		{
			return Error(StatusCodes.Status400BadRequest, "The bytes do not hash to the object's id.");
		}

		if (ObjectCodec.TypeOf(bytes) is not null)
		{
			try
			{
				ObjectCodec.Decode(bytes);
			}
			catch (InvalidDataException ex)
			{
				return Error(StatusCodes.Status400BadRequest, $"Refused a malformed object: {ex.Message}");
			}
		}

		await Blobs.PutAsync(objectId, bytes);
		ObjectsStored.Add(1);
		return Results.NoContent();
	}

	public async ValueTask DisposeAsync()
	{
		if (app is not null)
		{
			await app.StopAsync();
			await app.DisposeAsync();
		}
	}
}
