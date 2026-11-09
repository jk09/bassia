namespace Bassia.Tests;

using System.CommandLine;
using Bassia.Cli;

/// <summary>
/// The declared command line: <see cref="CommandTable.Root"/> is a <c>System.CommandLine</c> tree, so its structure can
/// be checked directly, and the normalizer keeps the grammar bassia has always accepted in front of the library.
/// </summary>
public sealed class CommandLineTests
{
	private static IEnumerable<BassiaCommand> Leaves => CommandTable.Commands;

	[Fact]
	public void TheTree_NestsSubcommandsUnderGroups_AndEveryLeafRuns()
	{
		var top = CommandTable.Root.Subcommands.OfType<BassiaCommand>().Select(command => command.Name).ToList();
		Assert.Equal(top.Distinct(StringComparer.Ordinal).Count(), top.Count);
		Assert.Contains("workspace", top);
		Assert.DoesNotContain("create", top);

		var workspace = CommandTable.Find("workspace")!;
		Assert.True(workspace.IsGroup);
		Assert.Null(workspace.Handler);
		Assert.Contains("create", workspace.Nested.Select(child => child.Name));
		Assert.Equal("workspace create", workspace.FindChild("create")!.FullName);

		Assert.All(Leaves, leaf =>
		{
			Assert.NotNull(leaf.Handler);
			Assert.False(string.IsNullOrWhiteSpace(leaf.Summary));
			Assert.NotEmpty(leaf.Examples);
		});
		Assert.Equal(Leaves.Select(leaf => leaf.FullName).Distinct().Count(), Leaves.Count());
	}

	[Fact]
	public void EveryCommand_DeclaresItsSwitchesConsistently()
	{
		Assert.All(Leaves, leaf =>
		{
			var names = leaf.Options.Select(option => option.Name).ToList();
			Assert.Equal(names.Distinct().Count(), names.Count);
			Assert.All(names, name => Assert.Matches("^-[a-z][a-z-]*$", name));
			Assert.True(leaf.Options.Count(option => option.IsRest()) <= 1, $"{leaf.FullName} has two rest-of-line switches");

			// The bare argument stands for one of the command's own switches.
			if (leaf.Positional is { } positional)
			{
				Assert.NotNull(leaf.FindSwitch(positional));
				Assert.NotNull(leaf.PositionalArgument);
			}

			// Documented switches say what value they take.
			Assert.All(leaf.VisibleSwitches.Where(option => !option.IsFlag()), option => Assert.False(string.IsNullOrEmpty(option.HelpName), $"{leaf.FullName} {option.Name}"));
		});
	}

	[Fact]
	public void TheGlobalSwitches_AreOnTheRoot_AndTheLibrarysOwnAreGone()
	{
		var names = CommandTable.Root.Options.Select(option => option.Name).ToList();
		Assert.Equal(["-human", "-help", "-C"], names);
		Assert.Contains("-h", CommandTable.HelpRequested.Aliases);
		Assert.All(new Option[] { CommandTable.Human, CommandTable.HelpRequested, CommandTable.WorkingDirectory }, option => Assert.True(option.Recursive));
		Assert.Empty(CommandTable.Root.Directives);
	}

	[Fact]
	public void ReadOnlyAndInteractiveCommands_AreDeclaredCommands()
	{
		var names = Leaves.Select(leaf => leaf.FullName).ToHashSet();
		Assert.Subset(names, CommandCatalog.ReadOnlyCommands.ToHashSet());
		Assert.Subset(names, CommandCatalog.InteractiveCommands.ToHashSet());
	}

	[Theory]
	[InlineData("WORKSPACE Create --NAME x -Human", "workspace create -name x -human")]
	[InlineData("workspace create -h", "workspace create -help")]
	[InlineData("-C /tmp -c ../x status", "-C /tmp -C ../x status")]
	[InlineData("config get -key -x", "config get -key -x")]
	[InlineData("mainline show -seq -1", "mainline show -seq -1")]
	[InlineData("workspace create fix --context @api", "workspace create fix -context @api")]
	public void Normalizing_CanonicalizesCommandsAndSwitches(string line, string expected)
	{
		var normalized = CommandLineNormalizer.Normalize(CommandTable.Root, line.Split(' '));
		Assert.Equal(expected.Split(' '), normalized.Tokens);
		Assert.Null(normalized.Rest);
	}

	[Fact]
	public void Normalizing_CutsRestOfLineText_OffVerbatim()
	{
		var run = CommandLineNormalizer.Normalize(CommandTable.Root, ["agent", "start", "-NAME", "w", "--run", "echo", "-land", "--human", "&&", "a b"]);
		Assert.Equal(["agent", "start", "-name", "w"], run.Tokens);
		Assert.Equal(new RestOfLine("run", Invocation.JoinRest(["echo", "-land", "--human", "&&", "a b"])), run.Rest);

		var message = CommandLineNormalizer.Normalize(CommandTable.Root, ["feedback", "send", "-yes", "land", "is", "-slow"]);
		Assert.Equal(["feedback", "send", "-yes"], message.Tokens);
		Assert.Equal(new RestOfLine("message", Invocation.JoinRest(["land", "is", "-slow"])), message.Rest);

		var empty = CommandLineNormalizer.Normalize(CommandTable.Root, ["agent", "start", "-run"]);
		Assert.Equal(new RestOfLine("run", ""), empty.Rest);
	}

	[Fact]
	public void Normalizing_ReportsSwitchesNoCommandDeclares()
	{
		var normalized = CommandLineNormalizer.Normalize(CommandTable.Root, ["workspace", "show", "-frob", "--other", "-workspace", "-x"]);
		Assert.Equal(["-frob", "--other"], normalized.UnknownSwitches);
	}

	[Theory]
	[InlineData("workspace create -path")]
	[InlineData("workspace create -frob x")]
	[InlineData("workspace create")]
	[InlineData("workspace show -workspace a b")]
	[InlineData("config get a -key b")]
	[InlineData("mainline show -seq")]
	[InlineData("agent start -run")]
	[InlineData("feedback send")]
	[InlineData("workspace frobnicate")]
	[InlineData("component list")]
	[InlineData("nosuch")]
	public async Task ACommandLineThatDoesNotParse_ExitsWithTwo_AndAnErrorOnStderr(string line)
	{
		var (exit, output, error) = await TestEnvironment.RunAsync(line.Split(' '));
		Assert.Equal(2, exit);
		Assert.Equal("", output);
		Assert.Contains("ok = false", error);
	}

	[Theory]
	[InlineData("workspace create -help", "Open a workspace over the monorepo")]
	[InlineData("workspace create --HELP", "Open a workspace over the monorepo")]
	[InlineData("workspace -h", "has the subcommands create")]
	[InlineData("workspace", "has the subcommands create")]
	[InlineData("workspace help", "has the subcommands create")]
	[InlineData("help workspace land", "Land the workspace on mainline")]
	public async Task Help_IsGeneratedFromTheTree(string line, string expected)
	{
		var (exit, output, _) = await TestEnvironment.RunAsync(line.Split(' '));
		Assert.Equal(0, exit);
		Assert.Contains(expected, output);
	}

	[Fact]
	public async Task HelpAll_ListsEveryDeclaredCommand_WithItsUsageLine()
	{
		var (exit, output, _) = await TestEnvironment.RunAsync("help", "-all");
		Assert.Equal(0, exit);
		var described = ((Tomlyn.Model.TomlTableArray)TestEnvironment.Parse(output)["commands"]).ToDictionary(command => (string)command["name"], command => (string)command["usage"]);
		Assert.Equal(Leaves.ToDictionary(leaf => leaf.FullName, leaf => leaf.UsageLine), described);
	}

	[Fact]
	public void TheUsageLine_ListsRequiredSwitchesFirst_ThenOptionalOnes_ThenTheRestOfLine()
	{
		Assert.Equal("bassia workspace create -name <name> [-path <dir>] [-on <workspace>] [-intent <text>] [-context <rule,...>] [-materialize <full|lazy>]",
			CommandTable.Find("workspace")!.FindChild("create")!.UsageLine);
		Assert.Equal("bassia agent show -id <run-id>", CommandTable.Find("agent")!.FindChild("show")!.UsageLine);
		Assert.Equal("bassia feedback send [-kind <bug|idea|question>] [-yes] <message...>", CommandTable.Find("feedback")!.FindChild("send")!.UsageLine);
		Assert.EndsWith("[-run <command...>]", CommandTable.Find("agent")!.FindChild("start")!.UsageLine);
	}
}
