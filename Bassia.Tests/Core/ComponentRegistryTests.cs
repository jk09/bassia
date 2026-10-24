namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Storage;
using Bassia.Storage.Memory;
using static MemoryMonorepo;

/// <summary>The component graph the meta-repo maintains, and the meta-repo persisting it, with no storage behind either.</summary>
public class ComponentRegistryTests
{
	private static ComponentDefinition Component(string name, params string[] references) => new(name, "", Refs(references));

	private static ComponentRegistry Registry(params ComponentDefinition[] components) =>
		components.Aggregate(ComponentRegistry.Empty, (registry, component) => registry.Add(component));

	[Fact]
	public void Add_RegistersInOrder_AndLeavesTheOriginalUnchanged()
	{
		var empty = ComponentRegistry.Empty;
		var registry = empty.Add(Component("lib")).Add(Component("app", "lib"));

		Assert.Empty(empty.Components);
		Assert.Equal(["lib", "app"], registry.Components.Select(component => component.Name));
		Assert.Equal("app", Assert.Single(registry.ReferrersOf("lib")).Name);
	}

	[Theory]
	[InlineData("")]
	[InlineData(".hidden")]
	[InlineData("a b")]
	[InlineData("a/b")]
	[InlineData("a:b")]
	public void Add_InvalidName_Throws(string name)
	{
		var error = Assert.Throws<MonorepoException>(() => ComponentRegistry.Empty.Add(Component(name)));
		Assert.Contains("is not a valid component name", error.Message);
	}

	[Fact]
	public void Add_DuplicateOrDanglingOrSelfReference_Throws()
	{
		var registry = Registry(Component("lib"));

		Assert.Contains("already registered", Assert.Throws<MonorepoException>(() => registry.Add(Component("lib"))).Message);
		Assert.Contains("'missing', which is not registered", Assert.Throws<MonorepoException>(() => registry.Add(Component("app", "missing"))).Message);
		Assert.Contains("cannot reference itself", Assert.Throws<MonorepoException>(() => registry.Add(Component("app", "app"))).Message);
	}

	[Theory]
	[InlineData("../outside")]
	[InlineData("/abs")]
	[InlineData("a//b")]
	[InlineData("a/./b")]
	public void Add_ReferencePathOutsideTheComponent_Throws(string path)
	{
		var registry = Registry(Component("lib"));

		var error = Assert.Throws<MonorepoException>(() => registry.Add(new ComponentDefinition("app", "", [new ComponentReference("lib", path)])));

		Assert.Contains("must be a relative path inside the component", error.Message);
	}

	[Fact]
	public void Add_TwoComponentsAtOnePath_Throws()
	{
		var registry = Registry(Component("lib"), Component("ui"));

		var error = Assert.Throws<MonorepoException>(() => registry.Add(new ComponentDefinition("app", "", [new("lib", "vendor"), new("ui", "vendor")])));

		Assert.Contains("more than one component at 'vendor'", error.Message);
	}

	[Fact]
	public void SetReferences_ThatWouldCloseACycle_Throws()
	{
		var registry = Registry(Component("lib"), Component("app", "lib"), Component("tool", "app"));

		var error = Assert.Throws<MonorepoException>(() => registry.SetReferences("lib", Refs("tool")));

		Assert.Contains("cyclic component reference", error.Message);
		Assert.Empty(registry.Require("lib").References);
	}

	[Fact]
	public void Remove_StillReferenced_Throws_ElseUnregisters()
	{
		var registry = Registry(Component("lib"), Component("app", "lib"));

		Assert.Contains("is referenced by app", Assert.Throws<MonorepoException>(() => registry.Remove("lib")).Message);
		Assert.Equal(["lib"], registry.Remove("app").Components.Select(component => component.Name));
	}

	[Fact]
	public void Closure_IsInDependencyOrder_AndDependencyOrderIsItsReverse()
	{
		var registry = Registry(Component("base"), Component("lib", "base"), Component("ui", "base"), Component("app", "lib", "ui"), Component("other"));

		var closure = registry.Closure(["app"]).Select(component => component.Name).ToList();

		Assert.Equal(4, closure.Count);
		Assert.Equal("app", closure[0]);
		Assert.Equal("base", closure[^1]);
		var order = registry.DependencyOrder().Select(component => component.Name).ToList();
		Assert.True(order.IndexOf("base") < order.IndexOf("lib") && order.IndexOf("lib") < order.IndexOf("app"));
		Assert.Contains("other", order);
	}

	[Fact]
	public void FromDefinitions_KeepsAHandEditedCycle_ButClosureReportsIt()
	{
		var registry = ComponentRegistry.FromDefinitions([Component("a", "b"), Component("b", "a")]);

		var error = Assert.Throws<MonorepoException>(() => registry.Closure(["a"]));

		Assert.Contains("a -> b -> a", error.Message);
	}

	[Fact]
	public void FromDefinitions_DuplicateName_Throws()
	{
		var error = Assert.Throws<MonorepoException>(() => ComponentRegistry.FromDefinitions([Component("a"), Component("a")]));

		Assert.Contains("more than once", error.Message);
	}

	// ----- the meta-repo -----

	[Fact]
	public async Task MetaRepo_CommitsChangesAsOneRevision_AndReopensWithThem()
	{
		var store = new MemoryMetaStore();
		var metaRepo = await MetaRepo.OpenAsync(store);

		var revision = await metaRepo.CommitAsync(
			[new RegistryChange.Add(Component("lib")), new RegistryChange.Add(Component("app", "lib"))], "Add lib and app");

		Assert.Equal("r1", revision);
		var (_, message, changes) = Assert.Single(store.Revisions);
		Assert.Equal("Add lib and app", message);
		Assert.Equal(2, changes.Count);
		var reopened = await MetaRepo.OpenAsync(store);
		Assert.Equal(["lib", "app"], reopened.Registry.Components.Select(component => component.Name));
	}

	[Fact]
	public async Task MetaRepo_ChangeThatDoesNotFit_PersistsNothing()
	{
		var store = new MemoryMetaStore();
		var metaRepo = await MetaRepo.OpenAsync(store);
		await metaRepo.CommitAsync(new RegistryChange.Add(Component("lib")), "Add lib");

		// The second change is invalid, so the first of the batch must not be persisted either.
		await Assert.ThrowsAsync<MonorepoException>(() => metaRepo.CommitAsync(
			[new RegistryChange.Add(Component("app", "lib")), new RegistryChange.Remove("lib")], "Add app, remove lib"));

		Assert.Single(store.Revisions);
		Assert.Equal(["lib"], metaRepo.Registry.Components.Select(component => component.Name));
		Assert.Equal(["lib"], (await store.LoadAsync()).Select(component => component.Name));
	}
}
