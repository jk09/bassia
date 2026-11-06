namespace Bassia.Web;

using System.Globalization;
using System.Text;
using static Bassia.Web.Html;

/// <summary>A node of a left-to-right flow (a stack of workspaces, a run's path to mainline): its column decides where it is drawn.</summary>
internal sealed record FlowNode(string Id, int Column, string Label, string? Sub, string? Href, string Kind);

/// <summary>One part of a stacked bar or a donut: a count, its label and the CSS class that colours it.</summary>
internal sealed record Segment(string Label, int Count, string Class);

/// <summary>A run as a bar on the run chart.</summary>
internal sealed record RunBar(string RunId, string Label, string Status, DateTimeOffset Start, DateTimeOffset? End, bool Live, string Detail);

/// <summary>
/// The dashboard's pictures, drawn on the server as inline SVG (or plain HTML boxes) so every page shows them without
/// JavaScript and without loading anything from elsewhere. Colours come from CSS classes in <see cref="Html.Stylesheet"/>;
/// the script only adds hover highlighting, ticking clocks and live refresh.
/// </summary>
internal static class Charts
{
	private static string N(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

	private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "\u2026";

	// ----- flows -----

	/// <summary>
	/// Nodes in columns, left to right, joined by curves: workspaces stacked on
	/// mainline and on each other, or a run's path from its base to its landing.
	/// </summary>
	public static string Flow(IReadOnlyList<FlowNode> nodes, IReadOnlyList<(string From, string To)> edges, IReadOnlyList<string> columnTitles)
	{
		if (nodes.Count == 0)
		{
			return "";
		}

		const int width = 168, height = 44, gapX = 56, gapY = 14, margin = 14, titleHeight = 22;
		var columns = nodes.GroupBy(node => node.Column).ToDictionary(group => group.Key, group => group.ToList());
		var columnCount = Math.Max(columnTitles.Count, nodes.Max(node => node.Column) + 1);
		var tallest = columns.Values.Max(list => list.Count);
		var totalWidth = margin * 2 + columnCount * (width + gapX) - gapX;
		var totalHeight = margin * 2 + titleHeight + tallest * (height + gapY) - gapY;

		var position = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
		foreach (var (column, list) in columns)
		{
			var offset = (tallest - list.Count) * (height + gapY) / 2.0;
			for (var i = 0; i < list.Count; i++)
			{
				position[list[i].Id] = (margin + column * (width + gapX), margin + titleHeight + offset + i * (height + gapY));
			}
		}

		var svg = new StringBuilder($"<svg class=\"chart flow\" viewBox=\"0 0 {totalWidth} {totalHeight}\" width=\"{totalWidth}\" role=\"img\">");
		for (var column = 0; column < columnTitles.Count; column++)
		{
			svg.Append($"<text class=\"col-title\" x=\"{N(margin + column * (width + gapX) + width / 2.0)}\" y=\"{margin + 8}\">{E(columnTitles[column])}</text>");
		}

		foreach (var (from, to) in edges.Distinct())
		{
			if (!position.TryGetValue(from, out var a) || !position.TryGetValue(to, out var b))
			{
				continue;
			}

			var (x1, y1, x2, y2) = (a.X + width, a.Y + height / 2.0, b.X, b.Y + height / 2.0);
			var bend = (x2 - x1) / 2;
			svg.Append($"<path class=\"edge\" d=\"M{N(x1)} {N(y1)} C{N(x1 + bend)} {N(y1)} {N(x2 - bend)} {N(y2)} {N(x2)} {N(y2)}\"/>");
		}

		foreach (var node in nodes)
		{
			var (nx, ny) = position[node.Id];
			var body = new StringBuilder();
			body.Append($"<g class=\"fnode k-{E(node.Kind)}\"><title>{E(node.Label)}{(node.Sub is null ? "" : " · " + E(node.Sub))}</title>");
			body.Append($"<rect x=\"{N(nx)}\" y=\"{N(ny)}\" width=\"{width}\" height=\"{height}\" rx=\"7\"/>");
			body.Append($"<text x=\"{N(nx + 10)}\" y=\"{N(ny + (node.Sub is null ? 27 : 19))}\" class=\"name\">{E(Clip(node.Label, 21))}</text>");
			if (node.Sub is not null)
			{
				body.Append($"<text x=\"{N(nx + 10)}\" y=\"{N(ny + 35)}\" class=\"sub\">{E(Clip(node.Sub, 25))}</text>");
			}

			body.Append("</g>");
			svg.Append(node.Href is null ? body.ToString() : $"<a href=\"{E(node.Href)}\">{body}</a>");
		}

		return $"<div class=\"chart-box\">{svg}</svg></div>";
	}

	// ----- runs over time -----

	/// <summary>
	/// The runs as bars over time, newest on top: start to finish, coloured by status; a live run's bar reaches up to
	/// now and is animated. The time axis spans the oldest start shown to now.
	/// </summary>
	public static string RunTimeline(IReadOnlyList<RunBar> bars, DateTimeOffset now)
	{
		if (bars.Count == 0)
		{
			return Notice("No agentic run yet. Start one with 'bassia agent start -prompt <text>'.");
		}

		const int labelWidth = 190, plotWidth = 760, rowHeight = 24, top = 26, margin = 8;
		var end = new[] { now, bars.Max(bar => bar.End ?? now) }.Max();
		var span = Math.Max(60, (end - bars.Min(bar => bar.Start)).TotalSeconds);
		var start = end.AddSeconds(-span); // at least a minute, ending now
		double X(DateTimeOffset time) => labelWidth + (time - start).TotalSeconds / span * plotWidth;
		var height = top + bars.Count * rowHeight + margin;
		var width = labelWidth + plotWidth + margin * 2;

		var svg = new StringBuilder($"<svg class=\"chart gantt\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" role=\"img\" aria-label=\"Agentic runs over time\">");
		for (var i = 0; i <= 4; i++)
		{
			var time = start.AddSeconds(span * i / 4);
			var tx = labelWidth + plotWidth * i / 4.0;
			svg.Append($"<line class=\"grid\" x1=\"{N(tx)}\" y1=\"{top - 6}\" x2=\"{N(tx)}\" y2=\"{height - margin}\"/>");
			svg.Append($"<text class=\"tick\" x=\"{N(tx)}\" y=\"{top - 10}\"{(i == 4 ? " text-anchor=\"end\"" : "")}>{E(i == 4 ? "now" : TickLabel(time, span))}</text>");
		}

		for (var i = 0; i < bars.Count; i++)
		{
			var bar = bars[i];
			var y = top + i * rowHeight;
			// A run of a few seconds still gets a bar you can see and hover; one that would run past the plot ends at its edge.
			var x2 = Math.Max(X(bar.Start) + MinBarWidth, X(bar.End ?? now));
			var x1 = X(bar.Start);
			if (x2 > labelWidth + plotWidth)
			{
				(x1, x2) = (Math.Min(x1, labelWidth + plotWidth - MinBarWidth), labelWidth + plotWidth);
			}
			svg.Append($"<a href=\"/runs/{Url(bar.RunId)}\" class=\"row\"><title>{E(bar.Label)} · {E(bar.Status)} · {E(Duration((bar.End ?? now) - bar.Start))} · {E(bar.Detail)}</title>");
			svg.Append($"<rect class=\"rowbg\" x=\"0\" y=\"{y}\" width=\"{width}\" height=\"{rowHeight}\"/>");
			svg.Append($"<circle class=\"dot s-{E(bar.Status)}{(bar.Live ? " live" : "")}\" cx=\"10\" cy=\"{y + rowHeight / 2}\" r=\"5\"/>");
			svg.Append($"<text class=\"label\" x=\"22\" y=\"{y + 16}\">{E(Clip(bar.Label, 24))}</text>");
			svg.Append($"<rect class=\"bar s-{E(bar.Status)}{(bar.Live ? " live" : "")}\" x=\"{N(x1)}\" y=\"{y + 5}\" width=\"{N(x2 - x1)}\" height=\"{rowHeight - 10}\" rx=\"4\"/>");
			svg.Append("</a>");
		}

		svg.Append($"<line class=\"now\" x1=\"{N(X(now))}\" y1=\"{top - 6}\" x2=\"{N(X(now))}\" y2=\"{height - margin}\"/>");
		svg.Append("</svg>");
		return $"<div class=\"chart-box\">{svg}</div>";
	}

	/// <summary>The narrowest bar the run chart draws, in chart units (the plot is 760 wide).</summary>
	internal const int MinBarWidth = 18;

	private static string Duration(TimeSpan span) =>
		span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s" : $"{Math.Max(0, (int)span.TotalSeconds)}s";

	private static string TickLabel(DateTimeOffset time, double span) =>
		time.ToUniversalTime().ToString(span > 2 * 86400 ? "MM-dd HH:mm" : span > 600 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);

	/// <summary>The steps a run goes through, with the current one highlighted (and pulsing while live).</summary>
	public static string PhaseSteps(string phase, bool live)
	{
		string[] steps = ["preparing", "agent", "finalizing", "done"];
		var current = phase switch
		{
			"preparing" or "queued" => 0,
			"agent" or "running" or "started" => 1,
			"finalizing" => 2,
			_ => 3
		};
		var builder = new StringBuilder($"<ol class=\"phases{(live ? " live" : "")}\">");
		for (var i = 0; i < steps.Length; i++)
		{
			var state = i < current ? "done" : i == current ? "now" : "todo";
			var label = i == 3 && current == 3 ? phase : steps[i] == "agent" ? "agent working" : steps[i];
			builder.Append($"<li class=\"{state}{(i == 3 && current == 3 ? $" s-{E(phase)}" : "")}\">{E(label)}</li>");
		}

		return builder.Append("</ol>").ToString();
	}

	// ----- proportions -----

	/// <summary>A donut of counts with the total in the middle and a legend.</summary>
	public static string Donut(IReadOnlyList<Segment> segments, string caption)
	{
		var total = segments.Sum(segment => segment.Count);
		const double radius = 46, stroke = 18, size = 120;
		var svg = new StringBuilder($"<svg class=\"chart donut\" viewBox=\"0 0 {size} {size}\" width=\"{size}\" role=\"img\" aria-label=\"{E(caption)}\">");
		svg.Append($"<circle class=\"track\" cx=\"{size / 2}\" cy=\"{size / 2}\" r=\"{radius}\" stroke-width=\"{stroke}\"/>");
		var circumference = 2 * Math.PI * radius;
		var offset = 0.0;
		foreach (var segment in segments.Where(segment => segment.Count > 0))
		{
			var length = circumference * segment.Count / total;
			svg.Append($"<circle class=\"arc {E(segment.Class)}\" cx=\"{size / 2}\" cy=\"{size / 2}\" r=\"{radius}\" stroke-width=\"{stroke}\" " +
				$"stroke-dasharray=\"{N(length)} {N(circumference - length)}\" stroke-dashoffset=\"{N(-offset)}\" transform=\"rotate(-90 {size / 2} {size / 2})\"><title>{E(segment.Label)}: {segment.Count}</title></circle>");
			offset += length;
		}

		svg.Append($"<text class=\"total\" x=\"{size / 2}\" y=\"{size / 2 + 6}\">{total}</text></svg>");
		return $"<figure class=\"donut-box\">{svg}<figcaption>{E(caption)}{Legend(segments.Where(segment => segment.Count > 0).Select(segment => (segment.Class, $"{segment.Label} {segment.Count}")).ToArray())}</figcaption></figure>";
	}

	/// <summary>A horizontal bar split by counts, e.g. how the steps of an integration are merged.</summary>
	/// <remarks>
	/// A segment of at least a quarter of the bar carries its label; a narrower one only its count, which always
	/// fits. With <paramref name="legend"/> every segment is named with its count under the bar.
	/// </remarks>
	public static string Stack(IReadOnlyList<Segment> segments, bool legend = false)
	{
		var shown = segments.Where(segment => segment.Count > 0).ToList();
		if (shown.Count == 0)
		{
			return "<div class=\"stack empty\"><span>nothing</span></div>";
		}

		var total = shown.Sum(segment => segment.Count);
		var bar = "<div class=\"stack\">" + string.Concat(shown.Select(segment =>
			$"<span class=\"{E(segment.Class)}\" style=\"flex:{segment.Count}\" title=\"{E(segment.Label)}: {segment.Count}\">" +
			$"{segment.Count}{(segment.Count * 4 >= total ? " " + E(segment.Label) : "")}</span>")) + "</div>";
		return legend ? bar + Legend(shown.Select(segment => (segment.Class, $"{segment.Count} {segment.Label}")).ToArray()) : bar;
	}

	public static string Legend(params (string Class, string Label)[] items) =>
		"<div class=\"legend\">" + string.Concat(items.Select(item => $"<span><i class=\"{E(item.Class)}\"></i>{E(item.Label)}</span>")) + "</div>";

	// ----- merge queue -----

	/// <summary>One lane of the merge queue: the base it merges onto, then each queued result in merge order.</summary>
	internal sealed record LaneItem(string RunId, string Label, string Class, string Title, string? Href);

	/// <summary>
	/// The merge queue as lanes, one per component: the base branch on the left, then the results waiting to merge
	/// onto it in the order the integration would take them, each coloured by how it would be merged.
	/// </summary>
	public static string Lanes(IReadOnlyList<(string Component, string Base, IReadOnlyList<LaneItem> Items)> lanes)
	{
		if (lanes.Count == 0)
		{
			return "";
		}

		const int labelWidth = 150, baseWidth = 96, rowHeight = 62, margin = 12, targetWidth = 1200;
		var widest = Math.Max(1, lanes.Max(lane => lane.Items.Count));

		// The longest lane spans the chart's full width: few results get room, many still get at least 116 units each.
		var step = Math.Clamp((targetWidth - labelWidth - baseWidth - margin * 2) / widest, 116, 280);
		var width = labelWidth + baseWidth + widest * step + margin * 2;
		var height = lanes.Count * rowHeight + margin;
		var svg = new StringBuilder($"<svg class=\"chart lanes\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" role=\"img\" aria-label=\"Merge queue\">");
		for (var i = 0; i < lanes.Count; i++)
		{
			var (component, baseRef, items) = lanes[i];
			var y = margin + i * rowHeight + 20;
			svg.Append($"<a href=\"/components/{Url(component)}\"><text class=\"lane-name\" x=\"{margin}\" y=\"{y + 5}\">{E(Clip(component, 18))}</text></a>");
			var lineEnd = labelWidth + baseWidth + Math.Max(0, items.Count - 1) * step + 20;
			svg.Append($"<line class=\"track\" x1=\"{lineEnd}\" y1=\"{y}\" x2=\"{width - margin}\" y2=\"{y}\"/>");
			svg.Append($"<line class=\"lane\" x1=\"{labelWidth}\" y1=\"{y}\" x2=\"{lineEnd}\" y2=\"{y}\"/>");
			svg.Append($"<rect class=\"base\" x=\"{labelWidth}\" y=\"{y - 12}\" width=\"{baseWidth - 20}\" height=\"24\" rx=\"12\"/><text class=\"base-label\" x=\"{labelWidth + (baseWidth - 20) / 2}\" y=\"{y + 4}\">{E(Clip(baseRef, 10))}</text>");
			for (var j = 0; j < items.Count; j++)
			{
				var item = items[j];
				var cx = labelWidth + baseWidth + j * step + 20;
				var body = $"<g class=\"qitem {E(item.Class)}\"><title>{E(item.Title)}</title><circle cx=\"{cx}\" cy=\"{y}\" r=\"11\"/>" +
					$"<text class=\"qlabel\" x=\"{cx}\" y=\"{y + 28}\">{E(Clip(item.Label, 16))}</text></g>";
				svg.Append(item.Href is null ? body : $"<a href=\"{E(item.Href)}\">{body}</a>");
			}
		}

		return $"<div class=\"chart-box\">{svg}</svg></div>";
	}

	/// <summary>Runs whose results collide with each other on their own, as a ring of nodes joined by red lines.</summary>
	public static string ConflictRing(IReadOnlyList<(string RunId, string Label)> runs, IReadOnlyList<(string A, string B, string Where)> conflicts)
	{
		if (runs.Count == 0)
		{
			return "";
		}

		// Labels sit outside the ring, pointing away from its centre, so neighbours never overlap.
		const double width = 560, height = 330, radius = 110, cx = width / 2, cy = height / 2;
		var position = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
		for (var i = 0; i < runs.Count; i++)
		{
			var angle = -Math.PI / 2 + 2 * Math.PI * i / runs.Count;
			position[runs[i].RunId] = runs.Count == 1 ? (cx, cy) : (cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
		}

		var svg = new StringBuilder($"<svg class=\"chart ring\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" role=\"img\" aria-label=\"Conflicts between queued runs\">");
		foreach (var (a, b, where) in conflicts)
		{
			if (position.TryGetValue(a, out var p) && position.TryGetValue(b, out var q))
			{
				svg.Append($"<line class=\"clash\" x1=\"{N(p.X)}\" y1=\"{N(p.Y)}\" x2=\"{N(q.X)}\" y2=\"{N(q.Y)}\"><title>{E(where)}</title></line>");
			}
		}

		var clashing = conflicts.SelectMany(conflict => new[] { conflict.A, conflict.B }).ToHashSet(StringComparer.Ordinal);
		foreach (var (runId, label) in runs)
		{
			var (x, y) = position[runId];
			var (dx, dy) = runs.Count == 1 ? (0.0, 1.0) : ((x - cx) / radius, (y - cy) / radius);
			var anchor = dx > 0.3 ? "start" : dx < -0.3 ? "end" : "middle";
			var (tx, ty) = (x + dx * 20, y + dy * 22 + 4);
			svg.Append($"<a href=\"/runs/{Url(runId)}\"><g class=\"rnode{(clashing.Contains(runId) ? " clashing" : "")}\"><circle cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"13\"/>" +
				$"<text x=\"{N(tx)}\" y=\"{N(ty)}\" text-anchor=\"{anchor}\">{E(Clip(label, 18))}</text></g></a>");
		}

		return $"<div class=\"chart-box\">{svg}</svg></div>";
	}

}
