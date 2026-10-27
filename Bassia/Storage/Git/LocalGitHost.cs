namespace Bassia.Storage.Git;

using Bassia.Git;
using Bassia.Storage.Cloud;

/// <summary>
/// <see cref="IGitHost"/> on the git storage: each component's repository next to the meta-repo. Pushed ref changes are
/// one <c>git update-ref --stdin</c> transaction, each change conditional on the old value, so they all happen or none.
/// </summary>
internal sealed class LocalGitHost(GitComponentStorage storage, string scratchRoot) : IGitHost
{
	public string ScratchRoot { get; } = scratchRoot;

	public bool Exists(string name) => !name.StartsWith('.') && storage.Exists(name) && Directory.Exists(storage.LocationOf(name));

	public Task<string> SyncAsync(string name) => Task.FromResult(storage.LocationOf(name));

	public Task PublishAsync(string name, IReadOnlyList<RefUpdate> updates) => UpdateRefsAsync(GitClient.In(storage.LocationOf(name)), name, updates);

	/// <summary>The updates as one ref transaction of the repository; throws <see cref="StorageException"/> when one does not hold.</summary>
	internal static async Task UpdateRefsAsync(GitClient git, string name, IReadOnlyList<RefUpdate> updates)
	{
		var current = await CloudComponentStorage.LocalRefsAsync(git, "refs");
		var script = new System.Text.StringBuilder("start\n");
		foreach (var update in updates)
		{
			var value = current.GetValueOrDefault(update.Ref);
			var line = update.Expect switch
			{
				RefExpect.Any => update.Object is null ? $"delete {update.Ref}" : $"update {update.Ref} {update.Object}",
				RefExpect.Absent or RefExpect.AbsentOrSame when value is null => update.Object is null ? null : $"create {update.Ref} {update.Object}",
				RefExpect.AbsentOrSame when value == update.Object => null,
				RefExpect.Value when value == update.ExpectedObject && value is not null =>
					update.Object is null ? $"delete {update.Ref} {value}" : $"update {update.Ref} {update.Object} {value}",
				RefExpect.FastForward when value is null => update.Object is null ? null : $"create {update.Ref} {update.Object}",
				RefExpect.FastForward when value == update.Object => null,
				RefExpect.FastForward when update.Object is not null && (await git.RunAsync(["merge-base", "--is-ancestor", value!, update.Object])).ExitCode == 0 =>
					$"update {update.Ref} {update.Object} {value}",
				_ => throw new CloudConflictException(CloudComponentStorage.Conflict(name, update, value))
			};
			if (line is not null)
			{
				script.Append(line).Append('\n');
			}
		}

		script.Append("prepare\ncommit\n");
		var result = await git.RunAsync(["update-ref", "--stdin"], script.ToString());
		if (result.ExitCode != 0)
		{
			throw new CloudConflictException($"Refs of component '{name}' moved meanwhile; nothing was published ({result.Error.Trim()}).");
		}
	}
}
