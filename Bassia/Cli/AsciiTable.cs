namespace Bassia.Cli;

using System.Text;

/// <summary>
/// A plain-ASCII table for list results (<c>run list</c>, <c>component list</c>, ...): a header row, a rule, and one
/// row per item, columns padded to their widest cell. It is for reading at a glance; the same data is in the
/// result's keys for parsing.
/// </summary>
internal static class AsciiTable
{
	public static string Render(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, int maxCell = 48)
	{
		var cells = rows.Select(row => row.Select(cell => Fit(Ascii(cell), maxCell)).ToList()).ToList();
		var widths = headers.Select((header, column) =>
			Math.Max(header.Length, cells.Count == 0 ? 0 : cells.Max(row => column < row.Count ? row[column].Length : 0))).ToList();

		var builder = new StringBuilder();
		AppendRow(builder, headers, widths);
		builder.Append(string.Join("  ", widths.Select(width => new string('-', width)))).Append('\n');
		foreach (var row in cells)
		{
			AppendRow(builder, row, widths);
		}

		if (cells.Count == 0)
		{
			builder.Append("(none)\n");
		}

		return builder.ToString().TrimEnd('\n');
	}

	private static void AppendRow(StringBuilder builder, IReadOnlyList<string> row, IReadOnlyList<int> widths)
	{
		var line = string.Join("  ", widths.Select((width, column) => (column < row.Count ? row[column] : "").PadRight(width)));
		builder.Append(line.TrimEnd()).Append('\n');
	}

	private static string Fit(string text, int width) => text.Length <= width ? text : text[..(width - 3)] + "...";

	/// <summary>Keeps a cell on one line and within 7-bit ASCII.</summary>
	public static string Ascii(string text) =>
		new(text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

	/// <summary><c>1:35</c>, <c>12:03</c>, <c>2:14:09</c> - the elapsed time the boards show.</summary>
	public static string Elapsed(TimeSpan elapsed) =>
		elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
}
