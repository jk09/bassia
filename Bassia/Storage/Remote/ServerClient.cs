namespace Bassia.Storage.Remote;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>A server reply that is an error: its HTTP status and the server's message.</summary>
internal sealed class ServerException(HttpStatusCode status, string message, string body = "") : MonorepoException(message)
{
	public HttpStatusCode Status { get; } = status;

	/// <summary>The reply as the server sent it (JSON), for errors that carry more than a message.</summary>
	public string Body { get; } = body;
}

/// <summary>
/// The Bassia server's API as this client calls it: authenticated with the stored credential (see
/// <see cref="ServerCredentials"/>), retried when the network or the server fails for a moment, every write carrying
/// one idempotency key across its retries so it is applied once, and failures turned into errors that say what to do.
/// </summary>
internal sealed class ServerClient
{
	private static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

	private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

	private readonly HttpClient http;

	public ServerClient(string server, HttpClient? http = null)
	{
		Server = ServerCredentials.Normalize(server);
		this.http = http ?? SharedHttp;
	}

	public string Server { get; }

	public HttpClient Http => http;

	public Task<T> GetAsync<T>(string path) => SendAsync<T>(HttpMethod.Get, path, null);

	/// <summary>The reply, or the default when the server has nothing there (404).</summary>
	public async Task<T?> FindAsync<T>(string path)
	{
		try
		{
			return await GetAsync<T>(path);
		}
		catch (ServerException ex) when (ex.Status == HttpStatusCode.NotFound)
		{
			return default;
		}
	}

	public Task<T> PostAsync<T>(string path, object? body) => SendAsync<T>(HttpMethod.Post, path, body);

	public Task PostAsync(string path, object? body) => SendAsync<JsonElement?>(HttpMethod.Post, path, body);

	public Task DeleteAsync(string path) => SendAsync<JsonElement?>(HttpMethod.Delete, path, null);

	public Task PutFileAsync(string path, string file) => SendAsync<JsonElement?>(HttpMethod.Put, path, null, file);

	/// <summary>Downloads to <paramref name="file"/>; false when the server has nothing there.</summary>
	public async Task<bool> GetFileAsync(string path, string file)
	{
		using var response = await SendRawAsync(HttpMethod.Get, path, null, null);
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			return false;
		}

		await ThrowUnlessSuccessAsync(response);
		await using var target = File.Create(file);
		await response.Content.CopyToAsync(target);
		return true;
	}

	/// <summary>The server's description; unauthenticated, so it tells whether <paramref name="url"/> is a Bassia server at all.</summary>
	public static async Task<ServerInfo?> InfoAsync(string url, HttpClient? http = null)
	{
		try
		{
			var info = await (http ?? SharedHttp).GetFromJsonAsync<ServerInfo>(ServerCredentials.Normalize(url) + Api.Prefix + "/info", Api.Json);
			return info?.Name == "bassia" ? info : null;
		}
		catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException or InvalidOperationException)
		{
			return null;
		}
	}

	private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? file = null)
	{
		using var response = await SendRawAsync(method, path, body, file);
		await ThrowUnlessSuccessAsync(response);
		if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
		{
			return default!;
		}

		return (await response.Content.ReadFromJsonAsync<T>(Api.Json))!;
	}

	private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, string? file)
	{
		var key = method == HttpMethod.Get ? null : Guid.NewGuid().ToString("N");
		for (var attempt = 0; ; attempt++)
		{
			using var request = new HttpRequestMessage(method, Server + Api.Prefix + path);
			if (await ServerCredentials.AccessTokenAsync(Server, http) is { } token)
			{
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			}

			if (key is not null)
			{
				request.Headers.Add(Api.IdempotencyHeader, key);
			}

			if (file is not null)
			{
				request.Content = new StreamContent(File.OpenRead(file));
			}
			else if (body is not null)
			{
				request.Content = JsonContent.Create(body, body.GetType(), options: Api.Json);
			}

			try
			{
				var response = await http.SendAsync(request);
				if (((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests) && attempt < Backoff.Length)
				{
					response.Dispose();
					await Task.Delay(Backoff[attempt]);
					continue;
				}

				return response;
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
			{
				if (attempt >= Backoff.Length)
				{
					throw new MonorepoException($"Cannot reach the Bassia server at {Server}: {ex.Message}");
				}

				await Task.Delay(Backoff[attempt]);
			}
		}
	}

	private async Task ThrowUnlessSuccessAsync(HttpResponseMessage response)
	{
		if (response.IsSuccessStatusCode)
		{
			return;
		}

		var text = await response.Content.ReadAsStringAsync();
		string message;
		try
		{
			message = JsonSerializer.Deserialize<ErrorBody>(text, Api.Json)?.Error ?? text;
		}
		catch (JsonException)
		{
			message = text;
		}

		if (response.StatusCode == HttpStatusCode.Unauthorized)
		{
			message = $"The Bassia server at {Server} did not accept the credential ({message.TrimEnd('.')}); log in with 'bassia login -server {Server}'.";
		}

		throw new ServerException(response.StatusCode, message.Length > 0 ? message : $"The Bassia server at {Server} answered {(int)response.StatusCode} {response.ReasonPhrase}.", text);
	}
}
