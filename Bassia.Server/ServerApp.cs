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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenTelemetry.Metrics;

/// <summary>
/// The single-node server: the version core over the server's stores (a blob container - local disk or S3 - and
/// PostgreSQL for heads, path index, leases and trees), the RPC surface clients' remote stores call, signed URLs so large chunks
/// never pass through it, the landing service (the only writer of mainline) and a watch stream. It is a Web API only: Bassia Studio is its web UI.
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
	private string? accessToken;

	public ServerApp(ServerOptions options)
	{
		this.options = options;
		Directory.CreateDirectory(options.Data);
		signingKey = LoadKey(Path.Combine(options.Data, "blob.key"));
		State = new PostgresState(options.Postgres
			?? throw new ArgumentException("-postgres <connection string> is required (or BASSIA_SERVER_POSTGRES): bassia-server keeps its state in PostgreSQL."), options.Migrate);
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

		Trees = new PostgresTreeStore(options.Postgres, options.Migrate);
		// The object catalog sits over the blob store (and over the harness's probe, which then counts only what
		// reaches blob storage): "have" is answered from PostgreSQL, and maintenance lists and sweeps through it.
		Catalog = new CatalogedObjectStore(options.DecorateBlobs?.Invoke(Blobs) ?? Blobs, State, Maintenance);
		Blobs = Catalog;
		Maintenance = Catalog;
		Repo = new Repository(Blobs, new NotifyingViewStore(State, this), State, State, options.Chunks, Trees, new NotifyingWorkspaceIndex(State, this));
		Repo.Objects.CacheLimitBytes = options.ObjectCache;
		var validate = options.Validate is { } command ? new ShellValidationHook(Repo, command) : null;
		Landing = new LandingService(Repo, MergeDrivers.Semantic(options.SemanticDriver), validate);
		Landing.Landed += landed => Publish(new Api.WatchEvent("landed", landed.Seq, landed.Prefixes, landed.Workspace, landed.Time));
		Reconciling = new ReconcileQueue(Landing.Reconciler) { Log = message => Console.Error.WriteLine(message) };
		Landing.Landed += landed => Reconciling.Landed(landed.Seq, landed.Workspace);
		Landing.Reconciler.Reconciled += candidate =>
			Publish(new Api.WatchEvent("reconciled", candidate.MainSeq, null, candidate.Workspace, candidate.Time));
	}

	private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(30);

	public IServerState State { get; }

	/// <summary>The relational tree store: trees live in PostgreSQL, not in blob storage.</summary>
	public PostgresTreeStore Trees { get; }

	public AzureBlobObjectStore? Azure { get; }

	public IObjectStore Blobs { get; }

	/// <summary>The blob store behind the object catalog: what <see cref="Blobs"/> and <see cref="Maintenance"/> are.</summary>
	public CatalogedObjectStore Catalog { get; }

	public IObjectStoreMaintenance Maintenance { get; }

	public DiskObjectStore? Disk { get; }

	public S3ObjectStore? S3 { get; }

	public Repository Repo { get; }

	public LandingService Landing { get; }

	/// <summary>Keeps open workspaces reconciled with mainline in the background (bassia-f162a7fd).</summary>
	internal ReconcileQueue Reconciling { get; }

	/// <summary>Where the server listens once started (the actual port when the options asked for port 0).</summary>
	public string? Address { get; private set; }

	private string PublicUrl => (options.PublicUrl ?? Address ?? options.Url).TrimEnd('/');

	public async Task StartAsync()
	{
		if (Azure is not null && options.AzureConnection is not null)
		{
			await Azure.CreateIfMissingAsync();
		}

		accessToken = ResolveToken();
		if (!await Catalog.CompleteIfEmptyAsync())
		{
			Console.Error.WriteLine("bassia-server: the object catalog does not cover this blob store yet (a store from before it); " +
				"until 'bassia-server gc' has reconciled it once, unknown objects are asked of blob storage.");
		}

		await Repo.InitializeAsync("bassia-server");
		// Heads the registry does not describe (the first start on it, or heads an older node moved) are recorded now.
		if (await Repo.SyncWorkspaceIndexAsync() is > 0 and var recorded)
		{
			Console.Error.WriteLine($"bassia-server: recorded {recorded} workspace head(s) in the workspace registry.");
		}

		Reconciling.Start();
		backfill = Task.Run(BackfillAsync);
		var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls(options.Url);
		builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.PutLimit);
		if (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 })
		{
			builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(Meter.Name).AddOtlpExporter());
		}

		builder.Services.AddResponseCompression(compression =>
		{
			compression.EnableForHttps = true;
			compression.MimeTypes = ["application/json", "application/octet-stream"];
			compression.ExcludedMimeTypes = ["text/event-stream"];
		});
		builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
		builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
		if (options.CorsOrigins.Count > 0)
		{
			builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy.WithOrigins([.. options.CorsOrigins])
				.AllowAnyMethod().WithHeaders("Authorization", "Content-Type", "Range", "Accept").WithExposedHeaders("Location", "Content-Range")));
		}

		app = builder.Build();
		if (options.RequestProbe is { } probe)
		{
			app.Use(probe);
		}

		app.UseResponseCompression();
		if (options.CorsOrigins.Count > 0)
		{
			// Ahead of authorization: a browser's preflight carries no token.
			app.UseCors();
		}

		app.Use(AuthorizeAsync);
		MapApi(app);
		await app.StartAsync();
		Address = app.Urls.First();
	}

	public Task WaitForShutdownAsync() => app!.WaitForShutdownAsync();

	private Task? backfill;
	private readonly CancellationTokenSource stopping = new();

	/// <summary>The mainline index's backfill, when the server started one (tests wait for it).</summary>
	internal Task Backfilled => backfill ?? Task.CompletedTask;

	/// <summary>
	/// Records the landings from before the mainline index, in the background: until it reaches a seq, reading mainline
	/// at that seq walks the log as it always did. Several nodes may run it at once; they record the same rows.
	/// </summary>
	private async Task BackfillAsync()
	{
		try
		{
			if (await Repo.BackfillMainlineIndexAsync(stopping.Token) is > 0 and var recorded)
			{
				Console.Error.WriteLine($"bassia-server: recorded {recorded} landing(s) in the mainline index.");
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"bassia-server: the mainline index's backfill stopped: {ex.Message}; it resumes at the next start.");
		}
	}

	// ----- authorization -----

	/// <summary>The file a generated token is kept in, when the options name none.</summary>
	public string TokenFile => Path.Combine(options.Data, "token");

	/// <summary>The token this server was given or generated; absent only with <c>-no-auth</c>. Resolved at start, so <c>gc</c> and <c>scrub</c> create none.</summary>
	private string? ResolveToken() => options.NoAuth ? null : options.Token ?? LoadOrCreateToken(TokenFile);

	/// <summary>Hex, not Base64: the command lines that take a token read a value starting with '-' as another switch.</summary>
	private static string LoadOrCreateToken(string file)
	{
		if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } existing)
		{
			return existing;
		}

		var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
		var create = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
		if (!OperatingSystem.IsWindows())
		{
			create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}

		using var writer = new StreamWriter(file, create);
		writer.Write(token);
		return token;
	}

	private bool IsToken(string? presented) => presented is not null && accessToken is not null
		&& CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));

	/// <summary>Every call needs the bearer token, except the health probe, the root description and signed blob URLs.</summary>
	private async Task AuthorizeAsync(HttpContext context, RequestDelegate next)
	{
		var request = context.Request;
		var path = request.Path;
		if (accessToken is null || path.StartsWithSegments("/blobs") || path == "/healthz" || path == "/")
		{
			await next(context);
			return;
		}

		var header = request.Headers.Authorization.ToString();
		if (IsToken(header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null))
		{
			await next(context);
			return;
		}

		context.Response.StatusCode = StatusCodes.Status401Unauthorized;
		context.Response.Headers.WWWAuthenticate = "Bearer";
		await context.Response.WriteAsJsonAsync(new Api.ErrorReply("A bearer token is required: 'Authorization: Bearer <token>'. A server started without -token keeps its own in the file 'token' of its data folder."), Api.Json);
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

	/// <summary>Publishes a moved workspace head to watchers (Studio refreshes on it).</summary>
	private sealed class NotifyingViewStore(IViewStore inner, ServerApp server) : IViewStore
	{
		public ValueTask<ObjectId?> GetHeadAsync(HeadName name, CancellationToken ct = default) => inner.GetHeadAsync(name, ct);

		public async ValueTask<HeadUpdate> AdvanceHeadAsync(HeadName name, ObjectId? expected, ObjectId next, CancellationToken ct = default)
		{
			var update = await inner.AdvanceHeadAsync(name, expected, next, ct);
			if (update.Success && name.IsWorkspace)
			{
				server.Publish(new Api.WatchEvent("workspace", Workspace: name.WorkspaceId, Time: DateTimeOffset.UtcNow));
				server.Reconciling?.Workspace(name.WorkspaceId);
			}

			return update;
		}

		public IAsyncEnumerable<(HeadName Name, ObjectId Head)> ListHeadsAsync(string prefix = "", CancellationToken ct = default) => inner.ListHeadsAsync(prefix, ct);

		public ValueTask<bool> DeleteHeadAsync(HeadName name, ObjectId expected, CancellationToken ct = default) => inner.DeleteHeadAsync(name, expected, ct);
	}

	/// <summary>The registry, publishing a moved workspace head to watchers and to the reconcile queue.</summary>
	private sealed class NotifyingWorkspaceIndex(IWorkspaceIndex inner, ServerApp server) : IWorkspaceIndex
	{
		public async ValueTask<HeadUpdate> AdvanceAsync(ObjectId? expected, WorkspaceSummary next, CancellationToken ct = default)
		{
			var update = await inner.AdvanceAsync(expected, next, ct);
			if (update.Success)
			{
				server.Publish(new Api.WatchEvent("workspace", Workspace: next.Id, Time: DateTimeOffset.UtcNow));
				server.Reconciling?.Workspace(next.Id);
			}

			return update;
		}

		public ValueTask<HeadUpdate> AdvanceCandidateAsync(string workspace, ObjectId? expected, CandidateSummary? next, CancellationToken ct = default) =>
			inner.AdvanceCandidateAsync(workspace, expected, next, ct);

		public ValueTask<bool> RemoveAsync(string workspace, ObjectId expected, CancellationToken ct = default) => inner.RemoveAsync(workspace, expected, ct);

		public Task<IReadOnlyList<WorkspaceSummary>> ListAsync(bool open, int limit = int.MaxValue, DateTimeOffset? before = null, CancellationToken ct = default) =>
			inner.ListAsync(open, limit, before, ct);

		public Task<IReadOnlyList<WorkspaceSummary>> GetAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => inner.GetAsync(ids, ct);

		public Task<IReadOnlyList<WorkspaceSummary>> FindAsync(string reference, CancellationToken ct = default) => inner.FindAsync(reference, ct);

		public Task<IReadOnlyList<string>> StaleAsync(long seq, string policyPath, CancellationToken ct = default) => inner.StaleAsync(seq, policyPath, ct);

		public Task<IReadOnlyList<(HeadName Name, ObjectId Head)>> UnindexedAsync(CancellationToken ct = default) => inner.UnindexedAsync(ct);

		public ValueTask RecordAsync(WorkspaceSummary summary, CandidateSummary? candidate, CancellationToken ct = default) => inner.RecordAsync(summary, candidate, ct);

		public ValueTask PruneAsync(CancellationToken ct = default) => inner.PruneAsync(ct);
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

	private static string Version => typeof(ServerApp).Assembly.GetName().Version?.ToString() ?? "0";

	private static IResult Json(object value) => Results.Json(value, Api.Json);

	private static IResult Error(int status, string message) => Results.Json(new Api.ErrorReply(message), Api.Json, statusCode: status);

	private static async Task<T> BodyAsync<T>(HttpRequest request) =>
		await request.ReadFromJsonAsync<T>(Api.Json) ?? throw new BadHttpRequestException("A JSON body is required.");

	private static bool TryId(string text, out ObjectId id) => ObjectId.TryParse(text, out id);

	private void MapApi(WebApplication web)
	{
		web.MapGet("/healthz", () => Results.Text("ok"));
		web.MapGet("/", () => Json(new Api.ServiceReply("bassia-server", Version, Api.Prefix, "/healthz")));
		web.MapFallback("{*path}", (HttpRequest request) => Error(StatusCodes.Status404NotFound, $"No such call: {request.Method} {request.Path}. bassia-server is an API under {Api.Prefix}."));
		var api = web.MapGroup(Api.Prefix);
		api.AddEndpointFilter(async (context, next) =>
		{
			try
			{
				return await next(context);
			}
			catch (Exception ex) when (ex is VersionException or MonorepoException or InvalidDataException or BadHttpRequestException or System.Text.Json.JsonException)
			{
				return Error(ex is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest, ex.Message);
			}
		});

		api.MapGet("/info", () => Json(new Api.Info("bassia-server", Version, options.Chunks.Min, options.Chunks.Average,
			options.Chunks.Max, options.Storage)));
		api.MapGet("/watch", WatchAsync);

		// GetHead: mainline's head and seq.
		api.MapGet("/main", async () =>
		{
			var main = await Repo.GetMainAsync();
			return Json(new Api.MainReply(main.Head, main.Seq, main.CommitId, main.Tree));
		});

		// The mainline index: the landing at a seq and pages of the log, without walking it.
		api.MapGet("/main/log", async (long from, int? count) =>
		{
			await IndexAsync();
			return Json(await State.ListEntriesAsync(from, Math.Clamp(count ?? 50, 1, 5000)));
		});
		api.MapGet("/main/indexed", async () => Json(new Api.SeqReply(await State.EntryCountAsync())));

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

			if (!name.IsWorkspace && !name.IsWorkspaceName && !name.Value.StartsWith("pin/", StringComparison.Ordinal))
			{
				return Error(StatusCodes.Status400BadRequest, $"'{name}' is not a head a client may move.");
			}

			// The operation and its view must be stored and well formed before a head may point at them.
			var op = await Repo.Objects.ReadOperationAsync(body.Next);
			var view = await Repo.Objects.ReadViewAsync(op.View);
			if (name.IsWorkspaceName && view.Workspace?.Name != name.Value[HeadName.WorkspaceNamePrefix.Length..])
			{
				return Error(StatusCodes.Status400BadRequest, $"'{name}' must point at an operation of a workspace with that name.");
			}

			// A workspace head moves through the registry, which records what the server itself read from the operation.
			if (name.IsWorkspace)
			{
				if (view.Workspace is not { } record || record.Id != name.WorkspaceId)
				{
					return Error(StatusCodes.Status400BadRequest, $"'{name}' must point at an operation of that workspace.");
				}

				var moved = await Repo.Workspaces.AdvanceAsync(body.Expected, WorkspaceSummary.Of(body.Next, record));
				return Json(new Api.AdvanceReply(moved.Success, moved.Current));
			}

			var update = await Repo.Views.AdvanceHeadAsync(name, body.Expected, body.Next);
			return Json(new Api.AdvanceReply(update.Success, update.Current));
		});
		api.MapPost("/heads/delete", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.DeleteHeadRequest>(request);
			var name = new HeadName(body.Name);
			return name == HeadName.Main ? Error(StatusCodes.Status403Forbidden, "Mainline cannot be deleted.")
				: name.IsCandidate ? Error(StatusCodes.Status403Forbidden, "Reconciled candidates are kept by the server.")
				: Json(new Api.AdvanceReply(name.IsWorkspace ? await State.RemoveAsync(name.WorkspaceId, body.Expected) : await State.DeleteHeadAsync(name, body.Expected), null));
		});

		// The workspace registry: listing, finding and resolving workspaces without reading every head.
		api.MapGet("/workspaces", async (bool? open, int? limit, DateTimeOffset? before) =>
			Json(await State.ListAsync(open ?? true, Math.Clamp(limit ?? int.MaxValue, 1, int.MaxValue), before)));
		api.MapGet("/workspaces/find", async (string reference) => Json(await State.FindAsync(reference)));
		api.MapPost("/workspaces/get", async (HttpRequest request) => Json(await State.GetAsync((await BodyAsync<Api.WorkspacesRequest>(request)).Ids)));
		api.MapGet("/workspaces/stale", async (long seq) =>
		{
			await IndexAsync();
			return Json(await State.StaleAsync(seq, RepoPolicy.FileName));
		});

		// Objects.
		api.MapPost("/objects/have", async (HttpRequest request) => Json(new Api.HaveReply(await HaveAsync((await BodyAsync<Api.IdsRequest>(request)).Ids))));
		api.MapGet("/objects/{id}/stat", async (string id) =>
			TryId(id, out var objectId) && await StatObjectAsync(objectId) is { } stat ? Json(new Api.StatReply(stat.Length)) : Error(404, $"No object {id}."));

		// Trees (directories) live in the tree store; a path, a subtree or a set of trees comes back in one call.
		api.MapPost("/trees/have", async (HttpRequest request) => Json(new Api.HaveReply(await Repo.Objects.TreeStore.HasAsync((await BodyAsync<Api.IdsRequest>(request)).Ids))));
		api.MapPost("/trees/fetch", async (HttpRequest request) =>
			TreeFrames(await Repo.Objects.TreeStore.GetManyAsync((await BodyAsync<Api.IdsRequest>(request)).Ids.Take(5000).ToList(), request.HttpContext.RequestAborted)));
		api.MapPost("/trees/subtree", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.SubtreeRequest>(request);
			return TreeFrames(await Repo.Objects.TreeStore.GetSubtreesAsync(body.Root, Math.Max(0, body.Depth), Math.Clamp(body.Limit, 1, ObjectAccess.SubtreeLimit), request.HttpContext.RequestAborted));
		});
		api.MapPost("/trees/path", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.TreePathRequest>(request);
			return TreeFrames(await Repo.Objects.TreeStore.GetPathAsync(body.Root, body.Names.Take(1000).ToList(), request.HttpContext.RequestAborted));
		});
		api.MapPost("/trees/put", async (HttpRequest request) =>
		{
			var trees = new List<(ObjectId, Tree)>();
			foreach (var (id, bytes) in Api.Frames.Read(await ReadBodyAsync(request)))
			{
				if (ObjectId.Of(bytes.Span) != id)
				{
					return Error(StatusCodes.Status400BadRequest, $"Tree {id.Short}: the bytes do not hash to its id.");
				}

				try
				{
					trees.Add((id, ObjectCodec.Decode<Tree>(bytes)));
				}
				catch (InvalidDataException ex)
				{
					return Error(StatusCodes.Status400BadRequest, $"Refused a malformed tree {id.Short}: {ex.Message}");
				}
			}

			await Repo.Objects.TreeStore.PutManyAsync(trees, request.HttpContext.RequestAborted);
			ObjectsStored.Add(trees.Count);
			return Results.NoContent();
		});
		api.MapGet("/objects/{id}", ReadObjectAsync);
		api.MapPut("/objects/{id}", PutObjectAsync);
		// Many small objects in one reply; those missing or large enough for a direct URL are left out.
		api.MapPost("/objects/fetch", async (HttpRequest request) =>
		{
			var body = await BodyAsync<Api.IdsRequest>(request);
			var ids = body.Ids.Distinct().Take(5000).ToList();
			var found = new ConcurrentBag<(ObjectId, ReadOnlyMemory<byte>)>();
			var considered = 0;
			long held = 0;

			// A group of ids at a time, until the reply's budget is spent: the rest is left out and marked, and the
			// client asks again. One reply never holds more than the budget plus one group of small objects.
			foreach (var group in ids.Chunk(FetchGroup))
			{
				if (Interlocked.Read(ref held) >= options.FetchBudget)
				{
					break;
				}

				await Parallel.ForEachAsync(group, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = request.HttpContext.RequestAborted },
					async (id, ct) =>
					{
						if (await Blobs.StatAsync(id, ct) is { Length: < ServerConnection.DirectTransferThreshold } && await Blobs.ReadAsync(id, null, ct) is { } bytes)
						{
							found.Add((id, bytes));
							Interlocked.Add(ref held, bytes.Length);
						}
					});
				var lacking = group.Except(found.Select(item => item.Item1)).ToList();
				if (lacking.Count > 0)
				{
					// A tree asked for as an object (an older client): its canonical encoding, from the tree store.
					foreach (var (id, tree) in await Repo.Objects.TreeStore.GetManyAsync(lacking, request.HttpContext.RequestAborted))
					{
						var encoded = ObjectCodec.Encode(tree);
						found.Add((id, encoded));
						Interlocked.Add(ref held, encoded.Length);
					}
				}

				considered += group.Length;
			}

			if (considered < ids.Count)
			{
				request.HttpContext.Response.Headers[Api.TruncatedHeader] = (ids.Count - considered).ToString();
			}

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

			var trees = objects.Where(item => ObjectCodec.TypeOf(item.Bytes) == "tree").ToList();
			if (trees.Count > 0)
			{
				await Repo.Objects.TreeStore.PutManyAsync(trees.Select(item => (item.Id, ObjectCodec.Decode<Tree>(item.Bytes))).ToList(), request.HttpContext.RequestAborted);
			}

			// One announcement and one confirmation in the catalog for the whole batch; the blobs are written in parallel.
			await Catalog.PutManyAsync(objects.Except(trees).ToList(), request.HttpContext.RequestAborted);
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
			Landings.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome), new KeyValuePair<string, object?>("reconciled", result.Reconciled));
			return Json(result);
		});

		// The live conflict overview: every open workspace reconciled with mainline, their overlaps and the hot paths.
		api.MapGet("/conflicts", async () => Json(await Landing.Reconciler.OverviewAsync()));

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
			await State.ConfirmAsync([(objectId, bytes.Length)]);
			ObjectsStored.Add(1);
			return Results.NoContent();
		});
	}

	private async Task IndexAsync() => await Repo.EnsureIndexedAsync(await Repo.GetMainAsync());

	/// <summary>Ids of a batched fetch looked at together; the reply's budget is checked between groups.</summary>
	private const int FetchGroup = 64;

	/// <summary>A request body in memory, up to the put limit; a larger one is refused before it is read.</summary>
	private async Task<byte[]> ReadBodyAsync(HttpRequest request)
	{
		if (request.ContentLength > options.PutLimit)
		{
			throw new BadHttpRequestException(
				$"The request body is {request.ContentLength} bytes; this server reads at most {options.PutLimit} (-put-limit). Send smaller batches, and large chunks on their upload URLs.",
				StatusCodes.Status413PayloadTooLarge);
		}

		using var buffer = new MemoryStream();
		await request.Body.CopyToAsync(buffer, request.HttpContext.RequestAborted);
		return buffer.ToArray();
	}

	/// <summary>Which objects are stored: blobs, or (for an id that is no blob) trees.</summary>
	private async Task<IReadOnlyList<bool>> HaveAsync(IReadOnlyList<ObjectId> ids)
	{
		var have = (await Blobs.HasAsync(ids)).ToList();
		var missing = ids.Where((_, index) => !have[index]).ToList();
		if (missing.Count > 0)
		{
			var trees = await Repo.Objects.TreeStore.HasAsync(missing);
			var next = 0;
			for (var index = 0; index < have.Count; index++)
			{
				if (!have[index])
				{
					have[index] = trees[next++];
				}
			}
		}

		return have;
	}

	/// <summary>A blob's size, or the size of a tree's canonical encoding.</summary>
	private async Task<ObjectStat?> StatObjectAsync(ObjectId id, CancellationToken ct = default)
	{
		if (await Blobs.StatAsync(id, ct) is { } stat)
		{
			return stat;
		}

		return (await Repo.Objects.TreeStore.GetManyAsync([id], ct)).TryGetValue(id, out var tree) ? new ObjectStat(id, ObjectCodec.Encode(tree).Length) : null;
	}

	private IResult TreeFrames(IReadOnlyDictionary<ObjectId, Tree> trees)
	{
		var reply = Api.Frames.Write(trees.Select(pair => (pair.Key, (ReadOnlyMemory<byte>)ObjectCodec.Encode(pair.Value))));
		BytesServed.Add(reply.Length);
		return Results.Bytes(reply, "application/octet-stream");
	}

	/// <summary>Serves an object: small ones and structural objects directly, large chunks by redirect to a signed URL.</summary>
	private async Task<IResult> ReadObjectAsync(string id, HttpRequest request)
	{
		if (!TryId(id, out var objectId))
		{
			return Error(404, $"No object {id}.");
		}

		// One stat tells a blob from a tree and a small blob from one to redirect; a blob is not asked about twice.
		if (await Blobs.StatAsync(objectId) is not { } stat)
		{
			if (!(await Repo.Objects.TreeStore.GetManyAsync([objectId])).TryGetValue(objectId, out var stored))
			{
				return Error(404, $"No object {id}.");
			}

			var encoded = ObjectCodec.Encode(stored);
			BytesServed.Add(encoded.Length);
			return Results.Bytes(encoded, "application/octet-stream");
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
				if (ObjectCodec.Decode(bytes) is Tree tree)
				{
					await Repo.Objects.TreeStore.PutManyAsync([(objectId, tree)]);
					ObjectsStored.Add(1);
					return Results.NoContent();
				}
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

		await stopping.CancelAsync();
		if (backfill is not null)
		{
			await backfill;
		}

		await Reconciling.DisposeAsync();
		await Trees.DisposeAsync();

		if (State is IAsyncDisposable state)
		{
			await state.DisposeAsync();
		}
	}
}
