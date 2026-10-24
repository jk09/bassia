namespace Bassia.Feedback;

using System.Text.RegularExpressions;

/// <summary>
/// Takes what identifies the person or grants access out of a report before it is shown or sent: the home folder,
/// the user and machine names, credentials in URLs, and anything that looks like a token or a secret setting.
/// </summary>
internal sealed partial class Redactor(string? home, string? user, string? machine)
{
	public static Redactor ForCurrentUser() =>
		new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

	public string Redact(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}

		// Both slash styles: a stack trace and a message may spell the same folder either way.
		if (!string.IsNullOrWhiteSpace(home))
		{
			var trimmed = home.TrimEnd('\\', '/');
			foreach (var spelling in new[] { trimmed, trimmed.Replace('\\', '/'), trimmed.Replace('/', '\\') }.Distinct())
			{
				if (spelling.Length > 1)
				{
					text = text.Replace(spelling, "~", StringComparison.OrdinalIgnoreCase);
				}
			}
		}

		text = Credentials().Replace(text, "${scheme}<credentials>@");
		text = Secrets().Replace(text, "<token>");
		text = SecretSetting().Replace(text, "${name}${separator}<secret>");
		// The machine name first: it often contains the user name.
		text = Word(text, machine, "<machine>");
		return Word(text, user, "<user>");
	}

	/// <summary>Replaces <paramref name="name"/> where it stands as a word; names of one or two characters are too ambiguous to blank out.</summary>
	private static string Word(string text, string? name, string replacement) =>
		string.IsNullOrWhiteSpace(name) || name.Length < 3
			? text
			: Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])", replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	[GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+.\-]*://)[^\s/@:]+(:[^\s/@]*)?@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Credentials();

	[GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_\-]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})\b|(?<=Bearer\s)[A-Za-z0-9._~+/\-]{16,}", RegexOptions.CultureInvariant)]
	private static partial Regex Secrets();

	[GeneratedRegex(@"(?<name>\b[\w.\-]*(?:token|secret|password|passwd|api[_\-]?key)[\w.\-]*)(?<separator>\s*[=:]\s*)(?!<)[^\s,;""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex SecretSetting();
}
