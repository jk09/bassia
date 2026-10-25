namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Integration;
using Bassia.Storage;
using static MemoryMonorepo;

/// <summary>Conformance rules over the component graph, and owner review in the integration, on the memory storage.</summary>
public class ConformanceTests
{
	[Theory]
	[InlineData("", "has no 'components'")]
	[InlineData("type:lib", "requires nothing")]
	[InlineData("bad pattern", "not a valid tag pattern")]
	public void InvalidRule_IsRejected(string components, string expected) =>
		Assert.Contains(expected, Assert.Throws<MonorepoException>(() => ComponentRegistry.Empty.AddConformance(new ConformanceRule(components, [], []))).Message);

	[Fact]
	public void RuleWithAPathOutsideTheComponent_IsRejected() =>
		Assert.Contains("must be a relative path inside the component",
			Assert.Throws<MonorepoException>(() => ComponentRegistry.Empty.AddConformance(new ConformanceRule("*", ["../x"], []))).Message);

	[Fact]
	public async Task Check_ReportsEveryUnmetRequirement_OfTheRulesThatApply()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib", null, ("README.md", "# lib\n"), ("docs/guide.md", "guide\n"), (TaskFile.FileName, "[tasks.build]\ncommand = \"make\"\n"));
		await monorepo.AddAsync("app", null, ("main.c", "int main() {}\n"));
		await monorepo.Graph.SetAttributesAsync("lib", tags: ["type:lib"]);
		await monorepo.Graph.AddConformanceAsync(new ConformanceRule("type:lib", ["README.md", "docs", "LICENSE"], ["build", "test"], Owners: true, Exports: true));
		await monorepo.Graph.AddConformanceAsync(new ConformanceRule("component:app", ["README.md"], []));

		var findings = await ConformanceChecker.CheckAllAsync(monorepo.Graph.Registry, monorepo.Storage);

		Assert.Equal([("lib", "LICENSE"), ("lib", "task test"), ("lib", "owners"), ("lib", "exports"), ("app", "README.md")],
			findings.Select(finding => (finding.Component, finding.Missing)));
		Assert.Contains("lib: defines no task 'test' in its bassia.toml (type:lib needs README.md, docs, LICENSE, task build, task test, owners, exports).", findings[1].Message);

		await monorepo.Graph.RemoveConformanceAsync(0);
		Assert.Single(await ConformanceChecker.CheckAllAsync(monorepo.Graph.Registry, monorepo.Storage));
	}

	[Fact]
	public async Task Create_WithFiles_AddsThemInASecondCommit()
	{
		using var monorepo = new MemoryMonorepo();

		await monorepo.Graph.CreateAsync("core", [], files: new Dictionary<string, byte[]> { ["README.md"] = "# core\n"u8.ToArray(), ["src/a.txt"] = "a"u8.ToArray() },
			filesMessage: "Apply template 'lib'");

		var store = monorepo.Graph.Store("core");
		var tip = (await store.BranchTipAsync("main"))!;
		Assert.Equal(["README.md", "src/a.txt"], await FilesAsync(store, tip));
		var history = await store.HistoryAsync(tip);
		Assert.Equal(2, history.Count);
		Assert.Contains(history, commit => commit.Message.StartsWith("Apply template 'lib'", StringComparison.Ordinal));
	}

	[Fact]
	public async Task OwnerReview_LeavesAResultInAComponentWithOwnersForThem()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib", null, ("a.txt", "a\n"));
		await monorepo.Graph.SetAttributesAsync("lib", owners: ["team-lib", "alice"]);
		var outcome = await monorepo.Runs(metadata =>
		{
			File.WriteAllText(Path.Combine(metadata.WorkspacePath, "lib", "a.txt"), "changed\n");
			return 0;
		}).StartAsync("lib@v1", "agent \"change a\"", Quiet());
		IntegrationSettings Settings(bool review) => new()
		{
			WorkspaceDir = monorepo.WorkspaceDir,
			MergePolicyFor = _ => MergePolicy.Default with { OwnerReview = review },
			RunResolver = _ => throw new InvalidOperationException("no resolver expected")
		};

		var reviewed = Assert.Single(await IntegrationPlanner.PlanAsync(monorepo.Graph, Settings(true), [outcome.Metadata]));
		var merged = Assert.Single(await IntegrationPlanner.PlanAsync(monorepo.Graph, Settings(false), [outcome.Metadata]));

		Assert.Equal(["team-lib", "alice"], reviewed.Owners);
		Assert.Equal(MergeStrategy.Manual, Assert.Single(reviewed.Steps).Strategy);
		Assert.Contains("for its owners to review: team-lib, alice", reviewed.Steps[0].Note);
		Assert.Equal(MergeStrategy.Syntactic, Assert.Single(merged.Steps).Strategy);
	}
}
