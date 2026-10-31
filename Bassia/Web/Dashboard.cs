namespace Bassia.Web;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using static Bassia.Web.Html;

/// <summary>What one dashboard request looks at: the monorepo's core, how it lands, and how run output is read.</summary>
internal sealed record DashboardScope(
	Repository Repo,
	ILanding Landing,
	string Label,
	Func<RunRecord, Task<string>> ReadLog,
	string? Account = null,
	string? Events = null,
	IReadOnlyList<(string Key, string Value, string Source)>? Settings = null);

/// <summary>
/// The web dashboard (<c>bassia web</c>, and the server's pages): a read-only site for watching agentic work - every
/// workspace and whether it can land on mainline now, live runs, mainline's landings, what keeps workspaces from
/// landing (and which paths keep colliding), leases and the repository's policy. Pages are rendered on the server,
/// pictures included, and work without JavaScript; the script refreshes the live parts. Nothing is changed from here:
/// every page shows the <c>bassia</c> commands that act on what it shows.
/// </summary>
internal sealed class Dashboard(Func<HttpContext, Task<DashboardScope>> open)
{
	private static readonly AsyncLocal<DashboardScope?> Scope = new();

	private static DashboardScope Current => Scope.Value ?? throw new InvalidOperationException("No dashboard scope.");

	private static Repository Repo => Current.Repo;

	public WebApplication Build(string url)
	{
		var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls(url);
		var app = builder.Build();
		app.Use(LoopbackAsync);
		app.Use(ServeAsync);
		Map(app);
		return app;
	}

	private static async Task LoopbackAsync(HttpContext context, RequestDelegate next)
	{
		if (context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			await context.Response.WriteAsync("The Bassia dashboard only answers requests addressed to 127.0.0.1 or localhost.");
			return;
		}

		await next(context);
	}

	/// <summary>Serves a dashboard request: reads only; an expected failure becomes an error page, not a dead server.</summary>
	public async Task ServeAsync(HttpContext context, RequestDelegate next)
	{
		if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
		{
			context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
			await context.Response.WriteAsync("The Bassia dashboard is read-only; change the monorepo with the bassia command line.");
			return;
		}

		try
		{
			if (!IsAsset(context.Request.Path))
			{
				Scope.Value = await open(context);
			}

			await next(context);
		}
		catch (Exception ex) when (!context.Response.HasStarted && ex is not OperationCanceledException)
		{
			context.Response.Clear();
			context.Response.StatusCode = ex is VersionException or MonorepoException ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
			context.Response.ContentType = "text/html; charset=utf-8";
			await context.Response.WriteAsync(Page(Scope.Value?.Label ?? "", "", "Error", Error(ex is VersionException or MonorepoException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}"),
				Scope.Value?.Account, Scope.Value?.Events));
		}
		finally
		{
			Scope.Value = null;
		}
	}

	public static bool IsAsset(PathString path) => path == "/style.css" || path == "/app.js";

	private static readonly string[] Paths = ["/style.css", "/app.js", "/fragment", "/workspaces", "/runs", "/mainline", "/conflicts", "/leases", "/files", "/config"];

	public static bool IsDashboardPath(PathString path) => path == "/" || Paths.Any(prefix => path.StartsWithSegments(prefix));

	public void Map(IEndpointRouteBuilder app)
	{
		app.MapGet("/style.css", () => Results.Text(Stylesheet + ExtraStyles, "text/css"));
		app.MapGet("/app.js", () => Results.Text(Script, "text/javascript"));
		app.MapGet("/", OverviewAsync);
		app.MapGet("/fragment/live", LiveFragmentAsync);
		app.MapGet("/workspaces", WorkspacesAsync);
		app.MapGet("/workspaces/{id}", WorkspaceAsync);
		app.MapGet("/runs", RunsAsync);
		app.MapGet("/runs/{id}", RunAsync);
		app.MapGet("/fragment/run/{id}", RunOutputFragmentAsync);
		app.MapGet("/mainline", MainlineAsync);
		app.MapGet("/mainline/{seq:long}", LandingAsync);
		app.MapGet("/conflicts", ConflictsAsync);
		app.MapGet("/leases", LeasesAsync);
		app.MapGet("/files", FilesAsync);
		app.MapGet("/config", ConfigAsync);
	}

	private static IResult Html(string active, string title, string body) =>
		Results.Content(Page(Current.Label, active, title, body, Current.Account, Current.Events), "text/html; charset=utf-8");

	private static IResult Fragment(string body, bool live) =>
		new FragmentResult(body, live);

	private sealed class FragmentResult(string body, bool live) : IResult
	{
		public async Task ExecuteAsync(HttpContext context)
		{
			context.Response.ContentType = "text/html; charset=utf-8";
			context.Response.Headers["X-Live"] = live ? "1" : "0";
			await context.Response.WriteAsync(body);
		}
	}

	// ----- data -----

	private sealed record Row(WorkspaceState State, LandResult? Check, Commit Working);

	/// <summary>Every workspace (open ones with a dry-run landing: can it land now?).</summary>
	private static async Task<List<Row>> RowsAsync(bool includeClosed, bool check = true)
	{
		var rows = new List<Row>();
		foreach (var state in await Repo.ListWorkspacesAsync(includeClosed))
		{
			LandResult? result = null;
			if (check && state.IsOpen)
			{
				try
				{
					result = await Current.Landing.LandAsync(new LandRequest(state.Id, DryRun: true));
				}
				catch (VersionException)
				{
				}
			}

			rows.Add(new Row(state, result, await Repo.Objects.ReadCommitAsync(state.Record.Working)));
		}

		return rows;
	}

	private static bool Live(RunRecord? run) => run is not null && Agents.AgentRunner.IsAlive(run);

	private static string Landability(Row row)
	{
		if (!row.State.IsOpen)
		{
			return Status(row.State.Record.Status);
		}

		return row.Check?.Outcome switch
		{
			LandOutcome.WouldLand => "<span class=\"status would-land\">can land</span>",
			LandOutcome.Nothing => "<span class=\"status muted\">no changes</span>",
			LandOutcome.NeedsReplay => $"<span class=\"status needs-replay\">needs replay</span> <span class=\"muted\">({row.Check.Conflicts.Count} path(s))</span>",
			LandOutcome.Conflict => $"<span class=\"status failed\">conflicts</span> <span class=\"muted\">({row.Check.Conflicts.Count})</span>",
			LandOutcome.Rejected => $"<span class=\"status stale\" title=\"{E(row.Check.Message)}\">blocked</span>",
			_ => "<span class=\"muted\">-</span>"
		};
	}

	// ----- overview -----

	private static async Task<IResult> OverviewAsync()
	{
		var main = await Repo.GetMainAsync();
		var rows = await RowsAsync(false);
		var live = rows.Where(row => Live(row.State.Record.Run)).ToList();
		var canLand = rows.Count(row => row.Check?.Outcome == LandOutcome.WouldLand);
		var blocked = rows.Count(row => row.Check?.Outcome is LandOutcome.NeedsReplay or LandOutcome.Conflict or LandOutcome.Rejected);
		var leases = await Repo.Leases.ListAsync();
		var body = new StringBuilder();
		body.Append("<div class=\"cards\">");
		body.Append($"<a class=\"card\" href=\"/mainline\"><div class=\"n\">{main.Seq}</div>mainline seq</a>");
		body.Append($"<a class=\"card\" href=\"/workspaces\"><div class=\"n\">{rows.Count}</div>open workspaces</a>");
		body.Append($"<a class=\"card live\" href=\"/runs\"><div class=\"n\">{live.Count}</div>live runs</a>");
		body.Append($"<a class=\"card\" href=\"/workspaces\"><div class=\"n\">{canLand}</div>can land now</a>");
		body.Append($"<a class=\"card{(blocked > 0 ? " alert" : "")}\" href=\"/conflicts\"><div class=\"n\">{blocked}</div>blocked from landing</a>");
		body.Append($"<a class=\"card\" href=\"/leases\"><div class=\"n\">{leases.Count}</div>leases</a>");
		body.Append("</div>");
		body.Append("<h2>Live runs</h2><div data-live-src=\"/fragment/live\">").Append(LiveCards(live)).Append("</div>");
		body.Append("<div class=\"split\"><section class=\"wide\"><h2>Workspaces</h2>").Append(WorkspaceTable(rows)).Append("</section>");
		body.Append("<section class=\"side\"><h2>Where workspaces stand</h2>").Append(StatusDonut(rows)).Append("</section></div>");
		body.Append("<h2>Recent landings</h2>").Append(await LandingTableAsync(10));
		body.Append("<p>").Append(Cmd("bassia agent start -prompt \"<task>\" -land")).Append(' ').Append(Cmd("bassia workspace create <name>")).Append("</p>");
		return Html("/", "Overview", body.ToString());
	}

	private static string StatusDonut(List<Row> rows)
	{
		var segments = new List<Segment>
		{
			new("can land", rows.Count(row => row.Check?.Outcome == LandOutcome.WouldLand), "s-completed"),
			new("needs replay", rows.Count(row => row.Check?.Outcome == LandOutcome.NeedsReplay), "s-partial"),
			new("conflicts", rows.Count(row => row.Check?.Outcome == LandOutcome.Conflict), "s-failed"),
			new("blocked", rows.Count(row => row.Check?.Outcome == LandOutcome.Rejected), "s-other"),
			new("no changes", rows.Count(row => row.Check?.Outcome == LandOutcome.Nothing), "s-abandoned")
		}.Where(segment => segment.Count > 0).ToList();
		return segments.Count == 0 ? Notice("No open workspace.") : Charts.Donut(segments, "open workspaces");
	}

	private static async Task<IResult> LiveFragmentAsync()
	{
		var live = (await RowsAsync(false, check: false)).Where(row => Live(row.State.Record.Run)).ToList();
		return Fragment(LiveCards(live), live.Count > 0);
	}

	private static string LiveCards(List<Row> live)
	{
		if (live.Count == 0)
		{
			return Notice("No agentic run is working right now.");
		}

		var body = new StringBuilder("<div class=\"livecards\">");
		foreach (var row in live)
		{
			var run = row.State.Record.Run!;
			body.Append($"<a class=\"livecard\" href=\"/runs/{Url(run.RunId)}\"><div class=\"head\"><span><span class=\"pulse\"></span> {E(run.RunId)}</span>");
			body.Append($"<span class=\"clock\" data-since=\"{run.Started.ToUniversalTime():O}\"></span></div>");
			body.Append(Charts.PhaseSteps("running", true));
			body.Append($"<div class=\"comps\">workspace <b>{E(row.State.Name)}</b> on seq {row.State.Record.BaseSeq} · {E(run.Agent)}{(run.Model is null ? "" : " · " + E(run.Model))}</div>");
			body.Append($"<div class=\"tail\">{E(Clip(run.Prompt ?? run.Command, 110))}</div></a>");
		}

		return body.Append("</div>").ToString();
	}

	private static string WorkspaceTable(List<Row> rows)
	{
		if (rows.Count == 0)
		{
			return Notice("No workspace. Create one with 'bassia workspace create <name>' or start an agent with 'bassia agent start -prompt <task>'.");
		}

		var names = rows.ToDictionary(row => row.State.Id, row => row.State.Name);
		var body = new StringBuilder("<table class=\"list\"><tr><th>Workspace</th><th>Lands?</th><th>Base</th><th>On</th><th>Run</th><th>Intent</th><th>Owner</th></tr>");
		foreach (var row in rows)
		{
			var record = row.State.Record;
			body.Append("<tr>");
			body.Append($"<td>{WorkspaceLink(record.Id, record.Name)}</td><td>{Landability(row)}</td><td>{SeqLink(record.BaseSeq)}</td>");
			body.Append($"<td>{(record.Parent is { } parent ? WorkspaceLink(parent, names.GetValueOrDefault(parent, parent)) : "<span class=\"muted\">mainline</span>")}</td>");
			body.Append($"<td>{(record.Run is { } run ? $"{RunLink(run.RunId)} {RunStatus(run)}" : "<span class=\"muted\">-</span>")}</td>");
			body.Append($"<td>{E(Clip(row.Working.Intent, 70))}</td><td>{E(record.Owner)}</td></tr>");
		}

		return body.Append("</table>").ToString();
	}

	private static string RunStatus(RunRecord run) => Live(run) ? Status("running") : run.Status == Core.Model.RunStatus.Running ? Status("lost") : Status(run.Status);

	// ----- workspaces -----

	private static async Task<IResult> WorkspacesAsync(HttpRequest request)
	{
		var all = request.Query.ContainsKey("all");
		var rows = await RowsAsync(all);
		var body = new StringBuilder();
		body.Append($"<div class=\"filters\"><a class=\"filter{(all ? "" : " active")}\" href=\"/workspaces\">open</a><a class=\"filter{(all ? " active" : "")}\" href=\"/workspaces?all\">all</a></div>");
		body.Append("<h2>Stacks</h2>").Append(StackFlow(rows.Where(row => row.State.IsOpen).ToList()));
		body.Append("<h2>Every workspace</h2>").Append(WorkspaceTable(rows));
		body.Append("<p>").Append(Cmd("bassia workspace check <name>")).Append(' ').Append(Cmd("bassia workspace land <name>")).Append(' ')
			.Append(Cmd("bassia workspace create <name> -on <workspace>")).Append("</p>");
		return Html("/workspaces", "Workspaces", body.ToString());
	}

	/// <summary>Mainline on the left, each open workspace to the right of what it is based on, coloured by whether it can land.</summary>
	private static string StackFlow(List<Row> rows)
	{
		if (rows.Count == 0)
		{
			return Notice("No open workspace.");
		}

		var depth = new Dictionary<string, int>();
		int Depth(Row row)
		{
			if (depth.TryGetValue(row.State.Id, out var known))
			{
				return known;
			}

			var parent = rows.FirstOrDefault(other => other.State.Id == row.State.Record.Parent);
			return depth[row.State.Id] = parent is null ? 1 : Depth(parent) + 1;
		}

		var nodes = new List<FlowNode> { new("main", 0, "mainline", null, "/mainline", "baseline") };
		var edges = new List<(string, string)>();
		foreach (var row in rows)
		{
			var kind = row.Check?.Outcome switch
			{
				LandOutcome.WouldLand => "integrated",
				LandOutcome.NeedsReplay or LandOutcome.Conflict => "waiter",
				_ => Live(row.State.Record.Run) ? "run" : "result"
			};
			nodes.Add(new FlowNode(row.State.Id, Depth(row), row.State.Name, $"seq {row.State.Record.BaseSeq} · {row.Check?.Outcome ?? row.State.Record.Status}",
				$"/workspaces/{Url(row.State.Id)}", kind));
			edges.Add((row.State.Record.Parent is { } parent && rows.Any(other => other.State.Id == parent) ? parent : "main", row.State.Id));
		}

		var columns = Enumerable.Range(0, nodes.Max(node => node.Column) + 1).Select(column => column == 0 ? "mainline" : column == 1 ? "workspaces" : $"stacked {column - 1}").ToList();
		return Charts.Flow(nodes, edges, columns) + Legend(("k-integrated", "can land"), ("k-waiter", "blocked"), ("k-run", "agent working"), ("k-result", "other"));
	}

	private static async Task<IResult> WorkspaceAsync(string id)
	{
		var state = await Repo.ResolveWorkspaceAsync(id);
		var record = state.Record;
		var working = await Repo.Objects.ReadCommitAsync(record.Working);
		var baseCommit = await Repo.Objects.ReadCommitAsync(record.Base);
		var check = state.IsOpen ? await Current.Landing.LandAsync(new LandRequest(state.Id, DryRun: true)) : null;
		var records = await Repo.Diffs.DiffAsync(baseCommit.Tree, working.Tree);
		var body = new StringBuilder("<div class=\"split\"><section>");
		body.Append("<table class=\"list facts\">");
		body.Append($"<tr><td>status</td><td>{Status(record.Status)}</td></tr>");
		body.Append($"<tr><td>can land</td><td>{Landability(new Row(state, check, working))}</td></tr>");
		body.Append($"<tr><td>base</td><td>{(record.Parent is null ? SeqLink(record.BaseSeq) : $"workspace {E(record.Parent)} (on {SeqLink(record.BaseSeq)})")}</td></tr>");
		body.Append($"<tr><td>change</td><td class=\"id\">{E(working.ChangeId.Hex)}</td></tr>");
		body.Append($"<tr><td>working commit</td><td class=\"id\">{E(record.Working.Short)}</td></tr>");
		body.Append($"<tr><td>intent</td><td>{E(working.Intent)}</td></tr>");
		body.Append($"<tr><td>owner</td><td>{E(record.Owner)}</td></tr>");
		body.Append($"<tr><td>created</td><td>{Time(record.Created)}</td></tr>");
		body.Append($"<tr><td>folder</td><td class=\"id\">{E(record.Path ?? "-")} <span class=\"muted\">{E(record.Host ?? "")}</span></td></tr>");
		if (working.Recipe is { } recipe)
		{
			body.Append($"<tr><td>replay recipe</td><td>{E(recipe.Agent ?? "")} {E(recipe.Model ?? "")}: {E(Clip(recipe.Prompt ?? recipe.Command ?? "", 200))}</td></tr>");
		}

		if (record.Run is { } run)
		{
			body.Append($"<tr><td>run</td><td>{RunLink(run.RunId)} {RunStatus(run)}</td></tr>");
		}

		if (record.LastLand is { } land)
		{
			body.Append($"<tr><td>last landing</td><td>{Status(land.Outcome)} {Time(land.Time)} {E(land.Message)}</td></tr>");
		}

		body.Append("</table></section><section class=\"side\">");
		if (check is not null)
		{
			body.Append("<h2>Landing now</h2>").Append(check.Outcome == LandOutcome.WouldLand ? Notice(check.Message) : $"<div class=\"attn\">{E(check.Message)}</div>");
			if (check.Conflicts.Count > 0)
			{
				body.Append(ConflictTable(check.Conflicts));
			}

			body.Append("<p>").Append(Cmd($"bassia workspace land {record.Name}")).Append(' ').Append(Cmd($"bassia workspace sync {record.Name}")).Append(' ')
				.Append(Cmd($"bassia agent replay {record.Name} -land")).Append("</p>");
		}

		body.Append("</section></div>");
		var counts = records.GroupBy(record => record.Status).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => $"{E(group.Key)}: {group.Count()}");
		body.Append($"<h2>Changes against its base <span class=\"muted\">({(records.Count == 0 ? "none" : string.Join(", ", counts))})</span></h2>");
		if (records.Any(record => record.Status == Core.Version.ChangeStatus.Mode))
		{
			body.Append("<p class=\"muted\">mode: the same content with the executable bit changed (git's 100755 / 100644).</p>");
		}

		body.Append(DiffTable(records));
		body.Append("<h2>Operations</h2><table class=\"list\"><tr><th>#</th><th>Operation</th><th>Time</th><th>By</th><th>Description</th></tr>");
		await foreach (var entry in Repo.LogAsync(HeadName.Workspace(state.Id), 50))
		{
			body.Append($"<tr><td>{entry.Operation.Seq}</td><td class=\"id\">{E(entry.Id.Short)}</td><td>{Time(entry.Operation.Time)}</td><td>{E(entry.Operation.Agent ?? "")}</td><td>{E(entry.Operation.Description)}</td></tr>");
		}

		body.Append("</table><p>").Append(Cmd($"bassia workspace restore {record.Name} -op <seq>")).Append(' ').Append(Cmd($"bassia workspace undo {record.Name}")).Append("</p>");
		return Html("/workspaces", $"Workspace {record.Name}", body.ToString());
	}

	private static string ConflictTable(IReadOnlyList<PathConflict> conflicts)
	{
		var body = new StringBuilder("<table class=\"list\"><tr><th>Path</th><th>Why</th><th>Policy</th><th>Detail</th></tr>");
		foreach (var conflict in conflicts)
		{
			body.Append($"<tr class=\"attn-row\"><td class=\"id\">{E(conflict.Path)}</td><td>{E(conflict.Case)}</td><td><span class=\"pv\">{E(conflict.Policy)}</span></td><td>{E(conflict.Detail)}</td></tr>");
		}

		return body.Append("</table>").ToString();
	}

	private static string DiffTable(IReadOnlyList<DiffRecord> records)
	{
		if (records.Count == 0)
		{
			return Notice("No changes.");
		}

		var body = new StringBuilder("<table class=\"list\"><tr><th>Status</th><th>Path</th><th>Size or mode</th></tr>");
		foreach (var record in records)
		{
			var status = record.New?.Kind == EntryKind.Conflict ? "conflict" : record.Status;
			body.Append($"<tr><td>{Status(status)}</td><td class=\"id\">{(record.OldPath is null ? "" : E(record.OldPath) + " &rarr; ")}{E(record.Path)}</td>");
			body.Append(record.Status == Core.Version.ChangeStatus.Mode
				? $"<td>{E(Core.Version.Diffs.ModeText(record.Old))} &rarr; {E(Core.Version.Diffs.ModeText(record.New))}</td></tr>"
				: $"<td>{(record.Old is null ? "" : CliCommands.Results.Size(record.Old.Size) + " &rarr; ")}{(record.New is null ? "-" : CliCommands.Results.Size(record.New.Size))}</td></tr>");
		}

		return body.Append("</table>").ToString();
	}

	// ----- runs -----

	private static async Task<IResult> RunsAsync()
	{
		var rows = (await RowsAsync(true, check: false)).Where(row => row.State.Record.Run is not null).OrderByDescending(row => row.State.Record.Run!.Started).ToList();
		var live = rows.Where(row => Live(row.State.Record.Run)).ToList();
		var body = new StringBuilder();
		body.Append("<h2>Live</h2><div data-live-src=\"/fragment/live\">").Append(LiveCards(live)).Append("</div>");
		body.Append("<h2>Every run over time</h2>");
		var bars = rows.Take(40).Select(row =>
		{
			var run = row.State.Record.Run!;
			var status = Live(run) ? "started" : run.Status switch
			{
				Core.Model.RunStatus.Succeeded => "completed",
				Core.Model.RunStatus.Failed => "failed",
				Core.Model.RunStatus.Stopped => "cancelled",
				_ => "stale"
			};
			return new RunBar(run.RunId, row.State.Name, status, run.Started, Live(run) ? null : run.Finished ?? run.Started, Live(run), Clip(run.Prompt ?? run.Command, 80));
		}).ToList();
		body.Append(Charts.RunTimeline(bars, DateTimeOffset.UtcNow));
		body.Append(Charts.Donut(rows.GroupBy(row => Live(row.State.Record.Run) ? "running" : row.State.Record.Run!.Status)
			.Select(group => new Segment(group.Key, group.Count(), group.Key switch
			{
				"running" => "s-started",
				Core.Model.RunStatus.Succeeded => "s-completed",
				Core.Model.RunStatus.Failed => "s-failed",
				_ => "s-other"
			})).ToList(), "runs by status"));
		body.Append("<table class=\"list\"><tr><th>Run</th><th>Status</th><th>Workspace</th><th>Started</th><th>Landing</th><th>Task</th></tr>");
		foreach (var row in rows)
		{
			var run = row.State.Record.Run!;
			body.Append($"<tr><td>{RunLink(run.RunId)}</td><td>{RunStatus(run)}</td><td>{WorkspaceLink(row.State.Id, row.State.Name)}</td><td>{Time(run.Started)}</td>");
			body.Append($"<td>{(row.State.Record.LastLand is { } land ? Status(land.Outcome) : "<span class=\"muted\">-</span>")}</td><td>{E(Clip(run.Prompt ?? run.Command, 80))}</td></tr>");
		}

		body.Append("</table><p>").Append(Cmd("bassia agent start -detach -prompt \"<task>\"")).Append(' ').Append(Cmd("bassia agent list")).Append("</p>");
		return Html("/runs", "Runs", body.ToString());
	}

	private static async Task<WorkspaceState> FindRunAsync(string id)
	{
		foreach (var state in await Repo.ListWorkspacesAsync(true))
		{
			if (state.Record.Run?.RunId == id)
			{
				return state;
			}
		}

		throw new VersionException($"No run '{id}'.");
	}

	private static async Task<IResult> RunAsync(string id)
	{
		var state = await FindRunAsync(id);
		var run = state.Record.Run!;
		var body = new StringBuilder();
		body.Append(Charts.PhaseSteps(Live(run) ? "running" : run.Status == Core.Model.RunStatus.Succeeded ? "completed" : "failed", Live(run)));
		body.Append("<table class=\"list facts\">");
		body.Append($"<tr><td>status</td><td>{RunStatus(run)}</td></tr><tr><td>workspace</td><td>{WorkspaceLink(state.Id, state.Name)} ({Status(state.Record.Status)})</td></tr>");
		body.Append($"<tr><td>agent</td><td>{E(run.Agent)} {E(run.Model ?? "")}</td></tr><tr><td>task</td><td>{E(run.Prompt ?? run.Command)}</td></tr>");
		body.Append($"<tr><td>started</td><td>{Time(run.Started)}</td></tr><tr><td>finished</td><td>{(run.Finished is { } finished ? Time(finished) : "-")}</td></tr>");
		body.Append($"<tr><td>exit code</td><td>{run.ExitCode?.ToString() ?? "-"}</td></tr><tr><td>host</td><td>{E(run.Host ?? "-")}</td></tr>");
		if (state.Record.LastLand is { } land)
		{
			body.Append($"<tr><td>landing</td><td>{Status(land.Outcome)} {E(land.Message)}</td></tr>");
		}

		body.Append("</table><h2>Output</h2>");
		body.Append($"<div data-live-src=\"/fragment/run/{Url(id)}\">{await RunOutputAsync(run)}</div>");
		body.Append("<p>").Append(Cmd($"bassia agent logs {run.RunId}")).Append(' ').Append(Cmd($"bassia agent wait {run.RunId}")).Append(' ')
			.Append(Cmd($"bassia agent stop {run.RunId}")).Append("</p>");
		return Html("/runs", $"Run {run.RunId}", body.ToString());
	}

	private static async Task<string> RunOutputAsync(RunRecord run)
	{
		var text = await Current.ReadLog(run);
		var lines = text.Split('\n');
		return $"<pre class=\"output\">{E(string.Join('\n', lines.TakeLast(400)))}</pre>";
	}

	private static async Task<IResult> RunOutputFragmentAsync(string id)
	{
		var run = (await FindRunAsync(id)).Record.Run!;
		return Fragment(await RunOutputAsync(run), Live(run));
	}

	// ----- mainline -----

	private static async Task<string> LandingTableAsync(int limit)
	{
		var body = new StringBuilder("<table class=\"list\"><tr><th>Seq</th><th>Time</th><th>Author</th><th>Workspace</th><th>Change</th><th>Intent</th></tr>");
		var state = await Repo.GetMainAsync();
		for (var count = 0; count < limit; count++)
		{
			var commit = state.Commit;
			var machine = commit.MachineMerged ? " <span class=\"pill\" style=\"background:var(--violet)\">machine-merged</span>" : "";
			body.Append($"<tr><td>{SeqLink(state.Seq)}</td><td>{Time(commit.Time)}</td><td>{E(commit.Author.ToString())}</td><td class=\"id\">{E(state.Operation.Workspace ?? "-")}</td>");
			body.Append($"<td class=\"id\">{E(commit.ChangeId.Short)}</td><td>{E(Clip(commit.Intent.Split('\n')[0], 90))}{machine}</td></tr>");
			if (state.Operation.Parents.Count == 0)
			{
				break;
			}

			state = await Repo.MainAtAsync(state.Operation.Parents[0]);
		}

		return body.Append("</table>").ToString();
	}

	private static async Task<IResult> MainlineAsync(HttpRequest request)
	{
		var limit = int.TryParse(request.Query["limit"], out var value) ? Math.Clamp(value, 1, 1000) : 50;
		var body = new StringBuilder("<p class=\"muted\">One trunk, linear: every landing is the next seq and keeps its workspace's change id.</p>");
		body.Append(await LandingTableAsync(limit));
		body.Append($"<div class=\"pager\"><a href=\"/mainline?limit={limit * 2}\">more</a></div>");
		body.Append("<p>").Append(Cmd("bassia mainline log")).Append(' ').Append(Cmd("bassia mainline show -seq <n> -patch")).Append(' ')
			.Append(Cmd("bassia mainline restore -seq <n>")).Append("</p>");
		return Html("/mainline", "Mainline", body.ToString());
	}

	private static async Task<IResult> LandingAsync(long seq, HttpRequest request)
	{
		var state = await Repo.MainAtSeqAsync(seq);
		MainState? parent = state.Operation.Parents.Count > 0 ? await Repo.MainAtAsync(state.Operation.Parents[0]) : null;
		var records = await Repo.Diffs.DiffAsync(parent?.Tree, state.Tree);
		var commit = state.Commit;
		var body = new StringBuilder("<table class=\"list facts\">");
		body.Append($"<tr><td>commit</td><td class=\"id\">{E(state.CommitId.Hex)}</td></tr><tr><td>change</td><td class=\"id\">{E(commit.ChangeId.Hex)}</td></tr>");
		body.Append($"<tr><td>author</td><td>{E(commit.Author.ToString())}</td></tr><tr><td>time</td><td>{Time(commit.Time)}</td></tr>");
		body.Append($"<tr><td>workspace</td><td class=\"id\">{E(state.Operation.Workspace ?? "-")}</td></tr>");
		body.Append($"<tr><td>intent</td><td><pre class=\"brief\">{E(commit.Intent)}</pre></td></tr>");
		if (commit.Recipe is { } recipe)
		{
			body.Append($"<tr><td>recipe</td><td>{E(recipe.Agent ?? "")} {E(recipe.Model ?? "")}: {E(recipe.Prompt ?? recipe.Command ?? "")}</td></tr>");
		}

		body.Append("</table><h2>Changes</h2>").Append(DiffTable(records));
		if (request.Query.ContainsKey("patch"))
		{
			body.Append("<h2>Review</h2>");
			foreach (var record in records.Take(100))
			{
				var patch = await Repo.Diffs.PatchAsync(record);
				body.Append($"<h3 class=\"id\">{E(record.Path)}</h3>");
				body.Append(patch.Hunks is { } hunks ? $"<pre>{E(hunks)}</pre>" : $"<div class=\"attn\">{E(patch.Summary ?? "")}</div>");
			}
		}
		else
		{
			body.Append($"<p><a href=\"/mainline/{seq}?patch\">review the hunks</a> (rewritten files show as a summary)</p>");
		}

		var nav = $"<div class=\"pager\">{(seq > 0 ? $"<a href=\"/mainline/{seq - 1}\">&larr; seq {seq - 1}</a>" : "")}<a href=\"/mainline/{seq + 1}\">seq {seq + 1} &rarr;</a></div>";
		return Html("/mainline", $"Mainline seq {seq}: {Clip(commit.Intent.Split('\n')[0], 60)}", nav + body);
	}

	// ----- conflicts -----

	private static async Task<IResult> ConflictsAsync()
	{
		var rows = await RowsAsync(true);
		var blocked = rows.Where(row => row.Check?.Outcome is LandOutcome.NeedsReplay or LandOutcome.Conflict or LandOutcome.Rejected).ToList();
		var body = new StringBuilder();
		body.Append("<p class=\"muted\">When two agents rewrote the same path, the default is to re-run the later task on the new mainline (replay), not to merge text " +
			"nobody reviewed. Policies per path are set in .bassia.toml.</p>");
		body.Append($"<h2>Blocked from landing <span class=\"count\">{blocked.Count}</span></h2>");
		if (blocked.Count == 0)
		{
			body.Append(Notice("Every open workspace can land (or has nothing to land)."));
		}

		foreach (var row in blocked)
		{
			body.Append($"<h3>{WorkspaceLink(row.State.Id, row.State.Name)} {Landability(row)}</h3><div class=\"attn\">{E(row.Check!.Message)}</div>");
			if (row.Check.Conflicts.Count > 0)
			{
				body.Append(ConflictTable(row.Check.Conflicts));
			}

			body.Append("<p>").Append(row.Check.Outcome switch
			{
				LandOutcome.NeedsReplay => Cmd($"bassia agent replay {row.State.Name} -land") + " " + Cmd($"bassia workspace sync {row.State.Name}"),
				LandOutcome.Conflict => Cmd($"bassia workspace status {row.State.Name}") + " " + Cmd($"bassia workspace land {row.State.Name}"),
				_ => Cmd($"bassia workspace check {row.State.Name}")
			}).Append("</p>");
		}

		// Paths that keep colliding: candidates for an exclusive lease or for splitting the task.
		var hot = rows.SelectMany(row => (row.Check?.Conflicts.Select(conflict => conflict.Path) ?? []).Concat(row.State.Record.LastLand?.Conflicts ?? []))
			.GroupBy(path => path).Select(group => (Path: group.Key, Count: group.Count())).OrderByDescending(item => item.Count).Take(20).ToList();
		body.Append("<h2>Hot paths</h2>");
		if (hot.Count == 0)
		{
			body.Append(Notice("No path has caused a conflict."));
		}
		else
		{
			var max = hot.Max(item => item.Count);
			body.Append("<table class=\"list\"><tr><th>Path</th><th>Conflicts</th><th></th></tr>");
			foreach (var (path, count) in hot)
			{
				body.Append($"<tr><td class=\"id\">{E(path)}</td><td>{count}</td><td class=\"stackcell\"><div class=\"stack\"><span class=\"st-manual\" style=\"width:{100 * count / max}%\"></span></div></td></tr>");
			}

			body.Append("</table><p>A path that keeps causing replays is a candidate for an exclusive lease: ")
				.Append(Cmd("bassia lease acquire -prefix <folder> -wait 600")).Append("</p>");
		}

		return Html("/conflicts", "Conflicts", body.ToString());
	}

	// ----- leases, files, config -----

	private static async Task<IResult> LeasesAsync()
	{
		var leases = await Repo.Leases.ListAsync();
		var names = (await Repo.ListWorkspacesAsync(true)).ToDictionary(state => state.Id, state => state.Name);
		var body = new StringBuilder();
		if (leases.Count == 0)
		{
			body.Append(Notice("No live lease."));
		}
		else
		{
			body.Append("<table class=\"list\"><tr><th>Prefix</th><th>Mode</th><th>Workspace</th><th>Run</th><th>Token</th><th>Expires</th></tr>");
			foreach (var lease in leases)
			{
				body.Append($"<tr><td class=\"id\">{E(lease.Prefix.Length == 0 ? "/" : lease.Prefix)}</td><td>{E(lease.Mode.ToString().ToLowerInvariant())}</td>");
				body.Append($"<td>{WorkspaceLink(lease.Owner, names.GetValueOrDefault(lease.Owner, lease.Owner))}</td><td class=\"id\">{E(lease.Run ?? "-")}</td>");
				body.Append($"<td>{lease.Token}</td><td>{Time(lease.Expires)}</td></tr>");
			}

			body.Append("</table>");
		}

		body.Append("<p>").Append(Cmd("bassia lease acquire -prefix <folder> -workspace <name>")).Append(' ').Append(Cmd("bassia lease list")).Append("</p>");
		return Html("/leases", "Leases", body.ToString());
	}

	private static async Task<IResult> FilesAsync(HttpRequest request)
	{
		var main = await Repo.GetMainAsync();
		var seq = long.TryParse(request.Query["seq"], out var given) ? given : main.Seq;
		var state = seq == main.Seq ? main : await Repo.MainAtSeqAsync(seq);
		var path = PathNames.Normalize(request.Query["path"].ToString());
		var leaf = await Repo.Trees.GetAsync(state.Tree, path) ?? throw new VersionException($"'{path}' does not exist at seq {seq}.");
		var body = new StringBuilder($"<p class=\"muted\">seq {seq} · listed from tree metadata; file content is fetched only when you open a file.</p><p class=\"id\">");
		body.Append($"<a href=\"/files?seq={seq}\">/</a>");
		var prefix = "";
		foreach (var part in PathNames.Split(path))
		{
			prefix = PathNames.Join(prefix, part);
			body.Append($" / <a href=\"/files?seq={seq}&amp;path={Url(prefix)}\">{E(part)}</a>");
		}

		body.Append("</p>");
		if (leaf.Kind == EntryKind.Tree)
		{
			body.Append("<table class=\"list\"><tr><th>Name</th><th>Kind</th><th>Size</th></tr>");
			foreach (var entry in (await Repo.Objects.ReadTreeAsync(leaf.Id)).Entries)
			{
				var child = PathNames.Join(path, entry.Name);
				body.Append($"<tr><td class=\"id\"><a href=\"/files?seq={seq}&amp;path={Url(child)}\">{E(entry.Name)}{(entry.Kind == EntryKind.Tree ? "/" : "")}</a></td>");
				body.Append($"<td>{E(entry.Kind.ToString().ToLowerInvariant())}</td><td>{CliCommands.Results.Size(entry.Size)}</td></tr>");
			}

			body.Append("</table>");
		}
		else
		{
			var bytes = leaf.Size <= 2 * 1024 * 1024 ? await Repo.Objects.ReadContentAsync(leaf) : null;
			body.Append(bytes is not null && Core.Text.LineDiff.IsText(bytes)
				? $"<pre>{E(Encoding.UTF8.GetString(bytes))}</pre>"
				: Notice($"{CliCommands.Results.Size(leaf.Size)} of {(bytes is null ? "content too large to show" : "binary content")}."));
		}

		return Html("/files", "Files", body.ToString());
	}

	private static async Task<IResult> ConfigAsync()
	{
		var main = await Repo.GetMainAsync();
		var policy = await RepoPolicy.LoadAsync(Repo.Trees, main.Tree);
		var body = new StringBuilder($"<h2>Repository policy <span class=\"muted\">({E(RepoPolicy.FileName)} at seq {main.Seq})</span></h2>");
		body.Append("<table class=\"list\"><tr><th>Paths</th><th>Conflict policy</th></tr>");
		foreach (var (glob, kind) in policy.ConflictRules)
		{
			body.Append($"<tr><td class=\"id\">{E(glob.Pattern)}</td><td><span class=\"pv\">{E(kind)}</span></td></tr>");
		}

		body.Append($"<tr><td class=\"muted\">everything else</td><td><span class=\"pv own\">{E(policy.DefaultConflictPolicy)}</span></td></tr></table>");
		body.Append("<h3>Lease required</h3>").Append(policy.LeaseRequired.Count == 0
			? "<p class=\"muted\">No prefix requires a lease: leases are advisory everywhere.</p>"
			: "<p>" + string.Concat(policy.LeaseRequired.Select(prefix => $"<span class=\"chip id\">{E(prefix)}</span>")) + "</p>");
		body.Append("<h3>Never snapshotted</h3><p>").Append(string.Concat(policy.Ignore.Select(glob => $"<span class=\"chip id\">{E(glob.Pattern)}</span>"))).Append("</p>");
		if (Current.Settings is { } settings)
		{
			body.Append("<h2>Settings of this monorepo folder</h2><table class=\"list\"><tr><th>Key</th><th>Value</th><th>Source</th></tr>");
			foreach (var (key, value, source) in settings)
			{
				body.Append($"<tr><td class=\"id\">{E(key)}</td><td class=\"id\">{E(value)}</td><td><span class=\"layer wins {(source == "default" ? "default" : "monorepo")}\">{E(source)}</span></td></tr>");
			}

			body.Append("</table>");
		}

		body.Append("<p>").Append(Cmd("bassia config list")).Append("</p>");
		return Html("/config", "Config", body.ToString());
	}

	private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

	private static string Legend(params (string Class, string Label)[] items) =>
		"<div class=\"legend\">" + string.Concat(items.Select(item => $"<span><i class=\"{item.Class}\" style=\"border-radius:3px\"></i>{E(item.Label)}</span>")) + "</div>";

	private const string ExtraStyles = """

		.status.open, .status.would-land, .status.succeeded, .status.landed { color:var(--ok); }
		.status.needs-replay, .status.lost, .status.replaced, .status.stopped { color:var(--warn); }
		.status.conflict, .status.rejected { color:var(--bad); } .status.closed, .status.abandoned, .status.nothing { color:var(--muted); }
		.status.added { color:var(--ok); } .status.deleted { color:var(--bad); } .status.modified, .status.renamed { color:var(--live); }
		.status.mode { color:var(--violet); }
		i.k-integrated { background:#eaf6ee; border:2px solid var(--ok); } i.k-waiter { background:#fff6e6; border:2px dashed var(--warn); }
		i.k-run { background:#e9f3ff; border:2px solid var(--live); } i.k-result { background:#fff; border:2px dashed var(--live); }
		""";
}
