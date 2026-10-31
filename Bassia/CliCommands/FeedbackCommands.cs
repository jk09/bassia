namespace Bassia.CliCommands;

using Bassia.Cli;
using Bassia.Feedback;

/// <summary><c>bassia feedback</c>: send feedback as a GitHub issue, choose what happens to crash reports, and send the ones kept.</summary>
internal static class FeedbackCommands
{
	public static async Task<int> SendAsync(Invocation invocation)
	{
		var store = FeedbackServices.Store();
		var draft = ReportBuilder.Feedback(store.Repository, invocation.Require("message"), invocation.Get("kind") ?? "bug", FeedbackServices.Redaction());
		var data = DraftData(draft);

		if (!invocation.Has("yes"))
		{
			if (!ProgramCli.Interactive())
			{
				data["sent"] = false;
				return ProgramCli.WriteResult(true, invocation.Command, "Not sent: it needs your confirmation, and there is no terminal to ask on. Run it again with -yes to send it.", data);
			}

			Console.Error.WriteLine($"This will be filed as an issue in {draft.Repository}:\n\n{draft.Title}\n\n{draft.Body}");
			Console.Error.Write("Send it? [y/N]: ");
			if (FeedbackServices.ReadAnswer()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
			{
				data["sent"] = false;
				return ProgramCli.WriteResult(true, invocation.Command, "Not sent.", data);
			}
		}

		var note = await CrashReporter.SendApprovedAsync(draft, store);
		data["sent"] = note.StartsWith("Reported as", StringComparison.Ordinal);
		if (!(bool)data["sent"]!)
		{
			throw new FeedbackException(note);
		}

		return ProgramCli.WriteResult(true, invocation.Command, note, data);
	}

	public static Task<int> ReportsAsync(Invocation invocation)
	{
		var store = FeedbackServices.Store();
		if (invocation.Get("mode") is { } mode)
		{
			if (!Enum.TryParse<ReportMode>(mode, ignoreCase: true, out var parsed) || parsed == ReportMode.Unset)
			{
				throw invocation.Usage($"-mode must be off, ask or auto; got '{mode}'.");
			}

			store.SetMode(parsed);
		}

		var current = store.Mode;
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command, current == ReportMode.Unset
			? "Crash reports: not chosen yet (nothing is sent; an interactive crash asks once)."
			: $"Crash reports: {current.ToString().ToLowerInvariant()}.", new Dictionary<string, object?>
		{
			["mode"] = current == ReportMode.Unset ? "unset" : current.ToString().ToLowerInvariant(),
			["repository"] = store.Repository,
			["token_available"] = GitHubIssueSubmitter.Token() is not null,
			["folder"] = store.Folder
		}));
	}

	public static async Task<int> PendingAsync(Invocation invocation)
	{
		var store = FeedbackServices.Store();
		var pending = store.Pending();
		var sent = new List<IReadOnlyDictionary<string, object?>>();
		var failed = 0;
		if (invocation.Has("send"))
		{
			foreach (var (path, draft) in pending)
			{
				try
				{
					using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
					var receipt = await FeedbackServices.Submitter().SubmitAsync(draft, timeout.Token);
					if (draft.Fingerprint is not null)
					{
						store.RecordSent(draft.Fingerprint, receipt);
					}

					store.RemovePending(path);
					sent.Add(new Dictionary<string, object?> { ["title"] = draft.Title, ["url"] = receipt.Url });
				}
				catch (FeedbackException ex)
				{
					if (failed++ == 0 && sent.Count == 0)
					{
						throw new FeedbackException($"{ex.Message} {pending.Count} report(s) remain.");
					}
				}
			}
		}

		var remaining = pending.Count - sent.Count;
		var data = new Dictionary<string, object?> { ["pending"] = remaining };
		if (sent.Count > 0)
		{
			data["sent"] = sent;
		}

		if (!invocation.Has("send") && pending.Count > 0)
		{
			data["report"] = pending.Select(item => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["title"] = item.Draft.Title,
				["repository"] = item.Draft.Repository
			}).ToList();
		}

		return ProgramCli.WriteResult(true, invocation.Command,
			invocation.Has("send") ? $"Sent {sent.Count} report(s); {remaining} remain." : $"{pending.Count} report(s) waiting to be sent.", data);
	}

	private static Dictionary<string, object?> DraftData(IssueDraft draft) => new()
	{
		["repository"] = draft.Repository,
		["title"] = draft.Title,
		["labels"] = draft.Labels.ToList(),
		["body"] = draft.Body
	};
}
