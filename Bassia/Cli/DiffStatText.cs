namespace Bassia.Cli;

using System.Text;
using Bassia.Storage;

/// <summary>Line counts per file drawn the way <c>git diff --stat</c> draws them, with the summary line under them.</summary>
internal static class DiffStatText
{
	private const int BarWidth = 40;

	public static string Render(IReadOnlyList<FileStat> files)
	{
		if (files.Count == 0)
		{
			return "";
		}

		var width = files.Max(file => file.Path.Length);
		var largest = Math.Max(1, files.Max(file => Math.Max(0, file.Added) + Math.Max(0, file.Removed)));
		var builder = new StringBuilder();
		foreach (var file in files)
		{
			builder.Append(' ').Append(file.Path.PadRight(width)).Append(" | ");
			if (file.Added < 0)
			{
				builder.Append("Bin\n");
				continue;
			}

			var changed = file.Added + file.Removed;
			var scale = largest > BarWidth ? (double)BarWidth / largest : 1;
			var plus = (int)Math.Ceiling(file.Added * scale);
			var minus = (int)Math.Ceiling(file.Removed * scale);
			builder.Append(changed.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(largest.ToString(System.Globalization.CultureInfo.InvariantCulture).Length))
				.Append(' ').Append(new string('+', plus)).Append(new string('-', minus)).Append('\n');
		}

		var insertions = files.Sum(file => Math.Max(0, file.Added));
		var deletions = files.Sum(file => Math.Max(0, file.Removed));
		builder.Append($" {files.Count} file{(files.Count == 1 ? "" : "s")} changed");
		if (insertions > 0 || deletions == 0)
		{
			builder.Append($", {insertions} insertion{(insertions == 1 ? "" : "s")}(+)");
		}

		if (deletions > 0)
		{
			builder.Append($", {deletions} deletion{(deletions == 1 ? "" : "s")}(-)");
		}

		return builder.Append('\n').ToString();
	}
}
