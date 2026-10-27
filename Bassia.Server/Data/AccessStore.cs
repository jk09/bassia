namespace Bassia.Server.Data;

using System.Security.Cryptography;
using System.Text;
using Bassia.Storage.Remote;

/// <summary>
/// Who is who and who may do what, in the server's database: links from identity providers' subjects to registered
/// users, permission grants, the server's own groups, refresh tokens (by hash only), replies to writes by idempotency
/// key, the audit log and the landing queue.
/// </summary>
internal sealed class AccessStore(ServerDatabase database)
{
	public ServerDatabase Database => database;

	// ----- identities -----

	public async Task<string?> UserOfAsync(string provider, string subject) =>
		(await database.QueryAsync("SELECT user_id FROM identities WHERE provider = @provider AND subject = @subject;", reader => reader.GetString(0),
			("@provider", provider), ("@subject", subject))).FirstOrDefault();

	public Task LinkAsync(IdentityLink link) => database.ExecuteAsync(
		"INSERT INTO identities (provider, subject, user_id) VALUES (@provider, @subject, @user) ON CONFLICT (provider, subject) DO UPDATE SET user_id = excluded.user_id;",
		("@provider", link.Provider), ("@subject", link.Subject), ("@user", link.User));

	public Task<int> UnlinkAsync(string provider, string subject) =>
		database.ExecuteAsync("DELETE FROM identities WHERE provider = @provider AND subject = @subject;", ("@provider", provider), ("@subject", subject));

	public Task<IReadOnlyList<IdentityLink>> LinksAsync() =>
		database.QueryAsync("SELECT provider, subject, user_id FROM identities ORDER BY user_id, provider, subject;",
			reader => new IdentityLink(reader.GetString(0), reader.GetString(1), reader.GetString(2)));

	// ----- grants and groups -----

	public Task<IReadOnlyList<Grant>> GrantsAsync() =>
		database.QueryAsync("SELECT principal, component, permission FROM grants ORDER BY component, principal;",
			reader => new Grant(reader.GetString(0), reader.GetString(1), reader.GetString(2)));

	public Task GrantAsync(Grant grant) => database.ExecuteAsync(
		"INSERT INTO grants (principal, component, permission) VALUES (@principal, @component, @permission) ON CONFLICT (principal, component) DO UPDATE SET permission = excluded.permission;",
		("@principal", grant.Principal), ("@component", grant.Component), ("@permission", grant.Permission));

	public Task<int> RevokeAsync(string principal, string component) =>
		database.ExecuteAsync("DELETE FROM grants WHERE principal = @principal AND component = @component;", ("@principal", principal), ("@component", component));

	public Task<IReadOnlyList<GroupMember>> MembersAsync() =>
		database.QueryAsync("SELECT group_name, user_id FROM group_members ORDER BY group_name, user_id;", reader => new GroupMember(reader.GetString(0), reader.GetString(1)));

	public async Task<IReadOnlyList<string>> GroupsOfAsync(string user) =>
		await database.QueryAsync("SELECT group_name FROM group_members WHERE user_id = @user ORDER BY group_name;", reader => reader.GetString(0), ("@user", user));

	public Task AddMemberAsync(GroupMember member) => database.ExecuteAsync(
		"INSERT INTO group_members (group_name, user_id) VALUES (@group, @user) ON CONFLICT (group_name, user_id) DO NOTHING;",
		("@group", member.Group), ("@user", member.User));

	public Task<int> RemoveMemberAsync(GroupMember member) =>
		database.ExecuteAsync("DELETE FROM group_members WHERE group_name = @group AND user_id = @user;", ("@group", member.Group), ("@user", member.User));

	// ----- refresh tokens -----

	private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

	/// <summary>A new refresh token for <paramref name="user"/>, valid for <paramref name="lifetime"/>; only its hash is stored.</summary>
	public async Task<string> IssueRefreshAsync(string user, TimeSpan lifetime)
	{
		var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		await database.ExecuteAsync("INSERT INTO refresh_tokens (token_hash, user_id, expires) VALUES (@hash, @user, @expires);",
			("@hash", Hash(token)), ("@user", user), ("@expires", ServerDatabase.Now(DateTimeOffset.UtcNow + lifetime)));
		return token;
	}

	/// <summary>Spends a refresh token: its user while it is valid, null otherwise. A token is good for one refresh.</summary>
	public Task<string?> RedeemRefreshAsync(string token) => database.TransactAsync(async (connection, transaction) =>
	{
		var user = (await ServerDatabase.QueryAsync(connection, transaction, "SELECT user_id FROM refresh_tokens WHERE token_hash = @hash AND expires > @now;",
			reader => reader.GetString(0), ("@hash", Hash(token)), ("@now", ServerDatabase.Now()))).FirstOrDefault();
		await ServerDatabase.ExecuteAsync(connection, transaction, "DELETE FROM refresh_tokens WHERE token_hash = @hash OR expires <= @now;",
			("@hash", Hash(token)), ("@now", ServerDatabase.Now()));
		return user;
	});

	// ----- idempotency -----

	public async Task<(int Status, string Body)?> ReplyOfAsync(string key, string user) =>
		(await database.QueryAsync("SELECT status, body FROM idempotency WHERE idem_key = @key AND user_id = @user;",
			reader => (Convert.ToInt32(reader.GetValue(0)), reader.GetString(1)), ("@key", key), ("@user", user))).Cast<(int, string)?>().FirstOrDefault();

	/// <summary>Remembers a write's reply under its key for a day, so a retry gets the same reply instead of a second write.</summary>
	public async Task RememberAsync(string key, string user, int status, string body)
	{
		await database.ExecuteAsync(
			"INSERT INTO idempotency (idem_key, user_id, status, body, created) VALUES (@key, @user, @status, @body, @now) ON CONFLICT (idem_key, user_id) DO NOTHING;",
			("@key", key), ("@user", user), ("@status", status), ("@body", body), ("@now", ServerDatabase.Now()));
		await database.ExecuteAsync("DELETE FROM idempotency WHERE created < @before;", ("@before", ServerDatabase.Now(DateTimeOffset.UtcNow.AddDays(-1))));
	}

	// ----- audit -----

	/// <summary>Appends to the audit log; it is never changed or deleted through the server.</summary>
	public Task AuditAsync(string user, string action, string target, string detail, string remote) => database.ExecuteAsync(
		"INSERT INTO audit (time, user_id, action, target, detail, remote) VALUES (@time, @user, @action, @target, @detail, @remote);",
		("@time", ServerDatabase.Now()), ("@user", user), ("@action", action), ("@target", target), ("@detail", detail), ("@remote", remote));

	public Task<IReadOnlyList<AuditEntry>> AuditLogAsync(int limit, long? before) => database.QueryAsync(
		"SELECT seq, time, user_id, action, target, detail, remote FROM audit WHERE seq < @before ORDER BY seq DESC LIMIT @limit;",
		reader => new AuditEntry(Convert.ToInt64(reader.GetValue(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
			reader.GetString(5), reader.GetString(6)),
		("@before", before ?? long.MaxValue), ("@limit", limit));

	// ----- the landing queue -----

	/// <summary>
	/// Takes the landing slot of <paramref name="component"/> for <paramref name="holder"/> unless another holder has it
	/// and its lease has not run out; true when taken.
	/// </summary>
	public async Task<bool> TakeLandingAsync(string component, string holder, TimeSpan lease) =>
		await database.ExecuteAsync(
			"""
			INSERT INTO land_queue (component, holder, expires) VALUES (@component, @holder, @expires)
			ON CONFLICT (component) DO UPDATE SET holder = excluded.holder, expires = excluded.expires WHERE land_queue.expires < @now;
			""",
			("@component", component), ("@holder", holder), ("@expires", ServerDatabase.Now(DateTimeOffset.UtcNow + lease)), ("@now", ServerDatabase.Now())) == 1;

	public Task ReleaseLandingAsync(string component, string holder) =>
		database.ExecuteAsync("DELETE FROM land_queue WHERE component = @component AND holder = @holder;", ("@component", component), ("@holder", holder));

	/// <summary>The components being landed on now, with who holds each slot.</summary>
	public Task<IReadOnlyList<(string Component, string Holder)>> LandingAsync() =>
		database.QueryAsync("SELECT component, holder FROM land_queue WHERE expires > @now ORDER BY component;",
			reader => (reader.GetString(0), reader.GetString(1)), ("@now", ServerDatabase.Now()));
}
