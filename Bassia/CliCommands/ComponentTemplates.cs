namespace Bassia;

using System.Text;
using Bassia.Core;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// A template for new components: <c>.bassia/templates/&lt;name&gt;/template.toml</c> (description, tags, visibility,
/// exports, owners, references) and the starter files under <c>files/</c>, in whose contents and paths
/// <c>{{name}}</c> becomes the new component's name.
/// </summary>
internal sealed record ComponentTemplate(string Name, string? Description, ComponentAttributes Attributes, IReadOnlyList<ComponentReference> References,
	IReadOnlyList<string> Files, string Folder)
{
	public const string FolderName = "templates";
	public const string FileName = "template.toml";
	public const string Placeholder = "{{name}}";

	public static string TemplatesDir(string root) => Path.Combine(root, Monorepo.MetaRepoFolderName, FolderName);

	/// <summary>Every template of the monorepo, by name.</summary>
	public static IReadOnlyList<ComponentTemplate> List(string root) =>
		Directory.Exists(TemplatesDir(root))
			? Directory.EnumerateDirectories(TemplatesDir(root)).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).Select(name => Load(root, name)).ToList()
			: [];

	/// <summary>The template <paramref name="name"/>; throws when there is none or it does not parse.</summary>
	public static ComponentTemplate Load(string root, string name)
	{
		var folder = Path.Combine(TemplatesDir(root), name);
		if (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0 || name.StartsWith('.') || !Directory.Exists(folder))
		{
			var known = List(root).Select(template => template.Name).ToList();
			throw new MonorepoException($"There is no template '{name}' in {TemplatesDir(root)}" + (known.Count == 0 ? "." : $"; templates: {string.Join(", ", known)}."));
		}

		var where = $"Template '{name}'";
		var table = new TomlTable();
		var path = Path.Combine(folder, FileName);
		if (File.Exists(path))
		{
			try
			{
				table = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? new TomlTable();
			}
			catch (TomlException ex)
			{
				throw new MonorepoException($"{where}: {FileName} is not valid TOML: {ex.Message}");
			}
		}

		foreach (var key in table.Keys.Where(key => key is not ("description" or "tags" or "visibility" or "exports" or "owners" or "references")))
		{
			throw new MonorepoException($"{where}: unknown key '{key}' in {FileName}; expected description, tags, visibility, exports, owners and references.");
		}

		List<string>? Strings(string key) => !table.TryGetValue(key, out var value) ? null
			: value is TomlArray array && array.All(item => item is string) ? array.Cast<string>().ToList()
			: throw new MonorepoException($"{where}: '{key}' in {FileName} must be an array of strings.");

		var filesDir = Path.Combine(folder, "files");
		var files = Directory.Exists(filesDir)
			? Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(filesDir, file).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList()
			: [];
		return new ComponentTemplate(name, table.TryGetValue("description", out var description) ? description as string : null,
			new ComponentAttributes(Strings("tags") ?? [], Strings("visibility"), Strings("owners") ?? [], Strings("exports")),
			ComponentsFile.ParseReferences(Strings("references") ?? []), files, folder);
	}

	/// <summary>The starter files for a component named <paramref name="component"/>, with <c>{{name}}</c> replaced.</summary>
	public IReadOnlyDictionary<string, byte[]> FilesFor(string component) =>
		Files.ToDictionary(file => file.Replace(Placeholder, component), file =>
		{
			var bytes = File.ReadAllBytes(Path.Combine(Folder, "files", file));
			return bytes.Contains((byte)0) ? bytes : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(Placeholder, component));
		}, StringComparer.Ordinal);
}
