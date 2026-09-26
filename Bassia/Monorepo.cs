namespace Bassia;

using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// A component registered in the meta-repo's <c>components.toml</c>. <paramref name="References"/> are the
/// outgoing edges of the (acyclic) component reference graph: components this one nests as subfolders.
/// </summary>
internal sealed record ComponentDefinition(string Name, string Url, IReadOnlyList<ComponentReference> References);

/// <summary>A nested component and the subfolder (relative to the referencing component) where it is expected.</summary>
internal sealed record ComponentReference(string Name, string Path);

internal sealed class MonorepoException(string message) : Exception(message);

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

	/// <summary>Agent command a run composed from a prompt starts with (<c>bassia run start -prompt</c>, the frontends).</summary>
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

	public IReadOnlyList<ComponentDefinition> Components { get; }

	private Monorepo(string root, string workspaceDir, string commitSubject, string resolver, string agentCommand, IReadOnlyList<ComponentDefinition> components)
	{
		Root = root;
		WorkspaceDir = workspaceDir;
		CommitSubject = commitSubject;
		Resolver = resolver;
		AgentCommand = agentCommand;
		Components = components;
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

	public static Monorepo Load(string root)
	{
		var metaRepoDir = Path.Combine(root, MetaRepoFolderName);
		var config = ReadToml(Path.Combine(metaRepoDir, "config.toml"));
		var workspaceDir = Path.Combine(root, DefaultWorkspaceFolderName);

		if (config.TryGetValue("workspace", out var workspaceSection) && workspaceSection is TomlTable workspace
			&& workspace.TryGetValue("path", out var path) && path is string workspacePath && !string.IsNullOrWhiteSpace(workspacePath))
		{
			workspaceDir = Path.GetFullPath(workspacePath, root);
		}

		var commitSubject = DefaultCommitSubject;
		if (config.TryGetValue("agent", out var agentSection) && agentSection is TomlTable agent
			&& agent.TryGetValue("commit", out var commitSection) && commitSection is TomlTable commit
			&& commit.TryGetValue("subject", out var subject) && subject is string subjectTemplate && !string.IsNullOrWhiteSpace(subjectTemplate))
		{
			commitSubject = subjectTemplate;
		}

		var resolver = DefaultResolver;
		if (config.TryGetValue("integration", out var integrationSection) && integrationSection is TomlTable integration
			&& integration.TryGetValue("resolver", out var resolverValue) && resolverValue is string configuredResolver && !string.IsNullOrWhiteSpace(configuredResolver))
		{
			resolver = configuredResolver;
		}

		var agentCommand = DefaultAgentCommand;
		if (config.TryGetValue("agent", out var agentTable) && agentTable is TomlTable agentConfig
			&& agentConfig.TryGetValue("command", out var commandValue) && commandValue is string configuredCommand && !string.IsNullOrWhiteSpace(configuredCommand))
		{
			agentCommand = configuredCommand;
		}

		var components = ReadComponents(ReadToml(Path.Combine(metaRepoDir, "components.toml")));
		return new Monorepo(root, workspaceDir, commitSubject, resolver, agentCommand, components);
	}

	public ComponentDefinition? FindComponent(string name) =>
		Components.FirstOrDefault(component => string.Equals(component.Name, name, StringComparison.Ordinal));

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
	public IReadOnlyList<ComponentDefinition> Closure(IEnumerable<string> roots)
	{
		var ordered = new List<ComponentDefinition>();
		var state = new Dictionary<string, VisitState>(StringComparer.Ordinal);
		var path = new Stack<string>();

		void Visit(string name)
		{
			if (state.TryGetValue(name, out var visited))
			{
				if (visited == VisitState.InProgress)
				{
					var cycle = string.Join(" -> ", path.Reverse().SkipWhile(item => item != name).Append(name));
					throw new MonorepoException($"components.toml contains a cyclic component reference: {cycle}.");
				}

				return;
			}

			var component = FindComponent(name)
				?? throw new MonorepoException($"Component '{name}' is referenced but not registered in components.toml.");

			state[name] = VisitState.InProgress;
			path.Push(name);
			foreach (var reference in component.References)
			{
				Visit(reference.Name);
			}

			path.Pop();
			state[name] = VisitState.Done;
			ordered.Add(component);
		}

		foreach (var root in roots)
		{
			Visit(root);
		}

		ordered.Reverse();
		return ordered;
	}

	private enum VisitState { InProgress, Done }

	private static TomlTable ReadToml(string path)
	{
		if (!File.Exists(path))
		{
			return new TomlTable();
		}

		try
		{
			return TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? new TomlTable();
		}
		catch (TomlException ex)
		{
			throw new MonorepoException($"Could not parse '{path}': {ex.Message}");
		}
	}

	private static List<ComponentDefinition> ReadComponents(TomlTable table)
	{
		var components = new List<ComponentDefinition>();
		if (!table.TryGetValue("component", out var entries) || entries is not TomlTableArray componentTables)
		{
			return components;
		}

		foreach (var componentTable in componentTables)
		{
			var name = componentTable.TryGetValue("name", out var nameValue) ? nameValue as string : null;
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new MonorepoException("components.toml contains a [[component]] entry without a name.");
			}

			if (components.Any(component => component.Name == name))
			{
				throw new MonorepoException($"components.toml registers the component '{name}' more than once.");
			}

			var url = componentTable.TryGetValue("url", out var urlValue) ? urlValue as string ?? "" : "";
			components.Add(new ComponentDefinition(name, url, ReadReferences(name, componentTable)));
		}

		return components;
	}

	// references = ["lib", { name = "other", path = "libs/other" }]
	private static List<ComponentReference> ReadReferences(string owner, TomlTable componentTable)
	{
		var references = new List<ComponentReference>();
		if (!componentTable.TryGetValue("references", out var value) || value is null)
		{
			return references;
		}

		if (value is not TomlArray array)
		{
			throw new MonorepoException($"Component '{owner}': 'references' must be an array.");
		}

		foreach (var item in array)
		{
			switch (item)
			{
				case string name when !string.IsNullOrWhiteSpace(name):
					references.Add(new ComponentReference(name, name));
					break;
				case TomlTable table when table.TryGetValue("name", out var nameValue) && nameValue is string name && !string.IsNullOrWhiteSpace(name):
					var path = table.TryGetValue("path", out var pathValue) && pathValue is string customPath && !string.IsNullOrWhiteSpace(customPath)
						? customPath
						: name;
					if (Path.IsPathRooted(path) || path.Split(['/', '\\']).Any(segment => segment is "" or "." or ".."))
					{
						throw new MonorepoException($"Component '{owner}': reference path '{path}' must be a relative path inside the component.");
					}

					references.Add(new ComponentReference(name, path));
					break;
				default:
					throw new MonorepoException($"Component '{owner}': each reference must be a component name or {{ name = \"...\", path = \"...\" }}.");
			}

			if (references[^1].Name == owner)
			{
				throw new MonorepoException($"Component '{owner}' cannot reference itself.");
			}
		}

		return references;
	}
}
