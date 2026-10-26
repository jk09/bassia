namespace Bassia;

using Bassia.Cli;
using Bassia.Core;

/// <summary>
/// <c>bassia view ...</c>: config specs mapped onto folders of the worker's filesystem, whose files are downloaded when
/// they are loaded. A view command finds its view by <c>-name</c>, or as the view (or sparse run folder) the working
/// directory is in - also outside the monorepo, whose root the view's manifest names.
/// </summary>
internal static class ViewCommands
{
	// ----- bassia view create -----

	public static async Task<int> CreateAsync(Invocation invocation)
	{
		var monorepo = LoadMonorepo();
		var name = invocation.Require("name");
		var spec = await ReadSpecAsync(invocation.Require("spec"));
		var view = await monorepo.Views().CreateAsync(name, spec, invocation.Get("path") is { } path ? Path.GetFullPath(path) : null);
		var files = await monorepo.Views().FilesAsync(view);
		var loaded = files.Count(file => file.Loaded);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Created view '{name}' at '{view.Folder}': {view.Components.Count} component(s) mapped, {loaded} of {files.Count} file(s) loaded. " +
			"Load more with 'bassia view load -path <component>/<path>'.",
			Describe(view, files));
	}

	/// <summary>A config spec from a file, or from stdin for <c>-</c>.</summary>
	internal static async Task<string> ReadSpecAsync(string source)
	{
		if (source == "-")
		{
			return await Console.In.ReadToEndAsync();
		}

		return File.Exists(source) ? await File.ReadAllTextAsync(source) : throw new ViewException($"Config spec file '{source}' does not exist.");
	}

	// ----- bassia view list / show / ls -----

	public static Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = LoadMonorepo();
		var views = monorepo.Views().List();
		var entries = views.Select(entry => new Dictionary<string, object?>
		{
			["name"] = entry.Name,
			["path"] = entry.Folder,
			["exists"] = entry.Manifest is not null,
			["components"] = entry.Manifest?.Components.Select(component => component.Name).ToList(),
			["updated"] = entry.Manifest?.Updated ?? entry.Manifest?.Created
		}).ToList();
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command, $"{entries.Count} view(s).", new Dictionary<string, object?>
		{
			["table"] = new TomlText(AsciiTable.Render(["NAME", "COMPONENTS", "PATH"],
				views.Select(entry => (IReadOnlyList<string>)
				[
					entry.Name,
					entry.Manifest is null ? "(folder gone)" : string.Join(",", entry.Manifest.Components.Select(component => component.Name)),
					entry.Folder
				]))),
			["view"] = entries
		}));
	}

	public static async Task<int> ShowAsync(Invocation invocation)
	{
		var (monorepo, view) = FindView(invocation);
		var files = await monorepo.Views().FilesAsync(view);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"View '{view.Name}' at '{view.Folder}': {view.Components.Count} component(s), {files.Count(file => file.Loaded)} of {files.Count} file(s) loaded.",
			Describe(view, files));
	}

	public static async Task<int> LsAsync(Invocation invocation)
	{
		var (monorepo, view) = FindView(invocation);
		var files = await monorepo.Views().FilesAsync(view, invocation.Get("path"));
		var shown = invocation.Has("loaded") ? files.Where(file => file.Loaded).ToList() : invocation.Has("unloaded") ? files.Where(file => !file.Loaded).ToList() : files.ToList();
		return ProgramCli.WriteResult(true, invocation.Command,
			$"{shown.Count} file(s) in view '{view.Name}', {shown.Count(file => file.Loaded)} loaded.",
			new Dictionary<string, object?>
			{
				["view"] = view.Name,
				["path"] = view.Folder,
				["table"] = new TomlText(AsciiTable.Render(["PATH", "LOADED", "SIZE"],
					shown.Select(file => (IReadOnlyList<string>)[file.ViewPath, file.Loaded ? "yes" : "no", file.Size < 0 ? "?" : file.Size.ToString()]))),
				["file"] = shown.Select(file => new Dictionary<string, object?>
				{
					["path"] = file.ViewPath,
					["component"] = file.Component,
					["component_path"] = file.Path,
					["loaded"] = file.Loaded,
					["size"] = file.Size < 0 ? null : file.Size
				}).ToList()
			});
	}

	// ----- bassia view load / update / remove -----

	public static async Task<int> LoadAsync(Invocation invocation)
	{
		var (monorepo, view) = FindView(invocation);
		var loads = await monorepo.Views().LoadAsync(view, invocation.List("path"), Environment.CurrentDirectory);
		var count = loads.Sum(load => load.Files.Count);
		return ProgramCli.WriteResult(true, invocation.Command,
			count == 0 ? $"Nothing new to load in view '{view.Name}': those files are loaded already." : $"Loaded {count} file(s) into view '{view.Name}'.",
			new Dictionary<string, object?>
			{
				["view"] = view.Name,
				["path"] = view.Folder,
				["load"] = loads.Select(load => new Dictionary<string, object?>
				{
					["component"] = load.Component,
					["pattern"] = load.Pattern,
					["files"] = load.Files.Select(file => $"{view.Component(load.Component).Path}/{file}").ToList()
				}).ToList()
			});
	}

	public static async Task<int> UpdateAsync(Invocation invocation)
	{
		var (monorepo, view) = FindView(invocation);
		var moves = await monorepo.Views().UpdateAsync(view);
		var moved = moves.Count(move => move.Moved);
		var kept = moves.Count(move => !move.Moved && move.Reason != "up to date");
		return ProgramCli.WriteResult(true, invocation.Command,
			$"View '{view.Name}': {moved} component(s) moved, {moves.Count - moved - kept} up to date" + (kept > 0 ? $", {kept} kept back by local changes." : "."),
			new Dictionary<string, object?>
			{
				["view"] = view.Name,
				["component"] = moves.Select(move => new Dictionary<string, object?>
				{
					["name"] = move.Component,
					["version"] = move.Version,
					["from"] = move.From,
					["to"] = move.To,
					["moved"] = move.Moved,
					["reason"] = move.Reason
				}).ToList()
			});
	}

	public static async Task<int> RemoveAsync(Invocation invocation)
	{
		var monorepo = LoadMonorepo();
		var name = invocation.Require("name");
		var lost = await monorepo.Views().RemoveAsync(name, invocation.Has("force"));
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Removed view '{name}'" + (lost.Count > 0 ? $", discarding {lost.Count} changed file(s)." : "."),
			new Dictionary<string, object?> { ["view"] = name, ["discarded"] = lost });
	}

	// ----- finding the monorepo and the view -----

	/// <summary>The monorepo of the working directory, or of the view the working directory is in.</summary>
	private static Monorepo LoadMonorepo()
	{
		if (Monorepo.FindRoot(Environment.CurrentDirectory) is { } root)
		{
			return Monorepo.Load(root);
		}

		return ViewManifest.FindEnclosing(Environment.CurrentDirectory) is { } view
			? Monorepo.Load(view.Root)
			: throw new ViewException($"'{Environment.CurrentDirectory}' is neither inside a Bassia monorepo nor inside a view.");
	}

	/// <summary>The view <c>-name</c> names, else the one the working directory is in.</summary>
	private static (Monorepo Monorepo, ViewManifest View) FindView(Invocation invocation)
	{
		if (invocation.Get("name") is { } name)
		{
			var monorepo = LoadMonorepo();
			return (monorepo, monorepo.Views().Open(name));
		}

		var view = ViewManifest.FindEnclosing(Environment.CurrentDirectory)
			?? throw new ViewException($"'{Environment.CurrentDirectory}' is not inside a view; name one with -name ('bassia view list' lists them).");
		return (Monorepo.Load(view.Root), view);
	}

	private static Dictionary<string, object?> Describe(ViewManifest view, IReadOnlyList<ViewFile> files) => new()
	{
		["view"] = view.Name,
		["kind"] = view.Kind,
		["path"] = view.Folder,
		["created"] = view.Created,
		["updated"] = view.Updated,
		["spec"] = view.Spec is null ? null : new TomlText(view.Spec.TrimEnd()),
		["table"] = new TomlText(AsciiTable.Render(["COMPONENT", "VERSION", "COMMIT", "LOADED", "LOAD"],
			view.Components.Select(component => (IReadOnlyList<string>)
			[
				component.Name, component.Version, component.Commit[..7],
				$"{files.Count(file => file.Component == component.Name && file.Loaded)}/{files.Count(file => file.Component == component.Name)}",
				string.Join(",", component.Load)
			]))),
		["component"] = view.Components.Select(component => new Dictionary<string, object?>
		{
			["name"] = component.Name,
			["version"] = component.Version,
			["commit"] = component.Commit,
			["path"] = view.FolderOf(component),
			["load"] = component.Load,
			["files"] = files.Count(file => file.Component == component.Name),
			["loaded"] = files.Count(file => file.Component == component.Name && file.Loaded)
		}).ToList()
	};
}
