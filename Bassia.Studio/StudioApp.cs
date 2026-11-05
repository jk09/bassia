namespace Bassia.Studio;

using Bassia.Studio.Components;
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
		builder.Services.AddRazorComponents().AddInteractiveServerComponents();
		app = builder.Build();
		app.UseExceptionHandler("/error", createScopeForErrors: true);
		app.UseAntiforgery();
		app.MapStaticAssets();
		app.MapGet("/healthz", () => Results.Text("ok"));
		app.MapGet("/raw", RawAsync);
		app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
		await app.StartAsync();
		Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
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
