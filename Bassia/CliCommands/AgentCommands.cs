namespace Bassia.CliCommands;

using System.Diagnostics;
using Bassia.Agents;
using Bassia.Cli;
using Bassia.Core.Model;
using Bassia.Core.Version;
using static Bassia.CliCommands.Results;

/// <summary><c>bassia agent ...</c>: agentic runs, each in a workspace of its own.</summary>
internal static class AgentCommands
{
	private static AgentRunOptions Options(Invocation invocation, Monorepo monorepo)
	{
		var prompt = invocation.Get("prompt");
		var command = invocation.Get("run");
		if ((prompt is null) == (command is null))
		{
			throw invocation.Usage("Give exactly one of -prompt <text> and -run <command...>.");
		}

		return new AgentRunOptions(prompt, command, invocation.Get("workspace"), invocation.Get("name"), invocation.Get("on"), invocation.Get("model"),
			invocation.Has("land"), invocation.Has("keep"), invocation.List("lease"), TimeSpan.FromSeconds(invocation.Int("lease-wait", 0, 0)), invocation.Get("run-id"),
			Load: invocation.Has("load") ? invocation.List("load") : null);
	}

	public static async Task<int> StartAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var runner = new AgentRunner(monorepo);
		var options = Options(invocation, monorepo);
		if (invocation.Has("detach"))
		{
			var arguments = ForwardedArguments(invocation);
			var (runId, workspace) = await runner.StartDetachedAsync(options, arguments);
			var data = await WorkspaceAsync(monorepo.Repo, workspace);
			data["run_id"] = runId;
			data["log"] = runner.LogFile(runId);
			return Ok(invocation, $"Started run {runId} in workspace '{workspace.Name}' in the background. Follow it with 'bassia agent logs {runId}', wait with 'bassia agent wait {runId}'.", data);
		}

		var background = invocation.Has("background");
		var result = await runner.RunAsync(options, background ? null : Console.WriteLine);
		var outcome = await WorkspaceAsync(monorepo.Repo, result.Workspace);
		outcome["run_id"] = result.RunId;
		outcome["changed"] = result.Changes.Select(change => change.Path).ToList();
		outcome["log"] = result.LogFile;
		if (result.Land is { } land)
		{
			outcome["land"] = Land(land);
		}

		var message = $"Run {result.RunId} {result.Run.Status} (exit code {result.Run.ExitCode?.ToString() ?? "-"}) in workspace '{result.Workspace.Name}': " +
			$"{result.Changes.Count} path(s) changed." + (result.Land is { } landed ? $" Landing: {landed.Message}" : options.Land ? "" : $" 'bassia workspace check {result.Workspace.Name}' tells whether it can land.");
		return result.Run.Status == RunStatus.Succeeded && (result.Land is null || LandOutcome.IsSuccess(result.Land.Outcome))
			? Ok(invocation, message, outcome)
			: Fail(invocation, message, outcome);
	}

	/// <summary>The switches a detached run passes to its background process.</summary>
	private static List<string> ForwardedArguments(Invocation invocation)
	{
		var args = new List<string> { "-background" };
		foreach (var name in new[] { "name", "on", "model", "lease-wait" })
		{
			if (invocation.Get(name) is { } value)
			{
				args.AddRange(["-" + name, value]);
			}
		}

		foreach (var flag in new[] { "land", "keep" })
		{
			if (invocation.Has(flag))
			{
				args.Add("-" + flag);
			}
		}

		if (invocation.List("lease") is { Count: > 0 } leases)
		{
			args.AddRange(["-lease", string.Join(',', leases)]);
		}

		if (invocation.List("load") is { Count: > 0 } load)
		{
			args.AddRange(["-load", string.Join(',', load)]);
		}

		if (invocation.Get("prompt") is { } prompt)
		{
			args.AddRange(["-prompt", prompt]);
		}
		else
		{
			args.AddRange(["-run", invocation.Require("run")]);
		}

		return args;
	}

	public static async Task<int> ListAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var runner = new AgentRunner(monorepo);
		var status = invocation.Get("status");
		var runs = (await runner.ListAsync()).Where(state => status is null || RunState(state.Record.Run!) == status).ToList();
		return Ok(invocation, $"{runs.Count} run(s){(status is null ? "" : $" {status}")}, newest first.", new Dictionary<string, object?>
		{
			["table"] = Table(["RUN", "STATUS", "WORKSPACE", "STARTED", "ELAPSED", "LANDED", "TASK"], runs.Select(state =>
			{
				var run = state.Record.Run!;
				return (IReadOnlyList<string>)
				[
					run.RunId, RunState(run), state.Name, Time(run.Started), AsciiTable.Elapsed((run.Finished ?? DateTimeOffset.UtcNow) - run.Started),
					state.Record.LastLand?.Outcome ?? "-", Short(run.Prompt ?? run.Command, 50)
				];
			})),
			["run"] = Tables(runs, state =>
			{
				var data = Run(state.Record.Run!);
				data["workspace"] = state.Name;
				data["workspace_status"] = state.Record.Status;
				return data;
			})
		});
	}

	private static string RunState(RunRecord run) => run.Status == RunStatus.Running && !AgentRunner.IsAlive(run) ? "lost" : run.Status;

	public static async Task<int> ShowAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var state = await new AgentRunner(monorepo).FindAsync(invocation.Require("id"));
		var data = await WorkspaceAsync(monorepo.Repo, state);
		var run = state.Record.Run!;
		return Ok(invocation, $"Run {run.RunId} is {RunState(run)} in workspace '{state.Name}' ({state.Record.Status}).{(run.LastLine is { Length: > 0 } line ? $" Last output: {line}" : "")}", data);
	}

	public static async Task<int> LogsAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var runner = new AgentRunner(monorepo);
		var state = await runner.FindAsync(invocation.Require("id"));
		var text = await runner.ReadLogAsync(state.Record.Run!);
		var lines = text.Split('\n');
		if (invocation.Get("tail") is not null)
		{
			lines = lines.TakeLast(invocation.Int("tail", 20, 1)).ToArray();
		}

		return Ok(invocation, $"Output of run {state.Record.Run!.RunId} ({RunState(state.Record.Run!)}).", new Dictionary<string, object?>
		{
			["run_id"] = state.Record.Run!.RunId,
			["status"] = RunState(state.Record.Run!),
			["output"] = new TomlText(string.Join('\n', lines).TrimEnd('\n') is { Length: > 0 } output ? output : "(no output yet)")
		});
	}

	public static async Task<int> WaitAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var runner = new AgentRunner(monorepo);
		var timeout = TimeSpan.FromSeconds(invocation.Int("timeout", 3600, 1));
		var clock = Stopwatch.StartNew();
		var state = await runner.FindAsync(invocation.Require("id"));
		while (AgentRunner.IsAlive(state.Record.Run!) && clock.Elapsed < timeout)
		{
			await Task.Delay(500);
			state = (await monorepo.Repo.FindWorkspaceAsync(state.Id))!;
		}

		var run = state.Record.Run!;
		var data = await WorkspaceAsync(monorepo.Repo, state);
		if (run.Status == RunStatus.Running)
		{
			return AgentRunner.IsAlive(run)
				? Fail(invocation, $"Run {run.RunId} is still running after {timeout.TotalSeconds:0} s.", data)
				: Fail(invocation, $"Run {run.RunId} was lost: its process ended without recording a result.", data);
		}

		return run.Status == RunStatus.Succeeded
			? Ok(invocation, $"Run {run.RunId} succeeded in workspace '{state.Name}'{(state.Record.LastLand is { } land ? $"; landing: {land.Outcome}" : "")}.", data)
			: Fail(invocation, $"Run {run.RunId} {run.Status} (exit code {run.ExitCode?.ToString() ?? "-"}).", data);
	}

	public static async Task<int> StopAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var runner = new AgentRunner(monorepo);
		var state = await runner.FindAsync(invocation.Require("id"));
		var run = state.Record.Run!;
		if (!AgentRunner.IsAlive(run))
		{
			return Ok(invocation, $"Run {run.RunId} is not running ({RunState(run)}).");
		}

		if (run.Host != Environment.MachineName)
		{
			return Fail(invocation, $"Run {run.RunId} runs on '{run.Host}'; stop it there.");
		}

		Directory.CreateDirectory(monorepo.RunsDir);
		await File.WriteAllTextAsync(runner.StopFile(run.RunId), "stop");
		return Ok(invocation, $"Asked run {run.RunId} to stop; its agent is ended and what it changed is snapshotted.");
	}

	/// <summary>Rebase-by-replay: the same task, on mainline's head, in a new workspace that keeps the change's ChangeId.</summary>
	public static async Task<int> ReplayAsync(Invocation invocation)
	{
		using var monorepo = Monorepo.Current();
		var runner = new AgentRunner(monorepo);
		var reference = invocation.Require("id");
		WorkspaceState original;
		try
		{
			original = await runner.FindAsync(reference);
		}
		catch (MonorepoException)
		{
			original = await monorepo.Repo.ResolveWorkspaceAsync(reference);
		}

		var working = await monorepo.Repo.Objects.ReadCommitAsync(original.Record.Working);
		if (working.Recipe is not { } recipe || (recipe.Prompt is null && recipe.Command is null))
		{
			return Fail(invocation, $"Workspace '{original.Name}' has no replay recipe (it was not made by 'bassia agent start'); sync it and resolve by hand.");
		}

		var name = $"{original.Name.Split("-replay")[0]}-replay{DateTimeOffset.UtcNow:HHmmss}";
		var options = new AgentRunOptions(recipe.Prompt, recipe.Command, null, name, null, recipe.Model, invocation.Has("land"), false, invocation.List("lease"),
			TimeSpan.FromSeconds(invocation.Int("lease-wait", 0, 0)), ReplayOf: original.Id, ChangeId: working.ChangeId,
			// A replay loads what the original loaded here, unless told otherwise.
			Load: invocation.Has("load") ? invocation.List("load") : monorepo.Workspaces.Local(original)?.State.Rules is { IsAll: false } previous ? previous.Patterns : null);
		await monorepo.Workspaces.Service.SetStatusAsync(original.Id, WorkspaceStatus.Replaced, "replaced by a replay", monorepo.User);
		var result = await runner.RunAsync(options, Console.WriteLine);
		var data = await WorkspaceAsync(monorepo.Repo, result.Workspace);
		data["run_id"] = result.RunId;
		data["replay_of"] = original.Name;
		if (result.Land is { } land)
		{
			data["land"] = Land(land);
		}

		var message = $"Replayed '{original.Name}' as '{result.Workspace.Name}' on seq {result.Workspace.Record.BaseSeq} (same change {working.ChangeId.Short}): run {result.Run.Status}." +
			(result.Land is { } landed ? $" Landing: {landed.Message}" : "");
		return result.Run.Status == RunStatus.Succeeded && (result.Land is null || LandOutcome.IsSuccess(result.Land.Outcome)) ? Ok(invocation, message, data) : Fail(invocation, message, data);
	}
}
