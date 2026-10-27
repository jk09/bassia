namespace Bassia.Core;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Bassia.Split;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// The local cache of task results: under <see cref="Folder"/>, one folder per key holding <c>result.toml</c>,
/// <c>task.log</c> and the task's declared outputs under <c>outputs/</c>. Only tasks marked <c>cache = true</c> are
/// looked up, and only passed runs are stored.
/// </summary>
internal sealed class TaskCache(string folder) : Storage.ITaskCache
{
	public const string FolderName = ".task-cache";

	public string Folder { get; } = folder;

	public string Location => Folder;

	public static TaskCache In(string workspaceDir) => new(Path.Combine(workspaceDir, FolderName));

	/// <summary>
	/// The key of <paramref name="task"/>: a SHA-256 over its name, command, dependencies and outputs, the platform, and
	/// the fingerprint of its component and of each component in its closure (sorted by name). Null when a fingerprint is
	/// missing - the task then runs uncached.
	/// </summary>
	public static string? Key(ComponentRegistry registry, PlannedTask task, IReadOnlyDictionary<string, string> fingerprints)
	{
		var text = new StringBuilder()
			.Append("task\n").Append(task.Task.Name).Append('\n')
			.Append("command\n").Append(task.Task.Command).Append('\n')
			.Append("depends_on\n").AppendJoin('\n', task.Task.DependsOn).Append('\n')
			.Append("outputs\n").AppendJoin('\n', task.Task.OutputPatterns).Append('\n')
			.Append("platform\n").Append(RuntimeInformation.RuntimeIdentifier).Append('\n');
		foreach (var component in registry.Closure([task.Component]).OrderBy(component => component.Name, StringComparer.Ordinal))
		{
			if (!fingerprints.TryGetValue(component.Name, out var fingerprint))
			{
				return null;
			}

			text.Append("component\n").Append(component.Name).Append('\n').Append(fingerprint).Append('\n');
		}

		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
	}

	/// <summary>
	/// The cached result under <paramref name="key"/>, with its outputs restored into <paramref name="componentFolder"/>
	/// and its log copied to <paramref name="logPath"/>; null when there is none.
	/// </summary>
	public Task<TaskResult?> TryRestoreAsync(string key, PlannedTask task, string componentFolder, string logPath) =>
		Task.FromResult(TryRestore(key, task, componentFolder, logPath));

	private TaskResult? TryRestore(string key, PlannedTask task, string componentFolder, string logPath)
	{
		var entry = Path.Combine(Folder, key);
		var resultPath = Path.Combine(entry, "result.toml");
		if (!File.Exists(resultPath))
		{
			return null;
		}

		double seconds;
		try
		{
			var table = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(resultPath)) ?? new TomlTable();
			seconds = table.TryGetValue("seconds", out var value) && value is double number ? number : 0;
		}
		catch (TomlException)
		{
			return null;
		}

		var outputs = Path.Combine(entry, "outputs");
		if (Directory.Exists(outputs))
		{
			foreach (var file in Directory.EnumerateFiles(outputs, "*", SearchOption.AllDirectories))
			{
				var target = Path.Combine(componentFolder, Path.GetRelativePath(outputs, file));
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.Copy(file, target, overwrite: true);
			}
		}

		var log = Path.Combine(entry, "task.log");
		Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
		if (File.Exists(log))
		{
			File.Copy(log, logPath, overwrite: true);
		}
		else
		{
			File.WriteAllText(logPath, "");
		}

		return new TaskResult(task.Component, task.Task.Name, task.Task.Command, 0, seconds, logPath, Cached: true);
	}

	/// <summary>
	/// Stores a passed <paramref name="result"/> under <paramref name="key"/>: its log and the files of
	/// <paramref name="componentFolder"/> its task's outputs match, leaving out <paramref name="nested"/> (the reference
	/// paths). Written to a temporary folder first, so a reader never sees half an entry.
	/// </summary>
	public Task StoreAsync(string key, PlannedTask task, TaskResult result, string componentFolder, IReadOnlyList<string> nested)
	{
		Store(key, task, result, componentFolder, nested);
		return Task.CompletedTask;
	}

	private void Store(string key, PlannedTask task, TaskResult result, string componentFolder, IReadOnlyList<string> nested)
	{
		if (!result.Ok || result.Cached)
		{
			return;
		}

		var entry = Path.Combine(Folder, key);
		if (Directory.Exists(entry))
		{
			return;
		}

		var staging = Path.Combine(Folder, $".{key}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path.Combine(staging, "outputs"));
		try
		{
			if (task.Task.OutputPatterns.Count > 0)
			{
				var patterns = new PathPatterns(task.Task.OutputPatterns);
				foreach (var file in Files(componentFolder, nested).Where(patterns.Matches))
				{
					var target = Path.Combine(staging, "outputs", file);
					Directory.CreateDirectory(Path.GetDirectoryName(target)!);
					File.Copy(Path.Combine(componentFolder, file), target);
				}
			}

			if (File.Exists(result.LogPath))
			{
				File.Copy(result.LogPath, Path.Combine(staging, "task.log"));
			}

			File.WriteAllText(Path.Combine(staging, "result.toml"), TomlSerializer.Serialize(new TomlTable
			{
				["component"] = task.Component,
				["task"] = task.Task.Name,
				["command"] = task.Task.Command,
				["seconds"] = result.Seconds,
				["stored"] = DateTimeOffset.UtcNow.ToString("o")
			}));

			try
			{
				Directory.Move(staging, entry);
			}
			catch (IOException) when (Directory.Exists(entry))
			{
				// Another process stored the same key first; its entry is as good as ours.
			}
		}
		finally
		{
			if (Directory.Exists(staging))
			{
				Directory.Delete(staging, recursive: true);
			}
		}
	}

	/// <summary>How many entries the cache holds and their size in bytes.</summary>
	public Task<(int Entries, long Bytes)> SizeAsync() => Task.FromResult(Size());

	private (int Entries, long Bytes) Size() =>
		!Directory.Exists(Folder) ? (0, 0)
		: (Directory.EnumerateDirectories(Folder).Count(entry => !Path.GetFileName(entry).StartsWith('.')),
			Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length));

	/// <summary>Removes every entry.</summary>
	public Task ClearAsync()
	{
		Clear();
		return Task.CompletedTask;
	}

	private void Clear()
	{
		if (Directory.Exists(Folder))
		{
			Directory.Delete(Folder, recursive: true);
		}
	}

	/// <summary>The files under <paramref name="folder"/>, relative and '/' separated, without <c>.git</c>, links or <paramref name="nested"/>.</summary>
	public static IEnumerable<string> Files(string folder, IReadOnlyList<string> nested)
	{
		var skip = nested.Select(path => path.Replace('\\', '/').TrimEnd('/')).Append(".git").ToHashSet(StringComparer.Ordinal);
		var pending = new Stack<string>([folder]);
		while (pending.TryPop(out var directory))
		{
			foreach (var file in Directory.EnumerateFiles(directory))
			{
				yield return Path.GetRelativePath(folder, file).Replace('\\', '/');
			}

			foreach (var child in Directory.EnumerateDirectories(directory))
			{
				var relative = Path.GetRelativePath(folder, child).Replace('\\', '/');
				if (!skip.Contains(relative) && !DirectoryLinks.IsLink(child))
				{
					pending.Push(child);
				}
			}
		}
	}
}
