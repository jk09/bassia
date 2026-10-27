namespace Bassia.Server.Api;

using Bassia.Server.Auth;
using Bassia.Storage.Remote;

/// <summary>Grants, groups, identity links and the audit log: server admins only.</summary>
internal static class AccessEndpoints
{
	public static void Map(RouteGroupBuilder api, ServerState state)
	{
		var access = api.MapGroup("/access").AddEndpointFilter(async (context, next) =>
		{
			await state.RequireGlobalAsync(ServerApp.CallerOf(context.HttpContext), Permission.Admin);
			return await next(context);
		});

		access.MapGet("/grants", async () => ServerApp.Json(await state.Access.GrantsAsync()));
		access.MapPost("/grants", async (Grant grant, HttpContext context) =>
		{
			Permissions.Parse(grant.Permission);
			if (grant.Principal != "*" && !grant.Principal.StartsWith("user:", StringComparison.Ordinal) && !grant.Principal.StartsWith("group:", StringComparison.Ordinal))
			{
				throw new ArgumentException($"'{grant.Principal}' is no principal: user:<id>, group:<name> or *.");
			}

			await state.Access.GrantAsync(grant);
			await AuditAsync(state, context, "access.grant", grant.Component, $"{grant.Principal} {grant.Permission}");
			return Results.NoContent();
		});
		access.MapDelete("/grants", async (string principal, string component, HttpContext context) =>
		{
			var removed = await state.Access.RevokeAsync(principal, component);
			await AuditAsync(state, context, "access.revoke", component, principal);
			return ServerApp.Json(new { removed = removed > 0 });
		});

		access.MapGet("/groups", async () => ServerApp.Json(await state.Access.MembersAsync()));
		access.MapPost("/groups", async (GroupMember member, HttpContext context) =>
		{
			await state.Access.AddMemberAsync(member);
			await AuditAsync(state, context, "access.group-add", member.Group, member.User);
			return Results.NoContent();
		});
		access.MapDelete("/groups", async (string group, string user, HttpContext context) =>
		{
			var removed = await state.Access.RemoveMemberAsync(new GroupMember(group, user));
			await AuditAsync(state, context, "access.group-remove", group, user);
			return ServerApp.Json(new { removed = removed > 0 });
		});

		access.MapGet("/identities", async () => ServerApp.Json(await state.Access.LinksAsync()));
		access.MapPost("/identities", async (IdentityLink link, HttpContext context) =>
		{
			if ((await state.Meta.Store.LoadUsersAsync()).All(user => user.Id != link.User))
			{
				throw new ArgumentException($"'{link.User}' is not a registered user.");
			}

			await state.Access.LinkAsync(link);
			await AuditAsync(state, context, "access.link", link.User, $"{link.Provider} {link.Subject}");
			return Results.NoContent();
		});
		access.MapDelete("/identities", async (string provider, string subject, HttpContext context) =>
		{
			var removed = await state.Access.UnlinkAsync(provider, subject);
			await AuditAsync(state, context, "access.unlink", subject, provider);
			return ServerApp.Json(new { removed = removed > 0 });
		});

		access.MapGet("/audit", async (int? limit, long? before) => ServerApp.Json(await state.Access.AuditLogAsync(Math.Clamp(limit ?? 100, 1, 1000), before)));
	}

	private static Task AuditAsync(ServerState state, HttpContext context, string action, string target, string detail) =>
		state.Access.AuditAsync(ServerApp.CallerOf(context).Id, action, target, detail, ServerApp.RemoteOf(context));
}
