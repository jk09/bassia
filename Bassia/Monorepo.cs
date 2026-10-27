namespace Bassia;

/// <summary>
/// A component registered in the meta-repo's <c>components.toml</c>. <paramref name="References"/> are the
/// outgoing edges of the (acyclic) component reference graph: components this one nests as subfolders.
/// </summary>
/// <remarks>
/// <paramref name="ForkOf"/> and <paramref name="ForkCommit"/> name the component this one was forked from and the commit it
/// forked at. <see cref="Tags"/>, <see cref="Visibility"/> and <see cref="Owners"/> place it in the monorepo's architecture:
/// the boundary rules (<see cref="Core.Boundaries"/>) are written over tags.
/// </remarks>
internal sealed record ComponentDefinition(string Name, string Url, IReadOnlyList<ComponentReference> References, string? ForkOf = null, string? ForkCommit = null)
{
	/// <summary>Its tags (<c>type:lib</c>, <c>scope:billing</c>), besides the implicit <c>component:&lt;name&gt;</c>.</summary>
	public IReadOnlyList<string> Tags { get; init; } = [];

	/// <summary>Tag patterns a referrer must match one of; null means anyone may reference it, empty means nobody.</summary>
	public IReadOnlyList<string>? Visibility { get; init; }

	/// <summary>Who owns it: people or teams, as the monorepo names them.</summary>
	public IReadOnlyList<string> Owners { get; init; } = [];

	/// <summary>
	/// Its public API: path patterns (as in split plans) a referrer's code may use; null means every path is exported.
	/// </summary>
	public IReadOnlyList<string>? Exports { get; init; }
}

/// <summary>A nested component and the subfolder (relative to the referencing component) where it is expected.</summary>
internal sealed record ComponentReference(string Name, string Path);

internal class MonorepoException(string message) : Exception(message);

/// <summary>
/// The Bassia monorepo as laid out on disk: the <c>.bassia</c> meta-repo, its configuration and component registry,
/// plus the derived workspace location.
/// </summary>
internal sealed class Monorepo
{
	public const string MetaRepoFolderName = ".bassia";
	public const string DefaultWorkspaceFolderName = ".workspace";
	public const string RunsRepoFolderName = ".agentic-runs";
	public const string DefaultCommitSubject = "agent({short_id}): {summary}";

	/// <summary>
	/// Resolver of semantic merges. The brief arrives on stdin, which is how <c>claude -p</c> takes a prompt that is too
	/// long for a command line.
	/// </summary>
	public const string DefaultResolver = "claude -p --permission-mode acceptEdits";

	/// <summary>
	/// The structural (entity-level) merge driver the integration tries where git's merge conflicts: weave's
	/// <c>weave-driver</c>, used when it is installed. <c>off</c> disables it; see <see cref="Integration.StructuralMerge"/>.
	/// </summary>
	public const string DefaultStructuralDriver = "weave-driver";

	/// <summary>Agent command a run composed from a prompt starts with (<c>bassia agent start -prompt</c>, the frontends).</summary>
	public const string DefaultAgentCommand = "claude -p --permission-mode acceptEdits";

	public string Root { get; }
	public string MetaRepoDir => Path.Combine(Root, MetaRepoFolderName);

	/// <summary>Folder holding the isolated per-run areas. Configurable via <c>[workspace] path</c> in <c>config.toml</c>.</summary>
	public string WorkspaceDir { get; }

	/// <summary>
	/// Subject line of the commits an agentic run makes in a component. Configurable via <c>[agent.commit] subject</c>
	/// in <c>config.toml</c>; see <see cref="ResultCommitMessage"/> for the placeholders.
	/// </summary>
	public string CommitSubject { get; }

	/// <summary>Command that resolves a semantic merge. Configurable via <c>[integration] resolver</c> in <c>config.toml</c>.</summary>
	public string Resolver { get; }

	/// <summary>Structural merge driver, or <c>off</c>. Configurable via <c>[integration] weave</c> in <c>config.toml</c>.</summary>
	public string StructuralDriver { get; }

	/// <summary>Agent command for runs composed from a prompt. Configurable via <c>[agent] command</c> in <c>config.toml</c>.</summary>
	public string AgentCommand { get; }

	/// <summary>
	/// Bare repo (<c>.agentic-runs/.git</c>) that stores agentic run metadata. It lives at the monorepo root,
	/// next to <c>.bassia</c> and <c>.workspace</c>, not inside either of them: it isn't meta-repo content (so it
	/// must not sit in the <c>.bassia</c> working tree, which git would otherwise try to track it as part of), and
	/// unlike a run folder - scratch that may be discarded once its results are pushed - the mapping from a run to
	/// the tags it created in the components is durable monorepo state that must outlive the workspace.
	/// </summary>
	public string RunsRepoDir => Path.Combine(Root, RunsRepoFolderName, ".git");

	/// <summary>The component graph as the meta-repo holds it.</summary>
	public Core.ComponentRegistry Registry { get; }

	public IReadOnlyList<ComponentDefinition> Components => Registry.Components;

	/// <summary>The configuration layers as read when the monorepo was loaded (see <see cref="ConfigSnapshot"/>).</summary>
	public ConfigSnapshot Config { get; }

	/// <summary>How the integration merges <paramref name="component"/>'s results: the <c>merge.*</c> settings for it.</summary>
	public Integration.MergePolicy MergePolicyFor(string component) => Integration.MergePolicy.For(Config, component);

	/// <summary>Where everything this monorepo keeps is stored (see <see cref="Storage.IMonorepoBackend"/>).</summary>
	public Storage.IMonorepoBackend Backend { get; }

	private Monorepo(Storage.IMonorepoBackend backend, string root, string workspaceDir, string commitSubject, string resolver, string structuralDriver, string agentCommand,
		Core.ComponentRegistry registry, ConfigSnapshot config)
	{
		Backend = backend;
		Config = config;
		Root = root;
		WorkspaceDir = workspaceDir;
		CommitSubject = commitSubject;
		Resolver = resolver;
		StructuralDriver = structuralDriver;
		AgentCommand = agentCommand;
		Registry = registry;
	}

	/// <summary>Finds the monorepo root by walking up from <paramref name="startDirectory"/> until a <c>.bassia</c> folder is found.</summary>
	public static string? FindRoot(string startDirectory)
	{
		for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
		{
			if (Directory.Exists(Path.Combine(directory.FullName, MetaRepoFolderName)))
			{
				return directory.FullName;
			}
		}

		return null;
	}

	/// <summary>The monorepo at <paramref name="root"/> on this machine's disk (<see cref="LocalBackend"/>).</summary>
	public static Monorepo Load(string root) => Load(new LocalBackend(root));

	/// <summary>
	/// The monorepo <paramref name="backend"/> keeps: its settings and its registry as of now. It waits for the backend in
	/// place, which costs nothing on the local backend, whose meta store reads files.
	/// </summary>
	public static Monorepo Load(Storage.IMonorepoBackend backend)
	{
		var root = backend.Root;
		var config = backend.Config.Read();
		if (config.Problems() is { Count: > 0 } problems)
		{
			throw new MonorepoException($"Invalid configuration: {string.Join("; ", problems)}.");
		}

		var workspace = config.Resolve(ConfigFile.Find("workspace.path"));
		var workspaceDir = Path.GetFullPath(workspace.Value, root);

		var registry = Core.MetaRepo.OpenAsync(backend.Meta).GetAwaiter().GetResult().Registry;
		return new Monorepo(backend, root, workspaceDir, config.Value("agent.commit.subject"), config.Value("integration.resolver"),
			config.Value("integration.weave").Trim(), config.Value("agent.command"), registry, config);
	}

	public ComponentDefinition? FindComponent(string name) => Registry.Find(name);

	/// <summary>
	/// Source-of-truth repository of a component: the folder next to the meta-repo, holding either a bare
	/// <c>.git</c> (as created by <c>component add</c>) or a regular clone. Git resolves both.
	/// </summary>
	public string SourceRepoDir(string componentName) => Path.Combine(Root, componentName);

	/// <summary>
	/// Returns the reference-graph closure of <paramref name="roots"/> (roots included), in dependency order
	/// (a component precedes every component it references). Throws when the graph reachable from the roots
	/// contains a cycle or refers to an unregistered component.
	/// </summary>
	public IReadOnlyList<ComponentDefinition> Closure(IEnumerable<string> roots) => Registry.Closure(roots);

	/// <summary>
	/// The component graph of this monorepo on its backend's storage: the meta store and the component histories.
	/// Commands change components and the registry through it.
	/// </summary>
	public Core.MonorepoGraph Graph() => new(Core.MetaRepo.Open(Backend.Meta, Registry), Backend.Components);

	/// <summary>The monorepo this one is a clone of (<c>depot.url</c>), or null for a monorepo of its own.</summary>
	public Core.Depot? Depot => Config.Value("depot.url").Trim() is { Length: > 0 } url ? new Core.Depot(url) : null;

	/// <summary>The views of this monorepo (<c>bassia view</c>), importing missing components from the depot in a clone.</summary>
	public Core.Views Views() => new(Graph(), Root, WorkspaceDir, Depot);

	/// <summary>The lock table every run of the monorepo shares.</summary>
	public Storage.ILockStore Locks => Backend.Locks;

	/// <summary>The cache of task results.</summary>
	public Storage.ITaskCache TaskCache => Backend.TaskCache;

	/// <summary>The live jobs - runs and integrations being executed - of this monorepo.</summary>
	public Storage.IJobScheduler Jobs => Backend.Jobs;

	/// <summary>The store of the agentic run records.</summary>
	public Storage.IRunRecordStore RunRecords() => Backend.Records;

	/// <summary>The integration records, kept beside the run records.</summary>
	public Integration.IntegrationStore Integrations() => new(RunRecords());
}
