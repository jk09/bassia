namespace Bassia.Server.Auth;

using Bassia.Server.Data;

/// <summary>What a caller may do with a component; each level includes the ones before it.</summary>
internal enum Permission
{
	None,

	/// <summary>See it: its registration, history, runs and locks; fetch it.</summary>
	Read,

	/// <summary>Start runs that change it; push run branches and run tags.</summary>
	Run,

	/// <summary>Land on its default branch; push its other tags.</summary>
	Integrate,

	/// <summary>Change its registration; remove it.</summary>
	Admin
}

/// <summary>
/// The permissions of callers on components: granted to users (<c>user:&lt;id&gt;</c>), groups (<c>group:&lt;name&gt;</c>)
/// and everyone (<c>*</c>), on a component or every component (<c>*</c>); owners (<c>owners</c> in the registry, by user
/// id or <c>group:&lt;name&gt;</c>) hold <see cref="Permission.Integrate"/>; server admins hold everything; and
/// <c>default_permission</c> is what every user holds. A worker token holds at most <see cref="Permission.Run"/>, and only
/// on the components it names.
/// </summary>
internal sealed class Permissions(AccessStore access, ServerConfig config)
{
	public static Permission Parse(string text) => text switch
	{
		"read" => Permission.Read,
		"run" => Permission.Run,
		"integrate" => Permission.Integrate,
		"admin" => Permission.Admin,
		_ => throw new ArgumentException($"'{text}' is no permission: read, run, integrate or admin.")
	};

	public static string Name(Permission permission) => permission.ToString().ToLowerInvariant();

	public static bool Matches(string principal, string user, IReadOnlyList<string> groups) =>
		principal == "*" || principal == $"user:{user}" || (principal.StartsWith("group:", StringComparison.Ordinal) && groups.Contains(principal["group:".Length..], StringComparer.Ordinal));

	/// <summary>The caller's permission on each of <paramref name="components"/>.</summary>
	public async Task<IReadOnlyDictionary<string, Permission>> OfAsync(Caller caller, IReadOnlyList<ComponentDefinition> components)
	{
		var grants = caller.UserIsAdmin ? [] : await access.GrantsAsync();
		var levels = new Dictionary<string, Permission>(StringComparer.Ordinal);
		foreach (var component in components)
		{
			var level = caller.UserIsAdmin ? Permission.Admin : config.DefaultPermission == "read" ? Permission.Read : Permission.None;
			foreach (var grant in grants.Where(grant => (grant.Component == "*" || grant.Component == component.Name) && Matches(grant.Principal, caller.Id, caller.Groups)))
			{
				level = Max(level, Parse(grant.Permission));
			}

			if (component.Owners.Any(owner => owner == caller.Id || (owner.StartsWith("group:", StringComparison.Ordinal) ? caller.Groups.Contains(owner[6..]) : caller.Groups.Contains(owner))))
			{
				level = Max(level, Permission.Integrate);
			}

			if (caller.Scope is { } scope)
			{
				level = scope.Contains(component.Name, StringComparer.Ordinal) ? Min(level, Permission.Run) : Permission.None;
			}

			levels[component.Name] = level;
		}

		return levels;
	}

	/// <summary>The caller's permission on every component (<c>*</c>): what creating or importing a component needs.</summary>
	public async Task<Permission> GlobalAsync(Caller caller)
	{
		if (caller.Admin)
		{
			return Permission.Admin;
		}

		if (caller.Scope is not null)
		{
			return Permission.None;
		}

		var level = config.DefaultPermission == "read" ? Permission.Read : Permission.None;
		foreach (var grant in (await access.GrantsAsync()).Where(grant => grant.Component == "*" && Matches(grant.Principal, caller.Id, caller.Groups)))
		{
			level = Max(level, Parse(grant.Permission));
		}

		return level;
	}

	private static Permission Max(Permission first, Permission second) => first > second ? first : second;

	private static Permission Min(Permission first, Permission second) => first < second ? first : second;
}
