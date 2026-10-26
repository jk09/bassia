namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Split;
using Bassia.Storage;
using Bassia.Storage.Memory;
using static MemoryMonorepo;

/// <summary>Tags, visibility and boundary rules of the component graph, enforced by the meta-repo, with no storage behind it.</summary>
public class BoundaryTests
{
	private static ComponentDefinition Component(string name, string[] tags, params string[] references) =>
		new(name, "", Refs(references)) { Tags = tags };

	private static BoundaryRule Rule(string source, string[]? allow = null, string[]? deny = null) => new(source, allow ?? [], deny ?? []);

	private static ComponentRegistry Registry(IEnumerable<BoundaryRule> rules, params ComponentDefinition[] components) =>
		ComponentRegistry.FromDefinitions(components, rules);

	[Theory]
	[InlineData("type:*", "type:lib", true)]
	[InlineData("type:*", "scope:lib", false)]
	[InlineData("scope:bill?ng", "scope:billing", true)]
	[InlineData("*", "anything:at-all", true)]
	[InlineData("type:lib", "type:library", false)]
	[InlineData("a.b", "aXb", false)]
	public void TagPattern_Matches_WithGlobWildcards(string pattern, string tag, bool matches) =>
		Assert.Equal(matches, TagPattern.Parse(pattern, "test").Matches(tag));

	[Fact]
	public void TagPattern_CapturesWildcards_ForDollarSubstitution()
	{
		var captures = TagPattern.Parse("scope:*/?", "test").Match("scope:billing/x");

		Assert.Equal(["billing", "x"], captures);
		Assert.Equal("scope:billing,team:x,$3", TagPattern.Substitute("scope:$1,team:$2,$3", captures!));
	}

	[Fact]
	public void Allow_ReferenceToAComponentWithoutAnAllowedTag_IsAViolation()
	{
		var registry = Registry([Rule("type:app", allow: ["type:lib"])],
			Component("lib", ["type:lib"]), Component("ui", ["type:ui"]), Component("app", ["type:app"], "lib", "ui"));

		var violation = Assert.Single(registry.Violations());

		Assert.Equal(("app", "ui", ViolationKind.Rule), (violation.From, violation.To, violation.Kind));
		Assert.Contains("boundary #1 (type:app allow type:lib)", violation.Message);
		Assert.Contains("ui has component:ui, type:ui", violation.Message);
	}

	[Fact]
	public void Deny_ReferenceToADeniedTag_IsAViolation_AndUnconstrainedComponentsAreFree()
	{
		var registry = Registry([Rule("type:lib", deny: ["type:app"])],
			Component("app", ["type:app"]), Component("lib", ["type:lib"], "app"), Component("tool", [], "app"));

		var violation = Assert.Single(registry.Violations());

		Assert.Equal(("lib", "app"), (violation.From, violation.To));
		Assert.Contains("forbids type:lib to reference type:app", violation.Message);
	}

	[Fact]
	public void Captures_KeepScopesApart_WithASharedScopeAllowed()
	{
		var registry = Registry([Rule("scope:*", allow: ["scope:$1", "scope:shared"])],
			Component("shared", ["scope:shared"]),
			Component("billing-core", ["scope:billing"], "shared"),
			Component("billing-api", ["scope:billing"], "billing-core"),
			Component("search-api", ["scope:search"], "billing-core", "shared"));

		var violation = Assert.Single(registry.Violations());

		Assert.Equal(("search-api", "billing-core"), (violation.From, violation.To));
		Assert.Contains("lets scope:search reference only scope:search, scope:shared", violation.Message);
	}

	[Fact]
	public void Rules_AreAppliedOncePerMatchingSourceTag()
	{
		var registry = Registry([Rule("scope:*", allow: ["scope:$1"])],
			Component("a", ["scope:a"]), Component("both", ["scope:a", "scope:b"], "a"));

		var violation = Assert.Single(registry.Violations());

		Assert.Contains("(as scope:b)", violation.Constraint);
	}

	[Fact]
	public void Visibility_LimitsReferrers_ByTagOrImplicitComponentTag()
	{
		var core = new ComponentDefinition("core", "", []) { Visibility = ["scope:billing", "component:tool"] };
		var hidden = new ComponentDefinition("hidden", "", []) { Visibility = [] };
		var registry = Registry([], core, hidden,
			Component("billing", ["scope:billing"], "core"), Component("tool", [], "core"), Component("search", ["scope:search"], "core", "hidden"));

		var violations = registry.Violations();

		Assert.Equal([("search", "core"), ("search", "hidden")], violations.Select(violation => (violation.From, violation.To)));
		Assert.All(violations, violation => Assert.Equal(ViolationKind.Visibility, violation.Kind));
		Assert.Contains("hidden is private", violations[1].Message);
	}

	[Theory]
	[InlineData("", "type:lib", "has no 'source'")]
	[InlineData("type:app", null, "needs 'allow', 'deny' or both")]
	[InlineData("type:app", "scope:$1", "uses $1, but the source 'type:app' has 0 wildcard(s)")]
	[InlineData("type:app", "has space", "not a valid tag pattern")]
	public void InvalidRule_IsRejected(string source, string? allow, string expected)
	{
		var rule = Rule(source, allow is null ? [] : [allow]);

		Assert.Contains(expected, Assert.Throws<MonorepoException>(() => ComponentRegistry.Empty.AddBoundary(rule)).Message);
		Assert.Contains(expected, Assert.Throws<MonorepoException>(() => ComponentRegistry.FromDefinitions([], [rule])).Message);
	}

	[Theory]
	[InlineData("component:lib", "is implicit")]
	[InlineData("type:*", "is not a valid tag")]
	[InlineData("a,b", "is not a valid tag")]
	public void InvalidTag_IsRejected(string tag, string expected) =>
		Assert.Contains(expected, Assert.Throws<MonorepoException>(() => ComponentRegistry.Empty.Add(Component("lib", [tag]))).Message);

	[Fact]
	public async Task MetaRepo_RefusesAGraphChangeThatAddsAViolation_AndPersistsNothing()
	{
		var store = new MemoryMetaStore();
		var metaRepo = await MetaRepo.OpenAsync(store);
		await metaRepo.CommitAsync([new RegistryChange.Add(Component("app", ["type:app"])), new RegistryChange.Add(Component("tool", ["type:tool"]))], "add");
		await metaRepo.CommitAsync(new RegistryChange.AddBoundary(Rule("type:lib", deny: ["type:app"])), "rule");
		await metaRepo.CommitAsync(new RegistryChange.Add(Component("lib", ["type:lib"])), "lib");

		var error = await Assert.ThrowsAsync<BoundaryViolationException>(() => metaRepo.CommitAsync(new RegistryChange.SetReferences("lib", Refs("app")), "lib -> app"));

		Assert.Equal(("lib", "app"), (error.Violations[0].From, error.Violations[0].To));
		Assert.Empty(metaRepo.Registry.Require("lib").References);
		Assert.Equal(3, store.Revisions.Count);
		await metaRepo.CommitAsync(new RegistryChange.SetReferences("lib", Refs("tool")), "lib -> tool");
		Assert.Equal(["tool"], metaRepo.Registry.Require("lib").References.Select(reference => reference.Name));
	}

	[Fact]
	public async Task MetaRepo_KeepsExistingViolations_AndANewRuleOnlyRevealsThem()
	{
		var metaRepo = await MetaRepo.OpenAsync(new MemoryMetaStore());
		await metaRepo.CommitAsync([new RegistryChange.Add(Component("app", ["type:app"])), new RegistryChange.Add(Component("lib", ["type:lib"], "app"))], "add");

		await metaRepo.CommitAsync(new RegistryChange.AddBoundary(Rule("type:lib", deny: ["type:app"])), "a rule the graph breaks");
		Assert.Single(metaRepo.Registry.Violations());

		// A change elsewhere is not refused for the violation that was already there.
		await metaRepo.CommitAsync(new RegistryChange.Add(Component("tool", [], "app")), "tool");
		await metaRepo.CommitAsync(new RegistryChange.SetOwners("lib", ["team-a"]), "owners");

		// Retagging that adds a violation is refused; retagging that removes one is not.
		await Assert.ThrowsAsync<BoundaryViolationException>(() => metaRepo.CommitAsync(new RegistryChange.SetTags("tool", ["type:lib"]), "retag tool"));
		await metaRepo.CommitAsync(new RegistryChange.SetTags("lib", ["type:tool"]), "retag lib");
		Assert.Empty(metaRepo.Registry.Violations());

		await metaRepo.CommitAsync(new RegistryChange.RemoveBoundary(0), "remove the rule");
		Assert.Empty(metaRepo.Registry.Boundaries);
	}

	[Fact]
	public async Task MetaRepo_AllowNewViolations_RecordsThemAsWarnings()
	{
		var metaRepo = await MetaRepo.OpenAsync(new MemoryMetaStore());
		await metaRepo.CommitAsync(new RegistryChange.Add(new ComponentDefinition("core", "", []) { Visibility = [] }), "private core");

		await metaRepo.CommitAsync([new RegistryChange.Add(Component("app", [], "core"))], "unwound", allowNewViolations: true);

		Assert.Equal(ViolationKind.Visibility, Assert.Single(metaRepo.Registry.Violations()).Kind);
	}

	[Fact]
	public async Task Graph_CreateImportAndForkCarryAttributes_AndARefusedCreateLeavesNoStore()
	{
		using var mono = new MemoryMonorepo();
		var graph = mono.Graph;
		await graph.CreateAsync("lib", [], attributes: new ComponentAttributes(["type:lib"], ["type:app"], ["team-lib"]));
		await graph.AddBoundaryAsync(Rule("type:app", allow: ["type:lib"]));
		await graph.CreateAsync("app", Refs("lib"), attributes: new ComponentAttributes(["type:app"], null, []));

		var refused = await Assert.ThrowsAsync<BoundaryViolationException>(() =>
			graph.CreateAsync("tool", Refs("lib"), attributes: new ComponentAttributes(["type:tool"], null, [])));
		Assert.Contains("lib is visible only to type:app", refused.Message);
		Assert.False(mono.Storage.Exists("tool"));

		await graph.TagAsync("lib", "v1");
		var fork = await graph.ForkAsync("lib", "lib2");
		Assert.Equal(["type:lib"], fork.Component.Tags);
		Assert.Equal(["type:app"], fork.Component.Visibility);
		Assert.Equal(["team-lib"], fork.Component.Owners);

		var changed = await graph.SetAttributesAsync("lib", tags: ["type:lib", "scope:shared"], setVisibility: true, visibility: null);
		Assert.Null(changed.Component.Visibility);
		Assert.Equal(["type:lib", "scope:shared"], mono.MetaStore.LoadAsync().Result.Single(component => component.Name == "lib").Tags);
	}

	[Fact]
	public async Task Split_PartsTakeTheSourceAttributes_AndANewViolationIsAPlanProblem()
	{
		using var mono = new MemoryMonorepo();
		var url = mono.Upstream("app", ("core/a.txt", "a"), ("ui/b.txt", "b"));
		await mono.Graph.ImportAsync("app", url, [], new ComponentAttributes(["type:app"], null, ["team-app"]));
		await mono.Graph.AddBoundaryAsync(Rule("type:app", deny: ["type:app"]));

		var problems = new List<string>();
		var plan = SplitPlan.Parse("""
			source = "app"
			[[part]]
			name = "app-core"
			paths = ["core"]
			[[part]]
			name = "app-ui"
			paths = ["ui"]
			references = ["app-core"]
			""", problems);
		var error = await Assert.ThrowsAsync<SplitPlanException>(() => mono.Graph.PlanSplitAsync(plan, null, problems));
		Assert.Contains(error.Problems, problem => problem.Contains("app-ui -> app-core") && problem.Contains("forbids type:app"));

		problems.Clear();
		plan = SplitPlan.Parse("""
			source = "app"
			[[part]]
			name = "app-core"
			paths = ["core"]
			tags = ["type:lib"]
			[[part]]
			name = "app-ui"
			paths = ["ui"]
			references = ["app-core"]
			""", problems);
		var allocation = await mono.Graph.PlanSplitAsync(plan, null, problems);
		await mono.Graph.SplitAsync(allocation);

		Assert.Equal(["type:lib"], mono.Graph.Registry.Require("app-core").Tags);
		Assert.Equal(["type:app"], mono.Graph.Registry.Require("app-ui").Tags);
		Assert.Equal(["team-app"], mono.Graph.Registry.Require("app-core").Owners);
	}
}
