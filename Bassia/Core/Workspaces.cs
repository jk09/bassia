namespace Bassia.Core;

using Bassia.Storage;

/// <summary>
/// The folder layout runs, task runs and the integration's verification share: every component of a closure checked
/// out side by side, and each reference nested inside its referrer as a link (junction) to the referenced sibling, so
/// every reference to a component resolves to the same files and the same commit.
/// </summary>
internal static class Workspaces
{
	/// <summary>
	/// Nests the references of <paramref name="closure"/>: for each, a link at the reference path inside the referrer's
	/// folder (<paramref name="folders"/>) to the referenced component's folder, hidden from the referrer's working copy so
	/// it never commits the nested files. <paramref name="linked"/> hears of each link (referrer, reference).
	/// </summary>
	public static async Task NestAsync(IReadOnlyList<ComponentDefinition> closure, IReadOnlyDictionary<string, string> folders,
		IReadOnlyDictionary<string, IComponentWorkspace> workspaces, Action<string, ComponentReference>? linked = null)
	{
		foreach (var definition in closure)
		{
			foreach (var reference in definition.References)
			{
				var linkPath = Path.Combine(folders[definition.Name], reference.Path);
				if (Directory.Exists(linkPath) || File.Exists(linkPath))
				{
					throw new MonorepoException($"Component '{definition.Name}' already contains '{reference.Path}'; cannot nest component '{reference.Name}' there.");
				}

				Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
				await DirectoryLinks.CreateAsync(linkPath, folders[reference.Name]);
				linked?.Invoke(definition.Name, reference);
				await workspaces[definition.Name].HideAsync(reference.Path);
			}
		}
	}

	/// <summary>
	/// Checks out each of <paramref name="commits"/> (component -> commit) at <c>&lt;folder&gt;/&lt;component&gt;</c> on
	/// <paramref name="branch"/> and nests the references of <paramref name="closure"/>; returns the working copies.
	/// </summary>
	public static async Task<IReadOnlyDictionary<string, IComponentWorkspace>> MaterializeAsync(IComponentStorage storage, IReadOnlyList<ComponentDefinition> closure,
		IReadOnlyDictionary<string, string> commits, string folder, string branch)
	{
		var workspaces = new Dictionary<string, IComponentWorkspace>(StringComparer.Ordinal);
		var folders = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var component in closure)
		{
			folders[component.Name] = Path.Combine(folder, component.Name);
			workspaces[component.Name] = await storage.Open(component.Name).CheckoutAsync(commits[component.Name], folders[component.Name], branch);
		}

		await NestAsync(closure, folders, workspaces);
		return workspaces;
	}

	/// <summary>Removes a materialized folder: the links first, so the delete never follows one into another working copy.</summary>
	public static void Delete(string folder)
	{
		if (!Directory.Exists(folder))
		{
			return;
		}

		void Unlink(string directory)
		{
			foreach (var entry in Directory.EnumerateDirectories(directory))
			{
				if (DirectoryLinks.IsLink(entry))
				{
					DirectoryLinks.Delete(entry);
				}
				else
				{
					Unlink(entry);
				}
			}
		}

		Unlink(folder);
		foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(folder, recursive: true);
	}
}
