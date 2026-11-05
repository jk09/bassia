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
