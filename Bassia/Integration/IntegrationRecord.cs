namespace Bassia.Integration;

using Tomlyn;
using Tomlyn.Model;

internal sealed class IntegrationException(string message) : Exception(message);

/// <summary>
/// How a run's result relates to the integration head it is about to be merged into, as git's syntax-based merge
/// sees it. Decided before anything is merged, which is what the triage is.
/// </summary>
internal enum Triage
{
	/// <summary>The result is already contained in the head; there is nothing to merge.</summary>
	UpToDate,

	/// <summary>The head is an ancestor of the result: the result only adds to it.</summary>
	FastForward,

	/// <summary>Both sides changed, but git's three-way merge combines them without a conflict.</summary>
	Clean,

	/// <summary>Git's three-way merge stops on conflicting hunks.</summary>
	Conflict
}

/// <summary>How a step is merged: by git alone, by the resolver (an LLM) with the semantic brief, or not at all.</summary>
internal enum MergeStrategy { Syntactic, Semantic, Skip }

internal enum StepOutcome
{
	Pending,

	/// <summary>Nothing to do: the result was already contained in the integration head.</summary>
	UpToDate,

	/// <summary>Merged by git without help.</summary>
	Merged,

	/// <summary>Merged with the resolver's help.</summary>
	Resolved,

	Skipped,
	Failed
}

/// <summary>One run's result in one component, and what the integration did (or will do) with it.</summary>
internal sealed class IntegrationStep
{
	public required string RunId { get; set; }

	/// <summary>The run's one-line rationale: the prompt its agent was given, as far as the command reveals it.</summary>
	public required string Rationale { get; set; }

	/// <summary>The run's result tag in the component (<c>agent/run-&lt;id&gt;/&lt;n&gt;</c>).</summary>
	public required string SourceTag { get; set; }
	public required string SourceCommit { get; set; }

	public Triage Triage { get; set; }
	public MergeStrategy Strategy { get; set; }

	/// <summary>The strategy was chosen by the user rather than by the triage.</summary>
	public bool Overridden { get; set; }

	/// <summary>Files git could not merge (for a <see cref="Triage.Conflict"/>).</summary>
	public List<string> Conflicts { get; } = [];

	/// <summary>Other runs of the same integration whose results conflict with this one on their own.</summary>
	public List<string> ConflictsWith { get; } = [];

	public StepOutcome Outcome { get; set; } = StepOutcome.Pending;

	/// <summary>The integration head after this step: the merge commit, or the head it left unchanged.</summary>
	public string? Commit { get; set; }
	public string? Note { get; set; }

	/// <summary>Where the semantic brief handed to the resolver was written.</summary>
	public string? Brief { get; set; }

	public bool IsSemantic => Strategy == MergeStrategy.Semantic;
}

/// <summary>The integration of one component: its base, the ordered steps, and where the result went.</summary>
internal sealed class ComponentIntegration
{
	public required string Name { get; set; }

	/// <summary>What the integration was built on, as given: a branch (by default the repo's default branch) or a tag.</summary>
	public required string BaseRef { get; set; }
	public required string BaseCommit { get; set; }

	/// <summary>The steps in execution order: every syntactic step before every semantic one, skipped steps last.</summary>
	public List<IntegrationStep> Steps { get; } = [];

	public string? Branch { get; set; }
	public string? Path { get; set; }
	public ResultStatus ResultStatus { get; set; } = ResultStatus.Pending;
	public string? ResultCommit { get; set; }
	public string? ResultTag { get; set; }
	public string? ResultError { get; set; }

	/// <summary><see cref="BaseRef"/> was fast-forwarded to the result by <c>bassia integrate advance</c>.</summary>
	public bool Advanced { get; set; }
}

/// <summary>
/// The record of one integration, committed as <c>integration.toml</c> into the <c>.agentic-runs</c> store next to
/// the run records, tagged <c>integration/&lt;id&gt;/&lt;lineage&gt;</c>.
/// </summary>
internal sealed class IntegrationRecord
{
	public const string FileName = "integration.toml";
	public const string IdPrefix = "integration-";
	public const string RefPrefix = "integration/";

	public required string IntegrationId { get; set; }
	public required string Status { get; set; }
	public int Lineage { get; set; }
	public required string Created { get; set; }
	public string? Finished { get; set; }

	/// <summary>The runs whose results were integrated, in the order they were considered (oldest first).</summary>
	public List<string> Runs { get; } = [];
	public required string Resolver { get; set; }
	public required string WorkspacePath { get; set; }
	public List<ComponentIntegration> Components { get; } = [];

	public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N");

	public static string NormalizeId(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal) ? id : IdPrefix + id;

	public static string Key(string id) => id[IdPrefix.Length..];

	public static string ShortKey(string id) => Key(id)[..8];

	/// <summary>Branch of the integration in every component, and the base of its tags: <c>integration/&lt;id&gt;</c>.</summary>
	public static string RefBase(string id) => RefPrefix + Key(id);

	public static string TagName(string id, int index) => $"{RefBase(id)}/{index}";

	public IEnumerable<IntegrationStep> AllSteps => Components.SelectMany(component => component.Steps);

	public string ToToml()
	{
		var table = new TomlTable
		{
			["integration_id"] = IntegrationId,
			["status"] = Status,
			["lineage"] = (long)Lineage,
			["created"] = Created,
			["resolver"] = Resolver,
			["workspace"] = WorkspacePath,
			["runs"] = Array(Runs)
		};
		if (Finished is not null) table["finished"] = Finished;

		var components = new TomlTableArray();
		foreach (var component in Components)
		{
			var componentTable = new TomlTable
			{
				["name"] = component.Name,
				["base_ref"] = component.BaseRef,
				["base_commit"] = component.BaseCommit,
				["result_status"] = component.ResultStatus.ToString().ToLowerInvariant(),
				["advanced"] = component.Advanced
			};
			Optional(componentTable, "branch", component.Branch);
			Optional(componentTable, "path", component.Path);
			Optional(componentTable, "result_commit", component.ResultCommit);
			Optional(componentTable, "result_tag", component.ResultTag);
			Optional(componentTable, "result_error", component.ResultError);

			var steps = new TomlTableArray();
			foreach (var step in component.Steps)
			{
				var stepTable = new TomlTable
				{
					["run_id"] = step.RunId,
					["rationale"] = step.Rationale,
					["source_tag"] = step.SourceTag,
					["source_commit"] = step.SourceCommit,
					["triage"] = Snake(step.Triage),
					["strategy"] = Snake(step.Strategy),
					["overridden"] = step.Overridden,
					["conflicts"] = Array(step.Conflicts),
					["conflicts_with"] = Array(step.ConflictsWith),
					["outcome"] = Snake(step.Outcome)
				};
				Optional(stepTable, "commit", step.Commit);
				Optional(stepTable, "note", step.Note);
				Optional(stepTable, "brief", step.Brief);
				steps.Add(stepTable);
			}

			componentTable["step"] = steps;
			components.Add(componentTable);
		}

		table["component"] = components;
		return TomlSerializer.Serialize(table);
	}

	public static IntegrationRecord FromToml(string text)
	{
		var table = TomlSerializer.Deserialize<TomlTable>(text) ?? throw new MonorepoException("Empty integration record.");
		var record = new IntegrationRecord
		{
			IntegrationId = Str(table, "integration_id"),
			Status = Str(table, "status"),
			Lineage = (int)(long)table["lineage"],
			Created = Str(table, "created"),
			Finished = OptStr(table, "finished"),
			Resolver = Str(table, "resolver"),
			WorkspacePath = Str(table, "workspace")
		};
		record.Runs.AddRange(Strings(table, "runs"));

		if (table.TryGetValue("component", out var components) && components is TomlTableArray componentTables)
		{
			foreach (TomlTable componentTable in componentTables)
			{
				var component = new ComponentIntegration
				{
					Name = Str(componentTable, "name"),
					BaseRef = Str(componentTable, "base_ref"),
					BaseCommit = Str(componentTable, "base_commit"),
					Branch = OptStr(componentTable, "branch"),
					Path = OptStr(componentTable, "path"),
					ResultStatus = Enum.Parse<ResultStatus>(Str(componentTable, "result_status"), ignoreCase: true),
					ResultCommit = OptStr(componentTable, "result_commit"),
					ResultTag = OptStr(componentTable, "result_tag"),
					ResultError = OptStr(componentTable, "result_error"),
					Advanced = componentTable.TryGetValue("advanced", out var advanced) && advanced is true
				};

				if (componentTable.TryGetValue("step", out var steps) && steps is TomlTableArray stepTables)
				{
					foreach (TomlTable stepTable in stepTables)
					{
						var step = new IntegrationStep
						{
							RunId = Str(stepTable, "run_id"),
							Rationale = Str(stepTable, "rationale"),
							SourceTag = Str(stepTable, "source_tag"),
							SourceCommit = Str(stepTable, "source_commit"),
							Triage = Parse<Triage>(Str(stepTable, "triage")),
							Strategy = Parse<MergeStrategy>(Str(stepTable, "strategy")),
							Overridden = stepTable.TryGetValue("overridden", out var overridden) && overridden is true,
							Outcome = Parse<StepOutcome>(Str(stepTable, "outcome")),
							Commit = OptStr(stepTable, "commit"),
							Note = OptStr(stepTable, "note"),
							Brief = OptStr(stepTable, "brief")
						};
						step.Conflicts.AddRange(Strings(stepTable, "conflicts"));
						step.ConflictsWith.AddRange(Strings(stepTable, "conflicts_with"));
						component.Steps.Add(step);
					}
				}

				record.Components.Add(component);
			}
		}

		return record;
	}

	/// <summary>A deep copy, for handing a consistent view of a running integration to another thread.</summary>
	public IntegrationRecord Clone()
	{
		var copy = FromToml(ToToml());
		copy.Lineage = Lineage;
		return copy;
	}

	/// <summary><c>FastForward</c> -> <c>fast_forward</c>: the spelling the records and results use.</summary>
	public static string Snake<T>(T value) where T : struct, Enum =>
		string.Concat(value.ToString().Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

	public static T Parse<T>(string value) where T : struct, Enum => Enum.Parse<T>(value.Replace("_", ""), ignoreCase: true);

	private static TomlArray Array(IEnumerable<string> values)
	{
		var array = new TomlArray();
		foreach (var value in values)
		{
			array.Add(value);
		}

		return array;
	}

	private static void Optional(TomlTable table, string key, string? value)
	{
		if (value is not null)
		{
			table[key] = value;
		}
	}

	private static IEnumerable<string> Strings(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) && value is TomlArray array ? array.OfType<string>() : [];

	private static string Str(TomlTable table, string key) => (string)table[key];
	private static string? OptStr(TomlTable table, string key) => table.TryGetValue(key, out var value) ? value as string : null;
}

/// <summary>Integration records in the <c>.agentic-runs</c> store, beside (and never mixed up with) the run records.</summary>
internal sealed class IntegrationStore(RunMetadataStore store)
{
	public string RepoDir => store.RepoDir;

	public Task EnsureRepositoryAsync() => store.EnsureRepositoryAsync();

	public async Task<string> CommitAsync(IntegrationRecord record)
	{
		var refBase = IntegrationRecord.RefBase(record.IntegrationId);
		record.Lineage = await store.LatestIndexAsync(refBase) + 1;
		return await store.WriteAsync(refBase, record.Lineage, IntegrationRecord.FileName, record.ToToml(),
			$"{record.IntegrationId} #{record.Lineage}: {record.Status}");
	}

	public async Task<IntegrationRecord?> LoadLatestAsync(string integrationId)
	{
		if (await store.ReadLatestAsync(IntegrationRecord.RefBase(integrationId), IntegrationRecord.FileName) is not { } found)
		{
			return null;
		}

		var (toml, lineage) = found;

		var record = IntegrationRecord.FromToml(toml);
		record.Lineage = lineage;
		return record;
	}

	/// <summary>Every recorded integration, newest first, each at its latest lineage.</summary>
	public async Task<IReadOnlyList<IntegrationRecord>> ListLatestAsync()
	{
		var records = new List<IntegrationRecord>();
		foreach (var key in await store.ListKeysAsync(IntegrationRecord.RefPrefix))
		{
			if (await LoadLatestAsync(IntegrationRecord.NormalizeId(key)) is { } record)
			{
				records.Add(record);
			}
		}

		return records.OrderByDescending(record => record.Created, StringComparer.Ordinal).ToList();
	}
}
