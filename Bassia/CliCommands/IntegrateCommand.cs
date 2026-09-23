namespace Bassia;

using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Integration;

/// <summary>
/// <c>bassia integrate</c>: consolidates the results of several agentic runs, component by component, into one
/// integration tagged identically across the components. Results git can merge on its own are merged first; the
/// rest go to the resolver (an LLM) with a brief on what each side meant. <c>-plan</c> prints the triage only.
/// </summary>
internal static class IntegrateCommand
{
	private const string Usage =
		"Usage: bassia integrate -runs <run-id>[,<run-id>...]|all [-onto <component@branch-or-tag>[,...]]\n" +
		"                        [-semantic <run-id>[,...]] [-skip <run-id>[,...]] [-plan] [-resolve <command>]\n" +
		"       bassia integrate advance <integration-id>";

	public static async Task<int> RunAsync(string[] args)
	{
		if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
		{
			return ProgramCli.WriteResult(false, "integrate", Usage);
		}

		try
		{
			var root = Monorepo.FindRoot(Environment.CurrentDirectory)
				?? throw new IntegrationException($"'{Environment.CurrentDirectory}' is not inside a Bassia monorepo. Run 'bassia init' first.");
			var monorepo = Monorepo.Load(root);
			var git = new GitClient(root);

			return args[0].Equals("advance", StringComparison.OrdinalIgnoreCase)
				? await AdvanceAsync(git, monorepo, args[1..])
				: await IntegrateAsync(git, monorepo, args);
		}
		catch (Exception ex) when (ex is IntegrationException or AgentException or MonorepoException or GitException or IOException or UnauthorizedAccessException)
		{
			return ProgramCli.WriteResult(false, "integrate", ex.Message);
		}
	}

	private sealed record Arguments(string Runs, IReadOnlyDictionary<string, string> Onto, IReadOnlyList<string> Semantic, IReadOnlyList<string> Skip, bool Plan, string? Resolver);

	private static async Task<int> IntegrateAsync(GitClient git, Monorepo monorepo, string[] args)
	{
		var arguments = Parse(args);
		var store = new RunMetadataStore(git, monorepo.RunsRepoDir);
		var runs = await SelectRunsAsync(store, arguments.Runs);

		var strategies = new Dictionary<(string, string), MergeStrategy>();
		foreach (var runId in arguments.Semantic)
		{
			strategies[("*", RequireSelected(runs, runId))] = MergeStrategy.Semantic;
		}

		foreach (var runId in arguments.Skip)
		{
			strategies[("*", RequireSelected(runs, runId))] = MergeStrategy.Skip;
		}

		var plan = await IntegrationPlanner.PlanAsync(monorepo, runs, new IntegrationChoices { Onto = arguments.Onto, Strategies = strategies });
		if (arguments.Plan)
		{
			var steps = plan.SelectMany(component => component.Steps).ToList();
			return ProgramCli.WriteResult(true, "integrate",
				$"Triage of {runs.Count} run(s): {steps.Count(step => step.Strategy == MergeStrategy.Syntactic)} step(s) for git, " +
				$"{steps.Count(step => step.Strategy == MergeStrategy.Semantic)} for the resolver, {steps.Count(step => step.Strategy == MergeStrategy.Skip)} skipped. Nothing was changed.",
				new Dictionary<string, object?>
				{
					["plan"] = true,
					["runs"] = runs.OrderBy(run => run.Created, StringComparer.Ordinal).Select(run => run.RunId).ToList(),
					["component"] = plan.Select(ComponentData).ToList()
				});
		}

		var outcome = await IntegrationRunner.RunAsync(git, monorepo, runs, plan, arguments.Resolver ?? monorepo.Resolver);
		return ProgramCli.WriteResult(outcome.Ok, "integrate", outcome.Message, RecordData(outcome, store.RepoDir));
	}

	private static async Task<int> AdvanceAsync(GitClient git, Monorepo monorepo, string[] args)
	{
		if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
		{
			throw new IntegrationException("Usage: bassia integrate advance <integration-id>");
		}

		var id = IntegrationRecord.NormalizeId(args[0]);
		var store = new IntegrationStore(new RunMetadataStore(git, monorepo.RunsRepoDir));
		var record = await store.LoadLatestAsync(id) ?? throw new IntegrationException($"Unknown integration '{id}'.");
		var outcome = await IntegrationRunner.AdvanceAsync(git, monorepo, record);
		return ProgramCli.WriteResult(outcome.Ok, "integrate advance", outcome.Message, RecordData(outcome, store.RepoDir));
	}

	/// <summary>The runs named by <c>-runs</c>; <c>all</c> is every recorded run with a pushed result.</summary>
	internal static async Task<IReadOnlyList<RunMetadata>> SelectRunsAsync(RunMetadataStore store, string runs)
	{
		var recorded = await store.ListLatestAsync();
		if (runs.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
		{
			var candidates = recorded.Where(IntegrationPlanner.HasResults).ToList();
			return candidates.Count > 0 ? candidates : throw new IntegrationException("No recorded agentic run has pushed a result yet; there is nothing to integrate.");
		}

		var selected = new List<RunMetadata>();
		foreach (var entry in runs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var matches = recorded.Where(run => MatchesRun(run.RunId, entry)).ToList();
			selected.Add(matches.Count switch
			{
				1 => matches[0],
				0 => throw new IntegrationException($"Unknown agentic run '{entry}'."),
				_ => throw new IntegrationException($"'{entry}' names more than one agentic run; give more of its id.")
			});
		}

		return selected;
	}

	/// <summary>A run is named by its full id, its <c>&lt;id&gt;</c> part, or any unambiguous-enough prefix of it such as the short id.</summary>
	private static bool MatchesRun(string runId, string name) =>
		RunMetadata.Key(runId).StartsWith(RunMetadata.Key(RunMetadata.NormalizeRunId(name)), StringComparison.OrdinalIgnoreCase)
		&& RunMetadata.Key(RunMetadata.NormalizeRunId(name)).Length >= 4;

	private static string RequireSelected(IReadOnlyList<RunMetadata> runs, string name) =>
		runs.FirstOrDefault(run => MatchesRun(run.RunId, name))?.RunId
			?? throw new IntegrationException($"'{name}' is not one of the runs selected with -runs.");

	private static Arguments Parse(string[] args)
	{
		string? runs = null, resolver = null;
		var onto = new Dictionary<string, string>(StringComparer.Ordinal);
		List<string> semantic = [], skip = [];
		var plan = false;

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "-runs":
					runs = string.Join(",", new[] { runs, Value(args, ref i) }.Where(value => value is not null));
					break;
				case "-onto":
					foreach (var selection in ComponentSelection.ParseList("-onto", Value(args, ref i)))
					{
						onto[selection.Component] = selection.CommitIsh;
					}

					break;
				case "-semantic":
					semantic.AddRange(List(Value(args, ref i)));
					break;
				case "-skip":
					skip.AddRange(List(Value(args, ref i)));
					break;
				case "-plan":
					plan = true;
					break;
				case "-resolve":
					// Like agent's -run, -resolve takes the rest of the command line: one quoted string or the command's own words.
					var words = args[(i + 1)..];
					if (words.Length == 0)
					{
						throw new IntegrationException("-resolve requires the resolver command.");
					}

					resolver = words.Length == 1 ? words[0] : string.Join(' ', words.Select(word => word.Any(char.IsWhiteSpace) ? $"\"{word}\"" : word));
					i = args.Length;
					break;
				default:
					throw new IntegrationException($"Unknown argument '{args[i]}'.\n{Usage}");
			}
		}

		if (string.IsNullOrWhiteSpace(runs))
		{
			throw new IntegrationException($"-runs is required.\n{Usage}");
		}

		return new Arguments(runs, onto, semantic, skip, plan, resolver);

		static IEnumerable<string> List(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private static string Value(string[] args, ref int index)
	{
		if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
		{
			throw new IntegrationException($"{args[index]} requires a value.");
		}

		return args[++index];
	}

	// ----- output -----

	private static Dictionary<string, object?> RecordData(IntegrationOutcome outcome, string storeDir) => new()
	{
		["integration_id"] = outcome.Record.IntegrationId,
		["status"] = outcome.Record.Status,
		["workspace"] = outcome.Record.WorkspacePath,
		["runs"] = outcome.Record.Runs,
		["metadata_repo"] = storeDir,
		["metadata_tags"] = outcome.RecordTags,
		["component"] = outcome.Record.Components.Select(ComponentData).ToList()
	};

	/// <summary>Named as in <c>integration.toml</c>, so a caller reads the result and the stored record with the same keys.</summary>
	private static IReadOnlyDictionary<string, object?> ComponentData(ComponentIntegration component) => new Dictionary<string, object?>
	{
		["name"] = component.Name,
		["base_ref"] = component.BaseRef,
		["base_commit"] = component.BaseCommit,
		["branch"] = component.Branch,
		["result_status"] = component.ResultStatus.ToString().ToLowerInvariant(),
		["result_commit"] = component.ResultCommit,
		["result_tag"] = component.ResultTag,
		["result_error"] = component.ResultError,
		["advanced"] = component.Advanced,
		["step"] = component.Steps.Select(step => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["run_id"] = step.RunId,
			["rationale"] = step.Rationale,
			["source_tag"] = step.SourceTag,
			["triage"] = IntegrationRecord.Snake(step.Triage),
			["strategy"] = IntegrationRecord.Snake(step.Strategy),
			["conflicts"] = step.Conflicts,
			["conflicts_with"] = step.ConflictsWith,
			["outcome"] = IntegrationRecord.Snake(step.Outcome),
			["commit"] = step.Commit,
			["note"] = step.Note,
			["brief"] = step.Brief
		}).ToList()
	};
}
