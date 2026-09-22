using Tomlyn;
using Tomlyn.Model;

namespace Bassia.Tests;

public class TomlResultTests
{
	[Fact]
	public void Serialize_OpensWithTheMarkerAndParsesAsToml()
	{
		var text = TomlResult.Serialize(new Dictionary<string, object?>
		{
			["ok"] = true,
			["command"] = "init",
			["message"] = "Initialized Bassia monorepo at 'C:\\temp\\root'."
		});

		Assert.StartsWith(TomlResult.Marker + "\n", text);
		var table = Parse(text);
		Assert.True((bool)table["ok"]);
		Assert.Equal("init", table["command"]);
		Assert.Equal("Initialized Bassia monorepo at 'C:\\temp\\root'.", table["message"]);
	}

	[Fact]
	public void Serialize_WritesScalarsBeforeSectionsSoTheyStayTopLevel()
	{
		var text = TomlResult.Serialize(new Dictionary<string, object?>
		{
			["component"] = new List<Dictionary<string, object?>> { new() { ["name"] = "example" } },
			["ok"] = true,
			["status"] = "completed"
		});

		var table = Parse(text);
		Assert.Equal("completed", table["status"]);
		Assert.Equal("example", ((TomlTable)((TomlTableArray)table["component"])[0])["name"]);
	}

	[Fact]
	public void Serialize_DropsNullsAndKeepsNumbersAndStringArrays()
	{
		var text = TomlResult.Serialize(new Dictionary<string, object?>
		{
			["agent_exit_code"] = 3,
			["result_error"] = null,
			["metadata_tags"] = new[] { "agent/run-abc/0", "agent/run-abc/1" }
		});

		var table = Parse(text);
		Assert.Equal(3L, table["agent_exit_code"]);
		Assert.False(table.ContainsKey("result_error"));
		Assert.Equal(new object[] { "agent/run-abc/0", "agent/run-abc/1" }, (TomlArray)table["metadata_tags"]);
	}

	[Fact]
	public void Serialize_EscapesMultilineMessagesSoUsageTextStaysOneValue()
	{
		var text = TomlResult.Serialize(new Dictionary<string, object?> { ["error"] = "first line\nsecond line" });

		Assert.Equal("first line\nsecond line", Parse(text)["error"]);
	}

	private static TomlTable Parse(string text) => TomlSerializer.Deserialize<TomlTable>(text)!;
}
