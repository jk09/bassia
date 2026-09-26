namespace Bassia.CliCommands.Agent;

/// <summary>
/// Where a run stands. The first four are the phases a run passes through while it executes; the rest mirror the
/// <c>status</c> of a stored <see cref="RunMetadata"/> record, so one enum describes both a live and a recorded run.
/// </summary>
internal enum AgentRunPhase
{
	/// <summary>Accepted by the frontend, not started yet.</summary>
	Queued,

	/// <summary>Resolving the selection and materializing the checkouts.</summary>
	Preparing,

	/// <summary>The agent command is running in the run folder.</summary>
	Agent,

	/// <summary>Committing, tagging and pushing the component results.</summary>
	Finalizing,

	Completed,
	Partial,
	Failed,
	Cancelled,
	Abandoned,

	/// <summary>A stored record whose status this version of Bassia does not know.</summary>
	Unknown
}

/// <summary>One progress step of a run, as reported to <see cref="AgentRunContext.OnStep"/>.</summary>
internal readonly record struct AgentRunStep(string RunId, AgentRunPhase Phase, string Message);

/// <summary>
/// How a caller wants to observe and steer one <see cref="AgentCommand.StartRunAsync"/> call. Passing none keeps
/// the behavior the scriptable CLI has always had: progress on stderr, the agent process inheriting the console,
/// and no way to interrupt it.
/// </summary>
internal sealed class AgentRunContext
{
	/// <summary>
	/// Cancels the run up to and including the agent process, which is killed as a whole process tree. Once the
	/// agent has exited the commit/tag/push sequence runs to the end: it is short, and interrupting it would leave
	/// a component committed but not pushed for no benefit.
	/// </summary>
	public CancellationToken Cancellation { get; init; }

	/// <summary>Receives every progress step instead of the <c>bassia: ...</c> stderr lines.</summary>
	public Action<AgentRunStep>? OnStep { get; init; }

	/// <summary>
	/// Receives the agent process's stdout and stderr line by line. Setting it redirects both pipes; without it
	/// the agent inherits the console, which is what lets <c>bassia run start</c> stream an agent's output to a caller.
	/// </summary>
	public Action<string>? OnOutput { get; init; }

	/// <summary>The phase a stored record's <c>status</c> corresponds to.</summary>
	public static AgentRunPhase PhaseOfStatus(string status) => status switch
	{
		"started" => AgentRunPhase.Agent,
		"completed" => AgentRunPhase.Completed,
		"partial" => AgentRunPhase.Partial,
		"failed" => AgentRunPhase.Failed,
		"cancelled" => AgentRunPhase.Cancelled,
		"abandoned" => AgentRunPhase.Abandoned,
		_ => AgentRunPhase.Unknown
	};
}
