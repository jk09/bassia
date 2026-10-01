namespace Bassia.Integration;

using System.Text;

/// <summary>
/// The brief a semantic merge hands its resolver: not just the conflicting hunks (those are in the files, in diff3
/// style so the common ancestor is visible), but <em>why</em> each side changed - the prompt that drove the incoming
/// run, the runs already integrated and what they were for, and the history and diff of both sides. That is the
/// difference to git's syntax-based merge, which only ever sees lines.
/// </summary>
internal static class MergeBrief
{
	/// <summary>Upper bound for each embedded diff, so a large change cannot drown the rest of the brief.</summary>
	public const int DiffLimit = 12_000;

	internal sealed record Side(string Title, IReadOnlyList<string> Rationales, string History, string Diff);

	/// <summary>The structural merge the semantic merge was started with: its driver, git's own conflicts and weave's warnings.</summary>
	internal sealed record StructuralInfo(string Driver, IReadOnlyList<string> GitConflicts, IReadOnlyList<string> Warnings);

	internal sealed record Input(
		string IntegrationId,
		string Component,
		string BaseRef,
		string BaseCommit,
		IntegrationStep Incoming,
		RunMetadata? IncomingRun,
		IReadOnlyList<string> Conflicts,
		IReadOnlyList<string> ConflictsWith,
		StructuralInfo? Structural,
		Side Ours,
		Side Theirs);

	public static string Render(Input input)
	{
		var brief = new StringBuilder();
		var incoming = input.Incoming;
		var shortRun = RunMetadata.Key(incoming.RunId);

		brief.AppendLine($"# Semantic merge: run {shortRun} into component '{input.Component}'");
		brief.AppendLine();
		brief.AppendLine($"You are integrating the outcome of the agentic run `{incoming.RunId}` into integration " +
			$"`{input.IntegrationId}` of the component `{input.Component}`. The current directory is that component's git " +
			$"working tree, with the merge of `{incoming.SourceTag}` already started and not committed.");
		brief.AppendLine();

		if (input.Structural is { } structural)
		{
			AppendStructural(brief, input, structural);
		}
		else if (input.Conflicts.Count > 0)
		{
			brief.AppendLine("Git's line-based merge stopped on conflicts in these files; each conflict is marked in the file " +
				"with `<<<<<<<` (the integration so far), `|||||||` (the common ancestor), `=======` and `>>>>>>>` (the incoming run):");
			brief.AppendLine();
			foreach (var file in input.Conflicts)
			{
				brief.AppendLine($"- `{file}`");
			}
		}
		else
		{
			brief.AppendLine("Git merged both sides without a textual conflict, but this merge was sent to you for a semantic " +
				"review: check that the combined result still does what both sides meant (duplicate definitions, renamed " +
				"symbols used by the other side, contradicting behaviour, broken references) and fix what does not.");
		}

		brief.AppendLine();
		brief.AppendLine("## Your task");
		brief.AppendLine();
		brief.AppendLine("1. Understand the intent of both sides from their rationale, history and diff below.");
		brief.AppendLine("2. Edit the files so the result fulfils both intents. Where they genuinely contradict, prefer the " +
			"incoming run for what it was explicitly asked to do and keep everything else of the integration.");
		brief.AppendLine("3. Remove every conflict marker. Keep the change minimal: do not refactor or reformat unrelated code.");
		brief.AppendLine("4. Do not commit, do not switch branches and do not touch other components - Bassia commits the " +
			"merge when you exit with code 0. Exit with a non-zero code if you cannot produce a correct merge.");
		brief.AppendLine("5. End with a short explanation of how you reconciled the two sides.");
		brief.AppendLine();

		brief.AppendLine("## The incoming change (theirs)");
		brief.AppendLine();
		brief.AppendLine($"- Run: `{incoming.RunId}`");
		brief.AppendLine($"- Result tag: `{incoming.SourceTag}` ({incoming.SourceCommit})");
		if (input.IncomingRun is { } run)
		{
			brief.AppendLine($"- Selected baseline: `{run.Select}`");
			brief.AppendLine($"- Created: {run.Created}");
			brief.AppendLine($"- Agent command: `{run.Command}`");
		}

		brief.AppendLine($"- Rationale: {incoming.Rationale}");
		if (input.ConflictsWith.Count > 0)
		{
			brief.AppendLine($"- Collides on its own with the run(s): {string.Join(", ", input.ConflictsWith.Select(id => $"`{id}`"))}");
		}

		AppendSide(brief, input.Theirs);

		brief.AppendLine("## What is already integrated (ours)");
		brief.AppendLine();
		brief.AppendLine($"- Base: `{input.BaseRef}` ({input.BaseCommit})");
		if (input.Ours.Rationales.Count == 0)
		{
			brief.AppendLine("- No run has been integrated yet; ours is the base itself (plus anything the base branch gained since the run started).");
		}
		else
		{
			foreach (var rationale in input.Ours.Rationales)
			{
				brief.AppendLine($"- Integrated: {rationale}");
			}
		}

		AppendSide(brief, input.Ours);
		return brief.ToString();
	}

	/// <summary>
	/// The merge was started with the structural merge driver: say what git alone conflicted on, what weave merged of
	/// it, what is left and what weave warned about.
	/// </summary>
	private static void AppendStructural(StringBuilder brief, Input input, StructuralInfo structural)
	{
		var resolved = structural.GitConflicts.Except(input.Conflicts, StringComparer.Ordinal).ToList();
		brief.AppendLine($"The merge was started with weave (`{structural.Driver}`), an entity-level merge driver that merges " +
			"functions, classes and keys instead of lines; files weave does not understand were merged by git line by line.");
		brief.AppendLine();
		if (structural.GitConflicts.Count > 0)
		{
			brief.AppendLine($"Git's line-based merge alone conflicted in: {string.Join(", ", structural.GitConflicts.Select(file => $"`{file}`"))}.");
		}

		if (resolved.Count > 0)
		{
			brief.AppendLine($"Weave merged these of them without a conflict; check that the result is right: {string.Join(", ", resolved.Select(file => $"`{file}`"))}.");
		}

		brief.AppendLine();
		if (input.Conflicts.Count > 0)
		{
			brief.AppendLine("These files still conflict; each conflict is marked in the file with `<<<<<<<` (the integration so far), " +
				"`=======` and `>>>>>>>` (the incoming run):");
			brief.AppendLine();
			foreach (var file in input.Conflicts)
			{
				brief.AppendLine($"- `{file}`");
			}

			brief.AppendLine();
			brief.AppendLine(StructuralMerge.MarkerHelp());
		}
		else
		{
			brief.AppendLine("Nothing is left conflicted, but this merge was sent to you for a semantic review: check that the combined " +
				"result still does what both sides meant (duplicate definitions, renamed symbols used by the other side, contradicting " +
				"behaviour, broken references) and fix what does not.");
		}

		if (structural.Warnings.Count > 0)
		{
			brief.AppendLine();
			brief.AppendLine("Weave warned that its merge may not mean what both sides meant (e.g. an entity that depends on another " +
				"entity the other side changed, or a merged file that no longer parses). Check each of these in particular:");
			brief.AppendLine();
			brief.AppendLine("```json");
			foreach (var warning in structural.Warnings)
			{
				brief.AppendLine(warning);
			}

			brief.AppendLine("```");
		}
	}

	private static void AppendSide(StringBuilder brief, Side side)
	{
		brief.AppendLine();
		brief.AppendLine($"### {side.Title}: history since the common ancestor");
		brief.AppendLine();
		brief.AppendLine("```text");
		brief.AppendLine(side.History.Length == 0 ? "(no commits)" : side.History.TrimEnd());
		brief.AppendLine("```");
		brief.AppendLine();
		brief.AppendLine($"### {side.Title}: diff against the common ancestor");
		brief.AppendLine();
		brief.AppendLine("```diff");
		brief.AppendLine(side.Diff.Length == 0 ? "(no changes)" : Truncate(side.Diff).TrimEnd());
		brief.AppendLine("```");
		brief.AppendLine();
	}

	internal static string Truncate(string text) =>
		text.Length <= DiffLimit
			? text
			: text[..DiffLimit] + $"\n[... {text.Length - DiffLimit} more characters truncated; run git diff in the working tree to see all of it]";
}
