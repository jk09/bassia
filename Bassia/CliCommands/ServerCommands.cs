namespace Bassia;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bassia.Cli;
using Bassia.Storage.Remote;

/// <summary>
/// The commands of a Bassia server's users: <c>login</c>, <c>logout</c>, <c>credential</c> (git's credential helper),
/// <c>token create</c> (a worker's token), <c>access ...</c> (grants, groups, identity links) and <c>audit</c>.
/// </summary>
internal static class ServerCommands
{
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

	/// <summary>The server a command addresses: <c>-server</c>, or the one the current monorepo is a clone of.</summary>
	private static string ServerOf(Invocation invocation)
	{
		if (invocation.Get("server") is { Length: > 0 } server)
		{
			return ServerCredentials.Normalize(server);
		}

		var root = Monorepo.FindRoot(Environment.CurrentDirectory);
		return root is not null && ConfigSnapshot.Load(root).Raw(ConfigLayer.User, ConfigFile.Find("server.url")) is { Length: > 0 } cloned
			? ServerCredentials.Normalize(cloned)
			: throw invocation.Usage("-server is required outside a clone of a Bassia server.");
	}

	// ----- login -----

	public static async Task<int> LoginAsync(Invocation invocation)
	{
		var server = ServerOf(invocation);
		var info = await ServerClient.InfoAsync(server, Http) ?? throw new MonorepoException($"'{server}' is not a Bassia server (no {Api.Prefix}/info).");
		ServerCredential credential;
		if (invocation.Get("token") is { } token)
		{
			credential = new ServerCredential(server, "token", token);
		}
		else
		{
			var provider = ChooseProvider(invocation, info);
			credential = provider.Kind == "github"
				? await GitHubAsync(server, provider)
				: invocation.Get("client-secret") is { } secret
					? await ClientCredentialsAsync(server, provider, invocation.Get("client-id") ?? provider.ClientId ?? throw invocation.Usage("-client-id is required."), secret,
						invocation.Get("scopes") ?? "")
					: await AuthorizationCodeAsync(server, provider, invocation.Has("no-browser"));
		}

		await ServerCredentials.SaveAsync(credential);
		Me me;
		try
		{
			me = await new ServerClient(server, Http).GetAsync<Me>("/me");
		}
		catch (MonorepoException)
		{
			await ServerCredentials.RemoveAsync(server);
			throw;
		}

		return ProgramCli.WriteResult(true, invocation.Command, $"Logged in to {server} as '{me.User}'{(me.Admin ? " (admin)" : "")}.", new Dictionary<string, object?>
		{
			["server"] = server,
			["user"] = me.User,
			["groups"] = me.Groups.ToList(),
			["admin"] = me.Admin,
			["kind"] = credential.Kind
		});
	}

	private static AuthProvider ChooseProvider(Invocation invocation, ServerInfo info)
	{
		if (info.Providers.Count == 0)
		{
			throw new MonorepoException("The server offers no login; ask its admin for a token and log in with -token.");
		}

		if (invocation.Get("provider") is { } name)
		{
			return info.Providers.FirstOrDefault(provider => provider.Name == name)
				?? throw new MonorepoException($"The server has no login '{name}'; it has {string.Join(", ", info.Providers.Select(provider => provider.Name))}.");
		}

		return info.Providers.Count == 1
			? info.Providers[0]
			: throw new MonorepoException($"Choose a login with -provider: {string.Join(", ", info.Providers.Select(provider => provider.Name))}.");
	}

	private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	/// <summary>
	/// OIDC's authorization code flow with PKCE: the browser logs in at the issuer, which sends the code to a listener on
	/// this machine's loopback; the code is exchanged for tokens (with a refresh token, when the issuer grants one).
	/// </summary>
	private static async Task<ServerCredential> AuthorizationCodeAsync(string server, AuthProvider provider, bool noBrowser)
	{
		var clientId = provider.ClientId ?? throw new MonorepoException($"The login '{provider.Name}' names no client for the command line.");
		var (authorize, tokenEndpoint) = await Oidc.DiscoverAsync(Http, provider.Issuer!);
		var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
		var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
		var state = Base64Url(RandomNumberGenerator.GetBytes(16));

		using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		var redirect = $"http://127.0.0.1:{port}/callback/";
		using var listener = new HttpListener();
		listener.Prefixes.Add(redirect);
		listener.Start();

		var url = $"{authorize}{(authorize.Contains('?') ? '&' : '?')}response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
			$"&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString(provider.Scopes ?? "openid")}&state={state}" +
			$"&code_challenge={challenge}&code_challenge_method=S256";
		Console.Error.WriteLine($"Log in to {provider.Name} in your browser: {url}");
		if (!noBrowser)
		{
			WebCommand.OpenBrowser(url);
		}

		var waiting = listener.GetContextAsync();
		if (await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMinutes(5))) != waiting)
		{
			throw new MonorepoException("The login was not completed within 5 minutes.");
		}

		var context = await waiting;
		var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
		var page = Encoding.UTF8.GetBytes("<html><body>Bassia: you are logged in; you can close this window.</body></html>");
		context.Response.ContentType = "text/html";
		await context.Response.OutputStream.WriteAsync(page);
		context.Response.Close();
		if (query["state"] != state || query["code"] is not { } code)
		{
			throw new MonorepoException($"The identity provider did not log you in: {query["error_description"] ?? query["error"] ?? "no code came back"}.");
		}

		return await Oidc.TokenAsync(Http, tokenEndpoint, new Dictionary<string, string>
		{
			["grant_type"] = "authorization_code",
			["code"] = code,
			["redirect_uri"] = redirect,
			["client_id"] = clientId,
			["code_verifier"] = verifier
		}, new ServerCredential(server, "oidc", ClientId: clientId));
	}

	/// <summary>A worker's login: OIDC client credentials, used again for every new token.</summary>
	private static async Task<ServerCredential> ClientCredentialsAsync(string server, AuthProvider provider, string clientId, string secret, string scopes)
	{
		var (_, tokenEndpoint) = await Oidc.DiscoverAsync(Http, provider.Issuer!);
		return await Oidc.TokenAsync(Http, tokenEndpoint, new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = clientId,
			["client_secret"] = secret,
			["scope"] = scopes
		}, new ServerCredential(server, "oidc-client", ClientId: clientId, ClientSecret: secret, Scopes: scopes));
	}

	/// <summary>GitHub's device flow: the user enters a code at GitHub; the GitHub token is exchanged for the server's own.</summary>
	private static async Task<ServerCredential> GitHubAsync(string server, AuthProvider provider)
	{
		var device = provider.DeviceUrl ?? "https://github.com";
		async Task<JsonElement> PostAsync(string path, Dictionary<string, string> form)
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, device + path) { Content = new FormUrlEncodedContent(form) };
			request.Headers.Accept.Add(new("application/json"));
			using var response = await Http.SendAsync(request);
			return await response.Content.ReadFromJsonAsync<JsonElement>();
		}

		var code = await PostAsync("/login/device/code", new() { ["client_id"] = provider.ClientId!, ["scope"] = provider.Scopes ?? "read:user user:email" });
		if (!code.TryGetProperty("device_code", out var deviceCode))
		{
			throw new MonorepoException($"GitHub refused the login: {code}.");
		}

		Console.Error.WriteLine($"Open {code.GetProperty("verification_uri").GetString()} and enter the code {code.GetProperty("user_code").GetString()}.");
		var interval = TimeSpan.FromSeconds(code.TryGetProperty("interval", out var seconds) ? seconds.GetInt32() : 5);
		var deadline = DateTimeOffset.UtcNow.AddSeconds(code.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900);
		while (DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(interval);
			var token = await PostAsync("/login/oauth/access_token", new()
			{
				["client_id"] = provider.ClientId!,
				["device_code"] = deviceCode.GetString()!,
				["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
			});
			if (token.TryGetProperty("access_token", out var access))
			{
				using var response = await Http.PostAsJsonAsync(server + Api.Prefix + "/auth/github", new GitHubLogin(access.GetString()!), Api.Json);
				if (!response.IsSuccessStatusCode)
				{
					throw new MonorepoException($"The server refused the GitHub login: {(await response.Content.ReadFromJsonAsync<ErrorBody>(Api.Json))?.Error}");
				}

				var issued = (await response.Content.ReadFromJsonAsync<TokenResponse>(Api.Json))!;
				return new ServerCredential(server, "server", issued.AccessToken, issued.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(issued.ExpiresIn));
			}

			var error = token.TryGetProperty("error", out var why) ? why.GetString() : null;
			if (error == "slow_down")
			{
				interval += TimeSpan.FromSeconds(5);
			}
			else if (error != "authorization_pending")
			{
				throw new MonorepoException($"GitHub did not log you in: {error ?? token.ToString()}.");
			}
		}

		throw new MonorepoException("The GitHub login was not completed in time.");
	}

	public static async Task<int> LogoutAsync(Invocation invocation)
	{
		var server = ServerOf(invocation);
		var removed = await ServerCredentials.RemoveAsync(server);
		return ProgramCli.WriteResult(true, invocation.Command, removed ? $"Logged out of {server}." : $"There was no login to {server}.",
			new Dictionary<string, object?> { ["server"] = server, ["removed"] = removed });
	}

	// ----- git's credential helper -----

	/// <summary>
	/// <c>bassia credential get</c>, as git calls a credential helper: the request's <c>protocol</c> and <c>host</c> on
	/// standard input, and the token of the server at that address as the password on standard output. <c>store</c> and
	/// <c>erase</c> do nothing: <c>bassia login</c> and <c>logout</c> keep the credentials.
	/// </summary>
	public static async Task<int> CredentialAsync(Invocation invocation)
	{
		if (invocation.Get("operation") != "get")
		{
			return 0;
		}

		var request = new Dictionary<string, string>(StringComparer.Ordinal);
		while (Console.In.ReadLine() is { Length: > 0 } line)
		{
			if (line.IndexOf('=') is var equals and > 0)
			{
				request[line[..equals]] = line[(equals + 1)..];
			}
		}

		var address = $"{request.GetValueOrDefault("protocol")}://{request.GetValueOrDefault("host")}";
		var servers = (await ServerCredentials.ServersAsync()).Where(server => server.StartsWith(address, StringComparison.OrdinalIgnoreCase)).ToList();
		var server = servers.OrderByDescending(server => request.TryGetValue("path", out var path) && (address + "/" + path).StartsWith(server, StringComparison.OrdinalIgnoreCase))
			.FirstOrDefault();
		var token = Environment.GetEnvironmentVariable(ServerCredentials.TokenVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment
			: server is null ? null : await ServerCredentials.AccessTokenAsync(server, Http);
		if (token is not null)
		{
			Console.Out.Write($"username=bassia\npassword={token}\n");
		}

		return 0;
	}

	// ----- worker tokens -----

	public static async Task<int> TokenCreateAsync(Invocation invocation)
	{
		var server = ServerOf(invocation);
		var components = invocation.List("components");
		var minutes = invocation.Int("minutes", 120, 1, 24 * 60);
		var token = await new ServerClient(server, Http).PostAsync<TokenResponse>("/tokens/worker", new WorkerTokenRequest(components, minutes));
		return ProgramCli.WriteResult(true, invocation.Command,
			$"A worker token for {string.Join(", ", components)} ('run' at most), valid for {minutes} minutes. A worker uses it as {ServerCredentials.TokenVariable}.",
			new Dictionary<string, object?>
			{
				["server"] = server,
				["token"] = token.AccessToken,
				["components"] = components.ToList(),
				["expires_in"] = token.ExpiresIn
			});
	}

	// ----- access -----

	public static async Task<int> AccessListAsync(Invocation invocation)
	{
		var server = ServerOf(invocation);
		var client = new ServerClient(server, Http);
		var grants = await client.GetAsync<List<Grant>>("/access/grants");
		var members = await client.GetAsync<List<GroupMember>>("/access/groups");
		var links = await client.GetAsync<List<IdentityLink>>("/access/identities");
		var lines = grants.Select(grant => $"{grant.Principal} {grant.Permission} {grant.Component}")
			.Concat(members.Select(member => $"group:{member.Group} has {member.User}"))
			.Concat(links.Select(link => $"{link.User} is {link.Subject} at {link.Provider}"));
		return ProgramCli.WriteResult(true, invocation.Command, string.Join("\n", lines.DefaultIfEmpty("No grants, groups or identity links.")), new Dictionary<string, object?>
		{
			["grants"] = grants.Select(grant => (object?)new Dictionary<string, object?> { ["principal"] = grant.Principal, ["component"] = grant.Component, ["permission"] = grant.Permission }).ToList(),
			["groups"] = members.Select(member => (object?)new Dictionary<string, object?> { ["group"] = member.Group, ["user"] = member.User }).ToList(),
			["identities"] = links.Select(link => (object?)new Dictionary<string, object?> { ["provider"] = link.Provider, ["subject"] = link.Subject, ["user"] = link.User }).ToList()
		});
	}

	public static async Task<int> AccessGrantAsync(Invocation invocation)
	{
		var grant = new Grant(Principal(invocation.Require("to")), invocation.Get("component") ?? "*", invocation.Require("permission"));
		await new ServerClient(ServerOf(invocation), Http).PostAsync("/access/grants", grant);
		return ProgramCli.WriteResult(true, invocation.Command, $"Granted {grant.Permission} on {grant.Component} to {grant.Principal}.");
	}

	public static async Task<int> AccessRevokeAsync(Invocation invocation)
	{
		var principal = Principal(invocation.Require("from"));
		var component = invocation.Get("component") ?? "*";
		await new ServerClient(ServerOf(invocation), Http).DeleteAsync($"/access/grants?principal={Uri.EscapeDataString(principal)}&component={Uri.EscapeDataString(component)}");
		return ProgramCli.WriteResult(true, invocation.Command, $"Revoked what {principal} was granted on {component}.");
	}

	public static async Task<int> AccessGroupAsync(Invocation invocation)
	{
		var member = new GroupMember(invocation.Require("group"), invocation.Require("user"));
		var client = new ServerClient(ServerOf(invocation), Http);
		if (invocation.Has("remove"))
		{
			await client.DeleteAsync($"/access/groups?group={Uri.EscapeDataString(member.Group)}&user={Uri.EscapeDataString(member.User)}");
			return ProgramCli.WriteResult(true, invocation.Command, $"'{member.User}' is no longer in group '{member.Group}'.");
		}

		await client.PostAsync("/access/groups", member);
		return ProgramCli.WriteResult(true, invocation.Command, $"'{member.User}' is in group '{member.Group}'.");
	}

	public static async Task<int> AccessLinkAsync(Invocation invocation)
	{
		var link = new IdentityLink(invocation.Require("provider"), invocation.Require("subject"), invocation.Require("user"));
		await new ServerClient(ServerOf(invocation), Http).PostAsync("/access/identities", link);
		return ProgramCli.WriteResult(true, invocation.Command, $"'{link.Subject}' at {link.Provider} is '{link.User}'.");
	}

	/// <summary><c>alice</c> is <c>user:alice</c>; <c>group:x</c>, <c>user:x</c> and <c>*</c> stay as they are.</summary>
	private static string Principal(string text) => text == "*" || text.Contains(':') ? text : $"user:{text}";

	public static async Task<int> AuditAsync(Invocation invocation)
	{
		var limit = invocation.Int("limit", 50, 1, 1000);
		var entries = await new ServerClient(ServerOf(invocation), Http).GetAsync<List<AuditEntry>>($"/access/audit?limit={limit}");
		return ProgramCli.WriteResult(true, invocation.Command,
			string.Join("\n", entries.Select(entry => $"{entry.Time} {entry.User} {entry.Action} {entry.Target}{(entry.Detail.Length > 0 ? $" ({entry.Detail})" : "")} from {entry.Remote}")
				.DefaultIfEmpty("The audit log is empty.")),
			new Dictionary<string, object?>
			{
				["entries"] = entries.Select(entry => (object?)new Dictionary<string, object?>
				{
					["seq"] = entry.Seq, ["time"] = entry.Time, ["user"] = entry.User, ["action"] = entry.Action, ["target"] = entry.Target,
					["detail"] = entry.Detail, ["remote"] = entry.Remote
				}).ToList()
			});
	}
}
