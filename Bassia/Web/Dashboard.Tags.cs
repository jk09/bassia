namespace Bassia.Web;

using System.Text;
using Bassia.Integration;
using Microsoft.AspNetCore.Http;
using static Bassia.Web.Html;

/// <summary>
/// Tags as units of progress: the chart of every tag across the components in time order, and per tag the components
/// it spans, what made it, the runs that started from it and where its changes went.
/// </summary>
internal sealed partial class Dashboard
{
	private static readonly string[] TagKinds = ["baseline", "run", "integration", "unwind", "split"];

	private const int TagColumns = 60;

	private async Task<IResult> TagsAsync(HttpRequest request)
	{
		var state = await LoadAsync();
		var kind = request.Query["kind"].ToString();
		var multi = request.Query["multi"] == "1";
		var component = request.Query["c"].ToString();
		var stories = (await MultiComponentTag.ReadAllAsync(state.Monorepo))
			.Select(tag => TagStory.For(tag, state.Runs, state.Integrations))
			.Where(story => kind.Length == 0 || story.Kind == kind)
			.Where(story => !multi || story.Tag.Components.Count > 1)
			.Where(story => component.Length == 0 || story.Tag.Components.Any(entry => entry.Component == component))
			.OrderBy(story => story.Date, StringComparer.Ordinal).ThenBy(story => story.Tag.Name, StringComparer.Ordinal)
			.ToList();

		string Filter(string label, string query, bool active) => $"<a class=\"filter{(active ? " active" : "")}\" href=\"/tags{query}\">{E(label)}</a>";
		var body = new StringBuilder("<div class=\"filters\">");
		body.Append(Filter("all", "", kind.Length == 0 && !multi));
		foreach (var candidate in TagKinds)
		{
			body.Append(Filter(candidate, $"?kind={candidate}", kind == candidate));
		}

		body.Append(Filter("across components", "?multi=1", multi)).Append("</div>");
		body.Append("<p class=\"muted\">One column per tag, oldest left; a mark where the tag is in a component, joined when it spans several. " +
			"Click a column for the tag's story.</p>");

		var shown = stories.TakeLast(TagColumns).ToList();
		if (stories.Count > shown.Count)
		{
			body.Append($"<p class=\"muted\">The latest {shown.Count} of {stories.Count} tags.</p>");
		}

		var rows = state.Monorepo.Components.Select(entry => entry.Name).Where(name => component.Length == 0 || name == component).ToList();
		body.Append(Charts.TagMatrix(rows, shown.Select(story => new Charts.TagColumn(story.Tag.Name, story.Kind,
			story.Tag.Components.Select(entry => entry.Component).ToHashSet(StringComparer.Ordinal), story.Date)).ToList()));

		body.Append($"<p>{Cmd("bassia tag create -tag <name> -select <component[@ref],...>")} {Cmd("bassia tag list -min 2")}</p>");
		body.Append("<table class=\"list\"><tr><th>Tag</th><th>Kind</th><th>Date</th><th>Components</th><th>Made by</th><th>Runs started from it</th><th>Integrated by</th></tr>");
		foreach (var story in Enumerable.Reverse(stories))
		{
			body.Append($"""
				<tr><td><a class="tagchip k-{story.Kind}" href="/tag?name={Url(story.Tag.Name)}">{E(story.Tag.Name)}</a></td><td>{E(story.Kind)}</td><td>{Time(story.Date)}</td>
				<td>{string.Join(" ", story.Tag.Components.Select(entry => $"<a class=\"chip\" href=\"{ComponentHref(entry.Component)}\">{E(entry.Component)}</a>"))}</td>
				<td>{MadeBy(story)}</td><td>{string.Join(" ", story.StartedRuns.Select(run => RunLink(run.RunId)))}</td>
				<td>{string.Join(" ", story.IntegratedBy.Select(record => IntegrationLink(record.IntegrationId)))}</td></tr>
				""");
		}

		body.Append("</table>");
		return View("/tags", "Tags", body.ToString());
	}

	private static string MadeBy(TagStory story) =>
		story.MadeByRun is { } run ? "run " + RunLink(run.RunId)
		: story.MadeByIntegration is { } record ? "integration " + IntegrationLink(record.IntegrationId)
		: story.Kind switch { "unwind" => "submodule unwinding", "split" => "a split", _ => "<span class=\"muted\">set by hand</span>" };

	private async Task<IResult> TagAsync(HttpRequest request)
	{
		var name = request.Query["name"].ToString();
		var state = await LoadAsync();
		var tag = (await MultiComponentTag.ReadAllAsync(state.Monorepo)).FirstOrDefault(candidate => candidate.Name == name);
		if (tag is null)
		{
			return NotFound($"No component has a tag '{name}'.");
		}

		var story = TagStory.For(tag, state.Runs, state.Integrations);
		var body = new StringBuilder($"""
			<div class="split"><section>
			<table class="list facts">
			<tr><td>Kind</td><td><span class="tagchip k-{story.Kind}">{E(story.Kind)}</span></td></tr>
			<tr><td>Spans</td><td>{tag.Components.Count} component(s)</td></tr>
			<tr><td>Made by</td><td>{MadeBy(story)}</td></tr>
			<tr><td>Runs started from it</td><td>{(story.StartedRuns.Count == 0 ? "<span class=\"muted\">none</span>" : string.Join(" ", story.StartedRuns.Select(run => RunLink(run.RunId))))}</td></tr>
			<tr><td>Integrated by</td><td>{(story.IntegratedBy.Count == 0 ? "<span class=\"muted\">none</span>" : string.Join(" ", story.IntegratedBy.Select(record => IntegrationLink(record.IntegrationId))))}</td></tr>
			</table></section><section>
			<h3>Commands</h3>
			<p>{Cmd($"bassia run start -select {tag.Select} -prompt \"...\"")}</p>
			<p>{Cmd($"bassia tag show {tag.Name}")}</p>
			</section></div>
			<h2>Where it came from and where it went</h2>
			{Lineage(state, story)}
			<h2>In each component</h2>
			<table class="list"><tr><th>Component</th><th>Commit</th><th>Annotated</th><th>Date</th><th>Subject</th></tr>
			""");
		foreach (var entry in tag.Components)
		{
			body.Append($"<tr><td><a href=\"{ComponentHref(entry.Component)}\">{E(entry.Component)}</a></td><td><a class=\"hash\" href=\"/commit/{Url(entry.Component)}/{Url(entry.Commit)}\">{E(Short(entry.Commit))}</a></td>" +
				$"<td>{(entry.Annotated ? "yes" : "<span class=\"muted\">no</span>")}</td><td>{Time(entry.Date)}</td><td>{E(entry.Subject)}</td></tr>");
		}

		body.Append("</table>");
		return View("/tags", $"Tag {tag.Name}", body.ToString());
	}

	/// <summary>
	/// The tag's lineage as a flow: for a baseline, the runs that started from it and onwards; for a run's result, that
	/// run and onwards; for an integration's result, the runs it integrated and their results.
	/// </summary>
	private static string Lineage(State state, TagStory story)
	{
		var nodes = new List<FlowNode>();
		var edges = new List<(string, string)>();
		IEnumerable<RunMetadata> runs = story.MadeByRun is { } madeBy ? [madeBy]
			: story.MadeByIntegration is { } integration ? state.Runs.Where(run => integration.Runs.Contains(run.RunId))
			: story.StartedRuns;
		foreach (var run in runs)
		{
			AddRun(nodes, edges, state, run);
		}

		var results = nodes.Where(node => node.Id.StartsWith("t:", StringComparison.Ordinal)).Select(node => node.Id[2..]).ToHashSet(StringComparer.Ordinal);
		if (story.MadeByIntegration is { } made)
		{
			AddIntegrations(nodes, edges, [made], made.AllSteps.Select(step => step.SourceTag).ToHashSet(StringComparer.Ordinal), made.IntegrationId);
		}
		else
		{
			AddIntegrations(nodes, edges, state.Integrations, results);
		}

		if (nodes.All(node => node.Label != story.Tag.Name))
		{
			nodes.Add(new FlowNode("b:" + story.Tag.Name, 0, story.Tag.Name, string.Join(", ", story.Tag.Components.Select(entry => entry.Component)), null, "baseline"));
		}

		// The tag itself stands out wherever it is drawn.
		nodes = nodes.Select(node => node.Label == story.Tag.Name && node.Id is ['b' or 't', ':', ..] ? node with { Kind = node.Kind + " current" } : node).ToList();
		return Charts.Flow(nodes, edges, FlowColumns);
	}
}
