namespace Bassia.Tests;

using System.Text.RegularExpressions;
using Bassia.Agents;

public class RecordNameTests
{
	[Fact]
	public void NewRunId_IsAgentRunWithTwoWordsAndASlug()
	{
		var runId = AgentRunner.NewRunId();

		Assert.Matches(new Regex("^run-[a-z]+-[a-z]+-[a-z0-9]{6}$"), runId);
		Assert.True(RecordName.IsKey(runId[AgentRunner.RunPrefix.Length..]));
	}

	[Fact]
	public void NewIds_AreDistinct()
	{
		var ids = Enumerable.Range(0, 1000).Select(_ => AgentRunner.NewRunId()).ToList();

		Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
	}

	[Theory]
	[InlineData("magical-otter-vt9j3p", true)]
	[InlineData("magical-otter-VT9J3P", false)]
	[InlineData("magical-otter-vt9j3", false)]
	[InlineData("magical-vt9j3p", false)]
	[InlineData("0123456789abcdef0123456789abcdef", false)]
	[InlineData("../otter-vt9j3p", false)]
	public void IsKey_AcceptsOnlyTheKeyShape(string key, bool expected) => Assert.Equal(expected, RecordName.IsKey(key));
}
