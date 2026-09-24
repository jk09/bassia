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
		("/", "Home"),
		("/components", "Components"),
		("/timeline", "Timeline"),
		("/runs", "Runs"),
		("/integrations", "Integrations"),
		("/new", "New run")
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
		<header><span class="logo">Bassia</span> <span class="root">{{E(root)}}</span></header>
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

	public static string RunLink(string runId) => $"<a class=\"id\" href=\"/runs/{Url(runId)}\">{E(RunMetadata.ShortKey(runId))}</a>";

	public static string IntegrationLink(string id) => $"<a class=\"id\" href=\"/integrations/{Url(id)}\">{E(IntegrationRecord.ShortKey(id))}</a>";

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
				return key.Length == 32 && key.All(Uri.IsHexDigit) ? path + Url(idPrefix + key) : null;
			}
		}

		return null;
	}

	public static string Rationale(string command) => E(AgentCommand.SummarizeCommand(command));

	public static string Error(string message) => $"<div class=\"error\">{E(message)}</div>";

	public static string Notice(string message) => $"<div class=\"notice\">{E(message)}</div>";

	public const string Stylesheet = """
		:root { --fg:#1c2330; --muted:#6b7385; --line:#d5dae3; --bg:#fbfbfc; --head:#3b5b8f; --accent:#2f6fd0; --soft:#eef3fb;
		        --ok:#1f7a3a; --warn:#b36b00; --bad:#b3261e; --live:#0b72b9; }
		* { box-sizing: border-box; }
		body { margin:0; font:14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; color:var(--fg); background:var(--bg); }
		header { background:var(--head); color:#fff; padding:10px 18px; }
		header .logo { font-weight:700; font-size:18px; letter-spacing:.5px; }
		header .root { opacity:.8; font-family:ui-monospace, monospace; font-size:12px; margin-left:10px; }
		nav { background:#e7ebf2; border-bottom:1px solid var(--line); padding:0 12px; display:flex; flex-wrap:wrap; }
		nav a { padding:8px 12px; color:var(--fg); text-decoration:none; }
		nav a:hover { background:#dbe1eb; }
		nav a.active { background:var(--bg); font-weight:600; border:1px solid var(--line); border-bottom-color:var(--bg); margin-bottom:-1px; }
		main { padding:10px 18px 40px; max-width:1300px; }
		h1 { font-size:20px; margin:12px 0; } h2 { font-size:16px; margin:22px 0 8px; }
		a { color:var(--accent); }
		table.list { border-collapse:collapse; width:100%; margin:6px 0 14px; }
		table.list th { text-align:left; font-weight:600; background:#eef1f6; border-bottom:1px solid var(--line); padding:5px 8px; }
		table.list td { border-bottom:1px solid #eceff4; padding:5px 8px; vertical-align:top; }
		table.list tr:hover td { background:#f5f7fb; }
		table.facts td:first-child { color:var(--muted); padding-right:16px; white-space:nowrap; }
		.id, code, pre, .hash { font-family:ui-monospace, "Cascadia Mono", monospace; font-size:12.5px; }
		pre { background:#f3f4f7; border:1px solid var(--line); padding:10px; overflow:auto; white-space:pre-wrap; }
		pre.output { background:#11161f; color:#d9e1ee; max-height:480px; }
		.muted { color:var(--muted); }
		.status { font-weight:600; } .status.completed { color:var(--ok); } .status.partial, .status.cancelled { color:var(--warn); }
		.status.failed { color:var(--bad); } .status.started, .status.live, .status.running { color:var(--live); }
		.ref { display:inline-block; font:11.5px ui-monospace, monospace; padding:0 5px; margin:0 3px 2px 0; border-radius:3px;
		       background:#eef1f6; border:1px solid var(--line); color:var(--fg); text-decoration:none; }
		.ref.agent { background:#e9f3ff; border-color:#b9d4f5; } .ref.integration { background:#f3ecff; border-color:#d6c3f7; }
		.chip { display:inline-block; padding:0 6px; border-radius:3px; background:var(--soft); margin-right:4px; }
		.graph { background:#fff; border:1px solid var(--line); padding:8px; overflow:auto; }
		.graph a:hover rect { fill:#dce8fb; }
		.cards { display:flex; flex-wrap:wrap; gap:10px; margin:8px 0 16px; }
		.card { background:#fff; border:1px solid var(--line); border-radius:6px; padding:10px 14px; min-width:150px; color:var(--fg); text-decoration:none; }
		.card:hover { border-color:var(--accent); }
		.card .n { font-size:22px; font-weight:700; }
		.error { background:#fdecea; border:1px solid #f3b8b2; color:var(--bad); padding:8px 12px; border-radius:4px; margin:10px 0; }
		.notice { background:#eef6ee; border:1px solid #bfdcbf; color:var(--ok); padding:8px 12px; border-radius:4px; margin:10px 0; }
		form.inline { display:inline; }
		button, .button { font:inherit; padding:5px 12px; border:1px solid var(--head); background:var(--head); color:#fff; border-radius:4px; cursor:pointer; }
		button.secondary { background:#fff; color:var(--head); } button.danger { background:var(--bad); border-color:var(--bad); }
		select, input[type=text], input[type=number] { font:inherit; padding:4px 6px; border:1px solid var(--line); border-radius:4px; }
		.pick { display:grid; grid-template-columns:repeat(auto-fill, minmax(260px, 1fr)); gap:6px 16px; margin:6px 0 12px; }
		.pick label { display:flex; gap:8px; align-items:center; } .pick select { max-width:170px; }
		.chat { border:1px solid var(--line); border-radius:12px; background:#fff; padding:10px 12px; box-shadow:0 1px 3px rgba(0,0,0,.05); }
		.chat textarea { width:100%; min-height:110px; border:0; outline:0; resize:vertical; font:15px/1.5 system-ui, sans-serif; }
		.chat .bar { display:flex; flex-wrap:wrap; gap:10px; align-items:center; border-top:1px solid #eef0f4; padding-top:8px; }
		.chat .bar .grow { flex:1; }
		.chat .hint { color:var(--muted); font-size:12px; }
		.pager { margin:10px 0; } .pager a { margin-right:14px; }
		td.strategy-syntactic { color:var(--ok); font-weight:600; } td.strategy-semantic { color:#7a3ab8; font-weight:600; }
		td.strategy-skip { color:var(--muted); }
		""";

	public const string Script = """
		// Progressive enhancement only: every page works without this script.
		(function () {
		  // Parts of a page that follow live work reload themselves until the server says nothing is live any more.
		  document.querySelectorAll('[data-live-src]').forEach(function (el) {
		    var timer = setInterval(function () {
		      fetch(el.getAttribute('data-live-src'), { headers: { 'X-Fragment': '1' } }).then(function (r) {
		        var live = r.headers.get('X-Live') === '1';
		        return r.text().then(function (html) { el.innerHTML = html; if (!live) clearInterval(timer); });
		      }).catch(function () { clearInterval(timer); });
		    }, 1500);
		  });

		  // A live run page streams the run's phase and the agent's output as server-sent events.
		  var run = document.querySelector('[data-events]');
		  if (run && window.EventSource) {
		    var source = new EventSource(run.getAttribute('data-events'));
		    source.addEventListener('state', function (e) {
		      var s = JSON.parse(e.data);
		      document.querySelectorAll('[data-field]').forEach(function (el) {
		        var v = s[el.getAttribute('data-field')]; if (v !== undefined) el.textContent = v;
		      });
		    });
		    source.addEventListener('output', function (e) {
		      var out = document.getElementById('output');
		      if (out) { out.textContent = JSON.parse(e.data).join('\n'); out.scrollTop = out.scrollHeight; }
		    });
		    source.addEventListener('done', function () { source.close(); setTimeout(function () { location.reload(); }, 300); });
		  }

		  // The prompt: enter sends, shift+enter is a new line, and the command preview follows every change.
		  var prompt = document.getElementById('prompt');
		  if (prompt) {
		    var form = prompt.form;
		    prompt.addEventListener('keydown', function (e) {
		      if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); form.requestSubmit(); }
		    });
		    var preview = document.getElementById('preview');
		    var edited = false;
		    if (preview) {
		      preview.addEventListener('input', function () { edited = preview.value.length > 0; });
		      var update = function () {
		        if (edited) return;
		        var q = new URLSearchParams();
		        ['agent', 'model', 'effort', 'prompt', 'context'].forEach(function (k) {
		          var f = form.elements[k]; if (f) q.set(k, f.value);
		        });
		        fetch('/new/compose?' + q.toString()).then(function (r) { return r.text(); })
		          .then(function (t) { preview.placeholder = t; });
		      };
		      ['agent', 'model', 'effort', 'prompt', 'context'].forEach(function (k) {
		        var f = form.elements[k]; if (f) f.addEventListener('input', update);
		      });
		      update();
		    }
		  }
		})();
		""";
}
