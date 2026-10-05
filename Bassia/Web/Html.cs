namespace Bassia.Web;

using System.Globalization;
using System.Net;
using Bassia.CliCommands.Agent;
using Bassia.Integration;

/// <summary>
/// The dashboard's HTML: the page frame (header and menu, as in Fossil's web UI), the stylesheet, the progressive
/// script, and the small helpers every page uses. Every value that reaches markup goes through <see cref="E"/>.
/// </summary>
internal static class Html
{
	/// <summary>The menu, in order: path and label.</summary>
	public static readonly (string Path, string Label)[] Menu =
	[
		("/", "Overview"),
		("/components", "Components"),
		("/tags", "Tags"),
		("/runs", "Runs"),
		("/queue", "Merge queue"),
		("/integrations", "Integrations"),
		("/timeline", "Timeline"),
		("/config", "Config")
	];

	public static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

	public static string Url(string text) => Uri.EscapeDataString(text);

	public static string Page(string root, string active, string title, string body) => $$"""
		<!DOCTYPE html>
		<html lang="en">
		<head>
		<meta charset="utf-8">
		<meta name="viewport" content="width=device-width, initial-scale=1">
		<title>{{E(title)}} · Bassia</title>
		<link rel="stylesheet" href="/style.css">
		<script src="/app.js" defer></script>
		</head>
		<body>
		<header><span class="logo">Bassia</span> <span class="root">{{E(root)}}</span><span class="ro">read-only · change things with bassia or bassia prompt</span></header>
		<nav>{{string.Concat(Menu.Select(item => $"<a href=\"{item.Path}\"{(item.Path == active ? " class=\"active\"" : "")}>{E(item.Label)}</a>"))}}</nav>
		<main>
		<h1>{{E(title)}}</h1>
		{{body}}
		</main>
		</body>
		</html>
		""";

	public static string Short(string? commit) => commit is null ? "-" : commit[..Math.Min(10, commit.Length)];

	public static string Time(string? timestamp) =>
		timestamp is not null && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
			? Time(parsed)
			: E(timestamp ?? "-");

	public static string Time(DateTimeOffset time) =>
		$"<time datetime=\"{time.ToUniversalTime():O}\">{time.ToUniversalTime():yyyy-MM-dd HH:mm}</time>";

	public static string Status(string status) => $"<span class=\"status {E(status)}\">{E(status)}</span>";

	public static string RunLink(string runId) => $"<a class=\"id\" href=\"/runs/{Url(runId)}\">{E(RunMetadata.Key(runId))}</a>";

	public static string IntegrationLink(string id) => $"<a class=\"id\" href=\"/integrations/{Url(id)}\">{E(IntegrationRecord.Key(id))}</a>";

	/// <summary>
	/// A ref as a chip. Agent and integration refs link to the run or integration that made them, which is what ties a
	/// timeline back to the agentic work behind it.
	/// </summary>
	public static string Ref(string name)
	{
		var target = RefTarget(name);
		var kind = name.StartsWith(RunMetadata.RefPrefix, StringComparison.Ordinal) ? "agent"
			: name.StartsWith(IntegrationRecord.RefPrefix, StringComparison.Ordinal) ? "integration"
			: "plain";
		return target is null
			? $"<span class=\"ref {kind}\">{E(name)}</span>"
			: $"<a class=\"ref {kind}\" href=\"{target}\">{E(name)}</a>";
	}

	private static string? RefTarget(string name)
	{
		foreach (var (prefix, idPrefix, path) in new[]
		{
			(RunMetadata.RefPrefix, RunMetadata.RunIdPrefix, "/runs/"),
			(IntegrationRecord.RefPrefix, IntegrationRecord.IdPrefix, "/integrations/")
		})
		{
			if (name.StartsWith(prefix, StringComparison.Ordinal))
			{
				var key = name[prefix.Length..].Split('/')[0];
				return RecordName.IsKey(key) ? path + Url(idPrefix + key) : null;
			}
		}

		return null;
	}

	public static string Rationale(string command) => E(AgentCommand.SummarizeCommand(command));

	/// <summary>
	/// A command to run in a terminal (or to ask <c>bassia prompt</c> for): the dashboard shows what to do instead of
	/// doing it. Clicking it copies it.
	/// </summary>
	public static string Cmd(string command) => $"<code class=\"cmd\" title=\"click to copy\">{E(command)}</code>";

	public static string Error(string message) => $"<div class=\"error\">{E(message)}</div>";

	public static string Notice(string message) => $"<div class=\"notice\">{E(message)}</div>";

	public const string Stylesheet = """
		:root { --fg:#1c2330; --muted:#6b7385; --line:#d5dae3; --bg:#f6f7fa; --panel:#fff; --head:#22324f; --accent:#2f6fd0; --soft:#eef3fb;
		        --ok:#1f8a4c; --warn:#c27c0e; --bad:#c0392b; --live:#1677d2; --violet:#7a4cc2; --teal:#0f8b8d; --grey:#9aa3b2;
		        --git:#1f8a4c; --weave:#1677d2; --resolver:#7a4cc2; --human:#c0392b; }
		* { box-sizing: border-box; }
		body { margin:0; font:14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; color:var(--fg); background:var(--bg); }
		header { background:var(--head); color:#fff; padding:10px 18px; display:flex; align-items:baseline; gap:12px; }
		header .logo { font-weight:700; font-size:18px; letter-spacing:.5px; }
		header .root { opacity:.75; font-family:ui-monospace, monospace; font-size:12px; }
		header .ro { margin-left:auto; font-size:12px; opacity:.75; }
		nav { background:#e8ebf2; border-bottom:1px solid var(--line); padding:0 12px; display:flex; flex-wrap:wrap; }
		nav a { padding:8px 12px; color:var(--fg); text-decoration:none; }
		nav a:hover { background:#dbe1eb; }
		nav a.active { background:var(--bg); font-weight:600; border:1px solid var(--line); border-bottom-color:var(--bg); margin-bottom:-1px; }
		main { padding:10px 18px 40px; max-width:1400px; }
		h1 { font-size:20px; margin:12px 0; } h2 { font-size:16px; margin:24px 0 8px; } h3 { font-size:14px; margin:14px 0 6px; }
		a { color:var(--accent); }
		section { min-width:0; }
		.split { display:flex; flex-wrap:wrap; gap:18px; align-items:flex-start; } .split > section { flex:1 1 260px; } .split > section.wide { flex:3 1 520px; }
		table.list { border-collapse:collapse; width:100%; margin:6px 0 14px; background:var(--panel); }
		table.list th { text-align:left; font-weight:600; background:#eef1f6; border-bottom:1px solid var(--line); padding:5px 8px; }
		table.list td { border-bottom:1px solid #eceff4; padding:5px 8px; vertical-align:top; }
		table.list tr:hover td { background:#f5f7fb; }
		table.facts td:first-child { color:var(--muted); padding-right:16px; white-space:nowrap; }
		td.stackcell { width:55%; }
		.id, code, pre, .hash { font-family:ui-monospace, "Cascadia Mono", monospace; font-size:12.5px; }
		code.cmd { background:#11161f; color:#d9e1ee; padding:2px 7px; border-radius:4px; cursor:copy; white-space:pre-wrap; overflow-wrap:anywhere; display:inline-block; margin:1px 0; max-width:100%; }
		code.cmd::before { content:"$ "; color:#7d8799; }
		code.cmd.copied { background:var(--ok); }
		pre { background:#f3f4f7; border:1px solid var(--line); padding:10px; overflow:auto; white-space:pre-wrap; }
		pre.output { background:#11161f; color:#d9e1ee; max-height:420px; }
		pre.brief { background:var(--panel); max-height:none; }
		.muted { color:var(--muted); } .bad { color:var(--bad); }
		.status { font-weight:600; } .status.completed { color:var(--ok); } .status.partial, .status.cancelled, .status.stale { color:var(--warn); }
		.status.failed { color:var(--bad); } .status.started, .status.live, .status.running, .status.preparing { color:var(--live); }
		.status.needs_attention { color:var(--human); }
		.ref { display:inline-block; font:11.5px ui-monospace, monospace; padding:0 5px; margin:0 3px 2px 0; border-radius:3px;
		       background:#eef1f6; border:1px solid var(--line); color:var(--fg); text-decoration:none; }
		.ref.agent { background:#e9f3ff; border-color:#b9d4f5; } .ref.integration { background:#f3ecff; border-color:#d6c3f7; }
		.chip { display:inline-block; padding:0 6px; border-radius:3px; background:var(--soft); margin:0 4px 2px 0; text-decoration:none; color:var(--fg); }
		.cards { display:flex; flex-wrap:wrap; gap:10px; margin:8px 0 16px; }
		.card { background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:10px 14px; min-width:140px; color:var(--fg); text-decoration:none; }
		.card:hover { border-color:var(--accent); }
		.card .n { font-size:24px; font-weight:700; }
		.card.live .n { color:var(--live); } .card.alert { border-color:var(--bad); } .card.alert .n { color:var(--bad); }
		.error { background:#fdecea; border:1px solid #f3b8b2; color:var(--bad); padding:8px 12px; border-radius:4px; margin:10px 0; }
		.notice { background:#eef6ee; border:1px solid #bfdcbf; color:var(--ok); padding:8px 12px; border-radius:4px; margin:10px 0; }
		button { font:inherit; padding:4px 12px; border:1px solid var(--head); background:var(--head); color:#fff; border-radius:4px; cursor:pointer; }
		input[type=number] { font:inherit; padding:3px 6px; border:1px solid var(--line); border-radius:4px; width:80px; }
		.pick { display:grid; grid-template-columns:repeat(auto-fill, minmax(200px, 1fr)); gap:6px 16px; margin:6px 0 12px; }
		.pager { margin:10px 0; } .pager a { margin-right:14px; }
		.filters { display:flex; flex-wrap:wrap; gap:6px; margin:6px 0 10px; }
		.filters a.filter { padding:2px 10px; border:1px solid var(--line); border-radius:12px; text-decoration:none; color:var(--fg); background:var(--panel); }
		.filters a.filter.active { background:var(--head); color:#fff; border-color:var(--head); }

		/* charts */
		.chart-box { background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:8px; overflow:auto; margin:6px 0 10px; }
		svg.chart { max-width:100%; height:auto; font-family:system-ui, sans-serif; font-size:12px; display:block; }
		svg .edge { fill:none; stroke:#8792a6; stroke-width:1.6; transition:stroke .15s, opacity .15s; }
		svg .arrowhead { fill:#8792a6; }
		svg.map .node rect { fill:#fff; stroke:#3b5b8f; stroke-width:1.5; transition:fill .15s, opacity .15s; }
		svg.map .node:hover rect, svg.map .node.focus rect { fill:#e3edfc; stroke-width:2.5; }
		svg.map .node.attention rect { stroke:var(--bad); }
		svg.map .node.busy rect { stroke:var(--live); stroke-dasharray:6 4; animation:march 1s linear infinite; }
		svg.map .node .name { font-weight:600; font-size:14px; fill:var(--fg); }
		svg.map .node .sub { font-size:11px; fill:var(--muted); }
		svg.map.hovering .node:not(.hl) { opacity:.25; } svg.map.hovering .edge:not(.hl) { opacity:.12; }
		svg.map .edge.hl { stroke:var(--accent); stroke-width:2.4; }
		.badge circle { stroke:#fff; stroke-width:1.5; } .badge text { fill:#fff; font-size:10px; font-weight:700; text-anchor:middle; }
		.b-live circle, i.b-live { fill:var(--live); background:var(--live); } .b-live circle { animation:pulse 1.4s ease-in-out infinite; transform-box:fill-box; transform-origin:center; }
		.b-queued circle, i.b-queued { fill:var(--warn); background:var(--warn); }
		.b-attention circle, i.b-attention { fill:var(--bad); background:var(--bad); }
		i.arrowhead { background:#8792a6; }
		@keyframes march { to { stroke-dashoffset:-20; } }
		@keyframes pulse { 0%,100% { transform:scale(1); opacity:1; } 50% { transform:scale(1.25); opacity:.7; } }
		@keyframes stripes { to { background-position:28px 0; } }
		@keyframes slide { 0% { left:-35%; } 100% { left:100%; } }
		@keyframes glow { 0%,100% { box-shadow:0 0 0 0 rgba(22,119,210,.45); } 50% { box-shadow:0 0 0 6px rgba(22,119,210,0); } }

		.legend { display:flex; flex-wrap:wrap; gap:12px; font-size:12px; color:var(--muted); margin:4px 0 8px; }
		.legend i { display:inline-block; width:11px; height:11px; border-radius:50%; margin-right:5px; vertical-align:-1px; }

		svg.flow .fnode rect { fill:#fff; stroke:#8792a6; stroke-width:1.4; }
		svg.flow .fnode .name { font-weight:600; fill:var(--fg); } svg.flow .fnode .sub { font-size:10.5px; fill:var(--muted); }
		svg.flow .col-title { text-anchor:middle; fill:var(--muted); font-size:11px; text-transform:uppercase; letter-spacing:.6px; }
		svg.flow .k-baseline rect { stroke:var(--teal); fill:#e8f6f6; } svg.flow .k-run rect { stroke:var(--live); fill:#e9f3ff; }
		svg.flow .k-result rect { stroke:var(--live); fill:#fff; stroke-dasharray:4 3; } svg.flow .k-integration rect { stroke:var(--violet); fill:#f3ecff; }
		svg.flow .k-integrated rect { stroke:var(--ok); fill:#eaf6ee; }
		svg.flow .current rect { stroke-width:3; filter:drop-shadow(0 0 3px rgba(47,111,208,.5)); }
		svg.flow a:hover rect { fill:#dce8fb; }

		svg.gantt .grid { stroke:#e3e7ee; } svg.gantt .tick { fill:var(--muted); font-size:10.5px; text-anchor:middle; }
		svg.gantt .rowbg { fill:transparent; } svg.gantt .row:hover .rowbg { fill:#f0f4fa; }
		svg.gantt .label { fill:var(--fg); font-family:ui-monospace, monospace; font-size:11.5px; }
		svg.gantt .now { stroke:var(--bad); stroke-dasharray:3 3; }
		svg .s-completed { fill:var(--ok); } svg .s-failed { fill:var(--bad); } svg .s-partial, svg .s-cancelled, svg .s-stale { fill:var(--warn); }
		svg .s-abandoned, svg .s-other { fill:var(--grey); } svg .s-started, svg .s-preparing { fill:var(--live); }
		svg .bar.live { fill:var(--live); animation:blink 1.2s ease-in-out infinite; }
		svg .dot.live { animation:pulse 1.4s ease-in-out infinite; transform-box:fill-box; transform-origin:center; }
		@keyframes blink { 0%,100% { opacity:1; } 50% { opacity:.55; } }

		.donut-box { display:flex; gap:14px; align-items:center; margin:6px 0; }
		svg.donut .track { fill:none; stroke:#eceff4; } svg.donut .arc { fill:none; transition:stroke-width .15s; } svg.donut .arc:hover { stroke-width:22; }
		svg.donut .total { text-anchor:middle; font-size:22px; font-weight:700; fill:var(--fg); }
		svg.donut .s-completed { stroke:var(--ok); } svg.donut .s-failed { stroke:var(--bad); } svg.donut .s-partial, svg.donut .s-cancelled { stroke:var(--warn); }
		svg.donut .s-abandoned, svg.donut .s-other { stroke:var(--grey); } svg.donut .s-started { stroke:var(--live); }
		i.s-completed { background:var(--ok); } i.s-failed { background:var(--bad); } i.s-partial, i.s-cancelled { background:var(--warn); }
		i.s-abandoned, i.s-other { background:var(--grey); } i.s-started { background:var(--live); }

		.stack { display:flex; height:22px; border-radius:5px; overflow:hidden; background:#eceff4; font-size:11px; }
		.stack span { display:flex; align-items:center; justify-content:center; color:#fff; white-space:nowrap; overflow:hidden; padding:0 4px; min-width:18px; }
		.stack.empty span { color:var(--muted); }
		.st-syntactic { background:var(--git); } .st-structural { background:var(--weave); } .st-semantic { background:var(--resolver); }
		.st-manual { background:var(--human); } .st-failed { background:#7b1e14; } .st-uptodate { background:var(--grey); } .st-skip { background:#c9ced8; color:var(--fg) !important; }
		.pill { display:inline-block; padding:0 8px; border-radius:10px; color:#fff; font-size:11.5px; font-weight:600; }
		svg.lanes .lane { stroke:#c9ced8; stroke-width:3; } svg.lanes .track { stroke:#e6e9ef; stroke-width:3; stroke-dasharray:2 6; } svg.lanes .lane-name { font-weight:600; fill:var(--fg); }
		svg.lanes .base { fill:var(--head); } svg.lanes .base-label { fill:#fff; font-size:11px; text-anchor:middle; }
		svg.lanes .qitem circle { stroke:#fff; stroke-width:2; } svg.lanes .qlabel { text-anchor:middle; font-size:10.5px; fill:var(--muted); font-family:ui-monospace, monospace; }
		svg.lanes .st-syntactic circle { fill:var(--git); } svg.lanes .st-structural circle { fill:var(--weave); } svg.lanes .st-semantic circle { fill:var(--resolver); }
		svg.lanes .st-manual circle { fill:var(--human); animation:pulse 1.6s ease-in-out infinite; transform-box:fill-box; transform-origin:center; }
		svg.lanes .st-uptodate circle, svg.lanes .st-skip circle { fill:var(--grey); } svg.lanes .st-failed circle { fill:#7b1e14; }
		svg.lanes .qitem { background:none; }
		svg.ring .clash { stroke:var(--bad); stroke-width:2.5; stroke-dasharray:6 4; animation:march 1.2s linear infinite; }
		svg.ring .rnode circle { fill:var(--live); stroke:#fff; stroke-width:2; } svg.ring .rnode.clashing circle { fill:var(--bad); }
		svg.ring .rnode text { font-size:11px; fill:var(--fg); font-family:ui-monospace, monospace; }

		svg.tags .stripe { fill:#fff; } svg.tags .stripe.odd { fill:#f6f8fb; }
		svg.tags .rowname { font-weight:600; fill:var(--fg); } svg.tags .colname { font-size:11px; fill:var(--muted); font-family:ui-monospace, monospace; }
		svg.tags .hit { fill:transparent; } svg.tags .tagcol:hover .hit { fill:rgba(47,111,208,.08); } svg.tags .tagcol:hover .colname { fill:var(--accent); }
		svg.tags .span { stroke-width:4; stroke-linecap:round; opacity:.5; } svg.tags .mark { stroke:#fff; stroke-width:1.5; }
		.k-baseline .mark, .k-baseline .span { fill:var(--teal); stroke:var(--teal); } .k-run .mark, .k-run .span { fill:var(--live); stroke:var(--live); }
		.k-integration .mark, .k-integration .span { fill:var(--violet); stroke:var(--violet); } .k-unwind .mark, .k-unwind .span { fill:var(--warn); stroke:var(--warn); }
		.k-split .mark, .k-split .span { fill:var(--grey); stroke:var(--grey); }
		svg.tags .mark { stroke:#fff; }
		i.k-baseline { background:var(--teal); } i.k-run { background:var(--live); } i.k-integration { background:var(--violet); } i.k-unwind { background:var(--warn); } i.k-split { background:var(--grey); }
		.tagchip { display:inline-block; font:12px ui-monospace, monospace; padding:1px 7px; border-radius:10px; color:#fff; text-decoration:none; background:var(--grey); }
		.tagchip.k-baseline { background:var(--teal); } .tagchip.k-run { background:var(--live); } .tagchip.k-integration { background:var(--violet); } .tagchip.k-unwind { background:var(--warn); }
		ul.taglist { list-style:none; padding:0; margin:0; } ul.taglist li { margin:4px 0; }

		/* live runs */
		.pulse { display:inline-block; width:9px; height:9px; border-radius:50%; background:var(--live); animation:glow 1.4s infinite; vertical-align:1px; }
		.livecards { display:grid; grid-template-columns:repeat(auto-fill, minmax(320px, 1fr)); gap:12px; margin:6px 0 14px; }
		.livecard { display:block; background:var(--panel); border:1px solid #b9d4f5; border-radius:10px; padding:10px 12px; color:var(--fg); text-decoration:none; animation:glow 2.4s infinite; }
		.livecard .head { display:flex; justify-content:space-between; font-family:ui-monospace, monospace; }
		.livecard .tail { font:11.5px ui-monospace, monospace; color:#3d4a60; background:#f3f5f9; border-radius:4px; padding:3px 6px; margin-top:6px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; min-height:1.6em; }
		.livecard .comps { margin-top:4px; }
		.clock { font-variant-numeric:tabular-nums; }
		.progress { position:relative; height:4px; background:#e3ecf8; border-radius:2px; overflow:hidden; margin:6px 0; }
		.progress span { position:absolute; top:0; left:-35%; width:35%; height:100%; background:var(--live); border-radius:2px; animation:slide 1.6s ease-in-out infinite; }
		ol.phases { display:flex; list-style:none; padding:0; margin:6px 0; counter-reset:phase; }
		ol.phases li { flex:1; position:relative; text-align:center; font-size:11.5px; color:var(--muted); padding-top:18px; counter-increment:phase; }
		ol.phases li::before { content:counter(phase); position:absolute; top:0; left:50%; transform:translateX(-50%); width:16px; height:16px; border-radius:50%;
		                        background:#dfe4ec; color:#fff; font-size:10px; line-height:16px; z-index:1; }
		ol.phases li::after { content:""; position:absolute; top:7px; left:-50%; width:100%; height:2px; background:#dfe4ec; }
		ol.phases li:first-child::after { display:none; }
		ol.phases li.done { color:var(--fg); } ol.phases li.done::before, ol.phases li.done::after, ol.phases li.now::after { background:var(--ok); }
		ol.phases li.now { color:var(--live); font-weight:600; } ol.phases li.now::before { background:var(--live); }
		ol.phases.live li.now::before { animation:glow 1.2s infinite; }
		ol.phases li.s-completed::before { background:var(--ok); } ol.phases li.s-failed::before { background:var(--bad); }
		ol.phases li.s-partial::before, ol.phases li.s-cancelled::before { background:var(--warn); }

		/* merge queue and config */
		.qs { font-weight:600; } .qs.waiting { color:var(--warn); } .qs.integrated { color:var(--violet); } .qs.attention { color:var(--bad); } .qs.landed { color:var(--ok); }
		.attn { border-left:3px solid var(--bad); background:var(--panel); padding:5px 9px; margin:5px 0; } .attn.planned { border-left-style:dashed; }
		tr.attn-row td:first-child { border-left:3px solid var(--bad); } tr.attn-row.planned td:first-child { border-left-style:dashed; }
		table.meaning td { font-size:13px; }
		.layer { display:inline-block; padding:0 7px; border-radius:9px; font-size:11px; margin-right:3px; border:1px solid var(--line); color:var(--muted); }
		.layer.unset { opacity:.45; text-decoration:line-through; }
		.layer.wins { color:#fff; border-color:transparent; } .layer.default.wins { background:var(--grey); } .layer.monorepo.wins { background:var(--accent); } .layer.user.wins { background:var(--violet); }
		.layer.default:not(.wins), .layer.monorepo:not(.wins), .layer.user:not(.wins) { background:var(--panel); }
		.pv { display:inline-block; padding:1px 8px; border-radius:10px; background:#eef1f6; font-size:12px; }
		.pv.own { font-weight:700; outline:1px solid var(--fg); }
		.pv.v-manual { background:#fbe3e0; color:var(--bad); } .pv.v-resolver { background:#efe7fb; color:var(--violet); } .pv.v-accept { background:#e2f0fd; color:var(--weave); }
		.pv.v-auto { background:#e4f4ea; color:var(--ok); }
		""";

	public const string Script = """
		// Progressive enhancement only: every page works without this script.
		(function () {
		  // Parts of a page that follow live work reload themselves until the server says nothing is live any more.
		  document.querySelectorAll('[data-live-src]').forEach(function (el) {
		    var timer = setInterval(function () {
		      fetch(el.getAttribute('data-live-src'), { headers: { 'X-Fragment': '1' } }).then(function (r) {
		        var live = r.headers.get('X-Live') === '1';
		        return r.text().then(function (html) { el.innerHTML = html; tick(); if (!live) clearInterval(timer); });
		      }).catch(function () { clearInterval(timer); });
		    }, 2000);
		  });

		  // Clocks of live runs tick every second between refreshes.
		  function tick() {
		    document.querySelectorAll('[data-since]').forEach(function (el) {
		      var s = Math.max(0, Math.floor((Date.now() - Date.parse(el.getAttribute('data-since'))) / 1000));
		      var h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), r = s % 60;
		      el.textContent = h > 0 ? h + 'h ' + String(m).padStart(2, '0') + 'm' : m + 'm ' + String(r).padStart(2, '0') + 's';
		    });
		  }
		  tick(); setInterval(tick, 1000);

		  // The component map: hovering a component lights up what it needs and what needs it.
		  document.querySelectorAll('svg.map').forEach(function (svg) {
		    svg.querySelectorAll('.node').forEach(function (node) {
		      node.addEventListener('mouseenter', function () {
		        var related = node.getAttribute('data-related').split(' ');
		        svg.classList.add('hovering');
		        svg.querySelectorAll('.node').forEach(function (n) { n.classList.toggle('hl', related.indexOf(n.getAttribute('data-node')) >= 0); });
		        svg.querySelectorAll('.edge').forEach(function (e) {
		          e.classList.toggle('hl', related.indexOf(e.getAttribute('data-from')) >= 0 && related.indexOf(e.getAttribute('data-to')) >= 0);
		        });
		      });
		      node.addEventListener('mouseleave', function () { svg.classList.remove('hovering'); });
		    });
		  });

		  // Commands copy themselves on click: the dashboard shows what to run, the terminal runs it.
		  document.addEventListener('click', function (e) {
		    var cmd = e.target.closest && e.target.closest('code.cmd');
		    if (!cmd || !navigator.clipboard) return;
		    navigator.clipboard.writeText(cmd.textContent).then(function () {
		      cmd.classList.add('copied'); setTimeout(function () { cmd.classList.remove('copied'); }, 900);
		    });
		  });
		})();
		""";
}
