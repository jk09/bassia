using Bassia;
using Bassia.Graph;

namespace Bassia.Tests.Graph;

public class ComponentBoardTests
{
	private static ComponentStatus Component(string name, params string[] references) =>
		new(new ComponentDefinition(name, $"https://example.invalid/{name}.git", references.Select(reference => new ComponentReference(reference, reference)).ToList()),
			HasRepo: true, AnnotatedTags: 2, Branches: 1, LatestTag: "v1", RecordedRuns: 3);

	private static (ComponentBoard Board, string Text) Render(IReadOnlyList<ComponentStatus> components, int width = 200,
		IReadOnlyDictionary<string, int>? active = null)
	{
		var board = new ComponentBoard(components, new ComponentGraph(components.Select(status => status.Definition).ToList()), width);
		return (board, board.RenderText(active ?? new Dictionary<string, int>()));
	}

	[Fact]
	public void ACardCarriesTheComponentsOwnFacts()
	{
		var (_, text) = Render([Component("app", "lib"), Component("lib")]);

		Assert.Contains("app", text);
		Assert.Contains("2 tags · 1 branches", text);
		Assert.Contains("needs: lib", text);
		Assert.Contains("used by: app", text);
		Assert.Contains("3 runs · v1", text);
	}

	[Fact]
	public void AReferencedComponentSitsBelowItsReferrerAndIsReachedByALine()
	{
		var (board, text) = Render([Component("app", "lib"), Component("lib")]);
		var lines = text.Split('\n');

		Assert.Equal(["app", "lib"], board.Order);
		var appRow = Array.FindIndex(lines, line => line.Contains("app"));
		var libRow = Array.FindLastIndex(lines, line => line.Contains("lib"));
		Assert.True(libRow > appRow, "the referenced component must be drawn below the one that references it");
		Assert.Contains(lines, line => line.Contains('▼'));
	}

	[Fact]
	public void TwoReferrersOfOneComponentEachReachItAtItsOwnArrowHead()
	{
		var (_, text) = Render([Component("app", "lib"), Component("tool", "lib"), Component("lib")]);
		var lines = text.Split('\n');

		// Each referrer gets its own slot on the referenced card's top edge, so the count of dependents is visible.
		var arrows = Array.FindIndex(lines, line => line.Contains('▼'));
		Assert.Equal(2, lines[arrows].Count(character => character == '▼'));
		Assert.Contains("used by: app, tool", text);
		Assert.StartsWith("┌", lines[arrows + 1].TrimStart());
	}

	[Fact]
	public void TheAsciiBoardIsTheSamePictureInSevenBitCharacters()
	{
		var components = new[] { Component("app", "lib"), Component("lib") };
		var board = new ComponentBoard(components, new ComponentGraph(components.Select(status => status.Definition).ToList()), 200);
		var active = new Dictionary<string, int> { ["lib"] = 1 };

		var ascii = board.RenderAscii(active);

		Assert.All(ascii, character => Assert.True(character <= '~', $"not 7-bit: '{character}'"));
		Assert.Equal(new string(board.RenderText(active).Select(CharCanvas.Ascii).ToArray()), ascii);
		Assert.Contains("+- app ", ascii);
		Assert.Contains("* 1 running - 3 runs", ascii);
	}

	[Fact]
	public void ALiveRunIsCountedOnTheCardOfEveryComponentItTouches()
	{
		var (_, text) = Render([Component("app"), Component("lib")], active: new Dictionary<string, int> { ["app"] = 2 });

		Assert.Contains("● 2 running · 3 runs", text);
		Assert.Contains("3 runs · v1", text);
	}

	[Fact]
	public void AComponentWithoutALocalRepositorySaysSo()
	{
		var missing = Component("app") with { HasRepo = false };

		var (_, text) = Render([missing]);

		Assert.Contains("no local repository", text);
	}

	[Fact]
	public void ANarrowWidthWrapsALevelOntoSeveralRowsInsteadOfOverflowing()
	{
		var (board, text) = Render([Component("a"), Component("b"), Component("c")], width: 70);

		Assert.True(board.Width <= 70, $"the board must fit the width, was {board.Width}");
		Assert.Equal(["a", "b", "c"], board.Order);
		Assert.All(text.Split('\n'), line => Assert.True(line.Length <= 70, $"line too wide: '{line}'"));
	}

	[Fact]
	public void AnEmptyRegistrySaysWhatToDoInstead()
	{
		var (_, text) = Render([]);

		Assert.Contains("no components registered", text);
	}
}
