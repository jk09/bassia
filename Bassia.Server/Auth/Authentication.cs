namespace Bassia.Server.Auth;

using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Bassia.Core;
using Bassia.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Who a request comes from: a registered user, their groups (the server's and the identity provider's), whether they
/// are a server admin, and - for a worker token - the only components it may touch.
/// </summary>
internal sealed record Caller(UserDefinition User, IReadOnlyList<string> Groups, bool UserIsAdmin, IReadOnlyList<string>? Scope, string Provider)
{
	public string Id => User.Id;

	/// <summary>A server admin acting as one: a worker token of an admin is still scoped.</summary>
	public bool Admin => UserIsAdmin && Scope is null;
}

internal sealed class AuthException(string message) : Exception(message)
{
	/// <summary>A claim of a validated token, or null.</summary>
	public static object? Claim(IDictionary<string, object> claims, string name) => claims.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// The server's own tokens, signed with its key (HMAC-SHA256): access tokens after a GitHub login or a refresh, and
/// worker tokens scoped to components. They are short-lived; a refresh token (see <see cref="AccessStore"/>) gets a new
/// access token.
/// </summary>
internal sealed class ServerTokens(ServerConfig config)
{
	private readonly SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(config.SigningKey));
	private readonly JsonWebTokenHandler handler = new();

	public string Issue(string user, TimeSpan lifetime, IReadOnlyList<string>? scope = null)
	{
		var claims = new Dictionary<string, object> { ["sub"] = user, ["typ"] = scope is null ? "access" : "worker" };
		if (scope is not null)
		{
			claims["cmp"] = string.Join(" ", scope);
		}

		return handler.CreateToken(new SecurityTokenDescriptor
		{
			Issuer = ServerConfig.Issuer,
			Audience = ServerConfig.Issuer,
			Claims = claims,
			Expires = DateTime.UtcNow + lifetime,
			NotBefore = DateTime.UtcNow.AddSeconds(-5),
			SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
		});
	}

	/// <summary>The user and scope of a valid token of this server; null when it is not one (forged, expired).</summary>
	public async Task<(string User, IReadOnlyList<string>? Scope)?> ValidateAsync(string token)
	{
		var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
		{
			ValidIssuer = ServerConfig.Issuer,
			ValidAudience = ServerConfig.Issuer,
			IssuerSigningKey = key,
			ClockSkew = TimeSpan.FromSeconds(30)
		});
		if (!result.IsValid || AuthException.Claim(result.Claims, "sub") is not string user)
		{
			return null;
		}

		var scope = AuthException.Claim(result.Claims, "typ") as string == "worker"
			? (AuthException.Claim(result.Claims, "cmp") as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
			: null;
		return (user, scope);
	}
}

/// <summary>Validates the access tokens of one OpenID Connect issuer: its signing keys from its discovery document, audience, lifetime.</summary>
internal sealed class OidcValidator
{
	private readonly OidcProvider provider;
	private readonly ConfigurationManager<OpenIdConnectConfiguration> configuration;
	private readonly JsonWebTokenHandler handler = new();

	public OidcValidator(OidcProvider provider, HttpClient? http = null)
	{
		this.provider = provider;
		var retriever = new HttpDocumentRetriever(http ?? new HttpClient()) { RequireHttps = provider.Issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase) };
		configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
			provider.Issuer.TrimEnd('/') + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), retriever);
	}

	public OidcProvider Provider => provider;

	public async Task<IDictionary<string, object>?> ValidateAsync(string token)
	{
		for (var attempt = 0; attempt < 2; attempt++)
		{
			var discovered = await configuration.GetConfigurationAsync(CancellationToken.None);
			var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
			{
				ValidIssuer = provider.Issuer,
				ValidAudience = provider.Audience,
				IssuerSigningKeys = discovered.SigningKeys,
				ClockSkew = TimeSpan.FromSeconds(60)
			});
			if (result.IsValid)
			{
				return result.Claims;
			}

			// The issuer may have rotated its keys since the discovery document was read.
			if (result.Exception is SecurityTokenSignatureKeyNotFoundException && attempt == 0)
			{
				configuration.RequestRefresh();
				continue;
			}

			return null;
		}

		return null;
	}
}

/// <summary>
/// Turns a request's credential into a <see cref="Caller"/>: a bearer token, or a token as the password of HTTP basic
/// authentication (what git sends). The token is the server's own, or an access token of a configured OIDC issuer; an
/// identity is mapped to a registered user by its link (provider, subject), made on first use from a verified email.
/// </summary>
internal sealed class Authenticator(ServerConfig config, ServerTokens tokens, AccessStore access, Func<Task<IReadOnlyList<UserDefinition>>> users,
	IReadOnlyList<OidcValidator> issuers)
{
	public static string? TokenOf(HttpRequest request)
	{
		if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.FirstOrDefault(), out var header) || header.Parameter is null)
		{
			return null;
		}

		if (header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
		{
			return header.Parameter;
		}

		if (header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
				return pair.IndexOf(':') is var colon and >= 0 ? pair[(colon + 1)..] : null;
			}
			catch (FormatException)
			{
				return null;
			}
		}

		return null;
	}

	/// <summary>The caller, or null when the request carries no valid credential of a registered user.</summary>
	public async Task<Caller?> AuthenticateAsync(HttpRequest request)
	{
		if (TokenOf(request) is not { } token)
		{
			return null;
		}

		try
		{
			return await AuthenticateAsync(token);
		}
		catch (AuthException)
		{
			return null;
		}
	}

	/// <summary>The caller a token stands for; throws <see cref="AuthException"/> saying why it stands for nobody.</summary>
	public async Task<Caller> AuthenticateAsync(string token)
	{
		string issuer;
		try
		{
			issuer = new JsonWebToken(token).Issuer;
		}
		catch (ArgumentException)
		{
			throw new AuthException("The credential is not a token.");
		}

		var registered = await users();
		if (issuer == ServerConfig.Issuer)
		{
			var valid = await tokens.ValidateAsync(token) ?? throw new AuthException("The token is not valid or has expired; log in again.");
			// A configured admin may act before registering (a new server's meta-repo has no users yet).
			var user = registered.FirstOrDefault(candidate => candidate.Id == valid.User)
				?? (config.Admins.Contains(valid.User, StringComparer.Ordinal) ? new UserDefinition(valid.User, valid.User, $"{valid.User}@localhost") : null)
				?? throw new AuthException($"'{valid.User}' is no longer a registered user.");
			return await CallerAsync(user, [], valid.Scope, ServerConfig.Issuer);
		}

		var validator = issuers.FirstOrDefault(candidate => candidate.Provider.Issuer == issuer) ?? throw new AuthException($"Tokens of '{issuer}' are not accepted here.");
		var claims = await validator.ValidateAsync(token) ?? throw new AuthException("The token is not valid or has expired; log in again.");
		var subject = AuthException.Claim(claims, "sub") as string ?? throw new AuthException("The token names no subject.");
		var linked = await access.UserOfAsync(issuer, subject);
		if (linked is null && AuthException.Claim(claims, "email") is string email && AuthException.Claim(claims, "email_verified") is true or "true")
		{
			if (registered.FirstOrDefault(candidate => candidate.Email.Equals(email, StringComparison.OrdinalIgnoreCase)) is { } byEmail)
			{
				await access.LinkAsync(new(issuer, subject, byEmail.Id));
				linked = byEmail.Id;
			}
		}

		var mapped = registered.FirstOrDefault(candidate => candidate.Id == linked)
			?? throw new AuthException($"The identity '{subject}' of '{validator.Provider.Name}' is not linked to a registered user; an admin links it with 'bassia access link'.");
		return await CallerAsync(mapped, Groups(claims), null, issuer);
	}

	/// <summary>Maps a GitHub user (checked by the caller with GitHub) to a registered user: by link, else by a verified email.</summary>
	public async Task<UserDefinition> GitHubUserAsync(string id, IReadOnlyList<string> verifiedEmails)
	{
		var registered = await users();
		var linked = await access.UserOfAsync("github", id);
		if (linked is null && registered.FirstOrDefault(candidate => verifiedEmails.Contains(candidate.Email, StringComparer.OrdinalIgnoreCase)) is { } byEmail)
		{
			await access.LinkAsync(new("github", id, byEmail.Id));
			linked = byEmail.Id;
		}

		return registered.FirstOrDefault(candidate => candidate.Id == linked)
			?? throw new AuthException($"The GitHub account {id} is not linked to a registered user, and none of its verified emails is a registered user's.");
	}

	private async Task<Caller> CallerAsync(UserDefinition user, IReadOnlyList<string> tokenGroups, IReadOnlyList<string>? scope, string provider)
	{
		var groups = (await access.GroupsOfAsync(user.Id)).Concat(tokenGroups).Distinct(StringComparer.Ordinal).ToList();
		var admin = (config.Admins.Contains(user.Id, StringComparer.Ordinal)
			|| (await access.GrantsAsync()).Any(grant => grant.Component == "*" && grant.Permission == "admin" && Permissions.Matches(grant.Principal, user.Id, groups)));
		return new Caller(user, groups, admin, scope, provider);
	}

	private static IReadOnlyList<string> Groups(IDictionary<string, object> claims) => AuthException.Claim(claims, "groups") switch
	{
		string one => [one],
		IEnumerable<object> many => many.Select(item => item.ToString() ?? "").Where(item => item.Length > 0).ToList(),
		JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToList(),
		_ => []
	};
}
