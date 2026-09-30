using Bassia.Graph;

namespace Bassia.Tests.Graph;

public class ComponentGraphTests
{
	private static readonly ComponentDefinition App = new("app", "https://example.invalid/app.git", [new ComponentReference("lib", "libs/lib"), new ComponentReference("core", "core")]);
	private static readonly ComponentDefinition Lib = new("lib", "https://example.invalid/lib.git", [new ComponentReference("core", "core")]);
	private static readonly ComponentDefinition Core = new("core", "https://example.invalid/core.git", []);

	[Fact]
	public void RenderText_DrawsRootsAndNestedReferencesWithCustomPaths()
	{
		var text = new ComponentGraph([App, Lib, Core]).RenderText();

		Assert.Equal(string.Join('\n',
			"app",
			"├─ lib  (at libs/lib)",
			"│  └─ core",
			"└─ core"), text);
	}

	[Fact]
	public void RenderText_EmptyGraph_SaysSo()
	{
		Assert.Equal("(no components registered)", new ComponentGraph([]).RenderText());
	}

	[Fact]
	public void RenderText_Cycle_IsCutWithMarkerInsteadOfRecursingForever()
	{
		var a = new ComponentDefinition("a", "", [new ComponentReference("b", "b")]);
		var b = new ComponentDefinition("b", "", [new ComponentReference("a", "a")]);

		var text = new ComponentGraph([a, b]).RenderText();

		Assert.Contains("(cycle)", text);
	}

	[Fact]
	public void Levels_PlaceAComponentBelowItsDeepestReferrer()
	{
		var levels = new ComponentGraph([App, Lib, Core]).Levels();

		Assert.Equal(0, levels["app"]);
		Assert.Equal(1, levels["lib"]);
		Assert.Equal(2, levels["core"]);
	}

	[Fact]
	public void ToMermaidMarkdown_ProducesFencedTopDownGraphWithEdgeLabelsForCustomPaths()
	{
		var markdown = new ComponentGraph([App, Lib, Core]).ToMermaidMarkdown();

		Assert.StartsWith("# Components", markdown);
		Assert.Contains("```mermaid\ngraph TD\n", markdown);
		Assert.Contains("c_app[\"app\"]", markdown);
		Assert.Contains("c_app -->|libs/lib| c_lib", markdown);
		Assert.Contains("c_lib --> c_core", markdown);
		Assert.EndsWith("```\n", markdown);
	}

	[Fact]
	public void ToSvg_ProducesOneBoxPerComponentAndOneArrowPerReference()
	{
		var svg = new ComponentGraph([App, Lib, Core]).ToSvg();

		Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg);
		Assert.Equal(3, svg.Split("<rect ").Length - 1);
		Assert.Equal(3, svg.Split("<line ").Length - 1);
		Assert.Contains(">app</text>", svg);
		Assert.Contains(">libs/lib</text>", svg);
		Assert.EndsWith("</svg>\n", svg);
	}

	[Fact]
	public void ReferrersOf_ListsWhoNestsAComponentAndWhere()
	{
		var referrers = new ComponentGraph([App, Lib, Core]).ReferrersOf("core");

		Assert.Equal(["app", "lib"], referrers.Select(referrer => referrer.Name).Order().ToArray());
		Assert.Empty(new ComponentGraph([App, Lib, Core]).ReferrersOf("app"));
	}
}
