namespace Bassia.Ui;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The component reference graph of a monorepo as drawn by the frontend: a console rendering, and Mermaid/SVG
/// exports for Markdown viewers and browsers. Tolerates a cyclic graph (drawn with a marker) so the frontend
/// can still show what <c>components.toml</c> contains when <c>bassia agent</c> would reject it.
/// </summary>
internal sealed class ComponentGraph
{
	private readonly IReadOnlyList<ComponentDefinition> components;
	private readonly Dictionary<string, List<ComponentReference>> referrers;

	public ComponentGraph(IReadOnlyList<ComponentDefinition> components)
	{
		this.components = components;
		referrers = components.ToDictionary(component => component.Name, _ => new List<ComponentReference>(), StringComparer.Ordinal);
		foreach (var component in components)
		{
			foreach (var reference in component.References)
			{
				if (referrers.TryGetValue(reference.Name, out var list))
				{
					list.Add(new ComponentReference(component.Name, reference.Path));
				}
			}
		}
	}

	/// <summary>Components nobody references: the roots the graph is drawn from. Falls back to everything when every component is referenced (cycles).</summary>
	public IReadOnlyList<ComponentDefinition> Roots()
	{
		var roots = components.Where(component => referrers[component.Name].Count == 0).ToList();
		return roots.Count > 0 ? roots : components;
	}

	/// <summary>The components that reference <paramref name="name"/>, each with the path they nest it at.</summary>
	public IReadOnlyList<ComponentReference> ReferrersOf(string name) =>
		referrers.TryGetValue(name, out var list) ? list : [];

	/// <summary>Tree-style text: each root expanded through its references. A component reached twice is drawn twice; a cycle is cut with a marker.</summary>
	public string RenderText()
	{
		var builder = new StringBuilder();
		if (components.Count == 0)
		{
			return "(no components registered)";
		}

		foreach (var root in Roots())
		{
			builder.Append(root.Name).Append('\n');
			RenderChildren(builder, root, "", new HashSet<string>(StringComparer.Ordinal) { root.Name });
		}

		return builder.ToString().TrimEnd('\n');
	}

	private void RenderChildren(StringBuilder builder, ComponentDefinition component, string indent, HashSet<string> path)
	{
		for (var i = 0; i < component.References.Count; i++)
		{
			var reference = component.References[i];
			var last = i == component.References.Count - 1;
			builder.Append(indent).Append(last ? "└─ " : "├─ ").Append(reference.Name);
			if (reference.Path != reference.Name)
			{
				builder.Append("  (at ").Append(reference.Path).Append(')');
			}

			var target = components.FirstOrDefault(candidate => candidate.Name == reference.Name);
			if (target is null)
			{
				builder.Append("  (not registered)\n");
				continue;
			}

			if (!path.Add(reference.Name))
			{
				builder.Append("  (cycle)\n");
				continue;
			}

			builder.Append('\n');
			RenderChildren(builder, target, indent + (last ? "   " : "│  "), path);
			path.Remove(reference.Name);
		}
	}

	/// <summary>A Markdown document with one <c>mermaid</c> fenced graph (top-down), renderable by Markdown viewers.</summary>
	public string ToMermaidMarkdown()
	{
		var builder = new StringBuilder();
		builder.Append("# Components\n\n```mermaid\ngraph TD\n");
		foreach (var component in components)
		{
			builder.Append("    ").Append(MermaidId(component.Name)).Append("[\"").Append(component.Name.Replace("\"", "#quot;")).Append("\"]\n");
		}

		foreach (var component in components)
		{
			foreach (var reference in component.References)
			{
				builder.Append("    ").Append(MermaidId(component.Name));
				builder.Append(reference.Path != reference.Name ? $" -->|{reference.Path.Replace("|", "#124;")}| " : " --> ");
				builder.Append(MermaidId(reference.Name)).Append('\n');
			}
		}

		builder.Append("```\n");
		return builder.ToString();
	}

	private static string MermaidId(string name) => "c_" + Regex.Replace(name, "[^A-Za-z0-9_]", "_");

	/// <summary>A standalone SVG: components as boxes in layers (a component sits below everything that references it), references as arrows.</summary>
	public string ToSvg()
	{
		const int boxWidth = 160, boxHeight = 40, horizontalGap = 40, verticalGap = 80, margin = 30;

		var levels = Levels();
		var rows = levels.GroupBy(pair => pair.Value).OrderBy(group => group.Key)
			.Select(group => group.Select(pair => pair.Key).OrderBy(name => name, StringComparer.Ordinal).ToList()).ToList();

		var positions = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
		for (var row = 0; row < rows.Count; row++)
		{
			for (var column = 0; column < rows[row].Count; column++)
			{
				positions[rows[row][column]] = (margin + column * (boxWidth + horizontalGap), margin + row * (boxHeight + verticalGap));
			}
		}

		var width = margin * 2 + Math.Max(1, rows.Count == 0 ? 0 : rows.Max(row => row.Count)) * (boxWidth + horizontalGap) - horizontalGap;
		var height = margin * 2 + Math.Max(1, rows.Count) * (boxHeight + verticalGap) - verticalGap;

		var svg = new StringBuilder();
		svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\" font-size=\"13\">\n");
		svg.Append("  <defs><marker id=\"arrow\" viewBox=\"0 0 10 10\" refX=\"10\" refY=\"5\" markerWidth=\"8\" markerHeight=\"8\" orient=\"auto-start-reverse\"><path d=\"M 0 0 L 10 5 L 0 10 z\" fill=\"#555\"/></marker></defs>\n");

		foreach (var component in components)
		{
			var (fromX, fromY) = positions[component.Name];
			foreach (var reference in component.References)
			{
				if (!positions.TryGetValue(reference.Name, out var to))
				{
					continue;
				}

				var (x1, y1) = (fromX + boxWidth / 2, fromY + boxHeight);
				var (x2, y2) = (to.X + boxWidth / 2, to.Y);
				svg.Append(CultureInfo.InvariantCulture, $"  <line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"#555\" stroke-width=\"1.5\" marker-end=\"url(#arrow)\"/>\n");
				if (reference.Path != reference.Name)
				{
					svg.Append(CultureInfo.InvariantCulture, $"  <text x=\"{(x1 + x2) / 2 + 4}\" y=\"{(y1 + y2) / 2}\" fill=\"#777\" font-size=\"11\">{Xml(reference.Path)}</text>\n");
				}
			}
		}

		foreach (var component in components)
		{
			var (x, y) = positions[component.Name];
			svg.Append(CultureInfo.InvariantCulture, $"  <rect x=\"{x}\" y=\"{y}\" width=\"{boxWidth}\" height=\"{boxHeight}\" rx=\"6\" fill=\"#eef3fb\" stroke=\"#3b5b8f\" stroke-width=\"1.5\"/>\n");
			svg.Append(CultureInfo.InvariantCulture, $"  <text x=\"{x + boxWidth / 2}\" y=\"{y + boxHeight / 2 + 5}\" text-anchor=\"middle\" fill=\"#1c2b45\">{Xml(component.Name)}</text>\n");
		}

		svg.Append("</svg>\n");
		return svg.ToString();
	}

	/// <summary>Layer of each component: 0 for roots, otherwise one below its deepest referrer. Cycles are cut at the first repeated component.</summary>
	internal Dictionary<string, int> Levels()
	{
		var levels = new Dictionary<string, int>(StringComparer.Ordinal);
		var path = new HashSet<string>(StringComparer.Ordinal);

		int LevelOf(string name)
		{
			if (levels.TryGetValue(name, out var known))
			{
				return known;
			}

			if (!path.Add(name))
			{
				return 0;
			}

			var level = referrers[name].Count == 0 ? 0 : referrers[name].Max(referrer => LevelOf(referrer.Name) + 1);
			path.Remove(name);
			levels[name] = level;
			return level;
		}

		foreach (var component in components)
		{
			LevelOf(component.Name);
		}

		return levels;
	}

	private static string Xml(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
