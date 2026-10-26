namespace Bassia.Tests;

/// <summary><c>bassia template list</c>, <c>component create -template</c>, <c>bassia conformance ...</c> and owner review, on the git storage.</summary>
public class TemplateAndConformanceCommandTests
{
	private static async Task WriteTemplateAsync(MonorepoFixture monorepo)
	{
		var folder = Path.Combine(monorepo.MetaRepo, "templates", "lib");
		Directory.CreateDirectory(Path.Combine(folder, "files", "src"));
		await File.WriteAllTextAsync(Path.Combine(folder, "template.toml"),
			"description = \"A library\"\ntags = [\"type:lib\"]\nexports = [\"src\"]\nowners = [\"team-libs\"]\n");
		await File.WriteAllTextAsync(Path.Combine(folder, "files", "README.md"), "# {{name}}\n");
		await File.WriteAllTextAsync(Path.Combine(folder, "files", "src", "{{name}}.ts"), "export const name = '{{name}}';\n");
	}

	[Fact]
	public async Task CreateFromTemplate_GivesFilesTagsExportsAndOwners_AndSwitchesWin()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await WriteTemplateAsync(monorepo);

		var (listExit, listOutput, listError) = await monorepo.BassiaAsync("template", "list");
		Assert.True(listExit == 0, listError);
		Assert.Contains("description = \"A library\"", listOutput);
		Assert.Contains("files = [\"README.md\", \"src/{{name}}.ts\"]", listOutput);

		var (exit, output, error) = await monorepo.BassiaAsync("component", "create", "parser", "-template", "lib", "-owners", "alice");

		Assert.True(exit == 0, error);
		Assert.Contains("from template 'lib' (2 file(s))", output);
		var repo = monorepo.SourceRepo("parser");
		Assert.Equal("README.md\nsrc/parser.ts", await TestEnvironment.GitAsync(repo, "ls-tree", "-r", "--name-only", "main"));
		Assert.Equal("# parser", await TestEnvironment.GitAsync(repo, "show", "main:README.md"));
		Assert.Equal("Apply template 'lib'\nCreate component 'parser'", await TestEnvironment.GitAsync(repo, "log", "--format=%s", "main"));
		var toml = await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml"));
		Assert.Contains("tags = [\"type:lib\"]\nowners = [\"alice\"]\nexports = [\"src\"]", toml);
	}

	[Fact]
	public async Task CreateFromAnUnknownTemplate_FailsAndCreatesNothing()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await WriteTemplateAsync(monorepo);

		var (exit, _, error) = await monorepo.BassiaAsync("component", "create", "parser", "-template", "nope");

		Assert.Equal(1, exit);
		Assert.Contains("There is no template 'nope'", error);
		Assert.Contains("templates: lib", error);
		Assert.False(Directory.Exists(monorepo.SourceRepo("parser")));
	}

	[Fact]
	public async Task Conformance_AddCheckRemove_ReportWhatComponentsLack()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "lib", "-tags", "type:lib")).ExitCode);

		var (addExit, addOutput, addError) = await monorepo.BassiaAsync("conformance", "add", "-components", "type:lib", "-files", "README.md,LICENSE", "-tasks", "test", "-owners",
			"-description", "libraries are documented, tested and owned");
		Assert.True(addExit == 0, addError);
		Assert.Contains("3 unmet requirement(s)", addOutput);
		Assert.Contains("[[conformance]]\ndescription = \"libraries are documented, tested and owned\"\ncomponents = \"type:lib\"\nfiles = [\"README.md\", \"LICENSE\"]\ntasks = [\"test\"]\nowners = true",
			await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml")));

		var (checkExit, checkOutput, _) = await monorepo.BassiaAsync("conformance", "check");
		var (strictExit, _, strictError) = await monorepo.BassiaAsync("conformance", "check", "-strict");
		Assert.Equal((0, 1), (checkExit, strictExit));
		Assert.Contains("findings = 3", checkOutput);
		Assert.Contains("lib: has no 'LICENSE'", strictError);

		Assert.Equal(0, (await monorepo.BassiaAsync("conformance", "remove", "1")).ExitCode);
		Assert.Contains("0 conformance rule(s)", (await monorepo.BassiaAsync("conformance", "list")).Output);
	}

	[Fact]
	public async Task OwnerReview_LeavesTheResultForTheOwners()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "lib", "-owners", "team-lib")).ExitCode);
		Assert.Equal(0, (await monorepo.BassiaAsync("config", "set", "-key", "merge.component.lib.owner_review", "-value", "manual")).ExitCode);
		var runId = await monorepo.RunWritingAsync("lib", "a.txt", "a");

		var (exit, output, error) = await monorepo.BassiaAsync("integration", "plan", "-runs", runId);

		Assert.True(exit == 0, error);
		Assert.Contains("owners = [\"team-lib\"]", output);
		Assert.Contains("strategy = \"manual\"", output);
		Assert.Contains("for its owners to review: team-lib", output);
	}
}
