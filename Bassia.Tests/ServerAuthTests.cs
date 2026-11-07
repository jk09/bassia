namespace Bassia.Tests;

using System.Net;
using Bassia.Server;

/// <summary>A server is closed unless it is told otherwise: connecting to its port gets no one in (bassia-edb66a7f).</summary>
public sealed class ServerAuthTests : IDisposable
{
	private readonly TempDirectory temp = new();
	private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

	public void Dispose()
	{
		http.Dispose();
		temp.Dispose();
	}

	private ServerOptions Options() => new() { Data = temp.Combine("server"), Postgres = Backends.RequiredPostgres(), Url = "http://127.0.0.1:0" };

	private async Task<ServerApp> StartAsync(ServerOptions options)
	{
		var server = new ServerApp(options);
		await server.StartAsync();
		return server;
	}

	private Task<HttpResponseMessage> GetAsync(string url, string? accept = null, string? bearer = null, string? cookie = null)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, url);
		if (accept is not null) { request.Headers.TryAddWithoutValidation("Accept", accept); }
		if (bearer is not null) { request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer); }
		if (cookie is not null) { request.Headers.TryAddWithoutValidation("Cookie", cookie); }
		return http.SendAsync(request);
	}

	[PostgresFact]
	public async Task WithoutAToken_TheServerGeneratesOne_AndConnectingGetsNoOneIn()
	{
		string token;
		await using (var server = await StartAsync(Options()))
		{
			token = File.ReadAllText(server.TokenFile);
			Assert.Equal(64, token.Length);
			if (!OperatingSystem.IsWindows())
			{
				Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(server.TokenFile));
			}

			var url = server.Address!;
			var refused = await GetAsync(url + "/api/v1/main");
			Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
			Assert.Equal("Bearer", refused.Headers.WwwAuthenticate.ToString());
			Assert.Contains("\"error\"", await refused.Content.ReadAsStringAsync());
			Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(url + "/api/v1/watch", "text/event-stream")).StatusCode);
			Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(url + "/api/v1/main", bearer: "wrong")).StatusCode);

			Assert.Equal(HttpStatusCode.OK, (await GetAsync(url + "/healthz")).StatusCode);
			Assert.Equal(HttpStatusCode.OK, (await GetAsync(url + "/api/v1/main", bearer: token)).StatusCode);
		}

		await using var again = await StartAsync(Options());
		Assert.Equal(token, File.ReadAllText(again.TokenFile));
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(again.Address + "/api/v1/main", bearer: token)).StatusCode);
	}

	/// <summary>The bearer token is the only credential: the cookie and query token of the removed dashboard get no one in (bassia-de3319ff).</summary>
	[PostgresFact]
	public async Task OnlyTheBearerToken_Authenticates()
	{
		await using var server = await StartAsync(Options() with { Token = "s3cret" });
		var url = server.Address!;
		Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(url + "/api/v1/main?token=s3cret")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(url + "/api/v1/main", cookie: "bassia_token=s3cret")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(url + "/api/v1/main", "text/html")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(url + "/api/v1/main", bearer: "s3cret")).StatusCode);
	}

	/// <summary>No pages, no sign-in: the server is an API, and its root says so to anyone (bassia-de3319ff).</summary>
	[PostgresFact]
	public async Task TheServer_ServesNoHtml_OnlyTheApi()
	{
		await using var server = await StartAsync(Options() with { Token = "s3cret" });
		var url = server.Address!;
		var root = await GetAsync(url + "/", "text/html");
		Assert.Equal(HttpStatusCode.OK, root.StatusCode);
		Assert.Equal("application/json", root.Content.Headers.ContentType!.MediaType);
		var description = await root.Content.ReadAsStringAsync();
		Assert.Contains("\"service\":\"bassia-server\"", description);
		Assert.Contains("\"api\":\"/api/v1\"", description);

		foreach (var path in new[] { "/mainline", "/workspaces", "/fragment/live", "/style.css", "/app.js", "/login", "/events", "/api/v1/nothing" })
		{
			foreach (var bearer in new[] { null, "s3cret" })
			{
				var response = await GetAsync(url + path, "text/html", bearer);
				var expected = bearer is null ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound;
				Assert.True(expected == response.StatusCode, $"{path} ({(bearer is null ? "no token" : "token")}): {response.StatusCode}");
				Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
				Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync());
			}
		}

		var login = await http.PostAsync(url + "/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "s3cret" }));
		Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
		Assert.False(login.Headers.Contains("Set-Cookie"));
	}

	[PostgresFact]
	public async Task CorsOrigins_LetListedBrowserAppsCallTheApi()
	{
		await using var server = await StartAsync(Options() with { Token = "s3cret", CorsOrigins = ["https://studio.example"] });
		var url = server.Address!;

		var preflight = new HttpRequestMessage(HttpMethod.Options, url + "/api/v1/land");
		preflight.Headers.Add("Origin", "https://studio.example");
		preflight.Headers.Add("Access-Control-Request-Method", "POST");
		preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
		var allowed = await http.SendAsync(preflight);
		Assert.True(allowed.IsSuccessStatusCode, allowed.StatusCode.ToString());
		Assert.Equal("https://studio.example", Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));

		var call = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1/main");
		call.Headers.Add("Origin", "https://studio.example");
		call.Headers.TryAddWithoutValidation("Authorization", "Bearer s3cret");
		var reply = await http.SendAsync(call);
		Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
		Assert.Equal("https://studio.example", Assert.Single(reply.Headers.GetValues("Access-Control-Allow-Origin")));

		var other = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1/main");
		other.Headers.Add("Origin", "https://evil.example");
		other.Headers.TryAddWithoutValidation("Authorization", "Bearer s3cret");
		Assert.False((await http.SendAsync(other)).Headers.Contains("Access-Control-Allow-Origin"));
	}

	[PostgresFact]
	public async Task WithoutCorsOrigins_NoCorsHeaders()
	{
		await using var server = await StartAsync(Options() with { Token = "s3cret" });
		var call = new HttpRequestMessage(HttpMethod.Get, server.Address + "/api/v1/main");
		call.Headers.Add("Origin", "https://studio.example");
		call.Headers.TryAddWithoutValidation("Authorization", "Bearer s3cret");
		Assert.False((await http.SendAsync(call)).Headers.Contains("Access-Control-Allow-Origin"));
	}

	[Fact]
	public void CorsOrigins_AreParsed_AndWildcardsRefused()
	{
		Assert.Equal(["https://a.example", "http://localhost:5173"],
			ServerOptions.Parse(["-data", temp.Path, "-cors-origin", "https://a.example/, http://localhost:5173"]).CorsOrigins);
		Assert.Empty(ServerOptions.Parse(["-data", temp.Path]).CorsOrigins);
		Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["-data", temp.Path, "-cors-origin", "*"]));
		Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["-data", temp.Path, "-cors-origin", "https://a.example/app"]));
		Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["-data", temp.Path, "-cors-origin", "ftp://a.example"]));
	}

	[PostgresFact]
	public async Task NoAuth_IsOpen_AndWritesNoToken()
	{
		await using var server = await StartAsync(Options() with { NoAuth = true });
		Assert.False(File.Exists(server.TokenFile));
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(server.Address + "/api/v1/main")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(server.Address + "/")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(server.Address + "/login", "text/html")).StatusCode);
	}

	[Fact]
	public void NoAuth_AndAToken_AreRefused()
	{
		Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["-data", temp.Path, "-no-auth", "-token", "t"]));
		Assert.True(ServerOptions.Parse(["-data", temp.Path, "-no-auth"]).NoAuth);
		Assert.False(ServerOptions.Parse(["-data", temp.Path]).NoAuth);
	}
}
