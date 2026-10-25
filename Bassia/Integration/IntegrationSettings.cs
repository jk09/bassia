namespace Bassia.Integration;

using Bassia.Locks;

/// <summary>A resolver to run: its command, where, with which environment and brief (on stdin), and how to watch and stop it.</summary>
internal sealed record ResolverRequest(string Command, string Directory, IReadOnlyDictionary<string, string> Environment, string Brief,
	Action<string>? OnOutput, CancellationToken Cancellation);

/// <summary>
/// What an integration needs besides the component graph and its records: where it works, each component's merge and
/// lock policy, and how a resolver is run. <see cref="For"/> reads them from a monorepo on disk; tests set them directly.
/// </summary>
internal sealed class IntegrationSettings
{
	/// <summary>Folder holding one folder per integration.</summary>
	public required string WorkspaceDir { get; init; }

	/// <summary>The monorepo root a resolver is told about (<c>BASSIA_ROOT</c>).</summary>
	public string Root { get; init; } = "";

	public Func<string, MergePolicy> MergePolicyFor { get; init; } = _ => MergePolicy.Default;

	public Func<string, LockConflictPolicy> LockPolicyFor { get; init; } = _ => LockConflictPolicy.Resolver;

	/// <summary>Runs a resolver; returns its exit code, or null when it was cancelled. By default a shell command.</summary>
	public Func<ResolverRequest, Task<int?>> RunResolver { get; init; } = request =>
		ShellCommand.RunAsync(request.Command, request.Directory, request.Environment, request.Brief, request.OnOutput, request.Cancellation);

	public static IntegrationSettings For(Monorepo monorepo) => new()
	{
		WorkspaceDir = monorepo.WorkspaceDir,
		Root = monorepo.Root,
		MergePolicyFor = monorepo.MergePolicyFor,
		LockPolicyFor = component => LockFilter.ConflictPolicy(monorepo.Config, component)
	};
}
