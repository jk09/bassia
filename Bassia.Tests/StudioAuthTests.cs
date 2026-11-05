namespace Bassia.Tests;

using System.Net;
using System.Security.Claims;
using System.Net.Http.Json;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Server;
using Bassia.Studio;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Studio with Microsoft Entra ID sign-in, without Entra: anonymous requests are sent to sign in, and a signed-in
/// session (a cookie minted with Studio's own keys, as the OpenID Connect handler would leave it) acts as its person,
/// with the role of its strongest app role.
/// </summary>
public sealed class StudioAuthTests : IAsyncLifetime
{
	private readonly TempDirectory temp = new();
	private ServerApp server = null!;
	private StudioApp studio = null!;

	public async Task InitializeAsync()
	{
		server = new ServerApp(new ServerOptions { Data = temp.Combine("server"), Url = "http://127.0.0.1:0", Chunks = new ChunkParameters(1024, 4096, 16384) });
		await server.StartAsync();
		studio = new StudioApp(new StudioOptions
		{
			Server = server.Address!,
			Url = "http://127.0.0.1:0",
			Cache = temp.Combine("cache"),
			Auth = "entra",
			EntraTenant = "00000000-0000-0000-0000-000000000001",
			EntraClientId = "00000000-0000-0000-0000-000000000002",
			EntraClientSecret = "not-used-here"
		});
		await studio.StartAsync();
	}

	public async Task DisposeAsync()
	{
		await studio.DisposeAsync();
		await server.DisposeAsync();
		temp.Dispose();
	}

	private HttpClient Client(string? cookie = null)
	{
		var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
		var http = new HttpClient(handler) { BaseAddress = new Uri(studio.Address!) };
		if (cookie is not null)
		{
			http.DefaultRequestHeaders.Add("Cookie", "bassia_studio=" + cookie);
		}

		return http;
	}

	private string SessionCookie(params Claim[] claims)
	{
		var options = studio.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
		var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "OpenIdConnect", "preferred_username", "roles"));
		var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
			CookieAuthenticationDefaults.AuthenticationScheme);
		return options.TicketDataFormat.Protect(ticket);
	}

	[Fact]
	public async Task Anonymous_IsSentToSignIn_ButHealthAndJobReportsAreNot()
	{
		using var http = Client();
		using var page = await http.GetAsync("/log?from=3");
		Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
		Assert.Equal("/signin", page.Headers.Location!.AbsolutePath);
		Assert.Contains("ReturnUrl=%2Flog%3Ffrom%3D3", page.Headers.Location!.Query);

		using var raw = await http.GetAsync("/raw?path=a.txt");
		Assert.Equal(HttpStatusCode.Redirect, raw.StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/healthz")).StatusCode);
		using var report = await http.PostAsync("/runs/report/run-x", JsonContent.Create(new { lines = new[] { "x" } }));
		Assert.Equal(HttpStatusCode.Unauthorized, report.StatusCode);
	}

	[Fact]
	public async Task SignedIn_ActsAsThePersonWithTheirRole()
	{
		var cookie = SessionCookie(new Claim("preferred_username", "ann@contoso.example"), new Claim("roles", "Reader"), new Claim("roles", "Contributor"));
		using var http = Client(cookie);
		using var response = await http.GetAsync("/workspaces/new");
		var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("ann@contoso.example · contributor", body);
		Assert.Contains("Sign out", body);
		Assert.Contains("Create", body);

		var reader = SessionCookie(new Claim("preferred_username", "rita@contoso.example"));
		using var readerHttp = Client(reader);
		var readerBody = WebUtility.HtmlDecode(await readerHttp.GetStringAsync("/workspaces/new"));
		Assert.Contains("rita@contoso.example · reader", readerBody);
		Assert.Contains("is a reader and cannot create workspaces", readerBody);
	}

	[Fact]
	public void Claims_MapToTheStrongestAppRole()
	{
		ClaimsPrincipal Principal(bool authenticated, params Claim[] claims) => new(new ClaimsIdentity(claims, authenticated ? "test" : null));
		Assert.Equal(new StudioUser("a@x", StudioRole.Admin),
			StudioClaims.From(Principal(true, new Claim("preferred_username", "a@x"), new Claim("roles", "contributor"), new Claim("roles", "Admin"))));
		Assert.Equal(new StudioUser("Bea", StudioRole.Reader), StudioClaims.From(Principal(true, new Claim("name", "Bea"), new Claim("roles", "Unknown"))));
		Assert.Equal(StudioRole.Reader, StudioClaims.From(Principal(false, new Claim("preferred_username", "x"), new Claim("roles", "Admin"))).Role);
		Assert.Equal(StudioRole.Contributor, StudioClaims.From(Principal(true, new Claim(ClaimTypes.Role, "Contributor"))).Role);
	}

	[Fact]
	public async Task Entra_NeedsATenantAndAClient()
	{
		await using var broken = new StudioApp(new StudioOptions { Server = server.Address!, Url = "http://127.0.0.1:0", Auth = "entra", EntraClientId = "x" });
		await Assert.ThrowsAsync<ArgumentException>(broken.StartAsync);
		var parsed = StudioOptions.Parse(["-server", "http://s", "-auth", "entra", "-entra-tenant", "t", "-entra-client-id", "c"]);
		Assert.True(parsed.UsesEntra);
		Assert.Equal("t", parsed.EntraTenant);
	}
}
