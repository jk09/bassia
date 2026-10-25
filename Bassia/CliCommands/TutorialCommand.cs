namespace Bassia;

using Bassia.Cli;
using Bassia.Tutorial;

/// <summary>
/// <c>bassia tutorial</c>, and a bare <c>bassia</c> at a terminal: the guided setup of a new monorepo, or the options
/// for the monorepo the user is in (<see cref="TutorialSession"/>).
/// </summary>
internal static class TutorialCommand
{
	/// <summary>How the guide runs commands; tests run them in-process instead of starting bassia again.</summary>
	internal static Func<string, ICommandExecutor> ExecutorFactory { get; set; } = directory => new SelfExecutor(directory, relay: false);

	// ----- bassia tutorial -----

	public static async Task<int> RunAsync(Invocation invocation)
	{
		var human = HumanOutput.Enabled;
		var outcome = await Session().RunAsync(offer: false, CancellationToken.None);

		// Commands run in-process (in tests) reset the output mode; the result is printed the way this command line asked.
		HumanOutput.Enabled = human;
		var exitCode = ProgramCli.WriteResult(outcome.Ok, invocation.Command, outcome.Message, new Dictionary<string, object?>
		{
			["root"] = outcome.Root,
			["commands"] = outcome.Commands.ToList(),
			["then"] = outcome.Then is null ? null : CommandCatalog.Display(outcome.Then)
		});
		return outcome.Then is { } then ? await ProgramCli.RunAsync([.. then]) : exitCode;
	}

	// ----- bassia (no arguments, at a terminal) -----

	/// <summary>The short help, then the guide with an offer to set up a monorepo, on stderr.</summary>
	public static async Task<int> WelcomeAsync()
	{
		Console.Out.WriteLine(Help.ShortText);
		TutorialOutcome outcome;
		try
		{
			outcome = await Session().RunAsync(offer: true, CancellationToken.None);
		}
		catch (Exception ex) when (ProgramCli.IsReportable(ex))
		{
			// Not a command, so not under RunAsync's handler: a broken monorepo is reported like any failure.
			return ProgramCli.WriteResult(false, "bassia", ex.Message);
		}

		return outcome.Then is { } then ? await ProgramCli.RunAsync([.. then]) : outcome.Ok ? 0 : 1;
	}

	private static TutorialSession Session()
	{
		var directory = Environment.CurrentDirectory;
		return new TutorialSession(Console.In, Console.Error, ExecutorFactory(directory), directory);
	}
}
