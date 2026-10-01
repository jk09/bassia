namespace Bassia;

using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Integration;

/// <summary>
/// What <c>bassia integration</c> shares with the frontends: choosing the runs to integrate and presenting an
/// integration's record with the same keys as <c>integration.toml</c>.
/// </summary>
internal static class IntegrationSupport
{
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

	/// <summary>A run is named by its full id, its <c>&lt;key&gt;</c> part (the short id), or any unambiguous-enough prefix of the key.</summary>
	internal static bool MatchesRun(string runId, string name) =>
		RunMetadata.Key(runId).StartsWith(RunMetadata.Key(RunMetadata.NormalizeRunId(name)), StringComparison.OrdinalIgnoreCase)
		&& RunMetadata.Key(RunMetadata.NormalizeRunId(name)).Length >= 4;

	internal static string RequireSelected(IReadOnlyList<RunMetadata> runs, string name) =>
		runs.FirstOrDefault(run => MatchesRun(run.RunId, name))?.RunId
			?? throw new IntegrationException($"'{name}' is not one of the runs selected with -runs.");

	// ----- output -----

	internal static Dictionary<string, object?> RecordData(IntegrationOutcome outcome, string storeDir) => new()
	{
		["integration_id"] = outcome.Record.IntegrationId,
		["status"] = outcome.Record.Status,
		["workspace"] = outcome.Record.WorkspacePath,
		["runs"] = outcome.Record.Runs,
		["structural_driver"] = outcome.Record.StructuralDriver,
		["metadata_repo"] = storeDir,
		["metadata_tags"] = outcome.RecordTags,
		["component"] = outcome.Record.Components.Select(ComponentData).ToList()
	};

	/// <summary>Named as in <c>integration.toml</c>, so a caller reads the result and the stored record with the same keys.</summary>
	internal static IReadOnlyDictionary<string, object?> ComponentData(ComponentIntegration component) => new Dictionary<string, object?>
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
			["structural"] = step.Structural is { } structural ? IntegrationRecord.Snake(structural) : null,
			["structural_conflicts"] = step.StructuralConflicts,
			["structural_warnings"] = step.StructuralWarnings,
			["outcome"] = IntegrationRecord.Snake(step.Outcome),
			["commit"] = step.Commit,
			["note"] = step.Note,
			["brief"] = step.Brief
		}).ToList()
	};
}
