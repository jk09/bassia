namespace Bassia.Interop;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

/// <summary>A git command that failed, or git that is not installed.</summary>
internal sealed class GitException(string message) : Exception(message);

/// <summary>Runs the installed <c>git</c>: Git is an import and export format at the edge, never part of the core.</summary>
internal static class GitProcess
{
	public static async Task<byte[]> RunAsync(string? directory, IReadOnlyList<string> args, byte[]? input = null, CancellationToken ct = default)
	{
		var start = new ProcessStartInfo("git")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = input is not null,
			UseShellExecute = false
		};
		if (directory is not null)
		{
			start.ArgumentList.Add("-C");
			start.ArgumentList.Add(directory);
		}

		foreach (var arg in args)
		{
			start.ArgumentList.Add(arg);
		}

		start.Environment["GIT_TERMINAL_PROMPT"] = "0";
		Process process;
		try
		{
			process = Process.Start(start) ?? throw new GitException("git did not start.");
		}
		catch (Win32Exception)
		{
			throw new GitException("git was not found. Install git and make sure it is on PATH: import and export use it.");
		}

		using (process)
		{
			var output = new MemoryStream();
			var copy = process.StandardOutput.BaseStream.CopyToAsync(output, ct);
			var error = process.StandardError.ReadToEndAsync(ct);
			if (input is not null)
			{
				await process.StandardInput.BaseStream.WriteAsync(input, ct);
				process.StandardInput.Close();
			}

			await copy;
			await process.WaitForExitAsync(ct);
			if (process.ExitCode != 0)
			{
				throw new GitException($"git {string.Join(' ', args.Take(3))} failed: {(await error).Trim()}");
			}

			return output.ToArray();
		}
	}

	public static async Task<string> TextAsync(string? directory, params string[] args) =>
		Encoding.UTF8.GetString(await RunAsync(directory, args)).TrimEnd('\n');
}

/// <summary>Reads git objects through one long-lived <c>git cat-file --batch</c>.</summary>
internal sealed class GitObjectReader : IAsyncDisposable
{
	private readonly Process process;

	public GitObjectReader(string gitDir)
	{
		var start = new ProcessStartInfo("git") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in new[] { "-C", gitDir, "cat-file", "--batch" })
		{
			start.ArgumentList.Add(arg);
		}

		process = Process.Start(start)!;
	}

	public async Task<byte[]> ReadAsync(string sha, CancellationToken ct = default)
	{
		await process.StandardInput.WriteAsync(sha + "\n");
		await process.StandardInput.FlushAsync(ct);
		var stream = process.StandardOutput.BaseStream;
		var header = new StringBuilder();
		while (true)
		{
			var b = stream.ReadByte();
			if (b < 0)
			{
				throw new GitException("git cat-file ended early.");
			}

			if (b == '\n')
			{
				break;
			}

			header.Append((char)b);
		}

		var parts = header.ToString().Split(' ');
		if (parts.Length < 3)
		{
			throw new GitException($"git cannot read object {sha}: {header}");
		}

		var size = int.Parse(parts[2], CultureInfo.InvariantCulture);
		var buffer = new byte[size];
		await stream.ReadExactlyAsync(buffer, ct);
		stream.ReadByte();
		return buffer;
	}

	public async ValueTask DisposeAsync()
	{
		process.StandardInput.Close();
		await process.WaitForExitAsync();
		process.Dispose();
	}
}

/// <summary>One repository to import: where it is, and the top-level folder it goes into.</summary>
internal sealed record GitSource(string Url, string Prefix);

/// <summary>What an import did, per repository.</summary>
internal sealed record ImportReport(string Url, string Prefix, int Commits, int Files, long Bytes, IReadOnlyList<string> Skipped);

/// <summary>
/// Imports git repositories into the monorepo, each under its own top-level folder: as one baseline commit each, or
/// with their first-parent history interleaved by commit time. Every imported commit lands through the normal landing
/// path from an import workspace, so it gets the same checks as any agent's work.
/// </summary>
internal sealed class GitImporter(Monorepo monorepo)
{
	private readonly Dictionary<string, Leaf> blobs = new(StringComparer.Ordinal);

	public Action<string>? Progress { get; init; }

	private sealed record GitCommit(GitSource Source, string GitDir, string Sha, long Time, string Author, string Message);

	private sealed record GitEntry(string Mode, string Sha, string Path);

	public async Task<IReadOnlyList<ImportReport>> ImportAsync(IReadOnlyList<GitSource> sources, bool history, int limit, CancellationToken ct = default)
	{
		var main = await monorepo.Repo.GetMainAsync(ct);
		foreach (var source in sources)
		{
			if (await monorepo.Repo.Trees.GetAsync(main.Tree, source.Prefix, ct) is not null)
			{
				throw new MonorepoException($"'{source.Prefix}' already exists on mainline; import into another folder with -prefix.");
			}
		}

		var scratch = Directory.CreateTempSubdirectory("bassia-import-").FullName;
		try
		{
			var commits = new List<GitCommit>();
			var reports = new Dictionary<GitSource, (int Commits, HashSet<string> Files, long Bytes, List<string> Skipped)>();
			foreach (var source in sources)
			{
				var gitDir = Path.Combine(scratch, Guid.NewGuid().ToString("N")[..8] + ".git");
				Progress?.Invoke($"fetching {source.Url}");
				await GitProcess.RunAsync(null, ["clone", "--bare", "--quiet", "--single-branch", "--no-tags", source.Url, gitDir], null, ct);
				var revList = history
					? await GitProcess.TextAsync(gitDir, "rev-list", "--first-parent", $"--max-count={limit}", "--format=%H %ct %an%x00%B%x01", "HEAD")
					: await GitProcess.TextAsync(gitDir, "log", "-1", "--format=commit %H\n%H %ct %an%x00%B%x01", "HEAD");
				var found = ParseRevList(source, gitDir, revList);
				if (found.Count == 0)
				{
					throw new MonorepoException($"'{source.Url}' has no commits to import.");
				}

				commits.AddRange(found);
				reports[source] = (0, new HashSet<string>(StringComparer.Ordinal), 0, []);
			}

			// One timeline across repositories: oldest first, a repository's own order kept on equal times.
			commits = commits.Select((commit, index) => (commit, index)).OrderBy(item => item.commit.Time).ThenBy(item => item.index).Select(item => item.commit).ToList();
			var name = "import-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
			var workspace = await monorepo.Workspaces.Service.CreateAsync(name, monorepo.User, intent: "import", ct: ct);
			var previous = new Dictionary<GitSource, Dictionary<string, GitEntry>>();
			var readers = new Dictionary<string, GitObjectReader>();
			try
			{
				foreach (var commit in commits)
				{
					ct.ThrowIfCancellationRequested();
					if (!readers.TryGetValue(commit.GitDir, out var reader))
					{
						readers[commit.GitDir] = reader = new GitObjectReader(commit.GitDir);
					}

					var report = reports[commit.Source];
					var entries = await ListTreeAsync(commit.GitDir, commit.Sha, report.Skipped, ct);
					var before = previous.GetValueOrDefault(commit.Source) ?? new Dictionary<string, GitEntry>(StringComparer.Ordinal);
					var changes = new List<PathChange>();
					foreach (var (path, entry) in entries)
					{
						if (before.TryGetValue(path, out var old) && old.Sha == entry.Sha && old.Mode == entry.Mode)
						{
							continue;
						}

						var leaf = await LeafAsync(reader, entry, ct);
						report.Bytes += leaf.Size;
						changes.Add(new PathChange(PathNames.Join(commit.Source.Prefix, path), leaf));
					}

					changes.AddRange(before.Keys.Where(path => !entries.ContainsKey(path)).Select(path => new PathChange(PathNames.Join(commit.Source.Prefix, path), null)));
					previous[commit.Source] = entries;
					report.Files.UnionWith(entries.Keys);
					report.Commits++;
					reports[commit.Source] = report;
					if (changes.Count == 0)
					{
						continue;
					}

					var state = await monorepo.Repo.FindWorkspaceAsync(workspace.Id, ct) ?? throw new MonorepoException("The import workspace disappeared.");
					var working = await monorepo.Repo.Objects.ReadCommitAsync(state.Record.Working, ct);
					var tree = await monorepo.Repo.Trees.EditAsync(working.Tree, changes, ct);
					var intent = $"{commit.Message.Trim()}\n\nImported from {commit.Source.Url} {commit.Sha}";
					await monorepo.Workspaces.Service.AmendWorkingAsync(workspace.Id, current => current with
					{
						Tree = tree,
						Intent = intent,
						Author = new Author(commit.Author),
						Time = DateTimeOffset.FromUnixTimeSeconds(commit.Time)
					}, $"import {commit.Sha[..10]}", monorepo.User, ct);
					var result = await monorepo.Landing.LandAsync(new LandRequest(workspace.Id, Keep: true, Agent: monorepo.User), ct);
					if (result.Outcome != LandOutcome.Landed)
					{
						throw new MonorepoException($"Importing {commit.Sha[..10]} of {commit.Source.Url} did not land: {result.Message}");
					}

					Progress?.Invoke($"landed {commit.Source.Prefix} {commit.Sha[..10]} at seq {result.Seq}");
				}
			}
			finally
			{
				foreach (var reader in readers.Values)
				{
					await reader.DisposeAsync();
				}

				await monorepo.Workspaces.Service.CloseAsync(workspace.Id, false, monorepo.User, ct);
			}

			return sources.Select(source => new ImportReport(source.Url, source.Prefix, reports[source].Commits, reports[source].Files.Count,
				reports[source].Bytes, reports[source].Skipped)).ToList();
		}
		finally
		{
			try
			{
				Directory.Delete(scratch, true);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	private static List<GitCommit> ParseRevList(GitSource source, string gitDir, string text)
	{
		var commits = new List<GitCommit>();
		foreach (var record in text.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
		{
			var lines = record.TrimStart('\n').Split('\n', 2);
			if (lines.Length < 2 || !lines[0].StartsWith("commit ", StringComparison.Ordinal))
			{
				continue;
			}

			var header = lines[1];
			var nul = header.IndexOf('\0');
			var first = header[..nul].Split(' ', 3);
			commits.Add(new GitCommit(source, gitDir, first[0], long.Parse(first[1], CultureInfo.InvariantCulture), first.Length > 2 ? first[2] : "git", header[(nul + 1)..]));
		}

		commits.Reverse();
		return commits;
	}

	private static async Task<Dictionary<string, GitEntry>> ListTreeAsync(string gitDir, string commit, List<string> skipped, CancellationToken ct)
	{
		var bytes = await GitProcess.RunAsync(gitDir, ["ls-tree", "-r", "-z", commit], null, ct);
		var entries = new Dictionary<string, GitEntry>(StringComparer.Ordinal);
		foreach (var item in Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			var tab = item.IndexOf('\t');
			var meta = item[..tab].Split(' ');
			var path = item[(tab + 1)..];
			if (meta[1] != "blob")
			{
				if (!skipped.Contains(path))
				{
					skipped.Add(path);
				}

				continue;
			}

			entries[path] = new GitEntry(meta[0], meta[2], path);
		}

		return entries;
	}

	private async Task<Leaf> LeafAsync(GitObjectReader reader, GitEntry entry, CancellationToken ct)
	{
		var key = entry.Mode + entry.Sha;
		if (blobs.TryGetValue(key, out var cached))
		{
			return cached;
		}

		var content = await reader.ReadAsync(entry.Sha, ct);
		var leaf = entry.Mode == "120000"
			? await monorepo.Repo.Objects.WriteSymlinkAsync(Encoding.UTF8.GetString(content), ct)
			: await monorepo.Repo.Objects.WriteContentAsync(content, entry.Mode == "100755", ct);
		blobs[key] = leaf;
		return leaf;
	}
}

/// <summary>
/// Exports mainline into a git repository with <c>git fast-import</c>, one git commit per landing in seq order. Each
/// carries the intent as its message, the agent as author and a <c>Change-Id:</c> trailer; the last exported seq is
/// kept in the repository's config (<c>bassia.exportedSeq</c>), so the next export continues from there.
/// </summary>
internal sealed class GitExporter(Monorepo monorepo)
{
	public async Task<(long From, long To, int Commits)> ExportAsync(string gitDir, string branch, CancellationToken ct = default)
	{
		Directory.CreateDirectory(gitDir);
		if (!Directory.Exists(Path.Combine(gitDir, ".git")) && !File.Exists(Path.Combine(gitDir, "HEAD")))
		{
			await GitProcess.RunAsync(gitDir, ["init", "--quiet", "--initial-branch", branch], null, ct);
		}

		long exported = 0;
		try
		{
			exported = long.Parse(await GitProcess.TextAsync(gitDir, "config", "--get", "bassia.exportedSeq"), CultureInfo.InvariantCulture);
		}
		catch (GitException)
		{
		}

		var main = await monorepo.Repo.GetMainAsync(ct);
		if (exported >= main.Seq)
		{
			return (exported, exported, 0);
		}

		var states = new Stack<MainState>();
		var state = main;
		while (state.Seq > exported)
		{
			states.Push(state);
			state = await monorepo.Repo.MainAtAsync(state.Operation.Parents[0], ct);
		}

		var stream = new MemoryStream();
		void Write(string text) => stream.Write(Encoding.UTF8.GetBytes(text));
		void Data(byte[] bytes)
		{
			Write($"data {bytes.Length}\n");
			stream.Write(bytes);
			Write("\n");
		}

		var previous = state.Tree;
		var first = true;
		var count = 0;
		while (states.TryPop(out var next))
		{
			var commit = next.Commit;
			var time = commit.Time.ToUnixTimeSeconds();
			var agent = commit.Author.Agent;
			Write($"commit refs/heads/{branch}\n");
			Write($"author {Sanitize(agent)} <{Sanitize(agent).Replace(' ', '.')}@agents.bassia.invalid> {time} +0000\n");
			Write($"committer bassia <bassia@bassia.invalid> {time} +0000\n");
			var message = $"{(commit.Intent.Trim().Length == 0 ? "(no intent)" : commit.Intent.Trim())}\n\nChange-Id: {commit.ChangeId.Hex}\nBassia-Seq: {next.Seq}\n";
			Data(Encoding.UTF8.GetBytes(message));
			if (first && exported > 0)
			{
				Write($"from refs/heads/{branch}^0\n");
			}

			first = false;
			foreach (var change in await monorepo.Repo.Trees.DiffAsync(previous, next.Tree, "", ct))
			{
				if (change.New is null)
				{
					Write($"D {QuotePath(change.Path)}\n");
					continue;
				}

				var mode = change.New.Kind == EntryKind.Symlink ? "120000" : change.New.Executable ? "100755" : "100644";
				Write($"M {mode} inline {QuotePath(change.Path)}\n");
				Data(await monorepo.Repo.Objects.ReadContentAsync(change.New, ct));
			}

			Write("\n");
			previous = next.Tree;
			count++;
		}

		await GitProcess.RunAsync(gitDir, ["fast-import", "--quiet"], stream.ToArray(), ct);
		await GitProcess.RunAsync(gitDir, ["config", "bassia.exportedSeq", main.Seq.ToString(CultureInfo.InvariantCulture)], null, ct);
		return (exported, main.Seq, count);
	}

	private static string Sanitize(string text) => new(text.Where(c => c is not ('<' or '>' or '\n')).ToArray());

	private static string QuotePath(string path) =>
		path.Any(c => c is '"' or '\n' or '\\') || path.StartsWith('"') ? "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"" : path;
}
