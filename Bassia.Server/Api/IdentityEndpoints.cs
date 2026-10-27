namespace Bassia.Server.Api;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bassia.Server.Auth;
using Bassia.Storage.Remote;

/// <summary>
/// Who the caller is and how they get a credential: the server's description (<c>/info</c>, open), the GitHub login
/// exchange and refreshes (<c>/auth/...</c>, open), <c>/me</c>, and worker tokens.
/// </summary>
internal static class IdentityEndpoints
{
	/// <summary>The longest a worker token may live.</summary>
	public static readonly TimeSpan MaxWorkerLifetime = TimeSpan.FromHours(24);

	public static void Map(RouteGroupBuilder api, ServerState state)
	{
		api.MapGet("/info", () => ServerApp.Json(Info(state.Config)));
		api.MapPost("/auth/github", async (GitHubLogin login, HttpContext context) => ServerApp.Json(await GitHubAsync(state, login, context)));
		api.MapPost("/auth/refresh", async (RefreshRequest request, HttpContext context) => ServerApp.Json(await RefreshAsync(state, request, context)));
		api.MapGet("/me", (HttpContext context) =>
		{
			var caller = ServerApp.CallerOf(context);
			return ServerApp.Json(new Me(caller.Id, caller.Groups, caller.Admin, caller.Scope));
		});
		api.MapPost("/tokens/worker", async (WorkerTokenRequest request, HttpContext context) => ServerApp.Json(await WorkerTokenAsync(state, request, context)));
	}

	public static ServerInfo Info(ServerConfig config) => new("bassia", 1,
		config.Oidc.Select(provider => new AuthProvider("oidc", provider.Name, provider.Issuer, provider.ClientId, provider.Scopes))
			.Concat(config.GitHub is { } github ? [new AuthProvider("github", "github", ClientId: github.ClientId, Scopes: "read:user user:email", DeviceUrl: github.DeviceUrl)] : [])
			.ToList(),
		config.Organization);

	private static async Task<TokenResponse> IssueAsync(ServerState state, string user)
	{
		var lifetime = TimeSpan.FromMinutes(state.Config.TokenMinutes);
		var refresh = await state.Access.IssueRefreshAsync(user, TimeSpan.FromDays(state.Config.RefreshDays));
		return new TokenResponse(state.Tokens.Issue(user, lifetime), refresh, (long)lifetime.TotalSeconds);
	}

	/// <summary>
	/// Checks a GitHub access token with GitHub (who it belongs to, their verified emails), maps the account to a
	/// registered user and gives back the server's own tokens.
	/// </summary>
	private static async Task<TokenResponse> GitHubAsync(ServerState state, GitHubLogin login, HttpContext context)
	{
		var (user, account) = await GitHubUserAsync(state, login.AccessToken);
		await state.Access.AuditAsync(user.Id, "login", "github", $"GitHub account {account}", ServerApp.RemoteOf(context));
		return await IssueAsync(state, user.Id);
	}

	/// <summary>The registered user a GitHub access token's account is, and the account's id, checked with GitHub.</summary>
	public static async Task<(Core.UserDefinition User, string Account)> GitHubUserAsync(ServerState state, string accessToken)
	{
		var github = state.Config.GitHub ?? throw new AuthException("This server does not accept GitHub logins.");
		async Task<JsonElement> GetAsync(string path)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, github.ApiUrl + path);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
			request.Headers.UserAgent.Add(new ProductInfoHeaderValue("bassia-server", "1"));
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
			using var response = await state.Http.SendAsync(request);
			if (!response.IsSuccessStatusCode)
			{
				throw new AuthException($"GitHub did not accept the token ({(int)response.StatusCode}).");
			}

			return await response.Content.ReadFromJsonAsync<JsonElement>();
		}

		var account = await GetAsync("/user");
		var id = account.GetProperty("id").ToString();
		var emails = (await GetAsync("/user/emails")).EnumerateArray()
			.Where(email => email.TryGetProperty("verified", out var verified) && verified.GetBoolean())
			.Select(email => email.GetProperty("email").GetString()!)
			.ToList();
		return (await state.Authenticator.GitHubUserAsync(id, emails), id);
	}

	private static async Task<TokenResponse> RefreshAsync(ServerState state, RefreshRequest request, HttpContext context)
	{
		var user = await state.Access.RedeemRefreshAsync(request.RefreshToken) ?? throw new AuthException("The refresh token is not valid or has expired; log in again.");
		if (!state.Config.Admins.Contains(user) && (await state.Meta.Store.LoadUsersAsync()).All(candidate => candidate.Id != user))
		{
			throw new AuthException($"'{user}' is no longer a registered user.");
		}

		return await IssueAsync(state, user);
	}

	/// <summary>A token for a worker: <c>run</c> at most, on the named components only, for at most a day.</summary>
	private static async Task<TokenResponse> WorkerTokenAsync(ServerState state, WorkerTokenRequest request, HttpContext context)
	{
		var caller = ServerApp.CallerOf(context);
		if (caller.Scope is not null)
		{
			throw new ForbiddenException("A worker token cannot make another one.");
		}

		if (request.Components.Count == 0)
		{
			throw new ArgumentException("A worker token needs the components it may run on.");
		}

		await state.RequireAsync(caller, request.Components, Permission.Run);
		var lifetime = TimeSpan.FromMinutes(Math.Clamp(request.Minutes, 1, MaxWorkerLifetime.TotalMinutes));
		await state.Access.AuditAsync(caller.Id, "token.worker", string.Join(",", request.Components), $"{lifetime.TotalMinutes:0} minutes", ServerApp.RemoteOf(context));
		return new TokenResponse(state.Tokens.Issue(caller.Id, lifetime, request.Components), null, (long)lifetime.TotalSeconds);
	}
}
