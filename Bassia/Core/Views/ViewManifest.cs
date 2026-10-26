namespace Bassia.Core;

using Bassia.Storage;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// The monorepo a clone caches (<c>bassia clone</c>): a folder or a URL base under which <c>.bassia</c> and every
/// component are repositories, as in the monorepo itself.
/// </summary>
internal sealed record Depot(string Url)
{
	public string MetaRepoUrl => Join(Monorepo.MetaRepoFolderName);

	public string ComponentUrl(string component) => Join(component);

	private string Join(string name) => IsUrl(Url) ? $"{Url.TrimEnd('/')}/{name}" : Path.Combine(Url, name);

	/// <summary>A URL (<c>https://</c>, <c>file://</c>, <c>ssh://</c>) or git's scp-like <c>user@host:path</c>, rather than a folder.</summary>
	private static bool IsUrl(string text) => text.Contains("://", StringComparison.Ordinal) || (text.Contains('@') && text.Contains(':') && !Directory.Exists(text));

	/// <summary>The store of <paramref name="component"/>; one is imported from the depot, history only, when there is none yet.</summary>
	public async Task<IComponentStore> EnsureStoreAsync(IComponentStorage storage, string component) =>
		storage.Exists(component) ? storage.Open(component) : await storage.ImportHistoryAsync(component, ComponentUrl(component));
}

/// <summary>
/// A component as a view maps it: its version (the selector of the config spec, or its default branch), the commit that
/// resolved to, its folder relative to the view's, and its load patterns - the spec's and those <c>view load</c> added.
/// </summary>
internal sealed record ViewComponent(string Name, string Version, string Commit, string Path, IReadOnlyList<string> Load);

/// <summary>
/// <c>.bassia-view.toml</c> at the root of a view's folder (a view, or a sparse agentic run): which components are mapped
/// there, at which commits and with which load patterns, the config spec it came from, and the monorepo it belongs to.
/// View commands find it by walking up from the working directory, so they work inside the view, also outside the
/// monorepo.
/// </summary>
internal sealed class ViewManifest
{
	public const string FileName = ".bassia-view.toml";
	public const string ViewKind = "view";
	public const string RunKind = "run";

	public required string Name { get; init; }

	/// <summary><see cref="ViewKind"/> or <see cref="RunKind"/> (named by its run id).</summary>
	public required string Kind { get; init; }

	/// <summary>The monorepo root.</summary>
	public required string Root { get; init; }

	/// <summary>The view's folder (where the manifest is); not stored.</summary>
	public required string Folder { get; init; }

	/// <summary>The config spec, or null for a run sparse by <c>-load</c>.</summary>
	public string? Spec { get; init; }

	public required string Created { get; init; }

	public string? Updated { get; set; }

	public List<ViewComponent> Components { get; init; } = [];

	public string FolderOf(ViewComponent component) => System.IO.Path.Combine(Folder, component.Path);

	public ViewComponent Component(string name) =>
		Components.FirstOrDefault(component => component.Name == name) ?? throw new ViewException($"Component '{name}' is not mapped in view '{Name}'.");

	public void Replace(ViewComponent component) => Components[Components.FindIndex(existing => existing.Name == component.Name)] = component;

	public void Write()
	{
		var table = new TomlTable
		{
			["name"] = Name,
			["kind"] = Kind,
			["root"] = Root,
			["created"] = Created
		};
		if (Updated is not null) table["updated"] = Updated;
		if (Spec is not null) table["spec"] = Spec;
		var components = new TomlTableArray();
		foreach (var component in Components)
		{
			var load = new TomlArray();
			foreach (var pattern in component.Load)
			{
				load.Add(pattern);
			}

			components.Add(new TomlTable
			{
				["name"] = component.Name,
				["version"] = component.Version,
				["commit"] = component.Commit,
				["path"] = component.Path,
				["load"] = load
			});
		}

		table["component"] = components;
		File.WriteAllText(System.IO.Path.Combine(Folder, FileName),
			"# A Bassia view: components of the monorepo mapped onto this folder. Managed by 'bassia view'; do not edit.\n" + TomlSerializer.Serialize(table));
	}

	public static ViewManifest Read(string folder)
	{
		var path = System.IO.Path.Combine(folder, FileName);
		TomlTable table;
		try
		{
			table = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? new TomlTable();
		}
		catch (Exception ex) when (ex is TomlException or IOException)
		{
			throw new ViewException($"'{path}' is not a readable view manifest: {ex.Message}");
		}

		string Text(TomlTable from, string key) => from.TryGetValue(key, out var value) && value is string text
			? text
			: throw new ViewException($"'{path}' has no '{key}'.");

		var manifest = new ViewManifest
		{
			Name = Text(table, "name"),
			Kind = Text(table, "kind"),
			Root = Text(table, "root"),
			Folder = System.IO.Path.GetFullPath(folder),
			Created = Text(table, "created"),
			Updated = table.TryGetValue("updated", out var updated) ? updated as string : null,
			Spec = table.TryGetValue("spec", out var spec) ? spec as string : null
		};
		if (table.TryGetValue("component", out var components) && components is TomlTableArray entries)
		{
			foreach (var entry in entries)
			{
				var load = entry.TryGetValue("load", out var patterns) && patterns is TomlArray array ? array.OfType<string>().ToList() : [];
				manifest.Components.Add(new ViewComponent(Text(entry, "name"), Text(entry, "version"), Text(entry, "commit"), Text(entry, "path"), load));
			}
		}

		return manifest;
	}

	/// <summary>The view whose folder holds <paramref name="directory"/>, found by walking up; null when there is none.</summary>
	public static ViewManifest? FindEnclosing(string directory)
	{
		for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
		{
			if (File.Exists(System.IO.Path.Combine(current.FullName, FileName)))
			{
				return Read(current.FullName);
			}
		}

		return null;
	}
}
