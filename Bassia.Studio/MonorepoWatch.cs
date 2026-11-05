namespace Bassia.Studio;

using System.Net.Http.Headers;
using System.Text.Json;
using Bassia.Stores;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Follows the server's change stream (<c>/api/v1/watch</c>: landings and workspace changes) and tells open pages, so
/// they refresh without a reload; a tick every <see cref="TickInterval"/> covers what the stream does not carry (leases
/// granted, released or expired). Reconnects after a lost connection.
/// </summary>
public sealed class MonorepoWatch(StudioOptions options) : BackgroundService
{
	public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

	/// <summary>A change on the server (kind <c>landed</c> or <c>workspace</c>), or a <c>tick</c>.</summary>
	public event Action<Api.WatchEvent>? Changed;

	/// <summary>Whether the stream is connected now.</summary>
	public bool Connected { get; private set; }

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var ticking = TickAsync(stoppingToken);
		using var http = new HttpClient { BaseAddress = new Uri(options.Server.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
		if (options.Token is { } token)
		{
			http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}

		var delay = TimeSpan.FromSeconds(1);
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				using var response = await http.GetAsync(Api.Prefix.TrimStart('/') + "/watch", HttpCompletionOption.ResponseHeadersRead, stoppingToken);
				response.EnsureSuccessStatusCode();
				using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stoppingToken));
				Connected = true;
				delay = TimeSpan.FromSeconds(1);
				while (await reader.ReadLineAsync(stoppingToken) is { } line)
				{
					if (line.StartsWith("data:", StringComparison.Ordinal)
						&& JsonSerializer.Deserialize<Api.WatchEvent>(line[5..].Trim(), Api.Json) is { } change)
					{
						Raise(change);
					}
				}
			}
			catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException && !stoppingToken.IsCancellationRequested)
			{
			}
			catch (OperationCanceledException)
			{
				break;
			}

			Connected = false;
			try
			{
				await Task.Delay(delay, stoppingToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
		}

		await ticking;
	}

	private async Task TickAsync(CancellationToken ct)
	{
		try
		{
			while (true)
			{
				await Task.Delay(TickInterval, ct);
				Raise(new Api.WatchEvent("tick", Time: DateTimeOffset.UtcNow));
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private void Raise(Api.WatchEvent change)
	{
		try
		{
			Changed?.Invoke(change);
		}
		catch (Exception)
		{
			// A page that failed to refresh must not end the watch.
		}
	}
}
