using Bassia;
using Bassia.CliCommands.Agent;
using Bassia.Ui;

namespace Bassia.Tests.Ui;

public class ComponentBoardTests
{
	private static ComponentStatus Component(string name, params string[] references) =>
		new(new ComponentDefinition(name, $"https://example.invalid/{name}.git", references.Select(reference => new ComponentReference(reference, reference)).ToList()),
			HasRepo: true, AnnotatedTags: 2, Branches: 1, LatestTag: "v1", RecordedRuns: 3);

	private static (ComponentBoard Board, string Markup) Render(IReadOnlyList<ComponentStatus> components, int selected = 0, int width = 200,
		IReadOnlyDictionary<string, int>? active = null)
	{
		var board = new ComponentBoard(components, new ComponentGraph(components.Select(status => status.Definition).ToList()), width);
		return (board, board.Render(selected, active ?? new Dictionary<string, int>()));
	}

	[Fact]
	public void ACardCarriesTheComponentsOwnFacts()
	{
		var (_, markup) = Render([Component("app", "lib"), Component("lib")]);

		Assert.Contains("app", markup);
		Assert.Contains("2 tags · 1 branches", markup);
		Assert.Contains("needs: lib", markup);
		Assert.Contains("used by: app", markup);
		Assert.Contains("3 runs · v1", markup);
	}

	[Fact]
	public void AReferencedComponentSitsBelowItsReferrerAndIsReachedByALine()
	{
		var (board, markup) = Render([Component("app", "lib"), Component("lib")]);
		var lines = Plain(markup);

		Assert.Equal(["app", "lib"], board.Order);
		var appRow = Array.FindIndex(lines, line => line.Contains("app"));
		var libRow = Array.FindLastIndex(lines, line => line.Contains("lib"));
		Assert.True(libRow > appRow, "the referenced component must be drawn below the one that references it");
		Assert.Contains(lines, line => line.Contains('▼'));
	}

	[Fact]
	public void TwoReferrersOfOneComponentEachReachItAtItsOwnArrowHead()
	{
		var (_, markup) = Render([Component("app", "lib"), Component("tool", "lib"), Component("lib")]);
		var lines = Plain(markup);

		// Each referrer gets its own slot on the referenced card's top edge, so the count of dependents is visible.
		var arrows = Array.FindIndex(lines, line => line.Contains('▼'));
		Assert.Equal(2, lines[arrows].Count(character => character == '▼'));
		Assert.Contains("used by: app, tool", markup);
		Assert.StartsWith("┌", lines[arrows + 1].TrimStart());
	}

	[Fact]
	public void TheSelectedCardIsMarkedAndTheOrderIsWhatTheArrowKeysWalk()
	{
		var (board, markup) = Render([Component("app", "lib"), Component("lib")], selected: 1);

		Assert.Equal(["app", "lib"], board.Order);
		Assert.Contains("▸ lib", markup);
		Assert.DoesNotContain("▸ app", markup);
	}

	[Fact]
	public void ALiveRunIsCountedOnTheCardOfEveryComponentItTouches()
	{
		var (_, markup) = Render([Component("app"), Component("lib")], active: new Dictionary<string, int> { ["app"] = 2 });

		Assert.Contains("● 2 running · 3 runs", markup);
		Assert.Contains("3 runs · v1", markup);
	}

	[Fact]
	public void AComponentWithoutALocalRepositorySaysSo()
	{
		var missing = Component("app") with { HasRepo = false };

		var (_, markup) = Render([missing]);

		Assert.Contains("no local repository", markup);
	}

	[Fact]
	public void ANarrowTerminalWrapsALevelOntoSeveralRowsInsteadOfOverflowing()
	{
		var (board, markup) = Render([Component("a"), Component("b"), Component("c")], width: 70);

		Assert.True(board.Width <= 70, $"the board must fit the terminal, was {board.Width}");
		Assert.Equal(["a", "b", "c"], board.Order);
		Assert.All(Plain(markup), line => Assert.True(line.Length <= 70, $"line too wide: '{line}'"));
	}

	[Fact]
	public void AnEmptyRegistrySaysWhatToDoInstead()
	{
		var (_, markup) = Render([]);

		Assert.Contains("no components registered", markup);
	}

	private static string[] Plain(string markup) =>
		markup.Split('\n').Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"\[[^\]]*\]", "").Replace("[[", "[").Replace("]]", "]")).ToArray();
}

public class RunBoardTests
{
	private static RunCard Card(string key, AgentRunPhase phase, bool live = false, string? output = null) =>
		new(key, RunMetadata.RunIdPrefix + key.PadRight(32, '0'), phase, "app@v0", ["app"], "claude -p \"write the docs\"",
			"metadata committed", DateTimeOffset.UtcNow.AddMinutes(-2), live ? null : DateTimeOffset.UtcNow.AddMinutes(-1), live, output);

	[Fact]
	public void ACardCarriesTheRunsOwnFacts()
	{
		var markup = new RunBoard([Card("abcdef12", AgentRunPhase.Completed)], 200).Render(0, 0);

		Assert.Contains("abcdef12", markup);
		Assert.Contains("COMPLETED", markup);
		Assert.Contains("1:00", markup);
		Assert.Contains("app", markup);
		Assert.Contains("write the docs", markup);
		Assert.Contains("metadata committed", markup);
	}

	[Fact]
	public void ALiveCardAnimatesAndAFinishedOneDoesNot()
	{
		var live = new RunBoard([Card("abcdef12", AgentRunPhase.Agent, live: true, output: "editing README.md")], 200);

		var first = live.Render(0, 0);
		var later = live.Render(0, 3);

		Assert.Contains("RUNNING", first);
		Assert.Contains("editing README.md", first);
		Assert.NotEqual(first, later);
		Assert.Equal(
			new RunBoard([Card("abcdef12", AgentRunPhase.Completed)], 200).Render(0, 0),
			new RunBoard([Card("abcdef12", AgentRunPhase.Completed)], 200).Render(0, 7));
	}

	[Fact]
	public void EveryPhaseGetsItsOwnColourAndLabel()
	{
		Assert.Equal("green", RunBoard.StyleOf(AgentRunPhase.Completed));
		Assert.Equal("red", RunBoard.StyleOf(AgentRunPhase.Failed));
		Assert.Equal("grey", RunBoard.StyleOf(AgentRunPhase.Cancelled));
		Assert.Equal("RUNNING", RunBoard.PhaseText(AgentRunPhase.Agent));
		Assert.Equal("PARTIAL", RunBoard.PhaseText(AgentRunPhase.Partial));
	}

	[Fact]
	public void TheSelectedCardIsMarked()
	{
		var cards = new[] { Card("aaaaaaaa", AgentRunPhase.Completed), Card("bbbbbbbb", AgentRunPhase.Failed) };

		var markup = new RunBoard(cards, 200).Render(1, 0);

		Assert.Contains("▸ bbbbbbbb", markup);
		Assert.DoesNotContain("▸ aaaaaaaa", markup);
	}

	[Fact]
	public void CardsWrapToTheTerminalWidth()
	{
		var cards = Enumerable.Range(0, 5).Select(i => Card($"card{i:0000}", AgentRunPhase.Completed)).ToList();

		var board = new RunBoard(cards, 80);
		var lines = board.Render(0, 0).Split('\n');

		Assert.True(board.Width <= 80);
		// Two cards per row at 80 columns, so five cards need three rows of cards.
		Assert.Equal(3 * 7 + 2, lines.Length);
	}

	[Theory]
	[InlineData(0, "0:00")]
	[InlineData(65, "1:05")]
	[InlineData(3725, "1:02:05")]
	public void ElapsedIsReadableAtEveryScale(int seconds, string expected)
	{
		Assert.Equal(expected, RunBoard.Elapsed(TimeSpan.FromSeconds(seconds)));
	}

	[Fact]
	public void AnEmptyBoardSaysHowToStartARun()
	{
		Assert.Contains("press", new RunBoard([], 200).Render(0, 0));
	}
}
