namespace Bassia.Split;

using System.Text;
using Bassia.Git;
using Bassia.Graph;
using Tomlyn;
using Tomlyn.Model;

/// <summary>A split plan that does not fit the component or the monorepo: every problem, reported together.</summary>
internal sealed class SplitPlanException(IReadOnlyList<string> problems, IReadOnlyList<string> unallocated, IReadOnlyList<string> ambiguous)
	: Exception(problems.Count == 1 ? $"The split plan has a problem: {problems[0]}" : $"The split plan has {problems.Count} problems; the first: {problems[0]}")
{
	public IReadOnlyList<string> Problems { get; } = problems;
	public IReadOnlyList<string> Unallocated { get; } = unallocated;
	public IReadOnlyList<string> Ambiguous { get; } = ambiguous;
}

/// <summary>A checked plan: the source, its branch and tip, and the tip's files allocated to the parts.</summary>
internal sealed class SplitAllocation
{
	public required SplitPlan Plan { get; init; }
	public required ComponentDefinition Source { get; init; }
	public required string SourceDir { get; init; }
	public required string Branch { get; init; }
	public required string TipCommit { get; init; }

	/// <summary>The tip's files per part (<c>shared</c> files included), in plan order.</summary>
	public required IReadOnlyList<IReadOnlyList<TipFile>> Files { get; init; }
	public required IReadOnlyList<TipFile> Shared { get; init; }
	public required IReadOnlyList<TipFile> Dropped { get; init; }

	/// <summary>Owner of each tip path: a part index, -1 for shared, -2 for dropped.</summary>
	public required IReadOnlyDictionary<string, int> TipOwner { get; init; }

	/// <summary>Every component after the split: the parts added, referrers rewired, the source gone; parts in dependency order.</summary>
	public required IReadOnlyList<ComponentDefinition> After { get; init; }
	public required IReadOnlyList<SplitReferrer> Referrers { get; init; }

	public IReadOnlyList<ComponentDefinition> PartDefinitions =>
		After.Where(component => Plan.Parts.Any(part => part.Name == component.Name)).ToList();
}

/// <summary>A part as built: its repository, commits and tags.</summary>
internal sealed record SplitPartResult(string Name, string Path, int Files, long Bytes, int Commits, string Head, IReadOnlyList<string> Tags, IReadOnlyList<string> DroppedTags, int RenamedPaths);

internal sealed record SplitResult(string Id, SplitAllocation Allocation, IReadOnlyList<SplitPartResult> Parts, string SourceTag, string? MetaRepoCommit, IReadOnlyList<string> UnmergedResults);

/// <summary>
/// Breaks one component into several according to a <see cref="SplitPlan"/>: checks the plan against the component's
/// tip, rewrites the history for each part into a new repository, verifies each part's tip, and only then registers the
/// parts, rewires the references and retires the source. The source repository is never rewritten.
/// </summary>
internal static class ComponentSplitter
{
	public const string IdPrefix = "split-";
	public const string TagPrefix = "split/";
	public const string Trailer = "Split-from";

	public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N");

	public static string Key(string id) => id[IdPrefix.Length..];

	public static string TagName(string id) => TagPrefix + Key(id);

	// ----- checking a plan -----

	/// <summary>Checks <paramref name="plan"/> against the monorepo and the source's tip; throws <see cref="SplitPlanException"/> listing every problem.</summary>
	public static async Task<SplitAllocation> AllocateAsync(Monorepo monorepo, SplitPlan plan, string? sourceName, List<string> problems)
	{
		if (sourceName is not null && plan.Source is not null && plan.Source != sourceName)
		{
			problems.Add($"-name names '{sourceName}' but the plan's source is '{plan.Source}'.");
		}

		var name = sourceName ?? plan.Source;
		if (name is null)
		{
			problems.Add("The plan has no 'source'; name the component to split with 'source = \"<component>\"' or -name.");
			throw Fail(problems);
		}

		var source = monorepo.FindComponent(name);
		var sourceDir = monorepo.SourceRepoDir(name);
		if (source is null)
		{
			problems.Add($"The source '{name}' is not a registered component.");
			throw Fail(problems);
		}

		if (!Directory.Exists(sourceDir))
		{
			problems.Add($"The source '{name}' has no repository at '{sourceDir}'.");
			throw Fail(problems);
		}

		var git = GitClient.In(sourceDir);
		var branch = plan.Branch;
		if (branch is null)
		{
			var head = await git.RunAsync(["symbolic-ref", "--short", "HEAD"]);
			branch = head.ExitCode == 0 ? head.Output.Trim() : "main";
		}

		var tip = await git.RunAsync(["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}"]);
		if (tip.ExitCode != 0)
		{
			problems.Add($"The source '{name}' has no branch '{branch}'.");
			throw Fail(problems);
		}

		var files = await ListTipAsync(sourceDir, branch);
		var perPart = plan.Parts.Select(_ => new List<TipFile>()).ToList();
		var shared = new List<TipFile>();
		var dropped = new List<TipFile>();
		var owner = new Dictionary<string, int>(StringComparer.Ordinal);
		var unallocated = new List<string>();
		var ambiguous = new List<string>();

		foreach (var file in files)
		{
			var isShared = plan.Shared.Matches(file.Path);
			var isDropped = plan.Drop.Matches(file.Path);
			if (isShared && isDropped)
			{
				ambiguous.Add($"{file.Path} (shared, drop)");
				continue;
			}

			if (isShared || isDropped)
			{
				(isShared ? shared : dropped).Add(file);
				owner[file.Path] = isShared ? -1 : -2;
				continue;
			}

			var matching = Enumerable.Range(0, plan.Parts.Count).Where(index => plan.Parts[index].Paths.Matches(file.Path)).ToList();
			switch (matching.Count)
			{
				case 0:
					unallocated.Add(file.Path);
					break;
				case 1:
					perPart[matching[0]].Add(file);
					owner[file.Path] = matching[0];
					break;
				default:
					ambiguous.Add($"{file.Path} ({string.Join(", ", matching.Select(index => plan.Parts[index].Name))})");
					break;
			}
		}

		if (unallocated.Count > 0)
		{
			problems.Add($"{unallocated.Count} file(s) at the tip of '{name}' belong to no part, e.g. {string.Join(", ", unallocated.Take(5))}. " +
				"Allocate them to a part, or list them under 'shared' or 'drop'.");
		}

		if (ambiguous.Count > 0)
		{
			problems.Add($"{ambiguous.Count} file(s) match more than one part, e.g. {string.Join("; ", ambiguous.Take(5))}. " +
				"Narrow the patterns (a '!pattern' excludes), or list shared files under 'shared'.");
		}

		for (var index = 0; index < plan.Parts.Count; index++)
		{
			if (perPart[index].Count == 0)
			{
				problems.Add($"Part '{plan.Parts[index].Name}' gets no file at the tip of '{name}'.");
			}

			perPart[index].AddRange(shared);
		}

		var after = CheckRegistration(monorepo, plan, source, problems, out var referrers);

		if (problems.Count > 0)
		{
			throw Fail(problems, unallocated, ambiguous);
		}

		return new SplitAllocation
		{
			Plan = plan,
			Source = source,
			SourceDir = sourceDir,
			Branch = branch,
			TipCommit = tip.Output.Trim(),
			Files = perPart,
			Shared = shared,
			Dropped = dropped,
			TipOwner = owner,
			After = after,
			Referrers = referrers
		};
	}

	/// <summary>Checks names and references, and returns the components as they will be after the split.</summary>
	private static List<ComponentDefinition> CheckRegistration(Monorepo monorepo, SplitPlan plan, ComponentDefinition source, List<string> problems, out List<SplitReferrer> referrers)
	{
		var partNames = plan.Parts.Select(part => part.Name).ToHashSet(StringComparer.Ordinal);
		foreach (var part in plan.Parts)
		{
			if (monorepo.FindComponent(part.Name) is not null)
			{
				problems.Add($"Part '{part.Name}': a component of that name is already registered.");
			}
			else if (Directory.Exists(monorepo.SourceRepoDir(part.Name)) || File.Exists(monorepo.SourceRepoDir(part.Name)))
			{
				problems.Add($"Part '{part.Name}': '{monorepo.SourceRepoDir(part.Name)}' already exists.");
			}
		}

		bool Known(string name) => partNames.Contains(name) || (name != source.Name && monorepo.FindComponent(name) is not null);

		var parts = new List<ComponentDefinition>();
		foreach (var part in plan.Parts)
		{
			var references = part.References ?? source.References;
			foreach (var reference in references)
			{
				if (reference.Name == part.Name)
				{
					problems.Add($"Part '{part.Name}' cannot reference itself.");
				}
				else if (reference.Name == source.Name)
				{
					problems.Add($"Part '{part.Name}' references the source '{source.Name}', which the split retires; reference its parts instead.");
				}
				else if (!Known(reference.Name))
				{
					problems.Add($"Part '{part.Name}' references '{reference.Name}', which is neither registered nor a part.");
				}
			}

			parts.Add(new ComponentDefinition(part.Name, part.Url ?? "", references));
		}

		var graph = new ComponentGraph(monorepo.Components);
		var referring = graph.ReferrersOf(source.Name).Select(reference => reference.Name).ToHashSet(StringComparer.Ordinal);
		var rewiring = new List<SplitReferrer>();
		foreach (var referrer in plan.Referrers)
		{
			if (!referring.Contains(referrer.Name))
			{
				problems.Add($"Referrer '{referrer.Name}' does not reference the source '{source.Name}'" +
					(referring.Count == 0 ? "; nothing does." : $"; only {string.Join(", ", referring)} do."));
				continue;
			}

			foreach (var reference in referrer.References.Where(reference => !Known(reference.Name) || reference.Name == referrer.Name))
			{
				problems.Add(reference.Name == source.Name
					? $"Referrer '{referrer.Name}' still references the source '{source.Name}', which the split retires."
					: reference.Name == referrer.Name
						? $"Referrer '{referrer.Name}' cannot reference itself."
						: $"Referrer '{referrer.Name}' references '{reference.Name}', which is neither registered nor a part.");
			}

			rewiring.Add(referrer);
		}

		// A referrer the plan does not mention references every part instead of the source.
		foreach (var name in referring.Where(name => plan.Referrers.All(referrer => referrer.Name != name)))
		{
			var component = monorepo.FindComponent(name)!;
			var references = component.References
				.SelectMany(reference => reference.Name == source.Name ? plan.Parts.Select(part => new ComponentReference(part.Name, part.Name)) : [reference])
				.DistinctBy(reference => reference.Name)
				.ToList();
			rewiring.Add(new SplitReferrer(name, references));
		}

		referrers = rewiring;

		var after = monorepo.Components
			.Where(component => component.Name != source.Name)
			.Select(component => rewiring.FirstOrDefault(referrer => referrer.Name == component.Name) is { } rewired
				? component with { References = rewired.References }
				: component)
			.ToList();

		// Parts in dependency order (a part after the parts it references), so each can be registered in turn.
		var ordered = new List<ComponentDefinition>();
		var visiting = new HashSet<string>(StringComparer.Ordinal);
		void Visit(ComponentDefinition part, Stack<string> path)
		{
			if (ordered.Contains(part))
			{
				return;
			}

			if (!visiting.Add(part.Name))
			{
				problems.Add($"The parts reference each other in a cycle: {string.Join(" -> ", path.Reverse().SkipWhile(name => name != part.Name).Append(part.Name))}.");
				return;
			}

			path.Push(part.Name);
			foreach (var reference in part.References)
			{
				if (parts.FirstOrDefault(candidate => candidate.Name == reference.Name) is { } dependency)
				{
					Visit(dependency, path);
				}
			}

			path.Pop();
			if (!ordered.Contains(part))
			{
				ordered.Add(part);
			}
		}

		foreach (var part in parts)
		{
			Visit(part, new Stack<string>());
		}

		after.AddRange(ordered);
		if (problems.Count == 0)
		{
			CheckAcyclic(after, problems);
		}

		return after;
	}

	private static void CheckAcyclic(IReadOnlyList<ComponentDefinition> components, List<string> problems)
	{
		var state = new Dictionary<string, bool>(StringComparer.Ordinal);
		bool Visit(ComponentDefinition component, List<string> path)
		{
			if (state.TryGetValue(component.Name, out var done))
			{
				if (!done)
				{
					problems.Add($"After the split the components would reference each other in a cycle: {string.Join(" -> ", path.SkipWhile(name => name != component.Name).Append(component.Name))}.");
				}

				return done;
			}

			state[component.Name] = false;
			path.Add(component.Name);
			foreach (var reference in component.References)
			{
				if (components.FirstOrDefault(candidate => candidate.Name == reference.Name) is { } next && !Visit(next, path))
				{
					return false;
				}
			}

			path.RemoveAt(path.Count - 1);
			state[component.Name] = true;
			return true;
		}

		foreach (var component in components)
		{
			if (!Visit(component, []))
			{
				return;
			}
		}
	}

	private static SplitPlanException Fail(List<string> problems, List<string>? unallocated = null, List<string>? ambiguous = null) =>
		new(problems, unallocated ?? [], ambiguous ?? []);

	/// <summary>The files at the tip of <paramref name="branch"/>, with mode, object and size.</summary>
	internal static async Task<List<TipFile>> ListTipAsync(string repository, string branch)
	{
		var files = new List<TipFile>();
		foreach (var entry in GitBinary.SplitNul(await GitBinary.RunAsync(repository, ["ls-tree", "-r", "-z", "-l", "--full-tree", $"refs/heads/{branch}"])))
		{
			// <mode> SP <type> SP <object> SP+ <size> TAB <path>
			var tab = entry.IndexOf('\t');
			var fields = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			files.Add(new TipFile(entry[(tab + 1)..], fields[0], fields[2], long.TryParse(fields[3], out var size) ? size : 0));
		}

		return files;
	}

	// ----- performing a split -----

	public static async Task<SplitResult> SplitAsync(Monorepo monorepo, SplitAllocation allocation)
	{
		var id = NewId();
		var tag = TagName(id);
		var plan = allocation.Plan;
		var sourceDir = allocation.SourceDir;
		var source = GitClient.In(sourceDir);
		var created = new List<string>();
		var componentsPath = ComponentsFile.PathOf(monorepo.Root);
		var componentsText = File.ReadAllText(componentsPath);
		var sourceTagged = false;

		try
		{
			var tags = await ListTagsAsync(sourceDir, allocation.Branch);
			var export = await FastExport.ReadAsync(sourceDir, allocation.Branch, tags.Where(item => item.Annotated).Select(item => item.Name).ToList());
			var renames = plan.FollowRenames ? await ListRenamesAsync(sourceDir, allocation.Branch, export) : [];
			var ownership = new Ownership(plan, allocation.TipOwner, export.Commits, renames);
			var rewriter = new HistoryRewriter(export, ownership, (parent, commit) => DiffAsync(sourceDir, export.Commits[parent].OriginalId, export.Commits[commit].OriginalId));
			var lightweight = tags.Where(item => !item.Annotated && export.IndexOf.ContainsKey(item.Commit))
				.Select(item => new LightweightTag(item.Name, export.IndexOf[item.Commit])).ToList();
			var ident = (await source.RunOrThrowAsync(["var", "GIT_COMMITTER_IDENT"])).Trim();
			var objects = Path.Combine(await source.RunOrThrowAsync(["rev-parse", "--absolute-git-dir"]), "objects");
			var timestamp = DateTimeOffset.UtcNow.ToString("o");

			var parts = new List<SplitPartResult>();
			for (var index = 0; index < plan.Parts.Count; index++)
			{
				var part = plan.Parts[index];
				var record = RecordMessage(id, allocation, part, timestamp);
				var history = await rewriter.RewriteAsync(index, allocation.Branch, $"{Trailer}: {allocation.Source.Name}@",
					export.IndexOf[allocation.TipCommit], lightweight, ident, record, tag);

				var partDir = monorepo.SourceRepoDir(part.Name);
				var gitDir = Path.Combine(partDir, ".git");
				created.Add(partDir);
				await GitClient.In(monorepo.Root).RunOrThrowAsync(["init", "--bare", "--quiet", gitDir]);
				var target = GitClient.In(gitDir);

				// Borrow the source's objects while importing, then copy what the part reaches and stop borrowing.
				var alternates = Path.Combine(gitDir, "objects", "info", "alternates");
				await File.WriteAllTextAsync(alternates, objects.Replace('\\', '/') + "\n");
				await GitBinary.RunAsync(gitDir, ["fast-import", "--quiet"], history.Stream);
				await target.RunOrThrowAsync(["symbolic-ref", "HEAD", $"refs/heads/{allocation.Branch}"]);
				await target.RunOrThrowAsync(["repack", "-a", "-d", "-q"]);
				File.Delete(alternates);
				await target.RunOrThrowAsync(["fsck", "--connectivity-only", "--no-dangling", "--no-progress"]);
				if (part.Url is not null)
				{
					await target.RunOrThrowAsync(["remote", "add", "origin", part.Url]);
				}

				await VerifyTipAsync(gitDir, allocation.Branch, part.Name, allocation.Files[index]);
				parts.Add(new SplitPartResult(part.Name, partDir, allocation.Files[index].Count, allocation.Files[index].Sum(file => file.Size),
					history.Commits, await target.RunOrThrowAsync(["rev-parse", $"refs/heads/{allocation.Branch}"]),
					history.Tags, history.DroppedTags, ownership.ClaimedPaths(index)));
			}

			var unmerged = (await source.RunOrThrowAsync(["for-each-ref", "--no-merged", $"refs/heads/{allocation.Branch}", "--format=%(refname:strip=2)",
				"refs/tags/agent/", "refs/tags/integration/"])).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

			await source.RunOrThrowAsync(["tag", "-a", tag, "-m", RecordMessage(id, allocation, null, timestamp), allocation.TipCommit]);
			sourceTagged = true;

			foreach (var part in allocation.PartDefinitions)
			{
				ComponentsFile.Add(monorepo.Root, part.Name, part.Url, part.References);
			}

			foreach (var referrer in allocation.Referrers)
			{
				ComponentsFile.SetReferences(monorepo.Root, referrer.Name, referrer.References);
			}

			ComponentsFile.Remove(monorepo.Root, allocation.Source.Name);
			var commit = await MonorepoCommands.CommitMetaRepoAsync(monorepo.Root, RecordMessage(id, allocation, null, timestamp));
			return new SplitResult(id, allocation, parts, tag, commit, unmerged);
		}
		catch
		{
			if (File.ReadAllText(componentsPath) != componentsText)
			{
				File.WriteAllText(componentsPath, componentsText);
			}

			if (sourceTagged)
			{
				await source.RunAsync(["tag", "-d", tag]);
			}

			foreach (var directory in created.Where(Directory.Exists))
			{
				DeleteDirectory(directory);
			}

			throw;
		}
	}

	/// <summary>The tip of the part must hold exactly its allocated files, each with the source tip's mode and content.</summary>
	private static async Task VerifyTipAsync(string gitDir, string branch, string part, IReadOnlyList<TipFile> expected)
	{
		var actual = (await ListTipAsync(gitDir, branch)).ToDictionary(file => file.Path, StringComparer.Ordinal);
		var wanted = expected.ToDictionary(file => file.Path, StringComparer.Ordinal);
		var missing = wanted.Keys.Where(path => !actual.ContainsKey(path)).ToList();
		var extra = actual.Keys.Where(path => !wanted.ContainsKey(path)).ToList();
		var differing = wanted.Values.Where(file => actual.TryGetValue(file.Path, out var got) && (got.Object != file.Object || got.Mode != file.Mode)).Select(file => file.Path).ToList();
		if (missing.Count + extra.Count + differing.Count > 0)
		{
			throw new GitException($"The rewritten history of '{part}' does not end in its allocated files " +
				$"(missing: {Sample(missing)}; unexpected: {Sample(extra)}; different: {Sample(differing)}); nothing was changed.");
		}
	}

	private static string Sample(List<string> paths) => paths.Count == 0 ? "none" : string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? $" and {paths.Count - 5} more" : "");

	private sealed record SourceTag(string Name, bool Annotated, string Commit);

	/// <summary>The tags reachable from the branch, each with the commit it (finally) points to.</summary>
	private static async Task<List<SourceTag>> ListTagsAsync(string repository, string branch)
	{
		var output = await GitClient.In(repository).RunOrThrowAsync(["for-each-ref", "--merged", $"refs/heads/{branch}",
			"--format=%(refname:strip=2)%09%(objecttype)%09%(objectname)%09%(*objectname)", "refs/tags/"]);
		return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(line => line.Split('\t'))
			.Where(fields => fields.Length >= 3 && (fields[1] == "commit" || (fields[1] == "tag" && fields.Length == 4)))
			.Select(fields => new SourceTag(fields[0], fields[1] == "tag", fields[1] == "tag" ? fields[3] : fields[2]))
			.ToList();
	}

	/// <summary>Every rename on the branch (git's rename detection), as stream indices.</summary>
	private static async Task<List<Rename>> ListRenamesAsync(string repository, string branch, FastExport export)
	{
		var fields = GitBinary.SplitNul(await GitBinary.RunAsync(repository,
			["log", "-M", "--diff-filter=R", "--name-status", "-z", "--format=%x01%H", $"refs/heads/{branch}"]));
		var renames = new List<Rename>();
		var commit = -1;
		for (var i = 0; i < fields.Count; i++)
		{
			var field = fields[i].TrimStart('\n');
			if (field.StartsWith('\x01'))
			{
				commit = export.IndexOf.TryGetValue(field[1..], out var index) ? index : -1;
			}
			else if (field.StartsWith('R') && i + 2 < fields.Count)
			{
				if (commit >= 0)
				{
					renames.Add(new Rename(commit, fields[i + 1], fields[i + 2]));
				}

				i += 2;
			}
		}

		return renames;
	}

	/// <summary>The changes of <paramref name="commit"/> relative to <paramref name="parent"/> (full ids).</summary>
	private static async Task<List<FileChange>> DiffAsync(string repository, string parent, string commit)
	{
		var fields = GitBinary.SplitNul(await GitBinary.RunAsync(repository, ["diff-tree", "-r", "-z", "--no-renames", "--no-commit-id", parent, commit]));
		var changes = new List<FileChange>();
		for (var i = 0; i + 1 < fields.Count; i += 2)
		{
			// :<old mode> <new mode> <old object> <new object> <status>
			var header = fields[i].TrimStart(':').Split(' ');
			var path = fields[i + 1];
			changes.Add(header[4].StartsWith('D') ? new FileChange('D', "", "", path) : new FileChange('M', header[1], header[3], path));
		}

		return changes;
	}

	/// <summary>
	/// The split record: a subject and a TOML <c>[split]</c> table. Each part's record commit and the <c>split/&lt;id&gt;</c>
	/// tags carry it; the meta-repo commit carries it without a part.
	/// </summary>
	private static string RecordMessage(string id, SplitAllocation allocation, SplitPart? part, string created)
	{
		var plan = allocation.Plan;
		var record = new TomlTable
		{
			["id"] = id,
			["created"] = created,
			["source"] = allocation.Source.Name,
			["source_commit"] = allocation.TipCommit,
			["branch"] = allocation.Branch,
			["tag"] = TagName(id),
			["parts"] = ToArray(plan.Parts.Select(item => item.Name))
		};

		if (part is not null)
		{
			record["part"] = part.Name;
			record["paths"] = ToArray(part.Paths.Texts);
		}

		if (!plan.Shared.IsEmpty) record["shared"] = ToArray(plan.Shared.Texts);
		if (!plan.Drop.IsEmpty) record["drop"] = ToArray(plan.Drop.Texts);
		record["follow_renames"] = plan.FollowRenames;

		var subject = part is null
			? $"split({Key(id)[..8]}): {allocation.Source.Name} into {string.Join(", ", plan.Parts.Select(item => item.Name))}"
			: $"split({Key(id)[..8]}): {part.Name} from {allocation.Source.Name}";
		return $"{subject}\n\n{TomlSerializer.Serialize(new TomlTable { ["split"] = record })}";
	}

	private static TomlArray ToArray(IEnumerable<string> values)
	{
		var array = new TomlArray();
		foreach (var value in values)
		{
			array.Add(value);
		}

		return array;
	}

	private static void DeleteDirectory(string directory)
	{
		foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(directory, recursive: true);
	}
}
