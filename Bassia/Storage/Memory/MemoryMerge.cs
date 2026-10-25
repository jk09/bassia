namespace Bassia.Storage.Memory;

using System.Text;

/// <summary>
/// A file merge driver run in-process by the memory backend, in place of the driver's command: it gets the base (null
/// when the file is new on both sides), ours and theirs, and the file's path, and returns whether it merged cleanly, the
/// merged content (with its own markers when not) and what it wrote to stderr (where its warnings are).
/// </summary>
internal delegate (bool Clean, string Content, string Error) InProcessMergeDriver(string? @base, string ours, string theirs, string path);

/// <summary>
/// The memory backend's three-way merge, at file granularity: a file only one side changed takes that side; a file both
/// sides changed the same way is taken; a file both changed differently is merged by the <see cref="FileMergeDriver"/>
/// when it handles it, and is otherwise a conflict whose content is the whole file of each side between conflict markers.
/// It is the reference for the contract's merge semantics, not a line merge.
/// </summary>
internal static class MemoryMerge
{
	internal sealed record Result(SortedDictionary<string, MemoryFile> Files, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Warnings)
	{
		public bool Clean => Conflicts.Count == 0;
	}

	public static async Task<Result> MergeAsync(
		IReadOnlyDictionary<string, MemoryFile>? @base,
		IReadOnlyDictionary<string, MemoryFile> ours,
		IReadOnlyDictionary<string, MemoryFile> theirs,
		FileMergeDriver? driver,
		IReadOnlyDictionary<string, InProcessMergeDriver>? inProcess,
		bool diff3 = false)
	{
		@base ??= new Dictionary<string, MemoryFile>();
		var files = new SortedDictionary<string, MemoryFile>(StringComparer.Ordinal);
		var conflicts = new List<string>();
		var warnings = new List<string>();
		foreach (var path in @base.Keys.Concat(ours.Keys).Concat(theirs.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
		{
			var b = @base.GetValueOrDefault(path);
			var o = ours.GetValueOrDefault(path);
			var t = theirs.GetValueOrDefault(path);
			MemoryFile? merged;
			if (Same(o, t) || Same(t, b))
			{
				merged = o;
			}
			else if (Same(o, b))
			{
				merged = t;
			}
			else if (o is not null && t is not null && driver is not null && driver.Handles(path))
			{
				var (clean, content, error) = await RunDriverAsync(driver, inProcess, Text(b), Text(o)!, Text(t)!, path);
				warnings.AddRange(driver.Warnings(error));
				merged = MemoryFile.Text(content);
				if (!clean)
				{
					conflicts.Add(path);
				}
			}
			else
			{
				merged = MemoryFile.Text(Markers(Text(o), Text(b), Text(t), diff3));
				conflicts.Add(path);
			}

			if (merged is not null)
			{
				files[path] = merged;
			}
		}

		return new Result(files, conflicts, warnings.Distinct(StringComparer.Ordinal).ToList());
	}

	/// <summary>The paths whose content differs between two snapshots, in path order.</summary>
	public static IReadOnlyList<string> Changed(IReadOnlyDictionary<string, MemoryFile> from, IReadOnlyDictionary<string, MemoryFile> to) =>
		from.Keys.Concat(to.Keys).Distinct(StringComparer.Ordinal).Where(path => !Same(from.GetValueOrDefault(path), to.GetValueOrDefault(path)))
			.Order(StringComparer.Ordinal).ToList();

	/// <summary>A unified diff in which every changed file is shown whole: removed lines, then added ones.</summary>
	public static string Diff(IReadOnlyDictionary<string, MemoryFile> from, IReadOnlyDictionary<string, MemoryFile> to)
	{
		var builder = new StringBuilder();
		foreach (var path in Changed(from, to))
		{
			var before = Lines(Text(from.GetValueOrDefault(path)));
			var after = Lines(Text(to.GetValueOrDefault(path)));
			builder.Append($"diff --git a/{path} b/{path}\n")
				.Append(from.ContainsKey(path) ? $"--- a/{path}\n" : "--- /dev/null\n")
				.Append(to.ContainsKey(path) ? $"+++ b/{path}\n" : "+++ /dev/null\n")
				.Append($"@@ -1,{before.Count} +1,{after.Count} @@\n");
			foreach (var line in before)
			{
				builder.Append('-').Append(line).Append('\n');
			}

			foreach (var line in after)
			{
				builder.Append('+').Append(line).Append('\n');
			}
		}

		return builder.ToString();
	}

	public static bool Same(MemoryFile? left, MemoryFile? right) => left?.Object == right?.Object;

	private static string? Text(MemoryFile? file) => file is null ? null : Encoding.UTF8.GetString(file.Content);

	private static List<string> Lines(string? text) =>
		text is null || text.Length == 0 ? [] : text.Replace("\r", "").TrimEnd('\n').Split('\n').ToList();

	private static string Markers(string? ours, string? @base, string? theirs, bool diff3)
	{
		static string Block(string? text) => text is null || text.Length == 0 ? "" : text.EndsWith('\n') ? text : text + "\n";

		var builder = new StringBuilder("<<<<<<< ours\n").Append(Block(ours));
		if (diff3)
		{
			builder.Append("||||||| base\n").Append(Block(@base));
		}

		return builder.Append("=======\n").Append(Block(theirs)).Append(">>>>>>> theirs\n").ToString();
	}

	/// <summary>The driver registered in-process under its name, or else its command, run through the shell on temporary files.</summary>
	private static async Task<(bool Clean, string Content, string Error)> RunDriverAsync(
		FileMergeDriver driver, IReadOnlyDictionary<string, InProcessMergeDriver>? inProcess, string? @base, string ours, string theirs, string path)
	{
		if (inProcess?.GetValueOrDefault(driver.Name) is { } function)
		{
			return function(@base, ours, theirs, path);
		}

		var scratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"bassia-merge-{Guid.NewGuid():N}")).FullName;
		try
		{
			string Write(string name, string content)
			{
				var file = Path.Combine(scratch, name);
				File.WriteAllText(file, content);
				return file;
			}

			var (o, a, b) = (Write("base", @base ?? ""), Write("ours", ours), Write("theirs", theirs));
			var command = driver.Command.Replace("%O", Quote(o)).Replace("%A", Quote(a)).Replace("%B", Quote(b))
				.Replace("%L", "7").Replace("%P", Quote(path));
			var output = new StringBuilder();
			var exitCode = await ShellCommand.RunAsync(command, scratch, new Dictionary<string, string>(), null, line =>
			{
				lock (output)
				{
					output.Append(line).Append('\n');
				}
			}, CancellationToken.None);
			return (exitCode == 0, await File.ReadAllTextAsync(a), output.ToString());
		}
		finally
		{
			Directory.Delete(scratch, recursive: true);
		}

		static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
	}
}
