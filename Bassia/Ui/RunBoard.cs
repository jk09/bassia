namespace Bassia.Ui;

using Bassia.CliCommands.Agent;

/// <summary>
/// The agentic-runs view: a wallboard of rectangles, one per run, in the spirit of a build wallboard. A live run
/// animates so the board is readable as "something is happening" from across the room; a finished run keeps its
/// colour so the same board is also the history.
/// </summary>
internal sealed class RunBoard
{
	private const int CardWidth = 34, CardHeight = 7, HorizontalGap = 2, VerticalGap = 1;
	private const int BarWidth = 12;
	private const string Spinner = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

	private readonly IReadOnlyList<RunCard> cards;
	private readonly int perRow;

	public RunBoard(IReadOnlyList<RunCard> cards, int width)
	{
		this.cards = cards;
		perRow = Math.Max(1, (width + HorizontalGap) / (CardWidth + HorizontalGap));
		Width = Math.Max(1, Math.Min(perRow, Math.Max(1, cards.Count)) * (CardWidth + HorizontalGap) - HorizontalGap);
	}

	public int Width { get; }

	/// <summary><paramref name="frame"/> advances once per repaint and drives the spinner and the activity bar.</summary>
	public string Render(int selected, int frame)
	{
		if (cards.Count == 0)
		{
			return "[grey](no agentic runs yet; press [bold]n[/] to start one)[/]";
		}

		var rows = (cards.Count + perRow - 1) / perRow;
		var canvas = new CharCanvas(Width, rows * (CardHeight + VerticalGap) - VerticalGap);

		for (var i = 0; i < cards.Count; i++)
		{
			DrawCard(canvas, cards[i], (i % perRow) * (CardWidth + HorizontalGap), (i / perRow) * (CardHeight + VerticalGap), i == selected, frame);
		}

		return canvas.ToMarkup();
	}

	private static void DrawCard(CharCanvas canvas, RunCard card, int x, int y, bool selected, int frame)
	{
		var style = StyleOf(card.Phase);
		var glyph = card.IsLive ? Spinner[frame % Spinner.Length] : GlyphOf(card.Phase);
		canvas.Box(x, y, CardWidth, CardHeight, selected ? "bold yellow" : style, $"{(selected ? "▸ " : "")}{card.Label} {glyph}");

		const int inner = CardWidth - 4;
		var text = x + 2;
		var phase = PhaseText(card.Phase);
		var elapsed = Elapsed(card.Elapsed);
		canvas.Text(text, y + 1, phase + new string(' ', Math.Max(1, inner - phase.Length - elapsed.Length)) + elapsed, inner, style);
		canvas.Text(text, y + 2, string.Join(", ", card.Components), inner);
		canvas.Text(text, y + 3, Bar(card, frame), inner, style);
		canvas.Text(text, y + 4, AgentCommand.SummarizeCommand(card.Command), inner, "grey");
		canvas.Text(text, y + 5, card.LastOutput ?? card.Message, inner, "grey");
	}

	/// <summary>A block sliding along the bar while the run is live, a solid bar once it has ended.</summary>
	private static string Bar(RunCard card, int frame)
	{
		if (!card.IsLive)
		{
			return new string('━', BarWidth);
		}

		var position = frame % BarWidth;
		return string.Concat(Enumerable.Range(0, BarWidth).Select(i => i == position || i == (position + 1) % BarWidth ? '▓' : '░'));
	}

	internal static string Elapsed(TimeSpan elapsed) => elapsed < TimeSpan.Zero
		? "0:00"
		: elapsed.TotalHours >= 1
			? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
			: $"{elapsed.Minutes}:{elapsed.Seconds:00}";

	internal static string PhaseText(AgentRunPhase phase) => phase switch
	{
		AgentRunPhase.Agent => "RUNNING",
		AgentRunPhase.Unknown => "UNKNOWN",
		_ => phase.ToString().ToUpperInvariant()
	};

	internal static string StyleOf(AgentRunPhase phase) => phase switch
	{
		AgentRunPhase.Queued => "yellow",
		AgentRunPhase.Preparing => "aqua",
		AgentRunPhase.Agent => "deepskyblue1",
		AgentRunPhase.Finalizing => "mediumpurple",
		AgentRunPhase.Completed => "green",
		AgentRunPhase.Partial => "orange1",
		AgentRunPhase.Failed => "red",
		_ => "grey"
	};

	private static char GlyphOf(AgentRunPhase phase) => phase switch
	{
		AgentRunPhase.Completed => '✔',
		AgentRunPhase.Partial => '◐',
		AgentRunPhase.Failed => '✘',
		AgentRunPhase.Cancelled => '■',
		AgentRunPhase.Abandoned => '∅',
		_ => '·'
	};
}
