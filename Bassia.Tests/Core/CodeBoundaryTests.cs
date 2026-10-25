namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Integration;
using Bassia.Storage;
using static MemoryMonorepo;

/// <summary>Dependencies in a component's code against its boundaries: the scanner, the checker, runs and the integration.</summary>
public class CodeBoundaryTests
{
	/// <summary>core; lib nesting core at vendor/core and exporting src/api; app nesting lib.</summary>
	private static ComponentRegistry Registry(IReadOnlyList<string>? libExports = null) => ComponentRegistry.FromDefinitions(
	[
		new ComponentDefinition("core", "", []),
		new ComponentDefinition("lib", "", Refs("core:vendor/core")) { Exports = libExports ?? ["src/api"] },
		new ComponentDefinition("app", "", Refs("lib", "core"))
	]);

	private static CodeScan Check(params (string Path, string Text)[] files) => CodeBoundaries.Check(Registry(), "app", files);

	[Theory]
	[InlineData("src/main.ts", "./util", "src/util")]
	[InlineData("src/main.ts", "../lib/src/api", "lib/src/api")]
	[InlineData("main.ts", "../other/x", "../other/x")]
	[InlineData("a/b/c.h", "..\\..\\lib\\x.h", "lib/x.h")]
	[InlineData("app.csproj", "lib/src/**/*.cs", "lib/src")]
	[InlineData("x.ts", "/abs/path", null)]
	public void Resolve_IsRelativeToTheFile_InTheComponentNamespace(string file, string specifier, string? expected) =>
		Assert.Equal(expected, CodeBoundaries.Resolve(file, specifier));

	[Fact]
	public void Scanner_FindsPathDependencies_PerFileKind()
	{
		Assert.Equal(["./a", "../b", "./c", "./d", "./e"], DependencyScanner.Find("x.ts", """
			import { a } from './a';
			import '../b';
			const c = require("./c");
			const d = await import('./d');
			export * from "./e";
			import React from 'react';
			""").Select(found => found.Specifier));
		Assert.Equal([(2, "lib/api.h")], DependencyScanner.Find("x.cpp", "#include <vector>\n#include \"lib/api.h\"\n"));
		Assert.Equal(["..\\lib\\Lib.csproj"], DependencyScanner.Find("app.csproj", "<ItemGroup><ProjectReference Include=\"..\\lib\\Lib.csproj\" /><PackageReference Include=\"X\" /></ItemGroup>").Select(found => found.Specifier));
		Assert.Equal(["lib"], DependencyScanner.Find("package.json", "{ \"dependencies\": { \"lib\": \"file:lib\", \"y\": \"^1.0\" } }").Select(found => found.Specifier));
		Assert.Equal(["lib", "cmake/x.cmake"], DependencyScanner.Find("CMakeLists.txt", "add_subdirectory(lib)\ninclude(CTest)\ninclude(cmake/x.cmake)\n").Select(found => found.Specifier));
		Assert.Empty(DependencyScanner.Find("Program.cs", "using Lib.Internal;"));
	}

	[Fact]
	public void Check_ExportedPathAndInternalCode_AreFine_AndUsedReferencesAreKnown()
	{
		var scan = Check(("src/main.ts", "import { api } from '../lib/src/api/client';\nimport { x } from './x';"), ("lib.ts", "export * from './lib';"));

		Assert.Empty(scan.Violations);
		Assert.Equal(["lib"], scan.UsedReferences);
		Assert.Equal(["core"], scan.UnusedReferences);
	}

	[Fact]
	public void Check_ScriptImportWithoutExtension_MatchesAFileExport()
	{
		var registry = Registry(["src/index.ts"]);

		Assert.Empty(CodeBoundaries.Check(registry, "app", [("main.ts", "import './lib/src/index';")]).Violations);
		Assert.Single(CodeBoundaries.Check(registry, "app", [("main.ts", "import './lib/src/other';")]).Violations);
	}

	[Fact]
	public void Check_PrivateIndirectAndOutside_AreViolations()
	{
		var scan = Check(("main.ts", """
			import { secret } from './lib/src/internal/secret';
			import { y } from './lib/vendor/core/y';
			import { z } from '../core/z';
			"""));

		Assert.Equal([CodeViolationKind.Private, CodeViolationKind.Indirect, CodeViolationKind.Outside], scan.Violations.Select(violation => violation.Kind));
		Assert.Equal([1, 2, 3], scan.Violations.Select(violation => violation.Line));
		Assert.Contains("uses 'src/internal/secret' of lib, which exports only src/api", scan.Violations[0].Message);
		Assert.Contains("reaches 'core' through lib; use app's own reference at 'core/' instead", scan.Violations[1].Message);
		Assert.Contains("leaves the component for 'core'; use its nested copy at 'core/' instead", scan.Violations[2].Message);
		Assert.All(scan.Violations, violation => Assert.Equal("app", violation.Component));
	}

	[Fact]
	public void AddedOver_IgnoresViolationsThatOnlyMoved()
	{
		var before = Check(("main.ts", "import './lib/src/internal/a';"));
		var after = Check(("main.ts", "\n\nimport './lib/src/internal/a';\nimport './lib/src/internal/b';"));

		Assert.Equal(["./lib/src/internal/b"], after.AddedOver(before).Select(violation => violation.Specifier));
	}

	// ----- runs and the integration -----

	/// <summary>core, lib (nesting core, exporting src/api) and app (nesting lib), each tagged v1.</summary>
	private static async Task<MemoryMonorepo> MonorepoAsync()
	{
		var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("core", null, ("y.ts", "export const y = 1;\n"));
		await monorepo.AddAsync("lib", Refs("core:vendor/core"), ("src/api/index.ts", "export const api = 1;\n"), ("src/internal/secret.ts", "export const s = 1;\n"));
		await monorepo.Graph.SetAttributesAsync("lib", setExports: true, exports: ["src/api"]);
		await monorepo.AddAsync("app", Refs("lib"), ("main.ts", "import { api } from './lib/src/api';\n"));
		return monorepo;
	}

	private static async Task<RunMetadata> RunWritingAsync(MemoryMonorepo monorepo, string content, List<AgentRunStep>? steps = null)
	{
		var outcome = await monorepo.Runs(metadata =>
		{
			File.WriteAllText(Path.Combine(metadata.WorkspacePath, "app", "main.ts"), content);
			return 0;
		}).StartAsync("app@v1,lib@v1,core@v1", "agent \"change main.ts\"", Quiet(steps));
		Assert.True(outcome.Ok, outcome.Message);
		return outcome.Metadata;
	}

	[Fact]
	public async Task Run_RecordsTheViolationsItsResultAdds_AndStillCompletes()
	{
		using var monorepo = await MonorepoAsync();
		var steps = new List<AgentRunStep>();

		var run = await RunWritingAsync(monorepo, "import { api } from './lib/src/api';\nimport { s } from './lib/src/internal/secret';\nimport { y } from './lib/vendor/core/y';\n", steps);

		var app = run.Components.Single(component => component.Name == "app");
		Assert.Equal(ResultStatus.Pushed, app.ResultStatus);
		Assert.Equal([CodeViolationKind.Private, CodeViolationKind.Indirect], app.BoundaryViolations.Select(violation => violation.Kind));
		Assert.Contains(steps, step => step.Message.Contains("boundary violation (private)"));
		var recorded = (await monorepo.Records.LoadLatestAsync(run.RunId))!.Components.Single(component => component.Name == "app");
		Assert.Equal(2, recorded.BoundaryViolations.Count);
		Assert.Equal("lib", recorded.BoundaryViolations[0].Target);
	}

	[Fact]
	public async Task Integration_RecordsAddedViolationsPerStep_AndManualBoundariesHoldsTheResultBack()
	{
		using var monorepo = await MonorepoAsync();
		var bad = await RunWritingAsync(monorepo, "import { s } from './lib/src/internal/secret';\n");
		IntegrationSettings Settings(MergePolicy policy) => new()
		{
			WorkspaceDir = monorepo.WorkspaceDir,
			MergePolicyFor = _ => policy,
			RunResolver = _ => throw new InvalidOperationException("no resolver expected")
		};

		var warned = Assert.Single(Assert.Single(await IntegrationPlanner.PlanAsync(monorepo.Graph, Settings(MergePolicy.Default), [bad])).Steps);
		var held = Assert.Single(Assert.Single(await IntegrationPlanner.PlanAsync(monorepo.Graph, Settings(MergePolicy.Default with { ManualBoundaries = true }), [bad])).Steps);

		Assert.Equal(MergeStrategy.Syntactic, warned.Strategy);
		Assert.Contains("uses 'src/internal/secret' of lib", Assert.Single(warned.BoundaryViolations));
		Assert.Equal(MergeStrategy.Manual, held.Strategy);
		Assert.Contains("merge.boundaries = manual", held.Note);
	}

	[Fact]
	public async Task Store_ReadFile_ReturnsContentOrNull()
	{
		using var monorepo = await MonorepoAsync();
		var store = monorepo.Graph.Store("core");
		var tip = (await store.BranchTipAsync("main"))!;

		Assert.Equal("export const y = 1;\n", System.Text.Encoding.UTF8.GetString((await store.ReadFileAsync(tip, "y.ts"))!));
		Assert.Null(await store.ReadFileAsync(tip, "missing.ts"));
	}
}
