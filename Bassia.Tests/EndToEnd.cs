using System.Diagnostics;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Bassia.Tests;

/// <summary>
/// What the end-to-end tests share: driving the built <c>bassia</c> executable as a child process (which is how the
/// CLI is used, and what lets several runs be genuinely concurrent), reading its TOML results, and the Markdown proof
/// they leave behind.
/// </summary>
internal static class EndToEnd
{
	/// <summary>
	/// The upstream every component is cloned from. Point <c>BASSIA_E2E_COMPONENT_URL</c> at a local mirror to run offline.
	/// </summary>
	public static string ComponentUrl =>
		Environment.GetEnvironmentVariable("BASSIA_E2E_COMPONENT_URL") ?? "https://github.com/jk09/example.git";

	/// <summary><c>BASSIA_E2E_KEEP</c> leaves the monorepo behind so the refs named in a proof can be inspected.</summary>
	public static bool KeepMonorepo => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BASSIA_E2E_KEEP"));

	/// <summary>The bassia apphost, copied next to the test assembly by the project reference.</summary>
	public static string BassiaExecutable =>
		Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Bassia.exe" : "Bassia");

	/// <summary>
	/// Starts bassia as a child process in <paramref name="workingDirectory"/>. The process is started before the
	/// first await, so several calls made back to back really do run at the same time.
	/// </summary>
	public static async Task<CliResult> BassiaAsync(string workingDirectory, params string[] args)
	{
		var exe = BassiaExecutable;
		Assert.True(File.Exists(exe), $"'{exe}' is missing; build the solution before running the end-to-end tests.");

		var startInfo = new ProcessStartInfo(exe)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (var arg in args)
		{
			startInfo.ArgumentList.Add(arg);
		}

		using var process = Process.Start(startInfo)!;
		var standardOutput = process.StandardOutput.ReadToEndAsync();
		var standardError = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		return new CliResult(process.ExitCode, await standardOutput, await standardError, string.Join(' ', args));
	}

	/// <summary>Runs a bassia command that must succeed and returns its TOML result.</summary>
	public static async Task<TomlTable> OkAsync(string workingDirectory, params string[] args) =>
		AssertOk(await BassiaAsync(workingDirectory, args), string.Join(' ', args));

	/// <summary>Asserts the command succeeded and returns its TOML result.</summary>
	public static TomlTable AssertOk(CliResult result, string what)
	{
		Assert.True(result.ExitCode == 0, $"bassia {what} failed ({result.ExitCode}):\n{result.StandardError}\n{result.StandardOutput}");
		var toml = ParseResult(result.StandardOutput, what);
		Assert.True((bool)toml["ok"], $"bassia {what} reported failure:\n{result.StandardOutput}");
		return toml;
	}

	/// <summary>
	/// An agent command's own output can precede the result, so the result is the block starting at the last marker
	/// line bassia opens it with.
	/// </summary>
	public static TomlTable ParseResult(string standardOutput, string what)
	{
		var text = standardOutput.Replace("\r\n", "\n");
		var start = text.LastIndexOf(TomlResult.Marker + "\n", StringComparison.Ordinal);
		Assert.True(start >= 0, $"bassia {what} printed no TOML result:\n{standardOutput}");
		return TomlSerializer.Deserialize<TomlTable>(text[start..])!;
	}

	/// <summary>What the command printed before its result: the agent's own output for a foreground run.</summary>
	public static string OutputBeforeResult(string standardOutput)
	{
		var text = standardOutput.Replace("\r\n", "\n");
		var start = text.LastIndexOf(TomlResult.Marker + "\n", StringComparison.Ordinal);
		return start < 0 ? text : text[..start];
	}

	public static IReadOnlyList<TomlTable> Tables(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) && value is TomlTableArray array ? array.Cast<TomlTable>().ToList() : [];

	public static IReadOnlyList<string> Strings(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) && value is TomlArray array ? array.Cast<string>().ToList() : [];

	public static string? Text(TomlTable table, string key) => table.TryGetValue(key, out var value) ? value as string : null;

	/// <summary>
	/// Writes the report where a human (or a build) can pick it up: <c>BASSIA_E2E_PROOF</c> when set, otherwise a
	/// timestamped file named after <paramref name="name"/> in the temp folder.
	/// </summary>
	public static string WriteProof(string name, string report)
	{
		var path = Environment.GetEnvironmentVariable("BASSIA_E2E_PROOF");
		if (string.IsNullOrWhiteSpace(path))
		{
			path = Path.Combine(Path.GetTempPath(), $"{name}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md");
		}
		else if (Directory.Exists(path))
		{
			path = Path.Combine(path, $"{name}.md");
		}

		var directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(path, report);
		return Path.GetFullPath(path);
	}
}

internal sealed record CliResult(int ExitCode, string StandardOutput, string StandardError, string CommandLine = "");

/// <summary>The Markdown proof an end-to-end test emits: every fact it asserted, with the shas needed to re-check it.</summary>
internal sealed class ProofReport
{
	private readonly StringBuilder text = new();

	public void Heading(string title) => text.Append("# ").Append(title).Append("\n\n");

	public void Section(string title) => text.Append("\n## ").Append(title).Append("\n\n");

	public void Fact(string name, string? value) => text.Append("- **").Append(name).Append("**: ").Append(value).Append('\n');

	public void Line(string line) => text.Append(line).Append('\n');

	public void Block(string caption, string content) =>
		text.Append('\n').Append(caption).Append(":\n\n```\n").Append(content.Replace("\r", "").TrimEnd('\n')).Append("\n```\n");

	public override string ToString() => text.ToString();
}
