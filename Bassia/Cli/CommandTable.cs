namespace Bassia.Cli;

/// <summary>
/// Every command of the <c>bassia</c> command line. The table is the single source of what is accepted, what help
/// says and what an error suggests; handlers live with the model they drive.
/// </summary>
internal static class CommandTable
{
	/// <summary>Commands that were replaced by the current grammar, with the command to use instead.</summary>
	public static readonly IReadOnlyDictionary<string, string> Replaced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["agent"] = "'bassia agent' was replaced by 'bassia run start -select <component@tag,...> -run <command>' (and 'bassia run retry', 'bassia run abandon').",
		["add-component"] = "'bassia add-component' was replaced by 'bassia component add -url <url> [-name <name>]'.",
		["integrate"] = "'bassia integrate' was replaced by 'bassia integration plan|start|advance'.",
		["commit"] = "'bassia commit' was removed: 'bassia config set' and the 'bassia component' commands commit the meta-repo themselves; use 'git -C .bassia commit' for hand edits.",
		["branch"] = "'bassia branch' was removed: 'bassia component show -name <component>' lists a component's branches and tags.",
		["ui"] = "'bassia ui' was removed: use the command line, or 'bassia web' for the web dashboard."
	};

	private static readonly SwitchSpec Id = new("id", "run-id", "The run: its full id, its <id> part, or a prefix of at least 4 digits such as the short id.", Required: true);
	private static readonly SwitchSpec IntegrationId = new("id", "integration-id", "The integration: its full id, its <id> part, or a prefix of at least 4 digits.", Required: true);
	private static readonly SwitchSpec Runs = new("runs", "run-id,...|all", "The runs to integrate, oldest first; 'all' is every run with a pushed result.", Required: true);
	private static readonly SwitchSpec Onto = new("onto", "component@ref,...", "Integrate a component onto this branch or tag instead of its default branch.");
	private static readonly SwitchSpec Semantic = new("semantic", "run-id,...", "Send these runs' results to the resolver even without a textual conflict.");
	private static readonly SwitchSpec Skip = new("skip", "run-id,...", "Leave these runs out.");
	private static readonly SwitchSpec Log = new("log", "path", "The log of a detached job (set by -detach).", Hidden: true);

	public static readonly IReadOnlyList<CommandSpec> Commands =
	[
		// ----- the monorepo -----

		new("init", null, "Create a Bassia monorepo in an empty folder: the .bassia meta-repo and the .workspace folder.",
			[new("path", "directory", "The folder to create it in (default: the current directory); may be given without -path.")],
			["bassia init", "bassia init -path R:\\monorepo", "bassia -C /srv/mono init"],
			InitCommand.RunAsync, Positional: "path"),

		new("status", null, "Summarize the monorepo: components and their dependency tree, runs and integrations by status, live work, and meta-repo changes.",
			[],
			["bassia status", "bassia -C R:\\ status"],
			MonorepoCommands.StatusAsync),

		new("config", "list", "List every setting of .bassia/config.toml with its value, default and where it comes from.",
			[],
			["bassia config list"],
			MonorepoCommands.ConfigListAsync),

		new("config", "get", "Show one setting.",
			[new("key", "key", "workspace.path, agent.command, agent.commit.subject or integration.resolver.", Required: true)],
			["bassia config get -key integration.resolver", "bassia config get agent.command"],
			MonorepoCommands.ConfigGetAsync, Positional: "key"),

		new("config", "set", "Change one setting in .bassia/config.toml (comments are kept) and commit it to the meta-repo.",
			[
				new("key", "key", "workspace.path, agent.command, agent.commit.subject or integration.resolver.", Required: true),
				new("value", "value", "The new value. An empty value is not allowed; set the default explicitly to go back.", Required: true)
			],
			[
				"bassia config set -key agent.command -value \"claude -p --permission-mode acceptEdits --model opus\"",
				"bassia config set -key workspace.path -value D:\\bassia-workspace",
				"bassia config set agent.commit.subject -value \"agent({short_id}): {summary}\""
			],
			MonorepoCommands.ConfigSetAsync, Positional: "key"),

		new("version", null, "Show the bassia version.",
			[],
			["bassia version"],
			MonorepoCommands.VersionAsync),

		// ----- components -----

		new("component", "list", "List the registered components with their dependencies, tags, branches and runs, as a table and as [[component]] entries.",
			[],
			["bassia component list"],
			ComponentCommands.ListAsync),

		new("component", "add", "Clone a repository as a bare component next to the meta-repo and register it in components.toml.",
			[
				new("url", "url", "The repository to clone (any URL or path git clone accepts).", Required: true),
				new("name", "name", "The component's logical name (default: inferred from the URL)."),
				new("references", "component[:path],...", "Components this one nests, each at a subfolder (default: its name).")
			],
			[
				"bassia component add -url https://github.com/myrepo/lib.git",
				"bassia component add -url https://github.com/myrepo/app.git -name app -references lib",
				"bassia component add -url ../upstream/ui -references lib:vendor/lib"
			],
			ComponentCommands.AddAsync, Positional: "url"),

		new("component", "show", "Show a component: its branches, tags (annotated ones can be selected for a run), dependencies, dependency tree and the runs that touched it.",
			[new("name", "component", "The component.", Required: true)],
			["bassia component show -name app", "bassia component show app"],
			ComponentCommands.ShowAsync, Positional: "name"),

		new("component", "set", "Replace a component's references (its dependencies); a cycle or an unregistered name is rejected and nothing changes.",
			[
				new("name", "component", "The component.", Required: true),
				new("references", "component[:path],...", "The components it nests, each at a subfolder (default: its name)."),
				new("clear-references", null, "Remove all its references.")
			],
			["bassia component set -name app -references lib,ui:vendor/ui", "bassia component set -name app -clear-references"],
			ComponentCommands.SetAsync, Positional: "name"),

		new("component", "remove", "Unregister a component nobody references; -purge also deletes its repository.",
			[
				new("name", "component", "The component.", Required: true),
				new("purge", null, "Also delete the component's source-of-truth repository.")
			],
			["bassia component remove -name old-lib", "bassia component remove -name old-lib -purge"],
			ComponentCommands.RemoveAsync, Positional: "name"),

		new("component", "tag", "Create an annotated tag in a component - the baseline a run selects with -select <component>@<tag>.",
			[
				new("name", "component", "The component.", Required: true),
				new("tag", "tag", "The tag to create.", Required: true),
				new("ref", "commit-ish", "What to tag: a branch, tag or commit (default: the default branch's head)."),
				new("message", "text", "The tag message.")
			],
			["bassia component tag -name app -tag v1", "bassia component tag lib -tag v1 -ref main -message \"release 1\""],
			ComponentCommands.TagAsync, Positional: "name"),

		new("component", "survey", "Describe a component for planning a split: its folders with files, bytes and commits, the folders that change together, and a split plan skeleton.",
			[
				new("name", "component", "The component.", Required: true),
				new("branch", "branch", "The branch to survey (default: the default branch)."),
				new("depth", "n", "How many folder levels to report (default: 2)."),
				new("limit", "n", "Read at most this many of the latest commits (default: 5000; 0 for all)."),
				new("pairs", "n", "Report at most this many co-changing folder pairs (default: 30).")
			],
			["bassia component survey -name app", "bassia component survey app -depth 3 -limit 0"],
			SplitCommands.SurveyAsync, Positional: "name"),

		new("component", "split", "Split a component into new components by a TOML plan: each part gets its files with their whole history (renames followed), " +
			"the parts are registered, references rewired, and the source retired.",
			[
				new("plan", "file|-", "The split plan (TOML), or - to read it from stdin.", Required: true),
				new("name", "component", "The component to split, if the plan does not name it as 'source'."),
				new("dry-run", null, "Check the plan and show the allocation without changing anything.")
			],
			["bassia component split -plan split.toml -dry-run", "bassia component split -plan split.toml", "bassia component split -name app -plan -"],
			SplitCommands.SplitAsync, Positional: "plan",
			Details: "The plan: source = \"<component>\", optional branch, shared = [patterns] (copied into every part), drop = [patterns] (left out), " +
				"follow_renames = true|false, one [[part]] per new component with name, paths = [patterns], optional references and url, and optional " +
				"[[referrer]] tables (name, references) for components that referenced the source (by default they reference every part). Patterns are " +
				"relative to the component root: ** any folders, * within a folder, ? one character, a folder matches everything below it, !pattern " +
				"excludes. Every file at the tip must go to exactly one part, 'shared' or 'drop'; all problems are reported at once, and nothing changes. " +
				"Each part is a new repository whose history holds every commit that changed its files (authors, dates and messages kept, a Split-from " +
				"trailer added), the tags of the branch, and a split record commit tagged split/<id>; the source's repository is kept, tagged split/<id>."),

		new("graph", null, "Draw the component dependency graph: ASCII boxes in layers (board), an ASCII tree, Mermaid or SVG.",
			[
				new("name", "component", "Only this component and what it depends on."),
				new("format", "board|tree|mermaid|svg", "The drawing (default: board)."),
				new("width", "columns", "The width of the board (default: 100)."),
				new("out", "file", "Write the drawing to a file instead of the result.")
			],
			["bassia graph", "bassia graph -format tree", "bassia graph -name app -format tree", "bassia graph -format svg -out components.svg"],
			ComponentCommands.GraphAsync),

		new("log", null, "Show history with an ASCII graph: the meta-repo's by default, or the combined timeline of components and the components they depend on.",
			[
				new("component", "component,...", "The components; the ones they depend on join automatically."),
				new("only", null, "Only the named components, without their dependencies."),
				new("limit", "n", "Commits per page (default: 20)."),
				new("page", "n", "The page (default: 1).")
			],
			["bassia log", "bassia log -component app", "bassia log -component app -only -limit 50", "bassia log -component app,tool -page 2"],
			LogCommand.RunAsync),

		// ----- agentic runs -----

		new("run", "start", "Start an agentic run: materialize the selected components at their tags in an isolated run folder, run the agent there, then commit, tag and push each changed component.",
			[
				new("select", "component@tag,...", "Every component of the run at an annotated tag; must cover the components they reference.", Required: true),
				new("detach", null, "Return at once and continue in the background; follow with run show/logs/wait, stop with run stop."),
				new("prompt", "text", "Compose the agent command from this prompt and the configured agent.command."),
				new("agent", "command", "With -prompt: the agent command to use instead of agent.command."),
				new("model", "model", "With -prompt: passed to the agent as --model."),
				new("effort", "level", "With -prompt: passed to the agent as --effort."),
				new("context", "text", "With -prompt: appended to the prompt as context."),
				new("id", "run-id", "The run's id (set by -detach).", Hidden: true),
				Log,
				new("run", "command", "The agent command, verbatim: the rest of the command line (put it last).", Rest: true)
			],
			[
				"bassia run start -select app@v1,lib@v1 -prompt \"add a changelog\" -model opus",
				"bassia run start -select app@v1,lib@v1 -detach -prompt \"fix the failing tests\"",
				"bassia run start -select lib@v1 -run claude -p \"refactor the parser\" --permission-mode acceptEdits"
			],
			RunCommands.StartAsync,
			Usage: "bassia run start -select <component@tag,...> [-detach] (-prompt <text> [-agent <command>] [-model <model>] [-effort <level>] [-context <text>] | -run <command...>)",
			Details: "Without -detach the agent's output streams to the terminal and the result follows when the run ends; Ctrl-C stops the run and records it as cancelled. " +
				"With -detach the selection is checked first, then the run continues in a background bassia process and the result (run_id, pid, log) is printed at once. " +
				"The agent runs in the run folder with BASSIA_ROOT, BASSIA_RUN_ID and BASSIA_RUN_DIR set."),

		new("run", "list", "List runs newest first - recorded ones and those still preparing - as a table and as [[run]] entries.",
			[
				new("status", "status", "Only runs with this status: preparing, started, completed, partial, failed, cancelled, abandoned, or live / stale."),
				new("component", "component", "Only runs that include this component."),
				new("limit", "n", "At most this many (default: 20).")
			],
			["bassia run list", "bassia run list -status live", "bassia run list -component lib -limit 5"],
			RunCommands.ListAsync),

		new("run", "show", "Show a run: its record, per-component results, whether it is live, and its latest output line.",
			[Id],
			["bassia run show -id 3f2a91c4", "bassia run show 3f2a91c4"],
			RunCommands.ShowAsync, Positional: "id"),

		new("run", "logs", "Print the captured output of a detached run (agent output and bassia's progress).",
			[Id, new("tail", "n", "The last n lines (default: 50; 0 for all).")],
			["bassia run logs 3f2a91c4", "bassia run logs -id 3f2a91c4 -tail 0"],
			RunCommands.LogsAsync, Positional: "id"),

		new("run", "wait", "Wait until a run is no longer live; ok when it completed.",
			[Id, new("timeout", "seconds", "Give up after this long with timed_out = true (default: 0, wait as long as it takes).")],
			["bassia run wait 3f2a91c4", "bassia run wait -id 3f2a91c4 -timeout 600"],
			RunCommands.WaitAsync, Positional: "id"),

		new("run", "stop", "Stop a live run (started from any shell, detached, or in bassia web): kill the agent's process tree, record the run as cancelled and keep its folder.",
			[Id, new("timeout", "seconds", "How long to wait for it to stop before killing its process (default: 30).")],
			["bassia run stop 3f2a91c4"],
			RunCommands.StopAsync, Positional: "id"),

		new("run", "retry", "Retry the commit/tag/push steps of a run that finished partial.",
			[Id],
			["bassia run retry 3f2a91c4"],
			RunCommands.RetryAsync, Positional: "id"),

		new("run", "abandon", "Discard a finished run's folder and record it as abandoned; results already pushed stay where they are.",
			[Id],
			["bassia run abandon 3f2a91c4"],
			RunCommands.AbandonAsync, Positional: "id"),

		new("run", "diff", "Show what a run changed in each component: files, insertions and deletions, a diffstat, and the patch with -patch.",
			[Id, new("component", "component", "Only this component."), new("patch", null, "Include the full patch.")],
			["bassia run diff 3f2a91c4", "bassia run diff -id 3f2a91c4 -component app -patch"],
			RunCommands.DiffAsync, Positional: "id"),

		// ----- integration -----

		new("integration", "plan", "Triage how runs' results would merge, per component: up_to_date, fast_forward, clean or conflict, and whether git or the resolver merges each. Changes nothing.",
			[Runs, Onto, Semantic, Skip],
			["bassia integration plan -runs all", "bassia integration plan -runs 3f2a91c4,91ab22cd -onto lib@v1"],
			IntegrationCommands.PlanCommandAsync),

		new("integration", "start", "Integrate runs' results per component: git merges what it can, then the resolver merges the rest from a semantic brief; the result is tagged integration/<id>/<n> in every component.",
			[
				Runs, Onto, Semantic, Skip,
				new("detach", null, "Return at once and continue in the background; follow with integration show/logs/wait, stop with integration stop."),
				new("id", "integration-id", "The integration's id (set by -detach).", Hidden: true),
				Log,
				new("resolve", "command", "The resolver command instead of integration.resolver: the rest of the command line (put it last).", Rest: true)
			],
			[
				"bassia integration start -runs all",
				"bassia integration start -runs all -skip 5e11aa00 -detach",
				"bassia integration start -runs 3f2a91c4,91ab22cd -semantic 91ab22cd -resolve claude -p --permission-mode acceptEdits --model opus"
			],
			IntegrationCommands.StartAsync),

		new("integration", "list", "List integrations newest first, as a table and as [[integration]] entries.",
			[new("limit", "n", "At most this many (default: 20).")],
			["bassia integration list"],
			IntegrationCommands.ListAsync),

		new("integration", "show", "Show an integration: its steps per component with triage, strategy, outcome and conflicts, its result tags, and whether it is live.",
			[IntegrationId],
			["bassia integration show 5e11aa00"],
			IntegrationCommands.ShowAsync, Positional: "id"),

		new("integration", "logs", "Print the captured output of a detached integration (resolver output and bassia's progress).",
			[IntegrationId, new("tail", "n", "The last n lines (default: 50; 0 for all).")],
			["bassia integration logs 5e11aa00"],
			IntegrationCommands.LogsAsync, Positional: "id"),

		new("integration", "wait", "Wait until an integration is no longer live; ok when it completed.",
			[IntegrationId, new("timeout", "seconds", "Give up after this long with timed_out = true (default: 0, no limit).")],
			["bassia integration wait 5e11aa00 -timeout 1800"],
			IntegrationCommands.WaitAsync, Positional: "id"),

		new("integration", "stop", "Stop a live integration: a working resolver is killed and nothing further is published.",
			[IntegrationId, new("timeout", "seconds", "How long to wait for it to stop before killing its process (default: 30).")],
			["bassia integration stop 5e11aa00"],
			IntegrationCommands.StopAsync, Positional: "id"),

		new("integration", "advance", "Fast-forward each component's base branch to the integration's result (refused if a branch moved, or the base was a tag).",
			[IntegrationId],
			["bassia integration advance 5e11aa00"],
			IntegrationCommands.AdvanceAsync, Positional: "id"),

		// ----- frontends -----

		new("web", null, "Serve the web dashboard on 127.0.0.1 until Ctrl-C: components and their graph, a timeline, runs started from a prompt and streamed live, and integrations.",
			[new("port", "n", "The port to try first (default: 8080; the next free one is used when taken)."), new("no-open", null, "Do not open the browser.")],
			["bassia web", "bassia web -port 9000 -no-open"],
			WebCommand.RunAsync),

		new("help", null, "Show help: every command, the subcommands of a command, or one command's switches and examples.",
			[new("command", "command [subcommand]", "The command to explain; may be given without -command.")],
			["bassia help", "bassia help run", "bassia help run start", "bassia run start -help"],
			Help.RunAsync, Positional: "command")
	];

	/// <summary>The groups (<c>run</c>, <c>component</c>, ...) and top-level commands, in table order.</summary>
	public static IReadOnlyList<string> Names => Commands.Select(command => command.Name).Distinct().ToList();

	public static IReadOnlyList<CommandSpec> Group(string name) =>
		Commands.Where(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
}
