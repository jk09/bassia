namespace Bassia.Studio;

using Bassia.Studio.Components;
using Bassia.Studio.Runs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

/// <summary>
/// Bassia Studio: an ASP.NET Core (Blazor Server) web client for a <c>bassia-server</c>, in the role of a desktop
/// version-control client (TortoiseSVN, P4V) but in the browser. It reads and changes the monorepo through the same
/// core operations as the <c>bassia</c> command line, over the server's API, as a clone does.
/// </summary>
public sealed class StudioApp(StudioOptions options) : IAsyncDisposable
{
	private WebApplication? app;

	public StudioOptions Options => options;

	/// <summary>Where Studio listens once started (the actual port when the options asked for port 0).</summary>
	public string? Address { get; private set; }

	public async Task StartAsync()
	{
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			ContentRootPath = AppContext.BaseDirectory,
			ApplicationName = typeof(StudioApp).Assembly.GetName().Name, // its static-asset manifests, also when hosted by tests
		});
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls(options.Url);
		builder.WebHost.UseStaticWebAssets();
		builder.Services.AddSingleton(options);
		builder.Services.AddSingleton<MonorepoClient>();
		builder.Services.AddSingleton<MonorepoWatch>();
		builder.Services.AddHostedService(services => services.GetRequiredService<MonorepoWatch>());
		if (options.UsesEntra)
		{
			AddEntraSignIn(builder.Services, options);
			// Each circuit (and each prerendered request) acts as the person signed in.
			builder.Services.AddScoped(services => StudioClaims.From(
				services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync().GetAwaiter().GetResult().User));
		}
		else
		{
			builder.Services.AddSingleton(new StudioUser(options.User, options.Role));
		}

		builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
		{
			// Behind Azure Container Apps' ingress: the scheme and host the browser used (sign-in redirects depend on them).
			forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
			forwarded.KnownIPNetworks.Clear();
			forwarded.KnownProxies.Clear();
		});
		builder.Services.AddScoped<StudioActions>();
		builder.Services.AddSingleton(services => new RunService(services.GetRequiredService<MonorepoClient>(), options, CreateRunHost(options)));
		builder.Services.AddRazorComponents().AddInteractiveServerComponents();
		app = builder.Build();
		app.UseForwardedHeaders();
		app.UseExceptionHandler("/error", createScopeForErrors: true);
		if (options.UsesEntra)
		{
			app.UseAuthentication();
			app.UseAuthorization();
		}

		app.UseAntiforgery();
		app.MapStaticAssets();
		app.MapGet("/healthz", () => Results.Text("ok"));
		// Jobs report with their run's own token, not as a person.
		app.MapPost("/runs/report/{id}", Report).AllowAnonymous();
		var raw = app.MapGet("/raw", RawAsync);
		var pages = app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
		if (options.UsesEntra)
		{
			raw.RequireAuthorization();
			pages.RequireAuthorization();
			MapSignIn(app);
		}
		await app.StartAsync();
		Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
		Runs.ReportBase = () => options.PublicUrl ?? Address ?? options.Url;
	}

	/// <summary>
	/// Sign-in with Microsoft Entra ID: OpenID Connect (authorization code flow) into a cookie session. A request without
	/// a session goes to <c>/signin</c>, which starts the Entra ID sign-in; roles come from the app registration's app roles.
	/// </summary>
	private static void AddEntraSignIn(IServiceCollection services, StudioOptions options)
	{
		var tenant = options.EntraTenant ?? throw new ArgumentException("-auth entra needs -entra-tenant <tenant id>.");
		var clientId = options.EntraClientId ?? throw new ArgumentException("-auth entra needs -entra-client-id <app id>.");
		services.AddAuthentication(auth =>
			{
				auth.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
				auth.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
			})
			.AddCookie(cookie =>
			{
				cookie.LoginPath = "/signin";
				cookie.Cookie.Name = "bassia_studio";
				cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
				cookie.Cookie.SameSite = SameSiteMode.Lax;
				cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
				cookie.SlidingExpiration = true;
			})
			.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, oidc =>
			{
				oidc.Authority = options.EntraInstance.TrimEnd('/') + "/" + tenant + "/v2.0";
				oidc.ClientId = clientId;
				oidc.ClientSecret = options.EntraClientSecret;
				oidc.ResponseType = OpenIdConnectResponseType.Code;
				oidc.UsePkce = true;
				oidc.SaveTokens = false;
				oidc.Scope.Clear();
				oidc.Scope.Add("openid");
				oidc.Scope.Add("profile");
				oidc.Scope.Add("email");
				oidc.MapInboundClaims = false;
				oidc.TokenValidationParameters.NameClaimType = "preferred_username";
				oidc.TokenValidationParameters.RoleClaimType = "roles";
			});
		services.AddAuthorization();
		services.AddCascadingAuthenticationState();
	}

	private static void MapSignIn(WebApplication app)
	{
		app.MapGet("/signin", (string? returnUrl) => Results.Challenge(
			new AuthenticationProperties { RedirectUri = returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/" },
			[OpenIdConnectDefaults.AuthenticationScheme])).AllowAnonymous();
		app.MapGet("/signout", () => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
			[CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme])).RequireAuthorization();
	}

	/// <summary>Studio's services (tests mint sign-in cookies with them).</summary>
	internal IServiceProvider Services => app?.Services ?? throw new InvalidOperationException("Start Studio first.");

	/// <summary>The server's change stream as Studio follows it.</summary>
	public MonorepoWatch Watch => app?.Services.GetRequiredService<MonorepoWatch>() ?? throw new InvalidOperationException("Start Studio first.");

	/// <summary>The runs Studio starts and follows.</summary>
	public RunService Runs => app?.Services.GetRequiredService<RunService>() ?? throw new InvalidOperationException("Start Studio first.");

	/// <summary>The run host the options name.</summary>
	public static IRunHost? CreateRunHost(StudioOptions options) => options.RunHost.ToLowerInvariant() switch
	{
		"none" or "" => null,
		"local" => new LocalProcessRunHost(options.BassiaCommand(), options.Runs),
		"aca" => new ContainerAppsJobRunHost(new ContainerAppsJobOptions(
			options.AcaJob ?? throw new ArgumentException("-run-host aca needs -aca-job <job resource id>."),
			options.AcaImage ?? throw new ArgumentException("-run-host aca needs -aca-image <worker image>."),
			Cpu: options.AcaCpu,
			Memory: options.AcaMemory,
			SecretEnvironment: options.AcaSecretEnvironment())),
		var other => throw new ArgumentException($"-run-host is none, local or aca, not '{other}'.")
	};

	/// <summary>A job's report of its run's output and status; answers whether the run should stop.</summary>
	private static IResult Report(string id, RunReport report, HttpRequest request, RunService runs)
	{
		var header = request.Headers.Authorization.ToString();
		var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null;
		return runs.Report(id, token, report, out var stop) ? Results.Json(new { stop }) : Results.Unauthorized();
	}

	/// <summary>Downloads a file's content at a mainline seq or in a workspace.</summary>
	private static async Task<IResult> RawAsync(MonorepoClient client, string path, long? seq, string? workspace, CancellationToken ct)
	{
		var tree = await client.TreeAsync(seq, workspace, ct);
		var leaf = await client.Repo.Trees.GetAsync(tree, path, ct);
		if (leaf is null || !leaf.IsFile)
		{
			return Results.NotFound();
		}

		var bytes = await client.Repo.Objects.ReadContentAsync(leaf, ct);
		return Results.File(bytes, "application/octet-stream", Path.GetFileName(path));
	}

	public Task WaitForShutdownAsync() => app?.WaitForShutdownAsync() ?? Task.CompletedTask;

	public async ValueTask DisposeAsync()
	{
		if (app is not null)
		{
			await app.DisposeAsync();
		}
	}
}
