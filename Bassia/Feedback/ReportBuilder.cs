namespace Bassia.Feedback;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Builds the issues Bassia files: feedback a person wrote, and the report of an unhandled exception.</summary>
internal static partial class ReportBuilder
{
	public const string DefaultRepository = "jk09/bassia";

	/// <summary>The feedback kinds and the label each one files under.</summary>
	public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["bug"] = "bug",
		["idea"] = "enhancement",
		["question"] = "question"
	};

	/// <summary>How many of the innermost stack frames identify a crash.</summary>
	private const int FingerprintFrames = 3;

	public static string Version()
	{
		var assembly = typeof(ReportBuilder).Assembly;
		return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString() ?? "unknown";
	}

	public static IssueDraft Feedback(string repository, string message, string kind, Redactor redactor)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			throw new FeedbackException("The feedback message is empty. Give it with -message <text>.");
		}

		if (!Kinds.TryGetValue(kind, out var label))
		{
			throw new FeedbackException($"Unknown feedback kind '{kind}'; use {string.Join(" | ", Kinds.Keys)}.");
		}

		var text = redactor.Redact(message.Trim());
		var firstLine = text.Split('\n', 2)[0].Trim();
		var title = $"[{kind.ToLowerInvariant()}] {Truncate(firstLine, 80)}";
		var body = new StringBuilder()
			.Append(text).Append("\n\n")
			.Append(EnvironmentLines(redactor))
			.ToString();
		return new IssueDraft(repository, title, body, [label, "feedback"]);
	}

	public static IssueDraft Crash(string repository, string command, Exception exception, Redactor redactor)
	{
		var fingerprint = Fingerprint(exception);
		var type = exception.GetType().FullName ?? exception.GetType().Name;
		var title = $"[crash] {type} in 'bassia {command}' ({fingerprint[..8]})";
		var body = new StringBuilder()
			.Append("Bassia stopped with an unhandled exception; this report was sent with the user's approval.\n\n")
			.Append("- **command:** `").Append(command).Append("`\n")
			.Append("- **exception:** `").Append(type).Append("`\n")
			.Append("- **message:** ").Append(redactor.Redact(exception.Message).ReplaceLineEndings(" ")).Append('\n')
			.Append(EnvironmentLines(redactor)).Append("\n\n")
			.Append("```\n").Append(redactor.Redact(exception.ToString())).Append("\n```\n\n")
			.Append("<!-- bassia-fingerprint: ").Append(fingerprint).Append(" -->\n")
			.ToString();
		return new IssueDraft(repository, title, body, ["bug", "crash"], fingerprint);
	}

	/// <summary>
	/// A short hash of the exception type and its innermost frames' methods - not their files, lines or messages - so
	/// the same defect hashes the same on every machine and build.
	/// </summary>
	public static string Fingerprint(Exception exception)
	{
		var builder = new StringBuilder(exception.GetType().FullName);
		var frames = Frame().Matches(exception.StackTrace ?? "").Select(match => match.Groups["method"].Value).Take(FingerprintFrames);
		foreach (var frame in frames)
		{
			builder.Append('|').Append(frame);
		}

		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant()[..12];
	}

	private static string EnvironmentLines(Redactor redactor) =>
		$"- **bassia:** {Version()}\n- **runtime:** {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
		$"- **os:** {redactor.Redact(System.Runtime.InteropServices.RuntimeInformation.OSDescription)}";

	private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

	[GeneratedRegex(@"^\s*at (?<method>[^\r\n(]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex Frame();
}
