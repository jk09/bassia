namespace Bassia.Ui;

using System.Text;
using Spectre.Console;

/// <summary>
/// A fixed-size grid of characters, each with an optional Spectre style, rendered to markup in one piece. The
/// wallboards need to draw *between* their rectangles - dependency edges that cross gaps and each other - which
/// no table or panel layout can express, so they lay their cards out on this instead.
///
/// Box-drawing characters are merged rather than overwritten: a line crossing another line yields the character
/// with both sets of arms (<c>┬</c>, <c>┼</c>, ...), so several edges into one component read as a single tree.
/// </summary>
internal sealed class CharCanvas
{
	// Arms of a box-drawing character, as a bit set. Merging two line characters is an OR of their arms.
	private const int Up = 1, Down = 2, Left = 4, Right = 8;

	/// <summary>Indexed by arm mask; index 0 is unused (no arms is not a line character).</summary>
	private const string LineChars = " ╵╷│╴┘┐┤╶└┌├─┴┬┼";

	private readonly char[,] chars;
	private readonly string?[,] styles;

	public CharCanvas(int width, int height)
	{
		Width = Math.Max(1, width);
		Height = Math.Max(1, height);
		chars = new char[Height, Width];
		styles = new string?[Height, Width];
		for (var y = 0; y < Height; y++)
		{
			for (var x = 0; x < Width; x++)
			{
				chars[y, x] = ' ';
			}
		}
	}

	public int Width { get; }
	public int Height { get; }

	private bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

	public void Set(int x, int y, char value, string? style = null)
	{
		if (Inside(x, y))
		{
			chars[y, x] = value;
			styles[y, x] = style;
		}
	}

	/// <summary>Writes text left to right, clipped at the canvas edge.</summary>
	public void Text(int x, int y, string text, string? style = null)
	{
		for (var i = 0; i < text.Length; i++)
		{
			Set(x + i, y, text[i], style);
		}
	}

	/// <summary>Writes text clipped to <paramref name="width"/>, with an ellipsis when it does not fit.</summary>
	public void Text(int x, int y, string text, int width, string? style = null) => Text(x, y, Fit(text, width), style);

	/// <summary>Truncates to <paramref name="width"/>, marking the cut with a single-character ellipsis.</summary>
	public static string Fit(string text, int width)
	{
		text = text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
		return width <= 0 ? "" : text.Length <= width ? text : text[..(width - 1)] + "…";
	}

	/// <summary>A line character, merged with whatever line character is already there.</summary>
	private void Line(int x, int y, int arms, string? style)
	{
		if (!Inside(x, y))
		{
			return;
		}

		var existing = LineChars.IndexOf(chars[y, x]);
		if (existing > 0)
		{
			arms |= existing;
		}

		chars[y, x] = LineChars[arms];
		styles[y, x] = style;
	}

	/// <summary>Rows <paramref name="fromY"/>..<paramref name="toY"/> inclusive; an inverted range draws nothing.</summary>
	public void VerticalLine(int x, int fromY, int toY, string? style = null)
	{
		for (var y = fromY; y <= toY; y++)
		{
			Line(x, y, Up | Down, style);
		}
	}

	/// <summary>Columns <paramref name="fromX"/>..<paramref name="toX"/> inclusive; an inverted range draws nothing.</summary>
	public void HorizontalLine(int y, int fromX, int toX, string? style = null)
	{
		for (var x = fromX; x <= toX; x++)
		{
			Line(x, y, Left | Right, style);
		}
	}

	/// <summary>A rectangle with <paramref name="title"/> inlaid in its top edge, drawn over anything beneath it.</summary>
	public void Box(int x, int y, int width, int height, string? style = null, string? title = null)
	{
		for (var column = x; column < x + width; column++)
		{
			Set(column, y, '─', style);
			Set(column, y + height - 1, '─', style);
		}

		for (var row = y; row < y + height; row++)
		{
			Set(x, row, '│', style);
			Set(x + width - 1, row, '│', style);
			for (var column = x + 1; column < x + width - 1; column++)
			{
				if (row > y && row < y + height - 1)
				{
					Set(column, row, ' ');
				}
			}
		}

		Set(x, y, '┌', style);
		Set(x + width - 1, y, '┐', style);
		Set(x, y + height - 1, '└', style);
		Set(x + width - 1, y + height - 1, '┘', style);

		if (!string.IsNullOrEmpty(title))
		{
			Text(x + 2, y, " " + Fit(title, width - 6) + " ", style);
		}
	}

	/// <summary>
	/// An edge from a point on one box's bottom edge to a point on another's top edge, routed as down / across /
	/// down with an arrow head. The horizontal leg runs one row above the target, so edges into the same component
	/// merge into one line that forks just above it.
	/// </summary>
	public void ConnectDown(int fromX, int fromY, int toX, int toY, string? style = null)
	{
		if (toY - 1 <= fromY)
		{
			return;
		}

		if (fromX == toX)
		{
			VerticalLine(fromX, fromY + 1, toY - 2, style);
		}
		else
		{
			// The corners carry only the arms the edge actually uses, so a second edge merging into the same cell
			// adds its own arm instead of leaving a stub pointing nowhere.
			var lane = Math.Max(fromY + 1, toY - 2);
			var (leaving, arriving) = toX > fromX ? (Right, Left) : (Left, Right);
			VerticalLine(fromX, fromY + 1, lane - 1, style);
			Line(fromX, lane, Up | leaving, style);
			HorizontalLine(lane, Math.Min(fromX, toX) + 1, Math.Max(fromX, toX) - 1, style);
			Line(toX, lane, Down | arriving, style);
			VerticalLine(toX, lane + 1, toY - 2, style);
		}

		Set(toX, toY - 1, '▼', style);
	}

	/// <summary>
	/// The whole canvas as Spectre markup, one line per row, with neighbouring cells of the same style wrapped in
	/// one tag. Trailing blanks are dropped per row so the markup does not carry the padding to the right of the
	/// widest card.
	/// </summary>
	public string ToMarkup()
	{
		var builder = new StringBuilder();
		for (var y = 0; y < Height; y++)
		{
			var end = Width - 1;
			while (end >= 0 && chars[y, end] == ' ')
			{
				end--;
			}

			var run = new StringBuilder();
			string? runStyle = null;

			for (var x = 0; x <= end; x++)
			{
				if (styles[y, x] != runStyle)
				{
					Flush(builder, run, runStyle);
					runStyle = styles[y, x];
				}

				run.Append(chars[y, x]);
			}

			Flush(builder, run, runStyle);
			if (y < Height - 1)
			{
				builder.Append('\n');
			}
		}

		return builder.ToString();
	}

	/// <summary>
	/// The canvas as plain 7-bit ASCII, without styles: box-drawing characters become <c>+ - |</c>, the arrow head
	/// <c>v</c>, and the few symbols the cards use their nearest ASCII look-alike. Trailing blanks are dropped per row.
	/// </summary>
	public string ToAscii()
	{
		var builder = new StringBuilder();
		for (var y = 0; y < Height; y++)
		{
			var line = new StringBuilder();
			for (var x = 0; x < Width; x++)
			{
				line.Append(Ascii(chars[y, x]));
			}

			builder.Append(line.ToString().TrimEnd());
			if (y < Height - 1)
			{
				builder.Append('\n');
			}
		}

		return builder.ToString();
	}

	/// <summary>The ASCII stand-in for one canvas character.</summary>
	internal static char Ascii(char value) => value switch
	{
		'─' or '╴' or '╶' => '-',
		'│' or '╵' or '╷' => '|',
		'▼' => 'v',
		'●' => '*',
		'▸' => '>',
		'·' => '-',
		'…' => '~',
		_ when value != ' ' && LineChars.Contains(value) => '+',
		_ when value > '~' => '?',
		_ => value
	};

	private static void Flush(StringBuilder builder, StringBuilder run, string? style)
	{
		if (run.Length == 0)
		{
			return;
		}

		var text = Markup.Escape(run.ToString());
		builder.Append(style is null ? text : $"[{style}]{text}[/]");
		run.Clear();
	}
}
