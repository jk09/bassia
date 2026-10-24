namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Split;
using Bassia.Storage;
using static MemoryMonorepo;

/// <summary>Splitting a component into parts and unwrapping nested units into components, through the graph API on in-memory storage.</summary>
public class SplitAndUnwrapTests
{
	private static SplitPlan Plan(string toml)
	{
		var problems = new List<string>();
		var plan = SplitPlan.Parse(toml, problems);
		Assert.Empty(problems);
		return plan;
	}

	private const string TwoParts = """
		source = "app"
		shared = ["README.md"]
		drop = ["legacy/**"]

		[[part]]
		name = "app-core"
		paths = ["core/**"]

		[[part]]
		name = "app-ui"
		paths = ["ui/**"]
		references = ["app-core", "lib"]
		""";

	/// <summary>lib, app (references lib, has core/ ui/ legacy/ and a README over three commits) and tool (references app).</summary>
	private static async Task<(MemoryMonorepo Monorepo, string Tip)> SplittableAsync()
	{
		var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib");
		var app = await monorepo.AddAsync("app", Refs("lib"), ("README.md", "# app"), ("core/a.c", "a"), ("legacy/old.c", "old"));
		app.Commit("main", "Add the UI", new Dictionary<string, string?> { ["ui/view.c"] = "view" });
		var tip = app.Commit("main", "Change the core", new Dictionary<string, string?> { ["core/a.c"] = "a2", ["core/b.c"] = "b" });
		await monorepo.AddAsync("tool", Refs("app"));
		return (monorepo, tip);
	}

	[Fact]
	public async Task Split_RegistersTheParts_RewiresReferrers_AndRetiresTheSource()
	{
		var (monorepo, tip) = await SplittableAsync();
		using var _ = monorepo;

		var allocation = await monorepo.Graph.PlanSplitAsync(Plan(TwoParts), null, []);
		var result = await monorepo.Graph.SplitAsync(allocation);

		var registry = monorepo.Graph.Registry;
		Assert.False(registry.Contains("app"));
		Assert.Equal(["lib"], registry.Require("app-core").References.Select(reference => reference.Name));
		Assert.Equal(["app-core", "lib"], registry.Require("app-ui").References.Select(reference => reference.Name));
		Assert.Equal(["app-core", "app-ui"], registry.Require("tool").References.Select(reference => reference.Name).Order());
		Assert.Contains("app-core", monorepo.MetaStore.Revisions[^1].Message);

		// The source keeps its history, marked with the split tag; each part ends in exactly its files plus the shared ones.
		Assert.Equal(tip, (await monorepo.Storage["app"].GetTagAsync(result.SourceTag))!.Commit);
		var core = monorepo.Storage["app-core"];
		var ui = monorepo.Storage["app-ui"];
		Assert.Equal(["README.md", "core/a.c", "core/b.c"], await FilesAsync(core, (await core.BranchTipAsync("main"))!));
		Assert.Equal(["README.md", "ui/view.c"], await FilesAsync(ui, (await ui.BranchTipAsync("main"))!));
		Assert.NotNull(await core.GetTagAsync(result.SourceTag));
		Assert.Equal(["app-core", "app-ui"], result.Parts.Select(part => part.Name));
	}

	[Fact]
	public async Task Split_History_LeavesOutCommitsThatDoNotTouchThePart()
	{
		var (monorepo, _) = await SplittableAsync();
		using var _ = monorepo;

		await monorepo.Graph.SplitAsync(await monorepo.Graph.PlanSplitAsync(Plan(TwoParts), null, []));

		var core = monorepo.Storage["app-core"];
		var subjects = (await core.HistoryAsync((await core.BranchTipAsync("main"))!)).Select(commit => commit.Subject).ToList();
		Assert.DoesNotContain("Add the UI", subjects);
		Assert.Contains("Change the core", subjects);
		Assert.StartsWith("split(", subjects[0]);
	}

	[Fact]
	public async Task PlanSplit_ReportsEveryProblem_AndChangesNothing()
	{
		var (monorepo, _) = await SplittableAsync();
		using var _ = monorepo;
		var problems = new List<string>();

		var error = await Assert.ThrowsAsync<SplitPlanException>(() => monorepo.Graph.PlanSplitAsync(Plan("""
			source = "app"

			[[part]]
			name = "lib"
			paths = ["core/**"]

			[[part]]
			name = "app-ui"
			paths = ["ui/**"]
			references = ["app"]
			"""), null, problems));

		Assert.Contains(error.Unallocated, path => path == "README.md");
		Assert.Contains(error.Problems, problem => problem.Contains("'lib': a component of that name is already registered"));
		Assert.Contains(error.Problems, problem => problem.Contains("references the source 'app'"));
		Assert.True(monorepo.Graph.Registry.Contains("app"));
		Assert.DoesNotContain("app-ui", monorepo.Storage.Names);
	}

	// ----- unwrapping nested units -----

	/// <summary>An upstream lib, and an upstream app whose tip nests lib at vendor/lib, pinned at lib's first commit.</summary>
	private static (string AppUrl, string LibUrl, string LibCommit) Nesting(MemoryMonorepo monorepo)
	{
		var libUrl = monorepo.Upstream("lib", ("lib.c", "lib"));
		var lib = monorepo.Upstreams.At(libUrl);
		var libCommit = lib.BranchTipAsync("main").Result!;
		lib.Commit("main", "Later lib work", new Dictionary<string, string?> { ["more.c"] = "more" });
		var appUrl = monorepo.Upstream("app", ("main.c", "app"));
		monorepo.Upstreams.At(appUrl).Commit("main", "Add lib as a submodule", nested: [new NestedUnit("vendor/lib", libCommit, "lib", libUrl)]);
		return (appUrl, libUrl, libCommit);
	}

	[Fact]
	public async Task ImportUnwrapped_AddsTheNestedUnitAsAReferencedComponent()
	{
		using var monorepo = new MemoryMonorepo();
		var (appUrl, libUrl, libCommit) = Nesting(monorepo);

		using var unwinder = await monorepo.Graph.ImportUnwrappedAsync("app", appUrl, []);
		Assert.Empty(monorepo.Graph.Registry.Components);
		Assert.DoesNotContain("lib", monorepo.Storage.Names);
		var revision = await unwinder.ApplyAsync("Add app and unwind it");

		Assert.NotNull(revision);
		var registry = monorepo.Graph.Registry;
		Assert.Equal(libUrl, registry.Require("lib").Url);
		Assert.Equal(new ComponentReference("lib", "vendor/lib"), Assert.Single(registry.Require("app").References));

		// app's main moved to a commit on top of the old tip that no longer nests lib; both are linked by one tag.
		var app = monorepo.Storage["app"];
		var tip = (await app.BranchTipAsync("main"))!;
		Assert.Empty(await app.ListNestedAsync(tip));
		Assert.Equal(["main.c"], await FilesAsync(app, tip));
		var tag = Assert.Single(unwinder.Tags);
		Assert.Equal("unwind/app/0", tag.Name);
		Assert.Equal(tip, (await app.GetTagAsync(tag.Name))!.Commit);
		Assert.Equal(libCommit, (await monorepo.Storage["lib"].GetTagAsync(tag.Name))!.Commit);
	}

	[Fact]
	public async Task PlanUnwrap_ReusesARegisteredComponent_AndDryRunChangesNothing()
	{
		using var monorepo = new MemoryMonorepo();
		var (appUrl, libUrl, _) = Nesting(monorepo);
		await monorepo.Graph.ImportAsync("lib", libUrl, []);
		await monorepo.Graph.ImportAsync("app", appUrl, []);
		var before = (await monorepo.Storage["app"].BranchTipAsync("main"))!;

		using (var plan = await monorepo.Graph.PlanUnwrapAsync("app"))
		{
			var pin = Assert.Single(plan.Unwinding.SelectMany(context => context.Pins));
			Assert.False(pin.Added);
			Assert.Equal("lib", pin.Component.Name);
		}

		// Planned but not applied: nothing changed.
		Assert.Equal(before, await monorepo.Storage["app"].BranchTipAsync("main"));
		Assert.Empty(monorepo.Graph.Registry.Require("app").References);
	}

	[Fact]
	public async Task PlanUnwrap_NestedUnitWithoutUrl_Fails_AndAFailedImportLeavesNothing()
	{
		using var monorepo = new MemoryMonorepo();
		var url = monorepo.Upstream("app", ("main.c", "app"));
		monorepo.Upstreams.At(url).Commit("main", "Nest something unknown", nested: [new NestedUnit("vendor/x", new string('a', 40), "x", null)]);

		var error = await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.ImportUnwrappedAsync("app", url, []));

		Assert.Contains("gives no URL for", error.Message);
		Assert.Empty(monorepo.Storage.Names);
		Assert.Empty(monorepo.Graph.Registry.Components);
	}

	[Fact]
	public async Task PlanUnwrap_UnwrappingThatWouldMakeACycle_Fails()
	{
		using var monorepo = new MemoryMonorepo();
		var (appUrl, libUrl, _) = Nesting(monorepo);
		await monorepo.Graph.ImportAsync("app", appUrl, []);
		await monorepo.Graph.ImportAsync("lib", libUrl, Refs("app"));

		var error = await Assert.ThrowsAsync<MonorepoException>(() => monorepo.Graph.PlanUnwrapAsync("app"));

		Assert.Contains("cyclic", error.Message);
	}
}
