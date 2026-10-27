namespace Bassia.Server.Web;

using System.Text.Json;
using Bassia.Server.Auth;
using Bassia.Web;
using static Bassia.Web.Html;

/// <summary>
/// The dashboard on the server: the pages of <c>bassia web</c> at <c>/</c>, behind the sign-in (<see cref="WebSignIn"/>),
/// each request over the monorepo as the signed-in user may see it (<see cref="DashboardBackend"/>), and the live
/// changes pushed to the browser as server-sent events (<c>/events</c>).
/// </summary>
internal static class ServerDashboard
{
	/// <summary>How often a quiet event stream says it is still there (a load balancer closes idle connections).</summary>
	private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

	/// <summary>How often an event stream reads the viewer's permissions again: a permission taken away stops the changes it showed.</summary>
	private static readonly TimeSpan PermissionRefresh = TimeSpan.FromSeconds(30);

	/// <summary>How long a burst of changes is gathered into one message per kind.</summary>
	private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(200);

	public static void Map(WebApplication app, ServerState state)
	{
		var dashboard = new Dashboard(context => OpenAsync(state, context));
		app.UseWhen(context => Dashboard.IsDashboardPath(context.Request.Path) || context.Request.Path.StartsWithSegments("/events"),
			branch => branch.Use(async (context, next) =>
			{
				if (Dashboard.IsAsset(context.Request.Path))
				{
					await next(context);
					return;
				}

				if (await WebSignIn.CallerAsync(state, context) is not { } caller)
				{
					await WebSignIn.Challenge(context).ExecuteAsync(context);
					return;
				}

				context.Items[ServerApp.CallerItem] = caller;
				await next(context);
			}));
		app.UseWhen(context => Dashboard.IsDashboardPath(context.Request.Path), branch => branch.Use(dashboard.ServeAsync));

		dashboard.Map(app);
		WebSignIn.Map(app, state);
		app.MapGet("/events", (HttpContext context) => EventsAsync(state, context, app.Lifetime.ApplicationStopping));
	}

	private static async Task<DashboardScope> OpenAsync(ServerState state, HttpContext context)
	{
		var caller = ServerApp.CallerOf(context);
		var monorepo = Monorepo.Load(await DashboardBackend.OpenAsync(state, caller));
		var account = $"<span class=\"who\">{E(caller.User.Name)}{(caller.Admin ? " · admin" : "")}<a href=\"/logout\">sign out</a></span>";
		return new DashboardScope(monorepo, WebSignIn.Label(state.Config), account, "/events");
	}

	/// <summary>
	/// The changes the viewer may see, as they happen: one <c>change</c> event per kind (<c>run</c>, <c>integration</c>,
	/// <c>job</c>, <c>log</c>, <c>lock</c>, <c>land</c>) of the changes gathered in a moment. A change is only the kind of
	/// thing that changed - the page reads what it shows through the same filter as everything else - and only one whose
	/// components the viewer may all read is sent.
	/// </summary>
	private static async Task EventsAsync(ServerState state, HttpContext context, CancellationToken stopping)
	{
		var caller = ServerApp.CallerOf(context);
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
		using var subscription = state.Changes.Subscribe();
		context.Response.ContentType = "text/event-stream";
		context.Response.Headers.CacheControl = "no-cache";
		context.Response.Headers["X-Accel-Buffering"] = "no";
		await context.Response.WriteAsync("retry: 3000\n: connected\n\n", cancel.Token);
		await context.Response.Body.FlushAsync(cancel.Token);

		var readable = await state.ReadableAsync(caller);
		var read = DateTimeOffset.UtcNow;
		ServerMetrics.DashboardStreams.Add(1);
		try
		{
			while (!cancel.IsCancellationRequested)
			{
				using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
				wait.CancelAfter(KeepAlive);
				bool more;
				try
				{
					more = await subscription.Reader.WaitToReadAsync(wait.Token);
				}
				catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
				{
					await context.Response.WriteAsync(": keep-alive\n\n", cancel.Token);
					await context.Response.Body.FlushAsync(cancel.Token);
					continue;
				}

				if (!more)
				{
					return;
				}

				await Task.Delay(Coalesce, cancel.Token);
				if (DateTimeOffset.UtcNow - read > PermissionRefresh)
				{
					readable = await state.ReadableAsync(caller);
					read = DateTimeOffset.UtcNow;
				}

				var kinds = new SortedSet<string>(StringComparer.Ordinal);
				while (subscription.Reader.TryRead(out var change))
				{
					if (change.Components.All(readable.Contains))
					{
						kinds.Add(change.Kind);
					}
				}

				foreach (var kind in kinds)
				{
					await context.Response.WriteAsync($"event: change\ndata: {JsonSerializer.Serialize(new { kind })}\n\n", cancel.Token);
				}

				await context.Response.Body.FlushAsync(cancel.Token);
			}
		}
		catch (OperationCanceledException)
		{
			// The browser went away, or the server is stopping.
		}
		finally
		{
			ServerMetrics.DashboardStreams.Add(-1);
		}
	}
}
