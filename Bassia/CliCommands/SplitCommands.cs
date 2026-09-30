namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Git;
using Bassia.Split;
using Bassia.Ui;

/// <summary><c>bassia component split</c> and <c>bassia component survey</c>: breaking a component up as the monorepo grows.</summary>
internal static class SplitCommands
{
	private const int ListLimit = 200;

	// ----- bassia component split -----

	public static async Task<int> SplitAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var planSource = invocation.Require("plan");
		var text = planSource == "-" ? await Console.In.ReadToEndAsync() : await ReadPlanFileAsync(planSource);
		var problems = new List<string>();
		var plan = SplitPlan.Parse(text, problems);

		SplitAllocation allocation;
		try
		{
			// Everything else is checked against the component too, so one answer lists every problem.
			if (!plan.IsReadable)
			{
				throw new SplitPlanException(problems, [], []);
			}

			allocation = await ComponentSplitter.AllocateAsync(monorepo, plan, invocation.Get("name"), problems);
		}
		catch (SplitPlanException ex)
		{
			return ProgramCli.WriteResult(false, invocation.Command, ex.Message, new Dictionary<string, object?>
			{
				["problems"] = ex.Problems.ToList(),
				["unallocated"] = ex.Unallocated.Take(ListLimit).ToList(),
				["unallocated_count"] = ex.Unallocated.Count,
				["ambiguous"] = ex.Ambiguous.Take(ListLimit).ToList(),
				["ambiguous_count"] = ex.Ambiguous.Count
			});
		}

		var graph = new ComponentGraph(allocation.After);
		if (invocation.Has("dry-run"))
		{
			return ProgramCli.WriteResult(true, invocation.Command,
				$"The plan is valid: '{allocation.Source.Name}' ({allocation.Branch} at {allocation.TipCommit[..10]}) would split into " +
				$"{string.Join(", ", plan.Parts.Select(part => part.Name))}. Nothing was changed.",
				new Dictionary<string, object?>
				{
					["dry_run"] = true,
					["source"] = allocation.Source.Name,
					["branch"] = allocation.Branch,
					["source_commit"] = allocation.TipCommit,
					["shared"] = allocation.Shared.Select(file => file.Path).Take(ListLimit).ToList(),
					["dropped"] = allocation.Dropped.Select(file => file.Path).Take(ListLimit).ToList(),
					["dropped_count"] = allocation.Dropped.Count,
					["table"] = new TomlText(AllocationTable(allocation)),
					["graph"] = new TomlText(graph.RenderText(ascii: true)),
					["part"] = plan.Parts.Select((part, index) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
					{
						["name"] = part.Name,
						["path"] = monorepo.SourceRepoDir(part.Name),
						["files"] = allocation.Files[index].Count,
						["bytes"] = allocation.Files[index].Sum(file => file.Size),
						["folders"] = Folders(allocation.Files[index]),
						["references"] = allocation.After.First(component => component.Name == part.Name).References.Select(ComponentCommands.Describe).ToList()
					}).ToList(),
					["referrer"] = Referrers(allocation)
				});
		}

		var runs = await new RunMetadataStore(new GitClient(monorepo.Root), monorepo.RunsRepoDir).ListLatestAsync();
		if (ComponentCommands.LiveRunsPerComponent(monorepo, runs).GetValueOrDefault(allocation.Source.Name) is > 0 and var live)
		{
			throw new MonorepoException($"{live} run(s) on '{allocation.Source.Name}' are live; wait for them or stop them before splitting it.");
		}

		var result = await ComponentSplitter.SplitAsync(monorepo, allocation);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Split '{allocation.Source.Name}' into {string.Join(", ", result.Parts.Select(part => $"{part.Name} ({part.Commits} commits)"))}; " +
			$"'{allocation.Source.Name}' is retired and its repository kept at '{allocation.SourceDir}'.",
			new Dictionary<string, object?>
			{
				["split_id"] = result.Id,
				["tag"] = result.SourceTag,
				["source"] = allocation.Source.Name,
				["source_path"] = allocation.SourceDir,
				["branch"] = allocation.Branch,
				["source_commit"] = allocation.TipCommit,
				["meta_repo_commit"] = result.MetaRepoCommit,
				["unmerged_results"] = result.UnmergedResults.ToList(),
				["table"] = new TomlText(AsciiTable.Render(["PART", "FILES", "BYTES", "COMMITS", "TAGS", "HEAD"],
					result.Parts.Select(part => (IReadOnlyList<string>)
					[
						part.Name, part.Files.ToString(), part.Bytes.ToString(), part.Commits.ToString(), part.Tags.Count.ToString(), part.Head[..10]
					]))),
				["graph"] = new TomlText(graph.RenderText(ascii: true)),
				["part"] = result.Parts.Select(part => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = part.Name,
					["path"] = part.Path,
					["head"] = part.Head,
					["files"] = part.Files,
					["bytes"] = part.Bytes,
					["commits"] = part.Commits,
					["renamed_paths"] = part.RenamedPaths,
					["tags"] = part.Tags.ToList(),
					["dropped_tags"] = part.DroppedTags.ToList(),
					["references"] = allocation.After.First(component => component.Name == part.Name).References.Select(ComponentCommands.Describe).ToList(),
					["select"] = $"{part.Name}@{result.SourceTag}"
				}).ToList(),
				["referrer"] = Referrers(allocation)
			});
	}

	private static async Task<string> ReadPlanFileAsync(string path)
	{
		var full = Path.GetFullPath(path);
		return File.Exists(full) ? await File.ReadAllTextAsync(full) : throw new MonorepoException($"The plan file '{full}' does not exist.");
	}

	private static string AllocationTable(SplitAllocation allocation) =>
		AsciiTable.Render(["PART", "FILES", "BYTES", "FOLDERS"],
			allocation.Plan.Parts.Select((part, index) => (IReadOnlyList<string>)
			[
				part.Name, allocation.Files[index].Count.ToString(), allocation.Files[index].Sum(file => file.Size).ToString(),
				string.Join(",", Folders(allocation.Files[index]))
			]), 120);

	/// <summary>The distinct top two folder levels of the files (<c>.</c> for files at the top).</summary>
	private static List<string> Folders(IEnumerable<TipFile> files) =>
		files.Select(file => file.Path.Split('/'))
			.Select(segments => segments.Length == 1 ? "." : string.Join('/', segments.Take(Math.Min(2, segments.Length - 1))))
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.Take(ListLimit)
			.ToList();

	private static List<IReadOnlyDictionary<string, object?>> Referrers(SplitAllocation allocation) =>
		allocation.Referrers.Select(referrer => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["name"] = referrer.Name,
			["references"] = referrer.References.Select(ComponentCommands.Describe).ToList()
		}).ToList();

	// ----- bassia component survey -----

	public static async Task<int> SurveyAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var component = ComponentCommands.RequireComponent(monorepo, invocation.Require("name"));
		var repository = ComponentCommands.RequireRepo(monorepo, component.Name);
		var branch = invocation.Get("branch");
		if (branch is null)
		{
			var head = await GitClient.In(repository).RunAsync(["symbolic-ref", "--short", "HEAD"]);
			branch = head.ExitCode == 0 ? head.Output.Trim() : "main";
		}

		if ((await GitClient.In(repository).RunAsync(["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}"])).ExitCode != 0)
		{
			throw new MonorepoException($"Component '{component.Name}' has no branch '{branch}'.");
		}

		var survey = await ComponentSurvey.RunAsync(repository, component.Name, branch,
			invocation.Int("depth", 2, 1, 10), invocation.Int("limit", 5000, 0), invocation.Int("pairs", 30, 0, 1000));

		return ProgramCli.WriteResult(true, invocation.Command,
			$"Component '{component.Name}' ({branch}): {survey.Files} file(s), {survey.Bytes} byte(s), {survey.Folders.Count} folder(s), {survey.CommitsRead} commit(s) read.",
			new Dictionary<string, object?>
			{
				["name"] = component.Name,
				["branch"] = branch,
				["files"] = survey.Files,
				["bytes"] = survey.Bytes,
				["commits_read"] = survey.CommitsRead,
				["references"] = component.References.Select(ComponentCommands.Describe).ToList(),
				["referenced_by"] = new ComponentGraph(monorepo.Components).ReferrersOf(component.Name).Select(referrer => referrer.Name).ToList(),
				["top_level_files"] = survey.TopLevelFiles.Select(file => file.Path).ToList(),
				["table"] = new TomlText(AsciiTable.Render(["FOLDER", "FILES", "BYTES", "COMMITS"],
					survey.Folders.Select(folder => (IReadOnlyList<string>)[folder.Path, folder.Files.ToString(), folder.Bytes.ToString(), folder.Commits.ToString()]), 120)),
				["plan"] = new TomlText(survey.PlanSkeleton),
				["folder"] = survey.Folders.Select(folder => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["path"] = folder.Path,
					["files"] = folder.Files,
					["bytes"] = folder.Bytes,
					["commits"] = folder.Commits
				}).ToList(),
				["pair"] = survey.Pairs.Select(pair => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["folders"] = new List<string> { pair.First, pair.Second },
					["commits"] = pair.Commits
				}).ToList()
			});
	}
}
