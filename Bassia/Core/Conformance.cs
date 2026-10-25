namespace Bassia.Core;

using Bassia.Storage;

/// <summary>
/// A conformance rule of <c>components.toml</c> (a <c>[[conformance]]</c> table): what every component with a tag
/// matching <paramref name="Components"/> must have - <paramref name="Files"/> at the tip of its default branch (a path
/// is met by the file itself or by any file below it), <paramref name="Tasks"/> its <c>bassia.toml</c> defines, owners
/// (<paramref name="Owners"/>) and exports (<paramref name="Exports"/>).
/// </summary>
internal sealed record ConformanceRule(string Components, IReadOnlyList<string> Files, IReadOnlyList<string> Tasks, bool Owners = false, bool Exports = false,
	string? Description = null)
{
	/// <summary>The rule as one line: <c>type:lib needs README.md, task test, owners</c>.</summary>
	public string Text => $"{Components} needs " + string.Join(", ", Files
		.Concat(Tasks.Select(task => $"task {task}"))
		.Concat(Owners ? ["owners"] : [])
		.Concat(Exports ? ["exports"] : []));

	/// <summary>Throws when the rule cannot be applied: no pattern, a bad pattern or path, or nothing required.</summary>
	public void Validate(string where)
	{
		if (string.IsNullOrWhiteSpace(Components))
		{
			throw new MonorepoException($"{where} has no 'components'.");
		}

		TagPattern.Parse(Components, where);
		if (Files.Count == 0 && Tasks.Count == 0 && !Owners && !Exports)
		{
			throw new MonorepoException($"{where} ({Components}) requires nothing: give files, tasks, owners = true or exports = true.");
		}

		foreach (var file in Files.Where(file => string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file) || file.Replace('\\', '/').Split('/').Any(segment => segment is "" or "." or "..")))
		{
			throw new MonorepoException($"{where}: '{file}' must be a relative path inside the component.");
		}
	}

	/// <summary>Whether the rule applies to <paramref name="component"/>: one of its tags (the implicit one included) matches.</summary>
	public bool AppliesTo(ComponentDefinition component) => Boundaries.TagsOf(component).Any(TagPattern.Parse(Components, "conformance").Matches);
}

/// <summary>One requirement of a conformance rule a component does not meet.</summary>
internal sealed record ConformanceFinding(string Component, string Rule, string Missing, string Message);

/// <summary>Checks components against the conformance rules.</summary>
internal static class ConformanceChecker
{
	/// <summary>
	/// What <paramref name="component"/> at <paramref name="commit"/> (null: it has no history) lacks of the rules that
	/// apply to it, in rule order.
	/// </summary>
	public static async Task<IReadOnlyList<ConformanceFinding>> CheckAsync(ComponentRegistry registry, ComponentDefinition component, IComponentStore? store, string? commit)
	{
		var rules = registry.Conformance.Where(rule => rule.AppliesTo(component)).ToList();
		var findings = new List<ConformanceFinding>();
		if (rules.Count == 0)
		{
			return findings;
		}

		var files = store is not null && commit is not null ? (await store.ListFilesAsync(commit)).Select(file => file.Path).ToList() : [];
		var tasks = store is not null && commit is not null ? await TaskFile.ReadAsync(store, component.Name, commit) : new Dictionary<string, ComponentTask>();
		foreach (var rule in rules)
		{
			void Missing(string what, string message) => findings.Add(new ConformanceFinding(component.Name, rule.Text, what, $"{component.Name}: {message} ({rule.Text})."));

			foreach (var file in rule.Files.Select(file => file.Replace('\\', '/').TrimEnd('/')))
			{
				if (!files.Any(path => path == file || path.StartsWith(file + "/", StringComparison.Ordinal)))
				{
					Missing(file, $"has no '{file}'");
				}
			}

			foreach (var task in rule.Tasks.Where(task => !tasks.ContainsKey(task)))
			{
				Missing($"task {task}", $"defines no task '{task}' in its {TaskFile.FileName}");
			}

			if (rule.Owners && component.Owners.Count == 0)
			{
				Missing("owners", "has no owners");
			}

			if (rule.Exports && component.Exports is null)
			{
				Missing("exports", "declares no exports");
			}
		}

		return findings;
	}

	/// <summary>Every component's findings, each read at the tip of its default branch.</summary>
	public static async Task<IReadOnlyList<ConformanceFinding>> CheckAllAsync(ComponentRegistry registry, IComponentStorage storage, IEnumerable<ComponentDefinition>? components = null)
	{
		var findings = new List<ConformanceFinding>();
		foreach (var component in components ?? registry.Components)
		{
			IComponentStore? store = null;
			string? tip = null;
			if (storage.Exists(component.Name))
			{
				store = storage.Open(component.Name);
				tip = await store.DefaultBranchAsync() is { } branch ? await store.BranchTipAsync(branch) : null;
			}

			findings.AddRange(await CheckAsync(registry, component, store, tip));
		}

		return findings;
	}
}
