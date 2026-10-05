namespace Bassia.Web;

using System.Text;
using Microsoft.AspNetCore.Http;
using static Bassia.Web.Html;

/// <summary>The configuration, layer by layer, and the merge policy each component ends up with.</summary>
internal sealed partial class Dashboard
{
	private IResult ConfigPage()
	{
		var monorepo = Current();
		var config = monorepo.Config;
		var body = new StringBuilder($"""
			<p class="muted">Each setting comes from the highest layer that sets it: <span class="layer user wins">user</span>
			<code>{E(ConfigFile.PathOf(monorepo.Root, ConfigLayer.User))}</code> (yours, never committed) over
			<span class="layer monorepo wins">monorepo</span> <code>{E(ConfigFile.PathOf(monorepo.Root))}</code> (committed, shared) over
			<span class="layer default wins">default</span>. Change them with {Cmd("bassia config set -key <key> -value <value> [-user]")} or {Cmd("bassia config unset -key <key> [-user]")},
			or ask {Cmd("bassia prompt ...")}.</p>
			<table class="list config"><tr><th>Key</th><th>Effective value</th><th>Layers</th><th>Allowed</th><th>Change</th></tr>
			""");
		foreach (var key in ConfigFile.Keys.Concat(config.ComponentKeys()))
		{
			var value = config.Resolve(key);
			var monorepoValue = config.Raw(ConfigLayer.Monorepo, key);
			var userValue = config.Raw(ConfigLayer.User, key);
			string Layer(string name, string? set, bool wins) =>
				$"<span class=\"layer {name}{(wins ? " wins" : "")}{(set is null ? " unset" : "")}\" title=\"{E(set ?? "not set")}\">{name}</span>";
			body.Append($"""
				<tr><td><code>{E(key.Key)}</code><div class="muted">{E(key.Description)}</div></td>
				<td><b>{E(value.Value.Length == 0 ? "(empty)" : value.Value)}</b><div class="muted">{E(value.Source)}</div></td>
				<td class="layers">{Layer("default", key.Default, value.Layer == ConfigLayer.Default)}{Layer("monorepo", monorepoValue, value.Layer == ConfigLayer.Monorepo)}{Layer("user", userValue, value.Layer == ConfigLayer.User)}</td>
				<td class="muted">{E(key.Allowed)}</td>
				<td>{Cmd($"bassia config set {key.Key} -value \"{value.Value}\"")}</td></tr>
				""");
		}

		body.Append("</table>");

		body.Append("<h2>Merge policy per component</h2><p class=\"muted\">The <code>merge.*</code> settings as each component gets them; override one component with <code>merge.component.&lt;name&gt;.&lt;setting&gt;</code>.</p>");
		body.Append("<table class=\"list\"><tr><th>Component</th><th>Unmergeable conflicts</th><th>Weave warnings</th><th>Manual paths</th><th>Advance</th></tr>");
		foreach (var component in monorepo.Components)
		{
			string Cell(string setting)
			{
				var value = config.Resolve(ConfigFile.ComponentKey(component.Name, setting));
				var own = value.SetAs == value.Key.Key && value.Layer != ConfigLayer.Default;
				return $"<td><span class=\"pv v-{E(value.Value.Split(',')[0])}{(own ? " own" : "")}\" title=\"{E(value.Source)}\">{E(value.Value.Length == 0 ? "-" : value.Value)}</span></td>";
			}

			body.Append($"<tr><td><a href=\"{ComponentHref(component.Name)}\"><b>{E(component.Name)}</b></a></td>{Cell("semantic")}{Cell("warnings")}{Cell("manual_paths")}{Cell("advance")}</tr>");
		}

		body.Append("</table><p class=\"muted\">A bold value is set for that component itself; the others come from <code>merge.*</code>.</p>");
		return View("/config", "Configuration", body.ToString());
	}
}
