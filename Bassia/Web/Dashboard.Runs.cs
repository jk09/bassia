namespace Bassia.Web;

using System.Globalization;
using System.Text;
using Bassia.Integration;
using Microsoft.AspNetCore.Http;
using static Bassia.Web.Html;

/// <summary>The agentic runs: live ones as animated cards, all of them over time, and each one's path to the default branch.</summary>
internal sealed partial class Dashboard
{
	private static readonly string[] RunStatuses = ["started", "completed", "partial", "failed", "cancelled", "abandoned"];

	private async Task<IResult> RunsAsync()
	{
		var state = await LoadAsync();
		var body = $"""
			<div data-live-src="/fragment/live">{LiveCards(state.Live)}</div>
			<div class="split"><section class="wide"><h2>Runs over time</h2>{Charts.RunTimeline(Bars(state, 40), DateTimeOffset.UtcNow)}</section>
			<section><h2>By status</h2>{Charts.Donut(StatusSegments(state.Runs), "agentic runs")}</section></div>
			<p>{Cmd("bassia run start -select <component[@tag],...> -detach -prompt \"...\"")} {Cmd("bassia run list -status live")}</p>
			<h2>Recorded runs</h2>
			{RunsTable(state.Runs)}
			""";
		return View("/runs", "Agentic runs", body);
	}

	internal static IReadOnlyList<Segment> StatusSegments(IReadOnlyList<RunMetadata> runs) =>
		RunStatuses.Select(status => new Segment(status == "started" ? "live or stale" : status, runs.Count(run => run.Status == status), $"s-{status}"))
			.Append(new Segment("other", runs.Count(run => !RunStatuses.Contains(run.Status)), "s-other")).ToList();

	/// <summary>The latest runs as bars, newest on top; a live run without a record yet is drawn as preparing.</summary>
	private static IReadOnlyList<RunBar> Bars(State state, int count)
	{
		var live = state.Live.ToDictionary(run => run.RunId, StringComparer.Ordinal);
		var bars = state.Live.Where(run => run.Record is null)
			.Select(run => new RunBar(run.RunId, RunMetadata.Key(run.RunId), "preparing", run.Started, null, true, "preparing"))
			.Concat(state.Runs.Select(run =>
			{
				var isLive = live.ContainsKey(run.RunId);
				var status = isLive ? "started" : run.Status == "started" ? "stale" : run.Status;
				var detail = $"{AgentCommand(run)} · {string.Join(", ", run.Components.Select(component => component.Name))}";
				return new RunBar(run.RunId, RunMetadata.Key(run.RunId), status, LiveRuns.Parse(run.Created) ?? DateTimeOffset.UtcNow,
					isLive ? null : LiveRuns.Parse(run.Finished) ?? LiveRuns.Parse(run.Created), isLive, detail);
			}))
			.OrderByDescending(bar => bar.Start)
			.Take(count)
			.ToList();
		return bars;
	}

	private static string AgentCommand(RunMetadata run) => Bassia.CliCommands.Agent.AgentCommand.SummarizeCommand(run.Command);

	private async Task<IResult> LiveFragmentAsync(HttpResponse response)
	{
		var monorepo = Current();
		var runs = await new RunMetadataStore(new Git.GitClient(monorepo.Root), monorepo.RunsRepoDir).ListLatestAsync();
		var live = LiveRuns.Read(monorepo, runs);
		response.Headers["X-Live"] = "1"; // keep following: a run started from a terminal shows up here
		return Results.Content(LiveCards(live), "text/html; charset=utf-8");
	}

	/// <summary>
	/// One card per live run: its phase as steps (the current one pulsing), a clock that ticks in the browser, the
	/// components and the latest line of its output.
	/// </summary>
	private static string LiveCards(IReadOnlyList<LiveRun> live)
	{
		if (live.Count == 0)
		{
			return "<p class=\"muted idle\">No agentic run is live. This updates by itself when one starts.</p>";
		}

		var cards = new StringBuilder($"<h2>Live now <span class=\"pulse\"></span></h2><div class=\"livecards\">");
		foreach (var run in live)
		{
			var components = run.Record?.Components.Select(component => $"{component.Name}@{component.CommitIsh}") ?? [];
			cards.Append($"""
				<a class="livecard" href="/runs/{Url(run.RunId)}">
				<div class="head"><b>{E(RunMetadata.Key(run.RunId))}</b><span class="clock" data-since="{run.Started.ToUniversalTime():O}">{Elapsed(DateTimeOffset.UtcNow - run.Started)}</span></div>
				{Charts.PhaseSteps(run.Phase, live: true)}
				<div class="progress"><span></span></div>
				<div class="muted">{E(run.Record is null ? "selecting and checking out the components" : AgentCommand(run.Record))}</div>
				<div class="comps">{string.Join(" ", components.Select(component => $"<span class=\"chip\">{E(component)}</span>"))}</div>
				<div class="tail">{E(run.LastLine ?? (run.Detached ? "" : "running in a terminal; its output is there"))}</div>
				</a>
				""");
		}

		return cards.Append("</div>").ToString();
	}

	internal static string Elapsed(TimeSpan elapsed) =>
		elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m" : $"{elapsed.Minutes}m {elapsed.Seconds:00}s";

	private static string RunsTable(IReadOnlyList<RunMetadata> runs)
	{
		if (runs.Count == 0)
		{
			return Notice("No agentic run has been recorded yet.");
		}

		var rows = string.Concat(runs.Select(run => $"""
			<tr><td>{RunLink(run.RunId)}</td><td>{Status(run.Status)}</td><td>{Time(run.Created)}</td><td>{E(run.Select)}</td>
			<td>{Rationale(run.Command)}</td><td>{ResultTags(run)}</td></tr>
			"""));
		return $"<table class=\"list\"><tr><th>Run</th><th>Status</th><th>Created</th><th>Selection</th><th>Rationale</th><th>Result tags</th></tr>{rows}</table>";
	}

	/// <summary>A run's result tag has the same name in every component, so it is shown once, with the components it is in.</summary>
	private static string ResultTags(RunMetadata run) => string.Join(" ", run.Components
		.Where(component => component.ResultTag is not null)
		.GroupBy(component => component.ResultTag!, StringComparer.Ordinal)
		.Select(group => $"{Ref(group.Key)}<span class=\"muted\">{E(string.Join(", ", group.Select(component => component.Name)))}</span>"));

	private async Task<IResult> RunAsync(string id)
	{
		var state = await LoadAsync();
		var runId = RunMetadata.NormalizeRunId(id);
		var live = state.Live.FirstOrDefault(run => run.RunId == runId);
		var metadata = state.Runs.FirstOrDefault(run => run.RunId == runId);
		if (live is null && metadata is null)
		{
			return NotFound($"No agentic run '{id}' is recorded or running.");
		}

		var key = RunMetadata.Key(runId);
		var body = new StringBuilder();
		if (live is not null)
		{
			body.Append($"<div data-live-src=\"/fragment/run/{Url(runId)}\">{RunLive(live)}</div>");
		}

		if (metadata is not null)
		{
			body.Append("<h2>From baseline to the default branch</h2>").Append(RunFlow(state, metadata));
			body.Append($"""
				<div class="split"><section>
				<table class="list facts">
				<tr><td>Run</td><td><code>{E(metadata.RunId)}</code></td></tr>
				<tr><td>Status</td><td>{Status(live is null && metadata.Status == "started" ? "stale" : metadata.Status)}</td></tr>
				<tr><td>Selection</td><td>{E(metadata.Select)}</td></tr>
				<tr><td>Rationale</td><td>{Rationale(metadata.Command)}</td></tr>
				<tr><td>Command</td><td><code>{E(metadata.Command)}</code></td></tr>
				<tr><td>Created</td><td>{Time(metadata.Created)}</td></tr>
				<tr><td>Finished</td><td>{Time(metadata.Finished)}</td></tr>
				<tr><td>Agent exit code</td><td>{E(metadata.AgentExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-")}</td></tr>
				<tr><td>Workspace</td><td><code>{E(metadata.WorkspacePath)}</code></td></tr>
				<tr><td>Record tag</td><td><code>{E(RunMetadata.TagName(metadata.RunId, metadata.Lineage))}</code></td></tr>
				</table></section><section>
				<h3>Commands</h3>
				<p>{Cmd($"bassia run show {key}")}</p><p>{Cmd($"bassia run diff {key} -patch")}</p><p>{Cmd($"bassia log -run {key}")}</p>
				{(live is not null ? $"<p>{Cmd($"bassia run stop {key}")}</p>" : "")}
				{(IntegrationPlanner.HasResults(metadata) ? $"<p>{Cmd($"bassia integration plan -runs {key}")}</p><p><a href=\"/integrations/plan?run={Url(metadata.RunId)}\">Preview the triage of integrating this run</a></p>" : "")}
				{(metadata.Status == "partial" ? $"<p>{Cmd($"bassia run retry {key}")}</p>" : "")}
				</section></div>
				<table class="list"><tr><th>Component</th><th>Base</th><th>Base commit</th><th>Result</th><th>Result commit</th><th>Result tag</th><th>Merge queue</th></tr>
				""");
			foreach (var component in metadata.Components)
			{
				var result = component.ResultError is null ? E(component.ResultStatus.ToString().ToLowerInvariant()) : $"<span class=\"status failed\">failed</span> {E(component.ResultError)}";
				var resultCommit = component.ResultCommit is null ? "-" : $"<a class=\"hash\" href=\"/commit/{Url(component.Name)}/{Url(component.ResultCommit)}\">{E(Short(component.ResultCommit))}</a>";
				var queued = state.Queue.Items.FirstOrDefault(item => item.Run.RunId == metadata.RunId && item.Component == component.Name);
				body.Append($"""
					<tr><td><a href="{ComponentHref(component.Name)}">{E(component.Name)}</a></td><td>{Ref(component.CommitIsh)}</td>
					<td><a class="hash" href="/commit/{Url(component.Name)}/{Url(component.Commit)}">{E(Short(component.Commit))}</a></td>
					<td>{result}</td><td>{resultCommit}</td><td>{(component.ResultTag is null ? "-" : $"<a class=\"ref agent\" href=\"/tag?name={Url(component.ResultTag)}\">{E(component.ResultTag)}</a>")}</td>
					<td>{(queued is null ? "-" : QueueStateText(queued.State))}</td></tr>
					""");
			}

			body.Append("</table>");
		}

		return View("/runs", $"Run {key}", body.ToString());
	}

	private async Task<IResult> RunFragmentAsync(HttpResponse response, string id)
	{
		var monorepo = Current();
		var runs = await new RunMetadataStore(new Git.GitClient(monorepo.Root), monorepo.RunsRepoDir).ListLatestAsync();
		var live = LiveRuns.Read(monorepo, runs).FirstOrDefault(run => run.RunId == RunMetadata.NormalizeRunId(id));
		response.Headers["X-Live"] = live is null ? "0" : "1";
		var html = live is null
			? $"{Charts.PhaseSteps(runs.FirstOrDefault(run => run.RunId == RunMetadata.NormalizeRunId(id))?.Status ?? "done", live: false)}<p class=\"muted\">The run is no longer live; reload for its record.</p>"
			: RunLive(live);
		return Results.Content(html, "text/html; charset=utf-8");
	}

	/// <summary>A live run: its steps, a ticking clock and the tail of its output.</summary>
	private static string RunLive(LiveRun live) => $"""
		<div class="runlive">
		{Charts.PhaseSteps(live.Phase, live: true)}
		<div class="progress"><span></span></div>
		<p>Running for <b class="clock" data-since="{live.Started.ToUniversalTime():O}">{Elapsed(DateTimeOffset.UtcNow - live.Started)}</b>{(live.Detached ? "" : " in a terminal")}.</p>
		<pre class="output">{E(live.Tail.Count == 0 ? "(no output yet)" : string.Join('\n', live.Tail))}</pre>
		</div>
		""";

	/// <summary>
	/// The run's path as a flow: the baselines it started from, the run, its result tags, the integrations that took
	/// them and their result tags.
	/// </summary>
	private static string RunFlow(State state, RunMetadata run)
	{
		var nodes = new List<FlowNode>();
		var edges = new List<(string, string)>();
		AddRun(nodes, edges, state, run, current: run.RunId);
		AddIntegrations(nodes, edges, state.Integrations, run.Components.Where(component => component.ResultTag is not null).Select(component => component.ResultTag!).ToHashSet(StringComparer.Ordinal));
		return Charts.Flow(nodes, edges, FlowColumns);
	}

	private static readonly string[] FlowColumns = ["started from", "agentic runs", "results", "integrations", "integrated as"];

	/// <summary>A run with its baselines (column 0) and result tags (column 2).</summary>
	private static void AddRun(List<FlowNode> nodes, List<(string, string)> edges, State state, RunMetadata run, string? current = null)
	{
		var runNode = "r:" + run.RunId;
		if (nodes.All(node => node.Id != runNode))
		{
			nodes.Add(new FlowNode(runNode, 1, RunMetadata.Key(run.RunId), $"{run.Status} · {AgentCommand(run)}", $"/runs/{Url(run.RunId)}",
				"run" + (run.RunId == current ? " current" : "")));
		}

		foreach (var baseline in run.Components.GroupBy(component => component.CommitIsh, StringComparer.Ordinal))
		{
			var id = "b:" + baseline.Key;
			if (nodes.All(node => node.Id != id))
			{
				var isTag = baseline.All(component => component.CommitIsh != component.Commit && !component.Commit.StartsWith(component.CommitIsh, StringComparison.Ordinal));
				nodes.Add(new FlowNode(id, 0, baseline.Key, string.Join(", ", baseline.Select(component => component.Name)),
					isTag ? $"/tag?name={Url(baseline.Key)}" : null, "baseline"));
			}

			edges.Add((id, runNode));
		}

		foreach (var result in run.Components.Where(component => component.ResultTag is not null).GroupBy(component => component.ResultTag!, StringComparer.Ordinal))
		{
			var id = "t:" + result.Key;
			if (nodes.All(node => node.Id != id))
			{
				nodes.Add(new FlowNode(id, 2, result.Key, string.Join(", ", result.Select(component => component.Name)), $"/tag?name={Url(result.Key)}", "result"));
			}

			edges.Add((runNode, id));
		}
	}

	/// <summary>The integrations that took any of <paramref name="resultTags"/> (column 3) and the tags they made (column 4).</summary>
	private static void AddIntegrations(List<FlowNode> nodes, List<(string, string)> edges, IReadOnlyList<IntegrationRecord> integrations, IReadOnlySet<string> resultTags, string? current = null)
	{
		foreach (var record in integrations.Where(record => record.AllSteps.Any(step => resultTags.Contains(step.SourceTag))))
		{
			var id = "i:" + record.IntegrationId;
			if (nodes.All(node => node.Id != id))
			{
				nodes.Add(new FlowNode(id, 3, IntegrationRecord.Key(record.IntegrationId), record.Status, $"/integrations/{Url(record.IntegrationId)}",
					"integration" + (record.IntegrationId == current ? " current" : "")));
			}

			foreach (var step in record.AllSteps.Where(step => resultTags.Contains(step.SourceTag)))
			{
				edges.Add(("t:" + step.SourceTag, id));
			}

			foreach (var made in record.Components.Where(component => component.ResultTag is not null).GroupBy(component => component.ResultTag!, StringComparer.Ordinal))
			{
				var tagId = "t:" + made.Key;
				if (nodes.All(node => node.Id != tagId))
				{
					var advanced = made.Any(component => component.Advanced);
					nodes.Add(new FlowNode(tagId, 4, made.Key, string.Join(", ", made.Select(component => component.Name)) + (advanced ? " · advanced" : ""),
						$"/tag?name={Url(made.Key)}", "integrated"));
				}

				edges.Add((id, tagId));
			}
		}
	}
}
