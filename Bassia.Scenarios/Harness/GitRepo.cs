namespace Bassia.Scenarios.Harness;

using System.Diagnostics;

/// <summary>A git repository on disk, made and read with the git command line: the source of imports and the target of exports.</summary>
public sealed class GitRepo
{
	public GitRepo(string path)
	{
		Path = path;
	}

	public string Path { get; }

	/// <summary>A new repository with branch <c>main</c>.</summary>
	public static async Task<GitRepo> InitAsync(string path)
	{
		Directory.CreateDirectory(path);
		var repo = new GitRepo(path);
		await repo.GitAsync("init", "-q", "-b", "main");
		await repo.GitAsync("config", "core.autocrlf", "false");
		return repo;
	}

	/// <summary>A clone of <paramref name="url"/> (full history of the default branch).</summary>
	public static async Task<GitRepo> CloneAsync(string url, string path)
	{
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		await RunAsync(System.IO.Path.GetDirectoryName(path)!, "clone", "-q", "--single-branch", url, path);
		return new GitRepo(path);
	}

	/// <summary>Writes files (null deletes one) and commits them with <paramref name="message"/>; returns the commit id.</summary>
	public async Task<string> CommitAsync(string message, IReadOnlyDictionary<string, string?> files, string? author = null)
	{
		foreach (var (relative, content) in files)
		{
			var file = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
			if (content is null)
			{
				File.Delete(file);
			}
			else
			{
				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
				await File.WriteAllTextAsync(file, content);
			}
		}

		await GitAsync("add", "-A");
		await GitAsync(author is null ? ["commit", "-q", "--allow-empty", "-m", message] : ["commit", "-q", "--allow-empty", "-m", message, "--author", author]);
		return await GitAsync("rev-parse", "HEAD");
	}

	/// <summary>Every file of a commit with its content, as git stores it (<c>git ls-tree</c> + <c>git show</c>).</summary>
	public async Task<SortedDictionary<string, string>> FilesAsync(string revision = "HEAD")
	{
		var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in (await GitAsync("ls-tree", "-r", "--full-tree", revision)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			// <mode> <type> <id>\t<path>
			var tab = line.IndexOf('\t');
			var meta = line[..tab].Split(' ');
			if (meta[1] != "blob")
			{
				continue;
			}

			files[line[(tab + 1)..]] = await GitAsync(trim: false, "cat-file", "blob", meta[2]);
		}

		return files;
	}

	/// <summary>The paths of a commit (<c>git ls-tree -r --name-only</c>).</summary>
	public async Task<IReadOnlyList<string>> PathsAsync(string revision = "HEAD") =>
		(await GitAsync("ls-tree", "-r", "--name-only", "--full-tree", revision)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToList();

	/// <summary>How many first-parent commits lead to the revision.</summary>
	public async Task<int> FirstParentCountAsync(string revision = "HEAD") =>
		int.Parse(await GitAsync("rev-list", "--first-parent", "--count", revision));

	public Task<string> GitAsync(params string[] args) => RunAsync(Path, args);

	public Task<string> GitAsync(bool trim, params string[] args) => RunAsync(Path, args, trim);

	public static async Task<string> RunAsync(string directory, string[] args, bool trim = true)
	{
		var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in args)
		{
			start.ArgumentList.Add(arg);
		}

		using var process = Process.Start(start)!;
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed in '{directory}': {await error}");
		return trim ? (await output).Trim() : await output;
	}

	public static Task<string> RunAsync(string directory, params string[] args) => RunAsync(directory, args, true);
}

/// <summary>Files compared the way git identifies them: by blob id (SHA-1 of "blob &lt;length&gt;\0" + bytes), so binary files compare exactly.</summary>
public static class GitBlobs
{
	public static string BlobId(byte[] bytes)
	{
		var header = System.Text.Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
		return Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData([.. header, .. bytes]));
	}

	/// <summary>Every file of a commit by path, with its blob id (<c>git ls-tree -r</c>).</summary>
	public static async Task<SortedDictionary<string, string>> OfCommitAsync(GitRepo repo, string revision = "HEAD")
	{
		var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in (await repo.GitAsync("ls-tree", "-r", "--full-tree", revision)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var tab = line.IndexOf('\t');
			var meta = line[..tab].Split(' ');
			if (meta[1] == "blob")
			{
				files[line[(tab + 1)..]] = meta[2];
			}
		}

		return files;
	}

	/// <summary>Every file under a folder by relative path (with '/'), with its blob id; Bassia's own marker files are left out.</summary>
	public static SortedDictionary<string, string> OfFolder(string folder, string? under = null)
	{
		var root = under is null ? folder : Path.Combine(folder, under.Replace('/', Path.DirectorySeparatorChar));
		var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
		{
			var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
			if (relative is ".bassia-workspace" || relative.StartsWith(".git/", StringComparison.Ordinal))
			{
				continue;
			}

			files[relative] = BlobId(File.ReadAllBytes(file));
		}

		return files;
	}
}
