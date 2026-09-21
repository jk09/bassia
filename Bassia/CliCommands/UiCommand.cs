namespace Bassia;

using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Ui;
using Spectre.Console;

/// <summary><c>bassia ui</c>: the interactive frontend over the monorepo the current directory belongs to.</summary>
internal static class UiCommand
{
	public static async Task<int> RunAsync()
	{
		var root = Monorepo.FindRoot(Environment.CurrentDirectory);
		if (root is null)
		{
			return ProgramCli.WriteResult(false, "ui", $"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia init' first.");
		}

		if (!AnsiConsole.Profile.Capabilities.Interactive)
		{
			return ProgramCli.WriteResult(false, "ui", "bassia ui needs an interactive terminal; standard input or output is redirected.");
		}

		Monorepo monorepo;
		try
		{
			monorepo = Monorepo.Load(root);
		}
		catch (MonorepoException ex)
		{
			return ProgramCli.WriteResult(false, "ui", ex.Message);
		}

		var git = new GitClient(root);
		var store = new RunMetadataStore(git, monorepo.RunsRepoDir);
		var session = new InteractiveSession(AnsiConsole.Console, monorepo, store,
			(select, command) => AgentCommand.StartRunAsync(git, monorepo, select, command));

		// Prompts hide the cursor while they run; make sure Ctrl-C doesn't leave the terminal without one.
		ConsoleCancelEventHandler restoreCursor = (_, _) => AnsiConsole.Cursor.Show();
		Console.CancelKeyPress += restoreCursor;
		try
		{
			await session.RunAsync();
		}
		finally
		{
			Console.CancelKeyPress -= restoreCursor;
			AnsiConsole.Cursor.Show();
		}

		return 0;
	}
}
