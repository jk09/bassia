using Tomlyn;
using Tomlyn.Model;

namespace Bassia.Tests;

/// <summary>
/// <c>bassia component fork</c>: a component forked at its main branch keeps that history (same commit ids) on a
/// main branch of its own, and parent and fork are independent from then on.
/// </summary>
public class ComponentForkTests
{
	private static TomlTable Result(string text) => CliSurfaceTests.Result(text);

	/// <summary>Commits <paramref name="file"/> on the main branch of the component's repository, through a scratch clone.</summary>
	private static async Task<string> CommitAsync(MonorepoFixture monorepo, string component, string file, string tag)
	{
		using var scratch = new TempDirectory();
		var clone = Path.Combine(scratch.Path, "clone");
		await TestEnvironment.GitAsync(scratch.Path, "clone", "--quiet", monorepo.SourceRepo(component), clone);
		await File.WriteAllTextAsync(Path.Combine(clone, file), file);
		await TestEnvironment.GitAsync(clone, "add", "--all");
		await TestEnvironment.GitAsync(clone, "commit", "--quiet", "-m", $"add {file}");
		var commit = await TestEnvironment.GitAsync(clone, "rev-parse", "HEAD");
		await TestEnvironment.GitAsync(clone, "tag", "-a", tag, "-m", tag);
		await TestEnvironment.GitAsync(clone, "push", "--quiet", "origin", "HEAD", tag);
		return commit;
	}

	[Fact]
	public async Task Fork_KeepsParentHistoryOnItsOwnMainBranch()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		await monorepo.AddComponentAsync("app");
		await monorepo.SetReferencesAsync("app", "lib");
		var second = await CommitAsync(monorepo, "app", "b.txt", "v1");
		await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "tag", "-a", "agent-run/x", "-m", "run", second);

		var (exitCode, output, error) = await monorepo.BassiaAsync("component", "fork", "-name", "app", "-as", "app-search");

		Assert.True(exitCode == 0, error);
		var result = Result(output);
		var fork = monorepo.SourceRepo("app-search");
		Assert.Equal("app", result["fork_of"]);
		Assert.Equal(second, result["fork_commit"]);
		Assert.Equal(await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "log", "--format=%H", "main"), await TestEnvironment.GitAsync(fork, "log", "--format=%H", "main"));
		Assert.Equal("main", await TestEnvironment.GitAsync(fork, "branch", "--format=%(refname:short)"));
		Assert.Equal("v0\nv1", (await TestEnvironment.GitAsync(fork, "tag", "--list")).Replace("\r", ""));
		Assert.Equal("", await TestEnvironment.GitAsync(fork, "remote"));

		var toml = await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml"));
		Assert.Contains("fork_of = \"app\"", toml);
		Assert.Contains($"fork_commit = \"{second}\"", toml);
		var show = Result((await monorepo.BassiaAsync("component", "show", "-name", "app-search")).Output);
		Assert.Equal("app", show["fork_of"]);
		Assert.Equal("lib", ((TomlArray)show["references"]).Single());

		// Independent from here on: a later commit in the parent does not reach the fork.
		await CommitAsync(monorepo, "app", "c.txt", "v2");
		Assert.Equal(second, await TestEnvironment.GitAsync(fork, "rev-parse", "main"));
	}

	[Fact]
	public async Task Fork_AtAnEarlierRef_LeavesLaterHistoryBehind()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		var baseline = await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "rev-parse", "v0^{commit}");
		var second = await CommitAsync(monorepo, "app", "b.txt", "v1");

		var (exitCode, _, error) = await monorepo.BassiaAsync("component", "fork", "-name", "app", "-as", "app-old", "-ref", "v0");

		Assert.True(exitCode == 0, error);
		var fork = monorepo.SourceRepo("app-old");
		Assert.Equal(baseline, await TestEnvironment.GitAsync(fork, "rev-parse", "main"));
		Assert.Equal("v0", await TestEnvironment.GitAsync(fork, "tag", "--list"));
		using var probe = System.Diagnostics.Process.Start(
			new System.Diagnostics.ProcessStartInfo("git", ["-C", fork, "cat-file", "-e", second]) { RedirectStandardError = true, UseShellExecute = false })!;
		await probe.WaitForExitAsync();
		Assert.NotEqual(0, probe.ExitCode);
	}

	[Fact]
	public async Task Fork_Failures_LeaveNothingBehind()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("other");
		var before = await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml"));

		foreach (var args in new[]
		{
			new[] { "component", "fork", "-name", "missing", "-as", "x" },
			new[] { "component", "fork", "-name", "app", "-as", "other" },
			new[] { "component", "fork", "-name", "app", "-as", "bad/name" },
			new[] { "component", "fork", "-name", "app", "-as", "x", "-ref", "no-such-ref" },
			new[] { "component", "fork", "-name", "app", "-as", "x", "-references", "nope" }
		})
		{
			var (exitCode, _, _) = await monorepo.BassiaAsync(args);
			Assert.NotEqual(0, exitCode);
		}

		Assert.False(Directory.Exists(monorepo.SourceRepo("x")));
		Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml")));
	}
}
