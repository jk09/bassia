namespace Bassia.Web;

using System.Text;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using static Bassia.Web.Html;

/// <summary>
/// The web dashboard (<c>bassia web</c>): a local, read-only site over one monorepo, made to be looked at - the
/// components and how they relate, tags as units of progress, agentic runs as they happen, and the merge queue with
/// its triage and the merges that need a human. Pages are rendered on the server, pictures included, and work without
/// JavaScript; the script animates and refreshes the live parts. Nothing is changed from here: every page shows the
/// <c>bassia</c> command that does what it describes, for the command line or <c>bassia prompt</c>.
/// </summary>
internal sealed partial class Dashboard
{
	private readonly string root;

	public Dashboard(Monorepo monorepo) => root = monorepo.Root;

	public WebApplication Build(string url)
	{
		var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = root });
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls(url);
		var app = builder.Build();
		app.Use(GuardAsync);
		Map(app);
		return app;
	}

	// ----- request guard -----

	/// <summary>
	/// Only requests addressed to the loopback host are served (a DNS-rebinding page would send its own host name),
	/// only reads are accepted, and an expected failure becomes an error page, not a dead server.
	/// </summary>
	private async Task GuardAsync(HttpContext context, RequestDelegate next)
	{
		if (context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			await context.Response.WriteAsync("The Bassia dashboard only answers requests addressed to 127.0.0.1 or localhost.");
			return;
		}

		if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
		{
			context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
			await context.Response.WriteAsync("The Bassia dashboard is read-only; change the monorepo with the bassia command line or 'bassia prompt'.");
			return;
		}

		try
		{
			await next(context);
		}
		catch (Exception ex) when (ex is GitException or MonorepoException or AgentException or IntegrationException or IOException or UnauthorizedAccessException)
		{
			if (context.Response.HasStarted)
			{
				throw;
			}

			context.Response.Clear();
			context.Response.StatusCode = ex is MonorepoException or IntegrationException or AgentException ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
			context.Response.ContentType = "text/html; charset=utf-8";
			await context.Response.WriteAsync(Page(root, "", "Error", Error(ex.Message)));
		}
		catch (Exception ex) when (!context.Response.HasStarted)
		{
			// A bug, not an expected failure: still a page that says what went wrong, rather than an empty 500.
			context.Response.Clear();
			context.Response.StatusCode = StatusCodes.Status500InternalServerError;
			context.Response.ContentType = "text/html; charset=utf-8";
			await context.Response.WriteAsync(Page(root, "", "Error", Error($"Unexpected error: {ex.GetType().Name}: {ex.Message}")));
		}
	}

	private void Map(WebApplication app)
	{
		app.MapGet("/style.css", () => Results.Text(Stylesheet, "text/css"));
		app.MapGet("/app.js", () => Results.Text(Script, "text/javascript"));
		app.MapGet("/graph.svg", () => Results.Text(new Graph.ComponentGraph(Current().Components).ToSvg(ComponentHref), "image/svg+xml"));

		app.MapGet("/", OverviewAsync);
		app.MapGet("/fragment/live", LiveFragmentAsync);
		app.MapGet("/fragment/run/{id}", RunFragmentAsync);
		app.MapGet("/components", ComponentsAsync);
		app.MapGet("/components/{name}", ComponentAsync);
		app.MapGet("/tags", TagsAsync);
		app.MapGet("/tag", TagAsync);
		app.MapGet("/runs", RunsAsync);
		app.MapGet("/runs/{id}", RunAsync);
		app.MapGet("/queue", QueueAsync);
		app.MapGet("/integrations", IntegrationsAsync);
		app.MapGet("/integrations/plan", PlanAsync);
		app.MapGet("/integrations/{id}", IntegrationAsync);
		app.MapGet("/integrations/{id}/brief", BriefAsync);
		app.MapGet("/timeline", TimelineAsync);
		app.MapGet("/commit/{component}/{hash}", CommitAsync);
		app.MapGet("/config", ConfigPage);
	}

	/// <summary>The monorepo as it is now: components and configuration change under a running dashboard.</summary>
	private Monorepo Current() => Monorepo.Load(root);

	private IResult View(string active, string title, string body, int status = StatusCodes.Status200OK) =>
		Results.Content(Page(root, active, title, body), "text/html; charset=utf-8", Encoding.UTF8, status);

	private IResult NotFound(string what) => View("", "Not found", Error(what), StatusCodes.Status404NotFound);

	private static string ComponentHref(string name) => $"/components/{Url(name)}";

	/// <summary>Everything most pages draw from, read once per request.</summary>
	private sealed record State(Monorepo Monorepo, IReadOnlyList<RunMetadata> Runs, IReadOnlyList<IntegrationRecord> Integrations, IReadOnlyList<LiveRun> Live, MergeQueue Queue)
	{
		public RunMetadataStore Store => new(new GitClient(Monorepo.Root), Monorepo.RunsRepoDir);
	}

	private async Task<State> LoadAsync()
	{
		var monorepo = Current();
		var store = new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir);
		var runs = await store.ListLatestAsync();
		var integrations = await new IntegrationStore(store).ListLatestAsync();
		return new State(monorepo, runs, integrations, LiveRuns.Read(monorepo, runs), await MergeQueue.ReadAsync(monorepo, runs, integrations));
	}

	/// <summary>What the component map shows on each node.</summary>
	private static async Task<IReadOnlyDictionary<string, ComponentActivity>> ActivityAsync(State state, IReadOnlyList<MultiComponentTag>? tags = null)
	{
		tags ??= await MultiComponentTag.ReadAllAsync(state.Monorepo);
		var latest = state.Monorepo.Components.ToDictionary(component => component.Name, component => tags
			.Where(tag => TagStory.KindOf(tag.Name) != "run")
			.SelectMany(tag => tag.Components.Where(entry => entry.Component == component.Name).Select(entry => (tag.Name, entry.Date)))
			.OrderByDescending(pair => pair.Date, StringComparer.Ordinal).Select(pair => pair.Name).FirstOrDefault(), StringComparer.Ordinal);

		return state.Monorepo.Components.ToDictionary(component => component.Name, component => new ComponentActivity(
			component.Name,
			state.Live.Count(run => run.Record?.Components.Any(entry => entry.Name == component.Name) ?? false),
			state.Queue.Pending.Count(item => item.Component == component.Name && item.State != QueueState.NeedsAttention),
			state.Queue.Attention.Count(item => item.Component == component.Name),
			latest[component.Name],
			state.Runs.Count(run => run.Components.Any(entry => entry.Name == component.Name))), StringComparer.Ordinal);
	}

	// ----- overview -----

	private async Task<IResult> OverviewAsync()
	{
		var state = await LoadAsync();
		var tags = await MultiComponentTag.ReadAllAsync(state.Monorepo);
		var activity = await ActivityAsync(state, tags);
		var attention = state.Queue.Attention.ToList();

		var body = new StringBuilder();
		body.Append("<div class=\"cards\">")
			.Append(Card("components", state.Monorepo.Components.Count, "/components"))
			.Append(Card("live runs", state.Live.Count, "/runs", state.Live.Count > 0 ? "live" : null))
			.Append(Card("runs", state.Runs.Count, "/runs"))
			.Append(Card("results in the merge queue", state.Queue.Pending.Count(), "/queue"))
			.Append(Card("merges need attention", attention.Count, "/queue#attention", attention.Count > 0 ? "alert" : null))
			.Append(Card("tags across components", tags.Count(tag => tag.Components.Count > 1), "/tags?multi=1"))
			.Append(Card("integrations", state.Integrations.Count, "/integrations"))
			.Append("</div>");

		body.Append("<div data-live-src=\"/fragment/live\">").Append(LiveCards(state.Live)).Append("</div>");

		body.Append("<div class=\"split\"><section class=\"wide\"><h2>Components and what they need</h2>")
			.Append(Charts.ComponentMap(state.Monorepo.Components, activity))
			.Append("</section><section><h2>Runs by status</h2>")
			.Append(Charts.Donut(StatusSegments(state.Runs), "agentic runs"))
			.Append("</section></div>");

		body.Append("<h2>Recent agentic runs</h2>").Append(Charts.RunTimeline(Bars(state, 12), DateTimeOffset.UtcNow));

		body.Append("<div class=\"split\"><section><h2>Needs attention</h2>");
		body.Append(attention.Count == 0
			? "<p class=\"muted\">No merge waits for a human.</p>"
			: string.Concat(attention.Take(6).Select(item => $"<div class=\"attn\">{Ref(item.ResultTag)} in <b>{E(item.Component)}</b> — {E(item.Step?.Note ?? "needs a human")}</div>"))
				+ "<p><a href=\"/queue#attention\">All merges needing attention →</a></p>");
		body.Append("</section><section><h2>Latest tags</h2>");
		var recent = tags.Select(tag => TagStory.For(tag, state.Runs, state.Integrations)).OrderByDescending(story => story.Date, StringComparer.Ordinal).Take(10).ToList();
		body.Append(recent.Count == 0 ? "<p class=\"muted\">No tags yet.</p>" : "<ul class=\"taglist\">" + string.Concat(recent.Select(story =>
			$"<li><a class=\"tagchip k-{story.Kind}\" href=\"/tag?name={Url(story.Tag.Name)}\">{E(story.Tag.Name)}</a> <span class=\"muted\">{story.Tag.Components.Count} component(s) · {Time(story.Date)}</span></li>")) + "</ul>");
		body.Append("</section></div>");
		return View("/", "Overview", body.ToString());
	}

	private static string Card(string label, int n, string href, string? kind = null) =>
		$"<a class=\"card{(kind is null ? "" : " " + kind)}\" href=\"{href}\"><div class=\"n\">{n}</div><div class=\"muted\">{E(label)}</div></a>";

	// ----- components -----

	private async Task<IResult> ComponentsAsync()
	{
		var state = await LoadAsync();
		var activity = await ActivityAsync(state);
		var graph = new Graph.ComponentGraph(state.Monorepo.Components);
		var rows = new StringBuilder();
		foreach (var component in state.Monorepo.Components)
		{
			var info = activity[component.Name];
			rows.Append($"""
				<tr><td><a href="{ComponentHref(component.Name)}"><b>{E(component.Name)}</b></a></td>
				<td>{References(component.References.Select(reference => reference.Name))}</td>
				<td>{References(graph.ReferrersOf(component.Name).Select(reference => reference.Name))}</td>
				<td>{(info.LatestTag is null ? "<span class=\"muted\">-</span>" : Ref(info.LatestTag))}</td>
				<td>{info.Runs}</td><td>{info.Live}</td><td>{info.Queued}</td><td>{(info.Attention > 0 ? $"<b class=\"bad\">{info.Attention}</b>" : "0")}</td>
				<td class="muted">{E(component.Url)}</td></tr>
				""");
		}

		var body = $"""
			<p class="muted">Each box is a component; an arrow points at what it needs. Hover one to light up everything it needs and everything that needs it. <a href="/graph.svg">graph.svg</a> is the plain picture.</p>
			{Charts.ComponentMap(state.Monorepo.Components, activity)}
			<table class="list"><tr><th>Component</th><th>Needs</th><th>Used by</th><th>Latest tag</th><th>Runs</th><th>Live</th><th>Queued</th><th>Attention</th><th>URL</th></tr>
			{rows}</table>
			<p>{Cmd("bassia component add -url <url> -references <component,...>")} {Cmd("bassia graph -format tree")}</p>
			""";
		return View("/components", "Components", body);
	}

	private static string References(IEnumerable<string> names)
	{
		var list = names.ToList();
		return list.Count == 0 ? "<span class=\"muted\">-</span>" : string.Join(" ", list.Select(name => $"<a class=\"chip\" href=\"{ComponentHref(name)}\">{E(name)}</a>"));
	}

	private static async Task<IReadOnlyList<GitRef>?> RefsAsync(Monorepo monorepo, string component)
	{
		var sourceDir = monorepo.SourceRepoDir(component);
		if (!Directory.Exists(sourceDir))
		{
			return null;
		}

		try
		{
			return await GitRef.ListAsync(GitClient.In(sourceDir));
		}
		catch (GitException)
		{
			return null;
		}
	}

	private async Task<IResult> ComponentAsync(string name)
	{
		var state = await LoadAsync();
		if (state.Monorepo.FindComponent(name) is not { } component)
		{
			return NotFound($"Component '{name}' is not registered in components.toml.");
		}

		var graph = new Graph.ComponentGraph(state.Monorepo.Components);
		var refs = await RefsAsync(state.Monorepo, name);
		var policy = state.Monorepo.MergePolicyFor(name);
		var body = new StringBuilder($"""
			<div class="split"><section>
			<table class="list facts">
			<tr><td>Source repo</td><td><code>{E(state.Monorepo.SourceRepoDir(name))}</code></td></tr>
			<tr><td>URL</td><td>{E(component.Url)}</td></tr>
			<tr><td>Needs</td><td>{References(component.References.Select(reference => reference.Name))}</td></tr>
			<tr><td>Used by</td><td>{References(graph.ReferrersOf(name).Select(reference => reference.Name))}</td></tr>
			<tr><td>Merge policy</td><td>{PolicyText(policy)} <a href="/config">configuration</a></td></tr>
			</table>
			<p><a href="/timeline?c={Url(name)}">Timeline of {E(name)} and what it needs</a> · <a href="/tags?c={Url(name)}">its tags</a></p>
			<p>{Cmd($"bassia run start -select {name} -prompt \"...\"")}</p>
			</section><section class="wide">
			{Charts.ComponentMap(state.Monorepo.Components, await ActivityAsync(state), focus: name)}
			</section></div>
			""");

		var queued = state.Queue.Pending.Where(item => item.Component == name).ToList();
		if (queued.Count > 0)
		{
			body.Append("<h2>In the merge queue</h2><table class=\"list\"><tr><th>Run</th><th>Result</th><th>State</th><th>Why</th></tr>");
			foreach (var item in queued)
			{
				body.Append($"<tr><td>{RunLink(item.Run.RunId)}</td><td>{Ref(item.ResultTag)}</td><td>{QueueStateText(item.State)}</td><td>{Rationale(item.Run.Command)}</td></tr>");
			}

			body.Append("</table>");
		}

		if (refs is null)
		{
			body.Append(Error($"'{name}' has no readable repository at '{state.Monorepo.SourceRepoDir(name)}'."));
		}
		else
		{
			body.Append("<h2>Branches and tags</h2><table class=\"list\"><tr><th>Kind</th><th>Name</th><th>Commit</th><th>Subject</th></tr>");
			foreach (var reference in refs)
			{
				var kind = reference.Kind switch
				{
					GitRefKind.Branch => "branch",
					GitRefKind.AnnotatedTag => "<b>annotated tag</b>",
					_ => "<span class=\"muted\">lightweight tag</span>"
				};
				var label = reference.IsTag ? $"<a class=\"ref\" href=\"/tag?name={Url(reference.Name)}\">{E(reference.Name)}</a>" : Ref(reference.Name);
				body.Append($"<tr><td>{kind}</td><td>{label}</td><td><a class=\"hash\" href=\"/commit/{Url(name)}/{Url(reference.Commit)}\">{E(Short(reference.Commit))}</a></td><td>{E(reference.Subject)}</td></tr>");
			}

			body.Append("</table>");
		}

		body.Append("<h2>Agentic runs that touched it</h2><table class=\"list\"><tr><th>Run</th><th>Status</th><th>Base</th><th>Result</th><th>Result tag</th><th>Rationale</th></tr>");
		foreach (var run in state.Runs)
		{
			foreach (var entry in run.Components.Where(entry => entry.Name == name))
			{
				body.Append($"<tr><td>{RunLink(run.RunId)}</td><td>{Status(run.Status)}</td><td>{E(entry.CommitIsh)}</td><td>{E(entry.ResultStatus.ToString().ToLowerInvariant())}</td><td>{(entry.ResultTag is null ? "-" : Ref(entry.ResultTag))}</td><td>{Rationale(run.Command)}</td></tr>");
			}
		}

		body.Append("</table>");
		return View("/components", $"Component {name}", body.ToString());
	}

	internal static string PolicyText(MergePolicy policy) =>
		$"conflicts → <b>{(policy.ManualSemantic ? "a human" : "the resolver")}</b>, weave warnings → <b>{E(IntegrationRecord.Snake(policy.Warnings))}</b>, " +
		$"advance <b>{(policy.AutoAdvance ? "auto" : "manual")}</b>{(policy.ManualPaths.IsEmpty ? "" : $", manual paths <code>{E(string.Join(", ", policy.ManualPaths.Texts))}</code>")}";

	internal static string QueueStateText(QueueState state) => state switch
	{
		QueueState.Waiting => "<span class=\"qs waiting\">waiting</span>",
		QueueState.Integrated => "<span class=\"qs integrated\">integrated, not advanced</span>",
		QueueState.NeedsAttention => "<span class=\"qs attention\">needs attention</span>",
		_ => "<span class=\"qs landed\">landed</span>"
	};

	// ----- timeline -----

	private async Task<IResult> TimelineAsync(HttpRequest request)
	{
		var monorepo = Current();
		var chosen = request.Query["c"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList();
		var page = int.TryParse(request.Query["page"], out var p) ? p : 1;
		var size = int.TryParse(request.Query["n"], out var n) ? n : Timeline.DefaultPageSize;

		var form = new StringBuilder("<form method=\"get\" action=\"/timeline\"><div class=\"pick\">");
		foreach (var component in monorepo.Components)
		{
			var check = chosen.Contains(component.Name) ? " checked" : "";
			form.Append($"<label><input type=\"checkbox\" name=\"c\" value=\"{E(component.Name)}\"{check}> {E(component.Name)}</label>");
		}

		form.Append($"</div>Per page <input type=\"number\" name=\"n\" min=\"1\" max=\"{Timeline.MaxPageSize}\" value=\"{size}\"> <button>Show timeline</button></form>");

		if (chosen.Count == 0)
		{
			return View("/timeline", "Timeline", form + Notice("Choose the components to follow. Their dependencies join them automatically; the whole monorepo is never logged at once."));
		}

		var unit = Timeline.Unit(monorepo, chosen);
		var timeline = await Timeline.ReadAsync(monorepo, unit, page, size);
		var added = unit.Where(name => !chosen.Contains(name)).ToList();

		var body = new StringBuilder(form.ToString());
		body.Append($"<p>Unit: {string.Join(" ", unit.Select(name => $"<a class=\"chip\" href=\"{ComponentHref(name)}\">{E(name)}</a>"))}");
		if (added.Count > 0)
		{
			body.Append($" <span class=\"muted\">({E(string.Join(", ", added))} joined as dependencies)</span>");
		}

		body.Append("</p>");
		if (timeline.Entries.Count == 0)
		{
			body.Append(Notice(page > 1 ? "No more commits." : "These components have no commits yet."));
		}
		else
		{
			body.Append("<table class=\"list\"><tr><th>Time</th><th>Component</th><th>Commit</th><th>Subject</th><th>Author</th></tr>");
			string? day = null;
			foreach (var entry in timeline.Entries)
			{
				var entryDay = entry.Date.ToUniversalTime().ToString("yyyy-MM-dd dddd", System.Globalization.CultureInfo.InvariantCulture);
				if (entryDay != day)
				{
					day = entryDay;
					body.Append($"<tr><th colspan=\"5\">{E(day)}</th></tr>");
				}

				body.Append($"""
					<tr><td class="muted">{entry.Date.ToUniversalTime():HH:mm}</td>
					<td><a class="chip" href="{ComponentHref(entry.Component)}">{E(entry.Component)}</a></td>
					<td><a class="hash" href="/commit/{Url(entry.Component)}/{Url(entry.Hash)}">{E(entry.ShortHash)}</a></td>
					<td>{E(entry.Subject)} {string.Concat(entry.Refs.Select(Ref))}</td>
					<td class="muted">{E(entry.Author)}</td></tr>
					""");
			}

			body.Append("</table>");
		}

		var query = string.Join("&", chosen.Select(name => $"c={Url(name)}")) + $"&n={timeline.PageSize}";
		body.Append("<div class=\"pager\">");
		if (timeline.Page > 1)
		{
			body.Append($"<a href=\"/timeline?{query}&page={timeline.Page - 1}\">← newer</a>");
		}

		if (timeline.HasMore)
		{
			body.Append($"<a href=\"/timeline?{query}&page={timeline.Page + 1}\">older →</a>");
		}

		body.Append($"<span class=\"muted\">page {timeline.Page}</span></div>");
		return View("/timeline", "Timeline", body.ToString());
	}

	private async Task<IResult> CommitAsync(string component, string hash)
	{
		var monorepo = Current();
		if (monorepo.FindComponent(component) is null)
		{
			return NotFound($"Component '{component}' is not registered in components.toml.");
		}

		// Only a hash reaches git: nothing from the URL can be read as an option or a revision expression.
		if (hash.Length is < 4 or > 64 || !hash.All(Uri.IsHexDigit))
		{
			return NotFound($"'{hash}' is not a commit hash.");
		}

		var git = GitClient.In(monorepo.SourceRepoDir(component));
		var show = await git.RunAsync(["show", "--no-color", "--stat", "--format=fuller", hash, "--"]);
		if (show.ExitCode != 0)
		{
			return NotFound($"Commit {hash} does not exist in component '{component}'.");
		}

		var full = (await git.RunOrThrowAsync(["rev-parse", "--verify", $"{hash}^{{commit}}"])).Trim();
		var refs = await git.RunAsync(["for-each-ref", "--format=%(refname:short)", "--points-at", full, "refs/heads", "refs/tags"]);
		var chips = string.Concat(refs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Ref));
		var body = $"""
			<table class="list facts"><tr><td>Component</td><td><a href="{ComponentHref(component)}">{E(component)}</a></td></tr>
			<tr><td>Commit</td><td><code>{E(full)}</code></td></tr>
			<tr><td>Refs</td><td>{(chips.Length == 0 ? "<span class=\"muted\">-</span>" : chips)}</td></tr></table>
			<pre>{E(show.Output)}</pre>
			""";
		return View("/timeline", $"Commit {Short(full)}", body);
	}
}
