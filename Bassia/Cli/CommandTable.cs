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
		["run"] = "'bassia run' was replaced by 'bassia agent' (renamed): use 'bassia agent start|list|show|logs|wait|stop|retry|abandon|diff' (e.g. 'bassia agent start -select <component@tag,...> -prompt <text>').",
		["add-component"] = "'bassia add-component' was replaced by 'bassia component add -url <url> [-name <name>]'.",
		["integrate"] = "'bassia integrate' was replaced by 'bassia integration plan|start|advance'.",
		["commit"] = "'bassia commit' was removed: 'bassia config set' and the 'bassia component' commands commit the meta-repo themselves; use 'git -C .bassia commit' for hand edits.",
		["branch"] = "'bassia branch' was removed: a component has a single main branch; use 'bassia component fork -name <component> -as <fork>' to work in parallel ('bassia component show -name <component>' lists its tags).",
		["ui"] = "'bassia ui' was removed: use the command line, or 'bassia web' for the web dashboard.",
		["prompt"] = "'bassia prompt' was removed: bassia has no LLM of its own. Drive it from your agent (Claude Code, Codex, ...): the agent learns every command from 'bassia help' and 'bassia help -all' and runs them itself."
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
	private static readonly SwitchSpec LockPaths = new("path", "path,...", "Files or folders (a folder ends with /): relative to the working directory inside the run folder, as the agent sees them, or as <component>/<path>.", Required: true);
	private static readonly SwitchSpec LockRun = new("run", "run-id", "The run (default: BASSIA_RUN_ID, which every agent command has): its full id, its key, or a prefix of at least 4 characters of the key.");
	private static readonly SwitchSpec LockComponent = new("component", "component", "Read the paths relative to this component's root.");

	private const string LockDetails = "While a run's agent works, Bassia looks at what it changed every few seconds and holds every changed path the run's lock " +
		"filter covers: lock.files (files one by one) and lock.folders (a whole folder: src/* is each folder directly under src, ** the folder of every " +
		"file), per component as lock.component.<component>.files|folders, plus the run's own -lock-files/-lock-folders. Every run sees the holds in " +
		"<root>/.locks. A hold is live while its run renews it within lock.ttl seconds (and, on the same machine, its process lives); a crashed or " +
		"suspended run's holds expire, and a suspended run that comes back loses what others took meanwhile. When a run ends its holds are released " +
		"and remembered with its results until they land on the default branch: a run that changes such a path without that result is behind, and " +
		"'lock wait' brings the result in. A run that changes a path another live run holds, or a released path it is behind on, records a lock " +
		"conflict; the integration treats its result by lock.conflict (resolver, manual or merge).";

	private const string BoundaryDetails = "Rules are [[boundary]] tables in .bassia/components.toml (source, allow, deny, description), tags " +
		"and visibility are keys of each [[component]] (tags = [...], visibility = [...], owners = [...]). Every component has the implicit " +
		"tag component:<name>. A reference A -> B is checked against every rule whose source matches a tag of A, once per matching tag: with " +
		"allow, a tag of B must match an allow pattern; with deny, none may match a deny pattern. A component with a visibility may be " +
		"referenced only by components with a tag matching one of its patterns (visibility = [] makes it private). Patterns: * any " +
		"characters, ? one character; $1, $2, ... in allow and deny are what the source's wildcards matched (scope:* allow scope:$1).";

	private const string CodeDetails = "-code reads path-like dependencies from the code: relative JS/TS imports, quoted C/C++ includes, " +
		"MSBuild items and imports, file:/link: packages in package.json and CMake add_subdirectory/include (namespace imports such as C# " +
		"using or Python import name no folder and are not seen). Each is resolved where the run nests the references, and is a violation " +
		"when it uses a path of a referenced component its exports (exports = [patterns] on the component) do not cover (private), reaches " +
		"a component through another's nested reference such as lib/vendor/core (indirect), or leaves the component's root (outside). " +
		"References the code never uses are listed as unused_references, for information.";

	private const string ConformanceDetails = "Rules are [[conformance]] tables in .bassia/components.toml: components (a tag pattern, as in " +
		"boundary rules), and any of files = [paths], tasks = [names], owners = true, exports = true, description. Conformance is reported, " +
		"never enforced on a change; hold results back with merge.verify, or fail CI with 'conformance check -strict'.";

	private const string TaskDetails = "A component's tasks are [tasks.<name>] tables in bassia.toml at its root: command (a shell line run in the " +
		"component's folder) and depends_on - 'x' runs the component's own task x first, '^x' runs task x of every component it references, " +
		"transitively, first (depends_on = [\"^build\"]). cache = true lets an unchanged task be restored instead of run, with the files " +
		"outputs = [patterns] names (see 'bassia help task cache'). A component without bassia.toml has no tasks.";

	private const string UserDetails = "The current user comes from BASSIA_USER, else user.id in your own configuration layer (bassia user use), else the " +
		"registered user whose email is the git user.email configured for the meta-repo. Once a monorepo has users, a command that changes it " +
		"needs a known user: its commits, annotated tags and meta-repo revisions are authored by them, and runs, integrations and lock entries " +
		"record their id. A run's result commits and tags are committed as '<agent> via bassia' with the user's email (the agent is the first word " +
		"of the run's command), and the agent's own bassia commands act for the same user. 'lock list|check|claim|wait' name the user holding a " +
		"path and how to reach them. Without users nothing is attributed.";

	private const string ConfigDetails = "Settings resolve in layers: the user's .bassia/config.user.toml (git-ignored) over the monorepo's " +
		".bassia/config.toml (committed) over Bassia's default. A merge setting can be set per component as " +
		"merge.component.<component>.<setting>, which falls back to merge.<setting>. merge.semantic (resolver|manual): a result git and weave cannot " +
		"merge goes to the resolver or is left for a human; merge.warnings (resolver|manual|accept): a result weave merged with warnings; " +
		"merge.manual_paths (patterns): conflicts in these paths always need a human; merge.advance (manual|auto): a completed integration " +
		"fast-forwards the base branches itself. 'integration plan|start -semantic/-skip/-manual' override the policy for one integration. " +
		"lock.files / lock.folders (patterns): what a run holds while its agent changes it, file by file or folder by folder; lock.conflict " +
		"(resolver|manual|merge): a result that changed a path another run held; both per component as lock.component.<component>.<setting>; " +
		"lock.ttl (seconds): how long a hold lasts without its run renewing it.";

	private const string ServerDetails = "A Bassia server (bassia-server) keeps a monorepo for many users; 'bassia clone -from https://<server>' after logging in makes a clone " +
		"whose locks, jobs, run and integration records and registry are the server's. The login is kept in the OS keychain where there is one, else in " +
		"~/.bassia/credentials; BASSIA_TOKEN overrides it.";

	private const string AccessDetails = "Permissions are read, run (start runs, push run branches and tags), integrate (land on the default branch, push other " +
		"tags) and admin (change the registration), each including the ones before; owners of a component hold integrate, server admins everything. A user " +
		"sees only the components they may read.";

	private const string ViewDetails = "A config spec is line rules, # starting a comment: 'map <component>[,...]' maps the components and, transitively, " +
		"what they reference; 'select <component-pattern> <version>' picks a version - a branch (followed by 'view update'), a tag or a commit hash - " +
		"the first matching rule winning, a component no rule matches at its default branch's tip; 'load [!]<component-pattern>[/<path-pattern>]' " +
		"names the files to download and write (path patterns as in split plans: ** any folders, * within one segment, a folder is everything below " +
		"it; ! leaves files out). A mapped component nothing loads is there without files, its whole tree listed by 'view ls' and loaded on " +
		"request by 'view load'. In a clone (bassia clone, depot.url) a component without a local repository is imported from the depot, history " +
		"only, when a view first maps it, and content is downloaded file by file as it is loaded. The view folder holds .bassia-view.toml, so view " +
		"commands work from anywhere inside it.";

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
			[new("key", "key", "workspace.path, agent.command, agent.commit.subject, integration.resolver, integration.weave, merge.semantic, merge.warnings, merge.manual_paths, merge.advance, merge.component.<component>.<semantic|warnings|manual_paths|advance>, lock.files, lock.folders, lock.conflict, lock.ttl or lock.component.<component>.<files|folders|conflict>.", Required: true)],
			["bassia config get -key integration.resolver", "bassia config get merge.component.app.semantic"],
			MonorepoCommands.ConfigGetAsync, Positional: "key", Details: ConfigDetails),

		new("config", "set", "Change one setting: in .bassia/config.toml (comments are kept, committed to the meta-repo), or with -user in your own .bassia/config.user.toml (never committed).",
			[
				new("key", "key", "workspace.path, agent.command, agent.commit.subject, integration.resolver, integration.weave, merge.semantic, merge.warnings, merge.manual_paths, merge.advance, merge.component.<component>.<semantic|warnings|manual_paths|advance>, lock.files, lock.folders, lock.conflict, lock.ttl or lock.component.<component>.<files|folders|conflict>.", Required: true),
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
				new("key", "key", "workspace.path, agent.command, agent.commit.subject, integration.resolver, integration.weave, merge.semantic, merge.warnings, merge.manual_paths, merge.advance, merge.component.<component>.<semantic|warnings|manual_paths|advance>, lock.files, lock.folders, lock.conflict, lock.ttl or lock.component.<component>.<files|folders|conflict>.", Required: true),
				new("user", null, "Remove it from the user layer instead of the monorepo's.")
			],
			["bassia config unset -key merge.semantic", "bassia config unset merge.advance -user"],
			MonorepoCommands.ConfigUnsetAsync, Positional: "key", Details: ConfigDetails),

		new("tutorial", null, "Guide a person through Bassia: set up a new monorepo step by step, or pick the next step in the monorepo you are in. Reads its answers from stdin.",
			[],
			["bassia tutorial", "bassia -C R:\\monorepo tutorial"],
			TutorialCommand.RunAsync,
			Details: "Outside a monorepo it asks for the repositories that become components, which component needs which, and the root folder, " +
				"shows the plan as the bassia commands it will run (init, component add) and runs them once confirmed. Inside one it shows the " +
				"status and a menu: add components, set dependencies, show the status, start a run, open the dashboard. " +
				"At any question, ? explains the step. A bare " +
				"'bassia' at a terminal starts it after a short help."),

		new("version", null, "Show the bassia version.",
			[],
			["bassia version"],
			MonorepoCommands.VersionAsync),

		// ----- feedback -----

		new("feedback", "send", "Send feedback - a bug, an idea or a question - as an issue in Bassia's GitHub repository, after showing it and asking you to confirm.",
			[
				new("kind", "bug|idea|question", "What it is (default: bug); it becomes the issue's label."),
				new("yes", null, "Send without asking (needed when stdin is not a terminal)."),
				new("message", "text", "The feedback: the rest of the command line (put it last); its first line becomes the issue title. May be given without -message.", Required: true, Rest: true)
			],
			["bassia feedback send status shows a stale run after a crash", "bassia feedback send -kind idea -yes let tag create take a pattern"],
			FeedbackCommands.SendAsync, Positional: "message",
			Usage: "bassia feedback send [-kind <bug|idea|question>] [-yes] <message...>",
			Details: "The issue is filed in jk09/bassia with the token in BASSIA_FEEDBACK_TOKEN (else GITHUB_TOKEN). " +
				"Before anything leaves the machine the issue is shown, with the home folder, user and machine names, credentials and tokens " +
				"replaced; the Bassia version, runtime and operating system are added. Without -yes it needs a terminal to ask on. A send that " +
				"fails is kept and can be retried with 'feedback pending -send'."),

		new("feedback", "reports", "Show or choose what happens when Bassia crashes: off (nothing is sent), ask (show the report and ask) or auto (file it without asking).",
			[new("mode", "off|ask|auto", "The new choice, kept for this user. Until one is made, nothing is sent and a crash at a terminal asks once.")],
			["bassia feedback reports", "bassia feedback reports -mode auto", "bassia feedback reports -mode off"],
			FeedbackCommands.ReportsAsync,
			Details: "A crash is an exception no command handles. The report holds the command's name (never its arguments), the exception, its " +
				"redacted stack trace, and the Bassia, runtime and OS versions. A crash already filed from this machine is not filed again, and " +
				"the same crash from others is added to its open issue. Without a terminal only auto sends. The choice lives in " +
				"feedback.toml under BASSIA_HOME (the installer's folder), and the token in BASSIA_FEEDBACK_TOKEN or GITHUB_TOKEN."),

		new("feedback", "pending", "List the approved reports that could not be sent yet (no token, no network), or send them.",
			[new("send", null, "Send them now; each one that goes through is removed.")],
			["bassia feedback pending", "bassia feedback pending -send"],
			FeedbackCommands.PendingAsync),

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
				new("unwind", null, "Also unwind its git submodules, recursively, into components it references (see 'bassia help component unwind')."),
				new("tags", "tag,...", "Its architecture tags (type:lib, scope:billing) that the boundary rules are written over."),
				new("visibility", "pattern,...", "Only components with a tag matching one of these patterns may reference it (default: public)."),
				new("owners", "owner,...", "Who owns it: people or teams."),
				new("exports", "pattern,...", "Its public API: path patterns (as in split plans) a referrer's code may use (default: everything).")
			],
			[
				"bassia component add -url https://github.com/myrepo/lib.git",
				"bassia component add -url https://github.com/myrepo/app.git -name app -references lib",
				"bassia component add -url ../upstream/ui -references lib:vendor/lib",
				"bassia component add -url https://github.com/myrepo/tool.git -unwind"
			],
			ComponentCommands.AddAsync, Positional: "url"),

		new("component", "create", "Create a new, empty component next to the meta-repo and register it in components.toml.",
			[
				new("name", "name", "The component's logical name.", Required: true),
				new("references", "component[:path],...", "Components this one nests, each at a subfolder (default: its name)."),
				new("branch", "branch", "Its main branch (default: main)."),
				new("url", "url", "The upstream it will be published to, recorded with it."),
				new("template", "template", "Start it from a template in .bassia/templates: its files, tags, visibility, exports, owners and references (switches given win)."),
				new("tags", "tag,...", "Its architecture tags (type:lib, scope:billing) that the boundary rules are written over."),
				new("visibility", "pattern,...", "Only components with a tag matching one of these patterns may reference it (default: public)."),
				new("owners", "owner,...", "Who owns it: people or teams."),
				new("exports", "pattern,...", "Its public API: path patterns (as in split plans) a referrer's code may use (default: everything).")
			],
			[
				"bassia component create -name docs",
				"bassia component create -name app -references lib,ui:vendor/ui",
				"bassia component create -name billing-api -references billing-core -tags type:app,scope:billing",
				"bassia component create -name parser -template lib"
			],
			ComponentCommands.CreateAsync, Positional: "name",
			Details: "Its history starts with one commit without files on its main branch, so an agentic run can select it at once with -select <name> " +
				"(the tip of its main branch) and write its first files. Tag a baseline with 'bassia component tag'. With -template, a second commit " +
				"adds the template's files (see 'bassia help template list')."),

		new("template", "list", "List the templates new components can start from (component create -template), with what each gives a component.",
			[],
			["bassia template list"],
			ComponentCommands.TemplateListAsync,
			Details: "A template is a folder .bassia/templates/<name>/ in the meta-repo: template.toml (description, tags, visibility, exports, " +
				"owners, references - each optional) and the starter files under files/. In file contents and paths, {{name}} becomes the new " +
				"component's name. Commit the folder in the meta-repo to share it."),

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

		new("component", "set", "Change a component's references (its dependencies), tags, visibility or owners; a cycle, an unregistered name or a new boundary violation is rejected and nothing changes.",
			[
				new("name", "component", "The component.", Required: true),
				new("references", "component[:path],...", "The components it nests, each at a subfolder (default: its name)."),
				new("clear-references", null, "Remove all its references."),
				new("tags", "tag,...", "Replace its architecture tags (type:lib, scope:billing)."),
				new("clear-tags", null, "Remove all its tags."),
				new("visibility", "pattern,...", "Only components with a tag matching one of these patterns may reference it."),
				new("public", null, "Anyone may reference it (removes its visibility)."),
				new("private", null, "Nobody may reference it (visibility = [])."),
				new("owners", "owner,...", "Replace its owners."),
				new("clear-owners", null, "Remove all its owners."),
				new("exports", "pattern,...", "Its public API: path patterns (as in split plans) a referrer's code may use."),
				new("clear-exports", null, "Export every path again.")
			],
			[
				"bassia component set -name app -references lib,ui:vendor/ui", "bassia component set -name app -clear-references",
				"bassia component set -name lib -tags type:lib,scope:shared", "bassia component set billing-core -visibility scope:billing -owners team-billing"
			],
			ComponentCommands.SetAsync, Positional: "name",
			Details: "Every component also has the implicit tag component:<name>. The boundary rules ('bassia help boundary') are checked on every " +
				"change to the graph: a change that adds a violation not already in the graph is refused, naming each one; violations already " +
				"there are reported as warnings."),

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
				new("references", "component[:path],...", "Components the fork nests (default: the parent's references)."),
				new("tags", "tag,...", "Its architecture tags (default: the parent's)."),
				new("visibility", "pattern,...", "Who may reference it (default: the parent's visibility)."),
				new("owners", "owner,...", "Who owns it (default: the parent's owners)."),
				new("exports", "pattern,...", "Its public API (default: the parent's exports).")
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

		// ----- users -----

		new("whoami", null, "Show who you are in this monorepo - the user your locks, runs, tags and commits are attributed to - and where Bassia got it from.",
			[],
			["bassia whoami"],
			UserCommands.WhoAmIAsync,
			Details: UserDetails),

		new("user", "list", "List the monorepo's users with how to reach them.",
			[],
			["bassia user list"],
			UserCommands.ListAsync),

		new("user", "show", "Show a user: name, email, contact, key, and the runs attributed to them.",
			[new("id", "user", "The user's id.", Required: true)],
			["bassia user show -id jk", "bassia user show jk"],
			UserCommands.ShowAsync, Positional: "id"),

		new("user", "add", "Register a user in the meta-repo (users.toml): the identity locks, runs, tags and commits are attributed to.",
			[
				new("id", "user", "A short, stable id (no whitespace, '/', ':', ',' or '@').", Required: true),
				new("name", "name", "Display name, as in a git author.", Required: true),
				new("email", "email", "Email address, as in a git author.", Required: true),
				new("contact", "text", "How else to reach them (chat handle, phone), shown to whoever waits for their locks."),
				new("key", "public key", "A public key (e.g. ssh-ed25519 ...), kept for signing.")
			],
			["bassia user add -id jk -name \"Jozef Kosik\" -email jozef@example.com -contact \"slack @jozef\""],
			UserCommands.AddAsync,
			Details: "Anyone may register themselves, also without being known yet; the registration is attributed to the user it adds. " + UserDetails),

		new("user", "set", "Change a registered user's name, email, contact or key.",
			[
				new("id", "user", "The user's id.", Required: true),
				new("name", "name", "New display name."),
				new("email", "email", "New email address."),
				new("contact", "text", "New contact details."),
				new("clear-contact", null, "Remove the contact details."),
				new("key", "public key", "New public key."),
				new("clear-key", null, "Remove the key.")
			],
			["bassia user set -id jk -contact \"teams: Jozef Kosik\""],
			UserCommands.SetAsync, Positional: "id"),

		new("user", "remove", "Remove a user from the registry; what was attributed to them keeps their id.",
			[new("id", "user", "The user's id.", Required: true)],
			["bassia user remove -id old-colleague"],
			UserCommands.RemoveAsync, Positional: "id"),

		new("user", "use", "Say who you are in this monorepo: records the user id in your own configuration layer (config.user.toml, not committed).",
			[new("id", "user", "A registered user's id.", Required: true)],
			["bassia user use -id jk", "bassia user use jk"],
			UserCommands.UseAsync, Positional: "id",
			Details: UserDetails),

		// ----- clones and views -----

		new("clone", null, "Clone a monorepo (the depot) as a cache: its meta-repo and each component's history without file content, which views and sparse runs download as they load files.",
			[
				new("from", "depot", "The monorepo to clone: its root folder, or a URL under which .bassia and each component are repositories.", Required: true),
				new("path", "directory", "Where to clone it (default: a folder named after the depot); empty or new."),
				new("lazy", null, "Import no component's history yet: a view imports the components it maps.")
			],
			["bassia clone -from /srv/monorepo -path work", "bassia clone -from https://git.example.com/mono -lazy"],
			DepotCommands.CloneAsync, Positional: "from",
			Details: ViewDetails),

		new("fetch", null, "Bring a clone up to its depot: the meta-repo (fast-forward) and every component's history, without file content.",
			[],
			["bassia fetch"],
			DepotCommands.FetchAsync,
			Details: "Components the depot registered since are imported (history only) unless the clone is lazy (depot.lazy). Views move to new " +
				"versions with 'bassia view update'."),

		// ----- servers -----

		new("login", null, "Log in to a Bassia server: in the browser (OIDC), with a code at GitHub, as a worker (OIDC client credentials), or with a token.",
			[
				new("server", "url", "The server, e.g. https://bassia.example.com (default: the server this clone is of)."),
				new("provider", "name", "The login to use when the server offers several (see its /api/v1/info)."),
				new("token", "token", "A token to log in with as it is: a worker token (bassia token create) or an admin's bootstrap token."),
				new("client-id", "id", "A worker's OIDC client id: log in with client credentials."),
				new("client-secret", "secret", "The OIDC client's secret."),
				new("scopes", "scopes", "The scopes client credentials ask for."),
				new("no-browser", null, "Print the login URL without opening a browser.")
			],
			["bassia login -server https://bassia.example.com", "bassia login -server https://bassia.example.com -provider github",
				"bassia login -server https://bassia.example.com -client-id worker -client-secret $SECRET"],
			ServerCommands.LoginAsync,
			Details: ServerDetails),

		new("logout", null, "Forget the login to a Bassia server.",
			[new("server", "url", "The server (default: the server this clone is of).")],
			["bassia logout -server https://bassia.example.com"],
			ServerCommands.LogoutAsync),

		new("credential", null, "Git's credential helper for Bassia servers: prints the login's token for the server git asks about.",
			[new("operation", "get|store|erase", "What git asks (only get answers).", Required: true)],
			["bassia credential get"],
			ServerCommands.CredentialAsync, Positional: "operation",
			Details: "Git calls it with the request (protocol=, host=, path= lines) on stdin. Bassia gives itself to git as the helper for the server of a " +
				"clone, so 'bassia clone', fetches and pushes need nothing set up; plain git needs it configured: " +
				"git config --global credential.https://bassia.example.com.helper '!bassia credential'."),

		new("token", "create", "Make a worker's token: 'run' at most, on the named components only, expiring.",
			[
				new("components", "name,...", "The components the worker may run on.", Required: true),
				new("minutes", "n", "How long the token is valid (default 120, at most 1440)."),
				new("server", "url", "The server (default: the server this clone is of).")
			],
			["bassia token create -components app,lib -minutes 60"],
			ServerCommands.TokenCreateAsync,
			Details: "A worker logs in with 'bassia login -token <token>' or sets BASSIA_TOKEN."),

		new("access", "list", "List a server's grants, groups and identity links (admins).",
			[new("server", "url", "The server (default: the server this clone is of).")],
			["bassia access list"],
			ServerCommands.AccessListAsync,
			Details: AccessDetails),

		new("access", "grant", "Grant a permission on a component (or every component) to a user, a group or everyone (admins).",
			[
				new("to", "principal", "A user id, group:<name>, or * for everyone.", Required: true),
				new("permission", "read|run|integrate|admin", "The permission; each includes the ones before it.", Required: true),
				new("component", "name", "The component (default: * - every component)."),
				new("server", "url", "The server (default: the server this clone is of).")
			],
			["bassia access grant -to bob -permission run -component app", "bassia access grant -to group:release -permission integrate"],
			ServerCommands.AccessGrantAsync,
			Details: AccessDetails),

		new("access", "revoke", "Revoke what a principal was granted on a component (admins).",
			[
				new("from", "principal", "A user id, group:<name>, or *.", Required: true),
				new("component", "name", "The component (default: *)."),
				new("server", "url", "The server (default: the server this clone is of).")
			],
			["bassia access revoke -from bob -component app"],
			ServerCommands.AccessRevokeAsync,
			Details: AccessDetails),

		new("access", "group", "Put a user in one of the server's groups, or take them out (-remove) (admins).",
			[
				new("group", "name", "The group.", Required: true),
				new("user", "id", "The registered user.", Required: true),
				new("remove", null, "Take the user out of the group."),
				new("server", "url", "The server (default: the server this clone is of).")
			],
			["bassia access group -group release -user alice"],
			ServerCommands.AccessGroupAsync,
			Details: AccessDetails),

		new("access", "link", "Link an identity at a provider (an OIDC issuer's subject, or a GitHub account id) to a registered user (admins).",
			[
				new("provider", "issuer|github", "The OIDC issuer URL, or github.", Required: true),
				new("subject", "id", "The identity's subject there (a worker client's id, a GitHub account's number).", Required: true),
				new("user", "id", "The registered user it is.", Required: true),
				new("server", "url", "The server (default: the server this clone is of).")
			],
			["bassia access link -provider https://example.okta.com -subject 0oa1worker -user build-bot"],
			ServerCommands.AccessLinkAsync,
			Details: AccessDetails),

		new("audit", null, "Show a server's audit log, newest first: who changed what, when, from where (admins).",
			[
				new("limit", "n", "How many entries (default 50)."),
				new("server", "url", "The server (default: the server this clone is of).")
			],
			["bassia audit -limit 20"],
			ServerCommands.AuditAsync),

		new("view", "create", "Map the components of a config spec onto a folder: each mapped component and what it references, side by side, with only the files the spec loads downloaded and written.",
			[
				new("name", "view", "The view's name.", Required: true),
				new("spec", "file|-", "The config spec (map, select and load rules), or - to read it from stdin.", Required: true),
				new("path", "directory", "Where to map it (default: <workspace>/view-<name>); empty or new, anywhere on this machine.")
			],
			["bassia view create -name parser -spec parser.spec", "bassia view create -name docs -spec - -path /tmp/docs"],
			ViewCommands.CreateAsync,
			Details: ViewDetails),

		new("view", "list", "List the views of this monorepo with their components and folders, as a table and as [[view]] entries.",
			[],
			["bassia view list"],
			ViewCommands.ListAsync),

		new("view", "show", "Show a view: its config spec and, per component, the version, commit, load rules and how many of its files are loaded.",
			[new("name", "view", "The view (default: the one the working directory is in).")],
			["bassia view show -name parser", "bassia view show"],
			ViewCommands.ShowAsync, Positional: "name"),

		new("view", "ls", "List every file a view maps - read from the history, downloading nothing - with whether it is loaded and its size when known.",
			[
				new("name", "view", "The view (default: the one the working directory is in)."),
				new("path", "component[/pattern]", "Only these files: a component (or a pattern of them) and a path pattern in it."),
				new("loaded", null, "Only the loaded files."),
				new("unloaded", null, "Only the files not loaded yet.")
			],
			["bassia view ls", "bassia view ls -path app/src -unloaded", "bassia view ls -name parser -path \"*/README.md\""],
			ViewCommands.LsAsync),

		new("view", "load", "Download and write more of a view: files, folders or path patterns, each relative to the working directory inside the view (or to the view's folder), as <component>/<path>.",
			[
				new("path", "path,...", "What to load: app/src/main.c, app/src (a folder), app/**/*.h; a path through a nested component (app/lib/x.h) loads it from that component.", Required: true),
				new("name", "view", "The view (default: the one the working directory is in - a view or a sparse run folder).")
			],
			["bassia view load -path app/src/parser.c", "bassia view load -path \"app/include/**/*.h,lib\"", "bassia view load lib/README.md"],
			ViewCommands.LoadAsync, Positional: "path",
			Details: "Every path must match a mapped file, or nothing is loaded. The load rule is recorded in the view, so 'view update' keeps it. " + ViewDetails),

		new("view", "update", "Move a view to what its config spec names now: fetch the mapped components from the depot, resolve the versions again and check out every component whose commit changed, keeping its load rules.",
			[new("name", "view", "The view (default: the one the working directory is in).")],
			["bassia view update -name parser", "bassia view update"],
			ViewCommands.UpdateAsync, Positional: "name",
			Details: "A component with uncommitted changes in the view stays where it is and is reported. A component that now maps differently (its references changed) needs the view made again."),

		new("view", "remove", "Remove a view: its folder and its registration.",
			[
				new("name", "view", "The view.", Required: true),
				new("force", null, "Remove it even though files in it were changed (the changes are lost).")
			],
			["bassia view remove -name parser"],
			ViewCommands.RemoveAsync, Positional: "name"),

		// ----- architectural boundaries -----

		new("boundary", "list", "List the boundary rules of components.toml - which tagged components may reference which - and the components' visibility.",
			[],
			["bassia boundary list"],
			BoundaryCommands.ListAsync,
			Details: BoundaryDetails),

		new("boundary", "add", "Add a boundary rule: components with a tag matching -source may reference only components with a tag matching -allow, and none matching -deny.",
			[
				new("source", "pattern", "The tag pattern of the components the rule constrains (* any characters, ? one character).", Required: true),
				new("allow", "pattern,...", "What they may reference: a tag of the referenced component must match one; $1, $2, ... are what -source's wildcards matched."),
				new("deny", "pattern,...", "What they may not reference: no tag of the referenced component may match one."),
				new("description", "text", "Why the rule exists.")
			],
			[
				"bassia boundary add -source type:app -allow type:lib,type:ui",
				"bassia boundary add -source scope:* -allow scope:$1,scope:shared -description \"scopes stay apart\"",
				"bassia boundary add -source type:lib -deny type:app"
			],
			BoundaryCommands.AddAsync,
			Details: "Adding a rule is never refused for the violations it finds in the existing graph: they are reported as warnings, and from " +
				"then on no change may add a new one. " + BoundaryDetails),

		new("boundary", "remove", "Remove a boundary rule by its number in 'bassia boundary list'.",
			[new("index", "n", "The rule's number (1 is the first).", Required: true)],
			["bassia boundary remove -index 2", "bassia boundary remove 2"],
			BoundaryCommands.RemoveAsync, Positional: "index"),

		new("boundary", "check", "Check every reference against the boundary rules and the components' visibility, and list the violations.",
			[
				new("name", "component", "Only the references from or to this component."),
				new("code", null, "Also scan each component's code at the tip of its default branch: exports, indirect use and paths that leave the component."),
				new("strict", null, "Fail (exit code 1) when there is any violation - for CI.")
			],
			["bassia boundary check", "bassia boundary check -strict", "bassia boundary check -name app", "bassia boundary check -code -strict"],
			BoundaryCommands.CheckAsync,
			Details: "Without -strict the violations are warnings and the command succeeds. " + BoundaryDetails + " " + CodeDetails),

		// ----- conformance -----

		new("conformance", "list", "List the conformance rules of components.toml: what components with given tags must have.",
			[],
			["bassia conformance list"],
			ConformanceCommands.ListAsync,
			Details: ConformanceDetails),

		new("conformance", "add", "Add a conformance rule: every component with a tag matching -components must have the files, tasks, owners or exports given.",
			[
				new("components", "pattern", "The tag pattern of the components the rule applies to (* any characters, ? one character; component:<name> for one).", Required: true),
				new("files", "path,...", "Paths that must exist at the tip of its default branch (a folder is met by any file below it)."),
				new("tasks", "task,...", "Tasks its bassia.toml must define."),
				new("owners", null, "It must have owners."),
				new("exports", null, "It must declare exports."),
				new("description", "text", "Why the rule exists.")
			],
			["bassia conformance add -components type:lib -files README.md -tasks test -owners", "bassia conformance add -components * -files LICENSE"],
			ConformanceCommands.AddAsync,
			Details: ConformanceDetails),

		new("conformance", "remove", "Remove a conformance rule by its number in 'bassia conformance list'.",
			[new("index", "n", "The rule's number (1 is the first).", Required: true)],
			["bassia conformance remove -index 1", "bassia conformance remove 1"],
			ConformanceCommands.RemoveAsync, Positional: "index"),

		new("conformance", "check", "Check every component against the conformance rules that apply to it, at the tip of its default branch.",
			[
				new("name", "component", "Only this component."),
				new("strict", null, "Fail (exit code 1) when anything is missing - for CI.")
			],
			["bassia conformance check", "bassia conformance check -strict", "bassia conformance check -name app"],
			ConformanceCommands.CheckAsync,
			Details: "Without -strict the findings are warnings and the command succeeds. " + ConformanceDetails),

		// ----- tasks -----

		new("task", "list", "List the components' tasks, as each one's bassia.toml defines them on its default branch.",
			[new("name", "component", "Only this component.")],
			["bassia task list", "bassia task list -name app"],
			TaskCommands.ListAsync, Positional: "name",
			Details: TaskDetails),

		new("task", "run", "Run tasks of the selected components, with what they depend on, in a checkout of the selection laid out as a run's (or in the current run's folder).",
			[
				new("task", "task,...", "The tasks to run (build, test, ...).", Required: true),
				new("select", "component[@tag|hash],...", "What to check out and run them in (default: every component at its main-branch tip; inside a run folder: the run's own checkout)."),
				new("keep", null, "Keep the checkout after the tasks passed (a failure always keeps it)."),
				new("no-cache", null, "Run every task, also those the cache could restore.")
			],
			["bassia task run -task build", "bassia task run -task test -select app,lib@v2", "bassia task run test"],
			TaskCommands.RunAsync, Positional: "task",
			Details: "The tasks run one after another, each in its component's folder, and stop at the first that fails; each one's output goes " +
				"to a log under <workspace>/.task-logs. The checkout is a folder under the workspace (tasks-<key>), removed when every task " +
				"passed unless -keep. Inside an agentic run (BASSIA_RUN_DIR set) and without -select, the tasks run on the run's own checkout, " +
				"as the agent left it. " + TaskDetails),

		new("task", "cache", "Show the task cache - where it is, how many results it holds and their size - or empty it.",
			[new("clear", null, "Remove every cached result.")],
			["bassia task cache", "bassia task cache -clear"],
			TaskCommands.CacheAsync,
			Details: "A task marked cache = true in its bassia.toml is restored from <workspace>/.task-cache instead of run while its key is " +
				"unchanged: its command, depends_on and outputs, the platform, and the content of its component and of every component it " +
				"references (their commits in a checkout made from commits, a hash of their files in a run folder). A restore copies back the " +
				"task's outputs and its log. Only passed runs are stored."),

		new("affected", null, "List the components a change affects: the components runs changed (or the ones named), and every component that references one of them, transitively.",
			[
				new("runs", "run-id,...|all", "The runs whose pushed results are the change."),
				new("components", "component,...", "The changed components.")
			],
			["bassia affected -runs brave-otter", "bassia affected -components lib"],
			TaskCommands.AffectedAsync,
			Details: "The result lists them in dependency order and gives the -select value covering their closure, e.g. for 'bassia task run " +
				"-task test -select <select>'. The integration's gate (merge.verify) checks the same set."),

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

		new("agent", "start", "Start an agentic run: materialize the selected components at their tags, hashes or default-branch tips in an isolated run folder, run the agent there, then commit, tag and push each changed component.",
			[
				new("select", "component[@tag|hash],...", "Every component of the run: a bare name starts from the tip of its default branch (resolved and recorded when the run starts), or add an annotated tag or a commit hash (6 to 40 lowercase hex digits, unambiguous in the component); must cover the components they reference. Required unless -spec is given."),
				new("spec", "file|-", "Run on a config spec's mapping instead of -select (see 'bassia help view create'): its components pinned at the commits their versions name, only the files its load rules match on disk."),
				new("load", "[!]component/pattern,...", "Make the run sparse: only these files are materialized (path patterns as in split plans; component/ alone is all of it); the agent loads more with 'bassia view load'."),
				new("detach", null, "Return at once and continue in the background; follow with agent show/logs/wait, stop with agent stop."),
				new("prompt", "text", "Compose the agent command from this prompt and the configured agent.command."),
				new("agent", "command", "With -prompt: the agent command to use instead of agent.command."),
				new("model", "model", "With -prompt: passed to the agent as --model."),
				new("effort", "level", "With -prompt: passed to the agent as --effort."),
				new("context", "text", "With -prompt: appended to the prompt as context."),
				new("lock-files", "[component:]pattern,...", "Also hold these files one by one while the agent changes them, on top of lock.files; a component: prefix limits a pattern to that component."),
				new("lock-folders", "[component:]pattern,...", "Also hold these folders as a whole while the agent changes a file in them, on top of lock.folders."),
				new("id", "run-id", "The run's id (set by -detach).", Hidden: true),
				Log,
				new("run", "command", "The agent command: the rest of the command line (put it last). It runs through the platform shell (/bin/sh -c, cmd /c) in the run folder; give it as one quoted argument to keep its own quotes and operators intact.", Rest: true)
			],
			[
				"bassia agent start -select app@v1,lib@v1 -prompt \"add a changelog\" -model opus",
				"bassia agent start -select app@v1,lib@v1 -detach -prompt \"fix the failing tests\"",
				"bassia agent start -select lib@v1 -run claude -p \"refactor the parser\" --permission-mode acceptEdits",
				"bassia agent start -select app@3f9c2e1,lib@v1 -prompt \"bisect the regression\"",
				"bassia agent start -select app,lib -prompt \"update the docs\"",
				"bassia agent start -select app,lib -lock-folders \"app:src/*\" -prompt \"rewrite the parser\"",
				"bassia agent start -spec parser.spec -prompt \"fix the parser's error messages\"",
				"bassia agent start -select app,lib -load app/src/parser,lib/include -prompt \"rename Token to Lexeme\""
			],
			RunCommands.StartAsync,
			Usage: "bassia agent start (-select <component[@tag|hash],...> [-load <[!]component/pattern,...>] | -spec <file|->) [-detach] [-lock-files <[component:]pattern,...>] [-lock-folders <[component:]pattern,...>] (-prompt <text> [-agent <command>] [-model <model>] [-effort <level>] [-context <text>] | -run <command...>)",
			Details: "Without -detach the agent's output streams to the terminal and the result follows when the run ends; Ctrl-C stops the run and records it as cancelled. " +
				"With -detach the selection is checked first, then the run continues in a background bassia process and the result (run_id, pid, log) is printed at once. " +
				"The agent runs in the run folder with BASSIA_ROOT, BASSIA_RUN_ID and BASSIA_RUN_DIR set. What it changes is held for the run as the lock " +
				"filter says (see 'bassia help lock list'); when the filter locks anything, the run folder gets an AGENTS.md and a CLAUDE.md explaining it. " +
				"A run with -spec or -load is sparse: the run folder is a view (see 'bassia help view'), its guide tells the agent how to load more, and in a " +
				"clone (depot.url) every result is pushed on to the depot as well."),

		new("agent", "list", "List runs newest first - recorded ones and those still preparing - as a table and as [[run]] entries.",
			[
				new("status", "status", "Only runs with this status: preparing, started, completed, partial, failed, cancelled, abandoned, or live / stale."),
				new("component", "component", "Only runs that include this component."),
				new("limit", "n", "At most this many (default: 20).")
			],
			["bassia agent list", "bassia agent list -status live", "bassia agent list -component lib -limit 5"],
			RunCommands.ListAsync),

		new("agent", "show", "Show a run: its record, per-component results, whether it is live, and its latest output line.",
			[Id],
			["bassia agent show -id brave-otter-3f2a91", "bassia agent show brave-otter-3f2a91"],
			RunCommands.ShowAsync, Positional: "id"),

		new("agent", "logs", "Print the captured output of a detached run (agent output and bassia's progress).",
			[Id, new("tail", "n", "The last n lines (default: 50; 0 for all).")],
			["bassia agent logs brave-otter-3f2a91", "bassia agent logs -id brave-otter-3f2a91 -tail 0"],
			RunCommands.LogsAsync, Positional: "id"),

		new("agent", "wait", "Wait until a run is no longer live; ok when it completed.",
			[Id, new("timeout", "seconds", "Give up after this long with timed_out = true (default: 0, wait as long as it takes).")],
			["bassia agent wait brave-otter-3f2a91", "bassia agent wait -id brave-otter-3f2a91 -timeout 600"],
			RunCommands.WaitAsync, Positional: "id"),

		new("agent", "stop", "Stop a live run (started from any shell, or detached): kill the agent's process tree, record the run as cancelled and keep its folder.",
			[Id, new("timeout", "seconds", "How long to wait for it to stop before killing its process (default: 30).")],
			["bassia agent stop brave-otter-3f2a91"],
			RunCommands.StopAsync, Positional: "id"),

		new("agent", "retry", "Retry the commit/tag/push steps of a run that finished partial.",
			[Id],
			["bassia agent retry brave-otter-3f2a91"],
			RunCommands.RetryAsync, Positional: "id"),

		new("agent", "abandon", "Discard a finished run's folder and record it as abandoned; results already pushed stay where they are.",
			[Id],
			["bassia agent abandon brave-otter-3f2a91"],
			RunCommands.AbandonAsync, Positional: "id"),

		new("agent", "diff", "Show what a run changed in each component: files, insertions and deletions, a diffstat, and the patch with -patch.",
			[Id, new("component", "component", "Only this component."), new("patch", null, "Include the full patch.")],
			["bassia agent diff brave-otter-3f2a91", "bassia agent diff -id brave-otter-3f2a91 -component app -patch"],
			RunCommands.DiffAsync, Positional: "id"),

		// ----- locks -----

		new("lock", "list", "List what runs hold - files and folders, live, released (results not landed yet) or expired - and which runs wait for what, as a table and as [[hold]] and [[wait]] entries.",
			[new("component", "component", "Only holds in this component."), new("run", "run-id", "Only this run's holds and waits.")],
			["bassia lock list", "bassia lock list -component app", "bassia lock list -run brave-otter-3f2a91"],
			LockCommands.ListAsync, Details: LockDetails),

		new("lock", "check", "Tell whether paths are free to change: which live runs hold them, and which finished runs' results changed them that the run's checkout lacks (behind).",
			[LockPaths, LockComponent, LockRun],
			["bassia lock check -path app/src/parser.cs", "bassia lock check app/src/,lib/README.md", "bassia lock check -path src/parser.cs -component app"],
			LockCommands.CheckAsync, Positional: "path", Details: LockDetails),

		new("lock", "claim", "Hold paths for a run before its agent changes them, so other runs wait for it; all or none, failing with the runs that hold them.",
			[LockPaths, LockComponent, LockRun],
			["bassia lock claim -path app/src/parser.cs", "bassia lock claim app/src/parser/"],
			LockCommands.ClaimAsync, Positional: "path", Details: LockDetails),

		new("lock", "wait", "Wait until no other run holds the paths, then hold them for the run and bring the results of the runs that changed them into its checkout; re-read the files it lists under changed.",
			[
				LockPaths, LockComponent, LockRun,
				new("timeout", "seconds", "Give up after this long with timed_out = true (default: 0, wait as long as it takes)."),
				new("no-refresh", null, "Only report what changed; do not bring the other runs' results in.")
			],
			["bassia lock wait -path app/src/parser.cs", "bassia lock wait app/src/parser.cs -timeout 600"],
			LockCommands.WaitAsync, Positional: "path",
			Details: "A wait that would deadlock (the holder waits, directly or through others, for something this run holds) fails at once. " + LockDetails),

		new("lock", "release", "Let go of a run's holds before it ends; -force breaks the holds of another live run (a stuck one).",
			[
				LockPaths with { Required = false },
				new("all", null, "Everything the run holds (and, for a finished run, the results it is remembered for)."),
				LockComponent, LockRun,
				new("force", null, "Break the holds of another run although it is live.")
			],
			["bassia lock release -path app/src/parser.cs", "bassia lock release -all", "bassia lock release -run brave-otter-3f2a91 -all -force"],
			LockCommands.ReleaseAsync, Details: LockDetails),

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
				new("resolve", "command", "The resolver command instead of integration.resolver: the rest of the command line (put it last). It runs through the platform shell; give it as one quoted argument to keep its own quotes intact.", Rest: true)
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

		// ----- skills -----

		new("skill", "list", "List the meta-repo's skills (.bassia/skills/<name>/SKILL.md): reusable instructions for recurring tasks, for the agent that drives bassia.",
			[],
			["bassia skill list"],
			SkillCommands.SkillListAsync),

		new("skill", "show", "Show one skill: its description and instructions.",
			[new("name", "skill", "The skill.", Required: true)],
			["bassia skill show -name release", "bassia skill show release"],
			SkillCommands.SkillShowAsync, Positional: "name"),

		// ----- frontends -----

		new("web", null, "Serve the read-only web dashboard on 127.0.0.1 until Ctrl-C: the component map, tags across components, live and past runs, the merge queue with its triage and the merges needing attention, and the configuration layers.",
			[new("port", "n", "The port to try first (default: 8080; the next free one is used when taken)."), new("no-open", null, "Do not open the browser.")],
			["bassia web", "bassia web -port 9000 -no-open"],
			WebCommand.RunAsync),

		new("help", null, "Show help: the concepts, the usual workflow and every command; every command in full (-all); the subcommands of a command; or one command's switches and examples.",
			[
				new("command", "command [subcommand]", "The command to explain; may be given without -command."),
				new("all", null, "Every command in full - usage, details, switches and examples - in one result.")
			],
			["bassia help", "bassia help -all", "bassia help agent", "bassia help agent start", "bassia agent start -help"],
			Help.RunAsync, Positional: "command")
	];

	/// <summary>The groups (<c>run</c>, <c>component</c>, ...) and top-level commands, in table order.</summary>
	public static IReadOnlyList<string> Names => Commands.Select(command => command.Name).Distinct().ToList();

	public static IReadOnlyList<CommandSpec> Group(string name) =>
		Commands.Where(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
}
