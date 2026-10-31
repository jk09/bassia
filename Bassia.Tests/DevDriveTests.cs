namespace Bassia.Tests;

using System.Runtime.Versioning;
using Bassia.Platform;
using Tomlyn.Model;
using static Bassia.Tests.TestEnvironment;

/// <summary>A cloner standing in for block cloning where the platform has none: copies, and counts.</summary>
internal sealed class CopyCloner(bool fail = false) : IFileCloner
{
	public int Clones { get; private set; }

	public bool CanClone(string directory) => true;

	public bool SameVolume(string a, string b) => true;

	public void Clone(string source, string destination)
	{
		if (fail)
		{
			throw new IOException("too many references to a cluster");
		}

		File.Copy(source, destination);
		File.SetAttributes(destination, FileAttributes.Normal);
		Clones++;
	}

	public VolumeInfo Describe(string path) => new(path, "R:\\", "ReFS", true, true, true, 4096, null);
}

/// <summary>Workspaces block-cloned from the clone cache, with a stand-in cloner (every platform).</summary>
public sealed class CloneCacheTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose()
	{
		Monorepo.Cloner = null;
		temp.Dispose();
	}

	private string Root => temp.Combine("repo");

	private string Workspace(string name) => Path.Combine(Root, "workspaces", name);

	private static readonly string Big = string.Concat(Enumerable.Range(0, 2000).Select(index => $"line {index} of a big source file\n"));

	private async Task SeedAsync()
	{
		await OkAsync(temp.Path, "init", "-path", Root);
		await OkAsync(Root, "workspace", "create", "seed");
		Directory.CreateDirectory(Path.Combine(Workspace("seed"), "src"));
		for (var index = 0; index < 5; index++)
		{
			await File.WriteAllTextAsync(Path.Combine(Workspace("seed"), "src", $"big{index}.cs"), Big + index);
		}

		await File.WriteAllTextAsync(Path.Combine(Workspace("seed"), "small.txt"), "small\n");
		await OkAsync(Root, "workspace", "land", "seed");
	}

	[Fact]
	public async Task LargeFiles_AreClonedFromTheCache_WhichIsFilledOnce()
	{
		var cloner = new CopyCloner();
		Monorepo.Cloner = cloner;
		await SeedAsync();

		var first = await OkAsync(Root, "workspace", "create", "one");
		Assert.Equal(5L, first["files_cloned"]);
		Assert.Equal(1L, first["files_written"]);
		var second = await OkAsync(Root, "workspace", "create", "two");
		Assert.Equal(5L, second["files_cloned"]);
		Assert.Equal(10, cloner.Clones);
		Assert.Equal(5, Directory.EnumerateFiles(Path.Combine(Root, ".bassia", "clones"), "*", SearchOption.AllDirectories).Count());
		Assert.Equal(Big + 3, await File.ReadAllTextAsync(Path.Combine(Workspace("two"), "src", "big3.cs")));

		// A cloned file is an ordinary file of the workspace: edits snapshot and land, the cache entry stays as it was.
		await File.WriteAllTextAsync(Path.Combine(Workspace("one"), "src", "big3.cs"), "rewritten\n");
		var status = await OkAsync(Root, "workspace", "status", "one");
		Assert.Contains("1 modified", (string)status["message"]);
		Assert.Equal(Big + 3, await File.ReadAllTextAsync(Path.Combine(Workspace("two"), "src", "big3.cs")));
		Assert.All(Directory.EnumerateFiles(Path.Combine(Root, ".bassia", "clones"), "*", SearchOption.AllDirectories),
			file => Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)));
		await OkAsync(Root, "workspace", "land", "one");
	}

	[Fact]
	public async Task AFailedClone_FallsBackToACopy()
	{
		Monorepo.Cloner = new CopyCloner(fail: true);
		await SeedAsync();
		var created = await OkAsync(Root, "workspace", "create", "one");
		Assert.Equal(5L, created["files_cloned"]);
		var file = Path.Combine(Workspace("one"), "src", "big0.cs");
		Assert.Equal(Big + 0, await File.ReadAllTextAsync(file));
		Assert.False(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
	}

	[Fact]
	public async Task TheSettingTurnsCloningOff()
	{
		var cloner = new CopyCloner();
		Monorepo.Cloner = cloner;
		await SeedAsync();
		await OkAsync(Root, "config", "set", "-key", "workspace.clone", "-value", "off");
		var created = await OkAsync(Root, "workspace", "create", "one");
		Assert.Equal(0L, created["files_cloned"]);
		Assert.Equal(0, cloner.Clones);
		var status = await OkAsync(Root, "devdrive", "status");
		Assert.Equal(false, status["clone"]);
		Assert.Contains("off", (string)status["reason"]);
	}

	[Fact]
	public async Task Gc_PrunesCacheEntriesNoWorkspaceAndNoMainlineFileUses()
	{
		Monorepo.Cloner = new CopyCloner();
		await SeedAsync();
		await OkAsync(Root, "workspace", "create", "one");
		var cache = Path.Combine(Root, ".bassia", "clones");
		Directory.CreateDirectory(Path.Combine(cache, "ab"));
		var stale = Path.Combine(cache, "ab", new string('c', 62));
		await File.WriteAllTextAsync(stale, "content nobody holds");
		File.SetAttributes(stale, FileAttributes.ReadOnly);

		var gc = await OkAsync(Root, "gc");
		Assert.Equal(1L, gc["clone_cache_pruned"]);
		Assert.False(File.Exists(stale));
		Assert.Equal(5, Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Count());
	}

	[Fact]
	public async Task Status_ExplainsTheDecision()
	{
		Monorepo.Cloner = new CopyCloner();
		await SeedAsync();
		var status = await OkAsync(Root, "devdrive", "status");
		Assert.Equal(true, status["clone"]);
		Assert.Equal("ReFS", status["file_system"]);
		Assert.Equal(true, status["dev_drive"]);

		Monorepo.Cloner = null;
		var native = await OkAsync(Root, "devdrive", "status");
		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(false, native["clone"]);
			Assert.Contains("Windows only", (string)native["reason"]);
		}
	}
}

/// <summary>
/// Real block cloning, on a Dev Drive (or another block-cloning ReFS volume) named by BASSIA_TEST_DEVDRIVE_DIR. The
/// Windows CI job creates one; elsewhere these tests have nothing to run on and pass without checking anything.
/// </summary>
[Trait("Category", "DevDrive")]
public sealed class DevDriveTests : IDisposable
{
	private readonly string? root;

	public DevDriveTests()
	{
		if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("BASSIA_TEST_DEVDRIVE_DIR") is { Length: > 0 } dir)
		{
			root = Path.Combine(dir, "t" + Guid.NewGuid().ToString("N")[..8]);
			Directory.CreateDirectory(root);
		}
	}

	public void Dispose()
	{
		if (root is null || !Directory.Exists(root))
		{
			return;
		}

		// SQLite's connection pool keeps state.db open, and Windows deletes no open file.
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		try
		{
			foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
			{
				File.SetAttributes(file, FileAttributes.Normal);
			}

			Directory.Delete(root, true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best effort, as for every temporary folder: the CI volume is thrown away afterwards.
		}
	}

	[Fact]
	public void BlockClones_AreIsolatedCopies()
	{
		if (root is null || !OperatingSystem.IsWindows())
		{
			return;
		}

		Check(root);
	}

	[SupportedOSPlatform("windows")]
	private static void Check(string root)
	{
		var cloner = new WindowsBlockCloner();
		var volume = cloner.Describe(root);
		Assert.True(volume.BlockCloning, $"The test volume does not support block cloning: {volume}");
		Assert.True(cloner.CanClone(root));

		var source = Path.Combine(root, "source.bin");
		var data = new byte[3 * 1024 * 1024 + 123];
		new Random(4).NextBytes(data);
		File.WriteAllBytes(source, data);
		var clone = Path.Combine(root, "clone.bin");
		cloner.Clone(source, clone);
		Assert.Equal(data, File.ReadAllBytes(clone));
		Assert.True(cloner.SameVolume(source, clone));

		using (var stream = new FileStream(clone, FileMode.Open, FileAccess.Write))
		{
			stream.Position = 4096;
			stream.Write(new byte[100]);
		}

		Assert.Equal(data, File.ReadAllBytes(source));
		Assert.NotEqual(data, File.ReadAllBytes(clone));
		Assert.Throws<IOException>(() => cloner.Clone(source, clone));
	}

	[Fact]
	public async Task WorkspacesOnTheDevDrive_AreBlockCloned()
	{
		if (root is null)
		{
			return;
		}

		var repo = Path.Combine(root, "repo");
		await OkAsync(root, "init", "-path", repo);
		await OkAsync(repo, "workspace", "create", "seed");
		var big = new byte[512 * 1024];
		new Random(2).NextBytes(big);
		await File.WriteAllBytesAsync(Path.Combine(repo, "workspaces", "seed", "big.bin"), big);
		await OkAsync(repo, "workspace", "land", "seed");

		var status = await OkAsync(repo, "devdrive", "status");
		Assert.True(status["clone"] is true, $"{status["message"]}");
		var created = await OkAsync(repo, "workspace", "create", "one");
		Assert.Equal(1L, created["files_cloned"]);
		Assert.Equal(big, await File.ReadAllBytesAsync(Path.Combine(repo, "workspaces", "one", "big.bin")));
		await File.WriteAllBytesAsync(Path.Combine(repo, "workspaces", "one", "big.bin"), new byte[10]);
		Assert.Equal(big, await File.ReadAllBytesAsync(Directory.EnumerateFiles(Path.Combine(repo, ".bassia", "clones"), "*", SearchOption.AllDirectories).Single()));
		var second = await OkAsync(repo, "workspace", "create", "two");
		Assert.Equal(1L, second["files_cloned"]);
		Assert.Equal(big, await File.ReadAllBytesAsync(Path.Combine(repo, "workspaces", "two", "big.bin")));
	}
}
