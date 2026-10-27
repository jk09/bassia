namespace Bassia.Tests.Server;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// An identity provider for tests, on a local port: an OpenID Connect issuer (discovery, keys, the authorization code
/// flow with PKCE - it logs in <see cref="Subject"/> at once - refresh tokens and client credentials) and GitHub (the
/// device flow and the web flow, approved at once, and the user API).
/// </summary>
internal sealed class FakeIdentityProvider : IAsyncDisposable
{
	public const string Audience = "bassia";
	public const string GitHubToken = "gh-test-token";

	private readonly RSA rsa = RSA.Create(2048);
	private readonly Dictionary<string, string> challenges = new(StringComparer.Ordinal);
	private WebApplication app = null!;

	public string Url { get; private set; } = "";

	/// <summary>Who the issuer logs in: its subject, email (verified) and groups.</summary>
	public string Subject { get; set; } = "okta-bob";

	public string? Email { get; set; } = "bob@example.com";

	public IReadOnlyList<string> Groups { get; set; } = ["devs"];

	/// <summary>How many tokens the token endpoint issued, by grant.</summary>
	public Dictionary<string, int> Issued { get; } = new(StringComparer.Ordinal);

	private SecurityKey Key => new RsaSecurityKey(rsa) { KeyId = "test-key" };

	public static async Task<FakeIdentityProvider> StartAsync()
	{
		var provider = new FakeIdentityProvider();
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		provider.Url = $"http://127.0.0.1:{port}";

		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.UseUrls(provider.Url);
		provider.app = builder.Build();
		provider.Map(provider.app);
		await provider.app.StartAsync();
		return provider;
	}

	/// <summary>The configuration of a Bassia server that accepts this provider's logins.</summary>
	public string ServerAuth => $"""
		[[auth.oidc]]
		name = "fake"
		issuer = "{Url}"
		audience = "{Audience}"
		client_id = "bassia-cli"
		scopes = "openid email offline_access"
		[auth.github]
		client_id = "github-app"
		api_url = "{Url}/github-api"
		device_url = "{Url}/github"
		""";

	private string Token(string subject, string? email)
	{
		var claims = new Dictionary<string, object> { ["sub"] = subject, ["groups"] = Groups.ToArray() };
		if (email is not null)
		{
			claims["email"] = email;
			claims["email_verified"] = true;
		}

		return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
		{
			Issuer = Url,
			Audience = Audience,
			Claims = claims,
			Expires = DateTime.UtcNow.AddMinutes(10),
			SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256)
		});
	}

	private void Map(WebApplication web)
	{
		web.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
		{
			["issuer"] = Url,
			["authorization_endpoint"] = $"{Url}/authorize",
			["token_endpoint"] = $"{Url}/token",
			["jwks_uri"] = $"{Url}/jwks",
			["id_token_signing_alg_values_supported"] = new[] { "RS256" }
		}));
		web.MapGet("/jwks", () =>
		{
			var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey((RsaSecurityKey)Key);
			return Results.Json(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
		});
		web.MapGet("/authorize", (HttpContext context) =>
		{
			var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
			lock (challenges)
			{
				challenges[code] = context.Request.Query["code_challenge"].ToString();
			}

			return Results.Redirect($"{context.Request.Query["redirect_uri"]}?code={code}&state={context.Request.Query["state"]}");
		});
		web.MapPost("/token", async (HttpContext context) =>
		{
			var form = await context.Request.ReadFormAsync();
			var grant = form["grant_type"].ToString();
			lock (Issued)
			{
				Issued[grant] = Issued.GetValueOrDefault(grant) + 1;
			}

			switch (grant)
			{
				case "authorization_code":
					string? challenge;
					lock (challenges)
					{
						challenges.Remove(form["code"].ToString(), out challenge);
					}

					var verified = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
					return challenge == verified
						? Results.Json(new { access_token = Token(Subject, Email), refresh_token = "refresh-" + Subject, expires_in = 600, token_type = "Bearer" })
						: Results.Json(new { error = "invalid_grant", error_description = "the code verifier does not match" }, statusCode: 400);
				case "refresh_token":
					return Results.Json(new { access_token = Token(Subject, Email), refresh_token = form["refresh_token"].ToString(), expires_in = 600, token_type = "Bearer" });
				case "client_credentials":
					return Results.Json(new { access_token = Token(form["client_id"].ToString(), null), expires_in = 600, token_type = "Bearer" });
				default:
					return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);
			}
		});

		web.MapPost("/github/login/device/code", () => Results.Json(new { device_code = "device-1", user_code = "ABCD-1234", verification_uri = $"{Url}/github/device", interval = 0, expires_in = 60 }));
		web.MapGet("/github/login/oauth/authorize", (HttpContext context) =>
			Results.Redirect($"{context.Request.Query["redirect_uri"]}?code=github-code&state={context.Request.Query["state"]}"));
		web.MapPost("/github/login/oauth/access_token", () => Results.Json(new { access_token = GitHubToken, token_type = "bearer" }));
		web.MapGet("/github-api/user", (HttpContext context) => context.Request.Headers.Authorization.ToString() == $"Bearer {GitHubToken}"
			? Results.Json(new { id = 4242, login = "bob" })
			: Results.StatusCode(401));
		web.MapGet("/github-api/user/emails", (HttpContext context) => context.Request.Headers.Authorization.ToString() == $"Bearer {GitHubToken}"
			? Results.Json(new[] { new { email = Email, verified = true, primary = true } })
			: Results.StatusCode(401));
	}

	public async ValueTask DisposeAsync()
	{
		await app.StopAsync();
		await app.DisposeAsync();
		rsa.Dispose();
	}
}
