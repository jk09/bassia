namespace Bassia.Server.Meta;

using System.Security.Cryptography;
using System.Text;
using Bassia.Git;
using Bassia.Storage;
using Bassia.Storage.Git;

/// <summary>
/// The meta-repo as one user may see it: the registry without the components they may not read (and without the
/// references to those). A view is a single commit - no history, which would show the whole registry - with the date
/// and author of the revision it shows, so every server instance makes the same commit for the same revision and
/// readable components. A user who may read every component gets the meta-repo itself.
/// </summary>
internal sealed class MetaViews(string viewsRoot)
{
	private readonly SemaphoreSlim gate = new(1, 1);

	/// <summary>The folder of a bare repository with the view of <paramref name="tip"/> of <paramref name="source"/>.</summary>
	public async Task<string> ViewAsync(string source, string tip, IReadOnlyList<ComponentDefinition> components, Func<string, bool> readable)
	{
		if (components.All(component => readable(component.Name)))
		{
			return source;
		}

		var visible = components.Where(component => readable(component.Name)).Select(component => component.Name).Order(StringComparer.Ordinal);
		var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tip + "\n" + string.Join("\n", visible))))[..32].ToLowerInvariant();
		var view = Path.Combine(viewsRoot, key + ".git");
		if (Directory.Exists(view))
		{
			return view;
		}

		await gate.WaitAsync();
		try
		{
			if (!Directory.Exists(view))
			{
				await BuildAsync(source, tip, components, readable, view);
			}

			return view;
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task BuildAsync(string source, string tip, IReadOnlyList<ComponentDefinition> components, Func<string, bool> readable, string view)
	{
		var scratch = Path.Combine(viewsRoot, $"build-{Guid.NewGuid():N}");
		try
		{
			var work = Path.Combine(scratch, Monorepo.MetaRepoFolderName);
			Directory.CreateDirectory(scratch);
			await GitClient.In(scratch).RunOrThrowAsync(["clone", "--quiet", "--no-checkout", source, work]);
			var git = GitClient.In(work);
			await git.RunOrThrowAsync(["checkout", "--quiet", "--detach", tip]);
			var branch = (await GitClient.In(source).RunAsync(["symbolic-ref", "--quiet", "--short", "HEAD"])).Output.Trim() is { Length: > 0 } name ? name : "main";

			// References to what the user may not see go first, then the components themselves.
			var changes = components.Where(component => readable(component.Name) && component.References.Any(reference => !readable(reference.Name)))
				.Select(component => (RegistryChange)new RegistryChange.SetReferences(component.Name,
					component.References.Where(reference => readable(reference.Name)).ToList()))
				.Concat(components.Where(component => !readable(component.Name)).Select(component => new RegistryChange.Remove(component.Name)))
				.ToList();
			await new GitMetaStore(scratch).CommitAsync(changes, "view");

			var tree = await git.RunOrThrowAsync(["rev-parse", "HEAD^{tree}"]);
			var date = await git.RunOrThrowAsync(["log", "-1", "--format=%cI", tip]);
			var identity = new Dictionary<string, string>
			{
				["GIT_AUTHOR_NAME"] = "Bassia",
				["GIT_AUTHOR_EMAIL"] = "bassia@localhost",
				["GIT_AUTHOR_DATE"] = date,
				["GIT_COMMITTER_NAME"] = "Bassia",
				["GIT_COMMITTER_EMAIL"] = "bassia@localhost",
				["GIT_COMMITTER_DATE"] = date
			};
			var commit = await git.RunOrThrowAsync(["commit-tree", tree, "-m", $"The meta-repo at {tip[..7]}, limited to the components you may read"], environment: identity);

			var partial = view + $".{Guid.NewGuid():N}.tmp";
			await GitClient.In(scratch).RunOrThrowAsync(["init", "--quiet", "--bare", $"--initial-branch={branch}", partial]);
			await git.RunOrThrowAsync(["update-ref", "refs/heads/view", commit]);
			await git.RunOrThrowAsync(["push", "--quiet", partial, $"refs/heads/view:refs/heads/{branch}"]);
			Directory.Move(partial, view);
		}
		finally
		{
			GitComponentStorage.DeleteDirectory(scratch);
		}
	}
}
