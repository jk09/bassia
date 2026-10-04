namespace Bassia.Prompt;

using System.Text;

/// <summary>One completion: the standing instructions (<see cref="System"/>) and this round's prompt.</summary>
internal sealed record LlmRequest(string System, string Prompt);

/// <summary>
/// An LLM as <c>bassia prompt</c> uses it: text in, text out. Planning needs nothing more - Bassia runs the commands
/// itself - so any model fits behind this interface: an agentic CLI, a local model, an HTTP API.
/// </summary>
internal interface ILlmBackend
{
	/// <summary>The backend's registered name (<c>llm.backend</c>).</summary>
	string Name { get; }

	/// <summary>What it runs, for the result (a command line, an endpoint).</summary>
	string Description { get; }

	Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellation);
}

/// <summary>What a backend is configured with: <c>llm.command</c> (or <c>-llm</c>), <c>-model</c> and where to run.</summary>
internal sealed record LlmSettings(string Command, string? Model, string WorkingDirectory);

/// <summary>
/// The backends by name. A new one - an HTTP API client, say - is one class implementing <see cref="ILlmBackend"/>
/// and one entry here; the prompt layer, the config key and the <c>-backend</c> switch pick it up.
/// </summary>
internal static class LlmBackends
{
	public const string DefaultName = "claude";
	public const string DefaultCommand = "claude -p";

	private static readonly Dictionary<string, (string Summary, Func<LlmSettings, ILlmBackend> Create)> Registry = new(StringComparer.OrdinalIgnoreCase)
	{
		["claude"] = ("Claude Code: llm.command (default 'claude -p') with its tools off, text output and -model as --model.",
			settings => new ClaudeCodeBackend(settings)),
		["command"] = ("Any command that reads the prompt on stdin and prints the reply, e.g. 'ollama run llama3' or 'llm -m gpt-4o'; {model} in it is replaced by -model.",
			settings => new CommandBackend(settings))
	};

	public static IReadOnlyList<string> Names => Registry.Keys.ToList();

	public static string Summary(string name) => Registry[name].Summary;

	public static ILlmBackend Create(string name, LlmSettings settings) =>
		Registry.TryGetValue(name, out var entry)
			? entry.Create(settings)
			: throw new PromptException($"Unknown LLM backend '{name}'. Known backends: {string.Join(", ", Registry.Keys)}.");
}

/// <summary>
/// A backend that runs a shell command with the request on stdin and takes its stdout as the reply. stdin rather than
/// arguments, because the catalog of every command is far longer than a Windows command line may be.
/// </summary>
internal abstract class ShellLlmBackend(LlmSettings settings) : ILlmBackend
{
	protected LlmSettings Settings { get; } = settings;

	public abstract string Name { get; }

	public string Description => CommandLine;

	protected abstract string CommandLine { get; }

	protected virtual string Input(LlmRequest request) => request.System + "\n\n" + request.Prompt;

	public async Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellation)
	{
		var output = new StringBuilder();
		var error = new StringBuilder();
		int? exitCode;
		try
		{
			exitCode = await ShellCommand.RunAsync(CommandLine, Settings.WorkingDirectory, new Dictionary<string, string>(), Input(request),
				line => { lock (output) { output.Append(line).Append('\n'); } }, cancellation,
				line => { lock (error) { error.Append(line).Append('\n'); } });
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			throw new PromptException($"Could not start the LLM command '{CommandLine}': {ex.Message}");
		}

		if (exitCode is null)
		{
			throw new OperationCanceledException(cancellation);
		}

		var reply = output.ToString().Trim();
		if (exitCode != 0 || reply.Length == 0)
		{
			var detail = (error.ToString().Trim() + "\n" + reply).Trim();
			throw new PromptException($"The LLM command '{CommandLine}' {(exitCode != 0 ? $"failed with exit code {exitCode}" : "printed nothing")}" +
				(detail.Length == 0 ? "." : $": {Truncate(detail, 2000)}"));
		}

		return reply;
	}

	private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}

/// <summary>
/// Claude Code in print mode. Its tools are turned off - it only plans, Bassia executes - and sessions are not kept.
/// </summary>
internal sealed class ClaudeCodeBackend(LlmSettings settings) : ShellLlmBackend(settings)
{
	public override string Name => "claude";

	protected override string CommandLine =>
		$"{Settings.Command.Trim()} --tools \"\" --output-format text --no-session-persistence" +
		(string.IsNullOrWhiteSpace(Settings.Model) ? "" : $" --model {Settings.Model.Trim()}");
}

/// <summary>Any command: run as configured, with <c>{model}</c> replaced by the model.</summary>
internal sealed class CommandBackend(LlmSettings settings) : ShellLlmBackend(settings)
{
	public override string Name => "command";

	protected override string CommandLine
	{
		get
		{
			var command = Settings.Command.Trim();
			if (command.Contains("{model}", StringComparison.Ordinal))
			{
				return string.IsNullOrWhiteSpace(Settings.Model)
					? throw new PromptException($"The LLM command '{command}' needs a model for {{model}}: give -model.")
					: command.Replace("{model}", Settings.Model.Trim(), StringComparison.Ordinal);
			}

			return string.IsNullOrWhiteSpace(Settings.Model)
				? command
				: throw new PromptException($"The 'command' backend takes the model in llm.command: put {{model}} where '{command}' expects it.");
		}
	}
}
