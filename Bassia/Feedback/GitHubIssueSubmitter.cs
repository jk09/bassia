namespace Bassia.Feedback;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// Files issues through GitHub's REST API with the token in <c>BASSIA_FEEDBACK_TOKEN</c> (else <c>GITHUB_TOKEN</c>).
/// A crash whose fingerprint an open issue already carries is added to that issue as a comment.
/// </summary>
internal sealed class GitHubIssueSubmitter : IIssueSubmitter
{
	private static readonly Uri Api = new("https://api.github.com/");
	private static readonly string[] TokenVariables = ["BASSIA_FEEDBACK_TOKEN", "GITHUB_TOKEN"];

	private readonly HttpClient client;
	private readonly string? token = Token();

	public GitHubIssueSubmitter(HttpMessageHandler? handler = null)
	{
		client = handler is null ? new HttpClient() : new HttpClient(handler);
		client.BaseAddress = Api;
		client.Timeout = TimeSpan.FromSeconds(15);
	}

	public bool IsConfigured => token is not null;

	public static string? Token() => TokenVariables
		.Select(Environment.GetEnvironmentVariable)
		.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

	public async Task<IssueReceipt> SubmitAsync(IssueDraft draft, CancellationToken cancellation)
	{
		if (token is null)
		{
			throw new FeedbackException($"No GitHub token: set {string.Join(" or ", TokenVariables)} to a token that may create issues in {draft.Repository}.");
		}

		try
		{
			if (draft.Fingerprint is not null && await FindOpenAsync(draft, cancellation) is { } existing)
			{
				await SendAsync(HttpMethod.Post, $"repos/{draft.Repository}/issues/{existing.Number}/comments",
					new { body = $"Reported again.\n\n{draft.Body}" }, cancellation);
				return existing with { Duplicate = true };
			}

			using var created = await SendAsync(HttpMethod.Post, $"repos/{draft.Repository}/issues",
				new { title = draft.Title, body = draft.Body, labels = draft.Labels }, cancellation);
			return new IssueReceipt(created.RootElement.GetProperty("number").GetInt32(), created.RootElement.GetProperty("html_url").GetString()!, false);
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
		{
			throw new FeedbackException($"Could not reach GitHub: {ex.Message}", ex);
		}
	}

	private async Task<IssueReceipt?> FindOpenAsync(IssueDraft draft, CancellationToken cancellation)
	{
		var query = Uri.EscapeDataString($"repo:{draft.Repository} is:issue is:open in:body \"{draft.Fingerprint}\"");
		using var found = await SendAsync(HttpMethod.Get, $"search/issues?q={query}&per_page=1", null, cancellation);
		var items = found.RootElement.GetProperty("items");
		return items.GetArrayLength() == 0
			? null
			: new IssueReceipt(items[0].GetProperty("number").GetInt32(), items[0].GetProperty("html_url").GetString()!, true);
	}

	private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? payload, CancellationToken cancellation)
	{
		using var request = new HttpRequestMessage(method, path);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		request.Headers.UserAgent.Add(new ProductInfoHeaderValue("bassia", ReportBuilder.Version().Split('+')[0]));
		request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
		if (payload is not null)
		{
			request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
		}

		using var response = await client.SendAsync(request, cancellation);
		var text = await response.Content.ReadAsStringAsync(cancellation);
		if (!response.IsSuccessStatusCode)
		{
			var detail = TryMessage(text);
			throw new FeedbackException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}{(detail is null ? "" : $": {detail}")}.");
		}

		return JsonDocument.Parse(text);
	}

	private static string? TryMessage(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			return document.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
