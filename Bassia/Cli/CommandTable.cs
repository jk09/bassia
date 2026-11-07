namespace Bassia.Cli;

using System.CommandLine;
using Bassia.CliCommands;

/// <summary>
/// Every command of the <c>bassia</c> command line. The table is the single source of what is accepted, what help
/// says and what an error suggests; handlers live with the model they drive.
/// </summary>
internal static class CommandTable
{
	private const string Graph = "Bassia is a client-server monorepo now: there are no components, branches or tags. ";

	/// <summary>Commands that were removed or replaced, with what to use instead.</summary>
	public static readonly IReadOnlyDictionary<string, string> Replaced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["component"] = Graph + "The monorepo is one tree; import a git repository into a folder of it with 'bassia import -git <url>'.",
		["graph"] = Graph + "List the tree with 'bassia mainline ls'.",
		["tag"] = Graph + "Mainline is numbered by seq: 'bassia mainline log', 'bassia mainline show -seq <n>'.",
		["branch"] = Graph + "Open a workspace instead ('bassia workspace create <name>'); stack ('-on') or clone ('workspace clone') workspaces for branch-like work.",
		["commit"] = Graph + "Commits are implicit: every command that reads a workspace snapshots it.",
		["integration"] = Graph + "Workspaces land on mainline: 'bassia workspace check' and 'bassia workspace land'.",
		["integrate"] = Graph + "Workspaces land on mainline: 'bassia workspace check' and 'bassia workspace land'.",
		["lock"] = Graph + "Leases replaced locks: 'bassia lease list|acquire|renew|release'.",
		["view"] = Graph + "A workspace is the view: 'bassia workspace create <name>'.",
		["boundary"] = Graph + "Architectural boundaries between components are gone with the components.",
		["conformance"] = Graph + "Conformance rules over components are gone with the components.",
		["template"] = Graph + "Component templates are gone with the components.",
		["task"] = Graph + "Run builds and tests in a workspace's folder; the land.validate setting runs them on semantic merges.",
		["affected"] = Graph + "'bassia mainline changed -since <seq>' lists what changed.",
		["run"] = "'bassia run' was replaced by 'bassia agent': use 'bassia agent start -prompt <text>'.",
		["log"] = "'bassia log' is now 'bassia mainline log' (landings) or 'bassia workspace log' (a workspace's operations).",
		["fetch"] = "A clone reads from its server on demand; there is nothing to fetch.",
		["tutorial"] = "Run 'bassia help' for the concepts and the usual workflow.",
		["skill"] = "Skills lived in the old meta-repo; keep them in the tree and read them in a workspace.",
		["user"] = "Who acts comes from BASSIA_USER or 'bassia config set -key user.id -value <id>'.",
		["whoami"] = "Run 'bassia config get -key user.id' (or BASSIA_USER).",
		["ui"] = "The web UI is Bassia Studio: 'bassia-studio -server <url> -token <t>' over a bassia-server.",
		["web"] = "'bassia web' was removed: the web UI is Bassia Studio ('bassia-studio -server <url> -token <t>' over a bassia-server).",
		["prompt"] = "bassia has no LLM of its own: drive it from your agent, which learns it from 'bassia help -all'."
	};

	private static Option<string> WorkspaceSwitch() => Value("workspace", "workspace", "The workspace: its name, id, or a unique prefix of either (4+ characters). Default: the workspace the current folder is in.");
	private static Option<string> RunId() => Value("id", "run-id", "The run: its id (run-<key>), a unique prefix of its key (4+ characters), or its workspace's name.", Required: true);

	private static BassiaCommand[] Definitions() =>
	[
		// ----- monorepo -----
		Cmd("init", "Create a local monorepo: the version core runs in-process over a local-disk blob store (a stand-in for cloud storage) and a local PostgreSQL (state and trees).",
			[Value("path", "dir", "The folder (new or empty). Default: the current folder."),
				Value("postgres", "connection", "The local PostgreSQL connection string (else BASSIA_POSTGRES); the monorepo gets a schema of its own.")],
			["bassia init -path R:\\acme", "bassia init"], MonorepoCommands.InitAsync, Positional: "path"),
		Cmd("clone", "Make this machine a client of a bassia-server's monorepo. Nothing is downloaded until something reads it.",
			[Value("from", "url", "The server's URL.", Required: true), Value("path", "dir", "The folder (new or empty). Default: named after the server."),
				Value("token", "token", "The server's shared token, when it requires one (else BASSIA_SERVER_TOKEN)."),
				Value("postgres", "connection", "The local PostgreSQL connection string (else BASSIA_POSTGRES): the clone keeps the trees its work touches there; without one it keeps them in memory for each command.")],
			["bassia clone -from http://bassia.example.com:8080 -path acme"], MonorepoCommands.CloneAsync, Positional: "from"),
		Cmd("status", "Whether the current folder is in a monorepo: mainline's seq, the open workspaces, live runs and what needs a replay.",
			[], ["bassia status", "bassia -C R:\\acme status -human"], MonorepoCommands.StatusAsync),
		Cmd("version", "The version of bassia.", [], ["bassia version"], MonorepoCommands.VersionAsync),
		Cmd("help", "Explain bassia: the concepts, the workflow, every command; or one command in full.",
			[Value("command", "command", "A command (and subcommand) to explain."), Flag("all", "Every command in full, in one result.")],
			["bassia help", "bassia help -all", "bassia help workspace land"], Help.RunAsync, Positional: "command", Words: true),

		Group("config", "Show and change the settings of this monorepo folder.",
		[
			Cmd("list", "Every setting of this monorepo folder with its value and where it comes from.", [], ["bassia config list"], MonorepoCommands.ConfigListAsync),
			Cmd("get", "One setting's value.", [Value("key", "key", "The setting.", Required: true)], ["bassia config get -key agent.command"],
				MonorepoCommands.ConfigGetAsync, Positional: "key"),
			Cmd("set", "Change a setting of this monorepo folder.", [Value("key", "key", "The setting.", Required: true), Value("value", "value", "The value.", Required: true)],
				["bassia config set -key agent.model -value opus", "bassia config set -key land.validate -value \"dotnet test\""], MonorepoCommands.ConfigSetAsync),
			Cmd("unset", "Return a setting to its default.", [Value("key", "key", "The setting.", Required: true)], ["bassia config unset -key agent.model"],
				MonorepoCommands.ConfigUnsetAsync, Positional: "key"),
		]),

		// ----- workspaces -----
		Group("workspace", "Open, inspect, sync and land workspaces: where work happens instead of a branch.",
		[
			Cmd("create", "Open a workspace over the monorepo: a folder holding mainline's newest tree (or another workspace's work, with -on).",
				[Value("name", "name", "The workspace's name.", Required: true), Value("path", "dir", "Its folder (new or empty). Default: <monorepo>/workspaces/<name>."),
					Value("on", "workspace", "Stack it on this open workspace instead of mainline: it starts from that workspace's work and lands after it."),
					Value("intent", "text", "What the change is for: the message its landing records (also set by 'workspace describe')."),
					List("load", "rule,...", "What the folder loads (fetched, cached and written): paths from the root such as api/, *.md, !api/testdata/, or @profile from .bassia.toml [load.profiles]; all for everything. Default: workspace.load, else .bassia.toml [load] default, else everything.")],
				["bassia workspace create fix-login", "bassia workspace create fix-login-tests -on fix-login", "bassia workspace create spike -path /tmp/spike",
					"bassia workspace create api-fix -load api/,libs/shared/,*"],
				WorkspaceCommands.CreateAsync, Positional: "name",
				Details: "Instead of branches, every agentic run gets a workspace. Workspaces never contend with each other: each has its own operation log. " +
					"Only landing contends, on mainline."),
			Cmd("clone", "A new workspace with another workspace's base and work, as a new change: branch off a workspace.",
				[Value("from", "workspace", "The workspace to clone.", Required: true), Value("name", "name", "The new workspace's name.", Required: true),
					Value("path", "dir", "Its folder. Default: <monorepo>/workspaces/<name>."), List("load", "rule,...", "What the folder loads: paths from the root such as api/, *.md, !api/testdata/, or @profile from .bassia.toml [load.profiles]; all for everything. Default: workspace.load, else .bassia.toml [load] default, else everything.")],
				["bassia workspace clone -from fix-login -name fix-login-alt"], WorkspaceCommands.CloneAsync),
			Cmd("checkout", "Give a workspace a folder on this machine (one created elsewhere, or whose folder was removed).",
				[Value("workspace", "workspace", "The workspace.", Required: true), Value("path", "dir", "The folder. Default: <monorepo>/workspaces/<name>."),
					List("load", "rule,...", "What the folder loads: paths from the root such as api/, *.md, !api/testdata/, or @profile from .bassia.toml [load.profiles]; all for everything. Default: workspace.load, else .bassia.toml [load] default, else everything.")],
				["bassia workspace checkout fix-login", "bassia workspace checkout fix-login -load @web"], WorkspaceCommands.CheckoutAsync, Positional: "workspace"),
			Cmd("load", "Show or change what a workspace's folder loads: write the paths new rules add, remove the ones they drop (after a snapshot).",
				[WorkspaceSwitch(), List("load", "rule,...", "The new rules: paths from the root such as api/, *.md, !api/testdata/, or @profile from .bassia.toml [load.profiles]; all for everything. Default: workspace.load, else .bassia.toml [load] default, else everything.")],
				["bassia workspace load", "bassia workspace load fix-login -load api/,docs/", "bassia workspace load fix-login -load all"], WorkspaceCommands.LoadAsync,
				Positional: "workspace",
				Details: "A workspace's folder holds only what its load rules select, like a ClearCase snapshot view's load rules: the clone fetches and caches the " +
					"content of those paths only, and directories not loaded appear empty. The workspace's tree still holds everything: snapshots never read an " +
					"unloaded path as deleted, and sync and landing carry it along unchanged. Rules are tried in order and the last match decides."),
			Cmd("list", "The open workspaces (all with -all): status, base seq, what they are stacked on, their run and last landing.",
				[Flag("all", "Also landed, closed, replaced and abandoned workspaces.")], ["bassia workspace list", "bassia workspace list -all -human"],
				WorkspaceCommands.ListAsync),
			Cmd("show", "One workspace in full: its record, run and last landing, and a summary of its changes.", [WorkspaceSwitch()],
				["bassia workspace show fix-login"], WorkspaceCommands.ShowAsync, Positional: "workspace"),
			Cmd("status", "Snapshot a workspace and list what it changed against its base, conflicts included.", [WorkspaceSwitch()],
				["bassia workspace status", "bassia workspace status fix-login -human"], WorkspaceCommands.StatusAsync, Positional: "workspace",
				Details: "Only files whose size or modification time changed are hashed; listing and statistics come from tree metadata, never from file content."),
			Cmd("diff", "What a workspace changed against its base: per path, or with -patch as unified hunks (rewritten files as a summary).",
				[WorkspaceSwitch(), Flag("patch", "Include line hunks (computed now; a file sharing under half its lines with the old version is shown as rewritten)."),
					Value("path", "path", "Only paths under this one.")],
				["bassia workspace diff fix-login", "bassia workspace diff -patch -path src"], WorkspaceCommands.DiffAsync, Positional: "workspace"),
			Cmd("snapshot", "Record the workspace's folder now. Rarely needed: every command that reads a workspace snapshots it first.", [WorkspaceSwitch()],
				["bassia workspace snapshot"], WorkspaceCommands.SnapshotAsync, Positional: "workspace"),
			Cmd("describe", "Set what the workspace's change is for: the intent its landing records.",
				[WorkspaceSwitch(), Value("intent", "text", "The intent.", Required: true)], ["bassia workspace describe -intent \"paginate the list endpoint\""],
				WorkspaceCommands.DescribeAsync),
			Cmd("check", "Whether the workspace can land on mainline now, and if not, which paths conflict and what to do.",
				[WorkspaceSwitch(), Flag("strict", "Also refuse when a path under -reads changed since the workspace's base."),
					List("reads", "prefix,...", "The read set for -strict: what the change relied on."),
					List("lease-token", "id:token,...", "Leases to present (from 'lease acquire').")],
				["bassia workspace check fix-login", "bassia workspace check -strict -reads src/Api"], WorkspaceCommands.CheckAsync, Positional: "workspace",
				Details: "Outcomes: would-land, nothing (no changes), needs-replay (mainline rewrote a path the workspace changed; replay the task or sync and resolve), " +
					"conflict (unresolved conflicts in the workspace), rejected (stacked on an open workspace, or missing leases)."),
			Cmd("land", "Land the workspace on mainline: its changes rebased onto the head, as the next seq, keeping its ChangeId.",
				[WorkspaceSwitch(), Flag("keep", "Keep the workspace open afterwards, on the new head with a fresh change."),
					Flag("strict", "Refuse when a path under -reads changed since the workspace's base."), List("reads", "prefix,...", "The read set for -strict."),
					List("lease-token", "id:token,...", "Leases to present; required where .bassia.toml says lease_required.")],
				["bassia workspace land fix-login", "bassia workspace land -keep"], WorkspaceCommands.LandAsync, Positional: "workspace",
				Details: "Only the paths the workspace touched are compared with mainline. Disjoint changes land without any merge; a path both rewrote follows its " +
					"conflict policy in .bassia.toml (replay by default: rejected, re-run the task on the new head)."),
			Cmd("sync", "Rebase the workspace onto its newest base (mainline's head, or the workspace it is stacked on); conflicts are written with markers.",
				[WorkspaceSwitch()], ["bassia workspace sync fix-login"], WorkspaceCommands.SyncAsync, Positional: "workspace"),
			Cmd("undo", "Undo the workspace's last operation (snapshot, sync, ...); the folder follows. Undo again to redo.", [WorkspaceSwitch()],
				["bassia workspace undo"], WorkspaceCommands.UndoAsync, Positional: "workspace"),
			Cmd("restore", "Return the workspace to an earlier operation of its log.",
				[WorkspaceSwitch(), Value("op", "operation", "The operation: its seq in the log, or 4+ characters of its id.", Required: true)],
				["bassia workspace restore -op 3", "bassia workspace restore fix-login -op 9f2c41"], WorkspaceCommands.RestoreAsync, Positional: "workspace"),
			Cmd("log", "The workspace's operations, newest first.", [WorkspaceSwitch(), Value("limit", "n", "At most this many (default 50).")],
				["bassia workspace log fix-login"], WorkspaceCommands.LogAsync, Positional: "workspace"),
			Cmd("close", "Close a workspace (or -abandon it); its operations stay recoverable until retention ends.",
				[WorkspaceSwitch(), Flag("abandon", "Mark it abandoned rather than closed."), Flag("remove", "Also delete its folder on this machine.")],
				["bassia workspace close spike -abandon -remove"], WorkspaceCommands.CloseAsync, Positional: "workspace"),
		]),

		// ----- mainline -----
		Group("mainline", "Read mainline, the single trunk, and put an earlier seq back.",
		[
			Cmd("log", "Mainline's landings, newest first: seq, commit, change, author, intent.",
				[Value("limit", "n", "At most this many (default 30)."), Value("path", "path", "Only landings that touched this path.")],
				["bassia mainline log", "bassia mainline log -path src/Billing -limit 10"], MainlineCommands.LogAsync),
			Cmd("show", "One landing: its commit, intent, recipe and changes (with -patch, hunks).",
				[Value("seq", "n", "The landing (default: the head)."), Flag("patch", "Include line hunks.")],
				["bassia mainline show -seq 12 -patch"], MainlineCommands.ShowAsync, Positional: "seq"),
			Cmd("ls", "List a folder of mainline (from tree metadata: names, kinds and sizes, no file content).",
				[Value("path", "path", "The folder (default: the root)."), Value("seq", "n", "As of this seq (default: the head).")],
				["bassia mainline ls src", "bassia mainline ls -seq 3"], MainlineCommands.LsAsync, Positional: "path"),
			Cmd("cat", "A file of mainline.", [Value("path", "path", "The file.", Required: true), Value("seq", "n", "As of this seq (default: the head).")],
				["bassia mainline cat README.md"], MainlineCommands.CatAsync, Positional: "path"),
			Cmd("changed", "The paths that changed after a seq, from the path index (what a workspace based there should re-check).",
				[Value("since", "seq", "Changes after this seq.", Required: true), Value("prefix", "path", "Only under this folder.")],
				["bassia mainline changed -since 40 -prefix src"], MainlineCommands.ChangedAsync),
			Cmd("restore", "Put an earlier seq's tree back on mainline, as a new landing (history stays linear).",
				[Value("seq", "n", "The seq whose tree to restore.", Required: true), Value("intent", "text", "The landing's intent.")],
				["bassia mainline restore -seq 41"], MainlineCommands.RestoreAsync, Positional: "seq"),
		]),

		// ----- leases -----
		Group("lease", "Advisory holds on path prefixes, with fencing tokens.",
		[
			Cmd("list", "The live leases: prefix, mode, workspace, fencing token, expiry.", [Value("prefix", "path", "Only leases overlapping this prefix.")],
				["bassia lease list"], LeaseCommands.ListAsync),
			Cmd("acquire", "Lease a path prefix for a workspace: exclusive (others wait) or intent (announce work). Present the token when landing.",
				[Value("prefix", "path", "The prefix.", Required: true), WorkspaceSwitch(), Value("mode", "exclusive|intent", "Default exclusive."),
					Value("ttl", "seconds", "Lifetime unless renewed (default lease.ttl)."), Value("wait", "seconds", "Wait in line this long when it is taken (default 0).")],
				["bassia lease acquire -prefix src/Billing -wait 600"], LeaseCommands.AcquireAsync, Positional: "prefix",
				Details: "Exclusive on src/Billing/ also takes intent on src/ and the root; exclusive leases on disjoint prefixes never block each other. " +
					"Each grant carries a monotonic fencing token: a landing presenting a token whose lease expired and was re-granted is refused."),
			Cmd("renew", "Extend a lease.", [Value("id", "lease-id", "The lease.", Required: true), Value("token", "n", "Its fencing token.", Required: true),
				Value("ttl", "seconds", "New lifetime (default lease.ttl).")], ["bassia lease renew -id 5f... -token 12"], LeaseCommands.RenewAsync),
			Cmd("release", "Give a lease up.", [Value("id", "lease-id", "The lease.", Required: true), Value("token", "n", "Its fencing token.", Required: true)],
				["bassia lease release -id 5f... -token 12"], LeaseCommands.ReleaseAsync),
		]),

		// ----- agentic runs -----
		Group("agent", "Run coding agents in workspaces of their own, and replay their tasks.",
		[
			Cmd("start", "Run a coding agent in a workspace of its own: snapshot its result, record the run, and with -land land it.",
				[Value("prompt", "text", "The task for the agent (agent.command gets it as its last argument)."),
					Value("workspace", "workspace", "Run in this open workspace instead of a new one."),
					Value("name", "name", "The new workspace's name (default: from the prompt)."), Value("on", "workspace", "Stack the new workspace on this one."),
					Value("model", "model", "The model (default agent.model)."), Flag("land", "Land the workspace when the agent succeeds."),
					Flag("keep", "With -land: keep the workspace open on the new head."),
					List("lease", "prefix,...", "Lease these prefixes exclusively for the run (renewed while it runs, presented when landing)."),
					List("load", "rule,...", "What the run's workspace loads: paths from the root such as api/, *.md, !api/testdata/, or @profile from .bassia.toml [load.profiles]; all for everything. Default: workspace.load, else .bassia.toml [load] default, else everything."),
					Value("lease-wait", "seconds", "Wait this long in line for the leases (default 0)."),
					Flag("detach", "Run in the background and return at once with the run id."),
					Rest("run", "command", "Run this command instead of the agent with a prompt."),
					Value("run-id", "id", "", Hidden: true), Flag("background", "", Hidden: true)],
				["bassia agent start -prompt \"paginate the list endpoint\" -land", "bassia agent start -detach -lease src/Billing -prompt \"fix rounding\"",
					"bassia agent start -name smoke -run ./scripts/smoke.sh"],
				AgentCommands.StartAsync,
				Details: "The task is recorded on the workspace's change as its replay recipe. When the landing reports needs-replay, 'bassia agent replay' runs " +
					"the same task on mainline's head in a new workspace, keeping the change's ChangeId. Text is never merged behind the agent's back."),
			Cmd("list", "Every run, newest first: status, workspace, timing, landing.", [Value("status", "status", "Only running, succeeded, failed, stopped or lost runs.")],
				["bassia agent list", "bassia agent list -status running"], AgentCommands.ListAsync),
			Cmd("show", "One run and its workspace.", [RunId()], ["bassia agent show brave-otter"], AgentCommands.ShowAsync, Positional: "id"),
			Cmd("logs", "A run's output (live while it runs).", [RunId(), Value("tail", "n", "Only the last n lines.")], ["bassia agent logs brave-otter -tail 20"],
				AgentCommands.LogsAsync, Positional: "id"),
			Cmd("wait", "Wait until a run ends, and has landed when started with -land; ok when it succeeded.", [RunId(), Value("timeout", "seconds", "Give up after this long (default 3600).")],
				["bassia agent wait brave-otter -timeout 1800"], AgentCommands.WaitAsync, Positional: "id"),
			Cmd("stop", "Stop a run on this machine; what its agent changed so far is snapshotted.", [RunId()], ["bassia agent stop brave-otter"],
				AgentCommands.StopAsync, Positional: "id"),
			Cmd("replay", "Rebase by replay: run a workspace's recorded task again on mainline's head, in a new workspace with the same ChangeId.",
				[Value("id", "run|workspace", "The run or workspace to replay.", Required: true), Flag("land", "Land the replay when the agent succeeds."),
					List("lease", "prefix,...", "Lease these prefixes for the replay."), Value("lease-wait", "seconds", "Wait this long for the leases."),
					List("load", "rule,...", "What the replay loads (default: what the original loaded here): paths from the root such as api/, *.md, !api/testdata/, or @profile from .bassia.toml [load.profiles]; all for everything. Default: workspace.load, else .bassia.toml [load] default, else everything."),
					Value("run-id", "id", "", Hidden: true)],
				["bassia agent replay fix-login -land"], AgentCommands.ReplayAsync, Positional: "id"),
			Cmd("job", "Run an agentic job in a container: clone the server, then start or replay a run (what Bassia Studio's cloud runs execute).",
				[Value("from", "url", "The server (default BASSIA_JOB_SERVER)."), Value("token", "token", "The server's token (default BASSIA_JOB_TOKEN)."),
					Value("path", "folder", "Where to clone (default BASSIA_JOB_DIR, else a temporary folder).")],
				["BASSIA_JOB_SERVER=https://bassia.example BASSIA_JOB_ARGS='[\"agent\",\"start\",\"-prompt\",\"fix the build\",\"-land\"]' bassia agent job"],
				AgentCommands.JobAsync,
				Details: "The command comes from BASSIA_JOB_ARGS, a JSON array beginning with agent start or agent replay. With BASSIA_REPORT_URL and " +
					"BASSIA_REPORT_TOKEN set, the run posts its output there as it happens and stops when the answer asks it to."),
		]),

		// ----- interop and maintenance -----
		Cmd("import", "Import git repositories, each into its own top-level folder: as a baseline commit each, or with first-parent history.",
			[List("git", "url|path,...", "The repositories (URLs or local paths).", Required: true),
				List("prefix", "folder,...", "The folder per repository (default: the repository's name)."),
				Flag("history", "Import first-parent history, interleaved across repositories by commit time."),
				Value("limit", "n", "With -history: at most the newest n commits per repository (default 1000).")],
			["bassia import -git https://github.com/acme/api.git,https://github.com/acme/web.git", "bassia import -git ../lib -prefix libs/core -history"],
			MonorepoCommands.ImportAsync,
			Details: "Every imported commit lands through the normal landing path from an import workspace. Submodules are skipped (listed in 'skipped')."),
		Cmd("export", "Export mainline into a git repository with git fast-import, incrementally: one git commit per landing, with a Change-Id trailer.",
			[Value("git", "dir", "The git repository (created if missing).", Required: true), Value("branch", "name", "The branch (default main).")],
			["bassia export -git ../acme-mirror"], MonorepoCommands.ExportAsync),
		Cmd("gc", "Sweep blobs no retained operation reaches (older than the grace period); prune closed workspaces past retention and the clone cache.",
			[Flag("dry-run", "Only report."), Value("grace-hours", "n", "Default gc.grace_hours."), Value("retention-days", "n", "Default gc.retention_days.")],
			["bassia gc -dry-run", "bassia gc"], MonorepoCommands.GcAsync),
		Cmd("scrub", "Re-hash stored blobs and report any whose bytes do not match their id.", [Value("sample", "n", "Check only n random blobs.")],
			["bassia scrub -sample 1000"], MonorepoCommands.ScrubAsync),
		Group("devdrive", "Windows Dev Drive support for block-cloned workspaces.",
		[
			Cmd("status", "Whether workspaces in a folder are block-cloned on a Windows Dev Drive (copy-on-write, no bytes written), and why not.",
				[Value("path", "dir", "The folder (default: where workspaces go, workspace.dir).")],
				["bassia devdrive status", "bassia devdrive status -path D:\\acme\\workspaces"], MonorepoCommands.DevDriveStatusAsync, Positional: "path",
				Details: "On a Dev Drive (Windows 11 24H2 / Windows Server 2025 or later; any ReFS volume with block cloning), Bassia keeps one copy of each " +
					"file's content in .bassia/clones and block-clones it into workspaces on the same volume: creating a workspace costs metadata, not the " +
					"files' bytes, and an edited file reallocates only what was written. Put the monorepo folder on the Dev Drive; workspace.clone = off " +
					"turns it off. Files under 8 KiB are written directly. Creating a Dev Drive needs an administrator: Settings > System > Storage > Disks & " +
					"volumes > Create dev drive, or Format-Volume -DevDrive."),
		]),

		// ----- feedback -----
		Group("feedback", "Send feedback to the Bassia project and choose what happens when bassia crashes.",
		[
			Cmd("send", "Send feedback - a bug, an idea or a question - as an issue in Bassia's GitHub repository, after showing it and asking you to confirm.",
				[Value("kind", "bug|idea|question", "What it is (default: bug)."), Flag("yes", "Confirm in advance, without asking."),
					Rest("message", "text", "The feedback: the rest of the command line (put it last); its first line becomes the issue title. May be given without -message.", Required: true)],
				["bassia feedback send workspace check is slow on a big tree", "bassia feedback send -kind idea -yes let land take a glob"],
				FeedbackCommands.SendAsync, Positional: "message",
				Usage: "bassia feedback send [-kind <bug|idea|question>] [-yes] <message...>",
				Details: "The issue is filed in jk09/bassia with the token in BASSIA_FEEDBACK_TOKEN (else GITHUB_TOKEN). Nothing is sent without confirmation; " +
					"a submission that fails is kept and can be retried with 'feedback pending -send'."),
			Cmd("reports", "Show or choose what happens when Bassia crashes: off (nothing is sent), ask (show the report and ask) or auto (file it without asking).",
				[Value("mode", "off|ask|auto", "The new choice.")], ["bassia feedback reports", "bassia feedback reports -mode auto"], FeedbackCommands.ReportsAsync,
				Details: "The choice is kept per user in feedback.toml under BASSIA_HOME (the installer's folder)."),
			Cmd("pending", "List the approved reports that could not be sent yet (no token, no network), or send them.",
				[Flag("send", "Try to send them now.")], ["bassia feedback pending", "bassia feedback pending -send"], FeedbackCommands.PendingAsync)
		]),
	];

	// ----- the library's global options, accepted anywhere on the command line -----

	/// <summary>Print the result for a person (Markdown), not as TOML.</summary>
	public static readonly Option<bool> Human = new SwitchOption<bool>("human", null, "Print the result as Markdown for a person instead of TOML.") { Recursive = true };

	/// <summary>Explain the command instead of running it (<c>-h</c>, <c>-help</c>, <c>--help</c>).</summary>
	public static readonly Option<bool> HelpRequested = new SwitchOption<bool>("help", null, "Explain the command instead of running it.") { Recursive = true, Aliases = { "-h" } };

	/// <summary>Mirrors <c>git -C &lt;path&gt;</c>: run as if bassia had started in &lt;path&gt;. May repeat; each is relative to the one before.</summary>
	public static readonly Option<string[]> WorkingDirectory = new SwitchOption<string[]>("C", "path", "Run as if bassia had started in <path>. May repeat; each is relative to the previous one.") { Recursive = true };

	/// <summary>
	/// The command line, declared once: groups of commands, each with its switches and the one bare argument it
	/// may take. Everything that accepts, explains or rejects a command line reads this tree.
	/// </summary>
	public static RootCommand Root { get; } = Build();

	/// <summary>Every command that runs something, groups flattened, in declaration order.</summary>
	public static IReadOnlyList<BassiaCommand> Commands { get; } = Leaves(Root).ToList();

	/// <summary>The top-level command or group called <paramref name="name"/>, in any case.</summary>
	public static BassiaCommand? Find(string name) =>
		Root.Subcommands.OfType<BassiaCommand>().FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase));

	private static RootCommand Build()
	{
		var root = new RootCommand("bassia - a client-server monorepo for fleets of coding agents: a workspace per run, one trunk to land on.");

		// The library's own -h/--help and --version are replaced by the global switches below and by 'bassia version'.
		root.Options.Clear();
		root.Directives.Clear();
		root.Options.Add(Human);
		root.Options.Add(HelpRequested);
		root.Options.Add(WorkingDirectory);
		foreach (var definition in Definitions())
		{
			root.Subcommands.Add(definition);
		}

		return root;
	}

	private static IEnumerable<BassiaCommand> Leaves(Command parent) =>
		parent.Subcommands.OfType<BassiaCommand>().SelectMany(command => command.IsGroup ? Leaves(command) : [command]);

	// ----- declaration helpers -----

	private static Option<bool> Flag(string name, string description, bool Hidden = false) =>
		new SwitchOption<bool>(name, null, description, hidden: Hidden);

	private static Option<string> Value(string name, string valueName, string description, bool Required = false, bool Hidden = false) =>
		new SwitchOption<string>(name, valueName, description, Required, Hidden);

	/// <summary>A comma-separated list, given once or repeated.</summary>
	private static Option<string[]> List(string name, string valueName, string description, bool Required = false) =>
		new SwitchOption<string[]>(name, valueName, description, Required);

	/// <summary>A switch that takes everything after it on the command line; it comes last.</summary>
	private static Option<string> Rest(string name, string valueName, string description, bool Required = false) =>
		new RestOfLineOption(name, valueName, description, Required);

	/// <summary>A command with subcommands.</summary>
	private static BassiaCommand Group(string name, string summary, BassiaCommand[] subcommands)
	{
		var group = new BassiaCommand(name, summary);
		foreach (var subcommand in subcommands)
		{
			group.Subcommands.Add(subcommand);
		}

		return group;
	}

	/// <summary>
	/// A command that runs. <paramref name="Positional"/> names the switch a single bare argument stands for (several
	/// words when <paramref name="Words"/>, and for a rest-of-line switch).
	/// </summary>
	private static BassiaCommand Cmd(string name, string summary, Option[] options, string[] examples, Func<Invocation, Task<int>> handler,
		string? Positional = null, string? Details = null, string? Usage = null, bool Words = false)
	{
		var target = Positional is null ? null : options.First(option => option.SwitchName() == Positional);
		Argument? argument = target is null ? null
			: target.IsRest() || Words ? new Argument<string[]>(Positional!) { Arity = ArgumentArity.ZeroOrMore }
			: new Argument<string>(Positional!) { Arity = ArgumentArity.ZeroOrOne };
		var command = new BassiaCommand(name, summary)
		{
			Handler = handler,
			Examples = examples,
			Details = Details,
			Usage = Usage,
			Positional = Positional,
			PositionalArgument = argument
		};
		foreach (var option in options)
		{
			command.Options.Add(option);
		}

		if (argument is not null)
		{
			command.Arguments.Add(argument);

			// The library would demand the switch itself; the bare argument may stand in for it, which only
			// ProgramCli can check (Invocation reads both).
			target!.Required = false;
		}

		return command;
	}
}
