namespace Bassia.Tests;

/// <summary><c>bassia boundary ...</c> and the tags, visibility and owners of the component commands, on the git storage.</summary>
public class BoundaryCommandTests
{
	private static string ComponentsToml(MonorepoFixture monorepo) => File.ReadAllText(Path.Combine(monorepo.MetaRepo, "components.toml"));

	[Fact]
	public async Task RuleAddedToAGraphThatBreaksIt_Warns_ThenNewViolationsAreRefused()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		await monorepo.AddComponentAsync("ui");
		await monorepo.AddComponentAsync("app");
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "app", "-references", "lib", "-tags", "type:app")).ExitCode);
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "ui", "-tags", "type:ui")).ExitCode);

		var (addExit, addOutput, addError) = await monorepo.BassiaAsync("boundary", "add", "-source", "type:app", "-allow", "type:lib,type:ui", "-description", "apps use libs");

		Assert.True(addExit == 0, addError);
		Assert.Contains("violations = 1", addOutput);
		Assert.Contains("app -> lib: boundary #1 (type:app allow type:lib,type:ui)", addOutput);
		Assert.Contains("[[boundary]]\ndescription = \"apps use libs\"\nsource = \"type:app\"\nallow = [\"type:lib\", \"type:ui\"]", ComponentsToml(monorepo));
		Assert.Contains("Add boundary rule: type:app allow type:lib,type:ui", await TestEnvironment.GitAsync(monorepo.MetaRepo, "log", "-1", "--format=%s"));

		var (checkExit, checkOutput, _) = await monorepo.BassiaAsync("boundary", "check");
		var (strictExit, _, strictError) = await monorepo.BassiaAsync("boundary", "check", "-strict");
		Assert.Equal((0, 1), (checkExit, strictExit));
		Assert.Contains("violations = 1", checkOutput);
		Assert.Contains("from = \"app\"", strictError);

		// The existing violation does not block other changes; a new one is refused and nothing changes.
		var (keptExit, keptOutput, keptError) = await monorepo.BassiaAsync("component", "set", "app", "-references", "lib,ui", "-tags", "type:app,scope:x");
		Assert.True(keptExit == 0, keptError);
		Assert.Contains("warnings = [", keptOutput);
		var before = ComponentsToml(monorepo);
		var (newExit, _, newError) = await monorepo.BassiaAsync("component", "create", "tool", "-references", "app", "-tags", "type:app");
		Assert.Equal(1, newExit);
		Assert.Contains("breaks the monorepo's boundaries: tool -> app", newError);
		Assert.False(Directory.Exists(monorepo.SourceRepo("tool")));
		Assert.Equal(before, ComponentsToml(monorepo));

		var (fixExit, fixOutput, fixError) = await monorepo.BassiaAsync("component", "set", "lib", "-tags", "type:lib", "-owners", "team-lib");
		Assert.True(fixExit == 0, fixError);
		Assert.DoesNotContain("warnings", fixOutput);
		Assert.Contains("No boundary violations", (await monorepo.BassiaAsync("boundary", "check", "-strict")).Output);

		var (showExit, showOutput, _) = await monorepo.BassiaAsync("component", "show", "lib");
		Assert.Equal(0, showExit);
		Assert.Contains("tags = [\"type:lib\"]", showOutput);
		Assert.Contains("owners = [\"team-lib\"]", showOutput);

		var (removeExit, removeOutput, removeError) = await monorepo.BassiaAsync("boundary", "remove", "1");
		Assert.True(removeExit == 0, removeError);
		Assert.Contains("0 rule(s) left", removeOutput);
		Assert.DoesNotContain("\n[[boundary]]", ComponentsToml(monorepo));
	}

	[Fact]
	public async Task Visibility_RefusesAReferrerItDoesNotList_AndPublicLiftsIt()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("core");
		await monorepo.AddComponentAsync("billing");
		await monorepo.AddComponentAsync("search");
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "billing", "-tags", "scope:billing")).ExitCode);
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "core", "-visibility", "scope:billing,component:tool")).ExitCode);
		Assert.Contains("visibility = [\"scope:billing\", \"component:tool\"]", ComponentsToml(monorepo));

		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "billing", "-references", "core")).ExitCode);
		var (exit, _, error) = await monorepo.BassiaAsync("component", "set", "search", "-references", "core");
		Assert.Equal(1, exit);
		Assert.Contains("core is visible only to scope:billing, component:tool", error);

		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "core", "-public")).ExitCode);
		Assert.DoesNotContain("\nvisibility =", ComponentsToml(monorepo));
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "search", "-references", "core")).ExitCode);

		var (privateExit, _, privateError) = await monorepo.BassiaAsync("component", "set", "core", "-private");
		Assert.Equal(1, privateExit);
		Assert.Contains("core is private", privateError);
	}

	[Fact]
	public async Task HandEditedRulesAndTags_AreRead_AndTheirViolationsReportedAsWarnings()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("api");
		await monorepo.AddComponentAsync("web");
		var path = Path.Combine(monorepo.MetaRepo, "components.toml");
		var text = ComponentsToml(monorepo).Replace("name = \"web\"", "name = \"web\"\ntags = [\"scope:web\"]\nreferences = [\"api\"]")
			.Replace("name = \"api\"", "name = \"api\"\ntags = [\"scope:api\"]");
		await File.WriteAllTextAsync(path, text + "\n# scopes stay apart\n[[boundary]]\nsource = \"scope:*\"\nallow = [\"scope:$1\"]\n");

		var (listExit, listOutput, listError) = await monorepo.BassiaAsync("boundary", "list");
		Assert.True(listExit == 0, listError);
		Assert.Contains("source = \"scope:*\"", listOutput);
		Assert.Contains("web -> api: boundary #1 (scope:* allow scope:$1) lets scope:web reference only scope:web", listOutput);

		var (statusExit, statusOutput, _) = await monorepo.BassiaAsync("status");
		Assert.Equal(0, statusExit);
		Assert.Contains("boundary_violations = 1", statusOutput);
		Assert.Contains("1 boundary violation(s)", (await monorepo.BassiaAsync("component", "list")).Output);
		Assert.Contains("violations = 1", (await monorepo.BassiaAsync("graph", "-format", "tree")).Output);

		// Editing a component keeps the hand-written comment and the rule.
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "api", "-owners", "team-api")).ExitCode);
		Assert.Contains("# scopes stay apart\n[[boundary]]", ComponentsToml(monorepo));
		Assert.Contains("owners = [\"team-api\"]", ComponentsToml(monorepo));
	}

	[Theory]
	[InlineData("[[boundary]]\nallow = [\"x\"]\n", "has no 'source'")]
	[InlineData("[[boundary]]\nsource = \"type:a\"\n", "needs 'allow', 'deny' or both")]
	[InlineData("[[boundary]]\nsource = \"type:a\"\nallow = [\"scope:$1\"]\n", "uses $1")]
	[InlineData("[[boundary]]\nsource = \"type:a\"\nallowed = [\"x\"]\n", "unknown key 'allowed'")]
	public async Task InvalidRuleInComponentsToml_IsReportedByName(string rule, string expected)
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await File.AppendAllTextAsync(Path.Combine(monorepo.MetaRepo, "components.toml"), "\n" + rule);

		var (exit, _, error) = await monorepo.BassiaAsync("boundary", "list");

		Assert.Equal(1, exit);
		Assert.Contains("[[boundary]] #1", error);
		Assert.Contains(expected, error);
	}

	[Fact]
	public async Task Set_ReferencesAndTagsTogether_AreOneRevision_RefusedAsAWhole()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		await monorepo.AddComponentAsync("app");
		Assert.Equal(0, (await monorepo.BassiaAsync("boundary", "add", "-source", "type:app", "-allow", "type:lib")).ExitCode);
		var head = await TestEnvironment.GitAsync(monorepo.MetaRepo, "rev-parse", "HEAD");

		var (exit, _, error) = await monorepo.BassiaAsync("component", "set", "app", "-references", "lib", "-tags", "type:app");

		Assert.Equal(1, exit);
		Assert.Contains("app -> lib", error);
		Assert.Equal(head, await TestEnvironment.GitAsync(monorepo.MetaRepo, "rev-parse", "HEAD"));
		Assert.DoesNotContain("references", ComponentsToml(monorepo).Split("\n[[component]]")[^1]);

		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "lib", "-tags", "type:lib")).ExitCode);
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "app", "-references", "lib", "-tags", "type:app")).ExitCode);
		Assert.Equal("Component 'app': references lib; tags type:app", await TestEnvironment.GitAsync(monorepo.MetaRepo, "log", "-1", "--format=%s"));
	}

	[Fact]
	public async Task ForkTakesTheParentsAttributes_UnlessGiven()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "lib", "-tags", "type:lib", "-owners", "a,b")).ExitCode);

		var (exit, output, error) = await monorepo.BassiaAsync("component", "fork", "lib", "-as", "lib-next");
		var (otherExit, otherOutput, otherError) = await monorepo.BassiaAsync("component", "fork", "lib", "-as", "lib-exp", "-tags", "type:experiment");

		Assert.True(exit == 0, error);
		Assert.True(otherExit == 0, otherError);
		Assert.Contains("tags = [\"type:lib\"]", output);
		Assert.Contains("owners = [\"a\", \"b\"]", output);
		Assert.Contains("tags = [\"type:experiment\"]", otherOutput);
	}

	[Fact]
	public async Task Set_WithoutAnythingToChange_OrConflictingSwitches_IsAUsageError()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib");

		var (noneExit, _, noneError) = await monorepo.BassiaAsync("component", "set", "lib");
		var (bothExit, _, bothError) = await monorepo.BassiaAsync("component", "set", "lib", "-tags", "a:b", "-clear-tags");
		var (visibilityExit, _, visibilityError) = await monorepo.BassiaAsync("component", "set", "lib", "-public", "-private");

		Assert.Equal((2, 2, 2), (noneExit, bothExit, visibilityExit));
		Assert.Contains("Give what to change", noneError);
		Assert.Contains("either -tags or -clear-tags", bothError);
		Assert.Contains("only one of -visibility, -public and -private", visibilityError);
	}

	// ----- code boundaries -----

	/// <summary>lib (api.ts exported, secret.ts not) and app nesting lib, whose main.ts uses the API.</summary>
	private static async Task<MonorepoFixture> CodeMonorepoAsync()
	{
		var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("lib", files: new Dictionary<string, string> { ["api.ts"] = "export const a = 1;\n", ["secret.ts"] = "export const s = 1;\n" });
		await monorepo.AddComponentAsync("app", files: new Dictionary<string, string> { ["main.ts"] = "import { a } from './lib/api';\n" });
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "app", "-references", "lib")).ExitCode);
		var (exit, _, error) = await monorepo.BassiaAsync("component", "set", "lib", "-exports", "api.ts");
		Assert.True(exit == 0, error);
		return monorepo;
	}

	[Fact]
	public async Task CheckCode_ScansTheTips_AndStrictFailsOnAPrivateUse()
	{
		await using var monorepo = await CodeMonorepoAsync();
		Assert.Contains("exports = [\"api.ts\"]", ComponentsToml(monorepo));

		var (cleanExit, cleanOutput, cleanError) = await monorepo.BassiaAsync("boundary", "check", "-code", "-strict");
		Assert.True(cleanExit == 0, cleanError);
		Assert.Contains("code_violations = 0", cleanOutput);
		Assert.Contains("used_references = [\"lib\"]", cleanOutput);

		await monorepo.AddComponentAsync("tool", files: new Dictionary<string, string> { ["tool.ts"] = "import { s } from './lib/secret';\n" });
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "set", "tool", "-references", "lib")).ExitCode);
		var (exit, _, error) = await monorepo.BassiaAsync("boundary", "check", "-code", "-strict");

		Assert.Equal(1, exit);
		Assert.Contains("kind = \"private\"", error);
		Assert.Contains("tool/tool.ts:1: './lib/secret' uses 'secret' of lib, which exports only api.ts", error);
	}

	[Fact]
	public async Task Run_RecordsTheViolationsItAdds_GuidesTheAgent_AndManualBoundariesHoldsItInTheIntegration()
	{
		await using var monorepo = await CodeMonorepoAsync();

		var (exit, output, error) = await monorepo.RunStartAsync("-select", "app@v0,lib@v0", "-run", TestEnvironment.WriteFileCommand("app/main.ts", "import { s } from \"./lib/secret\";"));

		Assert.True(exit == 0, error);
		Assert.Contains("adds 1 boundary violation(s)", output);
		var runId = TestEnvironment.RunIdOf(output);
		var guide = await File.ReadAllTextAsync(Path.Combine(monorepo.RunDir(runId), "AGENTS.md"));
		Assert.Contains("# Architectural boundaries in this agentic run", guide);
		Assert.Contains("`lib/`: references nothing; exports only `api.ts`", guide);
		var (showExit, showOutput, _) = await monorepo.BassiaAsync("agent", "show", runId);
		Assert.Equal(0, showExit);
		Assert.Contains("boundary_violations = 1", showOutput);

		Assert.Equal(0, (await monorepo.BassiaAsync("config", "set", "-key", "merge.boundaries", "-value", "manual")).ExitCode);
		var (planExit, planOutput, planError) = await monorepo.BassiaAsync("integration", "plan", "-runs", runId);
		Assert.True(planExit == 0, planError);
		Assert.Contains("strategy = \"manual\"", planOutput);
		Assert.Contains("merge.boundaries = manual", planOutput);
	}
}
