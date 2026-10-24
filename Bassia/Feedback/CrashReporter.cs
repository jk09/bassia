namespace Bassia.Feedback;

/// <summary>
/// What is shared with the reporting code and replaced in tests: the transport, the store, the redaction, whether a
/// person is at the terminal, and how an answer is read.
/// </summary>
internal static class FeedbackServices
{
	public static Func<IIssueSubmitter> Submitter { get; set; } = () => new GitHubIssueSubmitter();
	public static Func<FeedbackStore> Store { get; set; } = FeedbackStore.Default;
	public static Func<Redactor> Redaction { get; set; } = Redactor.ForCurrentUser;
	public static Func<string?> ReadAnswer { get; set; } = Console.ReadLine;
}

/// <summary>
/// Turns an unhandled exception into an issue, but only as far as the user has agreed. Per the <c>reports</c> choice:
/// <c>off</c> sends nothing; <c>auto</c> sends without asking; <c>ask</c> - and an unset choice - asks at a terminal,
/// where the answer can also set the choice for good. Without a terminal only <c>auto</c> sends. It never throws and
/// never changes the failing command's outcome: what happened is returned as a short note for the error result.
/// </summary>
internal static class CrashReporter
{
	public static async Task<string?> HandleAsync(string command, Exception exception)
	{
		try
		{
			var store = FeedbackServices.Store();
			var draft = ReportBuilder.Crash(store.Repository, command, exception, FeedbackServices.Redaction());
			var mode = store.Mode;
			if (mode == ReportMode.Off || store.WasSent(draft.Fingerprint!))
			{
				return null;
			}

			if (mode != ReportMode.Auto)
			{
				if (!ProgramCli.Interactive())
				{
					return null;
				}

				switch (Ask(draft, store, mode))
				{
					case Answer.No:
						return null;
					case Answer.Never:
						store.SetMode(ReportMode.Off);
						return "Crash reports are off; 'bassia feedback reports -mode ask' turns them back on.";
					case Answer.Always:
						store.SetMode(ReportMode.Auto);
						break;
					default:
						if (mode == ReportMode.Unset)
						{
							store.SetMode(ReportMode.Ask);
						}

						break;
				}
			}

			return await SendApprovedAsync(draft, store);
		}
		catch (Exception ex)
		{
			// Reporting is best effort: whatever goes wrong here must not hide the original failure.
			return $"The crash could not be reported: {ex.Message}";
		}
	}

	/// <summary>Sends a draft the user has approved; when that fails it is kept for 'bassia feedback pending -send'.</summary>
	public static async Task<string> SendApprovedAsync(IssueDraft draft, FeedbackStore store)
	{
		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			var receipt = await FeedbackServices.Submitter().SubmitAsync(draft, timeout.Token);
			if (draft.Fingerprint is not null)
			{
				store.RecordSent(draft.Fingerprint, receipt);
			}

			return receipt.Duplicate ? $"Added to the known issue {receipt.Url}." : $"Reported as {receipt.Url}.";
		}
		catch (FeedbackException ex)
		{
			store.SavePending(draft);
			return $"{ex.Message} The report was kept; 'bassia feedback pending -send' sends it once that works.";
		}
	}

	private enum Answer { Yes, No, Always, Never }

	private static Answer Ask(IssueDraft draft, FeedbackStore store, ReportMode mode)
	{
		Console.Error.WriteLine($"Bassia stopped unexpectedly. This report can be filed as an issue in {draft.Repository}:");
		Console.Error.WriteLine();
		Console.Error.WriteLine(draft.Title);
		Console.Error.WriteLine(draft.Body.Replace("<!-- bassia-fingerprint:", "(fingerprint:").Replace(" -->", ")"));
		Console.Error.WriteLine($"Nothing is sent unless you answer yes. 'always' and 'never' are remembered in {Path.Combine(store.Folder, FeedbackStore.SettingsFileName)}.");
		Console.Error.Write("Send it? [y]es, [a]lways without asking, [n]o, [N]ever: ");
		var answer = FeedbackServices.ReadAnswer()?.Trim() ?? "";
		return answer switch
		{
			"y" or "Y" or "yes" or "Yes" => Answer.Yes,
			"a" or "A" or "always" or "Always" => Answer.Always,
			"N" or "never" or "Never" => Answer.Never,
			_ => Answer.No
		};
	}
}
