namespace Bassia.Feedback;

using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

/// <summary>When a crash report is sent: never, after asking, or without asking. Unset until the user has chosen.</summary>
internal enum ReportMode { Unset, Off, Ask, Auto }

/// <summary>
/// Everything feedback keeps on this machine, under the Bassia home (<c>BASSIA_HOME</c>, else the per-user data
/// folder the installer uses): the user's choice (<c>feedback.toml</c>), approved reports whose sending failed
/// (<c>pending/</c>) and the fingerprints already filed (<c>sent.txt</c>). It is per user, not per monorepo, because a
/// crash can happen anywhere - also outside a monorepo.
/// </summary>
internal sealed class FeedbackStore(string folder)
{
	public const string SettingsFileName = "feedback.toml";

	public static FeedbackStore Default() => new(Path.Combine(Home(), "feedback"));

	public static string Home()
	{
		if (Environment.GetEnvironmentVariable("BASSIA_HOME") is { Length: > 0 } configured)
		{
			return configured;
		}

		if (OperatingSystem.IsWindows())
		{
			return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bassia");
		}

		var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
		return Path.Combine(string.IsNullOrWhiteSpace(data) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : data, "bassia");
	}

	public string Folder => folder;

	private string SettingsPath => Path.Combine(folder, SettingsFileName);

	private string PendingFolder => Path.Combine(folder, "pending");

	private string SentPath => Path.Combine(folder, "sent.txt");

	public ReportMode Mode => ReadSetting("reports") is { } text && Enum.TryParse<ReportMode>(text, ignoreCase: true, out var mode) && mode != ReportMode.Unset
		? mode
		: ReportMode.Unset;

	public string Repository => ReadSetting("repository") is { Length: > 0 } repository ? repository : ReportBuilder.DefaultRepository;

	public void SetMode(ReportMode mode)
	{
		if (mode == ReportMode.Unset)
		{
			throw new FeedbackException("Use off, ask or auto.");
		}

		Directory.CreateDirectory(folder);
		var table = ReadTable();
		table["reports"] = mode.ToString().ToLowerInvariant();
		File.WriteAllText(SettingsPath, "# Bassia's feedback settings for this user (see 'bassia feedback reports').\n" + TomlSerializer.Serialize(table));
	}

	private string? ReadSetting(string name) => ReadTable().TryGetValue(name, out var value) ? value as string : null;

	private TomlTable ReadTable()
	{
		try
		{
			return File.Exists(SettingsPath) ? TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(SettingsPath)) ?? new TomlTable() : new TomlTable();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlException)
		{
			return new TomlTable();
		}
	}

	// ----- fingerprints already filed -----

	public bool WasSent(string fingerprint) =>
		File.Exists(SentPath) && File.ReadLines(SentPath).Any(line => line.Split(' ')[0] == fingerprint);

	public void RecordSent(string fingerprint, IssueReceipt receipt)
	{
		Directory.CreateDirectory(folder);
		File.AppendAllText(SentPath, $"{fingerprint} {receipt.Number}\n");
	}

	// ----- approved reports waiting to be sent -----

	public void SavePending(IssueDraft draft)
	{
		Directory.CreateDirectory(PendingFolder);
		File.WriteAllText(Path.Combine(PendingFolder, $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(draft));
	}

	public IReadOnlyList<(string Path, IssueDraft Draft)> Pending()
	{
		var list = new List<(string, IssueDraft)>();
		if (!Directory.Exists(PendingFolder))
		{
			return list;
		}

		foreach (var path in Directory.EnumerateFiles(PendingFolder, "*.json").Order(StringComparer.Ordinal))
		{
			try
			{
				if (JsonSerializer.Deserialize<IssueDraft>(File.ReadAllText(path)) is { } draft)
				{
					list.Add((path, draft));
				}
			}
			catch (Exception ex) when (ex is IOException or JsonException)
			{
				// A damaged file is skipped; it can be deleted by hand.
			}
		}

		return list;
	}

	public void RemovePending(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Left behind, it is offered again; a second issue would be a duplicate the fingerprint check catches.
		}
	}
}
