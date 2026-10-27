namespace Bassia.Storage.Remote;

using System.Text.Json;
using System.Text.Json.Serialization;
using Bassia.Locks;

/// <summary>
/// The Bassia server's API (<c>/api/v1</c>): the messages the server (<c>Bassia.Server</c>) and the remote backend
/// (<see cref="RemoteBackend"/>) exchange, as JSON. Records, lock entries and run records travel in their TOML form, as
/// the local stores keep them.
/// </summary>
internal static class Api
{
	public const string Prefix = "/api/v1";

	/// <summary>The header a write carries so a retry is applied once.</summary>
	public const string IdempotencyHeader = "Idempotency-Key";

	public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	/// <summary>The components a lock entry is about: those of its holds, waits, conflicts, catch-ups and results.</summary>
	public static IReadOnlySet<string> ComponentsOf(LockEntry entry) =>
		entry.Holds.Select(hold => hold.Key.Component)
			.Concat(entry.Waits.Select(wait => wait.Key.Component))
			.Concat(entry.Conflicts.Select(conflict => conflict.Component))
			.Concat(entry.CatchUps.Select(catchUp => catchUp.Component))
			.Concat(entry.Results.Select(result => result.Component))
			.ToHashSet(StringComparer.Ordinal);
}

/// <summary><c>GET /api/v1/info</c>, unauthenticated: that this is a Bassia server, its API version and how to log in.</summary>
internal sealed record ServerInfo(string Name, int Api, IReadOnlyList<AuthProvider> Providers, string? Organization = null);

/// <summary>
/// A way to log in: <c>oidc</c> (an OpenID Connect issuer and the public client the CLI uses with it) or <c>github</c>
/// (GitHub's device flow with the server's OAuth app).
/// </summary>
internal sealed record AuthProvider(string Kind, string Name, string? Issuer = null, string? ClientId = null, string? Scopes = null, string? DeviceUrl = null);

/// <summary><c>GET /api/v1/me</c>: the registered user the caller is, their groups, whether they are an admin, and a worker token's components.</summary>
internal sealed record Me(string User, IReadOnlyList<string> Groups, bool Admin, IReadOnlyList<string>? Scope = null);

internal sealed record ErrorBody(string Error);

/// <summary>A server token (<c>/auth/github</c>, <c>/auth/refresh</c>, <c>/tokens/worker</c>).</summary>
internal sealed record TokenResponse(string AccessToken, string? RefreshToken, long ExpiresIn);

internal sealed record GitHubLogin(string AccessToken);

internal sealed record RefreshRequest(string RefreshToken);

internal sealed record WorkerTokenRequest(IReadOnlyList<string> Components, int Minutes);

internal sealed record MetaCommitRequest(IReadOnlyList<RegistryChange> Changes, string Message);

internal sealed record RevisionResponse(string? Revision, bool Removed = false);

/// <summary>A setting of the monorepo layer to write (<see cref="Value"/>) or remove (null).</summary>
internal sealed record ConfigChange(string Key, string? Value);

internal sealed record RecordWrite(string RefBase, int Lineage, string FileName, string Content, string Message);

internal sealed record RecordContent(string Content, int Lineage);

internal sealed record NameResponse(string Name);

internal sealed record IndexResponse(int Index);

internal sealed record KeysResponse(IReadOnlyList<string> Keys);

/// <summary>The lock table as a caller may see it: its version and the entries (TOML) about components they may read.</summary>
internal sealed record LockTableResponse(long Version, IReadOnlyList<string> Entries);

/// <summary>A lock decision to write: only while the table is still at <see cref="ExpectedVersion"/>.</summary>
internal sealed record LockCommit(long ExpectedVersion, IReadOnlyList<string> Saved, IReadOnlyList<string> Deleted);

internal sealed record JobRegistration(string Id, string Kind, int Pid, string ProcessStart, string? Log, bool Detached, bool Hosted, string Host,
	IReadOnlyList<string>? Components = null);

internal sealed record LogChunk(string Text);

internal sealed record CacheSize(int Entries, long Bytes);

/// <summary>
/// A component for the server to make (<c>POST /api/v1/components/{name}</c>): <c>create</c> (default branch, upstream,
/// message), <c>import</c> (from <see cref="Url"/>) or <c>fork</c> (of <see cref="Source"/> at <see cref="Commit"/>).
/// </summary>
internal sealed record ComponentRequest(string Action, string? DefaultBranch = null, string? Url = null, string? Message = null, string? Source = null,
	string? Branch = null, string? Commit = null, IReadOnlyList<string>? Tags = null);

internal sealed record LandRequest(IReadOnlyList<BranchMove> Moves);

/// <summary>A landing refused because branches moved (409): which.</summary>
internal sealed record LandRefused(string Error, IReadOnlyList<BranchMove> Moved);

/// <summary>A permission: <c>read</c>, <c>run</c>, <c>integrate</c> or <c>admin</c> on a component (or <c>*</c>) for a principal (<c>user:x</c>, <c>group:x</c> or <c>*</c>).</summary>
internal sealed record Grant(string Principal, string Component, string Permission);

internal sealed record GroupMember(string Group, string User);

/// <summary>A user's identity at a provider (an OIDC issuer, or <c>github</c>) and its subject there.</summary>
internal sealed record IdentityLink(string Provider, string Subject, string User);

internal sealed record AuditEntry(long Seq, string Time, string User, string Action, string Target, string Detail, string Remote);
