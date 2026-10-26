namespace Bassia.Core;

using System.Text.RegularExpressions;
using Bassia.Split;
using Bassia.Storage;

/// <summary>A config spec resolved against the monorepo: the mapped closure in dependency order and each component's mapping.</summary>
internal sealed record ResolvedView(IReadOnlyList<ComponentDefinition> Closure, IReadOnlyList<ViewComponent> Components)
{
	/// <summary>The load patterns per component, for a sparse materialization.</summary>
	public IReadOnlyDictionary<string, IReadOnlyList<string>> Load =>
		Components.ToDictionary(component => component.Name, component => component.Load, StringComparer.Ordinal);

	public IReadOnlyDictionary<string, string> Commits =>
		Components.ToDictionary(component => component.Name, component => component.Commit, StringComparer.Ordinal);

	/// <summary>The selection pinned at the resolved commits: what an agentic run started from the spec selects.</summary>
	public string PinnedSelect => string.Join(",", Components.Select(component => $"{component.Name}@{component.Commit}"));

	/// <summary>The load rules per component as <c>-load</c> takes them: <c>[!]&lt;component&gt;/&lt;pattern&gt;</c>.</summary>
	public IReadOnlyList<string> LoadRules =>
		Components.SelectMany(component => component.Load.Select(pattern => pattern.StartsWith('!') ? $"!{component.Name}/{pattern[1..]}" : $"{component.Name}/{pattern}")).ToList();
}

/// <summary>A file of a view: its component, its path there and in the view, its size (-1 when not held) and whether it is on disk.</summary>
internal sealed record ViewFile(string Component, string Path, string ViewPath, long Size, bool Loaded);

/// <summary>What <see cref="Views.LoadAsync"/> did for one path: the component and pattern it came to and the files materialized now.</summary>
internal sealed record ViewLoad(string Component, string Pattern, IReadOnlyList<string> Files);

/// <summary>What <see cref="Views.UpdateAsync"/> did with one component: moved from one commit to another, or why not.</summary>
internal sealed record ViewMove(string Component, string Version, string From, string To, bool Moved, string? Reason);

/// <summary>
/// Views: config specs (<see cref="ConfigSpec"/>) mapped onto folders. A view checks out every mapped component side by
/// side, references nested as links as in agentic runs, every working copy sparse: only the files its load patterns
/// match are materialized, and only their content is fetched. Loading more later hydrates and materializes more.
/// In a clone (<paramref name="depot"/> given) a component without a store is imported from the depot, history only.
/// </summary>
internal sealed class Views(MonorepoGraph graph, string root, string workspaceDir, Depot? depot)
{
	/// <summary>The branch every working copy of a view is on: <c>view/&lt;name&gt;</c>.</summary>
	public static string BranchOf(string view) => $"view/{view}";

	/// <summary>The local registry of views: <c>&lt;root&gt;/.views/&lt;name&gt;.toml</c>, naming each view's folder.</summary>
	public string IndexDir => Path.Combine(root, ".views");

	private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

	// ----- resolving a config spec -----

	/// <summary>
	/// Resolves <paramref name="spec"/>: every mapped component must be registered, every load rule must match a mapped
	/// component, and every version must name exactly one commit (a branch, a tag, or an unambiguous commit hash).
	/// </summary>
	public static async Task<ResolvedView> ResolveAsync(ConfigSpec spec, MonorepoGraph graph, Depot? depot)
	{
		foreach (var (line, name) in spec.Maps)
		{
			if (!graph.Registry.Contains(name))
			{
				throw new ViewException($"config spec line {line}: component '{name}' is not registered in components.toml.");
			}
		}

		var closure = graph.Registry.Closure(spec.Maps.Select(map => map.Component).Distinct(StringComparer.Ordinal));
		foreach (var rule in spec.Loads.Where(rule => !closure.Any(component => rule.Components.Matches(component.Name))))
		{
			throw new ViewException($"config spec line {rule.Line}: 'load' names no mapped component (mapped: {string.Join(", ", closure.Select(component => component.Name))}).");
		}

		var components = new List<ViewComponent>();
		foreach (var component in closure)
		{
			var store = await StoreAsync(graph, depot, component.Name);
			var rule = spec.SelectFor(component.Name);
			var (version, commit) = rule is null
				? await DefaultBranchAsync(store)
				: (rule.Version, await ResolveVersionAsync(store, rule.Version, $"config spec line {rule.Line}"));
			components.Add(new ViewComponent(component.Name, version, commit, component.Name, spec.LoadFor(component.Name)));
		}

		return new ResolvedView(closure, components);
	}

	private static async Task<IComponentStore> StoreAsync(MonorepoGraph graph, Depot? depot, string component) =>
		depot is not null ? await depot.EnsureStoreAsync(graph.Storage, component)
		: graph.Storage.Exists(component) ? graph.Storage.Open(component)
		: throw new ViewException($"Component '{component}' has no repository at '{graph.Storage.LocationOf(component)}'.");

	private static async Task<(string Version, string Commit)> DefaultBranchAsync(IComponentStore store)
	{
		var branch = await store.DefaultBranchAsync()
			?? throw new ViewException($"Component '{store.Name}' has no default branch (its head is detached); select its version with 'select {store.Name} <version>'.");
		var tip = await store.BranchTipAsync(branch)
			?? throw new ViewException($"Component '{store.Name}' has no commit on its default branch '{branch}'.");
		return (branch, tip);
	}

	/// <summary>A branch, a tag (annotated or not) or a commit hash; a hash prefix must mean one commit and no ref.</summary>
	public static async Task<string> ResolveVersionAsync(IComponentStore store, string version, string where)
	{
		var reference = await store.ResolveRefAsync(version);
		if (AgentRuns.IsHashSelector(version))
		{
			var candidates = await store.FindObjectsAsync(version);
			if (candidates.Count > 0)
			{
				if (reference is not null || candidates.Count > 1)
				{
					throw new ViewException($"{where}: '{version}' is ambiguous in component '{store.Name}'" +
						(reference is not null ? $": it names '{reference.FullName}' and is the prefix of {candidates[0].Kind} {candidates[0].Id}." : $": {string.Join(", ", candidates.Select(candidate => $"{candidate.Kind} {candidate.Id}"))}.") +
						" Use a longer hash or the full ref name.");
				}

				return candidates[0].Kind == "commit"
					? candidates[0].Id
					: throw new ViewException($"{where}: '{version}' is a {candidates[0].Kind} in component '{store.Name}', not a commit.");
			}
		}

		return reference?.Commit ?? throw new ViewException($"{where}: '{version}' is no branch, tag or commit of component '{store.Name}'.");
	}

	// ----- creating, finding and removing views -----

	public async Task<ViewManifest> CreateAsync(string name, string specText, string? path = null)
	{
		if (!NamePattern.IsMatch(name))
		{
			throw new ViewException($"'{name}' is not a valid view name: letters, digits, '.', '_' and '-', starting with a letter or digit.");
		}

		if (File.Exists(IndexPath(name)))
		{
			throw new ViewException($"View '{name}' already exists (at '{FolderOf(name)}'); remove it first with 'bassia view remove -name {name}'.");
		}

		var folder = Path.GetFullPath(path ?? Path.Combine(workspaceDir, $"view-{name}"));
		if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
		{
			throw new ViewException($"'{folder}' is not empty; a view needs an empty or new folder.");
		}

		var spec = ConfigSpec.Parse(specText);
		var resolved = await ResolveAsync(spec, graph, depot);
		var created = !Directory.Exists(folder);
		try
		{
			await Workspaces.MaterializeAsync(graph.Storage, resolved.Closure, resolved.Commits, folder, BranchOf(name), resolved.Load);
			var manifest = new ViewManifest
			{
				Name = name,
				Kind = ViewManifest.ViewKind,
				Root = root,
				Folder = folder,
				Spec = spec.Text,
				Created = RunMetadata.Timestamp(),
				Components = [.. resolved.Components]
			};
			manifest.Write();
			Directory.CreateDirectory(IndexDir);
			File.WriteAllText(IndexPath(name), $"# The folder of view '{name}'; see 'bassia view show -name {name}'.\npath = {ConfigFile.Quote(folder)}\n");
			return manifest;
		}
		catch
		{
			if (created)
			{
				Workspaces.Delete(folder);
			}
			else
			{
				foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList())
				{
					if (Directory.Exists(entry))
					{
						Workspaces.Delete(entry);
					}
					else
					{
						File.Delete(entry);
					}
				}
			}

			throw;
		}
	}

	private string IndexPath(string name) => Path.Combine(IndexDir, $"{name}.toml");

	private string FolderOf(string name) =>
		Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(File.ReadAllText(IndexPath(name)))?.TryGetValue("path", out var path) == true && path is string folder
			? folder
			: throw new ViewException($"'{IndexPath(name)}' names no folder.");

	/// <summary>Every registered view, by name, with its manifest; null when its folder is gone.</summary>
	public IReadOnlyList<(string Name, string Folder, ViewManifest? Manifest)> List()
	{
		if (!Directory.Exists(IndexDir))
		{
			return [];
		}

		return Directory.EnumerateFiles(IndexDir, "*.toml").Select(file => Path.GetFileNameWithoutExtension(file)).Order(StringComparer.Ordinal)
			.Select(name =>
			{
				var folder = FolderOf(name);
				return (name, folder, File.Exists(Path.Combine(folder, ViewManifest.FileName)) ? ViewManifest.Read(folder) : null);
			}).ToList();
	}

	public ViewManifest Open(string name)
	{
		if (!File.Exists(IndexPath(name)))
		{
			throw new ViewException($"There is no view '{name}'; 'bassia view list' lists the views.");
		}

		var folder = FolderOf(name);
		return File.Exists(Path.Combine(folder, ViewManifest.FileName))
			? ViewManifest.Read(folder)
			: throw new ViewException($"The folder of view '{name}' ('{folder}') is gone; remove the view with 'bassia view remove -name {name}'.");
	}

	/// <summary>
	/// Removes a view's folder and registration. A component with uncommitted changes keeps it unless
	/// <paramref name="force"/>: a view's changes are nowhere else.
	/// </summary>
	public async Task<IReadOnlyList<string>> RemoveAsync(string name, bool force)
	{
		if (!File.Exists(IndexPath(name)))
		{
			throw new ViewException($"There is no view '{name}'; 'bassia view list' lists the views.");
		}

		var folder = FolderOf(name);
		var changed = new List<string>();
		if (File.Exists(Path.Combine(folder, ViewManifest.FileName)))
		{
			var manifest = ViewManifest.Read(folder);
			foreach (var component in manifest.Components)
			{
				if (Directory.Exists(manifest.FolderOf(component)))
				{
					changed.AddRange((await graph.Storage.OpenWorkspace(component.Name, manifest.FolderOf(component)).ChangedPathsAsync()).Select(path => $"{component.Path}/{path}"));
				}
			}

			if (changed.Count > 0 && !force)
			{
				throw new ViewException($"View '{name}' has changes that would be lost: {string.Join(", ", changed.Take(10))}{(changed.Count > 10 ? ", ..." : "")}. Remove it anyway with -force.");
			}
		}

		Workspaces.Delete(folder);
		File.Delete(IndexPath(name));
		return changed;
	}

	// ----- what a view maps, and loading more of it -----

	/// <summary>
	/// Every file the view maps - from the history, without fetching content - with whether it is on disk, optionally only
	/// those matching <paramref name="pattern"/> (<c>&lt;component-pattern&gt;[/&lt;path-pattern&gt;]</c>).
	/// </summary>
	public async Task<IReadOnlyList<ViewFile>> FilesAsync(ViewManifest view, string? pattern = null)
	{
		var filter = pattern is null ? null : ConfigSpec.ParseLoad(pattern, "-path");
		var files = new List<ViewFile>();
		foreach (var component in view.Components.Where(component => filter is null || filter.Components.Matches(component.Name)))
		{
			var matcher = filter is null ? null : new PathPatterns([filter.Pattern]);
			foreach (var file in await graph.Storage.Open(component.Name).ListFilesAsync(component.Commit))
			{
				if (matcher is null || matcher.Matches(file.Path))
				{
					files.Add(new ViewFile(component.Name, file.Path, $"{component.Path}/{file.Path}", file.Size,
						File.Exists(Path.Combine(view.FolderOf(component), file.Path))));
				}
			}
		}

		return files;
	}

	/// <summary>
	/// Loads <paramref name="paths"/> (files, folders or patterns) into the view: relative to <paramref name="workingDirectory"/>
	/// when it is inside the view, else to the view's folder; the first segment is a component (or a pattern of them), and a
	/// path through a nested reference belongs to the referenced component. Every path must match a mapped file, or
	/// nothing is loaded. Each is recorded in the manifest.
	/// </summary>
	public async Task<IReadOnlyList<ViewLoad>> LoadAsync(ViewManifest view, IReadOnlyList<string> paths, string workingDirectory)
	{
		var targets = new List<(ViewComponent Component, string Pattern)>();
		foreach (var path in paths)
		{
			var relative = ViewRelative(view, path, workingDirectory);
			var rule = ConfigSpec.ParseLoad(relative, "-path");
			if (rule.Exclude)
			{
				throw new ViewException($"'{path}': a view loads files; it does not unload them.");
			}

			var matched = view.Components.Where(component => rule.Components.Matches(component.Name)).ToList();
			if (matched.Count == 0)
			{
				throw new ViewException($"'{path}' is in no component of view '{view.Name}' (mapped: {string.Join(", ", view.Components.Select(component => component.Name))}).");
			}

			var found = false;
			foreach (var component in matched)
			{
				var (owner, pattern) = Descend(view, component, rule.Pattern);
				var matcher = new PathPatterns([pattern]);
				if ((await graph.Storage.Open(owner.Name).ListFilesAsync(owner.Commit)).Any(file => matcher.Matches(file.Path)))
				{
					targets.Add((owner, pattern));
					found = true;
				}
			}

			if (!found)
			{
				throw new ViewException($"'{path}' matches no file that view '{view.Name}' maps; 'bassia view ls' lists them.");
			}
		}

		var loads = new List<ViewLoad>();
		foreach (var (component, pattern) in targets)
		{
			var workspace = graph.Storage.OpenWorkspace(component.Name, view.FolderOf(component));
			loads.Add(new ViewLoad(component.Name, pattern, await workspace.LoadAsync([pattern])));
			var current = view.Component(component.Name);
			if (!current.Load.Contains(pattern, StringComparer.Ordinal))
			{
				view.Replace(current with { Load = [.. current.Load, pattern] });
			}
		}

		view.Updated = RunMetadata.Timestamp();
		view.Write();
		return loads;
	}

	/// <summary>A path as the view's folder sees it, with <c>/</c>.</summary>
	private static string ViewRelative(ViewManifest view, string path, string workingDirectory)
	{
		var normalized = path.Replace('\\', '/');
		var bang = normalized.StartsWith('!') ? "!" : "";
		normalized = normalized.TrimStart('!');
		var cwd = Path.GetFullPath(workingDirectory);
		var inside = cwd == view.Folder || cwd.StartsWith(view.Folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);
		string full;
		if (Path.IsPathRooted(normalized))
		{
			full = Path.GetFullPath(normalized);
		}
		else
		{
			full = Path.GetFullPath(normalized, inside ? cwd : view.Folder);
		}

		if (full != view.Folder && !full.StartsWith(view.Folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
		{
			throw new ViewException($"'{path}' is outside view '{view.Name}' ('{view.Folder}').");
		}

		return bang + Path.GetRelativePath(view.Folder, full).Replace('\\', '/');
	}

	/// <summary>The component a path of <paramref name="component"/> really belongs to: one nested at a reference path in it, transitively.</summary>
	private (ViewComponent Component, string Pattern) Descend(ViewManifest view, ViewComponent component, string pattern)
	{
		while (graph.Registry.Find(component.Name) is { } definition
			&& definition.References.FirstOrDefault(reference => pattern == reference.Path.Replace('\\', '/').Trim('/')
				|| pattern.StartsWith(reference.Path.Replace('\\', '/').Trim('/') + "/", StringComparison.Ordinal)) is { } nested
			&& view.Components.Any(candidate => candidate.Name == nested.Name))
		{
			var rest = pattern[nested.Path.Replace('\\', '/').Trim('/').Length..].TrimStart('/');
			component = view.Component(nested.Name);
			pattern = rest.Length == 0 ? "**" : rest;
		}

		return (component, pattern);
	}

	// ----- following the versions -----

	/// <summary>
	/// Brings the view up to its config spec: fetches the mapped components from the depot (in a clone), resolves the spec
	/// again and moves every component whose version now names another commit there, with the same load patterns. A
	/// component with uncommitted changes stays where it is.
	/// </summary>
	public async Task<IReadOnlyList<ViewMove>> UpdateAsync(ViewManifest view)
	{
		if (view.Spec is null || view.Kind != ViewManifest.ViewKind)
		{
			throw new ViewException($"'{view.Name}' is the folder of an agentic run, which stays at the commits it started from.");
		}

		if (depot is not null)
		{
			foreach (var component in view.Components)
			{
				await graph.Storage.Open(component.Name).FetchUpstreamAsync();
			}
		}

		var resolved = await ResolveAsync(ConfigSpec.Parse(view.Spec), graph, depot);
		if (!resolved.Components.Select(component => component.Name).Order(StringComparer.Ordinal)
			.SequenceEqual(view.Components.Select(component => component.Name).Order(StringComparer.Ordinal)))
		{
			throw new ViewException($"The components view '{view.Name}' maps have changed (now: {string.Join(", ", resolved.Components.Select(component => component.Name))}); " +
				"remove the view and create it again.");
		}

		var folders = view.Components.ToDictionary(component => component.Name, view.FolderOf, StringComparer.Ordinal);
		var moves = new List<ViewMove>();
		foreach (var target in resolved.Components)
		{
			var current = view.Component(target.Name);
			if (current.Commit == target.Commit)
			{
				moves.Add(new ViewMove(current.Name, target.Version, current.Commit, target.Commit, false, "up to date"));
				continue;
			}

			var folder = view.FolderOf(current);
			var changed = await graph.Storage.OpenWorkspace(current.Name, folder).ChangedPathsAsync();
			if (changed.Count > 0)
			{
				moves.Add(new ViewMove(current.Name, target.Version, current.Commit, target.Commit, false,
					$"{changed.Count} uncommitted change(s): {string.Join(", ", changed.Take(5))}{(changed.Count > 5 ? ", ..." : "")}"));
				continue;
			}

			Workspaces.Delete(folder);
			var workspace = await graph.Storage.Open(current.Name).CheckoutAsync(target.Commit, folder, BranchOf(view.Name), current.Load);
			await Workspaces.NestAsync([graph.Registry.Require(current.Name)], folders, new Dictionary<string, IComponentWorkspace> { [current.Name] = workspace });
			view.Replace(current with { Version = target.Version, Commit = target.Commit });
			moves.Add(new ViewMove(current.Name, target.Version, current.Commit, target.Commit, true, null));
		}

		view.Updated = RunMetadata.Timestamp();
		view.Write();
		return moves;
	}
}
