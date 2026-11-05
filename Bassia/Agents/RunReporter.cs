namespace Bassia.Agents;

using System.Net.Http.Json;
using System.Text.Json;

/// <summary>
/// Reports a run's output, as it happens, to whoever started it remotely (Bassia Studio): when
/// <c>BASSIA_REPORT_URL</c> is set, output lines are posted in batches with the bearer token in
/// <c>BASSIA_REPORT_TOKEN</c>, and the run ends with a final report of its status. The answer to each post can ask the
/// run to stop; it then stops as <c>bassia agent stop</c> would, with what the agent changed snapshotted.
/// Reporting is best effort: an unreachable reporter never fails the run.
/// </summary>
internal sealed class RunReporter : IAsyncDisposable
{
	public const string UrlVariable = "BASSIA_REPORT_URL";
	public const string TokenVariable = "BASSIA_REPORT_TOKEN";

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
	private readonly HttpClient http;
	private readonly string url;
	private readonly string runId;
	private readonly List<string> pending = [];
	private readonly Lock gate = new();
	private readonly CancellationTokenSource done = new();
	private Task? pump;
	private string? workspace;

	private RunReporter(string url, string? token, string runId)
	{
		this.url = url;
		this.runId = runId;
		http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
		if (token is { Length: > 0 })
		{
			http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
		}
	}

	/// <summary>A reporter when the environment asks for one.</summary>
	public static RunReporter? FromEnvironment(string runId) =>
		Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } url ? new RunReporter(url, Environment.GetEnvironmentVariable(TokenVariable), runId) : null;

	/// <summary>The body of a report.</summary>
	public sealed record Report(string RunId, string? Workspace, IReadOnlyList<string> Lines, string? Status = null, bool Done = false,
		string? Message = null);

	/// <summary>The answer to a report.</summary>
	public sealed record Answer(bool Stop);

	/// <summary>Starts posting: every half second while output is pending; <paramref name="stop"/> is cancelled when asked.</summary>
	public void Start(string workspaceId, CancellationTokenSource stop)
	{
		workspace = workspaceId;
		pump = PumpAsync(stop);
	}

	public void Line(string text)
	{
		lock (gate)
		{
			pending.Add(text);
		}
	}

	private async Task PumpAsync(CancellationTokenSource stop)
	{
		// The first post goes at once, so the starter learns the workspace before the agent prints anything.
		var first = true;
		while (!done.IsCancellationRequested)
		{
			if (!first)
			{
				try
				{
					await Task.Delay(500, done.Token);
				}
				catch (OperationCanceledException)
				{
					return;
				}
			}

			first = false;
			if (await PostAsync(Take(), null, false, null) is { Stop: true } && !stop.IsCancellationRequested)
			{
				await stop.CancelAsync();
			}
		}
	}

	private List<string> Take()
	{
		lock (gate)
		{
			var lines = pending.ToList();
			pending.Clear();
			return lines;
		}
	}

	private async Task<Answer?> PostAsync(IReadOnlyList<string> lines, string? status, bool final, string? message)
	{
		try
		{
			using var response = await http.PostAsJsonAsync(url, new Report(runId, workspace, lines, status, final, message), Json);
			return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<Answer>(Json) : null;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
		{
			return null;
		}
	}

	/// <summary>Sends the rest of the output and the run's final status.</summary>
	public async Task FinishAsync(string? workspaceId, string status, string? message)
	{
		await done.CancelAsync();
		if (pump is not null)
		{
			await pump;
		}

		workspace = workspaceId ?? workspace;
		await PostAsync(Take(), status, true, message);
	}

	public ValueTask DisposeAsync()
	{
		done.Cancel();
		http.Dispose();
		done.Dispose();
		return ValueTask.CompletedTask;
	}
}
