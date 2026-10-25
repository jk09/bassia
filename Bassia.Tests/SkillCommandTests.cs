using Bassia.Skills;
using Tomlyn.Model;

namespace Bassia.Tests;

/// <summary><c>bassia skill list|show</c>: the meta-repo's reusable instructions, read by the agent that drives bassia.</summary>
public class SkillCommandTests
{
	[Fact]
	public void SkillFile_FrontMatterNamesAndDescribesIt_OtherwiseTheFolderAndFirstLineDo()
	{
		var withFrontMatter = SkillStore.Parse("folder", "---\nname: release\ndescription: \"Cut a release.\"\n---\n\n# Steps\n1. tag\n", "p");
		Assert.Equal("release", withFrontMatter.Name);
		Assert.Equal("Cut a release.", withFrontMatter.Description);
		Assert.Equal("# Steps\n1. tag", withFrontMatter.Body);

		var plain = SkillStore.Parse("onboard", "# Onboard a library\n\nAdd it.\n", "p");
		Assert.Equal("onboard", plain.Name);
		Assert.Equal("Onboard a library", plain.Description);
	}

	[Fact]
	public async Task SkillCommands_ListAndShowTheMetaReposSkills()
	{
		using var temp = new TempDirectory();
		var (initCode, _, initError) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "init");
		Assert.True(initCode == 0, initError);

		var (emptyCode, emptyOutput, _) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "skill", "list");
		Assert.Equal(0, emptyCode);
		Assert.StartsWith("No skills.", (string)CliSurfaceTests.Result(emptyOutput)["message"]);

		var directory = Path.Combine(SkillStore.DirectoryOf(temp.Path), "release");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, SkillStore.FileName), "---\nname: release\ndescription: Cut a release.\n---\nTag everything.\n");

		var (listCode, listOutput, _) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "skill", "list");
		Assert.Equal(0, listCode);
		var entry = Assert.Single(((TomlTableArray)CliSurfaceTests.Result(listOutput)["skill"]).Cast<TomlTable>());
		Assert.Equal("release", entry["name"]);
		Assert.Equal("Cut a release.", entry["description"]);

		var (showCode, showOutput, _) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "skill", "show", "release");
		Assert.Equal(0, showCode);
		Assert.Equal("Tag everything.\n", CliSurfaceTests.Result(showOutput)["body"]);

		var (missingCode, _, missingError) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "skill", "show", "nope");
		Assert.Equal(1, missingCode);
		Assert.Contains("Known skills: release", missingError);
	}
}
