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

	private Task<HttpResponseMessage> SignInAsync(string url, string token) =>
		http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));

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
			Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(url + "/api/v1/main")).StatusCode);
			Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(url + "/")).StatusCode);
			Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(url + "/fragment/live", "text/html")).StatusCode);
			Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(url + "/events", "text/event-stream")).StatusCode);
			Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(url + "/api/v1/main", bearer: "wrong")).StatusCode);
			Assert.Equal(HttpStatusCode.Redirect, (await GetAsync(url + "/", "text/html", cookie: "bassia_token=wrong")).StatusCode);

			var page = await GetAsync(url + "/mainline?patch", "text/html,*/*");
			Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
			Assert.StartsWith("/login?next=%2Fmainline%3Fpatch", page.Headers.Location!.OriginalString);

			Assert.Equal(HttpStatusCode.OK, (await GetAsync(url + "/healthz")).StatusCode);
			Assert.Equal(HttpStatusCode.OK, (await GetAsync(url + "/style.css")).StatusCode);
			Assert.Equal(HttpStatusCode.OK, (await GetAsync(url + "/api/v1/main", bearer: token)).StatusCode);
		}

		await using var again = await StartAsync(Options());
		Assert.Equal(token, File.ReadAllText(again.TokenFile));
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(again.Address + "/api/v1/main", bearer: token)).StatusCode);
	}

	[PostgresFact]
	public async Task TheSignInPage_TakesTheToken_AndLeavesACookie()
	{
		await using var server = await StartAsync(Options() with { Token = "s3cret" });
		var url = server.Address!;
		Assert.False(File.Exists(server.TokenFile));

		var form = await GetAsync(url + "/login", "text/html");
		var html = await form.Content.ReadAsStringAsync();
		Assert.Equal(HttpStatusCode.OK, form.StatusCode);
		Assert.Contains("<form method=\"post\"", html);
		Assert.DoesNotContain("s3cret", html);

		var refused = await SignInAsync(url + "/login?next=%2Fmainline", "wrong");
		Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
		Assert.False(refused.Headers.Contains("Set-Cookie"));

		var signedIn = await SignInAsync(url + "/login?next=%2Fmainline", "s3cret");
		Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
		Assert.Equal("/mainline", signedIn.Headers.Location!.OriginalString);
		var cookie = Assert.Single(signedIn.Headers.GetValues("Set-Cookie"));
		Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

		var session = cookie.Split(';')[0];
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(url + "/mainline", "text/html", cookie: session)).StatusCode);
		Assert.Equal(HttpStatusCode.Redirect, (await GetAsync(url + "/login", "text/html", cookie: session)).StatusCode);
	}

	[PostgresTheory]
	[InlineData("https%3A%2F%2Fevil.example")]
	[InlineData("%2F%2Fevil.example")]
	[InlineData("%2F%5Cevil.example")]
	public async Task SigningIn_NeverRedirectsToAnotherSite(string next)
	{
		await using var server = await StartAsync(Options() with { Token = "s3cret" });
		var signedIn = await SignInAsync($"{server.Address}/login?next={next}", "s3cret");
		Assert.Equal("/", signedIn.Headers.Location!.OriginalString);
	}

	[PostgresFact]
	public async Task ATokenInTheUrl_SignsInAndLeavesTheUrl()
	{
		await using var server = await StartAsync(Options() with { Token = "s3cret" });
		var response = await GetAsync(server.Address + "/mainline?token=s3cret&patch=1", "text/html");
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal("/mainline?patch=1", response.Headers.Location!.OriginalString);
		Assert.Contains("bassia_token=s3cret", Assert.Single(response.Headers.GetValues("Set-Cookie")));

		var wrong = await GetAsync(server.Address + "/mainline?token=wrong", "text/html");
		Assert.Equal(HttpStatusCode.Redirect, wrong.StatusCode);
		Assert.DoesNotContain("wrong", wrong.Headers.Location!.OriginalString);
		Assert.False(wrong.Headers.Contains("Set-Cookie"));
	}

	[PostgresFact]
	public async Task NoAuth_IsOpen_AndWritesNoToken()
	{
		await using var server = await StartAsync(Options() with { NoAuth = true });
		Assert.False(File.Exists(server.TokenFile));
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(server.Address + "/api/v1/main")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(server.Address + "/", "text/html")).StatusCode);
		Assert.Equal(HttpStatusCode.Redirect, (await GetAsync(server.Address + "/login", "text/html")).StatusCode);
	}

	[Fact]
	public void NoAuth_AndAToken_AreRefused()
	{
		Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["-data", temp.Path, "-no-auth", "-token", "t"]));
		Assert.True(ServerOptions.Parse(["-data", temp.Path, "-no-auth"]).NoAuth);
		Assert.False(ServerOptions.Parse(["-data", temp.Path]).NoAuth);
	}
}
