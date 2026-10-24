using Bassia.Feedback;

namespace Bassia.Tests;

internal sealed class FakeSubmitter(bool configured = true, bool fail = false) : IIssueSubmitter
{
	public List<IssueDraft> Submitted { get; } = [];

	public bool IsConfigured => configured;

	public Task<IssueReceipt> SubmitAsync(IssueDraft draft, CancellationToken cancellation)
	{
		if (fail || !configured)
		{
			throw new FeedbackException("No GitHub token.");
		}

		Submitted.Add(draft);
		return Task.FromResult(new IssueReceipt(Submitted.Count, $"https://github.com/{draft.Repository}/issues/{Submitted.Count}", false));
	}
}

/// <summary>Redaction, fingerprints and the consent rules of feedback and crash reports. Nothing here reaches GitHub.</summary>
public sealed class FeedbackTests : IDisposable
{
	private readonly TempDirectory home = new();
	private readonly FakeSubmitter submitter;
	private readonly Func<IIssueSubmitter> originalSubmitter = FeedbackServices.Submitter;
	private readonly Func<FeedbackStore> originalStore = FeedbackServices.Store;
	private readonly Func<Redactor> originalRedaction = FeedbackServices.Redaction;
	private readonly Func<string?> originalRead = FeedbackServices.ReadAnswer;
	private readonly Func<bool> originalInteractive = ProgramCli.Interactive;
	private FakeSubmitter current;

	public FeedbackTests()
	{
		current = submitter = new FakeSubmitter();
		FeedbackServices.Submitter = () => current;
		FeedbackServices.Store = () => new FeedbackStore(home.Path);
		FeedbackServices.Redaction = () => new Redactor(@"C:\Users\ada", "ada", "ADA-PC");
		Interactive(false);
	}

	public void Dispose()
	{
		FeedbackServices.Submitter = originalSubmitter;
		FeedbackServices.Store = originalStore;
		FeedbackServices.Redaction = originalRedaction;
		FeedbackServices.ReadAnswer = originalRead;
		ProgramCli.Interactive = originalInteractive;
		home.Dispose();
	}

	private static void Interactive(bool value) => ProgramCli.Interactive = () => value;

	private static void Answer(string? text) => FeedbackServices.ReadAnswer = () => text;

	private static Exception Crash()
	{
		try
		{
			throw new InvalidOperationException(@"cannot read C:\Users\ada\repo with token=ghp_abcdefghijklmnopqrstuvwxyz0123456789");
		}
		catch (InvalidOperationException ex)
		{
			return ex;
		}
	}

	private FeedbackStore Store => FeedbackServices.Store();

	// ----- redaction -----

	[Fact]
	public void Redactor_RemovesHomeUserMachineCredentialsAndTokens()
	{
		var redactor = new Redactor(@"C:\Users\ada", "ada", "ADA-PC");

		var text = redactor.Redact(@"open C:\Users\ada\src and C:/Users/ada/x as ada on ADA-PC via https://ada:s3cret@github.com/a/b.git " +
			"using ghp_abcdefghijklmnopqrstuvwxyz0123456789 and Authorization: Bearer abcdefghijklmnop1234 and password=hunter2");

		Assert.DoesNotContain("ada", text, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("ADA-PC", text);
		Assert.DoesNotContain("s3cret", text);
		Assert.DoesNotContain("ghp_", text);
		Assert.DoesNotContain("hunter2", text);
		Assert.DoesNotContain("abcdefghijklmnop1234", text);
		Assert.Contains(@"~\src", text);
		Assert.Contains("https://<credentials>@github.com/a/b.git", text);
	}

	[Fact]
	public void Redactor_KeepsWordsThatMerelyContainTheUserName()
	{
		Assert.Equal("a nomadic user named <user>", new Redactor(null, "ada", null).Redact("a nomadic user named ada"));
		Assert.Equal("adapter", new Redactor(null, "ada", null).Redact("adapter"));
	}

	// ----- reports -----

	[Fact]
	public void Fingerprint_IsStableAcrossMessagesAndDiffersByDefect()
	{
		Exception Throw(string message, bool other)
		{
			try
			{
				if (other)
				{
					throw new ArgumentException(message);
				}

				throw new InvalidOperationException(message);
			}
			catch (Exception ex)
			{
				return ex;
			}
		}

		Assert.Equal(ReportBuilder.Fingerprint(Throw("one", false)), ReportBuilder.Fingerprint(Throw("two", false)));
		Assert.NotEqual(ReportBuilder.Fingerprint(Throw("one", false)), ReportBuilder.Fingerprint(Throw("one", true)));
	}

	[Fact]
	public void CrashReport_IsRedactedAndNamesTheCommandNotItsArguments()
	{
		var draft = ReportBuilder.Crash("jk09/bassia", "agent start", Crash(), new Redactor(@"C:\Users\ada", "ada", "ADA-PC"));

		Assert.StartsWith("[crash] System.InvalidOperationException in 'bassia agent start'", draft.Title);
		Assert.DoesNotContain("ghp_", draft.Body);
		Assert.DoesNotContain(@"C:\Users\ada", draft.Body);
		Assert.Contains($"bassia-fingerprint: {draft.Fingerprint}", draft.Body);
		Assert.Equal(new[] { "bug", "crash" }, draft.Labels);
	}

	[Theory]
	[InlineData("idea", "enhancement")]
	[InlineData("question", "question")]
	public void Feedback_LabelsByKind(string kind, string label)
	{
		var draft = ReportBuilder.Feedback("jk09/bassia", "make it faster\nplease", kind, new Redactor(null, null, null));

		Assert.Equal($"[{kind}] make it faster", draft.Title);
		Assert.Contains(label, draft.Labels);
	}

	[Fact]
	public void Feedback_RejectsAnEmptyMessageAndAnUnknownKind()
	{
		var redactor = new Redactor(null, null, null);

		Assert.Throws<FeedbackException>(() => ReportBuilder.Feedback("r/r", "  ", "bug", redactor));
		Assert.Throws<FeedbackException>(() => ReportBuilder.Feedback("r/r", "x", "rant", redactor));
	}

	// ----- consent for crash reports -----

	[Fact]
	public async Task Crash_WhenNothingWasChosenAndNoTerminal_SendsNothing()
	{
		Assert.Null(await CrashReporter.HandleAsync("status", Crash()));

		Assert.Empty(submitter.Submitted);
		Assert.Equal(ReportMode.Unset, Store.Mode);
	}

	[Fact]
	public async Task Crash_WhenOff_NeverSendsOrAsks()
	{
		Store.SetMode(ReportMode.Off);
		Interactive(true);
		Answer("y");

		Assert.Null(await CrashReporter.HandleAsync("status", Crash()));
		Assert.Empty(submitter.Submitted);
	}

	[Fact]
	public async Task Crash_WhenAuto_SendsWithoutAskingEvenWithoutTerminal()
	{
		Store.SetMode(ReportMode.Auto);

		var note = await CrashReporter.HandleAsync("status", Crash());

		Assert.Contains("Reported as", note);
		Assert.Single(submitter.Submitted);
	}

	[Fact]
	public async Task Crash_WhenAutoAndNoTokenOrNetwork_KeepsTheReportForLater()
	{
		Store.SetMode(ReportMode.Auto);
		current = new FakeSubmitter(configured: false);

		var note = await CrashReporter.HandleAsync("status", Crash());

		Assert.Contains("feedback pending -send", note);
		Assert.Single(Store.Pending());

		current = submitter;
		var (exitCode, output, _) = await TestEnvironment.RunAsync("feedback", "pending", "-send");
		Assert.Equal(0, exitCode);
		Assert.Contains("Sent 1", output);
		Assert.Empty(Store.Pending());
		Assert.Single(submitter.Submitted);
	}

	[Theory]
	[InlineData("y", "Ask", 1)]
	[InlineData("a", "Auto", 1)]
	[InlineData("n", "Unset", 0)]
	[InlineData("N", "Off", 0)]
	[InlineData("", "Unset", 0)]
	public async Task Crash_AtATerminalWithNothingChosen_AsksAndRemembersOnlyAlwaysAndNever(string answer, string expectedMode, int expectedSent)
	{
		Interactive(true);
		Answer(answer);
		var error = new StringWriter();
		var originalError = Console.Error;
		Console.SetError(error);
		try
		{
			await CrashReporter.HandleAsync("status", Crash());
		}
		finally
		{
			Console.SetError(originalError);
		}

		Assert.Equal(expectedSent, submitter.Submitted.Count);
		Assert.Equal(Enum.Parse<ReportMode>(expectedMode), Store.Mode);
		Assert.Contains("Send it?", error.ToString());
		Assert.DoesNotContain("ghp_", error.ToString());
	}

	[Fact]
	public async Task Crash_AlreadyFiledFromThisMachine_IsNotFiledAgain()
	{
		Store.SetMode(ReportMode.Auto);
		var crash = Crash();

		await CrashReporter.HandleAsync("status", crash);
		await CrashReporter.HandleAsync("status", crash);

		Assert.Single(submitter.Submitted);
	}

	// ----- commands -----

	[Fact]
	public async Task FeedbackSend_WithoutConfirmationAndWithoutTerminal_ShowsTheIssueAndSendsNothing()
	{
		var (exitCode, output, _) = await TestEnvironment.RunAsync("feedback", "send", "-kind", "idea", "tags", "should", "take", "patterns");

		Assert.Equal(0, exitCode);
		Assert.Contains("sent = false", output);
		Assert.Contains("[idea] tags should take patterns", output);
		Assert.Empty(submitter.Submitted);
	}

	[Fact]
	public async Task FeedbackSend_WithYes_FilesTheIssue()
	{
		var (exitCode, output, _) = await TestEnvironment.RunAsync("feedback", "send", "-yes", "status", "is", "slow");

		Assert.Equal(0, exitCode);
		Assert.Contains("sent = true", output);
		Assert.Equal("[bug] status is slow", Assert.Single(submitter.Submitted).Title);
	}

	[Fact]
	public async Task FeedbackSend_WhenSendingFails_ReportsTheErrorAndKeepsTheFeedback()
	{
		current = new FakeSubmitter(fail: true);

		var (exitCode, _, error) = await TestEnvironment.RunAsync("feedback", "send", "-yes", "status", "is", "slow");

		Assert.Equal(1, exitCode);
		Assert.Contains("feedback pending -send", error);
		Assert.Single(Store.Pending());
	}

	[Fact]
	public async Task FeedbackReports_ShowsAndSetsTheChoice()
	{
		var (_, before, _) = await TestEnvironment.RunAsync("feedback", "reports");
		var (exitCode, after, _) = await TestEnvironment.RunAsync("feedback", "reports", "-mode", "auto");
		var (badExit, _, _) = await TestEnvironment.RunAsync("feedback", "reports", "-mode", "sometimes");

		Assert.Contains("mode = \"unset\"", before);
		Assert.Equal(0, exitCode);
		Assert.Contains("mode = \"auto\"", after);
		Assert.Equal(ReportMode.Auto, Store.Mode);
		Assert.Equal(2, badExit);
	}

	[Fact]
	public async Task UnhandledException_InACommand_IsAnErrorResultAndIsReportedPerTheChoice()
	{
		Store.SetMode(ReportMode.Auto);
		FeedbackServices.Submitter = () => throw new InvalidProgramException("boom");

		var (exitCode, _, error) = await TestEnvironment.RunAsync("feedback", "send", "-yes", "x");

		Assert.Equal(1, exitCode);
		Assert.Contains("Bassia stopped unexpectedly (InvalidProgramException: boom)", error);
	}
}
