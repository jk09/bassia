namespace Bassia.Server.Api;

using Bassia.Core;
using Bassia.Server.Auth;
using Bassia.Storage;
using Bassia.Storage.Remote;

/// <summary>
/// The registry and the settings: changes go through Core's meta-repo checks (<see cref="MetaRepo"/>) exactly as on a
/// monorepo on disk; reads are what the caller may see. Clients read the registry from their clone's meta-repo (the
/// gateway's view of it) and change it here.
/// </summary>
internal static class MetaEndpoints
{
	public static void Map(RouteGroupBuilder api, ServerState state)
	{
		api.MapGet("/meta/components", async (HttpContext context) =>
		{
			var readable = await state.ReadableAsync(ServerApp.CallerOf(context));
			return ServerApp.Json((await state.ComponentsAsync()).Where(component => readable.Contains(component.Name)).ToList());
		});
		api.MapGet("/meta/users", async () => ServerApp.Json(await state.Meta.Store.LoadUsersAsync()));
		api.MapPost("/meta/commit", async (MetaCommitRequest request, HttpContext context) => ServerApp.Json(await CommitAsync(state, request, context)));
		api.MapPost("/config", async (ConfigChange change, HttpContext context) => ServerApp.Json(await ConfigAsync(state, change, context)));
	}

	/// <summary>What a change needs: a component's registration is its admins'; new components, rules and users are every component's admins'.</summary>
	private static async Task RequireAsync(ServerState state, Caller caller, RegistryChange change)
	{
		switch (change)
		{
			case RegistryChange.Add add:
				await state.RequireGlobalAsync(caller, Permission.Admin);
				await state.RequireAsync(caller, add.Component.References.Select(reference => reference.Name), Permission.Read);
				break;
			case RegistryChange.SetReferences set:
				await state.RequireAsync(caller, [set.Name], Permission.Admin);
				await state.RequireAsync(caller, set.References.Select(reference => reference.Name), Permission.Read);
				break;
			case RegistryChange.Remove or RegistryChange.SetTags or RegistryChange.SetVisibility or RegistryChange.SetOwners or RegistryChange.SetExports:
				await state.RequireAsync(caller, [Named(change)], Permission.Admin);
				break;
			default:
				await state.RequireGlobalAsync(caller, Permission.Admin);
				break;
		}
	}

	private static string Named(RegistryChange change) => change switch
	{
		RegistryChange.Remove remove => remove.Name,
		RegistryChange.SetTags set => set.Name,
		RegistryChange.SetVisibility set => set.Name,
		RegistryChange.SetOwners set => set.Name,
		RegistryChange.SetExports set => set.Name,
		_ => ""
	};

	private static async Task<RevisionResponse> CommitAsync(ServerState state, MetaCommitRequest request, HttpContext context)
	{
		var caller = ServerApp.CallerOf(context);
		foreach (var change in request.Changes)
		{
			await RequireAsync(state, caller, change);
		}

		var meta = await MetaRepo.OpenAsync(state.Meta.Store);
		var revision = await meta.CommitAsync(request.Changes, request.Message);
		await state.Access.AuditAsync(caller.Id, "meta.commit", revision ?? "", request.Message, ServerApp.RemoteOf(context));
		return new RevisionResponse(revision);
	}

	private static async Task<RevisionResponse> ConfigAsync(ServerState state, ConfigChange change, HttpContext context)
	{
		var caller = ServerApp.CallerOf(context);
		await state.RequireGlobalAsync(caller, Permission.Admin);
		var key = ConfigFile.Find(change.Key);
		if (change.Value is { } value)
		{
			var revision = await state.Meta.SetConfigAsync(key, ConfigFile.Normalize(key, value));
			await state.Access.AuditAsync(caller.Id, "config.set", key.Key, value, ServerApp.RemoteOf(context));
			return new RevisionResponse(revision);
		}

		var (removed, unset) = await state.Meta.UnsetConfigAsync(key);
		await state.Access.AuditAsync(caller.Id, "config.unset", key.Key, "", ServerApp.RemoteOf(context));
		return new RevisionResponse(unset, removed);
	}
}
