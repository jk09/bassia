namespace Bassia.Web;

using Bassia.Storage;
using System.Text;
using Bassia.Locks;
using Microsoft.AspNetCore.Http;
using static Bassia.Web.Html;

/// <summary>
/// Locks: which files and folders parallel runs hold right now, who waits for whom, the holds remembered until their
/// results land, the lock filter of each component, and the results that collided on a lock - avoidance as the merge
/// strategy that comes before git, weave and the resolver.
/// </summary>
internal sealed partial class Dashboard
{
	private async Task<IResult> LocksAsync()
	{
		var state = await LoadAsync();
		var board = Visible(state);
		var conflicts = LockConflictRows(state).ToList();
		var waiting = board.Waits.Select(wait => wait.Entry.RunId).Distinct().Count();
		var body = new StringBuilder();
		body.Append("<div class=\"cards\">")
			.Append(Card("paths held by live runs", board.Live.Count(), "#holds", board.Live.Any() ? "live" : null))
			.Append(Card("runs waiting for a path", waiting, "#holds", waiting > 0 ? "live" : null))
			.Append(Card("released, results on their way", board.Holds.Count(row => row.State == LockState.Released), "#holds"))
			.Append(Card("expired holds", board.Holds.Count(row => row.State == LockState.Expired), "#holds"))
			.Append(Card("results with lock conflicts", conflicts.Count(row => row.Pending), "#conflicts", conflicts.Any(row => row.Pending) ? "alert" : null))
			.Append("</div>");

		body.Append($"""
			<p class="muted">While a run's agent works, Bassia holds every file it changes that the <a href="#filter">lock filter</a> covers - a file on its own,
			or its whole folder - and every other run sees it. An agent asks {Cmd("bassia lock check -path <path>")} before it rewrites something, and
			{Cmd("bassia lock wait -path <path>")} waits until the holder is done, then continues from the holder's result. A hold lasts while its run renews it
			(within <code>lock.ttl</code> = {board.Ttl.TotalSeconds:0}s): a crashed or suspended run's holds expire. A finished run's holds are remembered until its
			results land, so later runs catch up first. A run that changed a path anyway has a <a href="#conflicts">lock conflict</a>, and the integration treats its
			result by <code>lock.conflict</code>.</p>
			""");

		body.Append("<h2 id=\"holds\">Who holds what</h2>")
			.Append("<div data-live-src=\"/fragment/locks\" data-live-on=\"lock run\">").Append(Holdings(board)).Append("</div>");

		body.Append("<h2 id=\"filter\">Lock filter per component</h2>")
			.Append("<p class=\"muted\">What a run holds while its agent changes it: <code>lock.files</code> one by one, <code>lock.folders</code> as a whole " +
				"(<code>src/*</code> is each folder directly under src, <code>**</code> the folder of every file). Override one component with " +
				"<code>lock.component.&lt;name&gt;.&lt;setting&gt;</code>; a run adds its own with <code>-lock-files</code> / <code>-lock-folders</code>.</p>")
			.Append("<table class=\"list\"><tr><th>Component</th><th>Files</th><th>Folders</th><th>A result with a lock conflict goes to</th><th>Held now</th></tr>");
		foreach (var component in state.Monorepo.Components)
		{
			var filter = LockFilter.For(state.Monorepo, component.Name);
			var (files, folders) = filter.Texts;
			body.Append($"<tr><td><a href=\"{ComponentHref(component.Name)}\"><b>{E(component.Name)}</b></a></td>" +
				$"<td>{Patterns(files)}</td><td>{Patterns(folders)}</td><td>{LockPolicyText(LockFilter.ConflictPolicy(state.Monorepo.Config, component.Name))}</td>" +
				$"<td>{board.InComponent(component.Name).Count(row => row.State == LockState.Live)}</td></tr>");
		}

		body.Append("</table>")
			.Append($"<p>{Cmd("bassia config set lock.folders -value \"src/*\"")} {Cmd("bassia config set lock.component.<component>.files -value \"**/*.cs\"")} " +
				$"{Cmd("bassia config set lock.conflict -value manual")}</p>");

		body.Append($"<h2 id=\"conflicts\">Lock conflicts{(conflicts.Count > 0 ? $" <span class=\"count bad\">{conflicts.Count}</span>" : "")}</h2>");
		if (conflicts.Count == 0)
		{
			body.Append("<p class=\"muted\">No run changed a path another run held.</p>");
		}
		else
		{
			body.Append("<table class=\"list\"><tr><th>Run</th><th>Path</th><th>Conflict</th><th>With</th><th>Its result</th><th>Integration</th></tr>");
			foreach (var row in conflicts)
			{
				body.Append($"<tr{(row.Pending ? " class=\"attn-row\"" : "")}><td>{RunLink(row.Run.RunId)}<div class=\"muted\">{Rationale(row.Run.Command)}</div></td>" +
					$"<td><code>{E(row.Conflict.Display)}</code></td>" +
					$"<td>{(row.Conflict.Kind == LockConflict.Stale ? "changed it without the other run's result" : "changed it while the other run held it")}</td>" +
					$"<td>{RunLink(row.Conflict.With)} <span class=\"muted\">{E(row.Conflict.WithPath)}</span></td>" +
					$"<td>{(row.Queue is null ? "<span class=\"muted\">-</span>" : QueueStateText(row.Queue.State))}</td>" +
					$"<td>{LockPolicyText(LockFilter.ConflictPolicy(state.Monorepo.Config, row.Conflict.Component))}</td></tr>");
			}

			body.Append("</table>");
		}

		body.Append($"<p>{Cmd("bassia lock list")} {Cmd("bassia lock check -path <component>/<path>")} {Cmd("bassia lock release -run <run> -all -force")}</p>");
		return View("/locks", "Locks", body.ToString());
	}

	private async Task<IResult> LocksFragmentAsync(HttpResponse response)
	{
		var state = await LoadAsync();
		response.Headers["X-Live"] = "1"; // keep following: holds come and go with the runs
		return Results.Content(Holdings(Visible(state)), "text/html; charset=utf-8");
	}

	/// <summary>The lock table without the released holds whose results have landed: nobody needs to catch up with those any more.</summary>
	private static LockBoard Visible(State state) => state.Locks with
	{
		Holds = state.Locks.Holds.Where(row => row.State != LockState.Released || !state.Queue.Items.Any(item =>
			item.Run.RunId == row.Entry.RunId && item.Component == row.Hold.Key.Component && item.State == QueueState.Landed)).ToList()
	};

	/// <summary>
	/// The holds as a flow - runs waiting, the paths they wait for and every held path, and the runs holding them - and
	/// as a table with each hold's state, since when and its waiters.
	/// </summary>
	private static string Holdings(LockBoard board)
	{
		if (board.Holds.Count == 0 && board.Waits.Count == 0)
		{
			return "<p class=\"muted idle\">Nothing is held. This updates by itself when a run holds a path.</p>";
		}

		var nodes = new List<FlowNode>();
		var edges = new List<(string, string)>();
		foreach (var row in board.Holds)
		{
			var path = "p:" + row.Hold.Key.Display;
			if (nodes.All(node => node.Id != path))
			{
				nodes.Add(new FlowNode(path, 1, row.Hold.Key.Display, $"{LockKey.KindName(row.Hold.Key.Kind)} · {LockCommands.StateName(row.State)}", $"/components/{Url(row.Hold.Key.Component)}",
					$"lockpath {LockCommands.StateName(row.State)}"));
			}

			var holder = "h:" + row.Entry.RunId;
			if (nodes.All(node => node.Id != holder))
			{
				nodes.Add(new FlowNode(holder, 2, RunMetadata.Key(row.Entry.RunId), row.State == LockState.Released ? "finished" : LockCommands.StateName(row.State), $"/runs/{Url(row.Entry.RunId)}", "holder"));
			}

			edges.Add((path, holder));
		}

		foreach (var wait in board.Waits)
		{
			var waiter = "w:" + wait.Entry.RunId;
			if (nodes.All(node => node.Id != waiter))
			{
				nodes.Add(new FlowNode(waiter, 0, RunMetadata.Key(wait.Entry.RunId), "waiting", $"/runs/{Url(wait.Entry.RunId)}", "waiter"));
			}

			foreach (var row in board.Holds.Where(row => row.State == LockState.Live && row.Hold.Key.Overlaps(wait.Wait.Key)))
			{
				edges.Add((waiter, "p:" + row.Hold.Key.Display));
			}
		}

		var body = new StringBuilder(Charts.Flow(nodes, edges, ["runs waiting", "held paths", "holding runs"]));
		body.Append("<table class=\"list\"><tr><th>Path</th><th>Kind</th><th>Held by</th><th>State</th><th>How</th><th>Since</th><th>Renewed</th><th>Waiting</th></tr>");
		foreach (var row in board.Holds)
		{
			var renewed = Storage.LockStores.Age(row.Entry);
			var result = row.Entry.Results.FirstOrDefault(result => result.Component == row.Hold.Key.Component);
			body.Append($"<tr><td><span class=\"lockchip {LockCommands.StateName(row.State)}\">{E(row.Hold.Key.Display)}</span></td><td>{LockKey.KindName(row.Hold.Key.Kind)}</td>" +
				$"<td>{RunLink(row.Entry.RunId)}</td>" +
				$"<td><span class=\"ls {LockCommands.StateName(row.State)}\">{StateText(row.State)}</span>{(result is null ? "" : $" {Ref(result.Tag)}")}</td>" +
				$"<td>{E(row.Hold.How)}</td><td>{Time(row.Hold.Since)}</td>" +
				$"<td class=\"muted\">{(row.State == LockState.Released ? "-" : renewed == TimeSpan.MaxValue ? "?" : $"{Elapsed(renewed)} ago")}</td>" +
				$"<td>{(row.Waiters.Count == 0 ? "<span class=\"muted\">-</span>" : string.Join(" ", row.Waiters.Select(RunLink)))}</td></tr>");
		}

		return body.Append("</table>").ToString();
	}

	private static string StateText(LockState state) => state switch
	{
		LockState.Live => "live",
		LockState.Released => "released, result not landed",
		_ => "expired"
	};

	private static string Patterns(IReadOnlyList<string> patterns) =>
		patterns.Count == 0 ? "<span class=\"muted\">-</span>" : string.Join(" ", patterns.Select(pattern => $"<code>{E(pattern)}</code>"));

	internal static string LockPolicyText(LockConflictPolicy policy) => policy switch
	{
		LockConflictPolicy.Manual => "<span class=\"pill st-manual\">a human</span>",
		LockConflictPolicy.Merge => "<span class=\"pill st-syntactic\">merged as usual</span>",
		_ => "<span class=\"pill st-semantic\">the resolver</span>"
	};

	private sealed record LockConflictRow(RunMetadata Run, LockConflict Conflict, QueueItem? Queue)
	{
		/// <summary>The result is still on its way to the default branch, so the conflict still matters.</summary>
		public bool Pending => Queue is { State: not QueueState.Landed };
	}

	/// <summary>Every recorded lock conflict, newest run first, with where its result stands in the merge queue.</summary>
	private static IEnumerable<LockConflictRow> LockConflictRows(State state) => state.Runs
		.SelectMany(run => run.Components.SelectMany(component => component.LockConflicts.Select(conflict => new LockConflictRow(run, conflict,
			state.Queue.Items.FirstOrDefault(item => item.Run.RunId == run.RunId && item.Component == component.Name)))))
		.Take(50);

	/// <summary>A live run's holds and waits on its card.</summary>
	private static string LockLine(LiveRun run) =>
		(run.Holds is { Count: > 0 } holds ? string.Concat(holds.Select(hold => $"<span class=\"lockchip live\">{E(hold)}</span>")) : "") +
		(run.Waits is { Count: > 0 } waits ? string.Concat(waits.Select(wait => $"<span class=\"lockchip waiting\">{E(wait)}</span>")) : "");
}
