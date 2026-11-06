namespace Bassia.Studio;

/// <summary>What a Studio user may do: read, change workspaces and run agents, or also act on what others own.</summary>
public enum StudioRole
{
	Reader,
	Contributor,
	Admin
}

/// <summary>The person acting in Studio: recorded as owner and agent of what they change.</summary>
public sealed record StudioUser(string Name, StudioRole Role)
{
	public bool CanChange => Role >= StudioRole.Contributor;

	public bool IsAdmin => Role == StudioRole.Admin;
}

/// <summary>An action the user's role does not allow.</summary>
public sealed class StudioForbiddenException(string message) : Exception(message);

/// <summary>How a signed-in principal becomes a Studio user: its name, and its strongest Entra ID app role.</summary>
public static class StudioClaims
{
	/// <summary>The app role values (Entra ID app registration) and the roles they grant.</summary>
	public static readonly IReadOnlyDictionary<string, StudioRole> AppRoles = new Dictionary<string, StudioRole>(StringComparer.OrdinalIgnoreCase)
	{
		["Reader"] = StudioRole.Reader,
		["Contributor"] = StudioRole.Contributor,
		["Admin"] = StudioRole.Admin
	};

	/// <summary>
	/// The user a principal signs in as: named by its <c>preferred_username</c> (else name, else subject); its role is the
	/// strongest app role it holds, reader without one.
	/// </summary>
	public static StudioUser From(System.Security.Claims.ClaimsPrincipal principal)
	{
		string? Claim(params string[] types) => types.Select(type => principal.FindFirst(type)?.Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
		var name = Claim("preferred_username", "name", System.Security.Claims.ClaimTypes.Name, "sub", System.Security.Claims.ClaimTypes.NameIdentifier) ?? "anonymous";
		var roles = principal.FindAll("roles").Concat(principal.FindAll(System.Security.Claims.ClaimTypes.Role))
			.Select(claim => AppRoles.TryGetValue(claim.Value, out var role) ? role : StudioRole.Reader);
		return new StudioUser(name, principal.Identity?.IsAuthenticated == true ? roles.DefaultIfEmpty(StudioRole.Reader).Max() : StudioRole.Reader);
	}
}
