using Bassia.Cli;

namespace Bassia.Tests;

/// <summary><c>-human</c>: the same results printed for a person instead of as TOML.</summary>
public class HumanOutputTests
{
	[Fact]
	public void Render_ShowsTheMessageThenScalarsListsAndNestedSections()
	{
		var text = HumanOutput.Render(new Dictionary<string, object?>
		{
			["ok"] = true,
			["command"] = "demo",
			["message"] = "All good.",
			["count"] = 3,
			["flag"] = true,
			["skipped"] = null,
			["tags"] = new List<string> { "a", "b" },
			["nested"] = new Dictionary<string, object?> { ["inner"] = "x" }
		});

		Assert.Equal("All good.\ncount: 3\nflag: true\ntags:\n  - a\n  - b\nnested:\n  inner: x", text);
	}

	[Fact]
	public void Render_OfAFailure_StartsWithError()
	{
		var text = HumanOutput.Render(new Dictionary<string, object?> { ["ok"] = false, ["command"] = "demo", ["error"] = "It broke." });

		Assert.Equal("error: It broke.", text);
	}

	[Fact]
	public void Render_DrawsShortEntriesAsATableAndLongOnesAsBlocks()
	{
		var shortEntries = HumanOutput.Render(new Dictionary<string, object?>
		{
			["message"] = "m",
			["entry"] = new List<Dictionary<string, object?>> { new() { ["name"] = "app", ["runs"] = 2 }, new() { ["name"] = "lib", ["runs"] = 0 } }
		});
		var longEntries = HumanOutput.Render(new Dictionary<string, object?>
		{
			["message"] = "m",
			["entry"] = new List<Dictionary<string, object?>> { new() { ["name"] = "-url", ["description"] = new string('d', 80) } }
		});

		Assert.Equal("m\nentry:\n  name  runs\n  ----  ----\n  app   2\n  lib   0", shortEntries);
		Assert.Equal($"m\nentry:\n  - name: -url\n    description: {new string('d', 80)}", longEntries);
	}

	[Fact]
	public void Render_PrintsMultiLineTextAsDrawnAndLetsItsTableReplaceTheEntries()
	{
		var text = HumanOutput.Render(new Dictionary<string, object?>
		{
			["message"] = "m",
			["table"] = new TomlText("A  B\n-  -\n1  2"),
			["component"] = new List<Dictionary<string, object?>> { new() { ["name"] = "app" } }
		});

		Assert.Equal("m\n\nA  B\n-  -\n1  2", text);
		Assert.DoesNotContain("component", text);
	}

	[Fact]
	public async Task Human_PrintsPlainTextWithoutTheMarkerAndKeepsTheExitCode()
	{
		var (exitCode, output, error) = await TestEnvironment.RunAsync("version", "-human");

		Assert.Equal(0, exitCode);
		Assert.DoesNotContain(TomlResult.Marker, output);
		Assert.DoesNotContain("ok = ", output);
		Assert.Empty(error);
		Assert.False(string.IsNullOrWhiteSpace(output));
	}

	[Fact]
	public async Task Human_WithoutTheSwitch_StaysToml()
	{
		var (_, output, _) = await TestEnvironment.RunAsync("version");

		Assert.StartsWith(TomlResult.Marker, output);
	}

	[Theory]
	[InlineData("-help", "-human")]
	[InlineData("-human", "-help")]
	[InlineData("help", "-human")]
	[InlineData("component", "-help", "-human")]
	[InlineData("component", "add", "--HUMAN", "-help")]
	public async Task Human_WithHelp_PrintsReadableHelp(params string[] args)
	{
		var (exitCode, output, error) = await TestEnvironment.RunAsync(args);

		Assert.True(exitCode == 0, error);
		// The overview explains the marker in prose, so only a result that opens with it is TOML.
		Assert.DoesNotContain(TomlResult.Marker + "\n", output);
		Assert.DoesNotContain("ok = ", output);
		Assert.Contains("bassia", output);
	}

	[Fact]
	public async Task Human_ForAMissingSwitchOrUnknownCommand_IsAnErrorOnStderrWithExitCode2()
	{
		var (missingExit, missingOut, missingError) = await TestEnvironment.RunAsync("component", "add", "-human");
		var (unknownExit, _, unknownError) = await TestEnvironment.RunAsync("nosuch", "-human");

		Assert.Equal(2, missingExit);
		Assert.Empty(missingOut);
		Assert.StartsWith("error: -url is required.", missingError);
		Assert.Contains("Usage: bassia component add", missingError);
		Assert.Equal(2, unknownExit);
		Assert.StartsWith("error: Unknown command 'nosuch'", unknownError);
	}
}
