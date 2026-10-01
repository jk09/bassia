using Bassia.Git;
using Bassia.Integration;

namespace Bassia.Tests;

/// <summary>
/// The structural merge tier between git and the resolver. Most tests stand in for weave with a deterministic driver
/// built from git itself (<c>git merge-file</c>), so they run wherever git does; <see cref="WeaveFactAttribute"/>
/// tests use the real <c>weave-driver</c> and run only where it is installed.
/// </summary>
public class StructuralMergeTests
{
	/// <summary>Two functions; each run below appends its own, so git sees both runs change the end of the file.</summary>
	private const string BaseLib = "def a():\n    return 1\n";

	private static readonly Dictionary<string, string> LibFiles = new() { ["lib.py"] = BaseLib };

	/// <summary>A driver that always merges cleanly: git's union merge, which keeps both sides' lines.</summary>
	private const string UnionDriver = "git merge-file --union %A %O %B";

	/// <summary>A driver that merges cleanly but warns, like weave on a merge that may not mean what both sides meant.</summary>
	private const string WarningDriver = "git merge-file --union %A %O %B && echo 'weave-warning: {\"kind\":\"dependency_also_modified\",\"entity\":\"a\"}' >&2";

	/// <summary>A driver that conflicts wherever git's line merge does.</summary>
	private const string ConflictDriver = "git merge-file %A %O %B";

	private static IntegrationStore Store(MonorepoFixture fixture) =>
		new(new RunMetadataStore(new GitClient(fixture.Root), fixture.RunsRepo));

	private static async Task<IntegrationRecord> SingleIntegrationAsync(MonorepoFixture fixture) =>
		Assert.Single(await Store(fixture).ListLatestAsync());

	private static string Tag(IntegrationRecord record) => IntegrationRecord.TagName(record.IntegrationId, 0);

	/// <summary>Two runs over <c>lib.py</c>, each appending a function: git conflicts at the end of the file.</summary>
	private static async Task<(MonorepoFixture Fixture, string First, string Second)> TwoAppendingRunsAsync()
	{
		var fixture = await MonorepoFixture.CreateAsync();
		await fixture.AddComponentAsync("example", files: LibFiles);
		var first = await fixture.RunReplacingAsync("example", "lib.py", BaseLib + "\n\ndef b():\n    return 2\n");
		var second = await fixture.RunReplacingAsync("example", "lib.py", BaseLib + "\n\ndef c():\n    return 3\n");
		return (fixture, first, second);
	}

	[Fact]
	public async Task Plan_GitConflictTheStructuralMergeResolves_IsAStructuralStepChainedBeforeTheResolver()
	{
		var (fixture, first, second) = await TwoAppendingRunsAsync();
		await using var _ = fixture;

		var (exitCode, output, error) = await fixture.BassiaAsync("integration", "plan", "-runs", "all", "-weave", UnionDriver);

		Assert.True(exitCode == 0, error);
		Assert.Contains("1 step(s) for git, 1 for the structural merge, 0 for the resolver", output);
		Assert.Contains($"structural_merge = \"{UnionDriver}\"", output);
		Assert.Contains("STRUCT", output);
		Assert.Contains("weave: clean", output);
		Assert.Contains("triage = \"conflict\"", output);
		Assert.Contains("strategy = \"structural\"", output);
		Assert.DoesNotContain("conflicts_with = [\"" + first, output);
		Assert.Contains(RunMetadata.Key(second), output);
	}

	[Fact]
	public async Task Start_GitConflictTheStructuralMergeResolves_IsMergedWithoutTheResolver()
	{
		var (fixture, first, second) = await TwoAppendingRunsAsync();
		await using var _ = fixture;

		// A resolver that fails proves git and the structural merge did everything on their own.
		var (exitCode, output, error) = await fixture.IntegrationStartAsync("-runs", "all", "-weave", UnionDriver, "-resolve", TestEnvironment.FailingCommand);

		Assert.True(exitCode == 0, error);
		Assert.Contains("1 result(s) merged by git, 1 merged structurally by weave, 0 resolved semantically", output);
		var record = await SingleIntegrationAsync(fixture);
		Assert.Equal(UnionDriver, record.StructuralDriver);
		var steps = Assert.Single(record.Components).Steps;
		Assert.Equal([first, second], steps.Select(step => step.RunId));
		Assert.Equal((MergeStrategy.Syntactic, StepOutcome.Merged), (steps[0].Strategy, steps[0].Outcome));
		Assert.Equal((MergeStrategy.Structural, StepOutcome.Merged), (steps[1].Strategy, steps[1].Outcome));
		Assert.Equal((Triage.Conflict, StructuralTriage.Clean), (steps[1].Triage, steps[1].Structural));
		Assert.Equal(["lib.py"], steps[1].Conflicts);
		Assert.Empty(steps[1].StructuralConflicts);

		var source = fixture.SourceRepo("example");
		var lib = (await TestEnvironment.GitAsync(source, "show", $"{Tag(record)}:lib.py")).Replace("\r", "");
		Assert.Contains("def b():", lib);
		Assert.Contains("def c():", lib);
		var message = await TestEnvironment.GitAsync(source, "log", "-1", "--format=%B", Tag(record));
		Assert.Contains("strategy = \"structural\"", message);
		Assert.Contains("conflicts = [\"lib.py\"]", message);
		Assert.Contains("structural_driver = ", message);

		// Nothing in the component repo was set up for the driver: it came with -c for the one command.
		Assert.False(File.Exists(Path.Combine(source, ".git", "info", "attributes")));
		Assert.DoesNotContain("weave", await File.ReadAllTextAsync(Path.Combine(source, ".git", "config")));
	}

	[Fact]
	public async Task Start_StructuralMergeWithWarnings_GoesToTheResolverForReviewWithTheWarningsInTheBrief()
	{
		var (fixture, _, second) = await TwoAppendingRunsAsync();
		await using var _ = fixture;
		var stdin = Path.Combine(fixture.Root, "resolver-stdin.md");

		var (exitCode, output, error) = await fixture.IntegrationStartAsync("-runs", "all", "-weave", WarningDriver,
			"-resolve", TestEnvironment.CaptureStdinCommand(stdin));

		Assert.True(exitCode == 0, error);
		Assert.Contains("1 resolved semantically", output);
		var steps = Assert.Single((await SingleIntegrationAsync(fixture)).Components).Steps;
		var reviewed = Assert.Single(steps, step => step.RunId == second);
		Assert.Equal((MergeStrategy.Semantic, StepOutcome.Resolved), (reviewed.Strategy, reviewed.Outcome));
		Assert.False(reviewed.Overridden);
		Assert.Equal(StructuralTriage.Warnings, reviewed.Structural);
		Assert.Contains(reviewed.StructuralWarnings, warning => warning.Contains("dependency_also_modified"));

		var brief = (await File.ReadAllTextAsync(stdin)).Replace("\r", "");
		Assert.Contains("The merge was started with weave", brief);
		Assert.Contains("Weave merged these of them without a conflict", brief);
		Assert.Contains("dependency_also_modified", brief);
		Assert.Contains("semantic review", brief);
	}

	[Fact]
	public async Task Start_ConflictTheStructuralMergeCannotResolve_GoesToTheResolverWithWhatIsLeft()
	{
		var (fixture, _, second) = await TwoAppendingRunsAsync();
		await using var _ = fixture;
		var stdin = Path.Combine(fixture.Root, "resolver-stdin.md");
		var resolver = $"{TestEnvironment.CaptureStdinCommand(stdin)} && {TestEnvironment.WriteFileCommand("lib.py", "merged")}";

		var (planExit, plan, planError) = await fixture.BassiaAsync("integration", "plan", "-runs", "all", "-weave", ConflictDriver);
		var (exitCode, output, error) = await fixture.IntegrationStartAsync("-runs", "all", "-weave", ConflictDriver, "-resolve", resolver);

		Assert.True(planExit == 0, planError);
		Assert.Contains("weave: conflict [lib.py]", plan);
		Assert.True(exitCode == 0, error);
		Assert.Contains("1 resolved semantically", output);
		var reviewed = Assert.Single(Assert.Single((await SingleIntegrationAsync(fixture)).Components).Steps, step => step.RunId == second);
		Assert.Equal((MergeStrategy.Semantic, StepOutcome.Resolved), (reviewed.Strategy, reviewed.Outcome));
		Assert.Equal(StructuralTriage.Conflict, reviewed.Structural);
		Assert.Equal(["lib.py"], reviewed.Conflicts);
		Assert.Equal(["lib.py"], reviewed.StructuralConflicts);

		var brief = (await File.ReadAllTextAsync(stdin)).Replace("\r", "");
		Assert.Contains("These files still conflict", brief);
		Assert.Contains("refused_by:", brief);
	}

	[Fact]
	public async Task Plan_StructuralMergeOffOrMissing_LeavesEveryConflictToTheResolverAsBefore()
	{
		var (fixture, _, _) = await TwoAppendingRunsAsync();
		await using var _ = fixture;

		var (offExit, off, offError) = await fixture.BassiaAsync("integration", "plan", "-runs", "all");
		var (missingExit, missing, missingError) = await fixture.BassiaAsync("integration", "plan", "-runs", "all", "-weave", "no-such-weave-driver-3f2a91");

		Assert.True(offExit == 0, offError);
		Assert.Contains("0 for the structural merge, 1 for the resolver", off);
		Assert.Contains("structural_merge = \"off\"", off);
		Assert.DoesNotContain("weave:", off);
		Assert.True(missingExit == 0, missingError);
		Assert.Contains("1 for the resolver", missing);
		Assert.Contains("unavailable: 'no-such-weave-driver-3f2a91' was not found", missing);
	}

	[Fact]
	public async Task Config_Weave_DefaultsToWeaveDriverAndCanBeSet()
	{
		await using var fixture = await MonorepoFixture.CreateAsync();

		var (exitCode, _, error) = await fixture.BassiaAsync("config", "set", "-key", "integration.weave", "-value", "/opt/weave/weave-driver");

		Assert.True(exitCode == 0, error);
		Assert.Equal("/opt/weave/weave-driver", Monorepo.Load(fixture.Root).StructuralDriver);
		Assert.Equal("weave-driver", Monorepo.DefaultStructuralDriver);
		var init = await File.ReadAllTextAsync(ConfigFile.PathOf(fixture.Root));
		Assert.Contains("weave = ", init);
	}

	[Fact]
	public void Record_StructuralFields_RoundTripAndOldRecordsStillLoad()
	{
		var record = new IntegrationRecord
		{
			IntegrationId = IntegrationRecord.NewId(),
			Status = "completed",
			Created = RunMetadata.Timestamp(),
			Resolver = "r",
			StructuralDriver = "weave-driver",
			WorkspacePath = "/tmp/w"
		};
		var component = new ComponentIntegration { Name = "app", BaseRef = "main", BaseCommit = "abc" };
		var step = new IntegrationStep
		{
			RunId = "agent-run-1", Rationale = "x", SourceTag = "agent-run/1/0", SourceCommit = "def",
			Triage = Triage.Conflict, Strategy = MergeStrategy.Semantic, Structural = StructuralTriage.Warnings
		};
		step.Conflicts.Add("a.py");
		step.StructuralWarnings.Add("{\"kind\":\"parse_failed_after_merge\"}");
		component.Steps.Add(step);
		component.Steps.Add(new IntegrationStep { RunId = "agent-run-2", Rationale = "y", SourceTag = "t", SourceCommit = "c", Strategy = MergeStrategy.Structural });
		record.Components.Add(component);

		var toml = record.ToToml();
		var copy = IntegrationRecord.FromToml(toml);

		Assert.Equal(toml, copy.ToToml());
		Assert.Equal("weave-driver", copy.StructuralDriver);
		var steps = Assert.Single(copy.Components).Steps;
		Assert.Equal(StructuralTriage.Warnings, steps[0].Structural);
		Assert.Equal(["{\"kind\":\"parse_failed_after_merge\"}"], steps[0].StructuralWarnings);
		Assert.Equal(MergeStrategy.Structural, steps[1].Strategy);
		Assert.Null(steps[1].Structural);

		// A record written before the structural merge existed has none of its keys.
		var old = IntegrationRecord.FromToml(toml.Replace("structural_driver = \"weave-driver\"\n", "")
			.Replace("structural = \"warnings\"\n", "").Replace("structural_conflicts = []\n", "")
			.Replace("structural_warnings = [\"{\\\"kind\\\":\\\"parse_failed_after_merge\\\"}\"]\n", ""));
		Assert.Null(old.StructuralDriver);
		Assert.Null(Assert.Single(old.Components).Steps[0].Structural);
		Assert.Empty(old.Components[0].Steps[0].StructuralWarnings);
	}

	[Theory]
	[InlineData("weave-driver", "weave-driver")]
	[InlineData("weave-driver --audit", "weave-driver")]
	[InlineData("\"C:\\Program Files\\weave\\weave-driver.exe\" %O %A %B", "C:\\Program Files\\weave\\weave-driver.exe")]
	public void Executable_IsTheFirstWordOrQuotedString(string command, string executable) =>
		Assert.Equal(executable, StructuralMerge.Executable(command));

	[Fact]
	public void Warnings_AreTheWeaveWarningPayloadsOfStderr() =>
		Assert.Equal(["{\"a\":1}", "{\"b\":2}"],
			StructuralMerge.Warnings("weave: 2 entities auto-resolved\nweave-warning: {\"a\":1}\r\nAuto-merging x\nweave-warning: {\"b\":2}\nweave-warning: {\"a\":1}\n"));

	// ----- with the real weave -----

	[WeaveFact]
	public async Task Weave_TwoRunsAddingDifferentFunctions_MergeStructurallyAndTwoRunsChangingTheSameFunctionGoToTheResolver()
	{
		var (fixture, _, second) = await TwoAppendingRunsAsync();
		await using var _ = fixture;
		var third = await fixture.RunReplacingAsync("example", "lib.py", "def a():\n    return 100\n");
		var fourth = await fixture.RunReplacingAsync("example", "lib.py", "def a():\n    return 200\n");
		var stdin = Path.Combine(fixture.Root, "resolver-stdin.md");
		var seen = Path.Combine(fixture.Root, "resolver-lib.py");
		var keep = OperatingSystem.IsWindows() ? $"copy /y lib.py \"{seen}\" > nul" : $"cp lib.py '{seen}'";
		var resolver = $"{TestEnvironment.CaptureStdinCommand(stdin)} && {keep} && {TestEnvironment.WriteFileCommand("lib.py", "merged")}";

		var (exitCode, output, error) = await fixture.IntegrationStartAsync("-runs", "all", "-weave", "weave-driver", "-resolve", resolver);

		Assert.True(exitCode == 0, error);
		var steps = Assert.Single((await SingleIntegrationAsync(fixture)).Components).Steps;
		Assert.Equal(MergeStrategy.Structural, Assert.Single(steps, step => step.RunId == second).Strategy);
		var collision = Assert.Single(steps, step => step.RunId == fourth);
		Assert.Equal((MergeStrategy.Semantic, StructuralTriage.Conflict), (collision.Strategy, collision.Structural));
		Assert.Contains("The merge was started with weave", await File.ReadAllTextAsync(stdin));

		// The resolver faced weave's entity-labelled conflict, not git's line conflict.
		var conflicted = await File.ReadAllTextAsync(seen);
		Assert.Contains("function `a`", conflicted);
		Assert.Contains("refused_by:", conflicted);
		Assert.Contains(third, steps.Select(step => step.RunId));
		Assert.Contains("merged structurally by weave", output);
	}
}

/// <summary>A fact that needs weave's <c>weave-driver</c> on <c>PATH</c>, and is skipped where it is not installed.</summary>
public sealed class WeaveFactAttribute : FactAttribute
{
	public WeaveFactAttribute()
	{
		if (!StructuralMerge.IsInstalled(Monorepo.DefaultStructuralDriver))
		{
			Skip = "weave-driver is not installed (https://github.com/Ataraxy-Labs/weave).";
		}
	}
}
