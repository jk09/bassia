namespace Bassia.Interop;

using Bassia.Core.Model;
using Bassia.Core.Version;
using Bassia.Core.Workspaces;

/// <summary>
/// A merge driver run as a command in git's merge-driver convention: <c>%O</c> (base), <c>%A</c> (ours, where the
/// result is written), <c>%B</c> (theirs), <c>%L</c> (marker size) and <c>%P</c> (the path). Exit code 0 means a clean
/// merge. weave's <c>weave-driver</c> merges by language entities (tree-sitter), which is the semantic policy's default.
/// </summary>
internal sealed class CommandMergeDriver(string command) : IMergeDriver
{
	public string Name => Path.GetFileNameWithoutExtension(command.Trim().Split(' ')[0].Trim('"'));

	public async Task<byte[]?> MergeAsync(string path, byte[] baseContent, byte[] ours, byte[] theirs, CancellationToken ct = default)
	{
		var dir = Directory.CreateTempSubdirectory("bassia-merge-").FullName;
		try
		{
			var extension = Path.GetExtension(path);
			var o = Path.Combine(dir, "base" + extension);
			var a = Path.Combine(dir, "ours" + extension);
			var b = Path.Combine(dir, "theirs" + extension);
			await File.WriteAllBytesAsync(o, baseContent, ct);
			await File.WriteAllBytesAsync(a, ours, ct);
			await File.WriteAllBytesAsync(b, theirs, ct);
			var line = command.Contains("%A", StringComparison.Ordinal) ? command : command + " %O %A %B %L %P";
			line = line.Replace("%O", Quote(o)).Replace("%A", Quote(a)).Replace("%B", Quote(b)).Replace("%L", "7").Replace("%P", Quote(path));
			var exit = await ShellCommand.RunAsync(line, dir, new Dictionary<string, string>(), null, _ => { }, ct);
			return exit == 0 ? await File.ReadAllBytesAsync(a, ct) : null;
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

internal static class MergeDrivers
{
	/// <summary>The semantic driver a setting names: none when it is 'off', or a bare command that is not installed.</summary>
	public static IMergeDriver? Semantic(string setting)
	{
		if (string.IsNullOrWhiteSpace(setting) || setting.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var executable = setting.Split(' ')[0];
		if (executable.Contains('/') || executable.Contains('\\') || OnPath(executable))
		{
			return new CommandMergeDriver(setting);
		}

		// The release packages ship weave's weave-driver next to bassia.
		var bundled = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() && !Path.HasExtension(executable) ? executable + ".exe" : executable);
		return File.Exists(bundled) ? new CommandMergeDriver($"\"{bundled.Replace('\\', '/')}\"{setting[executable.Length..]}") : null;
	}

	public static bool OnPath(string name)
	{
		var extensions = OperatingSystem.IsWindows() ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';').Prepend("") : [""];
		return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Any(dir => extensions.Any(extension => File.Exists(Path.Combine(dir, name + extension))));
	}
}

/// <summary>
/// The validation hook of the semantic policy: writes the machine-merged tree to a scratch folder and runs a shell
/// command there (build and tests); exit code 0 lets the landing proceed.
/// </summary>
internal sealed class ShellValidationHook(Repository repo, string command) : IValidationHook
{
	public async Task<(bool Ok, string Message)> ValidateAsync(ObjectId tree, CancellationToken ct = default)
	{
		var dir = Directory.CreateTempSubdirectory("bassia-validate-").FullName;
		try
		{
			var state = new TreeState();
			await new CheckoutProvider(dir, repo).UpdateAsync(state, tree, ct);
			var output = new List<string>();
			var exit = await ShellCommand.RunAsync(command, dir, new Dictionary<string, string>(), null, line => { lock (output) { output.Add(line); } }, ct);
			var tail = string.Join(" | ", output.TakeLast(3));
			return exit == 0 ? (true, "validation passed") : (false, $"'{command}' exited with {exit?.ToString() ?? "a cancellation"}: {tail}");
		}
		finally
		{
			try
			{
				Directory.Delete(dir, true);
			}
			catch (IOException)
			{
			}
		}
	}
}
