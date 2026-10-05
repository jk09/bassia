namespace Bassia.Web;

using System.Text;
using Bassia.Integration;
using Microsoft.AspNetCore.Http;
using static Bassia.Web.Html;

/// <summary>
/// Merging: the merge queue with the triage of what waits in it, the meaning of each merge (why each side changed,
/// what conflicts and how weave saw it), the merges that need a human, and the integrations recorded so far.
/// </summary>
internal sealed partial class Dashboard
{
	private async Task<IResult> QueueAsync()
	{
		var state = await LoadAsync();
		var queue = state.Queue;
		var monorepo = state.Monorepo;
		var body = new StringBuilder();

		// The triage of what the next integration would take.
		var next = await NextAsync(state);
		var (runs, plan, planError, structuralStatus) = (next.Runs, next.Plan, next.Error, next.StructuralStatus);
		var attention = queue.Attention.Count() + next.PlannedManual(queue).Count();

		body.Append("<div class=\"cards\">")
			.Append(Card("waiting to be integrated", queue.Items.Count(item => item.State == QueueState.Waiting), "#plan"))
			.Append(Card("integrated, base not advanced", queue.Items.Count(item => item.State == QueueState.Integrated), "#advance"))
			.Append(Card("need attention", attention, "#attention", attention > 0 ? "alert" : null))
			.Append(Card("landed on the default branch", queue.Items.Count(item => item.State == QueueState.Landed), "/runs"))
			.Append("</div>");

		body.Append("<h2 id=\"plan\">The next integration</h2>");
		if (runs.Count == 0)
		{
			body.Append("<p class=\"muted\">Nothing waits to be integrated: every result is integrated or landed.</p>");
		}
		else if (planError is not null)
		{
			body.Append(Error(planError));
		}
		else
		{
			var keys = string.Join(",", runs.Select(run => RunMetadata.Key(run.RunId)));
			body.Append($"<p>{E(Summary(plan))}. Structural merge: <code>{E(structuralStatus)}</code>. In merge order, each dot coloured by who merges it:</p>");
			body.Append(Charts.Lanes(plan.Select(component => (component.Name, component.BaseRef,
				(IReadOnlyList<Charts.LaneItem>)component.Steps.Select(step => new Charts.LaneItem(step.RunId, RunMetadata.Key(step.RunId), StrategyClass(step),
					$"{RunMetadata.Key(step.RunId)}: {StrategyText(step)} - {step.Rationale}", $"/runs/{Url(step.RunId)}")).ToList())).ToList()));
			body.Append(StrategyLegend());
			body.Append("<div class=\"split\"><section class=\"wide\"><h3>Who merges what</h3><table class=\"list\">");
			foreach (var component in plan)
			{
				body.Append($"<tr><td><a href=\"{ComponentHref(component.Name)}\"><b>{E(component.Name)}</b></a></td><td class=\"stackcell\">{Charts.Stack(StrategySegments(component.Steps))}</td></tr>");
			}

			body.Append("</table>");
			body.Append($"<p>Integrate: {Cmd($"bassia integration start -runs {keys} -detach")}</p>");
			body.Append($"<p class=\"muted\">Per run: <code>-semantic</code> sends it to the resolver, <code>-manual</code> leaves it for a human, <code>-skip</code> leaves it out. The defaults come from the <a href=\"/config\">merge policy</a>.</p>");

			var pairs = plan.SelectMany(component => component.Steps.SelectMany(step => step.ConflictsWith
					.Where(other => string.CompareOrdinal(step.RunId, other) < 0)
					.Select(other => (step.RunId, other, $"{component.Name}: {RunMetadata.Key(step.RunId)} and {RunMetadata.Key(other)} change the same lines"))))
				.ToList();
			body.Append("</section><section><h3>Runs that collide</h3>");
			body.Append(pairs.Count == 0
				? "<p class=\"muted\">No two queued results conflict with each other.</p>"
				: Charts.ConflictRing(runs.Select(run => (run.RunId, RunMetadata.Key(run.RunId))).ToList(), pairs));
			body.Append("</section></div>");

			body.Append("<h3>What each merge means</h3>").Append(MeaningTable(plan, runs));
		}

		// Integrated but not on the default branch yet.
		var waitingToAdvance = queue.Items.Where(item => item.State == QueueState.Integrated && item.Integration is not null)
			.GroupBy(item => item.Integration!.IntegrationId).ToList();
		body.Append("<h2 id=\"advance\">Integrated, waiting to advance</h2>");
		if (waitingToAdvance.Count == 0)
		{
			body.Append("<p class=\"muted\">No integration result waits for its base branch to move.</p>");
		}
		else
		{
			body.Append("<table class=\"list\"><tr><th>Integration</th><th>Results</th><th>Advance</th></tr>");
			foreach (var group in waitingToAdvance)
			{
				body.Append($"<tr><td>{IntegrationLink(group.Key)}</td><td>{string.Join(" ", group.Select(item => $"{E(item.Component)} {Ref(item.ResultTag)}"))}</td><td>{Cmd($"bassia integration advance {IntegrationRecord.Key(group.Key)}")}</td></tr>");
			}

			body.Append("</table>");
		}

		body.Append("<h2 id=\"attention\">Needs attention</h2>").Append(AttentionTable(state, next));
		return View("/queue", "Merge queue", body.ToString());
	}

	/// <summary>The triage of what the next integration would take: the runs still waiting or left for a human.</summary>
	private sealed record NextIntegration(IReadOnlyList<RunMetadata> Runs, IReadOnlyList<ComponentIntegration> Plan, string? Error, string StructuralStatus)
	{
		/// <summary>Steps the merge policy would leave for a human that no recorded integration has left yet.</summary>
		public IEnumerable<(string Component, IntegrationStep Step)> PlannedManual(MergeQueue queue) => Plan
			.SelectMany(component => component.Steps.Where(step => step.Strategy == MergeStrategy.Manual).Select(step => (component.Name, step)))
			.Where(pair => !queue.Attention.Any(item => item.Run.RunId == pair.step.RunId && item.Component == pair.Name));
	}

	private static async Task<NextIntegration> NextAsync(State state)
	{
		var runs = state.Queue.RunsToIntegrate;
		var (structural, structuralStatus) = StructuralMerge.Resolve(state.Monorepo);
		if (runs.Count == 0)
		{
			return new NextIntegration(runs, [], null, structuralStatus);
		}

		try
		{
			return new NextIntegration(runs, await IntegrationPlanner.PlanAsync(state.Monorepo, runs, new IntegrationChoices { Structural = structural }), null, structuralStatus);
		}
		catch (IntegrationException ex)
		{
			return new NextIntegration(runs, [], ex.Message, structuralStatus);
		}
	}

	/// <summary>
	/// The semantic side of each merge: what the run was for (its prompt), what git found, how weave saw it and why the
	/// step goes where it goes - what a person needs to judge a merge, and what the resolver's brief is built from.
	/// </summary>
	private static string MeaningTable(IReadOnlyList<ComponentIntegration> plan, IReadOnlyList<RunMetadata> runs)
	{
		var body = new StringBuilder("<table class=\"list meaning\"><tr><th>Component</th><th>Run</th><th>Why it changed</th><th>Git</th><th>Weave</th><th>Merged by</th></tr>");
		foreach (var component in plan)
		{
			foreach (var step in component.Steps)
			{
				var run = runs.FirstOrDefault(candidate => candidate.RunId == step.RunId);
				var git = IntegrationRecord.Snake(step.Triage).Replace('_', ' ') + (step.Conflicts.Count > 0 ? $": {string.Join(", ", step.Conflicts)}" : "")
					+ (step.ConflictsWith.Count > 0 ? $"; collides with {string.Join(", ", step.ConflictsWith.Select(RunMetadata.Key))}" : "");
				var weave = step.Structural is null ? "-" : IntegrationRecord.Snake(step.Structural.Value)
					+ (step.StructuralConflicts.Count > 0 ? $": {string.Join(", ", step.StructuralConflicts)}" : "")
					+ (step.StructuralWarnings.Count > 0 ? $"; {step.StructuralWarnings.Count} warning(s)" : "");
				body.Append($"""
					<tr><td>{E(component.Name)}</td><td>{RunLink(step.RunId)}</td>
					<td>{E(step.Rationale)}<div class="muted">{E(run?.Select ?? "")}</div></td>
					<td>{E(git)}</td><td>{E(weave)}</td>
					<td><span class="pill {StrategyClass(step)}">{E(StrategyText(step))}</span>{(step.Note is null ? "" : $"<div class=\"muted\">{E(step.Note)}</div>")}</td></tr>
					""");
			}
		}

		return body.Append("</table>").ToString();
	}

	/// <summary>
	/// Every merge that needs a human: the ones a recorded integration left (or failed on), and the ones the next
	/// integration would leave, each with the commands that deal with it.
	/// </summary>
	private static string AttentionTable(State state, NextIntegration next)
	{
		var rows = new List<string>();
		foreach (var item in state.Queue.Attention)
		{
			var key = RunMetadata.Key(item.Run.RunId);
			var checkout = item.Integration?.Components.FirstOrDefault(component => component.Name == item.Component)?.Path;
			rows.Add($"""
				<tr class="attn-row"><td><b>{E(item.Component)}</b></td><td>{RunLink(item.Run.RunId)}<div class="muted">{Rationale(item.Run.Command)}</div></td>
				<td>{(item.Integration is null ? "-" : IntegrationLink(item.Integration.IntegrationId))} <span class="pill {StrategyClass(item.Step!)}">{E(IntegrationRecord.Snake(item.Step!.Outcome).Replace('_', ' '))}</span>
				<div>{E(item.Step.Note ?? "")}</div>{(item.Step.Conflicts.Count > 0 ? $"<div class=\"muted\">conflicts: {E(string.Join(", ", item.Step.Conflicts))}</div>" : "")}
				{BriefLink(item.Integration, item.Component, item.Step)}</td>
				<td>{Remedies(key, item.ResultTag, checkout ?? state.Monorepo.SourceRepoDir(item.Component))}</td></tr>
				""");
		}

		foreach (var (component, step) in next.PlannedManual(state.Queue))
		{
			rows.Add($"""
				<tr class="attn-row planned"><td><b>{E(component)}</b></td><td>{RunLink(step.RunId)}<div class="muted">{E(step.Rationale)}</div></td>
				<td><span class="pill st-manual">will need a human</span><div>{E(step.Note ?? "")}</div>{(step.Conflicts.Count > 0 ? $"<div class=\"muted\">conflicts: {E(string.Join(", ", step.Conflicts))}</div>" : "")}</td>
				<td>{Remedies(RunMetadata.Key(step.RunId), step.SourceTag, state.Monorepo.SourceRepoDir(component))}</td></tr>
				""");
		}

		return rows.Count == 0
			? "<p class=\"muted\">No merge needs a human.</p>"
			: $"<table class=\"list\"><tr><th>Component</th><th>Run</th><th>Why</th><th>What to do</th></tr>{string.Concat(rows)}</table>";
	}

	private static string Remedies(string key, string tag, string checkout) =>
		$"<div>resolver: {Cmd($"bassia integration start -runs {key} -semantic {key}")}</div>" +
		$"<div>by hand: {Cmd($"git -C \"{checkout}\" merge {tag}")}</div>" +
		$"<div>leave out: {Cmd($"bassia integration start -runs all -skip {key}")}</div>";

	private static string BriefLink(IntegrationRecord? record, string component, IntegrationStep step)
	{
		if (record is null || step.Brief is null)
		{
			return "";
		}

		var index = record.Components.First(candidate => candidate.Name == component).Steps.IndexOf(step);
		return $"<div><a href=\"/integrations/{Url(record.IntegrationId)}/brief?c={Url(component)}&i={index}\">semantic brief</a></div>";
	}

	internal static string StrategyClass(IntegrationStep step) =>
		step.Outcome is StepOutcome.Failed ? "st-failed"
		: step.Outcome is StepOutcome.NeedsAttention || step.Strategy == MergeStrategy.Manual ? "st-manual"
		: step.Triage == Triage.UpToDate && step.Strategy != MergeStrategy.Skip ? "st-uptodate"
		: "st-" + IntegrationRecord.Snake(step.Strategy);

	private static string StrategyText(IntegrationStep step) =>
		step.Outcome is StepOutcome.Failed ? "failed"
		: step.Strategy == MergeStrategy.Manual ? "a human"
		: step.Triage == Triage.UpToDate && step.Strategy != MergeStrategy.Skip ? "nothing to merge"
		: step.Strategy switch
		{
			MergeStrategy.Syntactic => step.Triage == Triage.FastForward ? "git (fast-forward)" : "git",
			MergeStrategy.Structural => "weave",
			MergeStrategy.Semantic => "the resolver",
			_ => "skipped"
		} + (step.Overridden ? " (chosen)" : "");

	private static IReadOnlyList<Segment> StrategySegments(IEnumerable<IntegrationStep> steps)
	{
		var list = steps.ToList();
		return
		[
			new("by git", list.Count(step => StrategyClass(step) == "st-syntactic"), "st-syntactic"),
			new("by weave", list.Count(step => StrategyClass(step) == "st-structural"), "st-structural"),
			new("by the resolver", list.Count(step => StrategyClass(step) == "st-semantic"), "st-semantic"),
			new("by a human", list.Count(step => StrategyClass(step) == "st-manual"), "st-manual"),
			new("failed", list.Count(step => StrategyClass(step) == "st-failed"), "st-failed"),
			new("up to date", list.Count(step => StrategyClass(step) == "st-uptodate"), "st-uptodate"),
			new("skipped", list.Count(step => StrategyClass(step) == "st-skip"), "st-skip")
		];
	}

	private static string StrategyLegend() => Charts.Legend(("st-syntactic", "git"), ("st-structural", "weave"), ("st-semantic", "resolver"),
		("st-manual", "a human"), ("st-uptodate", "up to date"), ("st-skip", "skipped"), ("st-failed", "failed"));

	/// <summary>One line: how many steps go which way.</summary>
	internal static string Summary(IReadOnlyList<ComponentIntegration> plan)
	{
		var steps = plan.SelectMany(component => component.Steps).ToList();
		var upToDate = steps.Count(step => step.Triage == Triage.UpToDate && step.Strategy != MergeStrategy.Skip);
		var syntactic = steps.Count(step => step.Strategy == MergeStrategy.Syntactic) - upToDate;
		var structural = steps.Count(step => step.Strategy == MergeStrategy.Structural);
		var semantic = steps.Count(step => step.Strategy == MergeStrategy.Semantic);
		var manual = steps.Count(step => step.Strategy == MergeStrategy.Manual);
		var skipped = steps.Count(step => step.Strategy == MergeStrategy.Skip);
		return $"{syntactic} by git (syntax) · {structural} by weave (structure) · {semantic} by the resolver (semantic) · {manual} by a human · {upToDate} up to date · {skipped} skipped";
	}

	// ----- integrations -----

	private async Task<IResult> IntegrationsAsync()
	{
		var state = await LoadAsync();
		var candidates = state.Runs.Where(IntegrationPlanner.HasResults).OrderBy(run => run.Created, StringComparer.Ordinal).ToList();
		var body = new StringBuilder("<p class=\"muted\">Every integration with how its steps were merged. The <a href=\"/queue\">merge queue</a> shows what waits for the next one.</p>");
		body.Append(IntegrationsTable(state.Integrations));
		body.Append("<h2>Preview a triage</h2><p class=\"muted\">Choose runs with results to see how they would integrate. Nothing changes.</p>");
		if (candidates.Count == 0)
		{
			body.Append(Notice("No run has pushed a result yet."));
		}
		else
		{
			body.Append("<form method=\"get\" action=\"/integrations/plan\"><table class=\"list\"><tr><th></th><th>Run</th><th>Status</th><th>Components</th><th>Rationale</th></tr>");
			foreach (var run in candidates)
			{
				body.Append($"<tr><td><input type=\"checkbox\" name=\"run\" value=\"{E(run.RunId)}\"></td><td>{RunLink(run.RunId)}</td><td>{Status(run.Status)}</td><td>{E(string.Join(", ", run.Components.Where(component => component.ResultStatus == ResultStatus.Pushed).Select(component => component.Name)))}</td><td>{Rationale(run.Command)}</td></tr>");
			}

			body.Append("</table><button>Preview triage</button></form>");
		}

		return View("/integrations", "Integrations", body.ToString());
	}

	private static string IntegrationsTable(IReadOnlyList<IntegrationRecord> records)
	{
		if (records.Count == 0)
		{
			return Notice("No integration has been recorded yet.");
		}

		var rows = string.Concat(records.Select(record => $"""
			<tr><td>{IntegrationLink(record.IntegrationId)}</td><td>{Status(record.Status)}</td><td>{Time(record.Created)}</td>
			<td>{RunList(record.Runs)}</td>
			<td class="stackcell">{Charts.Stack(StrategySegments(record.AllSteps), legend: true)}</td>
			<td>{ResultTagsOf(record)}</td></tr>
			"""));
		return $"<table class=\"list\"><tr><th>Integration</th><th>Status</th><th>Created</th><th>Runs</th><th>How merged</th><th>Results</th></tr>{rows}</table>";
	}

	/// <summary>A few runs as links; many as a count that opens to the full list (works without JavaScript).</summary>
	private static string RunList(IReadOnlyList<string> runs) =>
		runs.Count <= 3
			? string.Join("<br>", runs.Select(RunLink))
			: $"<details class=\"runs\"><summary>{runs.Count} runs</summary>{string.Join("<br>", runs.Select(RunLink))}</details>";

	/// <summary>
	/// What an integration made: its result tag (one name in every component it changed) with those components, and
	/// which base branches were advanced to it.
	/// </summary>
	private static string ResultTagsOf(IntegrationRecord record) => string.Concat(record.Components
		.Where(component => component.ResultTag is not null)
		.GroupBy(component => component.ResultTag!, StringComparer.Ordinal)
		.Select(group =>
		{
			var advanced = group.Where(component => component.Advanced).Select(component => $"{component.Name}:{component.BaseRef}").ToList();
			return $"<div class=\"result\">{Ref(group.Key)}<div class=\"muted\">in {E(string.Join(", ", group.Select(component => component.Name)))}</div>" +
				(advanced.Count == 0 ? "" : $"<div class=\"advanced\">→ advanced {E(string.Join(", ", advanced))}</div>") + "</div>";
		}));

	private async Task<IResult> PlanAsync(HttpRequest request)
	{
		var monorepo = Current();
		var ids = request.Query["run"].Select(value => value ?? "").Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
		var store = new RunMetadataStore(new Git.GitClient(monorepo.Root), monorepo.RunsRepoDir);
		var runs = (await store.ListLatestAsync()).Where(run => ids.Contains(run.RunId)).ToList();
		if (runs.Count == 0)
		{
			return View("/integrations", "Triage", Error("Choose at least one recorded run with results."), StatusCodes.Status400BadRequest);
		}

		var (structural, structuralStatus) = StructuralMerge.Resolve(monorepo);
		var plan = await IntegrationPlanner.PlanAsync(monorepo, runs, new IntegrationChoices { Structural = structural });
		var command = $"bassia integration start -runs {string.Join(",", runs.OrderBy(run => run.Created, StringComparer.Ordinal).Select(run => RunMetadata.Key(run.RunId)))}";
		var body = $"""
			<p>{E(Summary(plan))}. Structural merge: <code>{E(structuralStatus)}</code>. Nothing was changed. To integrate: {Cmd(command)}</p>
			{StepsTable(plan)}
			<h2>What each merge means</h2>
			{MeaningTable(plan, runs)}
			""";
		return View("/integrations", "Triage", body);
	}

	private async Task<IResult> IntegrationAsync(string id)
	{
		var state = await LoadAsync();
		var integrationId = IntegrationRecord.NormalizeId(id);
		var record = state.Integrations.FirstOrDefault(candidate => candidate.IntegrationId == integrationId);
		if (record is null)
		{
			return NotFound($"No integration '{id}' is recorded.");
		}

		var key = IntegrationRecord.Key(record.IntegrationId);
		var nodes = new List<FlowNode>();
		var edges = new List<(string, string)>();
		foreach (var run in state.Runs.Where(run => record.Runs.Contains(run.RunId)))
		{
			AddRun(nodes, edges, state, run);
		}

		AddIntegrations(nodes, edges, [record], record.AllSteps.Select(step => step.SourceTag).ToHashSet(StringComparer.Ordinal), current: record.IntegrationId);

		var body = new StringBuilder($"""
			{Charts.Flow(nodes, edges, FlowColumns)}
			<div class="split"><section>
			<table class="list facts">
			<tr><td>Integration</td><td><code>{E(record.IntegrationId)}</code></td></tr>
			<tr><td>Status</td><td>{Status(record.Status)}</td></tr>
			<tr><td>Runs</td><td>{string.Join(" ", record.Runs.Select(RunLink))}</td></tr>
			<tr><td>Resolver</td><td><code>{E(record.Resolver)}</code></td></tr>
			<tr><td>Structural merge</td><td><code>{E(record.StructuralDriver ?? "-")}</code></td></tr>
			<tr><td>Created</td><td>{Time(record.Created)}</td></tr>
			<tr><td>Finished</td><td>{Time(record.Finished)}</td></tr>
			<tr><td>Workspace</td><td><code>{E(record.WorkspacePath)}</code></td></tr>
			</table></section><section>
			<h3>How merged</h3>{Charts.Stack(StrategySegments(record.AllSteps), legend: true)}
			<p>{Cmd($"bassia integration show {key}")}</p>
			{(record.Components.Any(component => component.ResultStatus == ResultStatus.Pushed && !component.Advanced) ? $"<p>{Cmd($"bassia integration advance {key}")}</p>" : "")}
			</section></div>
			{StepsTable(record.Components, record)}
			""");
		return View("/integrations", $"Integration {key}", body.ToString());
	}

	/// <summary>A semantic brief the resolver was handed, as it was written into the integration's workspace.</summary>
	private async Task<IResult> BriefAsync(string id, HttpRequest request)
	{
		var monorepo = Current();
		var store = new IntegrationStore(new RunMetadataStore(new Git.GitClient(monorepo.Root), monorepo.RunsRepoDir));
		var record = IntegrationRecord.IsId(IntegrationRecord.NormalizeId(id)) ? await store.LoadLatestAsync(IntegrationRecord.NormalizeId(id)) : null;
		var component = record?.Components.FirstOrDefault(candidate => candidate.Name == request.Query["c"].ToString());
		var step = component is not null && int.TryParse(request.Query["i"], out var index) && index >= 0 && index < component.Steps.Count ? component.Steps[index] : null;
		if (record is null || step?.Brief is null)
		{
			return NotFound("No such semantic brief is recorded.");
		}

		// Only a path the record names, inside the integration's own workspace, is ever read.
		var path = Path.GetFullPath(step.Brief);
		var workspace = Path.GetFullPath(record.WorkspacePath) + Path.DirectorySeparatorChar;
		var text = path.StartsWith(workspace, StringComparison.Ordinal) && File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
		var body = $"""
			<p>{RunLink(step.RunId)} into <b>{E(component!.Name)}</b> of {IntegrationLink(record.IntegrationId)} · <code>{E(step.Brief)}</code></p>
			{(text is null ? Notice("The brief is no longer in the integration's workspace.") : $"<pre class=\"brief\">{E(text)}</pre>")}
			""";
		return View("/integrations", "Semantic brief", body);
	}

	/// <summary>The triage or the outcome, per component in execution order.</summary>
	private static string StepsTable(IReadOnlyList<ComponentIntegration> components, IntegrationRecord? record = null)
	{
		var body = new StringBuilder("<table class=\"list\"><tr><th>Component</th><th>#</th><th>Run</th><th>Triage</th><th>Strategy</th><th>Conflicts</th><th>Outcome</th></tr>");
		foreach (var component in components)
		{
			for (var i = 0; i < component.Steps.Count; i++)
			{
				var step = component.Steps[i];
				var strategy = IntegrationRecord.Snake(step.Strategy);
				var conflicts = string.Join(", ", step.Conflicts) + (step.ConflictsWith.Count > 0 ? $" vs {string.Join(", ", step.ConflictsWith.Select(RunMetadata.Key))}" : "");
				if (step.Structural is { } verdict)
				{
					conflicts += $" · weave: {IntegrationRecord.Snake(verdict)}" + (step.StructuralConflicts.Count > 0 ? $" ({string.Join(", ", step.StructuralConflicts)})" : "")
						+ (step.StructuralWarnings.Count > 0 ? $", {step.StructuralWarnings.Count} warning(s)" : "");
				}

				var outcome = IntegrationRecord.Snake(step.Outcome) + (step.Commit is null ? "" : $" {Short(step.Commit)}");
				var head = i == 0 ? $"<a href=\"{ComponentHref(component.Name)}\"><b>{E(component.Name)}</b></a> <span class=\"muted\">onto {E(component.BaseRef)} {E(Short(component.BaseCommit))}</span>" : "";
				body.Append($"""
					<tr><td>{head}</td><td>{(step.Strategy == MergeStrategy.Skip ? "-" : i + 1)}</td><td>{RunLink(step.RunId)} <span class="muted">{E(step.Rationale)}</span></td>
					<td>{E(IntegrationRecord.Snake(step.Triage).Replace('_', ' '))}</td><td><span class="pill {StrategyClass(step)}">{E(strategy.ToUpperInvariant())}{(step.Overridden ? "*" : "")}</span></td>
					<td>{E(conflicts.Trim().Length == 0 ? "-" : conflicts.Trim())}{(step.Note is null ? "" : $"<div class=\"muted\">{E(step.Note)}</div>")}</td>
					<td>{E(outcome)}{(record is null ? "" : BriefLink(record, component.Name, step))}</td></tr>
					""");
			}

			if (component.ResultTag is not null || component.ResultError is not null)
			{
				body.Append($"<tr><td></td><td colspan=\"6\">{(component.ResultError is null ? $"→ {Ref(component.ResultTag!)}" : Error(component.ResultError))}</td></tr>");
			}
		}

		return body.Append("</table>").ToString();
	}
}
