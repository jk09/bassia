namespace Bassia.Lazy;

using Bassia.Core.Version;
using Bassia.Core.Workspaces;

/// <summary>
/// What a lazy folder's server calls before a file is first used: looks the path up in the folder's tree state (read
/// again whenever a command rewrote it) and hydrates it if it is still an untouched placeholder.
/// </summary>
internal sealed class Hydrator(Repository repo, LocalWorkspace local, Action<string>? log = null)
{
	private TreeState state = local.State;
	private DateTime stateTime = File.Exists(local.StateFile) ? File.GetLastWriteTimeUtc(local.StateFile) : default;
	private readonly HashSet<string> hydrated = new(StringComparer.Ordinal);

	public bool Hydrate(string path)
	{
		if (hydrated.Contains(path))
		{
			return false;
		}

		var time = File.Exists(local.StateFile) ? File.GetLastWriteTimeUtc(local.StateFile) : default;
		if (time != stateTime)
		{
			state = TreeState.Load(local.StateFile);
			stateTime = time;
			hydrated.Clear();
		}

		if (!state.Entries.TryGetValue(path, out var entry) || !entry.Placeholder)
		{
			return false;
		}

		var wrote = Placeholders.HydrateAsync(repo, local.Files, path, entry).GetAwaiter().GetResult();
		hydrated.Add(path);
		if (wrote)
		{
			log?.Invoke($"hydrated {path} ({entry.Leaf.Size} bytes)");
		}

		return wrote;
	}
}
