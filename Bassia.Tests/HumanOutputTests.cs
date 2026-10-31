using Bassia.Cli;

namespace Bassia.Tests;

/// <summary><c>-human</c>: the same results printed as Markdown for a person instead of as TOML.</summary>
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

		Assert.Equal("All good.\n\n- **count:** 3\n- **flag:** true\n\n## tags\n\n- a\n- b\n\n## nested\n\n- **inner:** x", text);
	}

	[Fact]
	public void Render_OfAFailure_StartsWithError()
	{
		var text = HumanOutput.Render(new Dictionary<string, object?> { ["ok"] = false, ["command"] = "demo", ["error"] = "It broke." });

		Assert.Equal("**error:** It broke.", text);
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

		Assert.Equal("m\n\n## entry\n\n| name | runs |\n| --- | --- |\n| app | 2 |\n| lib | 0 |", shortEntries);
		Assert.Equal($"m\n\n## entry\n\n- **name:** -url\n  - **description:** {new string('d', 80)}", longEntries);
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

		Assert.Equal("m\n\n```\nA  B\n-  -\n1  2\n```", text);
		Assert.DoesNotContain("component", text);
	}

	[Fact]
	public void Render_EscapesWhatMarkdownWouldTakeForMarkup()
	{
		var text = HumanOutput.Render(new Dictionary<string, object?>
		{
			["message"] = "m",
			["usage"] = "bassia <command> [-x] a|b *",
			["rest_of_line"] = "_x_ and a_b",
			["drawn"] = "one ``` two\nthree"
		});

		Assert.Equal("m\n\n- **usage:** bassia \\<command\\> \\[-x\\] a\\|b \\*\n- **rest_of_line:** \\_x\\_ and a_b\n\n## drawn\n\n````\none ``` two\nthree\n````", text);
	}

	[Fact]
	public async Task Human_PrintsMarkdownWithoutTheMarkerAndKeepsTheExitCode()
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
	[InlineData("workspace", "-help", "-human")]
	[InlineData("workspace", "create", "--HUMAN", "-help")]
	public async Task Human_WithHelp_PrintsReadableHelp(params string[] args)
	{
		var (exitCode, output, error) = await TestEnvironment.RunAsync(args);

		Assert.True(exitCode == 0, error);
		// Markdown has no result marker line, and no TOML.
		Assert.DoesNotContain(TomlResult.Marker + "\n", output);
		Assert.DoesNotContain("ok = ", output);
		Assert.Contains("bassia", output);
	}

	[Fact]
	public async Task Human_ForAMissingSwitchOrUnknownCommand_IsAnErrorOnStderrWithExitCode2()
	{
		var (missingExit, missingOut, missingError) = await TestEnvironment.RunAsync("lease", "acquire", "-human");
		var (unknownExit, _, unknownError) = await TestEnvironment.RunAsync("nosuch", "-human");

		Assert.Equal(2, missingExit);
		Assert.Empty(missingOut);
		Assert.StartsWith("**error:** -prefix is required.", missingError);
		Assert.Contains("Usage: bassia lease acquire", missingError);
		Assert.Equal(2, unknownExit);
		Assert.StartsWith("**error:** Unknown command 'nosuch'", unknownError);
	}
}
