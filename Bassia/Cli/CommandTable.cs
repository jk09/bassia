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
		["branch"] = "'bassia branch' was removed: a component has a single main branch; use 'bassia component fork -name <component> -as <fork>' to work in parallel ('bassia component show -name <component>' lists its tags).",
		["ui"] = "'bassia ui' was removed: use the command line, or 'bassia web' for the web dashboard."
	};

	private static readonly SwitchSpec Id = new("id", "run-id", "The run: its full id, its <key> part (the short id), or a prefix of at least 4 characters of the key.", Required: true);
	private static readonly SwitchSpec IntegrationId = new("id", "integration-id", "The integration: its full id, its <key> part (the short id), or a prefix of at least 4 characters of the key.", Required: true);
	private static readonly SwitchSpec Runs = new("runs", "run-id,...|all", "The runs to integrate, oldest first; 'all' is every run with a pushed result.", Required: true);
	private static readonly SwitchSpec Onto = new("onto", "component@ref,...", "Integrate a component onto this branch or tag instead of its default branch.");
	private static readonly SwitchSpec Semantic = new("semantic", "run-id,...", "Send these runs' results to the resolver even without a textual conflict.");
	private static readonly SwitchSpec Skip = new("skip", "run-id,...", "Leave these runs out.");
	private static readonly SwitchSpec Manual = new("manual", "run-id,...", "Leave these runs' results for a human (needs_attention) instead of merging them.");
	private static readonly SwitchSpec Weave = new("weave", "command|off", "The structural merge driver instead of integration.weave (default weave-driver, used when installed); off disables it.");
	private static readonly SwitchSpec Log = new("log", "path", "The log of a detached job (set by -detach).", Hidden: true);

	private const string ConfigDetails = "Settings resolve in layers: the user's .bassia/config.user.toml (git-ignored) over the monorepo's " +
		".bassia/config.toml (committed) over Bassia's default. A merge setting can be set per component as " +
		"merge.component.<component>.<setting>, which falls back to merge.<setting>. merge.semantic (resolver|manual): a result git and weave cannot " +
		"merge goes to the resolver or is left for a human; merge.warnings (resolver|manual|accept): a result weave merged with warnings; " +
		"merge.manual_paths (patterns): conflicts in these paths always need a human; merge.advance (manual|auto): a completed integration " +
		"fast-forwards the base branches itself. 'integration plan|start -semantic/-skip/-manual' override the policy for one integration.";

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

		new("config", "list", "List every setting with its effective value and the layer it comes from: default, monorepo (.bassia/config.toml) or user (.bassia/config.user.toml).",
			[],
			["bassia config list"],
			MonorepoCommands.ConfigListAsync,
			Details: ConfigDetails),

		new("config", "get", "Show one setting: its effective value, where it comes from, and its value in each layer.",
			[new("key", "key", "workspace.path, agent.command, agent.commit.subject, integration.resolver, integration.weave, merge.semantic, merge.warnings, merge.manual_paths, merge.advance, merge.component.<component>.<semantic|warnings|manual_paths|advance>, llm.backend or llm.command.", Required: true)],
			["bassia config get -key integration.resolver", "bassia config get merge.component.app.semantic"],
			MonorepoCommands.ConfigGetAsync, Positional: "key", Details: ConfigDetails),

		new("config", "set", "Change one setting: in .bassia/config.toml (comments are kept, committed to the meta-repo), or with -user in your own .bassia/config.user.toml (never committed).",
			[
				new("key", "key", "workspace.path, agent.command, agent.commit.subject, integration.resolver, integration.weave, merge.semantic, merge.warnings, merge.manual_paths, merge.advance, merge.component.<component>.<semantic|warnings|manual_paths|advance>, llm.backend or llm.command.", Required: true),
				new("value", "value", "The new value; a choice setting accepts only its choices, a list setting takes comma-separated values.", Required: true),
				new("user", null, "Write the user layer (.bassia/config.user.toml) instead of the monorepo's.")
			],
			[
				"bassia config set -key agent.command -value \"claude -p --permission-mode acceptEdits --model opus\"",
				"bassia config set -key merge.semantic -value manual",
				"bassia config set merge.component.db.manual_paths -value \"migrations,**/*.sql\"",
				"bassia config set -key merge.advance -value auto -user"
			],
			MonorepoCommands.ConfigSetAsync, Positional: "key", Details: ConfigDetails),

		new("config", "unset", "Remove one setting from a layer, so the next layer down (or the default) applies again.",
			[
				new("key", "key", "workspace.path, agent.command, agent.commit.subject, integration.resolver, integration.weave, merge.semantic, merge.warnings, merge.manual_paths, merge.advance, merge.component.<component>.<semantic|warnings|manual_paths|advance>, llm.backend or llm.command.", Required: true),
				new("user", null, "Remove it from the user layer instead of the monorepo's.")
			],
			["bassia config unset -key merge.semantic", "bassia config unset merge.advance -user"],
			MonorepoCommands.ConfigUnsetAsync, Positional: "key", Details: ConfigDetails),

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
				new("references", "component[:path],...", "Components this one nests, each at a subfolder (default: its name)."),
				new("unwind", null, "Also unwind its git submodules, recursively, into components it references (see 'bassia help component unwind').")
			],
			[
				"bassia component add -url https://github.com/myrepo/lib.git",
				"bassia component add -url https://github.com/myrepo/app.git -name app -references lib",
				"bassia component add -url ../upstream/ui -references lib:vendor/lib",
				"bassia component add -url https://github.com/myrepo/tool.git -unwind"
			],
			ComponentCommands.AddAsync, Positional: "url"),

		new("component", "unwind", "Turn a component's git submodules, recursively, into components it references at the submodule paths, so runs nest them as junctioned folders.",
			[
				new("name", "component", "The component.", Required: true),
				new("dry-run", null, "Show the plan (components added or reused, unwind commits, linking tags) without changing anything.")
			],
			["bassia component unwind -name app -dry-run", "bassia component unwind app"],
			UnwindCommands.UnwindAsync, Positional: "name",
			Details: "A submodule is identified by its URL (relative URLs resolve against the parent's; scheme, user, .git suffix and case do not matter): " +
				"one registered with that URL (or origin) is reused, otherwise it is cloned as a new component named after the URL (prefixed with the " +
				"parent's name on a clash). Every commit with submodules gets one commit on top that removes the gitlinks and their .gitmodules sections: " +
				"on the default branch it becomes the new tip, for a commit a submodule pins it stays off the branch. Each such commit and the commits its " +
				"submodules pin (unwound in turn) are tagged unwind/<component>/<n> - one annotated tag of the same name in every component involved - so " +
				"-select a@unwind/a/0,b@unwind/a/0 reproduces exactly the pinned combination. The whole tree is planned first: a pinned commit that cannot " +
				"be fetched, a cycle, or a path already nesting another component fails it and nothing changes. The meta-repo is committed once."),

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

		new("component", "fork", "Fork a component into a new one with its own main branch that keeps the parent's main-branch history up to the fork point - Bassia's replacement for branching.",
			[
				new("name", "component", "The component to fork (the parent).", Required: true),
				new("as", "fork", "The name of the new component.", Required: true),
				new("ref", "commit-ish", "Where to fork: a tag or commit on the parent's main branch (default: its head)."),
				new("url", "url", "Upstream repository of the fork, set as its origin (default: none)."),
				new("references", "component[:path],...", "Components the fork nests (default: the parent's references).")
			],
			["bassia component fork -name app -as app-search", "bassia component fork app -as app-hotfix -ref v1"],
			ComponentCommands.ForkAsync, Positional: "name",
			Details: "The fork is a bare repository next to the meta-repo with a single branch, named like the parent's, at the fork point; commit ids " +
				"of the history are unchanged and the tags on that history come along (run and integration tags do not). Parent and fork are " +
				"independent afterwards. components.toml records fork_of and fork_commit; the parent and the components referencing it are not " +
				"changed - point a referrer at the fork with 'bassia component set'. Bring the work back with a run and an integration."),

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

		// ----- tags across components -----

		new("tag", "list", "List every tag across the components with how many components it tags, as a table and as [[tag]] entries.",
			[
				new("component", "component,...", "Only tags in any of these components (each still counts every component it is in)."),
				new("prefix", "prefix", "Only tags whose name starts with this, e.g. unwind/ or integration/."),
				new("min", "n", "Only tags in at least n components (default: 1); -min 2 lists the multi-component tags.")
			],
			["bassia tag list", "bassia tag list -min 2", "bassia tag list -prefix unwind/ -component app"],
			TagCommands.ListAsync,
			Details: "Tags are read from the component repositories, the only record of them. Each [[tag]] has its kind (unwind, integration, run, " +
				"split or other), the components and commits it tags, and the -select value that starts a run from it in all of them."),

		new("tag", "show", "Show one tag in every component that has it: commit, subject, date and tag message.",
			[new("tag", "tag", "The tag.", Required: true)],
			["bassia tag show -tag unwind/app/0", "bassia tag show release-3"],
			TagCommands.ShowAsync, Positional: "tag"),

		new("tag", "create", "Create the same annotated tag in several components - a baseline for a run spanning them; none is created if any already has it.",
			[
				new("tag", "tag", "The tag to create.", Required: true),
				new("select", "component[@ref],...", "The components and what to tag in each (default: the default branch's tip).", Required: true),
				new("message", "text", "The tag message (default: the components and commits).")
			],
			["bassia tag create -tag release-3 -select app,lib", "bassia tag create release-3 -select app@main,lib@v2 -message \"release 3\""],
			TagCommands.CreateAsync, Positional: "tag"),

		new("graph", null, "Draw the component dependency graph: ASCII boxes in layers (board), an ASCII tree, Mermaid or SVG.",
			[
				new("name", "component", "Only this component and what it depends on."),
				new("format", "board|tree|mermaid|svg", "The drawing (default: board)."),
				new("width", "columns", "The width of the board (default: 100)."),
				new("out", "file", "Write the drawing to a file instead of the result.")
			],
			["bassia graph", "bassia graph -format tree", "bassia graph -name app -format tree", "bassia graph -format svg -out components.svg"],
			ComponentCommands.GraphAsync),

		new("log", null, "Show history with an ASCII graph: the meta-repo's by default, the combined timeline of components and the components they depend on, " +
			"or with -run where runs' work went in every component. Each commit names the run (and integration) its message records.",
			[
				new("component", "component,...", "The components; the ones they depend on join automatically. With -run: only these components."),
				new("only", null, "Only the named components, without their dependencies."),
				new("branch", "branch", "Only the history of this branch (e.g. main) in each component."),
				new("run", "run-id,...|all", "The runs' result commits and the integration merges that brought them in, across the components they touched, " +
					"with whether each landed on its component's default branch."),
				new("limit", "n", "Commits per page (default: 20)."),
				new("page", "n", "The page (default: 1).")
			],
			[
				"bassia log", "bassia log -component app", "bassia log -component app -only -limit 50", "bassia log -component app,tool -page 2",
				"bassia log -component app -branch main", "bassia log -run brave-otter-3f2a91", "bassia log -run brave-otter-3f2a91,quiet-fern-91ab22 -component lib"
			],
			LogCommand.RunAsync,
			Details: "Every [[commit]] carries kind = \"result\" (a run's result commit, run_id) or kind = \"integration\" (an integration's merge " +
				"commit, integration_id and the run_id it merged) when its message holds Bassia's record. With -run, each commit also has " +
				"on_default_branch, and each [[run]] lists per component its result tag and commit, the default branch, landed (the result is " +
				"reachable from that branch) and merged_by (the integrations whose merge of it is there)."),

		// ----- agentic runs -----

		new("run", "start", "Start an agentic run: materialize the selected components at their tags, hashes or default-branch tips in an isolated run folder, run the agent there, then commit, tag and push each changed component.",
			[
				new("select", "component[@tag|hash],...", "Every component of the run: a bare name starts from the tip of its default branch (resolved and recorded when the run starts), or add an annotated tag or a commit hash (6 to 40 lowercase hex digits, unambiguous in the component); must cover the components they reference.", Required: true),
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
				"bassia run start -select lib@v1 -run claude -p \"refactor the parser\" --permission-mode acceptEdits",
				"bassia run start -select app@3f9c2e1,lib@v1 -prompt \"bisect the regression\"",
				"bassia run start -select app,lib -prompt \"update the docs\""
			],
			RunCommands.StartAsync,
			Usage: "bassia run start -select <component[@tag|hash],...> [-detach] (-prompt <text> [-agent <command>] [-model <model>] [-effort <level>] [-context <text>] | -run <command...>)",
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
			["bassia run show -id brave-otter-3f2a91", "bassia run show brave-otter-3f2a91"],
			RunCommands.ShowAsync, Positional: "id"),

		new("run", "logs", "Print the captured output of a detached run (agent output and bassia's progress).",
			[Id, new("tail", "n", "The last n lines (default: 50; 0 for all).")],
			["bassia run logs brave-otter-3f2a91", "bassia run logs -id brave-otter-3f2a91 -tail 0"],
			RunCommands.LogsAsync, Positional: "id"),

		new("run", "wait", "Wait until a run is no longer live; ok when it completed.",
			[Id, new("timeout", "seconds", "Give up after this long with timed_out = true (default: 0, wait as long as it takes).")],
			["bassia run wait brave-otter-3f2a91", "bassia run wait -id brave-otter-3f2a91 -timeout 600"],
			RunCommands.WaitAsync, Positional: "id"),

		new("run", "stop", "Stop a live run (started from any shell, or detached): kill the agent's process tree, record the run as cancelled and keep its folder.",
			[Id, new("timeout", "seconds", "How long to wait for it to stop before killing its process (default: 30).")],
			["bassia run stop brave-otter-3f2a91"],
			RunCommands.StopAsync, Positional: "id"),

		new("run", "retry", "Retry the commit/tag/push steps of a run that finished partial.",
			[Id],
			["bassia run retry brave-otter-3f2a91"],
			RunCommands.RetryAsync, Positional: "id"),

		new("run", "abandon", "Discard a finished run's folder and record it as abandoned; results already pushed stay where they are.",
			[Id],
			["bassia run abandon brave-otter-3f2a91"],
			RunCommands.AbandonAsync, Positional: "id"),

		new("run", "diff", "Show what a run changed in each component: files, insertions and deletions, a diffstat, and the patch with -patch.",
			[Id, new("component", "component", "Only this component."), new("patch", null, "Include the full patch.")],
			["bassia run diff brave-otter-3f2a91", "bassia run diff -id brave-otter-3f2a91 -component app -patch"],
			RunCommands.DiffAsync, Positional: "id"),

		// ----- integration -----

		new("integration", "plan", "Triage how runs' results would merge, per component: up_to_date, fast_forward, clean or conflict for git, and whether git, the structural merge (weave), the resolver or a human (the merge.* policy) merges each. Changes nothing.",
			[Runs, Onto, Semantic, Skip, Manual, Weave],
			["bassia integration plan -runs all", "bassia integration plan -runs brave-otter-3f2a91,quiet-fern-91ab22 -onto lib@v1", "bassia integration plan -runs all -weave off"],
			IntegrationCommands.PlanCommandAsync),

		new("integration", "start", "Integrate runs' results per component: git merges what it can, the structural merge (weave) what git cannot, then the resolver the rest from a semantic brief; the result is tagged integration/<key>/<n> in every component.",
			[
				Runs, Onto, Semantic, Skip, Manual, Weave,
				new("detach", null, "Return at once and continue in the background; follow with integration show/logs/wait, stop with integration stop."),
				new("id", "integration-id", "The integration's id (set by -detach).", Hidden: true),
				Log,
				new("resolve", "command", "The resolver command instead of integration.resolver: the rest of the command line (put it last).", Rest: true)
			],
			[
				"bassia integration start -runs all",
				"bassia integration start -runs all -skip quiet-fern-91ab22 -detach",
				"bassia integration start -runs brave-otter-3f2a91,quiet-fern-91ab22 -semantic quiet-fern-91ab22 -resolve claude -p --permission-mode acceptEdits --model opus"
			],
			IntegrationCommands.StartAsync),

		new("integration", "list", "List integrations newest first, as a table and as [[integration]] entries.",
			[new("limit", "n", "At most this many (default: 20).")],
			["bassia integration list"],
			IntegrationCommands.ListAsync),

		new("integration", "show", "Show an integration: its steps per component with triage, strategy, outcome and conflicts, its result tags, and whether it is live.",
			[IntegrationId],
			["bassia integration show steady-heron-5e11aa"],
			IntegrationCommands.ShowAsync, Positional: "id"),

		new("integration", "logs", "Print the captured output of a detached integration (resolver output and bassia's progress).",
			[IntegrationId, new("tail", "n", "The last n lines (default: 50; 0 for all).")],
			["bassia integration logs steady-heron-5e11aa"],
			IntegrationCommands.LogsAsync, Positional: "id"),

		new("integration", "wait", "Wait until an integration is no longer live; ok when it completed.",
			[IntegrationId, new("timeout", "seconds", "Give up after this long with timed_out = true (default: 0, no limit).")],
			["bassia integration wait steady-heron-5e11aa -timeout 1800"],
			IntegrationCommands.WaitAsync, Positional: "id"),

		new("integration", "stop", "Stop a live integration: a working resolver is killed and nothing further is published.",
			[IntegrationId, new("timeout", "seconds", "How long to wait for it to stop before killing its process (default: 30).")],
			["bassia integration stop steady-heron-5e11aa"],
			IntegrationCommands.StopAsync, Positional: "id"),

		new("integration", "advance", "Fast-forward each component's base branch to the integration's result (refused if a branch moved, or the base was a tag).",
			[IntegrationId],
			["bassia integration advance steady-heron-5e11aa"],
			IntegrationCommands.AdvanceAsync, Positional: "id"),

		// ----- skills and natural language -----

		new("skill", "list", "List the meta-repo's skills (.bassia/skills/<name>/SKILL.md): reusable instructions 'bassia prompt' offers the LLM.",
			[],
			["bassia skill list"],
			PromptCommands.SkillListAsync),

		new("skill", "show", "Show one skill: its description and instructions.",
			[new("name", "skill", "The skill.", Required: true)],
			["bassia skill show -name release", "bassia skill show release"],
			PromptCommands.SkillShowAsync, Positional: "name"),

		new("prompt", null, "Do what an ask in natural language says: an LLM plans bassia commands from it, bassia checks and runs them, and answers.",
			[
				new("backend", "name", "The LLM backend instead of llm.backend: claude (Claude Code, the default) or command."),
				new("llm", "command", "The command the backend runs instead of llm.command."),
				new("model", "model", "The model the backend uses (claude: --model; command: replaces {model})."),
				new("skill", "skill,...", "Hand the LLM these skills' instructions up front (a /<skill> word in the ask does the same)."),
				new("yes", null, "Run commands that change something without asking."),
				new("dry-run", null, "Change nothing: run only read-only commands and show the plan up to the first command that would change something."),
				new("max-rounds", "n", "Give up after this many LLM rounds (default: 8)."),
				new("ask", "text", "The ask: the rest of the command line (put it last); may be given without -ask.", Required: true, Rest: true)
			],
			[
				"bassia prompt initialize the monorepo at folder C:\\mono",
				"bassia prompt -dry-run add https://github.com/myrepo/lib.git as a component and tag it v1",
				"bassia -C R:\\ prompt which components depend on lib?",
				"bassia prompt -yes /release cut release 3 of app and lib",
				"bassia prompt -backend command -llm \"ollama run llama3\" show the status"
			],
			PromptCommands.PromptAsync, Positional: "ask",
			Usage: "bassia prompt [-backend <name>] [-llm <command>] [-model <model>] [-skill <skill,...>] [-yes] [-dry-run] [-max-rounds <n>] <ask...>",
			Details: "The LLM gets the ask, the working directory and monorepo, this command catalog and the skills' descriptions, and answers with " +
				"commands as TOML argument lists. Each is checked against the command table, then run as its own bassia process; its output is shown " +
				"and its TOML result goes back to the LLM for the next round until it is done. Read-only commands run at once; a command that changes " +
				"something is confirmed (y/n/a), or needs -yes when stdin is not a terminal. 'prompt' and 'web' are never run. Every [[step]] has the " +
				"command_line, status (ok, failed, rejected, declined, planned, skipped) and the command's message. Skills are " +
				".bassia/skills/<name>/SKILL.md files with 'name' and 'description' front matter; the LLM loads the ones it needs."),

		// ----- frontends -----

		new("web", null, "Serve the read-only web dashboard on 127.0.0.1 until Ctrl-C: the component map, tags across components, live and past runs, the merge queue with its triage and the merges needing attention, and the configuration layers.",
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
