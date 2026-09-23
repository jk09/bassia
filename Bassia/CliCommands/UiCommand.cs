namespace Bassia;

using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Integration;
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
		using var supervisor = new RunSupervisor(
			(select, command, context) => AgentCommand.StartRunAsync(git, monorepo, select, command, context));
		using var integrations = new IntegrationSupervisor(
			(runs, plan, resolver, context) => IntegrationRunner.RunAsync(git, monorepo, runs, plan, resolver, context));
		var session = new InteractiveSession(AnsiConsole.Console, monorepo, store, supervisor, integrations);

		// Prompts hide the cursor while they run; make sure Ctrl-C doesn't leave the terminal without one.
		// The frontend's own runs are stopped through it too, so a Ctrl-C never leaves agent processes behind.
		ConsoleCancelEventHandler stopEverything = (_, _) =>
		{
			supervisor.CancelAll();
			integrations.Cancel();
			AnsiConsole.Cursor.Show();
		};
		Console.CancelKeyPress += stopEverything;
		try
		{
			await session.RunAsync();
		}
		finally
		{
			Console.CancelKeyPress -= stopEverything;
			AnsiConsole.Cursor.Show();
		}

		return 0;
	}
}
