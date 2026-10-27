namespace Bassia.Server.Api;

using Bassia.Core;
using Bassia.Server.Auth;
using Bassia.Storage;
using Bassia.Storage.Remote;

/// <summary>
/// Component histories the server makes and removes (the registry is changed separately, through <c>/meta/commit</c>,
/// as <see cref="MonorepoGraph"/> does), and landing: moving components' default branches to integrated commits through
/// the merge queue - one landing per component at a time, components in name order so two landings never wait for
/// each other, and every branch of a landing moved together or none.
/// </summary>
internal static class ComponentEndpoints
{
	/// <summary>How long a landing may hold a component's slot; a crashed landing frees it after that.</summary>
	private static readonly TimeSpan LandingLease = TimeSpan.FromMinutes(2);

	/// <summary>How long a landing waits for the slots it needs before it is refused.</summary>
	private static readonly TimeSpan LandingWait = TimeSpan.FromMinutes(2);

	public static void Map(RouteGroupBuilder api, ServerState state)
	{
		api.MapPost("/components/{name}", async (string name, ComponentRequest request, HttpContext context) =>
		{
			var caller = ServerApp.CallerOf(context);
			ComponentRegistry.ValidateName(name);
			await state.RequireGlobalAsync(caller, Permission.Admin);
			if (state.Storage.Exists(name))
			{
				throw new StorageException($"Component '{name}' already has a history on the server.");
			}

			var store = request.Action switch
			{
				"create" => await state.Storage.CreateAsync(name, request.DefaultBranch ?? "main", request.Url ?? "", request.Message ?? $"Create component '{name}'"),
				"import" => await state.Storage.ImportAsync(name, Remote(request.Url)),
				"fork" => await ForkAsync(state, caller, name, request),
				_ => throw new ArgumentException($"'{request.Action}' is not create, import or fork.")
			};
			await state.Access.AuditAsync(caller.Id, $"component.{request.Action}", name, request.Url ?? request.Source ?? "", ServerApp.RemoteOf(context));
			return ServerApp.Json(new NameResponse(store.Name));
		});
		api.MapDelete("/components/{name}", async (string name, HttpContext context) =>
		{
			var caller = ServerApp.CallerOf(context);
			ComponentRegistry.ValidateName(name);
			if ((await state.ComponentsAsync()).Any(component => component.Name == name))
			{
				await state.RequireAsync(caller, [name], Permission.Admin);
			}
			else
			{
				await state.RequireGlobalAsync(caller, Permission.Admin);
			}

			await state.Storage.DeleteAsync(name);
			await state.Access.AuditAsync(caller.Id, "component.delete", name, "", ServerApp.RemoteOf(context));
			return Results.NoContent();
		});
		api.MapPost("/land", async (LandRequest request, HttpContext context) =>
		{
			await LandAsync(state, request, context);
			return Results.NoContent();
		});
		api.MapGet("/land", async (HttpContext context) =>
		{
			var readable = await state.ReadableAsync(ServerApp.CallerOf(context));
			return ServerApp.Json((await state.Access.LandingAsync()).Where(slot => readable.Contains(slot.Component))
				.Select(slot => new { component = slot.Component, holder = slot.Holder }).ToList());
		});
	}

	/// <summary>A URL the server may clone from: another server's, never a path on the server's own disk.</summary>
	private static string Remote(string? url) =>
		url is not null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("git@", StringComparison.Ordinal))
			? url
			: throw new ArgumentException($"'{url}' is not a URL the server imports from (https://, http://, ssh:// or git@host:path).");

	private static async Task<IComponentStore> ForkAsync(ServerState state, Caller caller, string name, ComponentRequest request)
	{
		var source = request.Source ?? throw new ArgumentException("A fork needs its source component.");
		await state.RequireAsync(caller, [source], Permission.Read);
		return await state.Storage.ForkAsync(state.Storage.Open(source), name, request.Branch ?? "main",
			request.Commit ?? throw new ArgumentException("A fork needs the commit it forks at."), request.Tags ?? [], request.Url ?? "");
	}

	private static async Task LandAsync(ServerState state, LandRequest request, HttpContext context)
	{
		var caller = ServerApp.CallerOf(context);
		var components = request.Moves.Select(move => move.Component).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
		await state.RequireAsync(caller, components, Permission.Integrate);

		var holder = $"{caller.Id}:{Guid.NewGuid():N}";
		var taken = new List<string>();
		var queued = System.Diagnostics.Stopwatch.StartNew();
		ServerMetrics.MergeQueueDepth.Add(1, ServerMetrics.Tag("state", "waiting"));
		var landing = false;
		try
		{
			foreach (var component in components)
			{
				var waited = System.Diagnostics.Stopwatch.StartNew();
				while (!await state.Access.TakeLandingAsync(component, holder, LandingLease))
				{
					if (waited.Elapsed > LandingWait)
					{
						throw new StorageException($"Component '{component}' is being landed on by another integration; nothing was landed, try again.");
					}

					await Task.Delay(100, context.RequestAborted);
				}

				taken.Add(component);
			}

			ServerMetrics.MergeQueueWait.Record(queued.Elapsed.TotalSeconds);
			ServerMetrics.MergeQueueDepth.Add(-1, ServerMetrics.Tag("state", "waiting"));
			ServerMetrics.MergeQueueDepth.Add(1, ServerMetrics.Tag("state", "landing"));
			landing = true;
			await state.Storage.MoveBranchesAsync(request.Moves);
			await state.Changes.AddAsync("land", string.Join(",", components), components);
			await state.Access.AuditAsync(caller.Id, "land", string.Join(",", components),
				string.Join(", ", request.Moves.Select(move => $"{move.Component}/{move.Branch} {move.Expected[..7]}..{move.Commit[..7]}")), ServerApp.RemoteOf(context));
		}
		finally
		{
			ServerMetrics.MergeQueueDepth.Add(-1, ServerMetrics.Tag("state", landing ? "landing" : "waiting"));
			foreach (var component in taken)
			{
				await state.Access.ReleaseLandingAsync(component, holder);
			}
		}
	}
}
