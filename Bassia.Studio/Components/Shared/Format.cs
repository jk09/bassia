namespace Bassia.Studio.Components.Shared;

using System.Globalization;
using Bassia.Core.Model;

/// <summary>Small display helpers shared by the pages.</summary>
public static class Format
{
	public static string Time(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

	public static string Size(long bytes) => bytes switch
	{
		< 1024 => $"{bytes} B",
		< 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
		< 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MiB",
		_ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB"
	};

	public static string FirstLine(string text, int max = 90)
	{
		var line = text.Split('\n')[0];
		return line.Length <= max ? line : line[..(max - 1)] + "…";
	}

	public static string Kind(EntryKind kind) => kind.ToString().ToLowerInvariant();

	/// <summary>A query string value, escaped.</summary>
	public static string Q(string value) => Uri.EscapeDataString(value);

	/// <summary>The link to a path in the depot browser at a seq (or now).</summary>
	public static string DepotLink(string path, long? seq = null) =>
		"/depot?path=" + Q(path) + (seq is { } at ? "&seq=" + at.ToString(CultureInfo.InvariantCulture) : "");

	/// <summary>The parent folder of a path ("" for the root).</summary>
	public static string Parent(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "";
}
