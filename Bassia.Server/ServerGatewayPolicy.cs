namespace Bassia.Server;

using Bassia.Integration;
using Bassia.Server.Auth;
using Bassia.Storage.Cloud;

/// <summary>
/// Who may do what through the server's git gateway: fetching a component takes <c>read</c>; pushing run branches and
/// the tags of runs and integrations takes <c>run</c>; pushing the default branch or any other tag takes
/// <c>integrate</c>. The meta-repo each caller gets is limited to the components they may read.
/// </summary>
internal sealed class ServerGatewayPolicy(ServerState state) : IGatewayPolicy
{
	public async Task<object?> AuthenticateAsync(HttpContext context) => await state.Authenticator.AuthenticateAsync(context.Request);

	public async Task<bool> MayFetchAsync(object caller, string component) => await state.LevelAsync((Caller)caller, component) >= Permission.Read;

	public async Task<string?> RefusePushAsync(object caller, string component, IReadOnlyList<PushCommand> commands)
	{
		var level = await state.LevelAsync((Caller)caller, component);
		if (level < Permission.Run)
		{
			return $"'{((Caller)caller).Id}' may not push to component '{component}'.";
		}

		var defaultBranch = commands.Count == 0 ? null : await state.Storage.Open(component).DefaultBranchAsync();
		foreach (var command in commands)
		{
			var needed = command.Ref == $"refs/heads/{defaultBranch}" ? Permission.Integrate
				: command.Ref.StartsWith("refs/heads/", StringComparison.Ordinal) ? Permission.Run
				: command.Ref.StartsWith($"refs/tags/{RunMetadata.RefPrefix}", StringComparison.Ordinal)
					|| command.Ref.StartsWith($"refs/tags/{IntegrationRecord.RefPrefix}", StringComparison.Ordinal) ? Permission.Run
				: Permission.Integrate;
			if (level < needed)
			{
				return $"'{((Caller)caller).Id}' may not push {command.Ref} of component '{component}': it takes {Permissions.Name(needed)}.";
			}
		}

		return null;
	}

	public async Task<string?> MetaRepoAsync(object caller)
	{
		var (directory, tip) = await state.Meta.GitAsync();
		return await state.Views.ViewAsync(directory, tip, await state.ComponentsAsync(), (await state.ReadableAsync((Caller)caller)).Contains);
	}

	public Task PushedAsync(object caller, string component, IReadOnlyList<PushCommand> commands, HttpContext context) =>
		state.Access.AuditAsync(((Caller)caller).Id, "git.push", component, string.Join(", ", commands.Select(command => $"{command.Ref} {Short(command.Old)}..{Short(command.New)}")),
			ServerApp.RemoteOf(context));

	private static string Short(string id) => id[..Math.Min(7, id.Length)];
}
