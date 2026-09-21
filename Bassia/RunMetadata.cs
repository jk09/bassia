namespace Bassia;

using System.Globalization;
using Bassia.Git;
using Tomlyn;
using Tomlyn.Model;

/// <summary>Where a component's result stands in the (non-transactional) commit/tag/push sequence.</summary>
internal enum ResultStatus { Pending, Unchanged, Committed, Pushed, Failed }

/// <summary>Per-component part of the run record.</summary>
internal sealed class ComponentRun
{
	public required string Name { get; set; }

	/// <summary>The annotated tag given for this component via -select.</summary>
	public required string CommitIsh { get; set; }

	/// <summary>The concrete commit the commit-ish resolved to when the run was created.</summary>
	public required string Commit { get; set; }

	/// <summary>Checkout location: <c>&lt;run folder&gt;/&lt;component&gt;</c>.</summary>
	public required string Path { get; set; }
	public required string Branch { get; set; }

	/// <summary>Junctions created inside this component's checkout: relative path -> nested component name.</summary>
	public Dictionary<string, string> Junctions { get; } = new(StringComparer.Ordinal);

	public ResultStatus ResultStatus { get; set; } = ResultStatus.Pending;
	public string? ResultCommit { get; set; }
	public string? ResultTag { get; set; }
	public string? ResultError { get; set; }
}

/// <summary>The agentic run record committed (as <c>run.toml</c>) into the <c>.agentic-runs</c> bare repo.</summary>
internal sealed class RunMetadata
{
	public const string FileName = "run.toml";
	public const string RunIdPrefix = "agent-run-";
	public const string RefPrefix = "agent/run-";

	public required string RunId { get; set; }
	public required string Status { get; set; }
	public int Lineage { get; set; }
	public required string Created { get; set; }
	public string? Finished { get; set; }
	public required string Select { get; set; }
	public required string Command { get; set; }

	/// <summary>The run folder holding every component's checkout.</summary>
	public required string WorkspacePath { get; set; }
	public int? AgentExitCode { get; set; }
	public List<ComponentRun> Components { get; } = [];

	public static string Timestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

	/// <summary><c>agent-run-&lt;id&gt;</c> with a fresh GUID, so ids never collide across workspaces sharing a component repo.</summary>
	public static string NewRunId() => RunIdPrefix + Guid.NewGuid().ToString("N");

	/// <summary>Accepts the full run id or just its <c>&lt;id&gt;</c> part.</summary>
	public static string NormalizeRunId(string runId) => runId.StartsWith(RunIdPrefix, StringComparison.Ordinal) ? runId : RunIdPrefix + runId;

	/// <summary>The <c>&lt;id&gt;</c> part of a run id.</summary>
	public static string Key(string runId) => runId[RunIdPrefix.Length..];

	public static string ShortKey(string runId) => Key(runId)[..8];

	/// <summary>Branch of the run in every component checkout: <c>agent/run-&lt;id&gt;</c>.</summary>
	public static string RefBase(string runId) => RefPrefix + Key(runId);

	/// <summary>
	/// <c>agent/run-&lt;id&gt;/&lt;index&gt;</c>: a result tag in a component repo, or a lineage tag in the run-metadata repo.
	/// </summary>
	public static string TagName(string runId, int index) => $"{RefBase(runId)}/{index}";

	/// <summary>Highest <c>&lt;index&gt;</c> among the given <c>agent/run-&lt;id&gt;/&lt;index&gt;</c> tags, or -1 when there is none.</summary>
	public static int HighestIndex(IEnumerable<string> tags)
	{
		var highest = -1;
		foreach (var tag in tags)
		{
			var suffix = tag[(tag.LastIndexOf('/') + 1)..];
			if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index > highest)
			{
				highest = index;
			}
		}

		return highest;
	}

	public string ToToml()
	{
		var table = new TomlTable
		{
			["run_id"] = RunId,
			["status"] = Status,
			["lineage"] = (long)Lineage,
			["created"] = Created,
			["select"] = Select,
			["command"] = Command,
			["workspace"] = WorkspacePath
		};
		if (Finished is not null) table["finished"] = Finished;
		if (AgentExitCode is not null) table["agent_exit_code"] = (long)AgentExitCode.Value;

		var components = new TomlTableArray();
		foreach (var component in Components)
		{
			var componentTable = new TomlTable
			{
				["name"] = component.Name,
				["commitish"] = component.CommitIsh,
				["commit"] = component.Commit,
				["path"] = component.Path,
				["branch"] = component.Branch,
				["result_status"] = component.ResultStatus.ToString().ToLowerInvariant()
			};
			if (component.ResultCommit is not null) componentTable["result_commit"] = component.ResultCommit;
			if (component.ResultTag is not null) componentTable["result_tag"] = component.ResultTag;
			if (component.ResultError is not null) componentTable["result_error"] = component.ResultError;
			if (component.Junctions.Count > 0)
			{
				var junctions = new TomlTable();
				foreach (var (path, target) in component.Junctions)
				{
					junctions[path] = target;
				}

				componentTable["junctions"] = junctions;
			}

			components.Add(componentTable);
		}

		table["component"] = components;
		return TomlSerializer.Serialize(table);
	}

	public static RunMetadata FromToml(string text)
	{
		var table = TomlSerializer.Deserialize<TomlTable>(text) ?? throw new MonorepoException("Empty run metadata record.");
		var metadata = new RunMetadata
		{
			RunId = Str(table, "run_id"),
			Status = Str(table, "status"),
			Lineage = (int)(long)table["lineage"],
			Created = Str(table, "created"),
			Finished = OptStr(table, "finished"),
			Select = Str(table, "select"),
			Command = Str(table, "command"),
			WorkspacePath = Str(table, "workspace"),
			AgentExitCode = table.TryGetValue("agent_exit_code", out var exitCode) ? (int)(long)exitCode : null
		};

		if (table.TryGetValue("component", out var components) && components is TomlTableArray componentTables)
		{
			foreach (TomlTable componentTable in componentTables)
			{
				var component = new ComponentRun
				{
					Name = Str(componentTable, "name"),
					CommitIsh = Str(componentTable, "commitish"),
					Commit = Str(componentTable, "commit"),
					Path = Str(componentTable, "path"),
					Branch = Str(componentTable, "branch"),
					ResultStatus = Enum.Parse<ResultStatus>(Str(componentTable, "result_status"), ignoreCase: true),
					ResultCommit = OptStr(componentTable, "result_commit"),
					ResultTag = OptStr(componentTable, "result_tag"),
					ResultError = OptStr(componentTable, "result_error")
				};
				if (componentTable.TryGetValue("junctions", out var junctions) && junctions is TomlTable junctionTable)
				{
					foreach (var (path, target) in junctionTable)
					{
						component.Junctions[path] = (string)target;
					}
				}

				metadata.Components.Add(component);
			}
		}

		return metadata;
	}

	private static string Str(TomlTable table, string key) => (string)table[key];
	private static string? OptStr(TomlTable table, string key) => table.TryGetValue(key, out var value) ? value as string : null;
}

/// <summary>
/// Stores run records in the <c>.agentic-runs</c> bare repo using plumbing commands only: no branch is ever
/// checked out or moved (so many parallel runs never contend for an index lock). Each record version is a commit
/// (child of the previous version) reachable only through its annotated tag <c>agent/&lt;run-id&gt;/&lt;lineage&gt;</c>.
/// </summary>
internal sealed class RunMetadataStore
{
	private readonly GitClient git;
	private readonly string repoDir;

	public RunMetadataStore(GitClient git, string repoDir)
	{
		this.repoDir = repoDir;
		this.git = GitClient.In(repoDir);
	}

	public string RepoDir => repoDir;

	public async Task EnsureRepositoryAsync()
	{
		if (Directory.Exists(Path.Combine(repoDir, "objects")))
		{
			return;
		}

		Directory.CreateDirectory(repoDir);
		await git.RunOrThrowAsync(["init", "--quiet", "--bare", repoDir]);
	}

	/// <summary>Highest lineage index recorded for the run, or -1 when the run is unknown.</summary>
	public async Task<int> LatestLineageAsync(string runId)
	{
		var tags = await git.RunOrThrowAsync(["tag", "--list", $"{RunMetadata.RefBase(runId)}/*"]);
		return RunMetadata.HighestIndex(tags.Split('\n', StringSplitOptions.RemoveEmptyEntries));
	}

	public async Task<RunMetadata?> LoadLatestAsync(string runId)
	{
		var lineage = await LatestLineageAsync(runId);
		if (lineage < 0)
		{
			return null;
		}

		var toml = await git.RunOrThrowAsync(["show", $"{RunMetadata.TagName(runId, lineage)}:{RunMetadata.FileName}"]);
		var metadata = RunMetadata.FromToml(toml);
		metadata.Lineage = lineage;
		return metadata;
	}

	/// <summary>
	/// Writes the record as a new commit + annotated tag. The commit's parent is the previous lineage commit (if any),
	/// so the run's history is a chain of tagged commits. Returns the tag name.
	/// </summary>
	public async Task<string> CommitAsync(RunMetadata metadata)
	{
		var previous = await LatestLineageAsync(metadata.RunId);
		metadata.Lineage = previous + 1;

		var blob = await git.RunOrThrowAsync(["hash-object", "-w", "--stdin"], metadata.ToToml());
		var tree = await git.RunOrThrowAsync(["mktree"], $"100644 blob {blob}\t{RunMetadata.FileName}\n");

		var message = $"{metadata.RunId} #{metadata.Lineage}: {metadata.Status}";
		var commitArguments = new List<string> { "commit-tree", tree, "-m", message };
		if (previous >= 0)
		{
			var parent = await git.RunOrThrowAsync(["rev-parse", $"{RunMetadata.TagName(metadata.RunId, previous)}^{{commit}}"]);
			commitArguments.AddRange(["-p", parent]);
		}

		var commit = await git.RunOrThrowAsync(commitArguments);
		var tag = RunMetadata.TagName(metadata.RunId, metadata.Lineage);
		await git.RunOrThrowAsync(["tag", "-a", tag, "-m", message, commit]);
		return tag;
	}
}
