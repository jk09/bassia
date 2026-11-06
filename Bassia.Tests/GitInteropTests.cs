namespace Bassia.Tests;

using Tomlyn.Model;
using static Bassia.Tests.TestEnvironment;

/// <summary>Git as an import and export format: several repositories in, mainline out.</summary>
public sealed class GitInteropTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose() => temp.Dispose();

	private async Task<string> RepositoryAsync(string name, params (string File, string Text, string Message, long Time)[] commits)
	{
		var dir = temp.Combine("sources", name);
		Directory.CreateDirectory(dir);
		await GitAsync(dir, "init", "-q", "-b", "main");
		foreach (var (file, text, message, time) in commits)
		{
			var path = Path.Combine(dir, file);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(path, text);
			await GitAsync(dir, "add", "-A");
			Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", $"@{1_700_000_000 + time} +0000");
			Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", $"@{1_700_000_000 + time} +0000");
			await GitAsync(dir, "commit", "-q", "-m", message);
		}

		Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", null);
		Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", null);
		return dir;
	}

	[Fact]
	public async Task SeveralRepositories_ImportWithInterleavedHistory_AndExportBack()
	{
		var api = await RepositoryAsync("api", ("src/api.cs", "v1\n", "api one", 1000), ("src/api.cs", "v2\n", "api two", 3000));
		var web = await RepositoryAsync("web", ("index.html", "<p>1</p>\n", "web one", 2000), ("app.js", "js\n", "web two", 4000));
		var root = temp.Combine("repo");
		await OkAsync(temp.Path, "init", "-path", root);

		var import = await OkAsync(root, "import", "-git", $"{api},{web}", "-history");
		Assert.Equal(4L, import["seq"]);
		var log = (TomlTableArray)(await OkAsync(root, "mainline", "log"))["landing"];
		Assert.Equal(["web two", "api two", "web one", "api one"], log.Take(4).Select(landing => ((string)landing["intent"]).Split('\n')[0]));
		Assert.Equal("v2\n", (await OkAsync(root, "mainline", "cat", "api/src/api.cs"))["content"]);
		Assert.Equal("js\n", (await OkAsync(root, "mainline", "cat", "web/app.js"))["content"]);
		await FailAsync(root, "import", "-git", api);

		var mirror = temp.Combine("mirror");
		var export = await OkAsync(root, "export", "-git", mirror);
		Assert.Equal(4L, export["commits"]);
		Assert.Equal("v2", await GitAsync(mirror, "show", "main:api/src/api.cs"));
		Assert.Contains("Change-Id:", await GitAsync(mirror, "log", "-1", "--format=%B", "main"));

		await OkAsync(root, "workspace", "create", "w");
		await File.WriteAllTextAsync(Path.Combine(root, "workspaces", "w", "web", "app.js"), "js2\n");
		await OkAsync(root, "workspace", "land", "w", "-keep");
		var again = await OkAsync(root, "export", "-git", mirror);
		Assert.Equal(1L, again["commits"]);
		Assert.Equal("js2", await GitAsync(mirror, "show", "main:web/app.js"));
		Assert.Equal("5", await GitAsync(mirror, "rev-list", "--count", "main"));
	}

	[Fact]
	public async Task WithoutHistory_EachRepositoryIsOneBaseline_UnderItsPrefix()
	{
		var lib = await RepositoryAsync("lib", ("a.txt", "1\n", "one", 1000), ("a.txt", "2\n", "two", 2000));
		var root = temp.Combine("repo");
		await OkAsync(temp.Path, "init", "-path", root);
		var import = await OkAsync(root, "import", "-git", lib, "-prefix", "libs/core");
		Assert.Equal(1L, import["seq"]);
		Assert.Equal("2\n", (await OkAsync(root, "mainline", "cat", "libs/core/a.txt"))["content"]);
	}
}
