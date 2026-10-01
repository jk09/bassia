namespace Bassia.Web;

using System.Text;
using System.Text.Json;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Graph;
using Bassia.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using static Bassia.Web.Html;

/// <summary>
/// The web dashboard (<c>bassia web</c>): a local site over one monorepo in the spirit of Fossil's web UI. Pages are
/// rendered on the server and work without JavaScript. It reads through the same model as the CLI, and starts
/// runs through the same <see cref="RunSupervisor"/> over <see cref="AgentCommand.StartRunAsync"/>.
/// Integrations are shown and their triage previewed; running them stays with the CLI.
/// </summary>
internal sealed class Dashboard
{
	private static readonly string[] Models = ["sonnet", "opus", "haiku"];
	private static readonly string[] Efforts = ["low", "medium", "high"];

	private readonly Monorepo monorepo;
	private readonly RunSupervisor supervisor;
	private readonly RunMetadataStore store;
	private readonly IntegrationStore integrations;
	private readonly ComponentGraph graph;

	public Dashboard(Monorepo monorepo, RunSupervisor supervisor)
	{
		this.monorepo = monorepo;
		this.supervisor = supervisor;
		store = new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir);
		integrations = new IntegrationStore(store);
		graph = new ComponentGraph(monorepo.Components);
	}

	/// <summary>
	/// Required in every form this server renders and checked on every POST. The dashboard can start shell commands,
	/// so a page on another site must not be able to submit a form to it (cross-site request forgery).
	/// </summary>
	public string Token { get; } = Guid.NewGuid().ToString("N");

	public WebApplication Build(string url)
	{
		var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = monorepo.Root });
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
	/// every POST must carry <see cref="Token"/>, and an expected failure becomes an error page, not a dead server.
	/// </summary>
	private async Task GuardAsync(HttpContext context, RequestDelegate next)
	{
		if (context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			await context.Response.WriteAsync("The Bassia dashboard only answers requests addressed to 127.0.0.1 or localhost.");
			return;
		}

		if (HttpMethods.IsPost(context.Request.Method))
		{
			var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync() : null;
			if (form is null || form["token"] != Token)
			{
				context.Response.StatusCode = StatusCodes.Status403Forbidden;
				await context.Response.WriteAsync("Missing or wrong form token; reload the page and try again.");
				return;
			}
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
			await context.Response.WriteAsync(Page(monorepo.Root, "", "Error", Error(ex.Message)));
		}
	}

	private void Map(WebApplication app)
	{
		app.MapGet("/style.css", () => Results.Text(Stylesheet, "text/css"));
		app.MapGet("/app.js", () => Results.Text(Script, "text/javascript"));
		app.MapGet("/graph.svg", () => Results.Text(graph.ToSvg(ComponentHref), "image/svg+xml"));

		app.MapGet("/", HomeAsync);
		app.MapGet("/fragment/live-runs", LiveRunsFragment);
		app.MapGet("/components", ComponentsAsync);
		app.MapGet("/components/{name}", ComponentAsync);
		app.MapGet("/timeline", TimelineAsync);
		app.MapGet("/commit/{component}/{hash}", CommitAsync);
		app.MapGet("/runs", RunsAsync);
		app.MapGet("/runs/{id}", RunAsync);
		app.MapGet("/runs/{id}/events", RunEventsAsync);
		app.MapPost("/runs/{id}/stop", StopRun);
		app.MapGet("/new", NewRunAsync);
		app.MapPost("/new", StartRunAsync);
		app.MapGet("/new/compose", (HttpRequest request) => Results.Text(Compose(request.Query)));
		app.MapGet("/integrations", IntegrationsAsync);
		app.MapGet("/integrations/plan", PlanAsync);
		app.MapGet("/integrations/{id}", IntegrationAsync);
	}

	private IResult View(string active, string title, string body, int status = StatusCodes.Status200OK) =>
		Results.Content(Page(monorepo.Root, active, title, body), "text/html; charset=utf-8", Encoding.UTF8, status);

	private IResult NotFound(string what) => View("", "Not found", Error(what), StatusCodes.Status404NotFound);

	private static string ComponentHref(string name) => $"/components/{Url(name)}";

	private string Hidden() => $"<input type=\"hidden\" name=\"token\" value=\"{Token}\">";

	// ----- home -----

	private async Task<IResult> HomeAsync()
	{
		var runs = await store.ListLatestAsync();
		var recorded = await integrations.ListLatestAsync();
		var live = supervisor.Cards().Count(card => card.IsLive);
		string Count(string label, int n, string href) => $"<a class=\"card\" href=\"{href}\"><div class=\"n\">{n}</div><div class=\"muted\">{E(label)}</div></a>";

		var body = new StringBuilder();
		body.Append("<div class=\"cards\">")
			.Append(Count("components", monorepo.Components.Count, "/components"))
			.Append(Count("live runs", live, "/runs"))
			.Append(Count("completed runs", runs.Count(run => run.Status == "completed"), "/runs"))
			.Append(Count("failed or partial runs", runs.Count(run => run.Status is "failed" or "partial"), "/runs"))
			.Append(Count("integrations", recorded.Count, "/integrations"))
			.Append("</div>");

		body.Append("<div data-live-src=\"/fragment/live-runs\">").Append(LiveRunsTable()).Append("</div>");
		body.Append("<h2>Components</h2>");
		body.Append(monorepo.Components.Count == 0
			? Notice("components.toml is empty; register one with 'bassia component add -url <url>'.")
			: $"<div class=\"graph\">{graph.ToSvg(ComponentHref)}</div><p class=\"muted\">Click a component to open it; <a href=\"/graph.svg\">graph.svg</a> is the same picture as a file.</p>");
		body.Append("<h2>Latest runs</h2>").Append(RunsTable(runs.Take(8).ToList()));
		body.Append("<h2>Latest integrations</h2>").Append(IntegrationsTable(recorded.Take(5).ToList()));
		return View("/", "Home", body.ToString());
	}

	// ----- components -----

	private async Task<IResult> ComponentsAsync()
	{
		var runs = await store.ListLatestAsync();
		var rows = new StringBuilder();
		foreach (var component in monorepo.Components)
		{
			var refs = await RefsAsync(component.Name);
			rows.Append($"""
				<tr><td><a href="{ComponentHref(component.Name)}"><b>{E(component.Name)}</b></a></td>
				<td class="muted">{E(component.Url)}</td>
				<td>{References(component.References.Select(reference => reference.Name))}</td>
				<td>{References(graph.ReferrersOf(component.Name).Select(reference => reference.Name))}</td>
				<td>{refs?.Count(reference => reference.Kind == GitRefKind.AnnotatedTag).ToString() ?? "<span class=\"muted\">no repo</span>"}</td>
				<td>{refs?.Count(reference => reference.Kind == GitRefKind.Branch).ToString() ?? ""}</td>
				<td>{runs.Count(run => run.Components.Any(entry => entry.Name == component.Name))}</td>
				<td><a href="/timeline?c={Url(component.Name)}">timeline</a></td></tr>
				""");
		}

		var body = $"""
			<table class="list"><tr><th>Component</th><th>URL</th><th>Needs</th><th>Used by</th><th>Annotated tags</th><th>Branches</th><th>Runs</th><th></th></tr>
			{rows}</table>
			<div class="graph">{graph.ToSvg(ComponentHref)}</div>
			""";
		return View("/components", "Components", body);
	}

	private static string References(IEnumerable<string> names)
	{
		var list = names.ToList();
		return list.Count == 0 ? "<span class=\"muted\">-</span>" : string.Join(" ", list.Select(name => $"<a class=\"chip\" href=\"{ComponentHref(name)}\">{E(name)}</a>"));
	}

	private async Task<IReadOnlyList<GitRef>?> RefsAsync(string component)
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
		if (monorepo.FindComponent(name) is not { } component)
		{
			return NotFound($"Component '{name}' is not registered in components.toml.");
		}

		var refs = await RefsAsync(name);
		var body = new StringBuilder($"""
			<table class="list facts">
			<tr><td>Source repo</td><td><code>{E(monorepo.SourceRepoDir(name))}</code></td></tr>
			<tr><td>URL</td><td>{E(component.Url)}</td></tr>
			<tr><td>Needs</td><td>{References(component.References.Select(reference => reference.Name))}</td></tr>
			<tr><td>Used by</td><td>{References(graph.ReferrersOf(name).Select(reference => reference.Name))}</td></tr>
			</table>
			<p><a href="/timeline?c={Url(name)}">Timeline of {E(name)} and what it needs</a> · <a href="/new?c={Url(name)}">Start a run on it</a></p>
			""");

		if (refs is null)
		{
			body.Append(Error($"'{name}' has no readable repository at '{monorepo.SourceRepoDir(name)}'."));
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
				body.Append($"<tr><td>{kind}</td><td>{Ref(reference.Name)}</td><td><a class=\"hash\" href=\"/commit/{Url(name)}/{Url(reference.Commit)}\">{E(Short(reference.Commit))}</a></td><td>{E(reference.Subject)}</td></tr>");
			}

			body.Append("</table>");
		}

		body.Append("<h2>Agentic runs that touched it</h2><table class=\"list\"><tr><th>Run</th><th>Status</th><th>Base</th><th>Result</th><th>Result tag</th><th>Rationale</th></tr>");
		foreach (var run in await store.ListLatestAsync())
		{
			foreach (var entry in run.Components.Where(entry => entry.Name == name))
			{
				body.Append($"<tr><td>{RunLink(run.RunId)}</td><td>{Status(run.Status)}</td><td>{E(entry.CommitIsh)}</td><td>{E(entry.ResultStatus.ToString().ToLowerInvariant())}</td><td>{(entry.ResultTag is null ? "-" : Ref(entry.ResultTag))}</td><td>{Rationale(run.Command)}</td></tr>");
			}
		}

		body.Append("</table>");
		return View("/components", $"Component {name}", body.ToString());
	}

	// ----- timeline -----

	private async Task<IResult> TimelineAsync(HttpRequest request)
	{
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

	// ----- runs -----

	private async Task<IResult> RunsAsync()
	{
		var body = $"""
			<p><a class="button" href="/new">New run</a></p>
			<div data-live-src="/fragment/live-runs">{LiveRunsTable()}</div>
			<h2>Recorded runs</h2>
			{RunsTable(await store.ListLatestAsync())}
			""";
		return View("/runs", "Agentic runs", body);
	}

	private IResult LiveRunsFragment(HttpContext context)
	{
		context.Response.Headers["X-Live"] = supervisor.Cards().Any(card => card.IsLive) ? "1" : "0";
		return Results.Content(LiveRunsTable(), "text/html; charset=utf-8");
	}

	/// <summary>The runs this server started, newest first; the part of a page that refreshes itself while any is live.</summary>
	private string LiveRunsTable()
	{
		var cards = supervisor.Cards();
		if (cards.Count == 0)
		{
			return "";
		}

		var rows = string.Concat(cards.Select(card => $"""
			<tr><td><a class="id" href="/runs/{LiveKey(card)}">{E(card.Label)}</a></td>
			<td>{Status(card.IsLive ? "live" : RunCard.PhaseText(card.Phase).ToLowerInvariant())} <span class="muted">{E(RunCard.PhaseText(card.Phase).ToLowerInvariant())}</span></td>
			<td>{RunCard.ElapsedText(card.Elapsed)}</td><td>{E(card.Select)}</td><td>{Rationale(card.Command)}</td>
			<td class="muted">{E(card.LastOutput ?? card.Message)}</td></tr>
			"""));
		return $"<h2>Started from this dashboard</h2><table class=\"list\"><tr><th>Run</th><th>State</th><th>Elapsed</th><th>Selection</th><th>Rationale</th><th>Latest</th></tr>{rows}</table>";
	}

	private static string LiveKey(RunCard card) => "s" + card.Key.TrimStart('#');

	private RunCard? LiveCard(string id) =>
		id.Length > 1 && id[0] == 's' && id[1..].All(char.IsAsciiDigit)
			? supervisor.Cards().FirstOrDefault(card => card.Key == "#" + id[1..])
			: supervisor.Cards().FirstOrDefault(card => card.RunId == id);

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
		var card = LiveCard(id);
		var runId = card?.RunId ?? (card is null ? RunMetadata.NormalizeRunId(id) : null);
		var metadata = runId is not null && RunMetadata.IsRunId(runId) ? await store.LoadLatestAsync(runId) : null;
		card ??= metadata is null ? null : LiveCard(metadata.RunId);
		if (card is null && metadata is null)
		{
			return NotFound($"No agentic run '{id}' is recorded or running.");
		}

		var body = new StringBuilder();
		if (card is not null)
		{
			var key = LiveKey(card);
			body.Append($"""
				<div{(card.IsLive ? $" data-events=\"/runs/{key}/events\"" : "")}>
				<table class="list facts">
				<tr><td>Phase</td><td><b data-field="phase">{E(RunCard.PhaseText(card.Phase))}</b></td></tr>
				<tr><td>Elapsed</td><td data-field="elapsed">{RunCard.ElapsedText(card.Elapsed)}</td></tr>
				<tr><td>Latest</td><td data-field="message">{E(card.Message)}</td></tr>
				<tr><td>Command</td><td><code>{E(card.Command)}</code></td></tr>
				</table>
				""");
			if (card.IsLive)
			{
				body.Append($"<form class=\"inline\" method=\"post\" action=\"/runs/{key}/stop\">{Hidden()}<button class=\"danger\">Stop run</button></form>");
			}

			body.Append($"<h2>Agent output</h2><pre class=\"output\" id=\"output\">{E(string.Join('\n', supervisor.OutputOf(card.Key)))}</pre></div>");
		}

		if (metadata is not null)
		{
			body.Append($"""
				<h2>Record</h2>
				<table class="list facts">
				<tr><td>Run</td><td><code>{E(metadata.RunId)}</code></td></tr>
				<tr><td>Status</td><td>{Status(metadata.Status)}</td></tr>
				<tr><td>Selection</td><td>{E(metadata.Select)}</td></tr>
				<tr><td>Command</td><td><code>{E(metadata.Command)}</code></td></tr>
				<tr><td>Created</td><td>{Time(metadata.Created)}</td></tr>
				<tr><td>Finished</td><td>{Time(metadata.Finished)}</td></tr>
				<tr><td>Agent exit code</td><td>{E(metadata.AgentExitCode?.ToString() ?? "-")}</td></tr>
				<tr><td>Workspace</td><td><code>{E(metadata.WorkspacePath)}</code></td></tr>
				<tr><td>Record tag</td><td><code>{E(RunMetadata.TagName(metadata.RunId, metadata.Lineage))}</code></td></tr>
				</table>
				<table class="list"><tr><th>Component</th><th>Base</th><th>Base commit</th><th>Result</th><th>Result commit</th><th>Result tag</th></tr>
				""");
			foreach (var component in metadata.Components)
			{
				var result = component.ResultError is null ? E(component.ResultStatus.ToString().ToLowerInvariant()) : $"<span class=\"status failed\">failed</span> {E(component.ResultError)}";
				var resultCommit = component.ResultCommit is null ? "-" : $"<a class=\"hash\" href=\"/commit/{Url(component.Name)}/{Url(component.ResultCommit)}\">{E(Short(component.ResultCommit))}</a>";
				body.Append($"""
					<tr><td><a href="{ComponentHref(component.Name)}">{E(component.Name)}</a></td><td>{Ref(component.CommitIsh)}</td>
					<td><a class="hash" href="/commit/{Url(component.Name)}/{Url(component.Commit)}">{E(Short(component.Commit))}</a></td>
					<td>{result}</td><td>{resultCommit}</td><td>{(component.ResultTag is null ? "-" : Ref(component.ResultTag))}</td></tr>
					""");
			}

			body.Append("</table>");
			if (IntegrationPlanner.HasResults(metadata))
			{
				body.Append($"<p><a href=\"/integrations/plan?run={Url(metadata.RunId)}\">Preview the triage of integrating this run</a></p>");
			}
		}

		var title = metadata is not null ? $"Run {RunMetadata.Key(metadata.RunId)}" : $"Run {card!.Label}";
		return View("/runs", title, body.ToString());
	}

	/// <summary>
	/// The run's state and the tail of the agent's output as server-sent events, twice a second while the run is live,
	/// then a <c>done</c> event. The whole kept tail is sent each time it changes: it is short, and replacing it keeps
	/// the page right even after lines fall off the front of the tail.
	/// </summary>
	private async Task RunEventsAsync(HttpContext context, string id)
	{
		if (LiveCard(id) is not { } first)
		{
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		context.Response.ContentType = "text/event-stream";
		context.Response.Headers.CacheControl = "no-cache";
		var sentOutput = -1;
		var aborted = context.RequestAborted;
		var key = first.Key;

		while (!aborted.IsCancellationRequested)
		{
			var card = supervisor.Cards().First(candidate => candidate.Key == key);
			var state = JsonSerializer.Serialize(new Dictionary<string, string>
			{
				["phase"] = RunCard.PhaseText(card.Phase),
				["elapsed"] = RunCard.ElapsedText(card.Elapsed),
				["message"] = card.Message
			});
			await context.Response.WriteAsync($"event: state\ndata: {state}\n\n", aborted);

			var output = supervisor.OutputOf(key);
			var fingerprint = output.Count == 0 ? 0 : HashCode.Combine(output.Count, output[^1]);
			if (fingerprint != sentOutput)
			{
				sentOutput = fingerprint;
				await context.Response.WriteAsync($"event: output\ndata: {JsonSerializer.Serialize(output)}\n\n", aborted);
			}

			await context.Response.Body.FlushAsync(aborted);
			if (!card.IsLive)
			{
				await context.Response.WriteAsync("event: done\ndata: {}\n\n", aborted);
				return;
			}

			try
			{
				await Task.Delay(500, aborted);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	private IResult StopRun(string id)
	{
		if (LiveCard(id) is not { } card)
		{
			return NotFound($"No run '{id}' was started from this dashboard.");
		}

		supervisor.Cancel(card.Key);
		return Results.Redirect($"/runs/{LiveKey(card)}", permanent: false, preserveMethod: false);
	}

	// ----- new run -----

	private async Task<IResult> NewRunAsync(HttpRequest request) =>
		View("/new", "New agentic run", await NewRunFormAsync(request.Query["c"].Select(value => value ?? "").ToHashSet(StringComparer.Ordinal), null, null));

	private async Task<string> NewRunFormAsync(IReadOnlySet<string> chosen, IFormCollection? previous, string? error)
	{
		var body = new StringBuilder(error is null ? "" : Error(error));
		body.Append($"<form method=\"post\" action=\"/new\">{Hidden()}");
		body.Append("<h2>Components</h2><p class=\"muted\">Choose what the agent works on and the annotated tag it starts from. Components a choice depends on join it automatically, at the tag chosen for them here.</p><div class=\"pick\">");
		foreach (var component in monorepo.Components)
		{
			var tags = (await RefsAsync(component.Name) ?? []).Where(reference => reference.Kind == GitRefKind.AnnotatedTag).Select(reference => reference.Name).ToList();
			var picked = previous?[$"tag:{component.Name}"].ToString();
			var selected = !string.IsNullOrEmpty(picked) ? picked : tags.LastOrDefault(tag => !tag.StartsWith(RunMetadata.RefPrefix, StringComparison.Ordinal)) ?? tags.LastOrDefault();
			var check = chosen.Contains(component.Name) ? " checked" : "";
			var options = tags.Count == 0
				? "<option value=\"\">no annotated tag</option>"
				: string.Concat(tags.Select(tag => $"<option{(tag == selected ? " selected" : "")}>{E(tag)}</option>"));
			body.Append($"<label><input type=\"checkbox\" name=\"c\" value=\"{E(component.Name)}\"{check}> <b>{E(component.Name)}</b> @ <select name=\"tag:{E(component.Name)}\">{options}</select></label>");
		}

		string Value(string field, string fallback = "") => previous?[field].ToString() is { Length: > 0 } value ? value : fallback;
		string Options(string field, IEnumerable<string> values) =>
			"<option value=\"\">agent default</option>" + string.Concat(values.Select(value => $"<option{(Value(field) == value ? " selected" : "")}>{E(value)}</option>"));

		body.Append($"""
			</div>
			<h2>Prompt</h2>
			<div class="chat">
			<textarea id="prompt" name="prompt" placeholder="Describe the task for the agent… (enter sends, shift+enter adds a line)" autofocus>{E(Value("prompt"))}</textarea>
			<div class="bar">
			<label>Model <select name="model">{Options("model", Models)}</select></label>
			<label>Effort <select name="effort">{Options("effort", Efforts)}</select></label>
			<input class="grow" type="text" name="context" placeholder="Additional context (optional)" value="{E(Value("context"))}">
			<button>Send ➤</button>
			</div>
			<div class="bar">
			<label>Agent <input type="text" name="agent" size="40" value="{E(Value("agent", monorepo.AgentCommand))}"></label>
			<input class="grow" type="text" id="preview" name="command" value="{E(Value("command"))}" placeholder="the command is composed from the fields above">
			</div>
			<div class="hint">Leave the command empty to compose it from the prompt; type one to run exactly that. It runs in the run folder, exactly as <code>bassia run start -select … -run …</code>.</div>
			</div>
			</form>
			""");
		return body.ToString();
	}

	private string Compose(IQueryCollection fields) => RunCommands.ComposeCommand(
		string.IsNullOrWhiteSpace(fields["agent"]) ? monorepo.AgentCommand : fields["agent"].ToString(),
		string.IsNullOrWhiteSpace(fields["model"]) ? null : fields["model"].ToString(),
		string.IsNullOrWhiteSpace(fields["effort"]) ? null : fields["effort"].ToString(),
		fields["prompt"].ToString(),
		fields["context"].ToString());

	private async Task<IResult> StartRunAsync(HttpRequest request)
	{
		var form = await request.ReadFormAsync();
		var chosen = form["c"].Select(value => value ?? "").Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
		async Task<IResult> Again(string error) =>
			View("/new", "New agentic run", await NewRunFormAsync(chosen, form, error), StatusCodes.Status400BadRequest);

		if (chosen.Count == 0)
		{
			return await Again("Choose at least one component.");
		}

		var command = form["command"].ToString().Trim();
		if (command.Length == 0)
		{
			if (string.IsNullOrWhiteSpace(form["prompt"]))
			{
				return await Again("Write a prompt for the agent, or a command to run.");
			}

			command = Compose(new QueryCollection(form.ToDictionary(pair => pair.Key, pair => pair.Value)));
		}

		IReadOnlyList<ComponentDefinition> closure;
		try
		{
			closure = monorepo.Closure(chosen);
		}
		catch (MonorepoException ex)
		{
			return await Again(ex.Message);
		}

		var selections = new List<string>();
		foreach (var component in closure)
		{
			var tag = form[$"tag:{component.Name}"].ToString();
			if (string.IsNullOrWhiteSpace(tag))
			{
				return await Again($"'{component.Name}' has no annotated tag to start from; create one on its component page or with git tag -a.");
			}

			selections.Add($"{component.Name}@{tag}");
		}

		var key = supervisor.Start(string.Join(",", selections), command);
		return Results.Redirect($"/runs/s{key.TrimStart('#')}");
	}

	// ----- integrations -----

	private async Task<IResult> IntegrationsAsync()
	{
		var candidates = (await store.ListLatestAsync()).Where(IntegrationPlanner.HasResults).OrderBy(run => run.Created, StringComparer.Ordinal).ToList();
		var body = new StringBuilder(IntegrationsTable(await integrations.ListLatestAsync()));
		body.Append("<h2>Preview a triage</h2><p class=\"muted\">Choose runs with results to see how they would integrate: which steps git merges by syntax, which go to the resolver for a semantic merge. Nothing changes; integrate with <code>bassia integration start</code>.</p>");
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
			<td>{string.Join(" ", record.Runs.Select(RunLink))}</td>
			<td>{string.Concat(record.Components.Where(component => component.ResultTag is not null).Select(component => $"{E(component.Name)} {Ref(component.ResultTag!)}{(component.Advanced ? $"<span class=\"muted\">→ {E(component.BaseRef)}</span> " : "")}"))}</td></tr>
			"""));
		return $"<table class=\"list\"><tr><th>Integration</th><th>Status</th><th>Created</th><th>Runs</th><th>Results</th></tr>{rows}</table>";
	}

	private async Task<IResult> PlanAsync(HttpRequest request)
	{
		var ids = request.Query["run"].Select(value => value ?? "").Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
		var runs = (await store.ListLatestAsync()).Where(run => ids.Contains(run.RunId)).ToList();
		if (runs.Count == 0)
		{
			return View("/integrations", "Triage", Error("Choose at least one recorded run with results."), StatusCodes.Status400BadRequest);
		}

		var plan = await IntegrationPlanner.PlanAsync(monorepo, runs);
		var command = $"bassia integration start -runs {string.Join(",", runs.OrderBy(run => run.Created, StringComparer.Ordinal).Select(run => RunMetadata.Key(run.RunId)))}";
		var body = $"""
			<p>{E(Summary(plan))}. Nothing was changed. To integrate: <code>{E(command)}</code>.</p>
			{StepsTable(plan)}
			""";
		return View("/integrations", "Triage", body);
	}

	/// <summary>One line: how many steps go which way.</summary>
	internal static string Summary(IReadOnlyList<ComponentIntegration> plan)
	{
		var steps = plan.SelectMany(component => component.Steps).ToList();
		var upToDate = steps.Count(step => step.Triage == Triage.UpToDate && step.Strategy != MergeStrategy.Skip);
		var syntactic = steps.Count(step => step.Strategy == MergeStrategy.Syntactic) - upToDate;
		var semantic = steps.Count(step => step.Strategy == MergeStrategy.Semantic);
		var skipped = steps.Count(step => step.Strategy == MergeStrategy.Skip);
		return $"{syntactic} by git (syntax) · {semantic} by the resolver (semantic) · {upToDate} up to date · {skipped} skipped";
	}

	private async Task<IResult> IntegrationAsync(string id)
	{
		var record = IntegrationRecord.IsId(IntegrationRecord.NormalizeId(id))
			? await integrations.LoadLatestAsync(IntegrationRecord.NormalizeId(id))
			: null;
		if (record is null)
		{
			return NotFound($"No integration '{id}' is recorded.");
		}

		var body = new StringBuilder($"""
			<table class="list facts">
			<tr><td>Integration</td><td><code>{E(record.IntegrationId)}</code></td></tr>
			<tr><td>Status</td><td>{Status(record.Status)}</td></tr>
			<tr><td>Runs</td><td>{string.Join(" ", record.Runs.Select(RunLink))}</td></tr>
			<tr><td>Resolver</td><td><code>{E(record.Resolver)}</code></td></tr>
			<tr><td>Created</td><td>{Time(record.Created)}</td></tr>
			<tr><td>Finished</td><td>{Time(record.Finished)}</td></tr>
			<tr><td>Workspace</td><td><code>{E(record.WorkspacePath)}</code></td></tr>
			</table>
			{StepsTable(record.Components)}
			""");

		var notes = record.AllSteps.Where(step => step.Note is not null || step.Brief is not null).ToList();
		if (notes.Count > 0)
		{
			body.Append("<h2>Notes and briefs</h2><table class=\"list\"><tr><th>Run</th><th>Note</th><th>Semantic brief</th></tr>");
			foreach (var step in notes)
			{
				body.Append($"<tr><td>{RunLink(step.RunId)}</td><td>{E(step.Note ?? "-")}</td><td><code>{E(step.Brief ?? "-")}</code></td></tr>");
			}

			body.Append("</table>");
		}

		return View("/integrations", $"Integration {IntegrationRecord.Key(record.IntegrationId)}", body.ToString());
	}

	/// <summary>The triage or the outcome, per component in execution order: the dashboard's view of merge resolution.</summary>
	private static string StepsTable(IReadOnlyList<ComponentIntegration> components)
	{
		var body = new StringBuilder("<table class=\"list\"><tr><th>Component</th><th>#</th><th>Run</th><th>Triage</th><th>Strategy</th><th>Conflicts</th><th>Outcome</th></tr>");
		foreach (var component in components)
		{
			for (var i = 0; i < component.Steps.Count; i++)
			{
				var step = component.Steps[i];
				var strategy = IntegrationRecord.Snake(step.Strategy);
				var conflicts = string.Join(", ", step.Conflicts) + (step.ConflictsWith.Count > 0 ? $" vs {string.Join(", ", step.ConflictsWith.Select(RunMetadata.Key))}" : "");
				var outcome = IntegrationRecord.Snake(step.Outcome) + (step.Commit is null ? "" : $" {Short(step.Commit)}");
				var head = i == 0 ? $"<a href=\"{ComponentHref(component.Name)}\"><b>{E(component.Name)}</b></a> <span class=\"muted\">onto {E(component.BaseRef)} {E(Short(component.BaseCommit))}</span>" : "";
				body.Append($"""
					<tr><td>{head}</td><td>{(step.Strategy == MergeStrategy.Skip ? "-" : i + 1)}</td><td>{RunLink(step.RunId)} <span class="muted">{E(step.Rationale)}</span></td>
					<td>{E(IntegrationRecord.Snake(step.Triage).Replace('_', ' '))}</td><td class="strategy-{strategy}">{E(strategy.ToUpperInvariant())}{(step.Overridden ? "*" : "")}</td>
					<td>{E(conflicts.Trim().Length == 0 ? "-" : conflicts.Trim())}</td><td>{E(outcome)}</td></tr>
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
