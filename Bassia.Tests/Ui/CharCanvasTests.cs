using Bassia.Ui;

namespace Bassia.Tests.Ui;

public class CharCanvasTests
{
	[Fact]
	public void Box_DrawsARectangleWithItsTitleInTheTopEdge()
	{
		var canvas = new CharCanvas(12, 4);

		canvas.Box(0, 0, 12, 4, title: "app");

		Assert.Equal(
			"┌─ app ────┐\n" +
			"│          │\n" +
			"│          │\n" +
			"└──────────┘",
			canvas.ToMarkup());
	}

	[Fact]
	public void Box_CoversWhateverWasDrawnUnderIt()
	{
		var canvas = new CharCanvas(12, 3);
		canvas.HorizontalLine(1, 0, 11);

		canvas.Box(2, 0, 6, 3);

		Assert.Equal("──│    │────", canvas.ToMarkup().Split('\n')[1]);
	}

	[Fact]
	public void Text_TruncatesWithAnEllipsisWhenItDoesNotFit()
	{
		var canvas = new CharCanvas(10, 1);

		canvas.Text(0, 0, "a-very-long-value", 8);

		Assert.Equal("a-very-…", canvas.ToMarkup());
	}

	[Fact]
	public void CrossingLines_MergeIntoTheCharacterWithBothSetsOfArms()
	{
		var canvas = new CharCanvas(5, 3);

		canvas.HorizontalLine(1, 0, 4);
		canvas.VerticalLine(2, 0, 2);

		Assert.Equal("  │\n──┼──\n  │", canvas.ToMarkup());
	}

	[Fact]
	public void ConnectDown_RoutesDownAcrossAndDownWithAnArrowHead()
	{
		var canvas = new CharCanvas(14, 6);

		canvas.ConnectDown(1, 0, 11, 5);

		Assert.Equal(
			"\n" +
			" │\n" +
			" │\n" +
			" └─────────┐\n" +
			"           ▼\n",
			canvas.ToMarkup());
	}

	[Fact]
	public void ConnectDown_TwoEdgesIntoOneColumnForkInsteadOfOverwritingEachOther()
	{
		var canvas = new CharCanvas(14, 5);

		canvas.ConnectDown(0, 0, 6, 4);
		canvas.ConnectDown(12, 0, 6, 4);

		// The two lanes share row 2 and meet above the target, which needs the three-armed character.
		Assert.Equal("└─────┬─────┘", canvas.ToMarkup().Split('\n')[2]);
		Assert.Equal("      ▼", canvas.ToMarkup().Split('\n')[3]);
	}

	[Fact]
	public void ToMarkup_WrapsEachStyledRunAndEscapesMarkupInTheText()
	{
		var canvas = new CharCanvas(12, 1);
		canvas.Text(0, 0, "ab", "green");
		canvas.Text(4, 0, "[x]", "red");

		Assert.Equal("[green]ab[/]  [red][[x]][/]", canvas.ToMarkup());
	}

	[Fact]
	public void ToMarkup_KeepsTheGapBetweenCardsButDropsTheTrailingPadding()
	{
		var canvas = new CharCanvas(30, 1);
		canvas.Text(0, 0, "aa", "green");
		canvas.Text(6, 0, "bb", "green");

		Assert.Equal("[green]aa[/]    [green]bb[/]", canvas.ToMarkup());
	}
}
