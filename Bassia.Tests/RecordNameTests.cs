namespace Bassia.Tests;

using System.Text.RegularExpressions;
using Bassia.Integration;
using Bassia.Web;

public class RecordNameTests
{
	[Fact]
	public void NewRunId_IsAgentRunWithTwoWordsAndASlug()
	{
		var runId = RunMetadata.NewRunId();

		Assert.Matches(new Regex("^agent-run-[a-z]+-[a-z]+-[a-z0-9]{6}$"), runId);
		Assert.True(RunMetadata.IsRunId(runId));
	}

	[Fact]
	public void NewIds_AreDistinct()
	{
		var ids = Enumerable.Range(0, 1000).Select(_ => RunMetadata.NewRunId()).ToList();

		Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void RunRefs_LiveUnderAgentRun()
	{
		const string runId = "agent-run-magical-otter-vt9j3p";

		Assert.Equal("magical-otter-vt9j3p", RunMetadata.Key(runId));
		Assert.Equal("agent-run/magical-otter-vt9j3p", RunMetadata.RefBase(runId));
		Assert.Equal("agent-run/magical-otter-vt9j3p/0", RunMetadata.TagName(runId, 0));
		Assert.Equal(runId, RunMetadata.NormalizeRunId("magical-otter-vt9j3p"));
	}

	[Fact]
	public void IntegrationIds_FollowTheSameScheme()
	{
		var id = IntegrationRecord.NewId();

		Assert.Matches(new Regex("^integration-[a-z]+-[a-z]+-[a-z0-9]{6}$"), id);
		Assert.True(IntegrationRecord.IsId(id));
		Assert.Equal("integration/steady-heron-k2m8qa/1", IntegrationRecord.TagName("integration-steady-heron-k2m8qa", 1));
	}

	[Theory]
	[InlineData("magical-otter-vt9j3p", true)]
	[InlineData("magical-otter-VT9J3P", false)]
	[InlineData("magical-otter-vt9j3", false)]
	[InlineData("magical-vt9j3p", false)]
	[InlineData("0123456789abcdef0123456789abcdef", false)]
	[InlineData("../otter-vt9j3p", false)]
	public void IsKey_AcceptsOnlyTheKeyShape(string key, bool expected) => Assert.Equal(expected, RecordName.IsKey(key));

	[Fact]
	public void MatchesRun_AcceptsTheFullIdTheKeyAndAPrefixOfTheKey()
	{
		const string runId = "agent-run-magical-otter-vt9j3p";

		Assert.True(IntegrationSupport.MatchesRun(runId, runId));
		Assert.True(IntegrationSupport.MatchesRun(runId, "magical-otter-vt9j3p"));
		Assert.True(IntegrationSupport.MatchesRun(runId, "magical-ot"));
		Assert.False(IntegrationSupport.MatchesRun(runId, "mag"));
		Assert.False(IntegrationSupport.MatchesRun(runId, "brave-otter"));
	}

	[Fact]
	public void RefLinks_PointAtTheRecordPage()
	{
		Assert.Contains("href=\"/runs/agent-run-magical-otter-vt9j3p\"", Html.Ref("agent-run/magical-otter-vt9j3p/0"));
		Assert.Contains("href=\"/integrations/integration-steady-heron-k2m8qa\"", Html.Ref("integration/steady-heron-k2m8qa"));
		Assert.DoesNotContain("href", Html.Ref("agent-run/not-a-key/0"));
	}
}
