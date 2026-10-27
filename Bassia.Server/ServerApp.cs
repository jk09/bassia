namespace Bassia.Server;

using System.Diagnostics;
using System.Text.Json;
using Bassia.Server.Api;
using Bassia.Server.Auth;
using Bassia.Storage;
using Bassia.Storage.Cloud;
using Bassia.Storage.Remote;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

/// <summary>
/// The server as an ASP.NET Core app: health endpoints, the API (<c>/api/v1</c>, every request but <c>/info</c> and the
/// login exchanges authenticated, every write replayed by its idempotency key), the git gateway (<c>/git</c>), and the
/// dashboard for every signed-in user (<c>/</c>, with <c>/login</c> and the live changes at <c>/events</c>).
/// </summary>
internal static class ServerApp
{
	public const string CallerItem = "bassia.caller";

	public static WebApplication Build(ServerState state, string? url = null)
	{
		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls(url ?? state.Config.Url);
		builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
		if (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 })
		{
			// The OTLP exporter reads its endpoint, protocol and interval from the OTEL_* variables.
			builder.Services.AddOpenTelemetry()
				.ConfigureResource(resource => resource.AddService(Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "bassia-server"))
				.WithMetrics(metrics => metrics.AddMeter(ServerMetrics.MeterName, "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel").AddOtlpExporter());
		}

		var app = builder.Build();

		app.Use((context, next) => LogAsync(state, context, next));
		app.Use(ErrorsAsync);
		app.Use((context, next) => AuthenticateAsync(state, context, next));
		app.Use((context, next) => IdempotentAsync(state, context, next));

		app.MapGet("/healthz", () => Results.Text("ok\n"));
		app.MapGet("/readyz", async () =>
		{
			try
			{
				await state.Database.QueryAsync("SELECT 1;", reader => reader.GetValue(0));
				await state.Meta.GitAsync();
				return Results.Text("ready\n");
			}
			catch (Exception ex)
			{
				return Results.Text($"not ready: {ex.Message}\n", statusCode: StatusCodes.Status503ServiceUnavailable);
			}
		});

		var api = app.MapGroup(Bassia.Storage.Remote.Api.Prefix);
		IdentityEndpoints.Map(api, state);
		MetaEndpoints.Map(api, state);
		StateEndpoints.Map(api, state);
		ComponentEndpoints.Map(api, state);
		AccessEndpoints.Map(api, state);

		new GitGateway(state.GitHost, new ServerGatewayPolicy(state)).Map(app);
		Web.ServerDashboard.Map(app, state);

		// Every instance follows the change table for the browsers connected to it.
		app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => state.Changes.RunAsync(app.Lifetime.ApplicationStopping)));
		return app;
	}

	public static Caller CallerOf(HttpContext context) => (Caller)context.Items[CallerItem]!;

	public static string RemoteOf(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

	public static IResult Error(int status, string message) => Results.Json(new ErrorBody(message), Bassia.Storage.Remote.Api.Json, statusCode: status);

	public static IResult Json<T>(T value) => Results.Json(value, Bassia.Storage.Remote.Api.Json);

	/// <summary>Whether the path is an API path anyone may call: the server's description and the login exchanges.</summary>
	private static bool IsOpen(PathString path) =>
		path.StartsWithSegments(Bassia.Storage.Remote.Api.Prefix + "/info") || path.StartsWithSegments(Bassia.Storage.Remote.Api.Prefix + "/auth");

	private static async Task AuthenticateAsync(ServerState state, HttpContext context, RequestDelegate next)
	{
		if (context.Request.Path.StartsWithSegments(Bassia.Storage.Remote.Api.Prefix) && !IsOpen(context.Request.Path))
		{
			if (await state.Authenticator.AuthenticateAsync(context.Request) is not { } caller)
			{
				var reason = Authenticator.TokenOf(context.Request) is { } token ? await ReasonAsync(state, token) : "No credential: log in with 'bassia login'.";
				await Error(StatusCodes.Status401Unauthorized, reason).ExecuteAsync(context);
				return;
			}

			context.Items[CallerItem] = caller;
		}

		await next(context);
	}

	private static async Task<string> ReasonAsync(ServerState state, string token)
	{
		try
		{
			await state.Authenticator.AuthenticateAsync(token);
			return "Not authenticated.";
		}
		catch (AuthException ex)
		{
			return ex.Message;
		}
	}

	/// <summary>An expected failure is an error reply with its message, not a broken connection.</summary>
	private static async Task ErrorsAsync(HttpContext context, RequestDelegate next)
	{
		try
		{
			await next(context);
		}
		catch (Exception ex) when (!context.Response.HasStarted && Status(ex) is { } status)
		{
			context.Response.Clear();
			if (ex is BranchesMovedException moved)
			{
				await Results.Json(new LandRefused(moved.Message, moved.Moved), Bassia.Storage.Remote.Api.Json, statusCode: status).ExecuteAsync(context);
				return;
			}

			await Error(status, ex.Message).ExecuteAsync(context);
		}
		catch (Exception ex) when (!context.Response.HasStarted && ex is not OperationCanceledException)
		{
			Console.Error.WriteLine(JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow.ToString("O"), path = context.Request.Path.Value, error = ex.ToString() }));
			context.Response.Clear();
			await Error(StatusCodes.Status500InternalServerError, $"The server failed: {ex.GetType().Name}: {ex.Message}").ExecuteAsync(context);
		}
	}

	private static int? Status(Exception ex) => ex switch
	{
		ForbiddenException => StatusCodes.Status403Forbidden,
		NotFoundException => StatusCodes.Status404NotFound,
		AuthException => StatusCodes.Status401Unauthorized,
		StorageException => StatusCodes.Status409Conflict,
		MonorepoException or ArgumentException or ServerConfigException or JsonException or BadHttpRequestException => StatusCodes.Status400BadRequest,
		_ => null
	};

	/// <summary>
	/// A write with an idempotency key is done once: its reply is kept, and a retry with the same key (the client's,
	/// after losing the reply) gets the kept reply without the write happening again.
	/// </summary>
	private static async Task IdempotentAsync(ServerState state, HttpContext context, RequestDelegate next)
	{
		if (HttpMethods.IsGet(context.Request.Method) || context.Request.Headers[Bassia.Storage.Remote.Api.IdempotencyHeader].FirstOrDefault() is not { Length: > 0 } key
			|| context.Items[CallerItem] is not Caller caller)
		{
			await next(context);
			return;
		}

		if (await state.Access.ReplyOfAsync(key, caller.Id) is { } kept)
		{
			context.Response.StatusCode = kept.Status;
			context.Response.ContentType = "application/json";
			await context.Response.WriteAsync(kept.Body);
			return;
		}

		var original = context.Response.Body;
		using var captured = new MemoryStream();
		context.Response.Body = captured;
		try
		{
			await next(context);
		}
		finally
		{
			context.Response.Body = original;
		}

		captured.Position = 0;
		if (context.Response.StatusCode < 500 && context.Response.StatusCode != StatusCodes.Status409Conflict)
		{
			await state.Access.RememberAsync(key, caller.Id, context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(captured.ToArray()));
		}

		await captured.CopyToAsync(original);
	}

	/// <summary>
	/// One JSON line per request: when, what, how it ended, how long it took, and for whom; and the request's metrics -
	/// its duration by route, and for the git gateway the bytes that went in and out.
	/// </summary>
	private static async Task LogAsync(ServerState state, HttpContext context, RequestDelegate next)
	{
		var clock = Stopwatch.StartNew();
		var git = context.Request.Path.StartsWithSegments("/git");
		CountingStream? received = null, sent = null;
		Stream? body = null;
		if (git)
		{
			context.Request.Body = received = new CountingStream(context.Request.Body);
			body = context.Response.Body;
			context.Response.Body = sent = new CountingStream(body);
		}

		try
		{
			await next(context);
		}
		finally
		{
			var status = context.Response.StatusCode;
			ServerMetrics.RequestDuration.Record(clock.Elapsed.TotalSeconds, ServerMetrics.Tag("method", context.Request.Method),
				ServerMetrics.Tag("route", ServerMetrics.RouteOf(context)), ServerMetrics.Tag("status", status));
			if (git)
			{
				context.Response.Body = body!;
				var service = ServerMetrics.GitServiceOf(context.Request);
				ServerMetrics.GitRequests.Add(1, ServerMetrics.Tag("service", service), ServerMetrics.Tag("status", status));
				ServerMetrics.GitBytes.Add(received!.Count, ServerMetrics.Tag("service", service), ServerMetrics.Tag("direction", "in"));
				ServerMetrics.GitBytes.Add(sent!.Count, ServerMetrics.Tag("service", service), ServerMetrics.Tag("direction", "out"));
			}

			if (state.Config.RequestLog)
			{
				Console.Out.WriteLine(JsonSerializer.Serialize(new
				{
					time = DateTimeOffset.UtcNow.ToString("O"),
					method = context.Request.Method,
					path = context.Request.Path.Value,
					status = context.Response.StatusCode,
					ms = Math.Round(clock.Elapsed.TotalMilliseconds, 1),
					user = (context.Items[CallerItem] as Caller)?.Id,
					remote = RemoteOf(context)
				}));
			}
		}
	}
}
