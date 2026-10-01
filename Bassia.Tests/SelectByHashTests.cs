using System.Security.Cryptography;
using System.Text;
using Bassia.CliCommands.Agent;

namespace Bassia.Tests;

/// <summary><c>-select component@&lt;hash&gt;</c>: a commit hash (or a prefix of at least 6 digits) instead of an annotated tag.</summary>
public class SelectByHashTests
{
	[Theory]
	[InlineData("abcdef", true)]
	[InlineData("0123456789abcdef0123456789abcdef01234567", true)]
	[InlineData("abcde", false)]
	[InlineData("ABCDEF", false)]
	[InlineData("v0abcdef", false)]
	[InlineData("refs/tags/abcdef", false)]
	public void IsHashSelector_RequiresSixToSixtyFourLowercaseHexDigits(string commitIsh, bool expected) =>
		Assert.Equal(expected, AgentCommand.IsHashSelector(commitIsh));

	[Theory]
	[InlineData(6)]
	[InlineData(40)]
	public async Task Select_CommitHash_StartsTheRunFromThatCommit(int length)
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await monorepo.SetReferencesAsync("app", "lib");
		var commit = await CommitAsync(monorepo.SourceRepo("app"), "untagged");
		var hash = await UniquePrefixAsync(monorepo.SourceRepo("app"), commit, length);

		var (exitCode, output, error) = await monorepo.RunStartAsync(
			"-select", $"app@{hash},lib@v0", "-run", TestEnvironment.WriteFileCommand("app/hello.cs", "class Hello {}"));

		Assert.True(exitCode == 0, error);
		Assert.Contains($"commitish = \"{hash}\"", output);
		Assert.Contains($"commit = \"{commit}\"", output);
		Assert.Contains("commitish = \"v0\"", output);
		var runId = TestEnvironment.RunIdOf(output);
		var result = await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "rev-parse", $"{RunMetadata.TagName(runId, 0)}^{{commit}}");
		Assert.Equal(commit, await TestEnvironment.GitAsync(monorepo.SourceRepo("app"), "rev-parse", $"{result}^"));
	}

	[Fact]
	public async Task Select_AmbiguousHashPrefix_FailsWithGitsError()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var prefix = await WriteCollidingCommitsAsync(monorepo.SourceRepo("example"), 6);

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", $"example@{prefix}", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains($"short object ID {prefix} is ambiguous", error);
		Assert.Contains("select it with a longer hash", error);
		Assert.False(monorepo.HasRunDirs);
	}

	[Fact]
	public async Task Select_HexNameThatIsBothRefAndHash_FailsAsAmbiguous()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var source = monorepo.SourceRepo("example");
		var commit = await TestEnvironment.GitAsync(source, "rev-parse", "v0^{commit}");
		var hash = await UniquePrefixAsync(source, commit, 8);
		await TestEnvironment.GitAsync(source, "tag", "-a", hash, "-m", "a tag named like a hash", "v0");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", $"example@{hash}", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains($"'example@{hash}' is ambiguous: it names the ref 'refs/tags/{hash}'", error);
		Assert.False(monorepo.HasRunDirs);

		// The full ref name is an unambiguous spelling of the same tag.
		var (fullRefExitCode, output, fullRefError) = await monorepo.RunStartAsync("-select", $"example@refs/tags/{hash}", "-run", "echo");
		Assert.True(fullRefExitCode == 0, fullRefError);
		Assert.Contains($"commit = \"{commit}\"", output);
	}

	[Theory]
	[InlineData("abcde")]
	[InlineData("c0ffee")]
	public async Task Select_HexTagThatPrefixesNoObject_IsATag(string tag)
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example", tag);
		var source = monorepo.SourceRepo("example");
		Assert.Empty(await TestEnvironment.GitAsync(source, "rev-parse", $"--disambiguate={tag.PadRight(6, '0')}"));

		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", $"example@{tag}", "-run", "echo");

		Assert.True(exitCode == 0, error);
		Assert.Contains($"commit = \"{await TestEnvironment.GitAsync(source, "rev-parse", $"{tag}^{{commit}}")}\"", output);
	}

	[Fact]
	public async Task Select_UnknownHash_Fails()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var source = monorepo.SourceRepo("example");
		var unknown = "0123456789abcdef0123456789abcdef01234567";
		Assert.Empty(await TestEnvironment.GitAsync(source, "rev-parse", $"--disambiguate={unknown}"));

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", $"example@{unknown}", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains($"'{unknown}' does not exist in component 'example'", error);
		Assert.False(monorepo.HasRunDirs);
	}

	[Fact]
	public async Task Select_HashOfATree_Fails()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var source = monorepo.SourceRepo("example");
		var tree = await TestEnvironment.GitAsync(source, "rev-parse", "v0^{tree}");

		var (exitCode, _, error) = await monorepo.RunStartAsync("-select", $"example@{tree}", "-run", "echo");

		Assert.Equal(1, exitCode);
		Assert.Contains($"'example@{tree}' is a tree, not a commit", error);
		Assert.False(monorepo.HasRunDirs);
	}

	/// <summary>An untagged commit on top of v0, reachable from a branch (source-of-truth repos have no work tree).</summary>
	private static async Task<string> CommitAsync(string repo, string branch)
	{
		var commit = await TestEnvironment.GitAsync(repo, "commit-tree", "v0^{tree}", "-p", "v0^{commit}", "-m", $"untagged on {branch}");
		await TestEnvironment.GitAsync(repo, "branch", branch, commit);
		return commit;
	}

	/// <summary>The commit's hash cut to <paramref name="length"/> digits, checked to be unique among the repo's objects.</summary>
	private static async Task<string> UniquePrefixAsync(string repo, string commit, int length)
	{
		var prefix = commit[..length];
		Assert.Equal(commit, await TestEnvironment.GitAsync(repo, "rev-parse", $"--disambiguate={prefix}"));
		return prefix;
	}

	/// <summary>
	/// Writes two commit objects whose hashes share their first <paramref name="digits"/> hex digits and returns that
	/// prefix. The candidates differ only in their message, so the birthday bound finds a pair in a few thousand tries.
	/// </summary>
	private static async Task<string> WriteCollidingCommitsAsync(string repo, int digits)
	{
		var tree = await TestEnvironment.GitAsync(repo, "rev-parse", "HEAD^{tree}");
		var parent = await TestEnvironment.GitAsync(repo, "rev-parse", "HEAD");
		var seen = new Dictionary<string, string>(StringComparer.Ordinal);
		for (var i = 0; ; i++)
		{
			var body = $"tree {tree}\nparent {parent}\nauthor Bassia Tests <tests@bassia.invalid> 1700000000 +0000\n" +
				$"committer Bassia Tests <tests@bassia.invalid> 1700000000 +0000\n\ncandidate {i}\n";
			var bytes = Encoding.UTF8.GetBytes(body);
			var hash = Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"commit {bytes.Length}\0"), .. bytes]));
			var prefix = hash[..digits];
			if (seen.TryGetValue(prefix, out var other))
			{
				using var temp = new TempDirectory();
				foreach (var content in new[] { other, body })
				{
					var file = Path.Combine(temp.Path, "candidate");
					await File.WriteAllTextAsync(file, content);
					await TestEnvironment.GitAsync(repo, "hash-object", "-t", "commit", "-w", file);
				}

				return prefix;
			}

			seen[prefix] = body;
		}
	}
}
