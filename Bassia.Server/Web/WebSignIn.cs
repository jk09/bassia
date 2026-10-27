namespace Bassia.Server.Web;

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bassia.Server.Api;
using Bassia.Server.Auth;
using static Bassia.Web.Html;

/// <summary>
/// Signing in to the dashboard in a browser: the authorization-code flow with PKCE, run by the server, with an OIDC
/// issuer (its client, confidential when it has a <c>client_secret</c>) or GitHub (the OAuth app's web flow, which
/// needs its <c>client_secret</c>). The identity is mapped to a registered user as on the API, and the browser gets a
/// session cookie holding the server's own token for <c>session_hours</c>: HttpOnly, SameSite=Lax, Secure on https.
/// Every request is still checked against the registry and the grants, so a removed user or permission takes effect
/// at once. A request with a bearer token (a script, a test) needs no cookie.
/// </summary>
internal static class WebSignIn
{
	public const string SessionCookie = "bassia_session";

	private const string LoginCookie = "bassia_login";

	/// <summary>How long the browser may take between leaving for the identity provider and coming back.</summary>
	private static readonly TimeSpan LoginLifetime = TimeSpan.FromMinutes(10);

	/// <summary>A way to sign in: an OIDC issuer's name and the client it uses, or <c>github</c>.</summary>
	internal sealed record Provider(string Name, string Label, OidcValidator? Oidc);

	public static IReadOnlyList<Provider> Providers(ServerState state) =>
		state.Authenticator.Issuers.Where(issuer => issuer.Provider.ClientId is not null)
			.Select(issuer => new Provider(issuer.Provider.Name, issuer.Provider.Name, issuer))
			.Concat(state.Config.GitHub is { ClientSecret: not null } ? [new Provider("github", "GitHub", null)] : [])
			.ToList();

	public static void Map(WebApplication app, ServerState state)
	{
		app.MapGet("/login", (HttpRequest request) => LoginPage(state, request));
		app.MapGet("/login/{provider}", (string provider, HttpContext context) => StartAsync(state, provider, context));
		app.MapGet("/login/{provider}/callback", (string provider, HttpContext context) => CallbackAsync(state, provider, context));
		app.MapGet("/logout", (HttpContext context) =>
		{
			context.Response.Cookies.Delete(SessionCookie, Cookie(state, context, DateTimeOffset.UnixEpoch));
			return Results.Redirect("/login?signed_out=1");
		});
	}

	/// <summary>
	/// The caller of a dashboard request: its bearer token, else its session cookie; null when neither stands for a
	/// registered user (none, forged, expired).
	/// </summary>
	public static async Task<Caller?> CallerAsync(ServerState state, HttpContext context)
	{
		if (await state.Authenticator.AuthenticateAsync(context.Request) is { } caller)
		{
			return caller;
		}

		if (context.Request.Cookies[SessionCookie] is not { Length: > 0 } session)
		{
			return null;
		}

		try
		{
			return await state.Authenticator.AuthenticateAsync(session);
		}
		catch (AuthException)
		{
			return null;
		}
	}

	/// <summary>Where a request without a sign-in goes: a page to the sign-in, coming back to it after; a fragment or the event stream is refused.</summary>
	public static IResult Challenge(HttpContext context) =>
		context.Request.Headers.ContainsKey("X-Fragment") || context.Request.Path.StartsWithSegments("/events")
			? Results.Text("Sign in again.", statusCode: StatusCodes.Status401Unauthorized)
			: Results.Redirect("/login?return=" + Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));

	public static string Label(ServerConfig config) => config.Organization ?? config.PublicUrl ?? "Bassia server";

	private static IResult Page(ServerState state, string title, string body, int status = StatusCodes.Status200OK) =>
		Results.Content(Bassia.Web.Html.Page(Label(state.Config), "", title, body), "text/html; charset=utf-8", Encoding.UTF8, status);

	private static IResult LoginPage(ServerState state, HttpRequest request)
	{
		var back = LocalPath(request.Query["return"]);
		var providers = Providers(state);
		var body = new StringBuilder();
		if (request.Query.ContainsKey("signed_out"))
		{
			body.Append("<p class=\"muted\">You are signed out.</p>");
		}

		body.Append(providers.Count == 0
			? Error("This server has no sign-in for browsers: give an [[auth.oidc]] issuer a client_id, or [auth.github] its client_secret.")
			: "<div class=\"cards\">" + string.Concat(providers.Select(provider =>
				$"<a class=\"card\" href=\"/login/{Url(provider.Name)}?return={Url(back)}\"><div class=\"n\">→</div><div>Sign in with <b>{E(provider.Label)}</b></div></a>")) + "</div>");
		body.Append("<p class=\"muted\">You see the components you may read, and nothing else. From a terminal, the same account works with " + Cmd("bassia login") + ".</p>");
		return Page(state, "Sign in", body.ToString());
	}

	private static async Task<IResult> StartAsync(ServerState state, string name, HttpContext context)
	{
		if (Providers(state).FirstOrDefault(provider => provider.Name == name) is not { } provider)
		{
			return Page(state, "Sign in", Error($"No sign-in '{name}' on this server."), StatusCodes.Status404NotFound);
		}

		var nonce = Random();
		var verifier = Random();
		var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
		var redirect = RedirectUri(state.Config, context.Request, name);
		string authorize;
		if (provider.Oidc is { } oidc)
		{
			var discovered = await oidc.DiscoverAsync();
			authorize = Query(discovered.AuthorizationEndpoint, ("response_type", "code"), ("client_id", oidc.Provider.ClientId!), ("redirect_uri", redirect),
				("scope", oidc.Provider.Scopes), ("state", nonce), ("code_challenge", challenge), ("code_challenge_method", "S256"));
		}
		else
		{
			authorize = Query(state.Config.GitHub!.DeviceUrl + "/login/oauth/authorize", ("client_id", state.Config.GitHub.ClientId), ("redirect_uri", redirect),
				("scope", "read:user user:email"), ("state", nonce), ("code_challenge", challenge), ("code_challenge_method", "S256"));
		}

		var login = new Login(name, nonce, verifier, LocalPath(context.Request.Query["return"]));
		context.Response.Cookies.Append(LoginCookie, Protect(state.Config, JsonSerializer.Serialize(login)), Cookie(state, context, DateTimeOffset.UtcNow + LoginLifetime, "/login"));
		return Results.Redirect(authorize);
	}

	/// <summary>What the browser carries to the identity provider and back: which sign-in, its state, its PKCE verifier, where to go after.</summary>
	private sealed record Login(string Provider, string State, string Verifier, string Return);

	private static async Task<IResult> CallbackAsync(ServerState state, string name, HttpContext context)
	{
		var query = context.Request.Query;
		Login? login = null;
		if (context.Request.Cookies[LoginCookie] is { } cookie && Unprotect(state.Config, cookie) is { } payload)
		{
			try
			{
				login = JsonSerializer.Deserialize<Login>(payload);
			}
			catch (JsonException)
			{
			}
		}

		context.Response.Cookies.Delete(LoginCookie, Cookie(state, context, DateTimeOffset.UnixEpoch, "/login"));
		if (query["error"] is { Count: > 0 } error)
		{
			return Page(state, "Sign in", Error($"The sign-in was refused: {error} {query["error_description"]}") + Again(), StatusCodes.Status401Unauthorized);
		}

		if (login is null || login.Provider != name || query["state"] != login.State || query["code"] is not { Count: > 0 } code
			|| Providers(state).FirstOrDefault(provider => provider.Name == name) is not { } provider)
		{
			return Page(state, "Sign in", Error("The sign-in expired or did not start here.") + Again(), StatusCodes.Status400BadRequest);
		}

		try
		{
			var redirect = RedirectUri(state.Config, context.Request, name);
			string user;
			IReadOnlyList<string> groups = [];
			if (provider.Oidc is { } oidc)
			{
				var form = new Dictionary<string, string>
				{
					["grant_type"] = "authorization_code",
					["code"] = code.ToString(),
					["redirect_uri"] = redirect,
					["client_id"] = oidc.Provider.ClientId!,
					["code_verifier"] = login.Verifier
				};
				if (oidc.Provider.ClientSecret is { } secret)
				{
					form["client_secret"] = secret;
				}

				var tokens = await PostAsync(state, (await oidc.DiscoverAsync()).TokenEndpoint, form);
				var caller = await AuthenticateAsync(state, tokens);
				var own = await state.Access.GroupsOfAsync(caller.Id);
				(user, groups) = (caller.Id, caller.Groups.Except(own, StringComparer.Ordinal).ToList());
			}
			else
			{
				var github = state.Config.GitHub!;
				var tokens = await PostAsync(state, github.DeviceUrl + "/login/oauth/access_token", new Dictionary<string, string>
				{
					["client_id"] = github.ClientId,
					["client_secret"] = github.ClientSecret!,
					["code"] = code.ToString(),
					["redirect_uri"] = redirect,
					["code_verifier"] = login.Verifier
				});
				var token = Text(tokens, "access_token") ?? throw new AuthException($"GitHub gave no access token: {Text(tokens, "error_description") ?? Text(tokens, "error") ?? "no reason"}.");
				user = (await IdentityEndpoints.GitHubUserAsync(state, token)).User.Id;
			}

			var lifetime = TimeSpan.FromHours(state.Config.SessionHours);
			context.Response.Cookies.Append(SessionCookie, state.Tokens.Issue(user, lifetime, groups: groups), Cookie(state, context, DateTimeOffset.UtcNow + lifetime));
			await state.Access.AuditAsync(user, "login", name, "dashboard", ServerApp.RemoteOf(context));
			return Results.Redirect(login.Return);
		}
		catch (AuthException ex)
		{
			return Page(state, "Sign in", Error(ex.Message) + Again(), StatusCodes.Status401Unauthorized);
		}
	}

	private static string Again() => "<p><a href=\"/login\">Sign in again</a></p>";

	/// <summary>The caller an issuer's token response stands for: its access token, else its id token (whose audience is the client).</summary>
	private static async Task<Caller> AuthenticateAsync(ServerState state, JsonElement tokens)
	{
		AuthException? failure = null;
		foreach (var token in new[] { Text(tokens, "access_token"), Text(tokens, "id_token") }.OfType<string>())
		{
			try
			{
				return await state.Authenticator.AuthenticateAsync(token);
			}
			catch (AuthException ex)
			{
				failure ??= ex;
			}
		}

		throw failure ?? new AuthException($"The identity provider gave no token: {Text(tokens, "error_description") ?? Text(tokens, "error") ?? "no reason"}.");
	}

	private static async Task<JsonElement> PostAsync(ServerState state, string url, Dictionary<string, string> form)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
		request.Headers.Accept.ParseAdd("application/json");
		using var response = await state.Http.SendAsync(request);
		try
		{
			return await response.Content.ReadFromJsonAsync<JsonElement>();
		}
		catch (JsonException)
		{
			throw new AuthException($"The identity provider's token endpoint answered {(int)response.StatusCode} without JSON.");
		}
	}

	private static string? Text(JsonElement json, string name) =>
		json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	/// <summary>Where the identity provider sends the browser back: under the public URL when there is one (behind a load balancer).</summary>
	internal static string RedirectUri(ServerConfig config, HttpRequest request, string provider) =>
		(config.PublicUrl ?? $"{request.Scheme}://{request.Host}").TrimEnd('/') + $"/login/{Uri.EscapeDataString(provider)}/callback";

	/// <summary>A path on this server to go back to; anything else (another site, a scheme) is the overview.</summary>
	internal static string LocalPath(string? path) =>
		path is { Length: > 0 } && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("/\\", StringComparison.Ordinal) ? path : "/";

	private static CookieOptions Cookie(ServerState state, HttpContext context, DateTimeOffset expires, string path = "/") => new()
	{
		HttpOnly = true,
		SameSite = SameSiteMode.Lax,
		Secure = (state.Config.PublicUrl ?? context.Request.Scheme + "://").StartsWith("https://", StringComparison.OrdinalIgnoreCase),
		Path = path,
		Expires = expires
	};

	private static string Query(string url, params (string Name, string Value)[] parameters) =>
		url + (url.Contains('?') ? "&" : "?") + string.Join("&", parameters.Select(parameter => $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value)}"));

	private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));

	private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	/// <summary>A value signed with the server's key, so the browser can carry it but not change it.</summary>
	private static string Protect(ServerConfig config, string value)
	{
		var payload = Base64Url(Encoding.UTF8.GetBytes(value));
		return payload + "." + Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(config.SigningKey), Encoding.UTF8.GetBytes("login:" + payload)));
	}

	private static string? Unprotect(ServerConfig config, string value)
	{
		var dot = value.IndexOf('.');
		if (dot <= 0)
		{
			return null;
		}

		var payload = value[..dot];
		var expected = Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(config.SigningKey), Encoding.UTF8.GetBytes("login:" + payload)));
		if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(value[(dot + 1)..])))
		{
			return null;
		}

		var base64 = payload.Replace('-', '+').Replace('_', '/');
		try
		{
			return Encoding.UTF8.GetString(Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4)));
		}
		catch (FormatException)
		{
			return null;
		}
	}
}
