namespace Bassia.Storage;

using Bassia.Core;

/// <summary>
/// Who the current process acts for: the user its locks, runs, tags and commits are attributed to. <see cref="LocalIdentity"/>
/// takes the user's word for it (<c>BASSIA_USER</c>, <c>user.id</c>, the git email); a server knows it from the caller's
/// credential.
/// </summary>
internal interface IIdentity
{
	/// <summary>
	/// The current user among <paramref name="users"/> and how that is known; null when nothing names one. A name that is
	/// not a registered user is an error.
	/// </summary>
	Task<(UserDefinition? User, string Source)> CurrentAsync(UserDirectory users);
}
