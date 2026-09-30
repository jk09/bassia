namespace Bassia.Graph;

using System.Text;

/// <summary>
/// A fixed-size grid of characters, rendered to text in one piece. The component board needs to draw *between* its
/// rectangles - dependency edges that cross gaps and each other - which no table layout can express, so it lays its
/// cards out on this instead.
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

	public CharCanvas(int width, int height)
	{
		Width = Math.Max(1, width);
		Height = Math.Max(1, height);
		chars = new char[Height, Width];
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

	public void Set(int x, int y, char value)
	{
		if (Inside(x, y))
		{
			chars[y, x] = value;
		}
	}

	/// <summary>Writes text left to right, clipped at the canvas edge.</summary>
	public void Text(int x, int y, string text)
	{
		for (var i = 0; i < text.Length; i++)
		{
			Set(x + i, y, text[i]);
		}
	}

	/// <summary>Writes text clipped to <paramref name="width"/>, with an ellipsis when it does not fit.</summary>
	public void Text(int x, int y, string text, int width) => Text(x, y, Fit(text, width));

	/// <summary>Truncates to <paramref name="width"/>, marking the cut with a single-character ellipsis.</summary>
	public static string Fit(string text, int width)
	{
		text = text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
		return width <= 0 ? "" : text.Length <= width ? text : text[..(width - 1)] + "…";
	}

	/// <summary>A line character, merged with whatever line character is already there.</summary>
	private void Line(int x, int y, int arms)
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
	}

	/// <summary>Rows <paramref name="fromY"/>..<paramref name="toY"/> inclusive; an inverted range draws nothing.</summary>
	public void VerticalLine(int x, int fromY, int toY)
	{
		for (var y = fromY; y <= toY; y++)
		{
			Line(x, y, Up | Down);
		}
	}

	/// <summary>Columns <paramref name="fromX"/>..<paramref name="toX"/> inclusive; an inverted range draws nothing.</summary>
	public void HorizontalLine(int y, int fromX, int toX)
	{
		for (var x = fromX; x <= toX; x++)
		{
			Line(x, y, Left | Right);
		}
	}

	/// <summary>A rectangle with <paramref name="title"/> inlaid in its top edge, drawn over anything beneath it.</summary>
	public void Box(int x, int y, int width, int height, string? title = null)
	{
		for (var column = x; column < x + width; column++)
		{
			Set(column, y, '─');
			Set(column, y + height - 1, '─');
		}

		for (var row = y; row < y + height; row++)
		{
			Set(x, row, '│');
			Set(x + width - 1, row, '│');
			for (var column = x + 1; column < x + width - 1; column++)
			{
				if (row > y && row < y + height - 1)
				{
					Set(column, row, ' ');
				}
			}
		}

		Set(x, y, '┌');
		Set(x + width - 1, y, '┐');
		Set(x, y + height - 1, '└');
		Set(x + width - 1, y + height - 1, '┘');

		if (!string.IsNullOrEmpty(title))
		{
			Text(x + 2, y, " " + Fit(title, width - 6) + " ");
		}
	}

	/// <summary>
	/// An edge from a point on one box's bottom edge to a point on another's top edge, routed as down / across /
	/// down with an arrow head. The horizontal leg runs one row above the target, so edges into the same component
	/// merge into one line that forks just above it.
	/// </summary>
	public void ConnectDown(int fromX, int fromY, int toX, int toY)
	{
		if (toY - 1 <= fromY)
		{
			return;
		}

		if (fromX == toX)
		{
			VerticalLine(fromX, fromY + 1, toY - 2);
		}
		else
		{
			// The corners carry only the arms the edge actually uses, so a second edge merging into the same cell
			// adds its own arm instead of leaving a stub pointing nowhere.
			var lane = Math.Max(fromY + 1, toY - 2);
			var (leaving, arriving) = toX > fromX ? (Right, Left) : (Left, Right);
			VerticalLine(fromX, fromY + 1, lane - 1);
			Line(fromX, lane, Up | leaving);
			HorizontalLine(lane, Math.Min(fromX, toX) + 1, Math.Max(fromX, toX) - 1);
			Line(toX, lane, Down | arriving);
			VerticalLine(toX, lane + 1, toY - 2);
		}

		Set(toX, toY - 1, '▼');
	}

	/// <summary>The canvas as text, one line per row, with trailing blanks dropped per row.</summary>
	public string ToText() => Render(value => value);

	/// <summary>
	/// The canvas as plain 7-bit ASCII: box-drawing characters become <c>+ - |</c>, the arrow head <c>v</c>, and the
	/// few symbols the cards use their nearest ASCII look-alike. Trailing blanks are dropped per row.
	/// </summary>
	public string ToAscii() => Render(Ascii);

	private string Render(Func<char, char> map)
	{
		var builder = new StringBuilder();
		for (var y = 0; y < Height; y++)
		{
			var line = new StringBuilder();
			for (var x = 0; x < Width; x++)
			{
				line.Append(map(chars[y, x]));
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
}
