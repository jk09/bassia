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
	public const string RefPrefix = "agent-run/";

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

	/// <summary><c>agent-run-&lt;key&gt;</c> with a fresh <see cref="RecordName"/> key, e.g. <c>agent-run-magical-otter-vt9j3p</c>.</summary>
	public static string NewRunId() => RunIdPrefix + RecordName.NewKey();

	/// <summary>Accepts the full run id or just its <c>&lt;key&gt;</c> part.</summary>
	public static string NormalizeRunId(string runId) => runId.StartsWith(RunIdPrefix, StringComparison.Ordinal) ? runId : RunIdPrefix + runId;

	/// <summary>The <c>&lt;key&gt;</c> part of a run id; short and readable, so it is also what is shown as the run's short id.</summary>
	public static string Key(string runId) => runId[RunIdPrefix.Length..];

	/// <summary>Whether <paramref name="runId"/> is a well-formed <c>agent-run-&lt;key&gt;</c>.</summary>
	public static bool IsRunId(string runId) => runId.StartsWith(RunIdPrefix, StringComparison.Ordinal) && RecordName.IsKey(Key(runId));

	/// <summary>Branch of the run in every component checkout: <c>agent-run/&lt;key&gt;</c>.</summary>
	public static string RefBase(string runId) => RefPrefix + Key(runId);

	/// <summary>
	/// <c>agent-run/&lt;key&gt;/&lt;index&gt;</c>: a result tag in a component repo, or a lineage tag in the run-metadata repo.
	/// </summary>
	public static string TagName(string runId, int index) => $"{RefBase(runId)}/{index}";

	/// <summary>Highest <c>&lt;index&gt;</c> among the given <c>agent-run/&lt;key&gt;/&lt;index&gt;</c> tags, or -1 when there is none.</summary>
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
/// (child of the previous version) reachable only through its annotated tag <c>agent-run/&lt;key&gt;/&lt;lineage&gt;</c>.
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

	/// <summary>A complete bare repo is in place (<c>objects/</c> exists).</summary>
	private bool IsInitialized => Directory.Exists(Path.Combine(repoDir, "objects"));

	/// <summary>
	/// Creates the bare repo on first use. Runs started in parallel reach this at the same moment, so the repo is
	/// initialized under a private staging name and then moved into place: the rename is atomic and fails when the
	/// destination already exists, so the loser of the race discards its own copy and every run sees a complete
	/// repo - never one that another process is still filling with template files.
	/// </summary>
	public async Task EnsureRepositoryAsync()
	{
		if (IsInitialized)
		{
			return;
		}

		var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repoDir))
			?? throw new MonorepoException($"'{repoDir}' has no parent directory to create the run-metadata repo in.");
		Directory.CreateDirectory(parent);

		var staging = Path.Combine(parent, $".init-{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(staging);
			await GitClient.In(staging).RunOrThrowAsync(["init", "--quiet", "--bare", staging]);

			try
			{
				Directory.Move(staging, repoDir);
			}
			catch (IOException) when (IsInitialized)
			{
				// Another run got there first; its repo is complete, so ours is redundant.
			}
		}
		finally
		{
			if (Directory.Exists(staging))
			{
				// A freshly initialized bare repo has no read-only pack files, so a plain recursive delete suffices.
				try
				{
					Directory.Delete(staging, recursive: true);
				}
				catch (IOException)
				{
					// Best-effort cleanup: a leftover staging folder is inert.
				}
			}
		}
	}

	/// <summary>Highest lineage index recorded for the run, or -1 when the run is unknown.</summary>
	public Task<int> LatestLineageAsync(string runId) => LatestIndexAsync(RunMetadata.RefBase(runId));

	/// <summary>Every run with a record in the store, newest first, each at its latest lineage.</summary>
	public async Task<IReadOnlyList<RunMetadata>> ListLatestAsync()
	{
		var runs = new List<RunMetadata>();
		foreach (var key in await ListKeysAsync(RunMetadata.RefPrefix))
		{
			if (await LoadLatestAsync(RunMetadata.NormalizeRunId(key)) is { } metadata)
			{
				runs.Add(metadata);
			}
		}

		return runs.OrderByDescending(run => run.Created, StringComparer.Ordinal).ToList();
	}

	public async Task<RunMetadata?> LoadLatestAsync(string runId)
	{
		if (await ReadLatestAsync(RunMetadata.RefBase(runId), RunMetadata.FileName) is not { } found)
		{
			return null;
		}

		var (toml, lineage) = found;

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
		metadata.Lineage = await LatestLineageAsync(metadata.RunId) + 1;
		return await WriteAsync(RunMetadata.RefBase(metadata.RunId), metadata.Lineage, RunMetadata.FileName, metadata.ToToml(),
			$"{metadata.RunId} #{metadata.Lineage}: {metadata.Status}");
	}

	// ----- record primitives, shared by every kind of record the store keeps (runs, integrations) -----

	/// <summary>Highest <c>&lt;index&gt;</c> among the <c>&lt;refBase&gt;/&lt;index&gt;</c> tags, or -1 when there is none.</summary>
	internal async Task<int> LatestIndexAsync(string refBase)
	{
		if (!IsInitialized)
		{
			return -1;
		}

		var tags = await git.RunOrThrowAsync(["tag", "--list", $"{refBase}/*"]);
		return RunMetadata.HighestIndex(tags.Split('\n', StringSplitOptions.RemoveEmptyEntries));
	}

	/// <summary>The distinct <c>&lt;key&gt;</c> parts of every <c>&lt;refPrefix&gt;&lt;key&gt;/&lt;index&gt;</c> tag.</summary>
	internal async Task<IReadOnlyList<string>> ListKeysAsync(string refPrefix)
	{
		if (!IsInitialized)
		{
			return [];
		}

		var tags = await git.RunOrThrowAsync(["tag", "--list", $"{refPrefix}*/*"]);
		return tags.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(tag => tag[refPrefix.Length..tag.LastIndexOf('/')])
			.Distinct(StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>The file of the latest lineage of a record, with that lineage; null when the record is unknown.</summary>
	internal async Task<(string Content, int Lineage)?> ReadLatestAsync(string refBase, string fileName)
	{
		var lineage = await LatestIndexAsync(refBase);
		if (lineage < 0)
		{
			return null;
		}

		return (await git.RunOrThrowAsync(["show", $"{refBase}/{lineage}:{fileName}"]), lineage);
	}

	/// <summary>
	/// Writes <paramref name="content"/> as <paramref name="fileName"/> in a new commit tagged
	/// <c>&lt;refBase&gt;/&lt;lineage&gt;</c>, parented on the previous lineage's commit when there is one.
	/// </summary>
	internal async Task<string> WriteAsync(string refBase, int lineage, string fileName, string content, string message)
	{
		var blob = await git.RunOrThrowAsync(["hash-object", "-w", "--stdin"], content);
		var tree = await git.RunOrThrowAsync(["mktree"], $"100644 blob {blob}\t{fileName}\n");

		var commitArguments = new List<string> { "commit-tree", tree, "-m", message };
		if (lineage > 0)
		{
			var parent = await git.RunOrThrowAsync(["rev-parse", $"{refBase}/{lineage - 1}^{{commit}}"]);
			commitArguments.AddRange(["-p", parent]);
		}

		var commit = await git.RunOrThrowAsync(commitArguments);
		var tag = $"{refBase}/{lineage}";
		await git.RunOrThrowAsync(["tag", "-a", tag, "-m", message, commit]);
		return tag;
	}
}
